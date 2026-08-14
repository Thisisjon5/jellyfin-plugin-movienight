using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// Owns the ffmpeg process lifecycle for the live broadcast: Go Live, Stop, natural end, crash
/// handling, a stall watchdog, and a disk guard. Single-rung only for Phase 2 (spec §5.2's ABR
/// ladder is Phase 4). No auto-restart on crash - a silent restart loop on a bad file is worse
/// than a clear failure (spec §5.4).
/// </summary>
public sealed class BroadcastManager : IDisposable
{
    private const int StartupTimeoutSeconds = 30;
    private const int StopGraceSeconds = 5;
    private const int WatchdogIntervalSeconds = 5;
    private const int SegmentStallSeconds = 30;
    private const long DiskGuardBytes = 2L * 1024 * 1024 * 1024;
    private const int Sigterm = 15;
    private const int RewindSeconds = 15;
    private const int SplicedSegmentSeconds = 4;
    private const int ServedWindowSize = 10;
    private const int SpliceTickSeconds = 2;

    private readonly object _lock = new();
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IGuideManager _guideManager;
    private readonly ILogger<BroadcastManager> _logger;

    // SPIKE 5 (2026-08-14, see planning/DECISIONS.md "mechanism replaced: filler-channel splice"):
    // rolling window of segments currently in the served HlsDirectory/master.m3u8 playlist, once
    // this class has taken over writing it (see the fields below and Pause()/Resume()).
    private readonly List<(string FileName, double Duration, bool DiscontinuityBefore)> _servedSegments = new();

    private Process? _process;
    private Process? _fillerProcess;
    private Timer? _watchdogTimer;
    private StderrTailBuffer? _stderrTail;
    private BroadcastState _state = BroadcastState.Idle;
    private string? _channelName;
    private string? _nowPlaying;
    private DateTime? _startedAtUtc;
    private long? _runTimeTicks;
    private string? _lastFailureReason;
    private string? _currentItemPath;
    private HardwareAccel _currentAccel;
    private string? _currentVaapiDevice;
    private bool _suppressExitHandling;

    private bool _isPaused;
    private double _pausedAtSeconds;
    private Timer? _spliceTimer;
    private string? _activeSourceDirectory;
    private string _activeSourcePrefix = string.Empty;
    private int _activeSourceCursor;
    private int _nextSegmentIndex;
    private bool _pendingDiscontinuityOnNextCopy;

    /// <summary>
    /// Initializes a new instance of the <see cref="BroadcastManager"/> class.
    /// </summary>
    /// <param name="libraryManager">Used to resolve a library item id to its file path.</param>
    /// <param name="mediaEncoder">Used to find the server's own ffmpeg/ffprobe binaries.</param>
    /// <param name="configurationManager">Used to read the server's hardware acceleration settings
    /// and (via <see cref="IServerConfigurationManager.ApplicationPaths"/>) a writable directory for
    /// HLS output - deriving it this way avoids depending on <see cref="IApplicationPaths"/> being
    /// separately DI-registered.</param>
    /// <param name="guideManager">Used to force an immediate guide refresh on every state change -
    /// the built-in "Refresh Guide" scheduled task only runs once every 24h by default (confirmed
    /// via a live test where a client showed "no schedule information" the entire broadcast), so
    /// without this a real movie-length broadcast would still finish before the guide caught up.</param>
    /// <param name="logger">Logger.</param>
    public BroadcastManager(
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        IServerConfigurationManager configurationManager,
        IGuideManager guideManager,
        ILogger<BroadcastManager> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _configurationManager = configurationManager;
        _guideManager = guideManager;
        _logger = logger;
        HlsDirectory = Path.Combine(configurationManager.ApplicationPaths.TempDirectory, "MovieNight", "hls");
        FillerDirectory = Path.Combine(configurationManager.ApplicationPaths.TempDirectory, "MovieNight", "filler");
        ResumedMovieDirectory = Path.Combine(configurationManager.ApplicationPaths.TempDirectory, "MovieNight", "resume");
    }

    /// <summary>
    /// Gets the directory the current (or most recent) broadcast's HLS output lives in.
    /// </summary>
    public string HlsDirectory { get; }

    /// <summary>
    /// Gets the private directory the background filler (pause-card) encoder writes into (SPIKE
    /// 5) - never served directly, copied into <see cref="HlsDirectory"/> by the splice timer.
    /// </summary>
    private string FillerDirectory { get; }

    /// <summary>
    /// Gets the private directory a resumed movie encoder writes into after a pause (SPIKE 5) -
    /// same copy-and-splice treatment as <see cref="FillerDirectory"/>, so ffmpeg never writes
    /// directly into <see cref="HlsDirectory"/> again once a broadcast has paused at least once.
    /// </summary>
    private string ResumedMovieDirectory { get; }

    /// <summary>
    /// Gets a value indicating whether a broadcast is currently live.
    /// </summary>
    public bool IsLive
    {
        get
        {
            lock (_lock)
            {
                return _state == BroadcastState.Live;
            }
        }
    }

    /// <summary>
    /// Gets a snapshot of the current broadcast state.
    /// </summary>
    /// <returns>The current status.</returns>
    public BroadcastStatus GetStatus()
    {
        lock (_lock)
        {
            return new BroadcastStatus(_state, _channelName, _nowPlaying, _startedAtUtc, _runTimeTicks, _lastFailureReason);
        }
    }

    /// <summary>
    /// Validates the requested item, spawns ffmpeg, and waits for the first HLS segments to
    /// appear (or the startup timeout to elapse).
    /// </summary>
    /// <param name="itemId">The library item id of the movie to broadcast.</param>
    /// <param name="channelName">The channel name to report in status.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the broadcast reached <see cref="BroadcastState.Live"/>.</returns>
    public async Task<bool> GoLiveAsync(Guid itemId, string channelName, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_state is BroadcastState.Starting or BroadcastState.Live)
            {
                _logger.LogWarning("Movie Night: Go Live requested while already {State}, ignoring", _state);
                return false;
            }
        }

        var item = _libraryManager.GetItemById(itemId);

        // CA3003 flags item.Path as tainted by the caller-supplied itemId, but GetItemById resolves
        // through Jellyfin's own library database (populated by its file system scanner) - the Guid
        // is an opaque lookup key, not a path, and Path is never derived from request data directly.
#pragma warning disable CA3003
        if (item is null || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            SetFailed($"Item {itemId} not found or has no playable file on disk");
            return false;
        }
#pragma warning restore CA3003

        if (!await ProbeAsync(item.Path, cancellationToken).ConfigureAwait(false))
        {
            SetFailed($"ffprobe could not read {item.Path} - file may be corrupt or an unsupported container");
            return false;
        }

        SetStarting(channelName, item.Name, item.RunTimeTicks);
        CleanHlsDirectory();

        var encodingOptions = _configurationManager.GetEncodingOptions();
        var accel = MapHardwareAccel(encodingOptions.HardwareAccelerationType);
        var args = FfmpegCommandBuilder.Build(item.Path, HlsDirectory, accel, encodingOptions.VaapiDevice);
        _currentItemPath = item.Path;
        _currentAccel = accel;
        _currentVaapiDevice = encodingOptions.VaapiDevice;

        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stderrTail = new StderrTailBuffer();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderrTail.Add(e.Data);
            }
        };
        process.Exited += (_, _) => OnProcessExited(process, stderrTail);

        try
        {
            process.Start();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Movie Night: failed to start ffmpeg at {Path}", _mediaEncoder.EncoderPath);
            SetFailed($"Failed to start ffmpeg: {ex.Message}");
            return false;
        }

        lock (_lock)
        {
            _process = process;
            _stderrTail = stderrTail;
        }

        var masterPlaylistPath = Path.Combine(HlsDirectory, "master.m3u8");
        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (HasHlsOutput(masterPlaylistPath))
            {
                lock (_lock)
                {
                    if (_state == BroadcastState.Starting)
                    {
                        _state = BroadcastState.Live;
                        _startedAtUtc = DateTime.UtcNow;
                    }
                }

                _logger.LogInformation("Movie Night: broadcast live, playing {Path}", item.Path);
                StartWatchdog();
                TriggerGuideRefresh();
                StartFillerEncoderInBackground();
                return true;
            }

            if (process.HasExited)
            {
                break;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        lock (_lock)
        {
            if (_state == BroadcastState.Starting)
            {
                SetFailedUnlocked($"Timed out waiting for HLS output after {StartupTimeoutSeconds}s. ffmpeg stderr: {stderrTail}");
                KillProcessUnlocked();
            }

            return _state == BroadcastState.Live;
        }
    }

    /// <summary>
    /// Gracefully stops the broadcast (SIGTERM, 5s grace, SIGKILL), cleans the HLS output
    /// directory, and returns to <see cref="BroadcastState.Idle"/>.
    /// </summary>
    /// <returns>A task that completes once the process has exited and cleanup is done.</returns>
    public async Task StopAsync()
    {
        Process? process;
        Process? fillerProcess;
        lock (_lock)
        {
            process = _process;
            fillerProcess = _fillerProcess;
        }

        if (process is not null && !process.HasExited)
        {
            _logger.LogInformation("Movie Night: stopping broadcast");
            if (!TrySendSigterm(process))
            {
                KillProcessUnlocked();
            }
            else
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(StopGraceSeconds));
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Movie Night: ffmpeg did not exit within {Seconds}s of SIGTERM, sending SIGKILL", StopGraceSeconds);
                    KillProcessUnlocked();
                }
            }
        }

        await KillFillerAsync(fillerProcess).ConfigureAwait(false);

        StopWatchdog();
        CleanHlsDirectory();

        lock (_lock)
        {
            _state = BroadcastState.Idle;
            _channelName = null;
            _nowPlaying = null;
            _startedAtUtc = null;
            _runTimeTicks = null;
            _process = null;
            _stderrTail = null;
            _currentItemPath = null;
            _fillerProcess = null;
            StopSpliceTimerUnlocked();
            _isPaused = false;
            _pausedAtSeconds = 0;
            _activeSourceDirectory = null;
            _activeSourcePrefix = string.Empty;
            _activeSourceCursor = 0;
            _servedSegments.Clear();
            _nextSegmentIndex = 0;
            _pendingDiscontinuityOnNextCopy = false;
        }

        TriggerGuideRefresh();
    }

    private async Task KillFillerAsync(Process? fillerProcess)
    {
        if (fillerProcess is null || fillerProcess.HasExited)
        {
            return;
        }

        if (TrySendSigterm(fillerProcess))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(StopGraceSeconds));
            try
            {
                await fillerProcess.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                // Falls through to the hard kill below.
            }
        }

        try
        {
            if (!fillerProcess.HasExited)
            {
                fillerProcess.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and Kill(); nothing to do.
        }
    }

    /// <summary>
    /// SPIKE 5: pauses the broadcast by killing the currently-active encoder (the movie, or
    /// whatever it was last spliced to) and splicing in a continuously-running filler encoder,
    /// with an <c>EXT-X-DISCONTINUITY</c> marker at the switch. The pause position is tracked as
    /// wall-clock elapsed time, not parsed from ffmpeg, per the DECISIONS.md ruling.
    /// </summary>
    /// <returns><c>true</c> if the splice to filler succeeded.</returns>
    public async Task<bool> Pause()
    {
        string? itemPath;
        DateTime? startedAt;
        lock (_lock)
        {
            if (_state != BroadcastState.Live || _isPaused)
            {
                return false;
            }

            itemPath = _currentItemPath;
            startedAt = _startedAtUtc;
        }

        if (itemPath is null)
        {
            return false;
        }

        // The filler has been running continuously since Go Live (StartFillerEncoderInBackground) -
        // find whatever it's already produced so the splice starts instantly, zero cold start. The
        // gap of spawning-and-waiting-for-a-filler-on-demand is what broke the first live test
        // tonight: Jellyfin's own downstream remux treats any gap in the served playlist as the
        // stream ending and quits, kicking clients back to the channel list.
        var fillerCursor = GetLatestFillerSegmentIndex();
        if (fillerCursor < 0)
        {
            _logger.LogError("Movie Night: spike 5 - background filler has no segments ready yet, can't splice");
            return false;
        }

        var elapsedSeconds = startedAt is DateTime started ? (DateTime.UtcNow - started).TotalSeconds : 0;

        await KillActiveEncoderForSpliceAsync().ConfigureAwait(false);

        lock (_lock)
        {
            if (_servedSegments.Count == 0)
            {
                SeedServedSegmentsFromExistingHlsDirectoryUnlocked();
            }

            _pausedAtSeconds = elapsedSeconds;
            _isPaused = true;
            _activeSourceDirectory = FillerDirectory;
            _activeSourcePrefix = "filler_";
            _activeSourceCursor = fillerCursor;
            _pendingDiscontinuityOnNextCopy = true;
            StopWatchdogUnlocked();
        }

        SpliceTick();
        _spliceTimer = new Timer(_ => SpliceTick(), null, TimeSpan.FromSeconds(SpliceTickSeconds), TimeSpan.FromSeconds(SpliceTickSeconds));
        _logger.LogInformation("Movie Night: spike 5 - paused at {Seconds:F1}s, spliced to already-warm filler", elapsedSeconds);
        return true;
    }

    private int GetLatestFillerSegmentIndex()
    {
        if (!Directory.Exists(FillerDirectory))
        {
            return -1;
        }

        return Directory.EnumerateFiles(FillerDirectory, "filler_*.ts")
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .Select(f => ParseFillerSegmentIndex(f!))
            .Where(i => i.HasValue)
            .Select(i => i!.Value)
            .DefaultIfEmpty(-1)
            .Max();
    }

    private static int? ParseFillerSegmentIndex(string fileName)
    {
        var match = Regex.Match(fileName, @"filler_(\d+)\.ts$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// SPIKE 5 counterpart to <see cref="Pause"/>: kills the filler, respawns the movie encoder a
    /// little before the pause position (<see cref="RewindSeconds"/>), waits for it to actually be
    /// producing output, then splices back with another discontinuity marker.
    /// </summary>
    /// <returns><c>true</c> if the splice back to the movie succeeded.</returns>
    public async Task<bool> Resume()
    {
        string itemPath;
        HardwareAccel accel;
        string? vaapiDevice;
        double pausedAt;
        int nextIndex;
        lock (_lock)
        {
            if (_state != BroadcastState.Live || !_isPaused || _currentItemPath is null)
            {
                return false;
            }

            itemPath = _currentItemPath;
            accel = _currentAccel;
            vaapiDevice = _currentVaapiDevice;
            pausedAt = _pausedAtSeconds;
            nextIndex = _nextSegmentIndex;
        }

        lock (_lock)
        {
            StopSpliceTimerUnlocked();
        }

        await KillActiveEncoderForSpliceAsync().ConfigureAwait(false);

        var resumePosition = Math.Max(0, pausedAt - RewindSeconds);
        CleanDirectory(ResumedMovieDirectory);
        var args = FfmpegCommandBuilder.Build(itemPath, ResumedMovieDirectory, accel, vaapiDevice, resumePosition);
        if (!SpawnSpliceSource(args))
        {
            _logger.LogError("Movie Night: spike 5 - failed to restart the movie encoder for resume");
            return false;
        }

        if (!await WaitForFirstSegmentAsync(ResumedMovieDirectory, "segment_").ConfigureAwait(false))
        {
            _logger.LogError("Movie Night: spike 5 - resumed movie encoder did not produce a segment in time");
            return false;
        }

        lock (_lock)
        {
            _isPaused = false;
            _activeSourceDirectory = ResumedMovieDirectory;
            _activeSourcePrefix = "segment_";
            _activeSourceCursor = 0;
            _pendingDiscontinuityOnNextCopy = true;
            _startedAtUtc = DateTime.UtcNow.AddSeconds(-resumePosition);
            _nextSegmentIndex = nextIndex; // preserved across the pause/resume for this splice pass
        }

        SpliceTick();
        StartWatchdog();
        _logger.LogInformation("Movie Night: spike 5 - resumed from {Position:F1}s (paused at {PausedAt:F1}s), spliced back to movie", resumePosition, pausedAt);
        return true;
    }

    /// <summary>
    /// Starts the filler (pause-card) encoder running continuously in the background, right after
    /// Go Live - so it already has ready segments the instant a pause happens (zero cold start,
    /// per the DECISIONS.md ruling). Fire-and-forget: a slow/failed filler start doesn't block Go
    /// Live's own response, and a failure here only matters at the next Pause() attempt.
    /// </summary>
    private void StartFillerEncoderInBackground()
    {
        _ = Task.Run(() =>
        {
            try
            {
                CleanDirectory(FillerDirectory);
                var startInfo = new ProcessStartInfo
                {
                    FileName = _mediaEncoder.EncoderPath,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var arg in BuildFillerArgs(FillerDirectory))
                {
                    startInfo.ArgumentList.Add(arg);
                }

                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                var stderrTail = new StderrTailBuffer();
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        stderrTail.Add(e.Data);
                    }
                };
                process.Exited += (_, _) =>
                {
                    lock (_lock)
                    {
                        if (ReferenceEquals(_fillerProcess, process))
                        {
                            _logger.LogWarning("Movie Night: spike 5 - background filler encoder exited unexpectedly. stderr: {Stderr}", stderrTail);
                            _fillerProcess = null;
                        }
                    }
                };

                process.Start();
                process.BeginErrorReadLine();

                lock (_lock)
                {
                    _fillerProcess = process;
                }

                _logger.LogInformation("Movie Night: spike 5 - background filler encoder started");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Movie Night: spike 5 - failed to start background filler encoder");
            }
        });
    }

    private static List<string> BuildFillerArgs(string outputDir) => new List<string>
    {
        "-re", "-f", "lavfi", "-i", "color=c=maroon:s=1280x720:r=30",
        "-re", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
        "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-level", "4.0",
        "-b:v", "1000k", "-maxrate", "1000k", "-bufsize", "2000k",
        "-c:a", "aac", "-b:a", "96k", "-ac", "2",
        "-force_key_frames", $"expr:gte(t,n_forced*{SplicedSegmentSeconds})",
        "-f", "hls",
        "-hls_time", SplicedSegmentSeconds.ToString(CultureInfo.InvariantCulture),
        "-hls_list_size", "10",
        "-hls_flags", "delete_segments+independent_segments",
        "-hls_segment_filename", Path.Combine(outputDir, "filler_%03d.ts"),
        Path.Combine(outputDir, "filler.m3u8"),
    };

    /// <summary>
    /// Kills whichever encoder is currently active, for a deliberate splice transition rather than
    /// a real stop - marks the exit as intentional first so <see cref="OnProcessExited"/> doesn't
    /// treat it as a crash or natural end.
    /// </summary>
    private async Task KillActiveEncoderForSpliceAsync()
    {
        Process? process;
        lock (_lock)
        {
            _suppressExitHandling = true;
            process = _process;
        }

        if (process is null || process.HasExited)
        {
            return;
        }

        if (!TrySendSigterm(process))
        {
            lock (_lock)
            {
                KillProcessUnlocked();
            }
        }
        else
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(StopGraceSeconds));
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (_lock)
                {
                    KillProcessUnlocked();
                }
            }
        }
    }

    /// <summary>
    /// Spawns an encoder for a splice source (filler or a resumed movie) and makes it the tracked
    /// "active" process for crash detection - <see cref="_process"/> always refers to whichever
    /// encoder is currently relevant, whatever it's feeding.
    /// </summary>
    private bool SpawnSpliceSource(IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stderrTail = new StderrTailBuffer();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderrTail.Add(e.Data);
            }
        };
        process.Exited += (_, _) => OnProcessExited(process, stderrTail);

        try
        {
            process.Start();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Movie Night: spike 5 splice - failed to start ffmpeg");
            return false;
        }

        lock (_lock)
        {
            _process = process;
            _stderrTail = stderrTail;
            _suppressExitHandling = false;
        }

        return true;
    }

    private static async Task<bool> WaitForFirstSegmentAsync(string directory, string prefix)
    {
        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, prefix + "*.ts").Any())
            {
                return true;
            }

            await Task.Delay(300).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Called under <see cref="_lock"/> the first time a broadcast ever pauses: seeds
    /// <see cref="_servedSegments"/> from whatever <c>segment_*.ts</c> files the (just-killed)
    /// movie encoder already left in <see cref="HlsDirectory"/>, so the served playlist continues
    /// from where the client already is rather than jumping backwards.
    /// </summary>
    private void SeedServedSegmentsFromExistingHlsDirectoryUnlocked()
    {
        if (!Directory.Exists(HlsDirectory))
        {
            return;
        }

        var files = Directory.EnumerateFiles(HlsDirectory, "segment_*.ts")
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .Select(f => f!)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            _servedSegments.Add((file, SplicedSegmentSeconds, false));
        }

        if (_servedSegments.Count > ServedWindowSize)
        {
            _servedSegments.RemoveRange(0, _servedSegments.Count - ServedWindowSize);
        }

        var maxIndex = files
            .Select(ParseSegmentIndex)
            .Where(i => i.HasValue)
            .Select(i => i!.Value)
            .DefaultIfEmpty(-1)
            .Max();
        _nextSegmentIndex = maxIndex + 1;
    }

    private static int? ParseSegmentIndex(string fileName)
    {
        var match = Regex.Match(fileName, @"segment_(\d+)\.ts$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// Copies any new segments the currently-active splice source has produced into
    /// <see cref="HlsDirectory"/> under continuing <see cref="_nextSegmentIndex"/> numbers, marks
    /// the first copied segment with a discontinuity if one is pending, and rewrites the served
    /// playlist. Runs on a timer while spliced (<see cref="_spliceTimer"/>) and once immediately
    /// after every splice transition.
    /// </summary>
    private void SpliceTick()
    {
        string? sourceDir;
        string sourcePrefix;
        int cursor;
        lock (_lock)
        {
            if (_activeSourceDirectory is null)
            {
                return;
            }

            sourceDir = _activeSourceDirectory;
            sourcePrefix = _activeSourcePrefix;
            cursor = _activeSourceCursor;
        }

        var copiedAny = false;
        while (true)
        {
            var sourceFile = Path.Combine(sourceDir, $"{sourcePrefix}{cursor:D3}.ts");
            if (!File.Exists(sourceFile))
            {
                break;
            }

            int destIndex;
            lock (_lock)
            {
                destIndex = _nextSegmentIndex;
            }

            var destFileName = $"segment_{destIndex:D3}.ts";
            var destFile = Path.Combine(HlsDirectory, destFileName);
            try
            {
                File.Copy(sourceFile, destFile, overwrite: true);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Movie Night: spike 5 - failed to copy spliced segment {Source}", sourceFile);
                break;
            }

            lock (_lock)
            {
                var markDiscontinuity = _pendingDiscontinuityOnNextCopy;
                _pendingDiscontinuityOnNextCopy = false;
                _servedSegments.Add((destFileName, SplicedSegmentSeconds, markDiscontinuity));
                while (_servedSegments.Count > ServedWindowSize)
                {
                    var dropped = _servedSegments[0];
                    _servedSegments.RemoveAt(0);
                    TryDeleteServedFile(dropped.FileName);
                }

                _nextSegmentIndex++;
                _activeSourceCursor = cursor + 1;
            }

            cursor++;
            copiedAny = true;
        }

        if (copiedAny)
        {
            WriteServedPlaylist();
        }
    }

    private void TryDeleteServedFile(string fileName)
    {
        try
        {
            var path = Path.Combine(HlsDirectory, fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover segment file isn't worth failing the splice over.
        }
    }

    /// <summary>
    /// Hand-writes <c>HlsDirectory/master.m3u8</c> from <see cref="_servedSegments"/> - once a
    /// broadcast has spliced at least once, ffmpeg never writes this file directly again, because
    /// its own live-HLS muxer would just overwrite the discontinuity markers this class inserts.
    /// </summary>
    private void WriteServedPlaylist()
    {
        List<(string FileName, double Duration, bool DiscontinuityBefore)> segments;
        int nextIndex;
        lock (_lock)
        {
            segments = new List<(string, double, bool)>(_servedSegments);
            nextIndex = _nextSegmentIndex;
        }

        if (segments.Count == 0)
        {
            return;
        }

        var mediaSequence = Math.Max(0, nextIndex - segments.Count);
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        sb.Append("#EXT-X-VERSION:3\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{SplicedSegmentSeconds}\n");
        sb.Append(CultureInfo.InvariantCulture, $"#EXT-X-MEDIA-SEQUENCE:{mediaSequence}\n");
        sb.Append("#EXT-X-INDEPENDENT-SEGMENTS\n");
        foreach (var segment in segments)
        {
            if (segment.DiscontinuityBefore)
            {
                sb.Append("#EXT-X-DISCONTINUITY\n");
            }

            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{segment.Duration:F1},\n{segment.FileName}\n");
        }

        var masterPath = Path.Combine(HlsDirectory, "master.m3u8");
        var tmpPath = masterPath + ".tmp";
        try
        {
            File.WriteAllText(tmpPath, sb.ToString());
            File.Move(tmpPath, masterPath, overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Movie Night: spike 5 - failed to write spliced playlist");
        }
    }

    private void CleanDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            Directory.CreateDirectory(dir);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Movie Night: failed to clean directory {Dir}", dir);
        }
    }

    private static HardwareAccel MapHardwareAccel(MediaBrowser.Model.Entities.HardwareAccelerationType type) =>
        type switch
        {
            MediaBrowser.Model.Entities.HardwareAccelerationType.qsv => HardwareAccel.Qsv,
            MediaBrowser.Model.Entities.HardwareAccelerationType.vaapi => HardwareAccel.Vaapi,
            MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc => HardwareAccel.Nvenc,
            MediaBrowser.Model.Entities.HardwareAccelerationType.amf => HardwareAccel.Amf,
            _ => HardwareAccel.None,
        };

    private static bool HasHlsOutput(string masterPlaylistPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(masterPlaylistPath);
            return File.Exists(masterPlaylistPath)
                && dir is not null
                && Directory.EnumerateFiles(dir, "*.ts").Any();
        }
        catch (IOException)
        {
            // Segment/playlist files can be mid-write; treat as "not ready yet" rather than failing.
            return false;
        }
    }

    private async Task<bool> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.ProbePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("default=nw=1:nk=1");
        startInfo.ArgumentList.Add(path);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
    }

    private void SetStarting(string channelName, string nowPlaying, long? runTimeTicks)
    {
        lock (_lock)
        {
            _state = BroadcastState.Starting;
            _channelName = channelName;
            _nowPlaying = nowPlaying;
            _startedAtUtc = null;
            _runTimeTicks = runTimeTicks;
            _lastFailureReason = null;
        }
    }

    private void SetFailed(string reason)
    {
        lock (_lock)
        {
            SetFailedUnlocked(reason);
        }
    }

    private void SetFailedUnlocked(string reason)
    {
        _state = BroadcastState.Failed;
        _lastFailureReason = reason;
        _logger.LogError("Movie Night: broadcast failed - {Reason}", reason);
    }

    private void OnProcessExited(Process process, StderrTailBuffer stderrTail)
    {
        lock (_lock)
        {
            if (_suppressExitHandling)
            {
                // SPIKE 5: a Pause()/Resume() splice transition killed this process on purpose
                // (swapping to filler, or swapping the filler back out for a resumed movie encode)
                // - it is not the broadcast ending.
                return;
            }

            if (!ReferenceEquals(_process, process))
            {
                // Already stopped/replaced by StopAsync or a new GoLiveAsync; nothing to do.
                return;
            }

            if (_state is BroadcastState.Idle or BroadcastState.Failed)
            {
                return;
            }

            var exitCode = SafeGetExitCode(process);
            if (exitCode == 0)
            {
                _state = BroadcastState.Ended;
                _logger.LogInformation("Movie Night: broadcast ended naturally (source played to completion)");
            }
            else
            {
                SetFailedUnlocked($"ffmpeg exited with code {exitCode}. stderr: {stderrTail}");
            }
        }

        StopWatchdog();
        TriggerGuideRefresh();
    }

    private static int SafeGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private void StartWatchdog()
    {
        StopWatchdogUnlocked();
        _watchdogTimer = new Timer(_ => CheckWatchdog(), null, TimeSpan.FromSeconds(WatchdogIntervalSeconds), TimeSpan.FromSeconds(WatchdogIntervalSeconds));
    }

    private void StopWatchdog()
    {
        lock (_lock)
        {
            StopWatchdogUnlocked();
        }
    }

    private void StopWatchdogUnlocked()
    {
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
    }

    private void StopSpliceTimerUnlocked()
    {
        _spliceTimer?.Dispose();
        _spliceTimer = null;
    }

    private void CheckWatchdog()
    {
        lock (_lock)
        {
            if (_state != BroadcastState.Live)
            {
                return;
            }

            if (IsSegmentStale())
            {
                _logger.LogError("Movie Night: no new HLS segment in over {Seconds}s, treating as stalled", SegmentStallSeconds);
                SetFailedUnlocked($"Encoder stalled - no new HLS segment written in time. ffmpeg stderr: {_stderrTail}");
                KillProcessUnlocked();
                StopWatchdogUnlocked();
                return;
            }

            var diskUsage = GetHlsDirectorySize();
            if (diskUsage > DiskGuardBytes)
            {
                _logger.LogError("Movie Night: HLS output directory exceeded the {LimitMb}MB disk guard ({ActualMb}MB) - segment deletion may not be keeping up", DiskGuardBytes / 1024 / 1024, diskUsage / 1024 / 1024);
                SetFailedUnlocked("HLS output exceeded the disk guard limit");
                KillProcessUnlocked();
                StopWatchdogUnlocked();
            }
        }
    }

    private bool IsSegmentStale()
    {
        try
        {
            if (!Directory.Exists(HlsDirectory))
            {
                return true;
            }

            var latestWrite = Directory.EnumerateFiles(HlsDirectory, "*.ts")
                .Select(f => new FileInfo(f).LastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            return latestWrite == DateTime.MinValue
                || DateTime.UtcNow - latestWrite > TimeSpan.FromSeconds(SegmentStallSeconds);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private long GetHlsDirectorySize()
    {
        try
        {
            if (!Directory.Exists(HlsDirectory))
            {
                return 0;
            }

            return Directory.EnumerateFiles(HlsDirectory).Sum(f => new FileInfo(f).Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Fires an immediate guide refresh in the background, without blocking the caller - guide-based
    /// clients (e.g. Roku) read the channel's now-playing info from the guide, not the M3U/EPG
    /// endpoints directly, and the built-in "Refresh Guide" task only runs once every 24h by
    /// default. Fire-and-forget with logged failure rather than awaited: it took ~3s in testing,
    /// and neither Go Live nor Stop should wait on it to respond.
    /// </summary>
    private void TriggerGuideRefresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _guideManager.RefreshGuide(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Movie Night: guide refresh after broadcast state change failed");
            }
        });
    }

    private void CleanHlsDirectory()
    {
        try
        {
            if (Directory.Exists(HlsDirectory))
            {
                Directory.Delete(HlsDirectory, recursive: true);
            }

            Directory.CreateDirectory(HlsDirectory);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Movie Night: failed to clean HLS directory {Dir}", HlsDirectory);
        }
    }

    private bool TrySendSigterm(Process process)
    {
        try
        {
            var result = NativeKill(process.Id, Sigterm);
            if (result != 0)
            {
                _logger.LogWarning("Movie Night: libc kill() returned {Result} sending SIGTERM, falling back to SIGKILL", result);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Movie Night: SIGTERM via libc failed, falling back to SIGKILL");
            return false;
        }
    }

    private void KillProcessUnlocked()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the HasExited check and Kill(); nothing to do.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopWatchdog();
        lock (_lock)
        {
            StopSpliceTimerUnlocked();
        }

        _process?.Dispose();
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int NativeKill(int pid, int signal);

    /// <summary>
    /// A small fixed-size ring buffer of the most recent ffmpeg stderr lines, for surfacing in
    /// failure messages (spec §5.4/§7's "Last failure panel").
    /// </summary>
    private sealed class StderrTailBuffer
    {
        private const int MaxLines = 40;
        private readonly object _bufferLock = new();
        private readonly Queue<string> _lines = new();

        public void Add(string line)
        {
            lock (_bufferLock)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxLines)
                {
                    _lines.Dequeue();
                }
            }
        }

        public override string ToString()
        {
            lock (_bufferLock)
            {
                return string.Join('\n', _lines);
            }
        }
    }
}

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
    private const int RewindSeconds = 60;
    private const int SplicedSegmentSeconds = 4;
    private const int ServedWindowSize = 10;
    private const int SpliceTickSeconds = 2;
    private const int ResumeWarmupBufferSeconds = 30;
    private const int Sw2ResumeRewindSeconds = 10;
    private const int Sw2ResumeWarmupSeconds = 5;

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

    // All CURRENTLY-LIVE switcher spike processes, not just the newest: Jellyfin can hold more
    // than one connection to the switcher URL open at once (probe + SharedHttpStream copy), each
    // of which spawns its own process, and a "send to the most recent one" design can address a
    // process that isn't the one actually feeding the player. Commands are written to every live
    // process; each entry carries its stderr tail so ffmpeg's command replies are inspectable.
    private readonly List<(Process Process, StderrTailBuffer Stderr)> _switcherSpikeProcesses = new();

    // SWITCHER V2 spike state (see BuildSwitcherSpikeArgs): the configured movie, the restartable
    // feeder that decodes/encodes it into the local feed the switcher consumes, and the position
    // bookkeeping Jon's resume ruling requires (pause logs the movie timestamp; resume restarts
    // the feeder a little before it). All guarded by _lock.
    private string? _sw2MoviePath;
    private int _sw2HttpPort;
    private double _sw2FeederStartSeconds;
    private DateTime? _sw2FeederStartedUtc;
    private bool _sw2Paused;
    private Process? _sw2FeederProcess;
    private StderrTailBuffer? _sw2FeederStderr;

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
    private bool _pendingDiscontinuityOnNextCopy;
    private bool _spliceTickRunning;
    private DateTime? _lastServedSegmentUtc;

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
        MovieDirectory = Path.Combine(configurationManager.ApplicationPaths.TempDirectory, "MovieNight", "movie");
        FillerDirectory = Path.Combine(configurationManager.ApplicationPaths.TempDirectory, "MovieNight", "filler");
        ResumedMovieDirectory = Path.Combine(configurationManager.ApplicationPaths.TempDirectory, "MovieNight", "resume");
    }

    /// <summary>
    /// Gets the directory the served master.m3u8 lives in - the only thing BroadcastManager ever
    /// writes here is that hand-authored playlist (see <see cref="WriteServedPlaylist"/>); no
    /// encoder writes segments into this directory. Kept separate from every source's own output
    /// directory (<see cref="MovieDirectory"/>, <see cref="FillerDirectory"/>,
    /// <see cref="ResumedMovieDirectory"/>) so BroadcastManager is the sole writer of this path for
    /// the whole broadcast lifetime, not just after the first pause - two writers racing on this
    /// same file (ffmpeg's own live muxer plus this class) was a real bug found 2026-08-14.
    /// </summary>
    public string HlsDirectory { get; }

    /// <summary>
    /// Gets the directory the live/original movie encoder writes into.
    /// </summary>
    private string MovieDirectory { get; }

    /// <summary>
    /// Gets the directory the background filler (pause-card) encoder writes into. Not copied
    /// anywhere - <c>MovieNightController</c> serves its segments directly via
    /// <see cref="ResolveServedFilePath"/> once the served playlist points at them.
    /// </summary>
    private string FillerDirectory { get; }

    /// <summary>
    /// Gets the directory a resumed movie encoder writes into after a pause. Not copied anywhere -
    /// same direct-serve treatment as <see cref="FillerDirectory"/>.
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
            return new BroadcastStatus(_state, _channelName, _nowPlaying, _startedAtUtc, _runTimeTicks, _lastFailureReason, _isPaused);
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

        // Re-check AND claim Starting atomically, in one lock, immediately before the only await
        // in this method. Found via a real live bug (2026-08-14): the earlier version re-checked
        // at the top of the method but only flipped _state to Starting AFTER awaiting ProbeAsync -
        // two Go Live calls close together (e.g. a double-click, now trivial to trigger from the
        // config page's own button) could both pass that early check before either one claimed the
        // state, both proceed to spawn ffmpeg into the same MovieDirectory, and stomp each other -
        // one process's CleanDirectory() call wipes the other's segments out from under it before
        // its own startup-detection loop ever sees them, so it looks like it's "encoding fine" per
        // its own stderr but never produces a detectable segment and times out at 30s regardless.
        lock (_lock)
        {
            if (_state is BroadcastState.Starting or BroadcastState.Live)
            {
                _logger.LogWarning("Movie Night: Go Live requested while already {State}, ignoring", _state);
                return false;
            }

            SetStartingUnlocked(channelName, item.Name, item.RunTimeTicks);
        }

        if (!await ProbeAsync(item.Path, cancellationToken).ConfigureAwait(false))
        {
            SetFailed($"ffprobe could not read {item.Path} - file may be corrupt or an unsupported container");
            return false;
        }

        CleanHlsDirectory();
        CleanDirectory(MovieDirectory);

        var encodingOptions = _configurationManager.GetEncodingOptions();
        var accel = MapHardwareAccel(encodingOptions.HardwareAccelerationType);
        var args = FfmpegCommandBuilder.Build(item.Path, MovieDirectory, accel, encodingOptions.VaapiDevice);
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

        var deadline = DateTime.UtcNow.AddSeconds(StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(MovieDirectory) && Directory.EnumerateFiles(MovieDirectory, "segment_*.ts").Any())
            {
                lock (_lock)
                {
                    if (_state == BroadcastState.Starting)
                    {
                        _state = BroadcastState.Live;
                        _startedAtUtc = DateTime.UtcNow;

                        // BroadcastManager owns the served master.m3u8 for the entire broadcast,
                        // from this first activation - not just after the first pause. The movie
                        // encoder above writes segment_NNN.ts into its own private MovieDirectory
                        // and never touches HlsDirectory, so there's only ever one writer of the
                        // served playlist (see the HlsDirectory doc comment for the bug this fixes).
                        _activeSourceDirectory = MovieDirectory;
                        _activeSourcePrefix = "segment_";
                        _activeSourceCursor = 0;
                        _pendingDiscontinuityOnNextCopy = false;
                    }
                }

                SpliceTick();
                _spliceTimer = new Timer(_ => SpliceTick(), null, TimeSpan.FromSeconds(SpliceTickSeconds), TimeSpan.FromSeconds(SpliceTickSeconds));

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
            _pendingDiscontinuityOnNextCopy = false;
            _lastServedSegmentUtc = null;
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
    /// SPIKE 6 (2026-08-14, per Jon: "what we've built is largely a pointer for an HLS stream -
    /// we need to seamlessly transition pointing from one stream to another"): pauses by pointing
    /// the served playlist at the always-running filler encoder, with an
    /// <c>EXT-X-DISCONTINUITY</c> marker at the switch. The movie encoder is deliberately left
    /// running, untouched - killing it is deferred to <see cref="Resume"/>. No file copying
    /// happens anywhere in this design: each encoder writes its own segments under its own
    /// filename prefix, in its own directory, and the served master.m3u8 just references
    /// whichever prefix is currently "selected" - <c>MovieNightController</c> resolves a
    /// requested filename to the right directory via <see cref="ResolveServedFilePath"/>. The
    /// pause position is tracked as wall-clock elapsed time, not parsed from ffmpeg, per the
    /// DECISIONS.md ruling.
    /// </summary>
    /// <returns><c>true</c> if the splice to filler succeeded.</returns>
    public Task<bool> Pause()
    {
        string? itemPath;
        DateTime? startedAt;
        lock (_lock)
        {
            if (_state != BroadcastState.Live || _isPaused)
            {
                return Task.FromResult(false);
            }

            itemPath = _currentItemPath;
            startedAt = _startedAtUtc;
        }

        if (itemPath is null)
        {
            return Task.FromResult(false);
        }

        // The filler has been running continuously since Go Live (StartFillerEncoderInBackground) -
        // find whatever it's already produced so the splice starts instantly, zero cold start. The
        // gap of spawning-and-waiting-for-a-filler-on-demand is what broke the first live test
        // tonight: Jellyfin's own downstream remux treats any gap in the served playlist as the
        // stream ending and quits, kicking clients back to the channel list.
        var fillerCursor = GetLatestSegmentIndex(FillerDirectory, "filler_");
        if (fillerCursor < 0)
        {
            _logger.LogError("Movie Night: spike 6 - background filler has no segments ready yet, can't splice");
            return Task.FromResult(false);
        }

        var elapsedSeconds = startedAt is DateTime started ? (DateTime.UtcNow - started).TotalSeconds : 0;

        // Point the served output at the already-warm filler immediately - fast, no waiting, and
        // (per Jon) the movie encoder is simply left running rather than killed here. It's not
        // colliding with anything: it keeps writing segment_NNN.ts into its own private
        // MovieDirectory exactly as it always has, while the served playlist below is now
        // referencing filler_NNN.ts from a different directory entirely. It gets torn down at the
        // start of Resume() instead.
        lock (_lock)
        {
            _pausedAtSeconds = elapsedSeconds;
            _isPaused = true;
            _activeSourceDirectory = FillerDirectory;
            _activeSourcePrefix = "filler_";
            _activeSourceCursor = fillerCursor;
            _pendingDiscontinuityOnNextCopy = true;
            StopWatchdogUnlocked();
            StopSpliceTimerUnlocked();
        }

        // A guaranteed synchronous tick before the timer starts, with the timer's own first tick
        // a full SpliceTickSeconds later, gives more headroom before any possible overlap than an
        // immediate-dueTime timer would (found via a real live-tested race, 2026-08-14).
        SpliceTick();
        _spliceTimer = new Timer(_ => SpliceTick(), null, TimeSpan.FromSeconds(SpliceTickSeconds), TimeSpan.FromSeconds(SpliceTickSeconds));
        _logger.LogInformation("Movie Night: spike 6 - paused at {Seconds:F1}s, pointed at already-warm filler", elapsedSeconds);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Highest already-produced segment index for a given source, so a fresh splice can start
    /// from "whatever's newest right now" instead of replaying from a (possibly long-since-
    /// deleted) historical start.
    /// </summary>
    private static int GetLatestSegmentIndex(string directory, string prefix)
    {
        if (!Directory.Exists(directory))
        {
            return -1;
        }

        return Directory.EnumerateFiles(directory, prefix + "*.ts")
            .Select(Path.GetFileName)
            .Where(f => f is not null)
            .Select(f => ParseSegmentIndex(f!, prefix))
            .Where(i => i.HasValue)
            .Select(i => i!.Value)
            .DefaultIfEmpty(-1)
            .Max();
    }

    private static int? ParseSegmentIndex(string fileName, string prefix)
    {
        var match = Regex.Match(fileName, Regex.Escape(prefix) + @"(\d+)\.ts$");
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// SPIKE 6 counterpart to <see cref="Pause"/>: tears down the movie encoder <see cref="Pause"/>
    /// deliberately left running, respawns it a little before the pause position
    /// (<see cref="RewindSeconds"/>) under a distinct filename prefix, waits for it to actually be
    /// producing output plus a warm-up buffer, then points the served playlist back at it with
    /// another discontinuity marker.
    /// </summary>
    /// <returns><c>true</c> if the splice back to the movie succeeded.</returns>
    public async Task<bool> Resume()
    {
        string itemPath;
        HardwareAccel accel;
        string? vaapiDevice;
        double pausedAt;
        Process? staleMovieProcess;
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
            staleMovieProcess = _process;
        }

        // Tear down the movie encoder Pause() left running, now that we're actually about to
        // replace it - synchronous and safe (unlike the 60s-delayed version this replaced): it's
        // still _process at this point, so the exit-suppression window is short and precise
        // rather than racing against an independent timer.
        if (staleMovieProcess is not null)
        {
            await KillTrackedProcessAsync(staleMovieProcess).ConfigureAwait(false);
        }

        // Deliberately does NOT stop the splice timer or touch the active source yet - it keeps
        // pointing at the filler for the ENTIRE time this new movie encoder is cold-starting
        // below (which can take many seconds). Cutting the served feed before the replacement is
        // ready was the actual bug (found 2026-08-14 live testing): it left the served playlist
        // stalled for the whole warm-up window, which is exactly what Jellyfin's own downstream
        // remux treats as the stream ending. Per Jon: we're building a video switcher, and a
        // switcher never cuts to a source before it's rolling.
        var resumePosition = Math.Max(0, pausedAt - RewindSeconds);
        CleanDirectory(ResumedMovieDirectory);
        var args = FfmpegCommandBuilder.Build(itemPath, ResumedMovieDirectory, accel, vaapiDevice, resumePosition, "resume_");
        if (!SpawnSpliceSource(args))
        {
            _logger.LogError("Movie Night: spike 6 - failed to restart the movie encoder for resume");
            return false;
        }

        if (!await WaitForFirstSegmentAsync(ResumedMovieDirectory, "resume_").ConfigureAwait(false))
        {
            _logger.LogError("Movie Night: spike 6 - resumed movie encoder did not produce a segment in time");
            return false;
        }

        // Per Jon: don't cut to the new source the instant a single segment exists - let it run
        // long enough to build a real rolling buffer first. The filler keeps serving the whole
        // time, so nothing user-visible is lost by waiting.
        await Task.Delay(TimeSpan.FromSeconds(ResumeWarmupBufferSeconds)).ConfigureAwait(false);

        var resumeCursor = GetLatestSegmentIndex(ResumedMovieDirectory, "resume_");

        // The already-running splice timer picks up this change on its next tick (within
        // SpliceTickSeconds) - no separate restart needed.
        lock (_lock)
        {
            _isPaused = false;
            _activeSourceDirectory = ResumedMovieDirectory;
            _activeSourcePrefix = "resume_";
            _activeSourceCursor = Math.Max(0, resumeCursor);
            _pendingDiscontinuityOnNextCopy = true;
            _startedAtUtc = DateTime.UtcNow.AddSeconds(-resumePosition);
        }

        StartWatchdog();
        _logger.LogInformation("Movie Night: spike 6 - resumed from {Position:F1}s (paused at {PausedAt:F1}s), pointed back at movie", resumePosition, pausedAt);
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
    /// Kills <paramref name="process"/>, suppressing <see cref="OnProcessExited"/> only if it's
    /// still the tracked <see cref="_process"/> at the moment of the call (Resume() calls this
    /// synchronously, right before reassigning <see cref="_process"/> to the replacement, so the
    /// suppression window is short and precise - not an independent timer racing against
    /// whatever else might reassign <see cref="_process"/> in the meantime, which is what made
    /// the earlier delayed-teardown design (spike 5) buggy).
    /// </summary>
    private async Task KillTrackedProcessAsync(Process process)
    {
        bool suppressed;
        lock (_lock)
        {
            suppressed = ReferenceEquals(_process, process);
            if (suppressed)
            {
                _suppressExitHandling = true;
            }
        }

        await KillProcessGracefullyAsync(process).ConfigureAwait(false);

        if (suppressed)
        {
            lock (_lock)
            {
                _suppressExitHandling = false;
            }
        }
    }

    /// <summary>
    /// SIGTERM, grace period, SIGKILL - on an arbitrary given process, not necessarily
    /// <see cref="_process"/>.
    /// </summary>
    private async Task KillProcessGracefullyAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        if (!TrySendSigterm(process))
        {
            await TryKillDirectlyAsync(process).ConfigureAwait(false);
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(StopGraceSeconds));
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryKillDirectlyAsync(process).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Kills the process and waits for its exit event to actually fire before returning - a bare
    /// <c>Process.Kill()</c> only requests termination, it doesn't wait for the OS to report it
    /// dead. Found via a real bug (2026-08-14 live testing): the caller that suppresses
    /// <see cref="OnProcessExited"/> around a deliberate kill released that suppression right
    /// after this returned, but the real exit event sometimes arrived slightly LATER than a bare
    /// <c>Kill()</c> call, landing unprotected and getting misread as a crash.
    /// </summary>
    private static async Task TryKillDirectlyAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and Kill(); nothing to do.
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
    /// Points the served playlist at whatever new segments the currently-active source (filler or
    /// a resumed movie) has produced since the last tick - no file copying, per Jon: "what we've
    /// built is largely a pointer for an HLS stream, we need to seamlessly transition pointing
    /// from one stream to another." Each source keeps writing its own segments, under its own
    /// filename prefix, in its own directory; this just adds their names to the served list.
    /// <c>MovieNightController</c> resolves a requested filename to the right directory via
    /// <see cref="ResolveServedFilePath"/>. Runs on <see cref="_spliceTimer"/> only -
    /// Pause()/Resume() don't also call this directly; that dual-trigger was a real race found
    /// live-testing spike 5. This guard is a backstop in case a tick is ever slow enough for the
    /// next one to fire before it finishes regardless.
    /// </summary>
    private void SpliceTick()
    {
        lock (_lock)
        {
            if (_spliceTickRunning)
            {
                return;
            }

            _spliceTickRunning = true;
        }

        try
        {
            SpliceTickCore();
        }
        finally
        {
            lock (_lock)
            {
                _spliceTickRunning = false;
            }
        }
    }

    private void SpliceTickCore()
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

        var addedAny = false;
        while (true)
        {
            var sourceFileName = $"{sourcePrefix}{cursor:D3}.ts";
            if (!File.Exists(Path.Combine(sourceDir, sourceFileName)))
            {
                break;
            }

            lock (_lock)
            {
                var markDiscontinuity = _pendingDiscontinuityOnNextCopy;
                _pendingDiscontinuityOnNextCopy = false;
                _servedSegments.Add((sourceFileName, SplicedSegmentSeconds, markDiscontinuity));
                while (_servedSegments.Count > ServedWindowSize)
                {
                    _servedSegments.RemoveAt(0);
                }

                _activeSourceCursor = cursor + 1;
                _lastServedSegmentUtc = DateTime.UtcNow;
            }

            cursor++;
            addedAny = true;
        }

        if (addedAny)
        {
            WriteServedPlaylist();
        }
    }

    /// <summary>
    /// Resolves a requested segment filename to its actual physical directory, by prefix -
    /// <c>segment_</c> is the live/original movie in <see cref="MovieDirectory"/>, <c>filler_</c> is
    /// the always-running filler, <c>resume_</c> is a resumed movie. Used by
    /// <c>MovieNightController</c> so it never needs to know these directories exist; also
    /// re-verifies the resolved path lands inside the target directory (path-traversal guard) and
    /// that the file actually exists before handing a path back.
    /// </summary>
    /// <param name="safeName">An already-validated filename (see MovieNightController's regex) - not raw user input.</param>
    /// <returns>The full path if it resolves to a real file inside the right directory, otherwise <c>null</c>.</returns>
    public string? ResolveServedFilePath(string safeName)
    {
        string? directory = safeName switch
        {
            _ when safeName.StartsWith("filler_", StringComparison.Ordinal) => FillerDirectory,
            _ when safeName.StartsWith("resume_", StringComparison.Ordinal) => ResumedMovieDirectory,
            _ when safeName.StartsWith("segment_", StringComparison.Ordinal) => MovieDirectory,
            _ => null,
        };

        if (directory is null)
        {
            return null;
        }

        var directoryFull = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(Path.Combine(directoryFull, safeName));
        if (!fullPath.StartsWith(directoryFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return null;
        }

        // CA3003 flags this as tainted despite the checks above: the analyzer's dataflow doesn't
        // model the prefix switch + anchored regex validation in MovieNightController plus the
        // StartsWith directory-containment re-check above as sanitization. safeName is provably
        // restricted to one of the three known prefixes there, and fullPath is provably inside
        // whichever directory that prefix maps to.
#pragma warning disable CA3003
        return File.Exists(fullPath) ? fullPath : null;
#pragma warning restore CA3003
    }

    /// <summary>
    /// SPIKE (raw MPEG-TS tuner feed, 2026-08-14, per Jon: "I feel like single process with inputs
    /// makes sense to try as does raw mpeg tuner. No reason not to try both."): spawns a standalone
    /// synthetic testsrc/tone encoder writing raw <c>-f mpegts</c> to stdout, for
    /// <c>MovieNightController</c> to pipe straight through to whatever client connects. Answers
    /// one open architectural question - does Jellyfin's M3U tuner accept and remux a raw
    /// continuous MPEG-TS stream URL the same way it does our hand-rolled HLS - without touching
    /// any of the real broadcast's state machine, encoders, or directories. Debug-only: a fresh
    /// process per connection (no reuse across reconnects), same model as a real hardware tuner
    /// only ever having one downstream reader at a time. If this direction is chosen for real, the
    /// remaining open question is fan-out to multiple simultaneous readers of one live encode -
    /// not addressed here, out of scope for a yes/no spike.
    /// </summary>
    /// <returns>The started process (stdout piped, not yet read from), or <c>null</c> on failure to start.</returns>
    public Process? StartRawTsSpikeProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildRawTsSpikeArgs())
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Movie Night: raw-TS spike - failed to start test encoder");
            return null;
        }
    }

    /// <summary>
    /// Stops a process started by <see cref="StartRawTsSpikeProcess"/> - called once the HTTP
    /// client that was reading its stdout disconnects.
    /// </summary>
    /// <param name="process">The process to stop and dispose.</param>
    public void StopRawTsSpikeProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and Kill(); nothing to do.
        }
        finally
        {
            process.Dispose();
        }
    }

    private static List<string> BuildRawTsSpikeArgs() => new List<string>
    {
        "-re", "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=30",
        "-re", "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=44100",
        "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-level", "4.0",
        "-b:v", "2500k", "-maxrate", "2500k", "-bufsize", "5000k",
        "-c:a", "aac", "-b:a", "96k", "-ac", "2",
        "-f", "mpegts",
        "pipe:1",
    };

    /// <summary>
    /// SPIKE (single-process/multi-input switcher, 2026-08-14, per Jon: "I feel like single
    /// process with inputs makes sense to try as does raw mpeg tuner"): one continuous ffmpeg
    /// process holds three synthetic color/tone inputs open simultaneously (standing in for the
    /// real welcome/pause/goodbye assets, which don't exist yet), switched via a
    /// <c>streamselect</c> filter. Control channel is ffmpeg's interactive stdin command protocol
    /// (the 'c' keypress + <c>streamselect -1 map N</c>), NOT zmq: jellyfin-ffmpeg 7.1.3 is built
    /// without libzmq ("No such filter: 'zmq'", confirmed 2026-08-14 via encode-probe on the real
    /// box), while the stdin protocol is core ffmpeg and was verified live to switch losslessly
    /// (frame-inspected color change, command reply ret:0). Explicitly does not attempt movie
    /// seek/switching (ffmpeg has no live-reseek primitive for an open input, per the ruling) -
    /// this spike only tests whether lossless switching among the three static sources works and
    /// survives Jellyfin's own remux hop. Same standalone/independent-of-real-state model as
    /// <see cref="StartRawTsSpikeProcess"/>.
    /// </summary>
    /// <returns>The started process (stdout piped, not yet read from), or <c>null</c> on failure to start.</returns>
    public Process? StartSwitcherSpikeProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildSwitcherSpikeArgs())
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            var process = new Process { StartInfo = startInfo };
            var stderrTail = new StderrTailBuffer();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderrTail.Add(e.Data);
                }
            };
            process.Start();
            process.BeginErrorReadLine();

            lock (_lock)
            {
                _switcherSpikeProcesses.Add((process, stderrTail));
            }

            _logger.LogInformation("Movie Night: switcher spike - started encoder pid {Pid}", process.Id);
            return process;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Movie Night: switcher spike - failed to start test encoder");
            return null;
        }
    }

    /// <summary>
    /// Stops a process started by <see cref="StartSwitcherSpikeProcess"/>, logging its stderr
    /// tail so ffmpeg's reaction to stdin commands is visible after the fact.
    /// </summary>
    /// <param name="process">The process to stop and dispose.</param>
    public void StopSwitcherSpikeProcess(Process process)
    {
        StderrTailBuffer? stderrTail = null;
        lock (_lock)
        {
            var index = _switcherSpikeProcesses.FindIndex(e => ReferenceEquals(e.Process, process));
            if (index >= 0)
            {
                stderrTail = _switcherSpikeProcesses[index].Stderr;
                _switcherSpikeProcesses.RemoveAt(index);
            }
        }

        try
        {
            var pid = process.Id;
            if (!process.HasExited)
            {
                process.Kill();
            }

            _logger.LogInformation("Movie Night: switcher spike - stopped encoder pid {Pid}. stderr tail: {Stderr}", pid, stderrTail?.ToString() ?? "(untracked)");
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check and Kill(); nothing to do.
        }
        finally
        {
            process.Dispose();
        }
    }

    private List<string> BuildSwitcherSpikeArgs()
    {
        // SWITCHER V2 (2026-08-14 evening, Jon: "let's do it"): when a movie has been configured
        // via the sw2 debug endpoints, the switcher channel serves the REAL graph - the movie
        // arriving through the restartable feeder (input 0) plus a pause card with quiet tone
        // (inputs 1/2), with video AND audio switched together. Without a configured movie it
        // stays the original three-color proof.
        string? moviePath;
        lock (_lock)
        {
            moviePath = _sw2MoviePath;
        }

        if (moviePath is not null)
        {
            var feedUrl = FormattableString.Invariant($"http://127.0.0.1:{_sw2HttpPort}/MovieNight/stream/feed.ts");
            return new List<string>
            {
                // The feed input stalls (no bytes, no EOF) while the feeder is being restarted at
                // a new position - the controller's feed.ts endpoint holds the connection open
                // across feeder swaps precisely so this input never terminates. genpts because
                // each new feeder starts a fresh mpegts timeline.
                "-fflags", "+genpts", "-i", feedUrl,
                "-re", "-f", "lavfi", "-i", "color=c=darkslategray:s=1280x720:r=30",
                "-re", "-f", "lavfi", "-i", "sine=frequency=196:sample_rate=44100",
                "-filter_complex",
                "[0:v]fps=30,scale=1280:720,format=yuv420p[mv];" +
                "[1:v]format=yuv420p[pv];" +
                "[0:a]aresample=44100:async=1,aformat=sample_fmts=fltp:channel_layouts=stereo[ma];" +
                "[2:a]aresample=44100,volume=0.15,aformat=sample_fmts=fltp:channel_layouts=stereo[pa];" +
                "[mv][pv]streamselect=inputs=2:map=0[vout];" +
                "[ma][pa]astreamselect=inputs=2:map=0[aout]",
                "-map", "[vout]", "-map", "[aout]",
                "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-level", "4.0",
                "-b:v", "2500k", "-maxrate", "2500k", "-bufsize", "5000k",
                "-c:a", "aac", "-b:a", "96k", "-ac", "2",
                "-f", "mpegts",
                "pipe:1",
            };
        }

        return new List<string>
        {
            "-re", "-f", "lavfi", "-i", "color=c=blue:s=1280x720:r=30",
            "-re", "-f", "lavfi", "-i", "color=c=maroon:s=1280x720:r=30",
            "-re", "-f", "lavfi", "-i", "color=c=darkgreen:s=1280x720:r=30",
            "-re", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
            "-filter_complex", "[0:v][1:v][2:v]streamselect=inputs=3:map=0[vout]",
            "-map", "[vout]", "-map", "3:a",
            "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-level", "4.0",
            "-b:v", "2500k", "-maxrate", "2500k", "-bufsize", "5000k",
            "-c:a", "aac", "-b:a", "96k", "-ac", "2",
            "-f", "mpegts",
            "pipe:1",
        };
    }

    /// <summary>
    /// Sends a live <c>streamselect map N</c> command to the currently-running switcher spike
    /// process over its stdin, using ffmpeg's interactive command protocol: the single character
    /// <c>c</c> enters command mode, then one line of <c>&lt;target&gt; &lt;time&gt; &lt;command&gt;
    /// &lt;arg&gt;</c> (time -1 = now). Verified live 2026-08-14: piped (non-tty) stdin is
    /// accepted, the filter replies ret:0, and the output stream switches losslessly.
    /// </summary>
    /// <param name="inputIndex">Which of the 3 static inputs to switch to (0, 1, or 2).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> on success, <c>false</c> plus a diagnostic message otherwise.</returns>
    public async Task<(bool Success, string Output)> SendSwitcherSpikeCommandAsync(int inputIndex, CancellationToken cancellationToken)
    {
        List<(Process Process, StderrTailBuffer Stderr)> targets;
        lock (_lock)
        {
            targets = new List<(Process, StderrTailBuffer)>(_switcherSpikeProcesses);
        }

        if (targets.Count == 0)
        {
            return (false, "No switcher spike process running - tune the switcher spike channel first");
        }

        // Target "all" rather than a filter name so the command reaches BOTH streamselect and
        // astreamselect in the v2 graph (filters that don't understand "map" reply ENOSYS, which
        // is harmless and visible in the stderr echo).
        var command = FormattableString.Invariant($"call -1 map {inputIndex}\n");
        var results = new List<string>();
        var anySuccess = false;
        foreach (var (process, stderr) in targets)
        {
            try
            {
                if (process.HasExited)
                {
                    results.Add($"pid {process.Id}: already exited");
                    continue;
                }

                await process.StandardInput.WriteAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                anySuccess = true;
                results.Add($"pid {process.Id}: sent");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                results.Add($"pid {process.Id}: write failed - {ex.Message}");
            }
        }

        // Give ffmpeg a moment to react, then include each process's recent stderr so the
        // command replies (or their absence) are visible directly in the API response.
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        foreach (var (process, stderr) in targets)
        {
            var tail = stderr.ToString();
            var lastLines = tail.Length > 400 ? tail[^400..] : tail;
            results.Add($"pid {process.Id} stderr tail: {lastLines}");
        }

        var output = $"streamselect -1 map {inputIndex} -> {targets.Count} process(es)\n" + string.Join('\n', results);
        _logger.LogInformation("Movie Night: switcher spike command: {Output}", output);
        return (anySuccess, output);
    }

    /// <summary>
    /// SWITCHER V2: configures the movie the switcher channel should carry and starts its feeder
    /// from the beginning. The switcher process itself spawns when a client tunes the channel.
    /// </summary>
    /// <param name="itemPath">Filesystem path of the movie.</param>
    /// <param name="httpPort">The server's own HTTP port, for the loopback feed URL.</param>
    public void ConfigureSwitcherV2(string itemPath, int httpPort)
    {
        Process? oldFeeder;
        lock (_lock)
        {
            oldFeeder = _sw2FeederProcess;
            _sw2FeederProcess = null;
            _sw2MoviePath = itemPath;
            _sw2HttpPort = httpPort;
            _sw2Paused = false;
        }

        KillSw2Feeder(oldFeeder);
        StartSw2Feeder(0);
        _logger.LogInformation("Movie Night: switcher v2 configured for {Path}", itemPath);
    }

    /// <summary>
    /// SWITCHER V2: tears the session down - feeder killed, movie cleared, the switcher channel
    /// falls back to the three-color test graph on next tune.
    /// </summary>
    public void ClearSwitcherV2()
    {
        Process? oldFeeder;
        lock (_lock)
        {
            oldFeeder = _sw2FeederProcess;
            _sw2FeederProcess = null;
            _sw2MoviePath = null;
            _sw2Paused = false;
            _sw2FeederStartSeconds = 0;
            _sw2FeederStartedUtc = null;
        }

        KillSw2Feeder(oldFeeder);
        _logger.LogInformation("Movie Night: switcher v2 cleared");
    }

    /// <summary>
    /// SWITCHER V2: pauses - logs the movie position (feeder start offset plus wall-clock elapsed,
    /// the same wall-clock ruling as the HLS design), switches BOTH selects to the pause card, and
    /// kills the feeder so the movie stops being consumed while paused.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success plus the logged pause position (or a diagnostic).</returns>
    public async Task<(bool Success, string Output)> PauseSwitcherV2Async(CancellationToken cancellationToken)
    {
        Process? feeder;
        double pausedAt;
        lock (_lock)
        {
            if (_sw2MoviePath is null || _sw2Paused)
            {
                return (false, "No switcher v2 session, or already paused");
            }

            pausedAt = _sw2FeederStartSeconds + (_sw2FeederStartedUtc is DateTime started ? (DateTime.UtcNow - started).TotalSeconds : 0);
            _sw2FeederStartSeconds = pausedAt;
            _sw2FeederStartedUtc = null;
            _sw2Paused = true;
            feeder = _sw2FeederProcess;
            _sw2FeederProcess = null;
        }

        var (sent, output) = await SendSwitcherSpikeCommandAsync(1, cancellationToken).ConfigureAwait(false);
        KillSw2Feeder(feeder);
        _logger.LogInformation("Movie Night: switcher v2 paused at {Position:F1}s", pausedAt);
        return (sent, FormattableString.Invariant($"paused at {pausedAt:F1}s\n{output}"));
    }

    /// <summary>
    /// SWITCHER V2: resumes - restarts the feeder a little before the logged pause position
    /// (Jon's ruling: back to the timestamp, or actually a little before it), gives it a few
    /// seconds to warm so the switcher's stalled feed input has fresh data, then switches both
    /// selects back to the movie.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Success plus the resume position (or a diagnostic).</returns>
    public async Task<(bool Success, string Output)> ResumeSwitcherV2Async(CancellationToken cancellationToken)
    {
        double resumeAt;
        lock (_lock)
        {
            if (_sw2MoviePath is null || !_sw2Paused)
            {
                return (false, "No switcher v2 session, or not paused");
            }

            resumeAt = Math.Max(0, _sw2FeederStartSeconds - Sw2ResumeRewindSeconds);
        }

        StartSw2Feeder(resumeAt);

        // Let the fresh feeder push real data into the switcher's stalled feed input before
        // cutting to it - the pause card keeps showing during this, so nothing is user-visible.
        await Task.Delay(TimeSpan.FromSeconds(Sw2ResumeWarmupSeconds), cancellationToken).ConfigureAwait(false);

        var (sent, output) = await SendSwitcherSpikeCommandAsync(0, cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            _sw2Paused = false;
        }

        _logger.LogInformation("Movie Night: switcher v2 resumed from {Position:F1}s", resumeAt);
        return (sent, FormattableString.Invariant($"resumed from {resumeAt:F1}s\n{output}"));
    }

    /// <summary>
    /// SWITCHER V2: session snapshot for the debug status endpoint.
    /// </summary>
    /// <returns>Movie path, pause state, current movie position, and process liveness.</returns>
    public object GetSwitcherV2Status()
    {
        lock (_lock)
        {
            var position = _sw2FeederStartSeconds + (_sw2FeederStartedUtc is DateTime started ? (DateTime.UtcNow - started).TotalSeconds : 0);
            return new
            {
                MoviePath = _sw2MoviePath,
                Paused = _sw2Paused,
                PositionSeconds = Math.Round(position, 1),
                FeederAlive = _sw2FeederProcess is not null && !_sw2FeederProcess.HasExited,
                SwitcherProcessCount = _switcherSpikeProcesses.Count,
            };
        }
    }

    /// <summary>
    /// SWITCHER V2: the current feeder process, for the controller's feed.ts copy loop. May change
    /// between calls (resume swaps feeders) - the copy loop re-fetches after each stream ends.
    /// </summary>
    /// <returns>The live feeder process, or <c>null</c> if none is running right now.</returns>
    public Process? GetSw2FeederProcess()
    {
        lock (_lock)
        {
            return _sw2FeederProcess is not null && !_sw2FeederProcess.HasExited ? _sw2FeederProcess : null;
        }
    }

    /// <summary>
    /// SWITCHER V2: whether a movie session is configured (drives whether feed.ts stays open).
    /// </summary>
    /// <returns><c>true</c> while a movie is configured.</returns>
    public bool IsSwitcherV2SessionActive()
    {
        lock (_lock)
        {
            return _sw2MoviePath is not null;
        }
    }

    private void StartSw2Feeder(double startSeconds)
    {
        string moviePath;
        lock (_lock)
        {
            if (_sw2MoviePath is null)
            {
                return;
            }

            moviePath = _sw2MoviePath;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // QSV-only for the spike (this NAS's real config); mirrors the proven Go Live video chain
        // including the v0.3.22 keyframe fix. Mezzanine bitrate a bit above the final encode so
        // the switcher's re-encode isn't stacking two lossy passes at the same rate.
        var args = new List<string>();
        if (startSeconds > 0)
        {
            args.AddRange(["-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-re", "-i", moviePath]);
        args.AddRange(["-init_hw_device", "qsv=hw", "-filter_hw_device", "hw", "-c:v", "h264_qsv", "-preset", "veryfast", "-vf", "format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:720"]);
        args.AddRange(["-profile:v", "high", "-level", "4.0", "-b:v", "4000k", "-maxrate", "4000k", "-bufsize", "8000k", "-g", "100", "-forced_idr", "1"]);
        args.AddRange(["-c:a", "aac", "-b:a", "128k", "-ac", "2"]);
        args.AddRange(["-f", "mpegts", "pipe:1"]);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            var process = new Process { StartInfo = startInfo };
            var stderrTail = new StderrTailBuffer();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderrTail.Add(e.Data);
                }
            };
            process.Start();
            process.BeginErrorReadLine();

            lock (_lock)
            {
                _sw2FeederProcess = process;
                _sw2FeederStderr = stderrTail;
                _sw2FeederStartSeconds = startSeconds;
                _sw2FeederStartedUtc = DateTime.UtcNow;
            }

            _logger.LogInformation("Movie Night: switcher v2 feeder pid {Pid} started at {Position:F1}s", process.Id, startSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Movie Night: switcher v2 - failed to start feeder");
        }
    }

    private void KillSw2Feeder(Process? feeder)
    {
        if (feeder is null)
        {
            return;
        }

        try
        {
            if (!feeder.HasExited)
            {
                feeder.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited; nothing to do.
        }
        finally
        {
            feeder.Dispose();
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
        lock (_lock)
        {
            segments = new List<(string, double, bool)>(_servedSegments);
        }

        if (segments.Count == 0)
        {
            return;
        }

        // The exact starting number doesn't matter to any player - EXT-X-MEDIA-SEQUENCE just has
        // to be a stable per-playlist-write value; nothing here parses it back out.
        var mediaSequence = 0;
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

    private void SetStartingUnlocked(string channelName, string nowPlaying, long? runTimeTicks)
    {
        _state = BroadcastState.Starting;
        _channelName = channelName;
        _nowPlaying = nowPlaying;
        _startedAtUtc = null;
        _runTimeTicks = runTimeTicks;
        _lastFailureReason = null;
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

            var diskUsage = GetActiveEncodersDiskUsage();
            if (diskUsage > DiskGuardBytes)
            {
                _logger.LogError("Movie Night: HLS output directory exceeded the {LimitMb}MB disk guard ({ActualMb}MB) - segment deletion may not be keeping up", DiskGuardBytes / 1024 / 1024, diskUsage / 1024 / 1024);
                SetFailedUnlocked("HLS output exceeded the disk guard limit");
                KillProcessUnlocked();
                StopWatchdogUnlocked();
            }
        }
    }

    /// <summary>
    /// Called under <see cref="_lock"/> from <see cref="CheckWatchdog"/>. Checks the served
    /// output's own progress (<see cref="_lastServedSegmentUtc"/>, stamped by
    /// <see cref="SpliceTickCore"/>) rather than scanning any one source directory for fresh
    /// files - the thing that actually matters is whether the SERVED playlist is still advancing,
    /// since that's what Jellyfin's downstream remux treats as "stream ended" if it stalls. Before
    /// this fix, the watchdog scanned <see cref="HlsDirectory"/> for <c>*.ts</c> files directly,
    /// which broke once that directory stopped holding segments at all (see the HlsDirectory doc
    /// comment).
    /// </summary>
    private bool IsSegmentStale() =>
        _lastServedSegmentUtc is null
            || DateTime.UtcNow - _lastServedSegmentUtc.Value > TimeSpan.FromSeconds(SegmentStallSeconds);

    /// <summary>
    /// Sums the on-disk size of every directory an encoder can currently be writing segments into
    /// - <see cref="HlsDirectory"/> itself only ever holds the small hand-written master.m3u8 text
    /// file now, so it's excluded.
    /// </summary>
    private long GetActiveEncodersDiskUsage()
    {
        long total = 0;
        foreach (var dir in new[] { MovieDirectory, FillerDirectory, ResumedMovieDirectory })
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    total += Directory.EnumerateFiles(dir).Sum(f => new FileInfo(f).Length);
                }
            }
            catch (IOException)
            {
                // Skip; a directory mid-write shouldn't fail the whole disk-guard check.
            }
        }

        return total;
    }

    /// <summary>
    /// Public wrapper around <see cref="TriggerGuideRefresh"/> - lets the config API force a guide
    /// refresh after the admin toggles which spike test channels are enabled, so the change is
    /// reflected in Jellyfin's channel list without waiting for the next automatic refresh.
    /// </summary>
    public void RefreshChannelsGuide() => TriggerGuideRefresh();

    /// <summary>
    /// Diagnostic encode probe (added 2026-08-14 for the 25fps Go Live stall): runs ffmpeg with a
    /// caller-supplied argument list into a scratch directory for a fixed number of seconds, kills
    /// it, and reports what files appeared plus the full stderr. Lets the stall be bisected live
    /// (no audio? different interleave delta? software vs QSV?) without a release-and-restart
    /// cycle per experiment. Completely independent of broadcast state - uses its own directory
    /// and never touches <see cref="_process"/>.
    /// </summary>
    /// <param name="args">Full ffmpeg argument list. The placeholder <c>{out}</c> in any argument
    /// is replaced with the scratch directory path.</param>
    /// <param name="seconds">How long to let ffmpeg run before killing it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Files created (name, size) and the stderr text (head + tail).</returns>
    public async Task<object> RunEncodeProbeAsync(IReadOnlyList<string> args, int seconds, CancellationToken cancellationToken)
    {
        var probeDir = Path.Combine(_configurationManager.ApplicationPaths.TempDirectory, "MovieNight", "probe");
        CleanDirectory(probeDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = _mediaEncoder.EncoderPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg.Replace("{out}", probeDir, StringComparison.Ordinal));
        }

        using var process = new Process { StartInfo = startInfo };
        var stderr = new StderrTailBuffer();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.Add(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        var exited = false;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(seconds));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            exited = true;
        }
        catch (OperationCanceledException)
        {
            await TryKillDirectlyAsync(process).ConfigureAwait(false);
        }

        var files = Directory.Exists(probeDir)
            ? Directory.EnumerateFiles(probeDir)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => (object)new { f.Name, f.Length })
                .ToArray()
            : Array.Empty<object>();

        return new
        {
            ExitedOnItsOwn = exited,
            ExitCode = exited ? SafeGetExitCode(process) : (int?)null,
            Files = files,
            Stderr = stderr.ToString(),
        };
    }

    /// <summary>
    /// Diagnostic snapshot of every encoder output directory (added 2026-08-14 for the 25fps Go
    /// Live stall): the NAS container's /tmp is unreachable from outside, so this is the only way
    /// to see whether ffmpeg is actually writing segments while a broadcast is Starting. Read via
    /// the debug controller, safe to call at any time.
    /// </summary>
    /// <returns>Per-directory existence plus file names, sizes, and last-write times.</returns>
    public object DescribeOutputDirectories()
    {
        object Describe(string dir)
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    return new { Path = dir, Exists = false, Files = Array.Empty<object>() };
                }

                var files = Directory.EnumerateFiles(dir)
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.Name, StringComparer.Ordinal)
                    .Select(f => (object)new { f.Name, f.Length, LastWriteUtc = f.LastWriteTimeUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) })
                    .ToArray();
                return new { Path = dir, Exists = true, Files = files };
            }
            catch (Exception ex)
            {
                return new { Path = dir, Error = ex.Message };
            }
        }

        return new
        {
            State = GetStatus().State.ToString(),
            Hls = Describe(HlsDirectory),
            Movie = Describe(MovieDirectory),
            Filler = Describe(FillerDirectory),
            Resume = Describe(ResumedMovieDirectory),
        };
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
        lock (_lock)
        {
            foreach (var (process, _) in _switcherSpikeProcesses)
            {
                process.Dispose();
            }

            _switcherSpikeProcesses.Clear();
            _sw2FeederProcess?.Dispose();
            _sw2FeederProcess = null;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int NativeKill(int pid, int signal);

    /// <summary>
    /// Keeps the FIRST lines of ffmpeg stderr (input probe, stream mapping, filter init, first
    /// segment opens - where startup failures actually explain themselves) plus a rolling tail of
    /// the most recent lines, for surfacing in failure messages (spec §5.4/§7's "Last failure
    /// panel"). Head capture added 2026-08-14 while diagnosing the 25fps Go Live stall: a
    /// tail-only buffer showed 40 healthy progress lines and nothing else, which made three
    /// separate live failures undiagnosable from their own error messages.
    /// </summary>
    private sealed class StderrTailBuffer
    {
        private const int MaxHeadLines = 60;
        private const int MaxTailLines = 40;
        private readonly object _bufferLock = new();
        private readonly List<string> _head = new();
        private readonly Queue<string> _tail = new();
        private int _totalLines;

        public void Add(string line)
        {
            lock (_bufferLock)
            {
                _totalLines++;
                if (_head.Count < MaxHeadLines)
                {
                    _head.Add(line);
                    return;
                }

                _tail.Enqueue(line);
                while (_tail.Count > MaxTailLines)
                {
                    _tail.Dequeue();
                }
            }
        }

        public override string ToString()
        {
            lock (_bufferLock)
            {
                if (_tail.Count == 0)
                {
                    return string.Join('\n', _head);
                }

                var omitted = _totalLines - _head.Count - _tail.Count;
                var middle = omitted > 0 ? $"\n... [{omitted} lines omitted] ...\n" : "\n";
                return string.Join('\n', _head) + middle + string.Join('\n', _tail);
            }
        }
    }
}

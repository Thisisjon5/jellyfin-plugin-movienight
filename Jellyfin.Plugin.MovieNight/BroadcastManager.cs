using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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

    private readonly object _lock = new();
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IGuideManager _guideManager;
    private readonly ILogger<BroadcastManager> _logger;

    private Process? _process;
    private Timer? _watchdogTimer;
    private StderrTailBuffer? _stderrTail;
    private BroadcastState _state = BroadcastState.Idle;
    private string? _channelName;
    private string? _nowPlaying;
    private DateTime? _startedAtUtc;
    private long? _runTimeTicks;
    private string? _lastFailureReason;

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
    }

    /// <summary>
    /// Gets the directory the current (or most recent) broadcast's HLS output lives in.
    /// </summary>
    public string HlsDirectory { get; }

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
        lock (_lock)
        {
            process = _process;
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
        }

        TriggerGuideRefresh();
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
                SetFailedUnlocked("Encoder stalled - no new HLS segment written in time");
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

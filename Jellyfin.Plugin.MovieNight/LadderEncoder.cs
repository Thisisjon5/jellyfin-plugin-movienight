using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// Owns the live ladder encoder process and the directory it writes: one ffmpeg reading
/// <c>feed.ts</c>, N encoded rungs out as sliding-window HLS (DESIGN-abr-ladder.md §4(D)/(E) as
/// revised by ROADMAP-2026-09-03.md §3). Also records who fetches the ladder, because that is
/// the instrument that tells a direct-playing client from a server-side hop: the viewer's own
/// address and player User-Agent, versus the server's address and Lavf.
/// <para>
/// Lifecycle decisions (restart on crash, when to stop) belong to <see cref="LiveSession"/>; this
/// class only starts, stops, and describes the process.
/// </para>
/// </summary>
public sealed class LadderEncoder : IDisposable
{
    private const int MaxRecordedHits = 40;

    private readonly object _lock = new();
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<LadderEncoder> _logger;
    private readonly List<string> _recentHits = [];
    private readonly Dictionary<string, int> _hitsByRemote = new(StringComparer.Ordinal);

    private Process? _process;
    private StderrTailBuffer? _stderr;
    private IReadOnlyList<string>? _args;
    private IReadOnlyList<LadderRung>? _rungs;
    private HardwareAccel _accel;
    private DateTime? _startedUtc;
    private int? _lastExitCode;
    private int _totalHits;
    private int _notFoundHits;

    /// <summary>
    /// Initializes a new instance of the <see cref="LadderEncoder"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Used to find the server's own ffmpeg binary.</param>
    /// <param name="configurationManager">Used for a writable directory. Segments live on disk, not tmpfs: the window is ~40 MB and the production box is presumably Windows.</param>
    /// <param name="logger">Logger.</param>
    public LadderEncoder(IMediaEncoder mediaEncoder, IServerConfigurationManager configurationManager, ILogger<LadderEncoder> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        LadderDirectory = Path.Combine(configurationManager.ApplicationPaths.TempDirectory, "MovieNight", "ladder");
    }

    /// <summary>
    /// Gets or sets the callback invoked (on a thread-pool thread) when the encoder process exits,
    /// with its exit code. <see cref="LiveSession"/> owns the restart decision.
    /// </summary>
    public Action<int>? ExitedCallback { get; set; }

    /// <summary>Gets the directory holding <c>master.m3u8</c> and the per-rung <c>v{i}/</c> folders.</summary>
    public string LadderDirectory { get; }

    /// <summary>Gets the encoder's captured stderr (head + rolling tail), or null before the first start.</summary>
    public string? StderrTail
    {
        get
        {
            lock (_lock)
            {
                return _stderr?.ToString();
            }
        }
    }

    /// <summary>Gets a value indicating whether the encoder process is alive.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _process is not null && !HasExitedSafe(_process);
            }
        }
    }

    /// <summary>
    /// Starts the encoder. Any previous process is killed first.
    /// </summary>
    /// <param name="args">Argument list from <see cref="LadderCommandBuilder.Build"/>.</param>
    /// <param name="rungs">The rung table the args encode (for status).</param>
    /// <param name="accel">The backend the args use (for status).</param>
    /// <param name="cleanDirectory">True to wipe the ladder directory first (Go Live); false to restart into it (crash recovery).</param>
    /// <returns>True if the process started.</returns>
    public bool Start(IReadOnlyList<string> args, IReadOnlyList<LadderRung> rungs, HardwareAccel accel, bool cleanDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(rungs);

        Process? old;
        lock (_lock)
        {
            old = _process;
            _process = null;
        }

        KillQuietly(old);

        if (cleanDirectory)
        {
            CleanDirectory();
            ResetHits();
        }

        // %v expands to the rung index, but the HLS muxer will not create those subdirectories.
        for (var i = 0; i < rungs.Count; i++)
        {
            Directory.CreateDirectory(Path.Combine(LadderDirectory, FormattableString.Invariant($"v{i}")));
        }

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
        var stderr = new StderrTailBuffer();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.Add(e.Data);
            }
        };
        process.Exited += (_, _) => OnProcessExited(process);

        try
        {
            process.Start();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Movie Night: ladder encoder failed to start");
            process.Dispose();
            return false;
        }

        lock (_lock)
        {
            _process = process;
            _stderr = stderr;
            _args = args;
            _rungs = rungs;
            _accel = accel;
            _startedUtc = DateTime.UtcNow;
            _lastExitCode = null;
        }

        _logger.LogInformation("Movie Night: ladder encoder pid {Pid} started ({Rungs} rung(s), {Accel}): {Args}", process.Id, rungs.Count, accel, string.Join(' ', args));
        return true;
    }

    /// <summary>
    /// Restarts the encoder with the arguments of the last <see cref="Start"/>, into the same
    /// directory, continuing the segment sequence after the highest segment on disk (a reset to
    /// 0 makes every HLS client abandon the stream) and declaring a discontinuity. Used for crash
    /// recovery. The stored args stay the ORIGINAL ones so repeated restarts recompute from disk.
    /// </summary>
    /// <returns>True if a process started.</returns>
    public bool Restart()
    {
        IReadOnlyList<string>? args;
        IReadOnlyList<LadderRung>? rungs;
        HardwareAccel accel;
        lock (_lock)
        {
            args = _args;
            rungs = _rungs;
            accel = _accel;
        }

        if (args is null || rungs is null)
        {
            return false;
        }

        var next = HighestSegmentNumber() + 1;
        _logger.LogInformation("Movie Night: ladder encoder restarting at segment {Next}", next);
        var started = Start(LadderCommandBuilder.WithRestartContinuity(args, next), rungs, accel, cleanDirectory: false);
        if (started)
        {
            lock (_lock)
            {
                _args = args;
            }
        }

        return started;
    }

    /// <summary>Highest <c>seg_N.ts</c> number in the top rung's directory, or -1 if none.</summary>
    /// <returns>The number.</returns>
    public int HighestSegmentNumber()
    {
        var v0 = Path.Combine(LadderDirectory, "v0");
        if (!Directory.Exists(v0))
        {
            return -1;
        }

        var highest = -1;
        foreach (var file in Directory.EnumerateFiles(v0, "seg_*.ts"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > highest)
            {
                highest = n;
            }
        }

        return highest;
    }

    /// <summary>
    /// Kills the encoder and waits briefly for it to go away. <see cref="ExitedCallback"/> still
    /// runs; callers that do not want to treat that as a crash set their own flag first.
    /// </summary>
    /// <returns>A task that completes once the process is gone or the wait gave up.</returns>
    public async Task StopAsync()
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!HasExitedSafe(process))
            {
                process.Kill();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Movie Night: ladder encoder did not exit within 5s of being killed");
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    /// <summary>
    /// Waits until the encoder has produced a master playlist, the top rung's index, and at least
    /// one top-rung segment - the moment a client can actually tune.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if output appeared; false on timeout or if the process died first.</returns>
    public async Task<bool> WaitForOutputAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (HasPlayableOutput())
            {
                return true;
            }

            if (!IsRunning)
            {
                return false;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        return HasPlayableOutput();
    }

    /// <summary>Whether master + v0 index + at least one v0 segment exist.</summary>
    /// <returns>True if a client can tune right now.</returns>
    public bool HasPlayableOutput()
    {
        var v0 = Path.Combine(LadderDirectory, "v0");
        return File.Exists(Path.Combine(LadderDirectory, "master.m3u8"))
            && File.Exists(Path.Combine(v0, "index.m3u8"))
            && Directory.Exists(v0)
            && Directory.EnumerateFiles(v0, "seg_*.ts").Any();
    }

    /// <summary>
    /// Resolves a requested ladder path to a file, or null. See <see cref="LadderFiles.ResolveFile"/>.
    /// </summary>
    /// <param name="requestPath">Path below <c>/MovieNight/stream/hls/</c>.</param>
    /// <returns>The full path on disk, or null.</returns>
    public string? ResolveFile(string? requestPath) => LadderFiles.ResolveFile(LadderDirectory, requestPath);

    /// <summary>
    /// Records a fetch of the ladder. The remote address plus User-Agent is what distinguishes a
    /// client pulling directly (its own IP, its player's UA) from Jellyfin fetching on a client's
    /// behalf (the server's IP, Lavf) - note a browser behind a reverse proxy arrives wearing the
    /// proxy's address, so the UA is the tiebreaker.
    /// </summary>
    /// <param name="remoteAddress">Remote address of the fetch.</param>
    /// <param name="userAgent">User agent of the fetch.</param>
    /// <param name="requestPath">What was requested.</param>
    /// <param name="found">Whether it resolved to a real file.</param>
    public void RecordHit(string? remoteAddress, string? userAgent, string? requestPath, bool found)
    {
        var remote = string.IsNullOrEmpty(remoteAddress) ? "unknown" : remoteAddress;
        lock (_lock)
        {
            _totalHits++;
            if (!found)
            {
                _notFoundHits++;
            }

            _hitsByRemote.TryGetValue(remote, out var count);
            _hitsByRemote[remote] = count + 1;
            if (_recentHits.Count >= MaxRecordedHits)
            {
                _recentHits.RemoveAt(0);
            }

            _recentHits.Add(FormattableString.Invariant($"{DateTime.UtcNow:HH:mm:ss} {remote} {(found ? "200" : "404")} {requestPath} [{userAgent}]"));
        }
    }

    /// <summary>Snapshot for the status API.</summary>
    /// <returns>Process state, rung table, per-rung segment counts, fetch instrumentation, stderr.</returns>
    public object GetStatus()
    {
        lock (_lock)
        {
            var rungs = _rungs ?? [];
            var rungStatus = rungs.Select((rung, i) =>
            {
                var dir = Path.Combine(LadderDirectory, FormattableString.Invariant($"v{i}"));
                var segments = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "seg_*.ts").Count() : 0;
                return (object)new { Index = i, rung.Height, rung.VideoKbps, rung.AudioKbps, SegmentsOnDisk = segments };
            }).ToArray();

            return new
            {
                Running = _process is not null && !HasExitedSafe(_process),
                Pid = _process is not null ? PidSafe(_process) : null,
                Accel = _accel.ToString(),
                StartedUtc = _startedUtc,
                LastExitCode = _lastExitCode,
                Playable = HasPlayableOutput(),
                LadderDirectory,
                Rungs = rungStatus,
                Fetches = new
                {
                    Total = _totalHits,
                    NotFound = _notFoundHits,
                    ByRemote = _hitsByRemote.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                    Recent = _recentHits.ToArray(),
                },
                Stderr = _stderr?.ToString(),
            };
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Process? process;
        lock (_lock)
        {
            process = _process;
            _process = null;
        }

        KillQuietly(process);
    }

    private static bool HasExitedSafe(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static int? PidSafe(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void KillQuietly(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ResetHits()
    {
        lock (_lock)
        {
            _recentHits.Clear();
            _hitsByRemote.Clear();
            _totalHits = 0;
            _notFoundHits = 0;
        }
    }

    private void CleanDirectory()
    {
        try
        {
            if (Directory.Exists(LadderDirectory))
            {
                Directory.Delete(LadderDirectory, recursive: true);
            }

            Directory.CreateDirectory(LadderDirectory);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Movie Night: failed to clean ladder directory {Dir}", LadderDirectory);
        }
    }

    private void OnProcessExited(Process process)
    {
        int code;
        try
        {
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            code = -1;
        }

        var isCurrent = false;
        string? stderr;
        lock (_lock)
        {
            if (ReferenceEquals(_process, process))
            {
                isCurrent = true;
                _lastExitCode = code;
            }

            stderr = _stderr?.ToString();
        }

        if (isCurrent)
        {
            _logger.LogWarning("Movie Night: ladder encoder pid {Pid} exited with {Code}. stderr: {Stderr}", PidSafe(process), code, stderr);
        }
        else
        {
            _logger.LogInformation("Movie Night: ladder encoder pid {Pid} exited with {Code} (superseded or stopped)", PidSafe(process), code);
        }

        ExitedCallback?.Invoke(code);
    }
}

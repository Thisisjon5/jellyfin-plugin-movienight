using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// T0 GATE SPIKE (planning/DESIGN-abr-ladder.md §8, T0). Owns the pre-generated STATIC HLS ladder
/// the gate test tunes, plus the instrumentation that answers the gate by measurement rather than
/// impression: which host actually fetched our playlists and segments.
/// <para>
/// The gate asks one thing - does Jellyfin hand the ladder URL to the client untouched, or does it
/// insert its own per-client ffmpeg remux hop (the O(N) behaviour that killed the 2026-08-18
/// soak). There is deliberately no feeder, no live pipeline and no muxer here: a synthetic,
/// finite, three-rung ladder on disk is enough to answer it, and every moving part left out is a
/// false failure that cannot happen.
/// </para>
/// Delete this class once the gate is answered and the real ladder lands (M2/M4).
/// </summary>
public sealed class T0Gate
{
    private const int MaxRecordedHits = 60;

    /// <summary>Rung directory names, in descending quality order.</summary>
    private static readonly string[] RungDirectories = ["v0", "v1", "v2"];

    /// <summary>
    /// The only file names the ladder route will serve. Anything else 404s, so no traversal is
    /// reachable regardless of what the caller sends.
    /// </summary>
    private static readonly Regex ServableFilePattern = new(@"^(?:master\.m3u8|v[0-2]/(?:index\.m3u8|seg_\d+\.ts))$", RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly List<string> _hits = [];
    private readonly Dictionary<string, int> _hitsByRemote = new(StringComparer.Ordinal);
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<T0Gate> _logger;

    private int _totalHits;
    private int _notFoundHits;

    /// <summary>
    /// Initializes a new instance of the <see cref="T0Gate"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Used to find the server's own ffmpeg binary.</param>
    /// <param name="configurationManager">Used for a writable directory, same derivation as <see cref="BroadcastManager"/>.</param>
    /// <param name="logger">Logger.</param>
    public T0Gate(IMediaEncoder mediaEncoder, IServerConfigurationManager configurationManager, ILogger<T0Gate> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        LadderDirectory = Path.Combine(configurationManager.ApplicationPaths.TempDirectory, "MovieNight", "t0");
    }

    /// <summary>
    /// Gets the directory holding the generated static ladder (master.m3u8 plus v0/v1/v2).
    /// </summary>
    public string LadderDirectory { get; }

    /// <summary>
    /// Gets or sets the base URL prefixed to the ladder path. Empty means a relative URL.
    /// <para>
    /// Runtime-settable (POST .../t0/source), and that is the whole point: the first gate run
    /// (v0.3.32.0, 2026-08-19) died on a RELATIVE url - Jellyfin handed "/MovieNight/stream/hls/
    /// master.m3u8" to ffmpeg as a FILE path, which exited 254 in 30ms, and no fetch of the ladder
    /// ever happened. A base URL must be absolute AND reachable by the client, or the measurement
    /// is meaningless. Loopback proves only that the server fetched it.
    /// </para>
    /// </summary>
    public string SourceBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the media source's declared container. Runtime-settable.
    /// <para>
    /// The first gate run returned "hls" and Jellyfin answered with
    /// <c>TranscodeReasons: ContainerNotSupported</c> - no device profile lists "hls" as a
    /// DIRECT-PLAY container (clients list it as a TRANSCODING target instead), so the profile
    /// matcher rejected direct play before <c>SupportsTranscoding=false</c> was ever consulted.
    /// This is the field most likely to decide the gate, hence a knob rather than a constant.
    /// </para>
    /// </summary>
    public string SourceContainer { get; set; } = "ts";

    /// <summary>Gets or sets a value indicating whether the media source declares SupportsDirectPlay. Runtime-settable.</summary>
    public bool SourceSupportsDirectPlay { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the media source declares SupportsDirectStream. Runtime-settable.</summary>
    public bool SourceSupportsDirectStream { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the media source declares SupportsTranscoding. Runtime-settable.</summary>
    public bool SourceSupportsTranscoding { get; set; }

    /// <summary>Gets or sets a value indicating whether the media source declares RequiresOpening. Runtime-settable.</summary>
    public bool SourceRequiresOpening { get; set; }

    /// <summary>
    /// Gets a value indicating whether the static ladder has been generated.
    /// </summary>
    public bool LadderExists => File.Exists(Path.Combine(LadderDirectory, "master.m3u8"));

    /// <summary>
    /// Gets the current media-source knob values, for <see cref="GetStatus"/> and the
    /// <c>t0/source</c> endpoint.
    /// </summary>
    /// <returns>The knobs as a flat object.</returns>
    public object GetSourceSettings() => new
    {
        SourceBaseUrl,
        SourceContainer,
        SourceSupportsDirectPlay,
        SourceSupportsDirectStream,
        SourceSupportsTranscoding,
        SourceRequiresOpening,
    };

    /// <summary>
    /// Generates the static ladder: three rungs of synthetic video and tone from one ffmpeg
    /// process, VOD playlists, one fixed closed GOP shared by every rung. Software x264 on
    /// purpose - the gate tests Jellyfin's plumbing, and QSV is a variable it does not need
    /// (proving the QSV ladder is T2's job).
    /// </summary>
    /// <param name="seconds">Ladder duration - long enough to tune, observe and measure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exit code, per-rung file counts, and ffmpeg's stderr tail.</returns>
    public async Task<object> BuildLadderAsync(int seconds, CancellationToken cancellationToken)
    {
        var duration = Math.Clamp(seconds, 30, 900);

        if (Directory.Exists(LadderDirectory))
        {
            Directory.Delete(LadderDirectory, recursive: true);
        }

        // %v expands to the rung index, but the HLS muxer will not create those subdirectories
        // itself - hence creating them up front.
        foreach (var rung in RungDirectories)
        {
            Directory.CreateDirectory(Path.Combine(LadderDirectory, rung));
        }

        var args = new[]
        {
            "-nostdin", "-hide_banner",
            "-f", "lavfi", "-i", FormattableString.Invariant($"testsrc2=size=1280x720:rate=24:duration={duration}"),
            "-f", "lavfi", "-i", FormattableString.Invariant($"sine=frequency=440:sample_rate=48000:duration={duration}"),
            "-filter_complex", "[0:v]split=3[s0][s1][s2];[s1]scale=854:480[r1];[s2]scale=640:360[r2]",
            "-map", "[s0]", "-map", "1:a", "-map", "[r1]", "-map", "1:a", "-map", "[r2]", "-map", "1:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-b:v:0", "1500k", "-b:v:1", "700k", "-b:v:2", "300k",
            "-c:a", "aac", "-b:a", "96k", "-ac", "2",

            // Fixed closed GOP, identical on every rung: 96 frames = 4.0s at 24fps, so segment
            // boundaries land on IDRs in all three and a client can switch rungs cleanly. Note
            // this is -g, NOT -force_key_frames - per CLAUDE.md that flag wedges h264_qsv
            // outright, and keeping one keyframe idiom across the codebase is how it stays out.
            "-g", "96", "-keyint_min", "96", "-sc_threshold", "0",

            "-f", "hls",
            "-var_stream_map", "v:0,a:0 v:1,a:1 v:2,a:2",
            "-hls_time", "4",
            "-hls_playlist_type", "vod",
            "-hls_segment_type", "mpegts",
            "-hls_flags", "independent_segments",
            "-master_pl_name", "master.m3u8",
            "-hls_segment_filename", Path.Combine(LadderDirectory, "v%v", "seg_%d.ts"),
            Path.Combine(LadderDirectory, "v%v", "index.m3u8"),
        };

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

        _logger.LogInformation("Movie Night T0: building static ladder ({Seconds}s) in {Directory}", duration, LadderDirectory);

        using var process = new Process { StartInfo = startInfo };
        var stderr = new List<string>();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stderr)
            {
                if (stderr.Count >= 200)
                {
                    stderr.RemoveAt(0);
                }

                stderr.Add(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var rungs = RungDirectories.Select(rung =>
        {
            var directory = Path.Combine(LadderDirectory, rung);
            var files = Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
            return (object)new
            {
                Rung = rung,
                Segments = files.Count(f => f.EndsWith(".ts", StringComparison.Ordinal)),
                Bytes = files.Sum(f => new FileInfo(f).Length),
                HasPlaylist = File.Exists(Path.Combine(directory, "index.m3u8")),
            };
        }).ToArray();

        _logger.LogInformation("Movie Night T0: ladder build finished, exit {ExitCode}, master present {Master}", process.ExitCode, LadderExists);

        return new
        {
            ExitCode = process.ExitCode,
            HasMasterPlaylist = LadderExists,
            Rungs = rungs,
            Stderr = string.Join(Environment.NewLine, stderr),
        };
    }

    /// <summary>
    /// Whether a requested path is one of the exact names the ladder produces. This whitelist is
    /// the whole guard on the anonymous ladder route - an exact-name match, not a traversal
    /// filter - so it is static and separately tested rather than buried inside an instance.
    /// </summary>
    /// <param name="requestPath">Path below <c>/MovieNight/stream/hls/</c>.</param>
    /// <returns>True if it may be served.</returns>
    public static bool IsServableName(string? requestPath)
        => !string.IsNullOrEmpty(requestPath) && ServableFilePattern.IsMatch(Normalize(requestPath));

    /// <summary>
    /// Resolves a requested ladder path to a real file, or null if it is not one of the exact
    /// names the ladder produces.
    /// </summary>
    /// <param name="requestPath">Path below <c>/MovieNight/stream/hls/</c>, e.g. <c>v1/seg_7.ts</c>.</param>
    /// <returns>The full path on disk, or null.</returns>
    public string? ResolveFile(string? requestPath)
    {
        if (!IsServableName(requestPath))
        {
            return null;
        }

        var normalized = Normalize(requestPath!);

#pragma warning disable CA3003 // ServableFilePattern is an exact-name whitelist (master.m3u8, v[0-2]/index.m3u8, v[0-2]/seg_<digits>.ts) - no traversal survives it
        var full = Path.GetFullPath(Path.Combine(LadderDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(full) ? full : null;
#pragma warning restore CA3003
    }

    /// <summary>
    /// Records a fetch of the ladder. This is the gate's actual instrument: if the only remote
    /// address that ever appears here is the server's own, Jellyfin fetched on the client's behalf
    /// and the hop is still there; if the viewer's own address appears, the client is pulling our
    /// playlists directly and the hop is gone.
    /// </summary>
    /// <param name="remoteAddress">Remote address of the fetch.</param>
    /// <param name="userAgent">User agent of the fetch - names the player when the address is ambiguous.</param>
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
            AddRecord(FormattableString.Invariant(
                $"{DateTime.UtcNow:HH:mm:ss} {remote} {(found ? "200" : "404")} {requestPath} [{userAgent}]"));
        }
    }

    /// <summary>
    /// Records that Jellyfin called into the tuner host. <c>GetChannelStream</c> appearing here is
    /// itself a gate-failure signal: it means Jellyfin opened a server-side live stream instead of
    /// handing the URL to the client.
    /// </summary>
    /// <param name="what">The tuner-host method and any relevant argument.</param>
    public void RecordTunerEvent(string what)
    {
        _logger.LogInformation("Movie Night T0: tuner host event - {Event}", what);
        lock (_lock)
        {
            AddRecord(FormattableString.Invariant($"{DateTime.UtcNow:HH:mm:ss} TUNER {what}"));
        }
    }

    /// <summary>Clears recorded hits, so one tune attempt can be measured without the previous one's noise.</summary>
    public void ResetHits()
    {
        lock (_lock)
        {
            _hits.Clear();
            _hitsByRemote.Clear();
            _totalHits = 0;
            _notFoundHits = 0;
        }
    }

    /// <summary>Snapshot of the gate's instrumentation.</summary>
    /// <returns>Ladder state plus who fetched what.</returns>
    public object GetStatus()
    {
        lock (_lock)
        {
            return new
            {
                LadderDirectory,
                LadderReady = LadderExists,
                Source = GetSourceSettings(),
                TotalHits = _totalHits,
                NotFoundHits = _notFoundHits,
                HitsByRemote = _hitsByRemote.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                Recent = _hits.ToArray(),
            };
        }
    }

    private static string Normalize(string requestPath) => requestPath.Replace('\\', '/').TrimStart('/');

    private void AddRecord(string line)
    {
        if (_hits.Count >= MaxRecordedHits)
        {
            _hits.RemoveAt(0);
        }

        _hits.Add(line);
    }
}

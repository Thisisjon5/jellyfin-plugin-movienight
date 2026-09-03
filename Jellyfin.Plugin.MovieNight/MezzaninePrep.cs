using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieNight;

// CA3003 (path injection): every path this class touches is either a Jellyfin library item's own
// Path (resolved by the server from an opaque Guid) or MezzanineDirectory + Guid.ToString("N") +
// a fixed extension. A Guid cannot carry traversal; the analyzer cannot see that.
#pragma warning disable CA3003

/// <summary>
/// Mezzanine preparation: a one-off, ahead-of-time transcode of a movie into the shape the live
/// pipeline wants - 720p h264 + AAC stereo 48 kHz in MP4, with the chosen subtitle track
/// <b>burned in</b>. Two jobs (ROADMAP-2026-09-03.md §3): captions, which cannot happen in the
/// live encoder because feed time diverges from movie time the moment a pause inserts slate; and
/// a cheap decode, so the live encoder is not chewing through a 40 Mbps remux. It no longer has
/// to produce ladder-aligned GOPs (every live rung is encoded).
/// <para>
/// One job at a time. Output lives under the plugin's data path, not the temp directory, so it
/// survives restarts: <c>{itemId}.mp4</c> plus a <c>{itemId}.json</c> sidecar describing it.
/// </para>
/// </summary>
public sealed class MezzaninePrep : IDisposable
{
    private static readonly string[] TextSubtitleCodecs = ["subrip", "srt", "ass", "ssa", "webvtt", "vtt", "mov_text", "text", "ttml"];
    private static readonly string[] BitmapSubtitleCodecs = ["pgssub", "hdmv_pgs_subtitle", "dvdsub", "dvd_subtitle", "dvbsub", "dvb_subtitle", "xsub"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<MezzaninePrep> _logger;

    private Process? _process;
    private StderrTailBuffer? _stderr;
    private PrepJob? _job;

    /// <summary>
    /// Initializes a new instance of the <see cref="MezzaninePrep"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Used to find the server's own ffmpeg binary.</param>
    /// <param name="libraryManager">Used to resolve item ids.</param>
    /// <param name="mediaSourceManager">Used to list an item's streams (subtitle tracks).</param>
    /// <param name="configurationManager">Used for the hardware-acceleration setting and a durable output directory.</param>
    /// <param name="logger">Logger.</param>
    public MezzaninePrep(
        IMediaEncoder mediaEncoder,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IServerConfigurationManager configurationManager,
        ILogger<MezzaninePrep> logger)
    {
        _mediaEncoder = mediaEncoder;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _configurationManager = configurationManager;
        _logger = logger;
        MezzanineDirectory = Path.Combine(configurationManager.ApplicationPaths.DataPath, "MovieNight", "mezzanine");
    }

    /// <summary>Gets the directory prepared files live in.</summary>
    public string MezzanineDirectory { get; }

    /// <summary>Gets a value indicating whether a prep job is running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _job is { State: "Running" };
            }
        }
    }

    /// <summary>
    /// Path of the prepared mezzanine for an item, or null if none has been prepared.
    /// </summary>
    /// <param name="itemId">Library item id.</param>
    /// <returns>The mp4 path, or null.</returns>
    public string? GetMezzaninePath(Guid itemId)
    {
        var path = OutputPath(itemId);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Lists an item's subtitle streams in the form the host page's dropdown needs.
    /// </summary>
    /// <param name="itemId">Library item id.</param>
    /// <returns>One row per subtitle stream, or null if the item does not exist.</returns>
    public IReadOnlyList<object>? ListSubtitleStreams(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        return _mediaSourceManager.GetMediaStreams(itemId)
            .Where(s => s.Type == MediaStreamType.Subtitle)
            .Select(s => (object)new
            {
                s.Index,
                s.Codec,
                s.Language,
                s.Title,
                s.IsExternal,
                s.IsDefault,
                s.IsForced,
                Kind = ClassifySubtitle(s.Codec),
                Display = s.DisplayTitle,
            })
            .ToList();
    }

    /// <summary>
    /// Starts preparing an item. Rejected if a job is already running.
    /// </summary>
    /// <param name="itemId">Library item id.</param>
    /// <param name="subtitleStreamIndex">Absolute stream index of the subtitle track to burn in, or null for none.</param>
    /// <returns>Success and a message.</returns>
    public (bool Success, string Message) Start(Guid itemId, int? subtitleStreamIndex)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            return (false, $"Item {itemId} not found or has no file on disk");
        }

        var streams = _mediaSourceManager.GetMediaStreams(itemId);
        MediaStream? subtitle = null;
        if (subtitleStreamIndex is int wanted)
        {
            subtitle = streams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == wanted);
            if (subtitle is null)
            {
                return (false, $"Item has no subtitle stream with index {wanted}");
            }

            if (ClassifySubtitle(subtitle.Codec) == "unsupported")
            {
                return (false, $"Subtitle codec '{subtitle.Codec}' cannot be burned in");
            }
        }

        var encodingOptions = _configurationManager.GetEncodingOptions();
        var accel = HardwareAccelMapper.Map(encodingOptions.HardwareAccelerationType);
        var subtitleSpec = subtitle is null ? null : DescribeSubtitle(item.Path, subtitle, streams);
        var partialPath = OutputPath(itemId) + ".partial.mp4";
        var args = BuildArgs(item.Path, partialPath, accel, encodingOptions.VaapiDevice, subtitleSpec);

        lock (_lock)
        {
            if (_job is { State: "Running" })
            {
                return (false, "A prepare job is already running");
            }

            Directory.CreateDirectory(MezzanineDirectory);
            TryDelete(partialPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = _mediaEncoder.EncoderPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            var job = new PrepJob
            {
                ItemId = itemId,
                ItemName = item.Name,
                RunTimeTicks = item.RunTimeTicks,
                SubtitleStreamIndex = subtitle?.Index,
                SubtitleTitle = subtitle?.DisplayTitle,
                Accel = accel.ToString(),
                State = "Running",
                StartedUtc = DateTime.UtcNow,
                PartialPath = partialPath,
            };

            try
            {
                var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                var stderr = new StderrTailBuffer();
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                    {
                        stderr.Add(e.Data);
                    }
                };
                process.OutputDataReceived += (_, e) => OnProgressLine(job, e.Data);
                process.Exited += (_, _) => OnExited(process, job, streams.Count);
                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                _process = process;
                _stderr = stderr;
                _job = job;
                _logger.LogInformation("Movie Night: prepare started for {Name} (pid {Pid}, subtitle {Subtitle}, accel {Accel})", item.Name, process.Id, subtitle?.DisplayTitle ?? "none", accel);
                return (true, FormattableString.Invariant($"preparing {item.Name}"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Movie Night: prepare failed to start");
                job.State = "Failed";
                job.Error = ex.Message;
                _job = job;
                return (false, ex.Message);
            }
        }
    }

    /// <summary>Cancels the running job, if any, discarding its partial output.</summary>
    public void Cancel()
    {
        Process? process;
        PrepJob? job;
        lock (_lock)
        {
            process = _process;
            job = _job;
            if (job is { State: "Running" })
            {
                job.State = "Cancelled";
            }
        }

        if (process is not null)
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
                // Already gone.
            }
        }

        if (job is not null)
        {
            TryDelete(job.PartialPath);
        }
    }

    /// <summary>Deletes a prepared mezzanine.</summary>
    /// <param name="itemId">Library item id.</param>
    /// <returns>True if something was deleted.</returns>
    public bool Delete(Guid itemId)
    {
        var path = OutputPath(itemId);
        var existed = File.Exists(path);
        TryDelete(path);
        TryDelete(SidecarPath(itemId));
        return existed;
    }

    /// <summary>Snapshot: the current/last job plus every prepared file on disk.</summary>
    /// <returns>Status object for the API.</returns>
    public object GetStatus()
    {
        PrepJob? job;
        string? stderr;
        lock (_lock)
        {
            job = _job;
            stderr = _stderr?.ToString();
        }

        var prepared = new List<object>();
        if (Directory.Exists(MezzanineDirectory))
        {
            foreach (var mp4 in Directory.EnumerateFiles(MezzanineDirectory, "*.mp4").Where(f => !f.EndsWith(".partial.mp4", StringComparison.Ordinal)))
            {
                var name = Path.GetFileNameWithoutExtension(mp4);
                object? meta = null;
                var sidecar = Path.ChangeExtension(mp4, ".json");
                if (File.Exists(sidecar))
                {
                    try
                    {
                        meta = JsonSerializer.Deserialize<PrepSidecar>(File.ReadAllText(sidecar));
                    }
                    catch (JsonException)
                    {
                        // Unreadable sidecar; the mp4 still counts.
                    }
                }

                prepared.Add(new { ItemId = name, Bytes = new FileInfo(mp4).Length, Meta = meta });
            }
        }

        return new
        {
            Job = job is null ? null : new
            {
                job.ItemId,
                job.ItemName,
                job.State,
                job.SubtitleStreamIndex,
                job.SubtitleTitle,
                job.Accel,
                job.StartedUtc,
                job.FinishedUtc,
                ProgressPercent = job.ProgressPercent,
                OutTimeSeconds = Math.Round(job.OutTimeSeconds, 1),
                job.Speed,
                job.Error,
                Stderr = job.State == "Running" ? null : stderr,
            },
            Prepared = prepared,
            MezzanineDirectory,
        };
    }

    /// <summary>
    /// Builds the prep ffmpeg argument list. Pure, unit-tested.
    /// </summary>
    /// <param name="inputPath">Source movie.</param>
    /// <param name="outputPath">Destination mp4 (the caller passes a partial name and renames on success).</param>
    /// <param name="accel">Encoder backend.</param>
    /// <param name="vaapiDevice">VA-API device for the VAAPI backend.</param>
    /// <param name="subtitle">Subtitle to burn in, or null.</param>
    /// <returns>The argument list.</returns>
    public static IReadOnlyList<string> BuildArgs(string inputPath, string outputPath, HardwareAccel accel, string? vaapiDevice, SubtitleBurnSpec? subtitle)
    {
        if (accel == HardwareAccel.Vaapi && string.IsNullOrEmpty(vaapiDevice))
        {
            accel = HardwareAccel.None;
        }

        var args = new List<string> { "-nostdin", "-hide_banner", "-nostats", "-y", "-progress", "pipe:1" };
        switch (accel)
        {
            case HardwareAccel.Qsv:
                args.AddRange(["-init_hw_device", "qsv=hw", "-filter_hw_device", "hw"]);
                break;
            case HardwareAccel.Vaapi:
                args.AddRange(["-init_hw_device", $"vaapi=hw:{vaapiDevice}", "-filter_hw_device", "hw"]);
                break;
        }

        args.AddRange(["-i", inputPath]);

        // Burn-in happens in software BEFORE any hardware upload/scale: the subtitles and overlay
        // filters need system-memory frames. Then the same scale chain the live ladder uses.
        var chain = new List<string>();
        var videoIn = "[0:v:0]";
        if (subtitle is not null)
        {
            if (subtitle.IsBitmap)
            {
                // Bitmap subs are a second input stream composited over the video.
                videoIn = FormattableString.Invariant($"[0:v:0][0:{subtitle.AbsoluteStreamIndex}]");
                chain.Add("overlay");
            }
            else
            {
                var filter = "subtitles=" + QuoteFilterPath(subtitle.SourcePath);
                if (subtitle.RelativeSubtitleIndex is int si)
                {
                    filter += FormattableString.Invariant($":si={si}");
                }

                chain.Add(filter);
            }
        }

        chain.Add(accel switch
        {
            HardwareAccel.Qsv => "format=nv12,hwupload=extra_hw_frames=64,scale_qsv=-1:720",
            HardwareAccel.Vaapi => "format=nv12,hwupload,scale_vaapi=-2:720",
            _ => "scale=-2:720,format=yuv420p",
        });

        args.AddRange(["-filter_complex", videoIn + string.Join(',', chain) + "[vout]"]);
        args.AddRange(["-map", "[vout]", "-map", "0:a:0?"]);

        switch (accel)
        {
            case HardwareAccel.Qsv:
                args.AddRange(["-c:v", "h264_qsv", "-preset", "medium", "-g", "96", "-forced_idr", "1"]);
                break;
            case HardwareAccel.Vaapi:
                args.AddRange(["-c:v", "h264_vaapi", "-g", "96"]);
                break;
            case HardwareAccel.Nvenc:
                args.AddRange(["-c:v", "h264_nvenc", "-preset", "p5", "-g", "96"]);
                break;
            case HardwareAccel.Amf:
                args.AddRange(["-c:v", "h264_amf", "-g", "96"]);
                break;
            default:
                args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-g", "96", "-sc_threshold", "0"]);
                break;
        }

        args.AddRange([
            "-profile:v", "high", "-level", "4.0",
            "-b:v", "6000k", "-maxrate", "6000k", "-bufsize", "12000k",
            "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-ar", "48000",
            "-sn",
            "-movflags", "+faststart",
            "-f", "mp4",
            outputPath,
        ]);

        return args;
    }

    /// <summary>
    /// Quotes a path for use as a filter option value. Inside the filter graph a value is quoted
    /// with single quotes, and within that the option parser still splits on <c>:</c> (which a
    /// Windows drive letter contains), so colons are backslash-escaped and backslashes become
    /// forward slashes. Matches the idiom Jellyfin's own subtitle burn-in uses.
    /// </summary>
    /// <param name="path">Filesystem path.</param>
    /// <returns>The quoted, escaped value.</returns>
    public static string QuoteFilterPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var escaped = path
            .Replace('\\', '/')
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return "'" + escaped + "'";
    }

    /// <summary>
    /// Classifies a subtitle codec for burn-in: <c>text</c>, <c>bitmap</c>, or <c>unsupported</c>.
    /// </summary>
    /// <param name="codec">ffprobe codec name.</param>
    /// <returns>The class.</returns>
    public static string ClassifySubtitle(string? codec)
    {
        if (string.IsNullOrEmpty(codec))
        {
            return "unsupported";
        }

        if (TextSubtitleCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase))
        {
            return "text";
        }

        return BitmapSubtitleCodecs.Contains(codec, StringComparer.OrdinalIgnoreCase) ? "bitmap" : "unsupported";
    }

    /// <summary>
    /// Turns Jellyfin's stream row into the burn-in spec: external text files are read directly;
    /// embedded text tracks are addressed by their index AMONG SUBTITLE STREAMS (the
    /// <c>subtitles</c> filter's <c>si</c>); bitmap tracks by absolute stream index for overlay.
    /// </summary>
    /// <param name="moviePath">The movie file.</param>
    /// <param name="subtitle">The chosen subtitle stream.</param>
    /// <param name="allStreams">Every stream of the item, for computing the relative index.</param>
    /// <returns>The spec.</returns>
    public static SubtitleBurnSpec DescribeSubtitle(string moviePath, MediaStream subtitle, IReadOnlyList<MediaStream> allStreams)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        ArgumentNullException.ThrowIfNull(allStreams);
        var isBitmap = ClassifySubtitle(subtitle.Codec) == "bitmap";
        if (subtitle.IsExternal && !string.IsNullOrEmpty(subtitle.Path))
        {
            return new SubtitleBurnSpec(subtitle.Path, null, subtitle.Index, false);
        }

        var relative = allStreams.Count(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && s.Index < subtitle.Index);
        return new SubtitleBurnSpec(moviePath, relative, subtitle.Index, isBitmap);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Cancel();
        _process?.Dispose();
        _process = null;
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private string OutputPath(Guid itemId) => Path.Combine(MezzanineDirectory, itemId.ToString("N") + ".mp4");

    private string SidecarPath(Guid itemId) => Path.Combine(MezzanineDirectory, itemId.ToString("N") + ".json");

    private void OnProgressLine(PrepJob job, string? line)
    {
        // -progress emits key=value lines; out_time_us (microseconds) is the one that matters.
        if (line is null)
        {
            return;
        }

        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
        {
            job.OutTimeSeconds = us / 1_000_000.0;
        }
        else if (line.StartsWith("speed=", StringComparison.Ordinal))
        {
            job.Speed = line[6..].Trim();
        }
    }

    private void OnExited(Process process, PrepJob job, int streamCount)
    {
        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        lock (_lock)
        {
            job.FinishedUtc = DateTime.UtcNow;
            if (job.State == "Cancelled")
            {
                TryDelete(job.PartialPath);
                return;
            }

            if (exitCode != 0)
            {
                job.State = "Failed";
                job.Error = FormattableString.Invariant($"ffmpeg exited {exitCode}");
                TryDelete(job.PartialPath);
                _logger.LogError("Movie Night: prepare for {Name} failed (exit {Code}). stderr: {Stderr}", job.ItemName, exitCode, _stderr?.ToString());
                return;
            }

            try
            {
                var finalPath = OutputPath(job.ItemId);
                TryDelete(finalPath);
                File.Move(job.PartialPath!, finalPath);
                var sidecar = new PrepSidecar
                {
                    ItemName = job.ItemName,
                    SubtitleStreamIndex = job.SubtitleStreamIndex,
                    SubtitleTitle = job.SubtitleTitle,
                    Accel = job.Accel,
                    CreatedUtc = DateTime.UtcNow,
                    SourceStreamCount = streamCount,
                };
                File.WriteAllText(SidecarPath(job.ItemId), JsonSerializer.Serialize(sidecar, JsonOptions));
                job.State = "Done";
                job.ProgressPercent = 100;
                _logger.LogInformation("Movie Night: prepare for {Name} done -> {Path}", job.ItemName, finalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                job.State = "Failed";
                job.Error = ex.Message;
                _logger.LogError(ex, "Movie Night: prepare for {Name} could not finalise its output", job.ItemName);
            }
        }
    }

    private sealed class PrepJob
    {
        private int _progressPercent;

        public Guid ItemId { get; init; }

        public string? ItemName { get; init; }

        public long? RunTimeTicks { get; init; }

        public int? SubtitleStreamIndex { get; init; }

        public string? SubtitleTitle { get; init; }

        public string? Accel { get; init; }

        public string State { get; set; } = "Idle";

        public DateTime StartedUtc { get; init; }

        public DateTime? FinishedUtc { get; set; }

        public string? PartialPath { get; init; }

        public double OutTimeSeconds { get; set; }

        public string? Speed { get; set; }

        public string? Error { get; set; }

        public int ProgressPercent
        {
            get => _progressPercent > 0 ? _progressPercent : RunTimeTicks is long ticks && ticks > 0
                ? (int)Math.Clamp(OutTimeSeconds / TimeSpan.FromTicks(ticks).TotalSeconds * 100, 0, 99)
                : 0;
            set => _progressPercent = value;
        }
    }
}

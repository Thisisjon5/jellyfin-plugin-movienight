using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// T0 GATE SPIKE (planning/DESIGN-abr-ladder.md §8, T0). A custom tuner host that advertises ONE
/// channel whose media source points straight at the static ladder <see cref="T0Gate"/> generated.
/// <para>
/// The load-bearing field is <c>SupportsTranscoding = false</c>: it is how we tell Jellyfin there
/// is no fallback hop to take. Whether Jellyfin honours that (hands the URL to the client) or
/// refuses playback outright is exactly what the gate answers - and it decides whether an O(1)
/// Live TV delivery path exists at all.
/// </para>
/// Delete this class once the gate is answered and the real tuner host lands (M4).
/// </summary>
public class T0GateTunerHost : ITunerHost
{
    /// <summary>Tuner host type key, also the type of the persisted <see cref="TunerHostInfo"/> entry.</summary>
    public const string TunerType = "movienight-t0";

    /// <summary>The single channel this host advertises.</summary>
    public const string GateChannelId = "movienight-t0-gate";

    private const string LiveTvConfigKey = "livetv";
    private const string LadderPath = "/MovieNight/stream/hls/master.m3u8";

    private readonly T0Gate _gate;
    private readonly IServerApplicationHost _appHost;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<T0GateTunerHost> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="T0GateTunerHost"/> class.
    /// </summary>
    /// <param name="gate">Owns the static ladder and the gate's instrumentation.</param>
    /// <param name="appHost">Used to build the loopback form of the ladder URL.</param>
    /// <param name="configurationManager">Used to find this host's persisted tuner entry id.</param>
    /// <param name="logger">Logger.</param>
    public T0GateTunerHost(
        T0Gate gate,
        IServerApplicationHost appHost,
        IServerConfigurationManager configurationManager,
        ILogger<T0GateTunerHost> logger)
    {
        _gate = gate;
        _appHost = appHost;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Movie Night T0 gate";

    /// <inheritdoc />
    public string Type => TunerType;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        // Only advertise the channel once the ladder actually exists - a tunable channel backed by
        // nothing produces a client-side failure that looks exactly like a gate failure.
        if (!_gate.LadderExists)
        {
            return Task.FromResult(new List<ChannelInfo>());
        }

        var channels = new List<ChannelInfo>
        {
            new()
            {
                Id = GateChannelId,
                Name = "Movie Night (T0 gate)",
                Number = "901",
                ChannelType = ChannelType.TV,
                TunerHostId = GetTunerHostId(),
            },
        };

        return Task.FromResult(channels);
    }

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        // A base URL of "loopback" is expanded here rather than being stored, so the knob can be
        // set without knowing the server's own port.
        var baseUrl = string.Equals(_gate.SourceBaseUrl, "loopback", StringComparison.OrdinalIgnoreCase)
            ? string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{_appHost.HttpPort}")
            : _gate.SourceBaseUrl.TrimEnd('/');
        var url = baseUrl + LadderPath;

        _gate.RecordTunerEvent(FormattableString.Invariant(
            $"GetChannelStreamMediaSources({channelId}) url={url} container={_gate.SourceContainer ?? "(null)"} subProtocol={_gate.SourceSubProtocol}"));

        var source = new MediaSourceInfo
        {
            Id = GateChannelId,
            Path = url,
            TranscodingUrl = url,
            Protocol = MediaProtocol.Http,
            // Container decides ffmpeg's INPUT demuxer on any server-side fallback path, and
            // Jellyfin's own M3U tuner leaves it null so ffmpeg auto-detects. Declaring "ts" made
            // it run "-f mpegts" against an HLS PLAYLIST (exit 187, 2026-08-19 Android round);
            // declaring "hls" makes the profile matcher refuse direct play. Null is the only value
            // that is not a lie to one consumer or the other.
            Container = _gate.SourceContainer,
            TranscodingContainer = "ts",
            TranscodingSubProtocol = string.Equals(_gate.SourceSubProtocol, "hls", StringComparison.Ordinal)
                ? MediaStreamProtocol.hls
                : MediaStreamProtocol.http,
            IsInfiniteStream = true,
            IsRemote = false,

            // The four fields the gate is actually about. SupportsTranscoding=false says "there is
            // no fallback hop to take"; UseMostCompatibleTranscodingProfile=true is what the failed
            // 2026-08-18 soak ran with, and is believed to be part of why every client got its own
            // ffmpeg.
            SupportsDirectPlay = _gate.SourceSupportsDirectPlay,
            SupportsDirectStream = _gate.SourceSupportsDirectStream,
            SupportsTranscoding = _gate.SourceSupportsTranscoding,
            UseMostCompatibleTranscodingProfile = false,

            // Nothing to open or close server-side: the client is supposed to fetch the playlist
            // itself. If Jellyfin ignores this and calls GetChannelStream anyway, that call is
            // recorded and is itself a gate-failure signal.
            RequiresOpening = _gate.SourceRequiresOpening,
            RequiresClosing = false,
            SupportsProbing = false,

            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Width = 1280,
                    Height = 720,
                    BitRate = 1_500_000,
                    IsInterlaced = false,
                    RealFrameRate = 24,
                },
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = 1,
                    Codec = "aac",
                    Channels = 2,
                    SampleRate = 48_000,
                    BitRate = 96_000,
                },
            ],
        };

        return Task.FromResult(new List<MediaSourceInfo> { source });
    }

    /// <inheritdoc />
    public Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // Reaching here means Jellyfin decided to open a server-side live stream despite
        // RequiresOpening=false - i.e. it intends to sit between the client and our ladder. That is
        // the gate failing, and it is worth failing loudly and recording rather than quietly
        // serving something that muddies the measurement.
        _gate.RecordTunerEvent(FormattableString.Invariant($"GetChannelStream({channelId}) CALLED - gate-failure signal"));
        _logger.LogWarning("Movie Night T0: Jellyfin called GetChannelStream for {ChannelId} - it wants a server-side live stream", channelId);
        throw new NotSupportedException("Movie Night T0 gate serves its ladder over HTTP only; a server-side live stream means the gate has failed.");
    }

    /// <inheritdoc />
    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
        => Task.FromResult(new List<TunerHostInfo>());

    private string? GetTunerHostId()
    {
        var options = (LiveTvOptions)_configurationManager.GetConfiguration(LiveTvConfigKey);
        return options.TunerHosts
            .FirstOrDefault(t => string.Equals(t.Type, TunerType, StringComparison.Ordinal))?.Id;
    }
}

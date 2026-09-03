using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MovieNight.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// The Movie Night tuner host: advertises the live channel while a broadcast is live, and hands
/// each client a media source whose URL is our own ladder - so the client pulls playlists and
/// segments itself and no per-client ffmpeg exists (the T0 gate result, DECISIONS 2026-08-19).
/// <para>
/// Two things the gate taught that this class is built around: <c>Container</c> must be
/// <c>"hls"</c> (it feeds both the client's profile matcher and the demuxer of any server-side
/// fallback, and "hls" is the one value both accept - CLAUDE.md), and the URL must be absolute
/// and reachable by THAT client - so it is derived per request from the address the client used
/// to reach Jellyfin, with the client's own token appended so the authed segment route lets it in.
/// </para>
/// </summary>
public class MovieNightTunerHost : ITunerHost
{
    /// <summary>Tuner host type key, also the type of the persisted <see cref="TunerHostInfo"/> entry.</summary>
    public const string TunerType = "movienight";

    /// <summary>
    /// The channel id. Matches the <c>tvg-id</c> / XMLTV channel id the EPG endpoint has always
    /// emitted, so the existing listings provider supplies its guide block.
    /// </summary>
    public const string ChannelId = "movienight";

    private const string LiveTvConfigKey = "livetv";
    private const string LadderPath = "/MovieNight/stream/hls/master.m3u8";
    private const string AnonymousLadderPath = "/MovieNight/stream/hls-open/master.m3u8";
    private const string TokenClaim = "Jellyfin-Token";

    private readonly LiveSession _session;
    private readonly LiveSourceKnobs _knobs;
    private readonly IServerApplicationHost _appHost;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<MovieNightTunerHost> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieNightTunerHost"/> class.
    /// </summary>
    /// <param name="session">The live broadcast.</param>
    /// <param name="knobs">Runtime overrides for the media-source fields.</param>
    /// <param name="appHost">Fallback URL derivation and the loopback port.</param>
    /// <param name="configurationManager">Used to find this host's persisted tuner entry id.</param>
    /// <param name="httpContextAccessor">The request that is asking for media sources - the client's address and token come from it.</param>
    /// <param name="logger">Logger.</param>
    public MovieNightTunerHost(
        LiveSession session,
        LiveSourceKnobs knobs,
        IServerApplicationHost appHost,
        IServerConfigurationManager configurationManager,
        IHttpContextAccessor httpContextAccessor,
        ILogger<MovieNightTunerHost> logger)
    {
        _session = session;
        _knobs = knobs;
        _appHost = appHost;
        _configurationManager = configurationManager;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Movie Night";

    /// <inheritdoc />
    public string Type => TunerType;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public Task<List<ChannelInfo>> GetChannels(bool enableCache, CancellationToken cancellationToken)
    {
        // Listed only while live: a tunable channel backed by nothing is a client-side error that
        // looks like a broken plugin. LiveSession forces a guide refresh on every state change.
        if (!_session.IsLive)
        {
            return Task.FromResult(new List<ChannelInfo>());
        }

        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var channels = new List<ChannelInfo>
        {
            new()
            {
                Id = ChannelId,
                Name = _session.ChannelName,
                Number = string.IsNullOrWhiteSpace(config.ChannelNumber) ? "900" : config.ChannelNumber,
                ChannelType = ChannelType.TV,
                TunerHostId = GetTunerHostId(),
            },
        };

        return Task.FromResult(channels);
    }

    /// <inheritdoc />
    public Task<List<MediaSourceInfo>> GetChannelStreamMediaSources(string channelId, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var topKbps = Math.Clamp(config.TopRungBitrateKbps, 500, 20000);
        var (url, how) = BuildLadderUrl(config);
        _logger.LogInformation(
            "Movie Night: media source requested for {Channel} - ladder URL via {How}: {Url} (container={Container} directPlay={DirectPlay} directStream={DirectStream} transcoding={Transcoding})",
            channelId,
            how,
            RedactToken(url),
            _knobs.Container ?? "(null)",
            _knobs.SupportsDirectPlay,
            _knobs.SupportsDirectStream,
            _knobs.SupportsTranscoding);

        var source = new MediaSourceInfo
        {
            Id = ChannelId,
            Path = url,
            TranscodingUrl = url,
            Protocol = MediaProtocol.Http,
            Container = _knobs.Container,
            TranscodingContainer = "ts",
            TranscodingSubProtocol = MediaStreamProtocol.hls,
            IsInfiniteStream = true,
            IsRemote = false,
            Bitrate = topKbps * 1000,

            SupportsDirectPlay = _knobs.SupportsDirectPlay,
            SupportsDirectStream = _knobs.SupportsDirectStream,

            // Default true so a client whose profile refuses the ladder (Firefox, measured) gets
            // Jellyfin's server-side hop instead of an error. The gate established that this flag
            // is NOT what decides direct play for the clients that can - the profile match happens
            // first. Knob-settable because T0 passed with false and nothing has re-measured.
            SupportsTranscoding = _knobs.SupportsTranscoding,
            UseMostCompatibleTranscodingProfile = false,

            // Nothing to open server-side: the client fetches the playlist itself.
            RequiresOpening = false,
            RequiresClosing = false,
            SupportsProbing = false,

            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                    Codec = "h264",
                    Profile = "High",
                    Level = 40,
                    Width = 1280,
                    Height = 720,
                    BitRate = topKbps * 1000,
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
                    BitRate = 128_000,
                },
            ],
        };

        return Task.FromResult(new List<MediaSourceInfo> { source });
    }

    /// <inheritdoc />
    public Task<ILiveStream> GetChannelStream(string channelId, string streamId, IList<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // RequiresOpening=false is honoured (T0); reaching here would mean Jellyfin wants to sit
        // between the client and the ladder, which is the O(N) path this design exists to avoid.
        _logger.LogWarning("Movie Night: Jellyfin called GetChannelStream for {ChannelId} - it wants a server-side live stream, which this tuner does not provide", channelId);
        throw new NotSupportedException("Movie Night serves its ladder over HTTP only.");
    }

    /// <inheritdoc />
    public Task<List<TunerHostInfo>> DiscoverDevices(int discoveryDurationMs, CancellationToken cancellationToken)
        => Task.FromResult(new List<TunerHostInfo>());

    /// <summary>
    /// Builds the ladder URL for the client behind the current request. Order: the explicit
    /// override from settings; the scheme/host/path-base the client itself used to reach Jellyfin
    /// (what a reverse proxy or port forward presents, and what the client can therefore reach
    /// again); Jellyfin's own <c>GetSmartApiUrl</c>; and finally loopback, which only the server
    /// can use. The client's token rides along as <c>api_key</c> so the authed route accepts it -
    /// and <see cref="LadderFiles.AppendQueryToUris"/> propagates it into every playlist line.
    /// </summary>
    /// <param name="config">Plugin configuration.</param>
    /// <returns>The URL and a short description of how it was derived (for the log).</returns>
    internal (string Url, string How) BuildLadderUrl(PluginConfiguration config)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var request = httpContext?.Request;

        string baseUrl;
        string how;
        if (!string.IsNullOrWhiteSpace(_knobs.BaseUrlOverride))
        {
            baseUrl = _knobs.BaseUrlOverride.Trim().TrimEnd('/');
            how = "debug knob BaseUrlOverride";
        }
        else if (!string.IsNullOrWhiteSpace(config.PublicBaseUrl))
        {
            baseUrl = config.PublicBaseUrl.Trim().TrimEnd('/');
            how = "PublicBaseUrl setting";
        }
        else if (request is not null && request.Host.HasValue)
        {
            baseUrl = string.Create(CultureInfo.InvariantCulture, $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}").TrimEnd('/');
            how = "request Host header";
        }
        else if (request is not null)
        {
            baseUrl = _appHost.GetSmartApiUrl(request).TrimEnd('/');
            how = "GetSmartApiUrl";
        }
        else
        {
            baseUrl = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{_appHost.HttpPort}");
            how = "loopback (no request context)";
        }

        if (_knobs.UseAnonymousRoute)
        {
            return (baseUrl + AnonymousLadderPath, how + ", ANONYMOUS route (debug knob)");
        }

        var url = baseUrl + LadderPath;
        var token = httpContext?.User?.FindFirst(TokenClaim)?.Value;
        if (!string.IsNullOrEmpty(token))
        {
            url += "?api_key=" + Uri.EscapeDataString(token);
        }
        else
        {
            how += ", NO TOKEN (segment route will refuse the client)";
        }

        return (url, how);
    }

    private static string RedactToken(string url)
    {
        var idx = url.IndexOf("api_key=", StringComparison.Ordinal);
        return idx < 0 ? url : url[..(idx + 8)] + "…";
    }

    private string? GetTunerHostId()
    {
        var options = (LiveTvOptions)_configurationManager.GetConfiguration(LiveTvConfigKey);
        return options.TunerHosts
            .FirstOrDefault(t => string.Equals(t.Type, TunerType, StringComparison.Ordinal))?.Id;
    }
}

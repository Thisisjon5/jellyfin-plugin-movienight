using System;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieNight.Api;

/// <summary>
/// Serves the Movie Night HLS stream, M3U playlist, and stub EPG to Jellyfin's own Live TV tuner.
/// All endpoints are anonymous but loopback-only: media never leaves the server unauthenticated,
/// clients only ever reach it through Jellyfin's authed Live TV path.
/// </summary>
[ApiController]
[Route("MovieNight")]
[AllowAnonymous]
public class MovieNightController : ControllerBase
{
    private const string ChannelName = "Movie Night";
    private const string MasterPlaylistFileName = "master.m3u8";

    // Embedded resources (Resources/Hls/*, see the .csproj LogicalName entries) rather than loose
    // files shipped alongside the DLL: on the target NAS, extracted plugin files appeared in
    // directory listings but genuinely couldn't be opened (File.Exists returned false, matching a
    // separate host-side ENOENT on the identical path) - a filesystem quirk with zip extraction in
    // that environment, not our code. Embedding sidesteps it entirely.
    private const string EmbeddedResourcePrefix = "Jellyfin.Plugin.MovieNight.Hls.";

    // Phase 1 spike: matches only the hand-made static test HLS asset embedded in the DLL.
    private static readonly Regex SegmentFileNamePattern = new(@"^segment_\d{3}\.ts$", RegexOptions.Compiled);

    private readonly IServerApplicationHost _appHost;
    private readonly ILogger<MovieNightController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieNightController"/> class.
    /// </summary>
    /// <param name="appHost">Used to build loopback URLs against the server's own HTTP port.</param>
    /// <param name="logger">Logger.</param>
    public MovieNightController(IServerApplicationHost appHost, ILogger<MovieNightController> logger)
    {
        _appHost = appHost;
        _logger = logger;
    }

    /// <summary>
    /// One-line M3U pointing at the master playlist. Consumed by Jellyfin's M3U tuner host.
    /// </summary>
    /// <returns>The M3U playlist text, or 403 if the caller isn't loopback.</returns>
    [HttpGet("playlist.m3u")]
    public IActionResult GetPlaylist()
    {
        if (!IsLoopbackRequest())
        {
            return Forbid();
        }

        var streamUrl = $"http://127.0.0.1:{_appHost.HttpPort}/MovieNight/stream/{MasterPlaylistFileName}";
        var m3u = $"#EXTM3U\n#EXTINF:-1 tvg-id=\"movienight\" tvg-name=\"{ChannelName}\",{ChannelName}\n{streamUrl}\n";
        return Content(m3u, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// Serves the HLS master playlist and its segments (hand-made static test asset for the
    /// Phase 1 spike, embedded in the DLL) from a single route - <c>master.m3u8</c> and
    /// <c>segment_NNN.ts</c> are both matched here, dispatched internally, so there's no route
    /// ambiguity between a literal and a parameterized template for the same URL shape.
    /// </summary>
    /// <param name="fileName">Either <c>master.m3u8</c> or a segment file name like <c>segment_000.ts</c>.</param>
    /// <returns>The requested file, 404 if missing/invalid, or 403 if the caller isn't loopback.</returns>
    [HttpGet("stream/{fileName}")]
    public IActionResult GetStreamFile([FromRoute] string fileName)
    {
        if (!IsLoopbackRequest())
        {
            return Forbid();
        }

        var safeName = System.IO.Path.GetFileName(fileName);
        var isMasterPlaylist = string.Equals(safeName, MasterPlaylistFileName, StringComparison.Ordinal);
        if (!isMasterPlaylist && !SegmentFileNamePattern.IsMatch(safeName))
        {
            return NotFound();
        }

        // safeName is provably restricted to "master.m3u8" or ^segment_\d{3}\.ts$ at this point, so
        // it's safe to use directly as the embedded resource name suffix - no path traversal is
        // possible since this never touches the filesystem.
        var resourceName = EmbeddedResourcePrefix + safeName;
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.LogWarning("Movie Night: embedded resource {ResourceName} not found", resourceName);
            return NotFound();
        }

        var contentType = isMasterPlaylist ? "application/vnd.apple.mpegurl" : "video/mp2t";
        return File(stream, contentType);
    }

    /// <summary>
    /// Stub XMLTV guide data: one channel, one programme block spanning "now" for a fixed duration.
    /// </summary>
    /// <returns>The XMLTV document, or 403 if the caller isn't loopback.</returns>
    [HttpGet("epg.xml")]
    public IActionResult GetEpg()
    {
        if (!IsLoopbackRequest())
        {
            return Forbid();
        }

        var start = DateTime.UtcNow;
        var stop = start.AddMinutes(15);
        const string Format = "yyyyMMddHHmmss +0000";

        var xml = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <tv>
              <channel id="movienight">
                <display-name>{ChannelName}</display-name>
              </channel>
              <programme start="{start.ToString(Format, CultureInfo.InvariantCulture)}" stop="{stop.ToString(Format, CultureInfo.InvariantCulture)}" channel="movienight">
                <title>{ChannelName}</title>
              </programme>
            </tv>
            """;

        return Content(xml, "application/xml");
    }

    private bool IsLoopbackRequest()
    {
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        return remoteIp is not null && IPAddress.IsLoopback(remoteIp);
    }
}

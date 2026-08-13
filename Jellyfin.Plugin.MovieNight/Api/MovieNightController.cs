using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    // Phase 1 spike: matches only the hand-made static test HLS asset shipped alongside the DLL.
    private static readonly Regex SegmentFileNamePattern = new(@"^segment_\d{3}\.ts$", RegexOptions.Compiled);

    private readonly IServerApplicationHost _appHost;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieNightController"/> class.
    /// </summary>
    /// <param name="appHost">Used to build loopback URLs against the server's own HTTP port.</param>
    public MovieNightController(IServerApplicationHost appHost)
    {
        _appHost = appHost;
    }

    private static string HlsDirectory =>
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty, "hls");

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

        var streamUrl = $"http://127.0.0.1:{_appHost.HttpPort}/MovieNight/stream/master.m3u8";
        var m3u = $"#EXTM3U\n#EXTINF:-1 tvg-id=\"movienight\" tvg-name=\"{ChannelName}\",{ChannelName}\n{streamUrl}\n";
        return Content(m3u, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// The HLS master playlist (hand-made static test asset for the Phase 1 spike).
    /// </summary>
    /// <returns>The master playlist file, 404 if missing, or 403 if the caller isn't loopback.</returns>
    [HttpGet("stream/master.m3u8")]
    public IActionResult GetMasterPlaylist()
    {
        if (!IsLoopbackRequest())
        {
            return Forbid();
        }

        var path = Path.Combine(HlsDirectory, "master.m3u8");
        if (!System.IO.File.Exists(path))
        {
            return NotFound();
        }

        return PhysicalFile(path, "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// An individual HLS segment (hand-made static test asset for the Phase 1 spike).
    /// </summary>
    /// <param name="fileName">The segment file name, e.g. <c>segment_000.ts</c>.</param>
    /// <returns>The segment file, 404 if missing/invalid, or 403 if the caller isn't loopback.</returns>
    [HttpGet("stream/{fileName}")]
    public IActionResult GetSegment([FromRoute] string fileName)
    {
        if (!IsLoopbackRequest())
        {
            return Forbid();
        }

        // Belt-and-suspenders against path traversal: allow-list the name pattern, then re-derive
        // the path from Path.GetFileName (strips any directory component) and verify the resolved
        // full path still lands inside HlsDirectory before touching the filesystem.
        var safeName = Path.GetFileName(fileName);
        if (!SegmentFileNamePattern.IsMatch(safeName))
        {
            return NotFound();
        }

        var hlsDirectoryFull = Path.GetFullPath(HlsDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(hlsDirectoryFull, safeName));
        if (!fullPath.StartsWith(hlsDirectoryFull + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return NotFound();
        }

        // CA3003 flags this as tainted despite the regex full-match + prefix checks above: the
        // analyzer's dataflow doesn't model an anchored Regex.IsMatch as sanitization. safeName is
        // provably restricted to ^segment_\d{3}\.ts$ and fullPath is provably inside HlsDirectory.
#pragma warning disable CA3003
        if (!System.IO.File.Exists(fullPath))
        {
            return NotFound();
        }
#pragma warning restore CA3003

        return PhysicalFile(fullPath, "video/mp2t");
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

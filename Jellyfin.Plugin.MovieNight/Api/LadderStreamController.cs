using System;
using System.IO;
using System.Threading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieNight.Api;

/// <summary>
/// Serves the live ladder - <c>master.m3u8</c>, per-rung <c>v{i}/index.m3u8</c>, and segments -
/// to clients directly. This is the one route on the plugin that clients (not just the server)
/// call, so it is authed with Jellyfin's own <c>[Authorize]</c> (design §4(F), milestone M3),
/// which accepts the <c>api_key</c> the tuner host put in the URL. Playlists are rewritten on the
/// way out so that key reaches every relative URI in them.
/// <para>
/// Its own controller because the main one is <c>[AllowAnonymous]</c> at class level, which
/// cannot be overridden per action. A single catch-all action dispatches internally (CLAUDE.md:
/// never a literal route plus a parameterized one for the same URL shape).
/// </para>
/// </summary>
[ApiController]
[Route("MovieNight/stream/hls")]
[Authorize]
public class LadderStreamController : ControllerBase
{
    private readonly LadderEncoder _encoder;
    private readonly ILogger<LadderStreamController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LadderStreamController"/> class.
    /// </summary>
    /// <param name="encoder">Owns the ladder directory and the fetch instrumentation.</param>
    /// <param name="logger">Logger.</param>
    public LadderStreamController(LadderEncoder encoder, ILogger<LadderStreamController> logger)
    {
        _encoder = encoder;
        _logger = logger;
    }

    /// <summary>
    /// Serves one ladder file.
    /// </summary>
    /// <param name="path">Path below <c>stream/hls/</c>: <c>master.m3u8</c>, <c>v0/index.m3u8</c>, <c>v0/seg_12.ts</c>.</param>
    /// <returns>The file, or 404 for anything that is not an exact ladder file name.</returns>
    [HttpGet("{**path}")]
    public IActionResult Get([FromRoute] string path)
    {
        var fullPath = _encoder.ResolveFile(path);
        _encoder.RecordHit(
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            path,
            fullPath is not null);

        if (fullPath is null)
        {
            return NotFound();
        }

        if (!path.EndsWith(".m3u8", StringComparison.Ordinal))
        {
            return PhysicalFile(fullPath, "video/mp2t");
        }

        var text = ReadPlaylist(fullPath);
        if (text is null)
        {
            return NotFound();
        }

        // Carry whichever credential form authorised this request onto every URI line.
        string? query = null;
        if (Request.Query.TryGetValue("api_key", out var apiKey) && !string.IsNullOrEmpty(apiKey))
        {
            query = "api_key=" + Uri.EscapeDataString(apiKey!);
        }
        else if (Request.Query.TryGetValue("ApiKey", out var apiKey2) && !string.IsNullOrEmpty(apiKey2))
        {
            query = "ApiKey=" + Uri.EscapeDataString(apiKey2!);
        }

        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Content(LadderFiles.AppendQueryToUris(text, query), "application/vnd.apple.mpegurl");
    }

    /// <summary>
    /// Reads a playlist ffmpeg may be rewriting right now (it writes a temp file and renames, so
    /// a read normally sees a whole file - but Windows can still refuse the open mid-rename).
    /// </summary>
    private string? ReadPlaylist(string fullPath)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException ex) when (attempt < 2)
            {
                _logger.LogDebug(ex, "Movie Night: playlist read retry {Attempt} for {Path}", attempt + 1, fullPath);
                Thread.Sleep(20);
            }
            catch (IOException)
            {
                return null;
            }
        }

        return null;
    }
}

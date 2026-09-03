using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// Pure helpers for serving the ladder directory: the exact-name whitelist that guards the
/// segment route, and the playlist rewrite that carries the client's credential onto every
/// relative URI.
/// </summary>
public static class LadderFiles
{
    /// <summary>
    /// The only names the ladder route will serve. An exact-name match rather than a traversal
    /// filter, so nothing the caller sends can reach outside the ladder directory.
    /// </summary>
    private static readonly Regex ServableFilePattern = new(@"^(?:master\.m3u8|v\d/(?:index\.m3u8|seg_\d+\.ts))$", RegexOptions.Compiled);

    /// <summary>
    /// Whether a requested path is one of the exact names the ladder encoder produces.
    /// </summary>
    /// <param name="requestPath">Path below <c>/MovieNight/stream/hls/</c>.</param>
    /// <returns>True if it may be served.</returns>
    public static bool IsServableName(string? requestPath)
        => !string.IsNullOrEmpty(requestPath) && ServableFilePattern.IsMatch(Normalize(requestPath));

    /// <summary>
    /// Resolves a requested ladder path to a file below <paramref name="ladderDirectory"/>, or
    /// null if it is not a servable name or does not exist yet.
    /// </summary>
    /// <param name="ladderDirectory">The ladder root.</param>
    /// <param name="requestPath">Path below <c>/MovieNight/stream/hls/</c>, e.g. <c>v1/seg_7.ts</c>.</param>
    /// <returns>The full path on disk, or null.</returns>
    public static string? ResolveFile(string ladderDirectory, string? requestPath)
    {
        if (!IsServableName(requestPath))
        {
            return null;
        }

        var normalized = Normalize(requestPath!);
#pragma warning disable CA3003 // ServableFilePattern is an exact-name whitelist - no traversal survives it
        var full = Path.GetFullPath(Path.Combine(ladderDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(full) ? full : null;
#pragma warning restore CA3003
    }

    /// <summary>
    /// Appends a query string to every URI line of an HLS playlist. Players resolve the relative
    /// URIs in a playlist against the playlist's own URL and DROP its query string, so the
    /// <c>api_key</c> that authorised <c>master.m3u8</c> would never reach <c>v0/index.m3u8</c>
    /// or a single segment - every one of them would 401. Jellyfin's own transcoding playlists
    /// carry the key on each line for the same reason.
    /// </summary>
    /// <param name="playlist">Playlist text as ffmpeg wrote it.</param>
    /// <param name="query">Query to append, without a leading <c>?</c>. Null or empty returns the text unchanged.</param>
    /// <returns>The rewritten playlist.</returns>
    public static string AppendQueryToUris(string playlist, string? query)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        if (string.IsNullOrEmpty(query))
        {
            return playlist;
        }

        var builder = new StringBuilder(playlist.Length + 256);
        foreach (var rawLine in playlist.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length > 0 && line[0] != '#')
            {
                builder.Append(line).Append(line.Contains('?', StringComparison.Ordinal) ? '&' : '?').Append(query).Append('\n');
            }
            else
            {
                builder.Append(line).Append('\n');
            }
        }

        // Split() leaves one empty trailing entry for a newline-terminated file; don't double it.
        var result = builder.ToString();
        return playlist.EndsWith('\n') && result.EndsWith("\n\n", StringComparison.Ordinal) ? result[..^1] : result;
    }

    private static string Normalize(string requestPath) => requestPath.Replace('\\', '/').TrimStart('/');
}

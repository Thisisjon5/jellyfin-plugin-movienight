using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MovieNight.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the name of the live channel as it appears in Live TV.
    /// </summary>
    public string ChannelName { get; set; } = "Movie Night";

    /// <summary>
    /// Gets or sets the channel number the live channel is listed under.
    /// </summary>
    public string ChannelNumber { get; set; } = "900";

    /// <summary>
    /// Gets or sets how many rungs the live ladder encodes (1-3). Ruled a setting by Jon,
    /// 2026-09-03 (ROADMAP-2026-09-03.md §2): one rung on the NAS, more on a box that can afford
    /// them. Every rung is a live encode, so N rungs cost N encodes. Read at Go Live.
    /// </summary>
    public int LadderRungs { get; set; } = 1;

    /// <summary>
    /// Gets or sets the top (720p) rung's video bitrate in kbps. Egress is O(viewers) with no CDN,
    /// so size this to the host's upload bandwidth divided by the expected viewer count.
    /// </summary>
    public int TopRungBitrateKbps { get; set; } = 4000;

    /// <summary>
    /// Gets or sets the HLS segment length in seconds (2-6, default 4). Shorter segments cut the
    /// pause-to-screen latency (clients buffer a few segments) at the cost of more requests and
    /// slightly less efficient encoding. Read at Go Live.
    /// </summary>
    public int SegmentSeconds { get; set; } = 4;

    /// <summary>
    /// Gets or sets an explicit base URL (e.g. <c>https://jellyfin.example.com</c>) that clients
    /// use to fetch the ladder. Empty means derive it per request from the address the client
    /// itself used to reach Jellyfin (<c>GetSmartApiUrl</c>) - the right answer for almost every
    /// setup; this override exists for a reverse proxy that rewrites hosts in a way the server
    /// cannot see.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the "Movie Night (raw TS spike)" test channel is
    /// listed in the tuner - off by default so the channel list only shows what's actually in use.
    /// </summary>
    public bool EnableRawTsSpikeChannel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the "Movie Night (switcher spike)" test channel is
    /// listed in the tuner - off by default so the channel list only shows what's actually in use.
    /// </summary>
    public bool EnableSwitcherSpikeChannel { get; set; }
}

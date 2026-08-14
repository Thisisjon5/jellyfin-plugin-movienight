using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MovieNight.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
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

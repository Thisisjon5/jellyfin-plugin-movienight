using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// Maps Jellyfin's configured hardware-acceleration type onto the backends this plugin has an
/// encoder recipe for. Anything else (v4l2m2m, videotoolbox, rkmpp) falls back to software.
/// </summary>
public static class HardwareAccelMapper
{
    /// <summary>
    /// Maps the server setting to a plugin backend.
    /// </summary>
    /// <param name="type">Jellyfin's setting.</param>
    /// <returns>The backend.</returns>
    public static HardwareAccel Map(HardwareAccelerationType type) => type switch
    {
        HardwareAccelerationType.qsv => HardwareAccel.Qsv,
        HardwareAccelerationType.vaapi => HardwareAccel.Vaapi,
        HardwareAccelerationType.nvenc => HardwareAccel.Nvenc,
        HardwareAccelerationType.amf => HardwareAccel.Amf,
        _ => HardwareAccel.None,
    };
}

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// Runtime-settable overrides for the media source the tuner host hands out, plus the anonymous
/// ladder route switch. In-memory only (reset on restart), set via
/// <c>POST /MovieNight/api/debug/live/source</c>.
/// <para>
/// This exists because of the T0 gate (v0.3.32 → v0.3.34): the first run failed on two field
/// values at once and could not tell them apart, and each hypothesis cost a ~4 minute NAS
/// restart until the fields became knobs. The 0.4 defaults are the values that passed; the knobs
/// are for when a new client, or the new <c>api_key</c> on the URL, says otherwise.
/// </para>
/// </summary>
public sealed class LiveSourceKnobs
{
    /// <summary>Gets or sets the declared container. "hls" is the value that passed T0 for Roku, Xbox and Chrome.</summary>
    public string? Container { get; set; } = "hls";

    /// <summary>Gets or sets a value indicating whether the source declares SupportsDirectPlay.</summary>
    public bool SupportsDirectPlay { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the source declares SupportsDirectStream.</summary>
    public bool SupportsDirectStream { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the source declares SupportsTranscoding. True lets a
    /// client whose profile refuses the ladder (Firefox) take Jellyfin's server-side hop instead of
    /// erroring; T0 passed with false.
    /// </summary>
    public bool SupportsTranscoding { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the tuner host should point clients at the
    /// ANONYMOUS ladder route (<c>stream/hls-open/</c>) with no token, instead of the authed one.
    /// Off by default; flipping it on also opens that route. A/B instrument for "is it the auth
    /// that breaks this client?" - never leave it on.
    /// </summary>
    public bool UseAnonymousRoute { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the QSV encoder should use the T2-era hardware
    /// scale chain (<c>hwupload</c> + <c>scale_qsv</c>) instead of software scale/pad. Off by
    /// default since 0.4.1: that chain died at the first slate seam. Read at Go Live. A/B only.
    /// </summary>
    public bool HardwareScale { get; set; }

    /// <summary>
    /// Gets or sets an absolute base URL that overrides both the setting and the per-request
    /// derivation, e.g. <c>http://192.168.68.118:8096</c>. Null means normal derivation.
    /// </summary>
    public string? BaseUrlOverride { get; set; }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// Registers the Movie Night M3U tuner and XMLTV listings provider once the server is actually
/// ready to serve requests. Idempotent: reuses the existing entry (matched by URL) instead of
/// duplicating it.
/// </summary>
public class TunerRegistrar : IHostedService
{
    private const string ChannelName = "Movie Night";
    private const string LiveTvConfigKey = "livetv";
    private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);

    private readonly ITunerHostManager _tunerHostManager;
    private readonly IListingsManager _listingsManager;
    private readonly IServerApplicationHost _appHost;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TunerRegistrar> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TunerRegistrar"/> class.
    /// </summary>
    /// <param name="tunerHostManager">Used to persist the M3U tuner host entry.</param>
    /// <param name="listingsManager">Used to persist the XMLTV listings provider entry.</param>
    /// <param name="appHost">Used to build loopback URLs and check core-startup readiness.</param>
    /// <param name="configurationManager">Used to read the current Live TV config for idempotency checks.</param>
    /// <param name="lifetime">Used to defer registration until the host has started.</param>
    /// <param name="logger">Logger.</param>
    public TunerRegistrar(
        ITunerHostManager tunerHostManager,
        IListingsManager listingsManager,
        IServerApplicationHost appHost,
        IServerConfigurationManager configurationManager,
        IHostApplicationLifetime lifetime,
        ILogger<TunerRegistrar> logger)
    {
        _tunerHostManager = tunerHostManager;
        _listingsManager = listingsManager;
        _appHost = appHost;
        _configurationManager = configurationManager;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // SaveTunerHost validates the M3U by fetching it over loopback HTTP. StartAsync runs during
        // host startup, before Kestrel is listening, so calling it here gets "Connection refused".
        // ApplicationStarted fires once Kestrel is up, but Jellyfin still returns 503 for a bit
        // longer while it runs its own "startup tasks" - CoreStartupHasCompleted is the actual
        // readiness signal for that window, so poll it before attempting registration.
        _lifetime.ApplicationStarted.Register(() => _ = WaitForReadyThenRegisterAsync());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WaitForReadyThenRegisterAsync()
    {
        var deadline = DateTime.UtcNow + ReadinessTimeout;
        while (!_appHost.CoreStartupHasCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(ReadinessPollInterval).ConfigureAwait(false);
        }

        if (!_appHost.CoreStartupHasCompleted)
        {
            _logger.LogWarning("Movie Night: server core startup did not complete within {Timeout}, skipping tuner/listings registration", ReadinessTimeout);
            return;
        }

        await RegisterAsync().ConfigureAwait(false);
    }

    private async Task RegisterAsync()
    {
        var baseUrl = $"http://127.0.0.1:{_appHost.HttpPort}/MovieNight";
        var playlistUrl = $"{baseUrl}/playlist.m3u";
        var epgUrl = $"{baseUrl}/epg.xml";

        try
        {
            var liveTvOptions = (LiveTvOptions)_configurationManager.GetConfiguration(LiveTvConfigKey);

            var existingTuner = liveTvOptions.TunerHosts
                .FirstOrDefault(t => string.Equals(t.Url, playlistUrl, StringComparison.OrdinalIgnoreCase));

            var tunerInfo = existingTuner ?? new TunerHostInfo();
            tunerInfo.Url = playlistUrl;
            tunerInfo.Type = "m3u";
            tunerInfo.FriendlyName = ChannelName;
            tunerInfo.TunerCount = 1;

            await _tunerHostManager.SaveTunerHost(tunerInfo).ConfigureAwait(false);
            _logger.LogInformation("Movie Night: registered M3U tuner at {Url}", playlistUrl);

            var existingListing = liveTvOptions.ListingProviders
                .FirstOrDefault(l => string.Equals(l.Path, epgUrl, StringComparison.OrdinalIgnoreCase));

            var listingInfo = existingListing ?? new ListingsProviderInfo();
            listingInfo.Type = "xmltv";
            listingInfo.Path = epgUrl;
            listingInfo.EnableAllTuners = true;

            await _listingsManager.SaveListingProvider(listingInfo, validateLogin: false, validateListings: false).ConfigureAwait(false);
            _logger.LogInformation("Movie Night: registered XMLTV listings provider at {Url}", epgUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Movie Night: failed to register tuner/listings provider");
        }
    }
}

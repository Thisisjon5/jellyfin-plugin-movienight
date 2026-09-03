using MediaBrowser.Controller;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.MovieNight;

/// <summary>
/// Registers plugin services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<TunerRegistrar>();
        serviceCollection.AddSingleton<BroadcastManager>();

        // The live pipeline (ROADMAP-2026-09-03.md step 1): ladder encoder, mezzanine prep, the
        // session that ties them to the v3 feeders, and the tuner host that hands clients the
        // ladder URL. Registering ITunerHost into DI is what makes a plugin-supplied tuner host
        // join the set Live TV enumerates (proven by the T0 gate).
        serviceCollection.AddSingleton<LadderEncoder>();
        serviceCollection.AddSingleton<MezzaninePrep>();
        serviceCollection.AddSingleton<LiveSession>();
        serviceCollection.AddSingleton<LiveSourceKnobs>();
        serviceCollection.AddSingleton<ITunerHost, MovieNightTunerHost>();

        // The tuner host derives each client's ladder URL from the request asking for media
        // sources. Jellyfin registers this itself; AddHttpContextAccessor is idempotent.
        serviceCollection.AddHttpContextAccessor();
    }
}

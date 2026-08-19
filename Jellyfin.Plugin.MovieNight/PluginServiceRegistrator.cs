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

        // T0 GATE SPIKE (planning/DESIGN-abr-ladder.md §8). Registering ITunerHost into Jellyfin's
        // DI is what makes a plugin-supplied tuner host join the enumerable Live TV resolves.
        // Remove both lines with the spike.
        serviceCollection.AddSingleton<T0Gate>();
        serviceCollection.AddSingleton<ITunerHost, T0GateTunerHost>();
    }
}

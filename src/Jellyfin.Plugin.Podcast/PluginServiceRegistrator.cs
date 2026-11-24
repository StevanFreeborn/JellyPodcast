using Jellyfin.Plugin.Podcast.Services;

using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;

using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Podcast;

/// <summary>
/// Registers plugin background services with Jellyfin's dependency injection container.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
  /// <inheritdoc />
  public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
  {
    serviceCollection.AddHostedService<PodcastLifecycleMonitor>();
  }
}
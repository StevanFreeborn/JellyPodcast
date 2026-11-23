namespace Jellyfin.Plugin.Podcast.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PluginInstanceFixture
{
  public const string Name = "PluginInstance";
}
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;

using Moq;

namespace Jellyfin.Plugin.Podcast.Tests;

[Collection(PluginInstanceFixture.Name)]
public sealed class PluginTests : IDisposable
{
  private readonly string _tempDir;

  public PluginTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), $"JellyPodcast_PluginTests_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDir);
  }

  public void Dispose()
  {
    if (Directory.Exists(_tempDir))
    {
      Directory.Delete(_tempDir, recursive: true);
    }

    GC.SuppressFinalize(this);
  }

  [Fact]
  public void Constructor_SetsMetadataSingletonAndConfigurationPages()
  {
    var pathsMock = new Mock<IApplicationPaths>();
    pathsMock.SetupGet(p => p.PluginConfigurationsPath).Returns(_tempDir);
    pathsMock.SetupGet(p => p.PluginsPath).Returns(_tempDir);

    var xmlMock = new Mock<IXmlSerializer>();

    var plugin = new Plugin(pathsMock.Object, xmlMock.Object);

    Assert.Equal("JellyPodcast", plugin.Name);
    Assert.Equal(Guid.Parse("a3af35b9-8c3f-4349-8fcb-18561540fbfc"), plugin.Id);
    Assert.Same(plugin, Plugin.Instance);

    var page = Assert.Single(plugin.GetPages());
    Assert.Equal("JellyPodcast", page.Name);
    Assert.Equal("Jellyfin.Plugin.Podcast.Configuration.configPage.html", page.EmbeddedResourcePath);
  }
}
using Jellyfin.Plugin.Podcast.Api;
using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Services;

using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Jellyfin.Plugin.Podcast.Tests;

[Collection(PluginInstanceFixture.Name)]
public sealed class PluginInstanceIntegrationTests : IDisposable
{
  private readonly string _tempDir;
  private readonly Plugin? _originalInstance;

  public PluginInstanceIntegrationTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), $"JellyPodcast_PluginInstTests_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDir);
    _originalInstance = Plugin.Instance;
  }

  public void Dispose()
  {
    Plugin.Instance = _originalInstance;

    if (Directory.Exists(_tempDir))
    {
      Directory.Delete(_tempDir, recursive: true);
    }

    GC.SuppressFinalize(this);
  }

  [Fact]
  public async Task PodcastLibraryManager_RefreshLibraryAsync_UsesPluginInstanceOrExitsCleanlyWhenNull()
  {
    var manager = new PodcastLibraryManager(
      new Mock<IPodcastFeedParser>().Object,
      new Mock<IPodcastMediaDownloader>().Object,
      new AudioMetadataTagger(),
      new PodcastStateStore(),
      NullLogger<PodcastLibraryManager>.Instance
    );

    Plugin.Instance = null;
    var nullProgress = new List<double>();
    await manager.RefreshLibraryAsync(TestContext.Current.CancellationToken, new SyncProgress(nullProgress));
    Assert.Equal([100d], nullProgress);

    var plugin = CreateTestPlugin(_tempDir);
    plugin.UpdateConfiguration(new PluginConfiguration { LibraryPath = _tempDir });

    var activeProgress = new List<double>();
    await manager.RefreshLibraryAsync(TestContext.Current.CancellationToken, new SyncProgress(activeProgress));
    Assert.Equal([0d, 100d], activeProgress);
  }

  [Fact]
  public void PodcastCleanupController_PublicConstructor_ReadsPluginInstance()
  {
    Plugin.Instance = null;
    var controller = new PodcastCleanupController(NullLogger<PodcastCleanupController>.Instance);

    var nullResult = Assert.IsType<ObjectResult>(controller.CleanupEpisodes());
    Assert.Equal(500, nullResult.StatusCode);

    var plugin = CreateTestPlugin(_tempDir);
    plugin.UpdateConfiguration(new PluginConfiguration { LibraryPath = _tempDir });

    var okResult = Assert.IsType<OkObjectResult>(controller.CleanupEpisodes());
    var payload = Assert.IsType<CleanupResult>(okResult.Value);
    Assert.Equal(0, payload.FilesDeleted);
  }

  [Fact]
  public void PodcastOpmlController_PublicConstructor_ReadsPluginInstance()
  {
    Plugin.Instance = null;
    var controller = new PodcastOpmlController(NullLogger<PodcastOpmlController>.Instance);

    var nullResult = Assert.IsType<ObjectResult>(controller.ExportOpml());
    Assert.Equal(500, nullResult.StatusCode);

    var plugin = CreateTestPlugin(_tempDir);
    plugin.UpdateConfiguration(
      new PluginConfiguration
      {
        LibraryPath = _tempDir,
        PodcastFeeds =
        [
          new PodcastFeed { Id = "f1", Url = "https://example.com/feed.xml", CustomName = "Example" },
        ],
      }
    );

    var fileResult = Assert.IsType<FileContentResult>(controller.ExportOpml());
    Assert.Equal("jellyfin-podcasts.opml", fileResult.FileDownloadName);
  }

  [Fact]
  public void PluginServiceRegistrator_RegistersPodcastLifecycleMonitor()
  {
    var services = new ServiceCollection();
    var registrator = new PluginServiceRegistrator();

    registrator.RegisterServices(services, new Mock<IServerApplicationHost>().Object);

    Assert.Contains(
      services,
      sd => sd.ServiceType == typeof(IHostedService) && sd.ImplementationType == typeof(PodcastLifecycleMonitor)
    );
  }

  [Fact]
  public async Task PodcastLifecycleMonitor_PublicConstructor_ReadsPluginInstance()
  {
    var libraryMock = new Mock<ILibraryManager>();
    var userDataMock = new Mock<IUserDataManager>();
    var monitor = new PodcastLifecycleMonitor(
      libraryMock.Object,
      userDataMock.Object,
      NullLogger<PodcastLifecycleMonitor>.Instance
    );

    var showDir = Path.Combine(_tempDir, "Public Show");
    Directory.CreateDirectory(showDir);
    var stateStore = new PodcastStateStore();
    stateStore.RecordDownloadedEpisode(showDir, "ep-pub", "ep.mp3");

    var plugin = CreateTestPlugin(_tempDir);
    plugin.UpdateConfiguration(new PluginConfiguration { LibraryPath = _tempDir, SkipDeletedEpisodes = true });

    await monitor.StartAsync(TestContext.Current.CancellationToken);
    libraryMock.Raise(
      m => m.ItemRemoved += null,
      libraryMock.Object,
      new ItemChangeEventArgs
      {
        Item = new MediaBrowser.Controller.Entities.Audio.Audio { Path = Path.Combine(showDir, "ep.mp3") },
      }
    );
    await monitor.StopAsync(TestContext.Current.CancellationToken);

    Assert.True(stateStore.IsEpisodeDismissed(showDir, "ep-pub", includeUserDeleted: true));
  }

  private static Plugin CreateTestPlugin(string configDir)
  {
    var pathsMock = new Mock<IApplicationPaths>();
    pathsMock.SetupGet(p => p.PluginConfigurationsPath).Returns(configDir);
    pathsMock.SetupGet(p => p.PluginsPath).Returns(configDir);
    return new Plugin(pathsMock.Object, new Mock<IXmlSerializer>().Object);
  }

  private sealed class SyncProgress(List<double> values) : IProgress<double>
  {
    public void Report(double value) => values.Add(value);
  }
}
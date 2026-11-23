using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.ScheduledTasks;

using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Jellyfin.Plugin.Podcast.Tests;

[Collection(PluginInstanceFixture.Name)]
public sealed class RefreshPodcastLibraryTaskTests : IDisposable
{
  private readonly string _tempDir;

  public RefreshPodcastLibraryTaskTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), $"JellyPodcast_TaskTests_{Guid.NewGuid():N}");
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
  public void MetadataAndDefaultTriggers_AreConfigured()
  {
    var factoryMock = new Mock<IHttpClientFactory>();
    var task = new RefreshPodcastLibraryTask(factoryMock.Object, NullLoggerFactory.Instance);

    Assert.Equal("Refresh Podcast Library", task.Name);
    Assert.Equal("Fetches new episodes from configured podcast feeds, downloads audio files, and embeds metadata", task.Description);
    Assert.Equal("Podcast", task.Category);
    Assert.Equal("RefreshPodcastLibrary", task.Key);

    var trigger = Assert.Single(task.GetDefaultTriggers());
    Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
    Assert.Equal(TimeSpan.FromHours(2).Ticks, trigger.IntervalTicks);
  }

  [Fact]
  public async Task ExecuteAsync_RunsLibraryRefreshAndReportsProgress()
  {
    var pathsMock = new Mock<IApplicationPaths>();
    pathsMock.SetupGet(p => p.PluginConfigurationsPath).Returns(_tempDir);
    pathsMock.SetupGet(p => p.PluginsPath).Returns(_tempDir);
    var xmlMock = new Mock<IXmlSerializer>();
    var plugin = new Plugin(pathsMock.Object, xmlMock.Object);
    plugin.UpdateConfiguration(new PluginConfiguration { LibraryPath = _tempDir });

    var factoryMock = new Mock<IHttpClientFactory>();
    var task = new RefreshPodcastLibraryTask(factoryMock.Object, NullLoggerFactory.Instance);

    var reported = new List<double>();
    await task.ExecuteAsync(new SyncProgress(reported), TestContext.Current.CancellationToken);

    Assert.Equal(0d, reported.First());
    Assert.Equal(100d, reported.Last());

    await task.ExecuteAsync(null!, TestContext.Current.CancellationToken);

    var libraryManagerMock = new Mock<ILibraryManager>();
    var fixedClockTask = new RefreshPodcastLibraryTask(
      factoryMock.Object,
      NullLoggerFactory.Instance,
      libraryManagerMock.Object,
      new FixedTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero))
    );
    var fixedClockProgress = new List<double>();
    await fixedClockTask.ExecuteAsync(new SyncProgress(fixedClockProgress), TestContext.Current.CancellationToken);
    Assert.Equal(0d, fixedClockProgress.First());
    Assert.Equal(100d, fixedClockProgress.Last());
    libraryManagerMock.Verify(l => l.QueueLibraryScan(), Times.Never);
  }

  [Fact]
  public async Task ExecuteAsync_QueuesLibraryScan_WhenEpisodesAreDownloaded()
  {
    const string rss = """
      <?xml version="1.0" encoding="UTF-8"?>
      <rss version="2.0">
        <channel>
          <title>Task Scan Show</title>
          <item>
            <title>Episode 1</title>
            <pubDate>Mon, 23 Nov 2026 10:00:00 GMT</pubDate>
            <enclosure url="https://cdn.example.com/ep1.mp3" type="audio/mpeg" length="834" />
            <guid>ep-task-1</guid>
          </item>
        </channel>
      </rss>
      """;

    var mp3Bytes = CreateMinimalValidMp3Bytes();
    var handler = new StubHttpMessageHandler(
      (req, _) =>
      {
        if (req.RequestUri!.AbsoluteUri.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
          var audioResponse = new HttpResponseMessage(HttpStatusCode.OK)
          {
            Content = new ByteArrayContent(mp3Bytes),
          };
          audioResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
          return Task.FromResult(audioResponse);
        }

        return Task.FromResult(
          new HttpResponseMessage(HttpStatusCode.OK)
          {
            Content = new StringContent(rss, Encoding.UTF8, "application/xml"),
          }
        );
      }
    );

    var pathsMock = new Mock<IApplicationPaths>();
    pathsMock.SetupGet(p => p.PluginConfigurationsPath).Returns(_tempDir);
    pathsMock.SetupGet(p => p.PluginsPath).Returns(_tempDir);
    var plugin = new Plugin(pathsMock.Object, new Mock<IXmlSerializer>().Object);
    plugin.UpdateConfiguration(
      new PluginConfiguration
      {
        LibraryPath = _tempDir,
        DownloadThumbnails = false,
        PodcastFeeds =
        [
          new PodcastFeed { Id = "feed-1", Url = "https://example.com/feed.xml", Enabled = true },
        ],
      }
    );

    var factoryMock = new Mock<IHttpClientFactory>();
    factoryMock
      .Setup(f => f.CreateClient(It.IsAny<string>()))
      .Returns(() => new HttpClient(handler, disposeHandler: false));

    var libraryManagerMock = new Mock<ILibraryManager>();
    var task = new RefreshPodcastLibraryTask(
      factoryMock.Object,
      NullLoggerFactory.Instance,
      libraryManagerMock.Object,
      new FixedTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero))
    );

    await task.ExecuteAsync(new SyncProgress([]), TestContext.Current.CancellationToken);

    libraryManagerMock.Verify(l => l.QueueLibraryScan(), Times.Once);
  }

  [Fact]
  public async Task ExecuteAsync_LogsAndRethrows_WhenCancelledWithEnabledFeeds()
  {
    var pathsMock = new Mock<IApplicationPaths>();
    pathsMock.SetupGet(p => p.PluginConfigurationsPath).Returns(_tempDir);
    pathsMock.SetupGet(p => p.PluginsPath).Returns(_tempDir);
    var xmlMock = new Mock<IXmlSerializer>();
    var plugin = new Plugin(pathsMock.Object, xmlMock.Object);
    plugin.UpdateConfiguration(
      new PluginConfiguration
      {
        LibraryPath = _tempDir,
        PodcastFeeds =
        [
          new PodcastFeed { Id = "feed-1", Url = "https://example.com/feed.xml", Enabled = true },
        ],
      }
    );

    var factoryMock = new Mock<IHttpClientFactory>();
    var task = new RefreshPodcastLibraryTask(factoryMock.Object, NullLoggerFactory.Instance);

    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    await Assert.ThrowsAsync<OperationCanceledException>(
      () => task.ExecuteAsync(new SyncProgress([]), cts.Token)
    );
  }

  private static byte[] CreateMinimalValidMp3Bytes()
  {
    const int frameSize = 417;
    var bytes = new byte[frameSize * 2];

    for (var frame = 0; frame < 2; frame++)
    {
      var offset = frame * frameSize;
      bytes[offset] = 0xFF;
      bytes[offset + 1] = 0xFB;
      bytes[offset + 2] = 0x90;
      bytes[offset + 3] = 0x00;
    }

    return bytes;
  }

  private sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder
  ) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      => responder(request, cancellationToken);
  }

  private sealed class SyncProgress(List<double> values) : IProgress<double>
  {
    public void Report(double value) => values.Add(value);
  }
}
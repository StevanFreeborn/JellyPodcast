using Jellyfin.Plugin.Podcast.Api;
using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Services;

using MediaBrowser.Controller.Library;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class PodcastCleanupControllerTests : IDisposable
{
  private readonly string _libraryPath;
  private readonly PodcastStateStore _stateStore = new();

  public PodcastCleanupControllerTests()
  {
    _libraryPath = Path.Combine(Path.GetTempPath(), $"JellyPodcast_CleanupTests_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_libraryPath);
  }

  public void Dispose()
  {
    if (Directory.Exists(_libraryPath))
    {
      Directory.Delete(_libraryPath, recursive: true);
    }

    GC.SuppressFinalize(this);
  }

  [Fact]
  public void CleanupEpisodes_DeletesAudioFilesAndState_AndRemovesOrphanedFeedDirectories()
  {
    var activeDir = Path.Combine(_libraryPath, "Active Show");
    Directory.CreateDirectory(activeDir);
    File.WriteAllBytes(Path.Combine(activeDir, "2026-11-23 - Ep1.mp3"), [1, 2, 3]);
    File.WriteAllBytes(Path.Combine(activeDir, "2026-11-24 - Ep2.m4a"), [4, 5, 6]);
    File.WriteAllBytes(Path.Combine(activeDir, PodcastMediaDownloader.CoverFileName), [7, 8, 9]);
    _stateStore.RecordDownloadedEpisode(activeDir, "ep-1", "2026-11-23 - Ep1.mp3", feedId: "feed-active", feedUrl: "https://example.com/active.xml");

    var orphanDir = Path.Combine(_libraryPath, "Removed Show");
    Directory.CreateDirectory(orphanDir);
    File.WriteAllBytes(Path.Combine(orphanDir, "2026-11-20 - Old.mp3"), [1, 2]);
    File.WriteAllBytes(Path.Combine(orphanDir, PodcastMediaDownloader.CoverFileName), [3, 4]);
    _stateStore.RecordDownloadedEpisode(orphanDir, "ep-old", "2026-11-20 - Old.mp3", feedId: "feed-removed", feedUrl: "https://example.com/removed.xml");

    var config = new PluginConfiguration
    {
      LibraryPath = _libraryPath,
      PodcastFeeds = [
        new() { Id = "feed-active", Url = "https://example.com/active.xml", Enabled = true }
      ]
    };

    var libraryManagerMock = new Mock<ILibraryManager>();
    var controller = new PodcastCleanupController(
      NullLogger<PodcastCleanupController>.Instance,
      () => config,
      libraryManager: libraryManagerMock.Object
    );

    var actionResult = controller.CleanupEpisodes();

    var okResult = Assert.IsType<OkObjectResult>(actionResult);
    var payload = Assert.IsType<CleanupResult>(okResult.Value);
    Assert.Equal(6, payload.FilesDeleted);
    Assert.Equal(1, payload.DirectoriesDeleted);
    libraryManagerMock.Verify(l => l.QueueLibraryScan(), Times.Once);

    // Active feed keeps cover.jpg; audio files and state file are deleted.
    Assert.True(Directory.Exists(activeDir));
    Assert.True(File.Exists(Path.Combine(activeDir, PodcastMediaDownloader.CoverFileName)));
    Assert.False(File.Exists(Path.Combine(activeDir, "2026-11-23 - Ep1.mp3")));
    Assert.False(File.Exists(Path.Combine(activeDir, "2026-11-24 - Ep2.m4a")));
    Assert.False(File.Exists(Path.Combine(activeDir, PodcastStateStore.StateFileName)));

    // Orphaned feed directory is completely removed.
    Assert.False(Directory.Exists(orphanDir));
  }

  [Fact]
  public void CleanupOrphanedFeeds_RemovesOrphanedDirectories_AndPreservesActiveFeeds()
  {
    var activeDir = Path.Combine(_libraryPath, "Active Show");
    Directory.CreateDirectory(activeDir);
    var activeAudio = Path.Combine(activeDir, "2026-11-23 - Ep1.mp3");
    File.WriteAllBytes(activeAudio, [1, 2, 3]);
    File.WriteAllBytes(Path.Combine(activeDir, PodcastMediaDownloader.CoverFileName), [7, 8, 9]);
    _stateStore.RecordDownloadedEpisode(activeDir, "ep-1", "2026-11-23 - Ep1.mp3", feedId: "feed-active", feedUrl: "https://example.com/active.xml");

    var orphanDir = Path.Combine(_libraryPath, "Orphaned Show");
    Directory.CreateDirectory(orphanDir);
    File.WriteAllBytes(Path.Combine(orphanDir, "2026-11-20 - Old.mp3"), [1, 2]);
    File.WriteAllBytes(Path.Combine(orphanDir, PodcastMediaDownloader.CoverFileName), [3, 4]);
    _stateStore.RecordDownloadedEpisode(orphanDir, "ep-old", "2026-11-20 - Old.mp3", feedId: "feed-removed", feedUrl: "https://example.com/removed.xml");

    var config = new PluginConfiguration
    {
      LibraryPath = _libraryPath,
      PodcastFeeds = [
        new() { Id = "feed-active", Url = "https://example.com/active.xml", CustomName = "Active Show", Enabled = true }
      ]
    };

    var libraryManagerMock = new Mock<ILibraryManager>();
    var controller = new PodcastCleanupController(
      NullLogger<PodcastCleanupController>.Instance,
      () => config,
      libraryManager: libraryManagerMock.Object
    );

    var actionResult = controller.CleanupOrphanedFeeds();

    var okResult = Assert.IsType<OkObjectResult>(actionResult);
    var payload = Assert.IsType<CleanupResult>(okResult.Value);
    Assert.Equal(3, payload.FilesDeleted);
    Assert.Equal(1, payload.DirectoriesDeleted);
    libraryManagerMock.Verify(l => l.QueueLibraryScan(), Times.Once);

    Assert.True(File.Exists(activeAudio));
    Assert.True(File.Exists(Path.Combine(activeDir, PodcastMediaDownloader.CoverFileName)));
    Assert.True(File.Exists(Path.Combine(activeDir, PodcastStateStore.StateFileName)));
    Assert.False(Directory.Exists(orphanDir));
  }

  [Fact]
  public void CleanupEpisodes_ReturnsNotFound_WhenLibraryPathDoesNotExist()
  {
    var missingPath = Path.Combine(_libraryPath, "does-not-exist");
    var config = new PluginConfiguration { LibraryPath = missingPath };
    var controller = new PodcastCleanupController(NullLogger<PodcastCleanupController>.Instance, () => config);

    var actionResult = controller.CleanupEpisodes();

    Assert.IsType<NotFoundObjectResult>(actionResult);
  }

  [Fact]
  public void CleanupEpisodes_Returns500_WhenConfigNullOrExceptionThrown()
  {
    var nullController = new PodcastCleanupController(NullLogger<PodcastCleanupController>.Instance, () => null);
    var nullResult = Assert.IsType<ObjectResult>(nullController.CleanupEpisodes());
    Assert.Equal(500, nullResult.StatusCode);

    var throwingController = new PodcastCleanupController(
      NullLogger<PodcastCleanupController>.Instance,
      () => throw new InvalidOperationException("boom")
    );
    var errorResult = Assert.IsType<ObjectResult>(throwingController.CleanupEpisodes());
    Assert.Equal(500, errorResult.StatusCode);
  }

  [Fact]
  public void CleanupEpisodes_DeletesTmpFiles_AndMatchesByFeedUrlOrCustomName()
  {
    var urlMatchedDir = Path.Combine(_libraryPath, "Url Matched");
    Directory.CreateDirectory(urlMatchedDir);
    File.WriteAllBytes(Path.Combine(urlMatchedDir, "ep.mp3.tmp"), [1]);
    File.WriteAllBytes(Path.Combine(urlMatchedDir, PodcastMediaDownloader.CoverFileName), [2]);
    _stateStore.Save(urlMatchedDir, new PodcastState { FeedUrl = "https://example.com/url-match.xml" });

    var customNamedDir = Path.Combine(_libraryPath, "Custom Folder");
    Directory.CreateDirectory(customNamedDir);
    File.WriteAllBytes(Path.Combine(customNamedDir, "ep.mp3"), [3]);

    var config = new PluginConfiguration
    {
      LibraryPath = _libraryPath,
      PodcastFeeds = [
        new PodcastFeed { Id = "other-id", Url = "https://example.com/url-match.xml", Enabled = true },
        new PodcastFeed { Id = "custom-id", Url = "https://example.com/custom.xml", CustomName = "Custom Folder", Enabled = true },
      ],
    };

    var controller = new PodcastCleanupController(NullLogger<PodcastCleanupController>.Instance, () => config);
    var result = Assert.IsType<OkObjectResult>(controller.CleanupEpisodes());
    var payload = Assert.IsType<CleanupResult>(result.Value);

    Assert.Equal(3, payload.FilesDeleted);
    Assert.Equal(0, payload.DirectoriesDeleted);
    Assert.True(File.Exists(Path.Combine(urlMatchedDir, PodcastMediaDownloader.CoverFileName)));
    Assert.False(File.Exists(Path.Combine(urlMatchedDir, "ep.mp3.tmp")));
    Assert.False(File.Exists(Path.Combine(customNamedDir, "ep.mp3")));
  }

  [Fact]
  public void CleanupEpisodes_DeletesEpisodeSidecarImages_AndPreservesShowCover()
  {
    var showDir = Path.Combine(_libraryPath, "Sidecar Cleanup Show");
    Directory.CreateDirectory(showDir);

    var showCoverPath = Path.Combine(showDir, PodcastMediaDownloader.CoverFileName);
    var ep1Audio = Path.Combine(showDir, "2026-11-23 - Ep1.mp3");
    var ep1Sidecar = Path.Combine(showDir, "2026-11-23 - Ep1.jpg");
    var ep2Audio = Path.Combine(showDir, "2026-11-24 - Ep2.m4a");
    var ep2Sidecar = Path.Combine(showDir, "2026-11-24 - Ep2.png");
    var ep3Sidecar = Path.Combine(showDir, "2026-11-25 - Ep3.webp");

    File.WriteAllBytes(showCoverPath, [1, 2]);
    File.WriteAllBytes(ep1Audio, [3, 4]);
    File.WriteAllBytes(ep1Sidecar, [5, 6]);
    File.WriteAllBytes(ep2Audio, [7, 8]);
    File.WriteAllBytes(ep2Sidecar, [9, 10]);
    File.WriteAllBytes(ep3Sidecar, [11, 12]);
    _stateStore.RecordDownloadedEpisode(showDir, "ep-1", "2026-11-23 - Ep1.mp3", feedId: "feed-sidecars", feedUrl: "https://example.com/sidecars.xml");

    var config = new PluginConfiguration
    {
      LibraryPath = _libraryPath,
      PodcastFeeds = [
        new PodcastFeed { Id = "feed-sidecars", Url = "https://example.com/sidecars.xml", Enabled = true }
      ]
    };

    var controller = new PodcastCleanupController(NullLogger<PodcastCleanupController>.Instance, () => config);
    var okResult = Assert.IsType<OkObjectResult>(controller.CleanupEpisodes());
    var payload = Assert.IsType<CleanupResult>(okResult.Value);

    Assert.Equal(6, payload.FilesDeleted);
    Assert.Equal(0, payload.DirectoriesDeleted);
    Assert.True(File.Exists(showCoverPath));
    Assert.False(File.Exists(ep1Audio));
    Assert.False(File.Exists(ep1Sidecar));
    Assert.False(File.Exists(ep2Audio));
    Assert.False(File.Exists(ep2Sidecar));
    Assert.False(File.Exists(ep3Sidecar));
    Assert.False(File.Exists(Path.Combine(showDir, PodcastStateStore.StateFileName)));
  }

  [Fact]
  public void CleanupOrphanedFeeds_HandlesNullConfigMissingDirectoryAndExceptions()
  {
    var nullController = new PodcastCleanupController(NullLogger<PodcastCleanupController>.Instance, () => null);
    var nullResult = Assert.IsType<ObjectResult>(nullController.CleanupOrphanedFeeds());
    Assert.Equal(500, nullResult.StatusCode);

    var missingController = new PodcastCleanupController(
      NullLogger<PodcastCleanupController>.Instance,
      () => new PluginConfiguration { LibraryPath = Path.Combine(_libraryPath, "missing") }
    );
    Assert.IsType<NotFoundObjectResult>(missingController.CleanupOrphanedFeeds());

    var throwingController = new PodcastCleanupController(
      NullLogger<PodcastCleanupController>.Instance,
      () => throw new InvalidOperationException("boom")
    );
    var errorResult = Assert.IsType<ObjectResult>(throwingController.CleanupOrphanedFeeds());
    Assert.Equal(500, errorResult.StatusCode);

    var noopLibraryManagerMock = new Mock<ILibraryManager>();
    var emptyConfig = new PluginConfiguration { LibraryPath = _libraryPath };
    var noopController = new PodcastCleanupController(
      NullLogger<PodcastCleanupController>.Instance,
      () => emptyConfig,
      libraryManager: noopLibraryManagerMock.Object
    );
    var noopResult = Assert.IsType<OkObjectResult>(noopController.CleanupOrphanedFeeds());
    var noopPayload = Assert.IsType<CleanupResult>(noopResult.Value);
    Assert.Equal(0, noopPayload.FilesDeleted);
    Assert.Equal(0, noopPayload.DirectoriesDeleted);
    noopLibraryManagerMock.Verify(l => l.QueueLibraryScan(), Times.Never);
  }

  [Fact]
  public void ResetDismissedHistory_ClearsTombstonesAndHandlesErrors()
  {
    var showA = Path.Combine(_libraryPath, "Show A");
    var showB = Path.Combine(_libraryPath, "Show B");
    Directory.CreateDirectory(showA);
    Directory.CreateDirectory(showB);
    _stateStore.RecordDismissedEpisode(showA, "ep-1", DismissedEpisodeReason.UserDeleted);
    _stateStore.RecordDismissedEpisode(showB, "ep-2", DismissedEpisodeReason.Played);

    var config = new PluginConfiguration { LibraryPath = _libraryPath };
    var controller = new PodcastCleanupController(NullLogger<PodcastCleanupController>.Instance, () => config, _stateStore);

    var okResult = Assert.IsType<OkObjectResult>(controller.ResetDismissedHistory());
    var payload = Assert.IsType<ResetDismissedResult>(okResult.Value);
    Assert.Equal(2, payload.EpisodesCleared);
    Assert.Empty(_stateStore.Load(showA).DismissedEpisodes);
    Assert.Empty(_stateStore.Load(showB).DismissedEpisodes);

    var nullController = new PodcastCleanupController(NullLogger<PodcastCleanupController>.Instance, () => null);
    Assert.Equal(500, Assert.IsType<ObjectResult>(nullController.ResetDismissedHistory()).StatusCode);

    var missingController = new PodcastCleanupController(
      NullLogger<PodcastCleanupController>.Instance,
      () => new PluginConfiguration { LibraryPath = Path.Combine(_libraryPath, "missing") }
    );
    Assert.IsType<NotFoundObjectResult>(missingController.ResetDismissedHistory());

    var throwingController = new PodcastCleanupController(
      NullLogger<PodcastCleanupController>.Instance,
      () => throw new InvalidOperationException("boom")
    );
    Assert.Equal(500, Assert.IsType<ObjectResult>(throwingController.ResetDismissedHistory()).StatusCode);
  }
}
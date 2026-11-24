using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Services;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class PodcastLifecycleMonitorTests : IDisposable
{
  private readonly string _tempDir;
  private readonly string _libraryPath;
  private readonly PodcastStateStore _stateStore = new(new FixedTimeProvider(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero)));

  public PodcastLifecycleMonitorTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), $"JellyPodcast_LifecycleTests_{Guid.NewGuid():N}");
    _libraryPath = Path.Combine(_tempDir, "podcasts");
    Directory.CreateDirectory(_libraryPath);
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
  public async Task ItemRemoved_RecordsUserDeletedTombstoneAndCleansSidecar_WhenSkipDeletedEpisodesEnabled()
  {
    var showDir = Path.Combine(_libraryPath, "Daily Show");
    Directory.CreateDirectory(showDir);
    var audioPath = Path.Combine(showDir, "2026-11-24 - Ep1.mp3");
    var sidecarPath = Path.Combine(showDir, "2026-11-24 - Ep1.png");
    var showCoverPath = Path.Combine(showDir, PodcastMediaDownloader.CoverFileName);

    await File.WriteAllBytesAsync(sidecarPath, [0x89, 0x50], TestContext.Current.CancellationToken);
    await File.WriteAllBytesAsync(showCoverPath, [0xFF, 0xD8], TestContext.Current.CancellationToken);
    _stateStore.RecordDownloadedEpisode(showDir, "ep-1", "2026-11-24 - Ep1.mp3");

    var libraryMock = new Mock<ILibraryManager>();
    var userDataMock = new Mock<IUserDataManager>();
    var config = new PluginConfiguration { LibraryPath = _libraryPath, SkipDeletedEpisodes = true };

    var monitor = new PodcastLifecycleMonitor(
      libraryMock.Object,
      userDataMock.Object,
      NullLogger<PodcastLifecycleMonitor>.Instance,
      () => config,
      _stateStore
    );

    await monitor.StartAsync(TestContext.Current.CancellationToken);
    libraryMock.Raise(
      m => m.ItemRemoved += null,
      libraryMock.Object,
      new ItemChangeEventArgs { Item = new AudioBook { Path = audioPath } }
    );
    await monitor.StopAsync(TestContext.Current.CancellationToken);

    Assert.False(File.Exists(sidecarPath));
    Assert.True(File.Exists(showCoverPath));
    Assert.True(_stateStore.IsEpisodeDismissed(showDir, "ep-1", includeUserDeleted: true));
    Assert.False(_stateStore.IsEpisodeDismissed(showDir, "ep-1", includeUserDeleted: false));
    Assert.Empty(_stateStore.Load(showDir).Episodes);
  }

  [Fact]
  public async Task ItemRemoved_RemovesTrackingWithoutTombstone_WhenSkipDeletedEpisodesDisabled()
  {
    var showDir = Path.Combine(_libraryPath, "Daily Show");
    Directory.CreateDirectory(showDir);
    var audioPath = Path.Combine(showDir, "2026-11-24 - Ep2.mp3");
    _stateStore.RecordDownloadedEpisode(showDir, "ep-2", "2026-11-24 - Ep2.mp3");

    var libraryMock = new Mock<ILibraryManager>();
    var userDataMock = new Mock<IUserDataManager>();
    var config = new PluginConfiguration { LibraryPath = _libraryPath, SkipDeletedEpisodes = false };

    var monitor = new PodcastLifecycleMonitor(
      libraryMock.Object,
      userDataMock.Object,
      NullLogger<PodcastLifecycleMonitor>.Instance,
      () => config,
      _stateStore
    );

    await monitor.StartAsync(TestContext.Current.CancellationToken);
    libraryMock.Raise(
      m => m.ItemRemoved += null,
      libraryMock.Object,
      new ItemChangeEventArgs { Item = new Audio { Path = audioPath } }
    );
    await monitor.StopAsync(TestContext.Current.CancellationToken);

    Assert.False(_stateStore.IsEpisodeDismissed(showDir, "ep-2", includeUserDeleted: true));
    Assert.Empty(_stateStore.Load(showDir).Episodes);
  }

  [Fact]
  public async Task ItemRemoved_IgnoresItemsOutsideLibraryMissingDirectoriesAndShortcuts()
  {
    var missingShowDir = Path.Combine(_libraryPath, "Deleted Show");
    var outsideDir = Path.Combine(_tempDir, "music", "Artist");
    Directory.CreateDirectory(outsideDir);

    var libraryMock = new Mock<ILibraryManager>();
    var userDataMock = new Mock<IUserDataManager>();
    var config = new PluginConfiguration { LibraryPath = _libraryPath, SkipDeletedEpisodes = true };

    var monitor = new PodcastLifecycleMonitor(
      libraryMock.Object,
      userDataMock.Object,
      NullLogger<PodcastLifecycleMonitor>.Instance,
      () => config,
      _stateStore
    );

    await monitor.StartAsync(TestContext.Current.CancellationToken);
    libraryMock.Raise(m => m.ItemRemoved += null, libraryMock.Object, new ItemChangeEventArgs { Item = new AudioBook { Path = Path.Combine(missingShowDir, "ep.mp3") } });
    libraryMock.Raise(m => m.ItemRemoved += null, libraryMock.Object, new ItemChangeEventArgs { Item = new Audio { Path = Path.Combine(outsideDir, "song.mp3") } });
    libraryMock.Raise(m => m.ItemRemoved += null, libraryMock.Object, new ItemChangeEventArgs { Item = new AudioBook { Path = Path.Combine(_libraryPath, "Show", "ep.mp3"), IsShortcut = true } });
    libraryMock.Raise(m => m.ItemRemoved += null, libraryMock.Object, new ItemChangeEventArgs { Item = new Folder { Path = _libraryPath } });
    await monitor.StopAsync(TestContext.Current.CancellationToken);

    Assert.False(Directory.Exists(missingShowDir));
  }

  [Fact]
  public async Task UserDataSaved_RecordsAndClearsPerUserPlayedTimestamps()
  {
    var showDir = Path.Combine(_libraryPath, "Show Played");
    Directory.CreateDirectory(showDir);
    var audioPath = Path.Combine(showDir, "2026-11-24 - Ep1.mp3");
    await File.WriteAllBytesAsync(audioPath, [1, 2, 3], TestContext.Current.CancellationToken);
    _stateStore.RecordDownloadedEpisode(showDir, "ep-1", "2026-11-24 - Ep1.mp3");

    var libraryMock = new Mock<ILibraryManager>();
    var userDataMock = new Mock<IUserDataManager>();
    var config = new PluginConfiguration { LibraryPath = _libraryPath };

    var monitor = new PodcastLifecycleMonitor(
      libraryMock.Object,
      userDataMock.Object,
      NullLogger<PodcastLifecycleMonitor>.Instance,
      () => config,
      _stateStore
    );

    var userId = Guid.NewGuid();
    var playedDate = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
    var item = new AudioBook { Path = audioPath };

    await monitor.StartAsync(TestContext.Current.CancellationToken);
    userDataMock.Raise(
      m => m.UserDataSaved += null,
      userDataMock.Object,
      new UserDataSaveEventArgs
      {
        UserId = userId,
        Item = item,
        SaveReason = UserDataSaveReason.PlaybackFinished,
        UserData = new UserItemData { Key = "ep-1", Played = true, LastPlayedDate = playedDate },
      }
    );

    Assert.Equal(playedDate, _stateStore.Load(showDir).Episodes["ep-1"].PlayedByUsersUtc[userId]);

    userDataMock.Raise(
      m => m.UserDataSaved += null,
      userDataMock.Object,
      new UserDataSaveEventArgs
      {
        UserId = userId,
        Item = item,
        SaveReason = UserDataSaveReason.TogglePlayed,
        UserData = new UserItemData { Key = "ep-1", Played = false, LastPlayedDate = null },
      }
    );
    await monitor.StopAsync(TestContext.Current.CancellationToken);

    Assert.Empty(_stateStore.Load(showDir).Episodes["ep-1"].PlayedByUsersUtc);
  }
}
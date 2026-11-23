using Jellyfin.Plugin.Podcast.Api;
using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Services;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class PodcastLibraryManagerTests : IDisposable
{
  private static readonly DateTimeOffset FixedUtcNow = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

  private readonly string _libraryPath;
  private readonly FixedTimeProvider _timeProvider = new(FixedUtcNow);
  private readonly PodcastStateStore _stateStore;
  private readonly AudioMetadataTagger _tagger = new();

  public PodcastLibraryManagerTests()
  {
    _stateStore = new(_timeProvider);
    _libraryPath = Path.Combine(Path.GetTempPath(), $"JellyPodcast_LibTests_{Guid.NewGuid():N}");
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
  public async Task RefreshLibraryAsync_DownloadsNewEpisodes_TagsThem_AndNeverCreatesStrmOrNfoFiles()
  {
    var feedUrl = "https://example.com/tech.xml";
    var feedData = new PodcastFeedData
    {
      FeedUrl = feedUrl,
      Title = "Tech: Weekly?",
      Author = "Alice Host",
      ImageUrl = "https://example.com/cover.jpg",
      Episodes = [
        new()
        {
          Guid = "ep-1",
          Title = "Pilot Episode",
          Description = "First episode notes",
          PubDate = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc),
          MediaUrl = "https://cdn.example.com/ep1.mp3",
          MediaType = "audio/mpeg"
        },
        new()
        {
          Guid = "ep-2",
          Title = "Second Episode",
          Description = "Second episode notes",
          PubDate = new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc),
          MediaUrl = "https://cdn.example.com/ep2.mp3",
          MediaType = "audio/mpeg"
        }
      ]
    };

    var fakeCoverBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
    var parserMock = new Mock<IPodcastFeedParser>();
    parserMock.Setup(p => p.ParseFeedAsync(feedUrl, It.IsAny<CancellationToken>()))
      .ReturnsAsync(feedData);

    var downloaderMock = CreateDownloaderMock(fakeCoverBytes);
    var manager = CreateManager(parserMock.Object, downloaderMock.Object);
    var config = CreateConfig(new PodcastFeed { Id = "feed-1", Url = feedUrl, Enabled = true });

    var refreshResult = await manager.RefreshLibraryAsync(config, CancellationToken.None);

    Assert.Equal(2, refreshResult.EpisodesDownloaded);
    Assert.Equal(0, refreshResult.EpisodesDeleted);
    Assert.True(refreshResult.HasChanges);

    var podcastDir = Path.Combine(_libraryPath, "Tech Weekly");
    Assert.True(Directory.Exists(podcastDir));
    Assert.Empty(Directory.GetDirectories(podcastDir));

    var files = Directory.GetFiles(podcastDir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
    Assert.Equal(
      new[]
      {
        ".podcast_state.json",
        "2026-11-20 - Pilot Episode.mp3",
        "2026-11-23 - Second Episode.mp3",
        "cover.jpg"
      },
      files
    );

    var newestMp3 = Path.Combine(podcastDir, "2026-11-23 - Second Episode.mp3");
    using var tagFile = TagLib.File.Create(newestMp3);
    Assert.Equal("Tech: Weekly?", tagFile.Tag.Album);
    Assert.Equal("Alice Host", tagFile.Tag.AlbumArtists[0]);
    Assert.Equal("Second Episode", tagFile.Tag.Title);
    Assert.Equal(1u, tagFile.Tag.Track);
    Assert.Equal(2026u, tagFile.Tag.Year);
    Assert.Equal("Second episode notes", tagFile.Tag.Comment);
    Assert.Single(tagFile.Tag.Pictures);
  }

  [Fact]
  public async Task RefreshLibraryAsync_SkipsAlreadyDownloadedEpisodes_OnSecondRun()
  {
    var feedUrl = "https://example.com/repeat.xml";
    var feedData = new PodcastFeedData
    {
      FeedUrl = feedUrl,
      Title = "Daily Brief",
      Author = "Host",
      Episodes = [
        new()
        {
          Guid = "ep-1",
          Title = "Episode One",
          PubDate = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc),
          MediaUrl = "https://cdn.example.com/ep1.mp3",
          MediaType = "audio/mpeg"
        }
      ]
    };

    var parserMock = new Mock<IPodcastFeedParser>();
    parserMock.Setup(p => p.ParseFeedAsync(feedUrl, It.IsAny<CancellationToken>()))
      .ReturnsAsync(feedData);

    var downloaderMock = CreateDownloaderMock(coverBytes: null);
    var manager = CreateManager(parserMock.Object, downloaderMock.Object);
    var config = CreateConfig(new PodcastFeed { Id = "feed-1", Url = feedUrl, Enabled = true });

    var firstRun = await manager.RefreshLibraryAsync(config, CancellationToken.None);

    Assert.Equal(1, firstRun.EpisodesDownloaded);
    Assert.Equal(0, firstRun.EpisodesDeleted);
    Assert.True(firstRun.HasChanges);
    downloaderMock.Verify(
      d => d.DownloadEpisodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
      Times.Once
    );

    downloaderMock.Invocations.Clear();

    var secondRun = await manager.RefreshLibraryAsync(config, CancellationToken.None);

    Assert.Equal(0, secondRun.EpisodesDownloaded);
    Assert.Equal(0, secondRun.EpisodesDeleted);
    Assert.False(secondRun.HasChanges);
    downloaderMock.Verify(
      d => d.DownloadEpisodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
      Times.Never
    );
  }

  [Fact]
  public async Task RefreshLibraryAsync_EnforcesMaxEpisodesPerPodcast_DeletingOldestAudioFiles()
  {
    var feedUrl = "https://example.com/limited.xml";
    var episodes = Enumerable.Range(1, 5)
      .Select(i => new PodcastEpisode
      {
        Guid = $"ep-{i}",
        Title = $"Episode {i}",
        PubDate = new DateTime(2026, 11, 10 + i, 8, 0, 0, DateTimeKind.Utc),
        MediaUrl = $"https://cdn.example.com/ep{i}.mp3",
        MediaType = "audio/mpeg"
      })
      .ToList();

    var feedData = new PodcastFeedData
    {
      FeedUrl = feedUrl,
      Title = "Limited Show",
      Author = "Host",
      Episodes = episodes
    };

    // Seed an older already-downloaded episode on disk to verify pruning across runs as well
    var podcastDir = Path.Combine(_libraryPath, "Limited Show");
    Directory.CreateDirectory(podcastDir);
    var legacyFileName = "2026-11-01 - Legacy Episode.mp3";
    await File.WriteAllBytesAsync(Path.Combine(podcastDir, legacyFileName), CreateMinimalValidMp3Bytes(), TestContext.Current.CancellationToken);
    _stateStore.RecordDownloadedEpisode(
      podcastDir,
      "ep-legacy",
      legacyFileName,
      new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc),
      "feed-1",
      feedUrl
    );

    var parserMock = new Mock<IPodcastFeedParser>();
    parserMock.Setup(p => p.ParseFeedAsync(feedUrl, It.IsAny<CancellationToken>()))
      .ReturnsAsync(feedData);

    var downloaderMock = CreateDownloaderMock(coverBytes: null);
    var manager = CreateManager(parserMock.Object, downloaderMock.Object);
    var config = CreateConfig(new PodcastFeed { Id = "feed-1", Url = feedUrl, Enabled = true });
    config.MaxEpisodesPerPodcast = 3;
    config.DownloadThumbnails = false;

    var pruneMaxResult = await manager.RefreshLibraryAsync(config, CancellationToken.None);

    Assert.Equal(3, pruneMaxResult.EpisodesDownloaded);
    Assert.Equal(1, pruneMaxResult.EpisodesDeleted);
    Assert.True(pruneMaxResult.HasChanges);

    var audioFiles = Directory.GetFiles(podcastDir, "*.mp3")
      .Select(Path.GetFileName)
      .OrderBy(n => n)
      .ToArray();

    Assert.Equal(
      new[]
      {
        "2026-11-13 - Episode 3.mp3",
        "2026-11-14 - Episode 4.mp3",
        "2026-11-15 - Episode 5.mp3"
      },
      audioFiles
    );

    var state = _stateStore.Load(podcastDir);
    Assert.Equal(3, state.Episodes.Count);
    Assert.False(state.Episodes.ContainsKey("ep-legacy"));
  }

  [Fact]
  public async Task RefreshLibraryAsync_EnforcesAutoCleanup_DeletingFilesOlderThanCleanupDays()
  {
    var now = FixedUtcNow.UtcDateTime;
    var feedUrl = "https://example.com/cleanup.xml";
    var feedData = new PodcastFeedData
    {
      FeedUrl = feedUrl,
      Title = "Cleanup Show",
      Author = "Host",
      Episodes = [
        new()
        {
          Guid = "ep-recent",
          Title = "Recent Episode",
          PubDate = now.AddDays(-5),
          MediaUrl = "https://cdn.example.com/recent.mp3",
          MediaType = "audio/mpeg"
        },
        new()
        {
          Guid = "ep-too-old-in-feed",
          Title = "Very Old Feed Episode",
          PubDate = now.AddDays(-60),
          MediaUrl = "https://cdn.example.com/too-old.mp3",
          MediaType = "audio/mpeg"
        }
      ]
    };

    var podcastDir = Path.Combine(_libraryPath, "Cleanup Show");
    Directory.CreateDirectory(podcastDir);
    var expiredFileName = $"{now.AddDays(-45):yyyy-MM-dd} - Expired Episode.mp3";
    await File.WriteAllBytesAsync(Path.Combine(podcastDir, expiredFileName), CreateMinimalValidMp3Bytes(), TestContext.Current.CancellationToken);
    _stateStore.RecordDownloadedEpisode(podcastDir, "ep-expired", expiredFileName, now.AddDays(-45), "feed-1", feedUrl);

    var parserMock = new Mock<IPodcastFeedParser>();
    parserMock.Setup(p => p.ParseFeedAsync(feedUrl, It.IsAny<CancellationToken>()))
      .ReturnsAsync(feedData);

    var downloaderMock = CreateDownloaderMock(coverBytes: null);
    var manager = CreateManager(parserMock.Object, downloaderMock.Object);
    var config = CreateConfig(new PodcastFeed { Id = "feed-1", Url = feedUrl, Enabled = true });
    config.EnableAutoCleanup = true;
    config.DaysToKeepEpisodes = 30;
    config.DownloadThumbnails = false;

    var cleanupResult = await manager.RefreshLibraryAsync(config, CancellationToken.None);

    Assert.Equal(1, cleanupResult.EpisodesDownloaded);
    Assert.Equal(1, cleanupResult.EpisodesDeleted);
    Assert.True(cleanupResult.HasChanges);

    var audioFiles = Directory.GetFiles(podcastDir, "*.mp3").Select(Path.GetFileName).ToArray();
    var item = Assert.Single(audioFiles);
    Assert.Contains("Recent Episode.mp3", item);
    Assert.False(File.Exists(Path.Combine(podcastDir, expiredFileName)));

    var state = _stateStore.Load(podcastDir);
    Assert.Single(state.Episodes);
    Assert.True(state.Episodes.ContainsKey("ep-recent"));
    Assert.False(state.Episodes.ContainsKey("ep-expired"));
  }

  [Fact]
  public async Task RefreshLibraryAsync_ContinuesOtherFeeds_WhenOneFeedOrEpisodeFails()
  {
    var failingFeedUrl = "https://example.com/broken.xml";
    var healthyFeedUrl = "https://example.com/healthy.xml";

    var healthyFeedData = new PodcastFeedData
    {
      FeedUrl = healthyFeedUrl,
      Title = "Healthy Show",
      Author = "Host",
      Episodes = [
        new()
        {
          Guid = "ep-fail",
          Title = "Broken Episode",
          PubDate = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc),
          MediaUrl = "https://cdn.example.com/fail.mp3",
          MediaType = "audio/mpeg"
        },
        new()
        {
          Guid = "ep-ok",
          Title = "Working Episode",
          PubDate = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc),
          MediaUrl = "https://cdn.example.com/ok.mp3",
          MediaType = "audio/mpeg"
        }
      ]
    };

    var parserMock = new Mock<IPodcastFeedParser>();
    parserMock.Setup(p => p.ParseFeedAsync(failingFeedUrl, It.IsAny<CancellationToken>()))
      .ThrowsAsync(new InvalidOperationException("Feed unreachable"));
    parserMock.Setup(p => p.ParseFeedAsync(healthyFeedUrl, It.IsAny<CancellationToken>()))
      .ReturnsAsync(healthyFeedData);

    var downloaderMock = new Mock<IPodcastMediaDownloader>();
    downloaderMock
      .Setup(d => d.DownloadEpisodeAsync("https://cdn.example.com/fail.mp3", It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ThrowsAsync(new IOException("CDN timeout"));
    downloaderMock
      .Setup(d => d.DownloadEpisodeAsync("https://cdn.example.com/ok.mp3", It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .Returns<string, string, CancellationToken>(
        async (_, dest, ct) =>
        {
          await File.WriteAllBytesAsync(dest, CreateMinimalValidMp3Bytes(), ct);
          return "audio/mpeg";
        }
      );

    var manager = CreateManager(parserMock.Object, downloaderMock.Object);
    var config = CreateConfig(
      new PodcastFeed { Id = "feed-bad", Url = failingFeedUrl, Enabled = true },
      new PodcastFeed { Id = "feed-good", Url = healthyFeedUrl, Enabled = true }
    );
    config.DownloadThumbnails = false;

    var partialFailureResult = await manager.RefreshLibraryAsync(config, CancellationToken.None);

    Assert.Equal(1, partialFailureResult.EpisodesDownloaded);
    Assert.Equal(0, partialFailureResult.EpisodesDeleted);
    Assert.True(partialFailureResult.HasChanges);

    var expectedPath = Path.Combine(_libraryPath, "Healthy Show", "2026-11-23 - Working Episode.mp3");
    Assert.True(File.Exists(expectedPath));
    Assert.False(File.Exists(Path.Combine(_libraryPath, "Healthy Show", "2026-11-22 - Broken Episode.mp3")));
  }

  [Fact]
  public async Task RefreshLibraryAsync_ReportsIncrementalProgress()
  {
    var feedUrl = "https://example.com/progress.xml";
    var feedData = new PodcastFeedData
    {
      FeedUrl = feedUrl,
      Title = "Progress Show",
      Author = "Host",
      Episodes = [
        new()
        {
          Guid = "ep-1",
          Title = "Ep 1",
          PubDate = new DateTime(2026, 11, 22, 8, 0, 0, DateTimeKind.Utc),
          MediaUrl = "https://cdn.example.com/1.mp3",
          MediaType = "audio/mpeg"
        },
        new()
        {
          Guid = "ep-2",
          Title = "Ep 2",
          PubDate = new DateTime(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc),
          MediaUrl = "https://cdn.example.com/2.mp3",
          MediaType = "audio/mpeg"
        }
      ]
    };

    var parserMock = new Mock<IPodcastFeedParser>();
    parserMock.Setup(p => p.ParseFeedAsync(feedUrl, It.IsAny<CancellationToken>()))
      .ReturnsAsync(feedData);

    var downloaderMock = CreateDownloaderMock(coverBytes: null);
    var manager = CreateManager(parserMock.Object, downloaderMock.Object);
    var config = CreateConfig(new PodcastFeed { Id = "feed-1", Url = feedUrl, Enabled = true });
    config.DownloadThumbnails = false;

    var reported = new List<double>();
    var progress = new SyncProgress(reported);

    await manager.RefreshLibraryAsync(config, CancellationToken.None, progress);

    Assert.True(reported.Count is >= 3);
    Assert.Equal(0d, reported.First());
    Assert.Equal(100d, reported.Last());
    Assert.True(reported.Zip(reported.Skip(1), (a, b) => a <= b).All(ok => ok));
  }

  [Fact]
  public async Task RefreshLibraryAsync_HandlesEmptyLibraryPathAndZeroEnabledFeeds()
  {
    var parserMock = new Mock<IPodcastFeedParser>();
    var downloaderMock = CreateDownloaderMock(coverBytes: null);
    var manager = CreateManager(parserMock.Object, downloaderMock.Object);

    var emptyPathProgress = new List<double>();
    var emptyPathResult = await manager.RefreshLibraryAsync(
      new PluginConfiguration { LibraryPath = "   " },
      TestContext.Current.CancellationToken,
      new SyncProgress(emptyPathProgress)
    );
    Assert.False(emptyPathResult.HasChanges);
    Assert.Equal([0d, 100d], emptyPathProgress);

    var noFeedsProgress = new List<double>();
    var noFeedsResult = await manager.RefreshLibraryAsync(
      CreateConfig(new PodcastFeed { Id = "disabled", Url = "https://example.com/a.xml", Enabled = false }),
      TestContext.Current.CancellationToken,
      new SyncProgress(noFeedsProgress)
    );
    Assert.False(noFeedsResult.HasChanges);
    Assert.Equal([0d, 100d], noFeedsProgress);
  }

  [Fact]
  public async Task RefreshLibraryAsync_HandlesNullParseResultCustomNameAndUnlimitedEpisodes()
  {
    var nullFeedUrl = "https://example.com/null.xml";
    var customFeedUrl = "https://example.com/custom.xml";

    var customFeedData = new PodcastFeedData
    {
      FeedUrl = customFeedUrl,
      Title = "Ignored Title",
      Author = string.Empty,
      Episodes = [
        new PodcastEpisode
        {
          Guid = string.Empty,
          Title = "No Url Episode",
          MediaUrl = string.Empty,
          PubDate = new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc),
        },
        new PodcastEpisode
        {
          Guid = string.Empty,
          Title = "Extensionless Stream",
          Description = string.Empty,
          MediaUrl = "https://cdn.example.com/stream/1",
          MediaType = "audio/mp4",
          PubDate = DateTime.MinValue,
        },
      ],
    };

    var parserMock = new Mock<IPodcastFeedParser>();
    parserMock.Setup(p => p.ParseFeedAsync(nullFeedUrl, It.IsAny<CancellationToken>())).ReturnsAsync((PodcastFeedData?)null);
    parserMock.Setup(p => p.ParseFeedAsync(customFeedUrl, It.IsAny<CancellationToken>())).ReturnsAsync(customFeedData);

    var downloaderMock = new Mock<IPodcastMediaDownloader>();
    downloaderMock
      .Setup(d => d.DownloadEpisodeAsync("https://cdn.example.com/stream/1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .Returns<string, string, CancellationToken>(
        async (_, dest, ct) =>
        {
          await File.WriteAllBytesAsync(dest, CreateMinimalValidMp3Bytes(), ct);
          return "audio/mpeg";
        }
      );

    var manager = CreateManager(parserMock.Object, downloaderMock.Object);
    var config = CreateConfig(
      new PodcastFeed { Id = "f-null", Url = nullFeedUrl, Enabled = true },
      new PodcastFeed
      {
        Id = "f-custom",
        Url = customFeedUrl,
        CustomName = "My Custom Podcast",
        Enabled = true,
        EnableAutoCleanup = false,
        DaysToKeepEpisodes = 14,
      }
    );
    config.MaxEpisodesPerPodcast = 0;
    config.DownloadThumbnails = false;

    await manager.RefreshLibraryAsync(config, TestContext.Current.CancellationToken);

    var podcastDir = Path.Combine(_libraryPath, "My Custom Podcast");
    var renamedPath = Path.Combine(podcastDir, "unknown - Extensionless Stream.mp3");
    Assert.True(File.Exists(renamedPath));

    var state = _stateStore.Load(podcastDir);
    Assert.True(state.Episodes.ContainsKey("https://cdn.example.com/stream/1"));
  }

  [Fact]
  public async Task RefreshLibraryAsync_PropagatesCancellation_DuringFeedAndEpisodeProcessing()
  {
    using var cts1 = new CancellationTokenSource();
    var parserMock1 = new Mock<IPodcastFeedParser>();
    parserMock1.Setup(p => p.ParseFeedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .Returns<string, CancellationToken>(
        (_, _) =>
        {
          cts1.Cancel();
          throw new OperationCanceledException(cts1.Token);
        }
      );

    var manager1 = CreateManager(parserMock1.Object, CreateDownloaderMock(null).Object);
    var config = CreateConfig(new PodcastFeed { Id = "f1", Url = "https://example.com/1.xml", Enabled = true });
    config.DownloadThumbnails = false;

    await Assert.ThrowsAsync<OperationCanceledException>(() => manager1.RefreshLibraryAsync(config, cts1.Token));

    using var cts2 = new CancellationTokenSource();
    var parserMock2 = new Mock<IPodcastFeedParser>();
    parserMock2.Setup(p => p.ParseFeedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .ReturnsAsync(
        new PodcastFeedData
        {
          Title = "Cancel Ep",
          Episodes = [
            new PodcastEpisode
            {
              Guid = "ep-c",
              Title = "Episode",
              MediaUrl = "https://cdn.example.com/c.mp3",
              PubDate = FixedUtcNow.UtcDateTime,
            },
          ],
        }
      );

    var downloaderMock2 = new Mock<IPodcastMediaDownloader>();
    downloaderMock2.Setup(d => d.DownloadEpisodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .Returns<string, string, CancellationToken>(
        (_, _, _) =>
        {
          cts2.Cancel();
          throw new OperationCanceledException(cts2.Token);
        }
      );

    var manager2 = CreateManager(parserMock2.Object, downloaderMock2.Object);
    await Assert.ThrowsAsync<OperationCanceledException>(() => manager2.RefreshLibraryAsync(config, cts2.Token));
  }

  [Fact]
  public async Task RefreshLibraryAsync_PruneHelpers_HandleEmptyFileNamesMissingFilesAndUntrackedFiles()
  {
    var now = FixedUtcNow.UtcDateTime;
    var feedUrl = "https://example.com/prune-edges.xml";
    var podcastDir = Path.Combine(_libraryPath, "Prune Edges");
    Directory.CreateDirectory(podcastDir);

    var minDateAudio = Path.Combine(podcastDir, "unknown - Old MinDate.mp3");
    await File.WriteAllBytesAsync(minDateAudio, CreateMinimalValidMp3Bytes(), TestContext.Current.CancellationToken);
    File.SetLastWriteTimeUtc(minDateAudio, now.AddDays(-60));

    var untrackedOldAudio = Path.Combine(podcastDir, "2020-01-01 - Untracked Old.mp3");
    await File.WriteAllBytesAsync(untrackedOldAudio, CreateMinimalValidMp3Bytes(), TestContext.Current.CancellationToken);
    File.SetLastWriteTimeUtc(untrackedOldAudio, now.AddDays(-60));

    var untrackedRecentAudio = Path.Combine(podcastDir, "2026-11-22 - Untracked Recent.mp3");
    await File.WriteAllBytesAsync(untrackedRecentAudio, CreateMinimalValidMp3Bytes(), TestContext.Current.CancellationToken);

    var nonAudioFile = Path.Combine(podcastDir, "notes.txt");
    await File.WriteAllTextAsync(nonAudioFile, "keep me", TestContext.Current.CancellationToken);
    File.SetLastWriteTimeUtc(nonAudioFile, now.AddDays(-60));

    _stateStore.Save(
      podcastDir,
      new PodcastState
      {
        FeedId = "f1",
        FeedUrl = feedUrl,
        Episodes =
        {
          ["ep-empty-name"] = new PodcastEpisodeState { Guid = "ep-empty-name", FileName = string.Empty },
          ["ep-mindate"] = new PodcastEpisodeState { Guid = "ep-mindate", FileName = "unknown - Old MinDate.mp3", PubDate = DateTime.MinValue },
          ["ep-missing-file"] = new PodcastEpisodeState { Guid = "ep-missing-file", FileName = "gone.mp3", PubDate = now.AddDays(-2) },
          ["ep-keep"] = new PodcastEpisodeState { Guid = "ep-keep", FileName = "2026-11-22 - Untracked Recent.mp3", PubDate = now.AddDays(-1) },
        },
      }
    );

    var parserMock = new Mock<IPodcastFeedParser>();
    parserMock.Setup(p => p.ParseFeedAsync(feedUrl, It.IsAny<CancellationToken>()))
      .ReturnsAsync(new PodcastFeedData { FeedUrl = feedUrl, Title = "Prune Edges", Episodes = [] });

    var manager = CreateManager(parserMock.Object, CreateDownloaderMock(null).Object);
    var config = CreateConfig(new PodcastFeed { Id = "f1", Url = feedUrl, Enabled = true });
    config.EnableAutoCleanup = true;
    config.DaysToKeepEpisodes = 30;
    config.MaxEpisodesPerPodcast = 1;
    config.DownloadThumbnails = false;

    await manager.RefreshLibraryAsync(config, TestContext.Current.CancellationToken);

    Assert.False(File.Exists(minDateAudio));
    Assert.False(File.Exists(untrackedOldAudio));
    Assert.True(File.Exists(untrackedRecentAudio));
    Assert.True(File.Exists(nonAudioFile));

    var state = _stateStore.Load(podcastDir);
    Assert.Single(state.Episodes);
    Assert.True(state.Episodes.ContainsKey("ep-keep"));
  }

  [Fact]
  public async Task RefreshLibraryAsync_UsesDistinctEpisodeArtwork_AndFallsBackToPodcastCover()
  {
    byte[] showCoverBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x01];
    byte[] episodeCoverBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x02];

    var feed = new PodcastFeed
    {
      Id = "feed-art",
      Url = "https://example.com/feed.xml",
      CustomName = "Art Show",
      Enabled = true,
    };

    var parserMock = new Mock<IPodcastFeedParser>();
    parserMock.Setup(p => p.ParseFeedAsync(feed.Url, It.IsAny<CancellationToken>()))
      .ReturnsAsync(
        new PodcastFeedData
        {
          Title = "Art Show",
          ImageUrl = "https://example.com/show.jpg",
          Episodes =
          [
            new PodcastEpisode
            {
              Guid = "ep-1",
              Title = "Episode One",
              MediaUrl = "https://example.com/ep1.mp3",
              MediaType = "audio/mpeg",
              ImageUrl = "https://example.com/ep1.png",
              PubDate = FixedUtcNow.UtcDateTime.AddDays(-1),
            },
            new PodcastEpisode
            {
              Guid = "ep-2",
              Title = "Episode Two",
              MediaUrl = "https://example.com/ep2.mp3",
              MediaType = "audio/mpeg",
              ImageUrl = "https://example.com/ep2-missing.png",
              PubDate = FixedUtcNow.UtcDateTime.AddDays(-2),
            },
            new PodcastEpisode
            {
              Guid = "ep-3",
              Title = "Episode Three",
              MediaUrl = "https://example.com/ep3.mp3",
              MediaType = "audio/mpeg",
              ImageUrl = "https://example.com/show.jpg",
              PubDate = FixedUtcNow.UtcDateTime.AddDays(-3),
            },
          ],
        }
      );

    var downloaderMock = CreateDownloaderMock(showCoverBytes);
    downloaderMock
      .Setup(d => d.DownloadImageBytesAsync("https://example.com/ep1.png", It.IsAny<CancellationToken>()))
      .ReturnsAsync(episodeCoverBytes);
    downloaderMock
      .Setup(d => d.DownloadImageBytesAsync("https://example.com/ep2-missing.png", It.IsAny<CancellationToken>()))
      .ReturnsAsync((byte[]?)null);

    var manager = CreateManager(parserMock.Object, downloaderMock.Object);
    await manager.RefreshLibraryAsync(CreateConfig(feed), TestContext.Current.CancellationToken);

    var podcastDir = Path.Combine(_libraryPath, "Art Show");
    var files = Directory.GetFiles(podcastDir, "*.mp3").OrderBy(f => f, StringComparer.Ordinal).ToArray();
    Assert.Equal(3, files.Length);

    var ep1File = Assert.Single(files, f => f.Contains("Episode One", StringComparison.Ordinal));
    var ep2File = Assert.Single(files, f => f.Contains("Episode Two", StringComparison.Ordinal));
    var ep3File = Assert.Single(files, f => f.Contains("Episode Three", StringComparison.Ordinal));

    using var ep1Tag = TagLib.File.Create(ep1File);
    Assert.Equal("image/png", Assert.Single(ep1Tag.Tag.Pictures).MimeType);
    Assert.Equal(episodeCoverBytes, Assert.Single(ep1Tag.Tag.Pictures).Data.Data);

    using var ep2Tag = TagLib.File.Create(ep2File);
    Assert.Equal("image/jpeg", Assert.Single(ep2Tag.Tag.Pictures).MimeType);
    Assert.Equal(showCoverBytes, Assert.Single(ep2Tag.Tag.Pictures).Data.Data);

    using var ep3Tag = TagLib.File.Create(ep3File);
    Assert.Equal("image/jpeg", Assert.Single(ep3Tag.Tag.Pictures).MimeType);
    Assert.Equal(showCoverBytes, Assert.Single(ep3Tag.Tag.Pictures).Data.Data);

    downloaderMock.Verify(d => d.DownloadImageBytesAsync("https://example.com/show.jpg", It.IsAny<CancellationToken>()), Times.Never);
  }

  private PodcastLibraryManager CreateManager(IPodcastFeedParser parser, IPodcastMediaDownloader downloader)
      => new(parser, downloader, _tagger, _stateStore, NullLogger<PodcastLibraryManager>.Instance, _timeProvider);

  private PluginConfiguration CreateConfig(params PodcastFeed[] feeds) => new()
  {
    LibraryPath = _libraryPath,
    MaxEpisodesPerPodcast = 50,
    DownloadThumbnails = true,
    EnableAutoCleanup = false,
    DaysToKeepEpisodes = 30,
    PodcastFeeds = feeds.ToList()
  };

  private static Mock<IPodcastMediaDownloader> CreateDownloaderMock(byte[]? coverBytes)
  {
    var mock = new Mock<IPodcastMediaDownloader>();
    mock.Setup(d => d.DownloadCoverArtAsync(It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .Returns<string?, string, CancellationToken>(
        async (_, dir, ct) =>
        {
          if (coverBytes is null)
          {
            return null;
          }

          Directory.CreateDirectory(dir);
          await File.WriteAllBytesAsync(Path.Combine(dir, PodcastMediaDownloader.CoverFileName), coverBytes, ct);
          return coverBytes;
        }
      );

    mock.Setup(d => d.DownloadEpisodeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
      .Returns<string, string, CancellationToken>(
        async (_, dest, ct) =>
        {
          var parent = Path.GetDirectoryName(dest);

          if (string.IsNullOrEmpty(parent) is false)
          {
            Directory.CreateDirectory(parent);
          }

          await File.WriteAllBytesAsync(dest, CreateMinimalValidMp3Bytes(), ct);
          return "audio/mpeg";
        }
      );

    return mock;
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

  private sealed class SyncProgress(List<double> values) : IProgress<double>
  {
    private readonly List<double> _values = values;

    public void Report(double value) => _values.Add(value);
  }
}
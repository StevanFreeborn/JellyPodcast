using System.Text.Json;

using Jellyfin.Plugin.Podcast.Services;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class PodcastStateStoreTests : IDisposable
{
  private static readonly DateTimeOffset FixedUtcNow = new(2026, 11, 23, 15, 30, 0, TimeSpan.Zero);

  private readonly string _tempDir;
  private readonly PodcastStateStore _store = new(new FixedTimeProvider(FixedUtcNow));

  public PodcastStateStoreTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), $"JellyPodcast_StateTests_{Guid.NewGuid():N}");
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
  public void IsEpisodeDownloaded_ReturnsFalse_WhenStateFileMissing()
  {
    var result = _store.IsEpisodeDownloaded(_tempDir, "ep-guid-1");

    Assert.False(result);
  }

  [Fact]
  public void RecordDownloadedEpisode_PersistsGuidAndFileName_ToJson()
  {
    var pubDate = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    _store.RecordDownloadedEpisode(
      _tempDir,
      "ep-guid-1",
      "2026-11-23 - Episode One.mp3",
      pubDate,
      feedId: "feed-1",
      feedUrl: "https://example.com/feed.xml"
    );

    var statePath = Path.Combine(_tempDir, PodcastStateStore.StateFileName);
    var tempPath = Path.Combine(_tempDir, $"{PodcastStateStore.StateFileName}.tmp");

    Assert.True(File.Exists(statePath));
    Assert.False(File.Exists(tempPath));

    var json = File.ReadAllText(statePath);
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    Assert.Equal("feed-1", root.GetProperty("FeedId").GetString());
    Assert.Equal("https://example.com/feed.xml", root.GetProperty("FeedUrl").GetString());
    Assert.Equal(
      "2026-11-23 - Episode One.mp3",
      root.GetProperty("Episodes").GetProperty("ep-guid-1").GetProperty("FileName").GetString()
    );

    var loaded = _store.Load(_tempDir);
    Assert.Equal(FixedUtcNow.UtcDateTime, loaded.Episodes["ep-guid-1"].DownloadedAtUtc);
  }

  [Fact]
  public void IsEpisodeDownloaded_ReturnsTrue_OnlyWhenGuidRecordedAndFileExistsOnDisk()
  {
    const string fileName = "2026-11-23 - Episode One.mp3";
    var audioPath = Path.Combine(_tempDir, fileName);
    File.WriteAllBytes(audioPath, [1, 2, 3]);

    _store.RecordDownloadedEpisode(_tempDir, "ep-guid-1", fileName);
    Assert.True(_store.IsEpisodeDownloaded(_tempDir, "ep-guid-1"));

    File.Delete(audioPath);
    Assert.False(_store.IsEpisodeDownloaded(_tempDir, "ep-guid-1"));
  }

  [Fact]
  public void RemoveEpisode_RemovesGuidFromPersistedState()
  {
    _store.RecordDownloadedEpisode(_tempDir, "ep-guid-1", "ep1.mp3");
    _store.RecordDownloadedEpisode(_tempDir, "ep-guid-2", "ep2.mp3");

    _store.RemoveEpisode(_tempDir, "ep-guid-1");

    var state = _store.Load(_tempDir);
    Assert.False(state.Episodes.ContainsKey("ep-guid-1"));
    Assert.True(state.Episodes.ContainsKey("ep-guid-2"));
  }

  [Fact]
  public void RemoveEpisode_IsNoOp_WhenGuidNotPresent()
  {
    _store.RecordDownloadedEpisode(_tempDir, "ep-guid-1", "ep1.mp3");

    _store.RemoveEpisode(_tempDir, "missing-guid");

    var state = _store.Load(_tempDir);
    Assert.Single(state.Episodes);
  }

  [Theory]
  [InlineData("not valid json")]
  [InlineData("null")]
  [InlineData("{\"FeedId\":\"f1\",\"Episodes\":null}")]
  public void Load_ReturnsEmptyEpisodes_WhenStateFileIsCorruptOrNull(string jsonContent)
  {
    File.WriteAllText(Path.Combine(_tempDir, PodcastStateStore.StateFileName), jsonContent);

    var state = _store.Load(_tempDir);

    Assert.NotNull(state.Episodes);
    Assert.Empty(state.Episodes);
  }

  [Fact]
  public void Load_ReturnsEmptyState_WhenStateFileIsLocked()
  {
    var statePath = Path.Combine(_tempDir, PodcastStateStore.StateFileName);
    File.WriteAllText(statePath, "{}");

    using var exclusiveLock = new FileStream(statePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    var state = _store.Load(_tempDir);

    Assert.Empty(state.Episodes);
  }

  [Fact]
  public void IsEpisodeDownloaded_ReturnsFalse_ForWhitespaceGuidOrEmptyRecordedFileName()
  {
    Assert.False(_store.IsEpisodeDownloaded(_tempDir, "   "));

    var state = new PodcastState
    {
      Episodes =
      {
        ["ep-empty-file"] = new PodcastEpisodeState { Guid = "ep-empty-file", FileName = string.Empty },
      },
    };

    _store.Save(_tempDir, state);

    Assert.False(_store.IsEpisodeDownloaded(_tempDir, "ep-empty-file"));
  }
}
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Jellyfin.Plugin.Podcast.Services;

/// <summary>
/// Known dismissal reasons recorded for episodes that should not be re-downloaded automatically.
/// </summary>
public static class DismissedEpisodeReason
{
  /// <summary>
  /// Episode was deleted by a user through the Jellyfin library UI.
  /// </summary>
  public const string UserDeleted = "UserDeleted";

  /// <summary>
  /// Episode was removed after being played.
  /// </summary>
  public const string Played = "Played";
}

/// <summary>
/// Represents a tombstone entry preventing automatic re-download of an episode.
/// </summary>
public class DismissedEpisodeState
{
  /// <summary>
  /// Gets or sets the dismissed episode GUID.
  /// </summary>
  [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Matches RSS <guid> element terminology.")]
  public string Guid { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the reason the episode was dismissed (e.g., <see cref="DismissedEpisodeReason.UserDeleted"/> or <see cref="DismissedEpisodeReason.Played"/>).
  /// </summary>
  public string Reason { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the UTC timestamp when the episode was dismissed.
  /// </summary>
  public DateTime DismissedAtUtc { get; set; }
}

/// <summary>
/// Represents persisted download state for a single podcast directory.
/// </summary>
public class PodcastState
{
  /// <summary>
  /// Gets or sets the configured feed identifier associated with this podcast folder.
  /// </summary>
  public string? FeedId { get; set; }

  /// <summary>
  /// Gets or sets the feed URL associated with this podcast folder.
  /// </summary>
  [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Serialized directly as string in .podcast_state.json.")]
  public string? FeedUrl { get; set; }

  /// <summary>
  /// Gets or sets the map of episode GUIDs to downloaded episode state records.
  /// </summary>
  [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Required for System.Text.Json deserialization.")]
  public Dictionary<string, PodcastEpisodeState> Episodes { get; set; } = new(StringComparer.Ordinal);

  /// <summary>
  /// Gets or sets the map of dismissed episode GUIDs to tombstone records.
  /// </summary>
  [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Required for System.Text.Json deserialization.")]
  public Dictionary<string, DismissedEpisodeState> DismissedEpisodes { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Represents metadata tracked for a downloaded podcast episode.
/// </summary>
public class PodcastEpisodeState
{
  /// <summary>
  /// Gets or sets the episode GUID.
  /// </summary>
  [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Matches RSS <guid> element terminology.")]
  public string Guid { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the local filename within the podcast directory.
  /// </summary>
  public string FileName { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the episode publication date.
  /// </summary>
  public DateTime PubDate { get; set; }

  /// <summary>
  /// Gets or sets the UTC timestamp when the episode was downloaded.
  /// </summary>
  public DateTime DownloadedAtUtc { get; set; }

  /// <summary>
  /// Gets or sets per-user UTC timestamps observed when users played or marked the episode as played.
  /// </summary>
  [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Required for System.Text.Json deserialization.")]
  public Dictionary<Guid, DateTime> PlayedByUsersUtc { get; set; } = [];
}

/// <summary>
/// Defines persistence operations for tracking downloaded podcast episodes.
/// </summary>
public interface IPodcastStateStore
{
  /// <summary>
  /// Loads the persisted state for the given podcast directory.
  /// </summary>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <returns>The loaded <see cref="PodcastState"/> or an empty instance if missing or unreadable.</returns>
  PodcastState Load(string podcastDir);

  /// <summary>
  /// Atomically saves the podcast state to disk.
  /// </summary>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <param name="state">State object to persist.</param>
  void Save(string podcastDir, PodcastState state);

  /// <summary>
  /// Determines whether an episode GUID is recorded in state and its audio file exists on disk.
  /// </summary>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <param name="episodeGuid">Episode GUID.</param>
  /// <returns><c>true</c> if the episode is recorded and present on disk; otherwise, <c>false</c>.</returns>
  bool IsEpisodeDownloaded(string podcastDir, string episodeGuid);

  /// <summary>
  /// Records a downloaded episode in the persisted state file.
  /// </summary>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <param name="episodeGuid">Episode GUID.</param>
  /// <param name="fileName">Downloaded local filename.</param>
  /// <param name="pubDate">Episode publication date.</param>
  /// <param name="feedId">Optional configured feed identifier.</param>
  /// <param name="feedUrl">Optional configured feed URL.</param>
  [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Feed URLs are persisted directly as raw strings.")]
  void RecordDownloadedEpisode(
    string podcastDir,
    string episodeGuid,
    string fileName,
    DateTime pubDate = default,
    string? feedId = null,
    string? feedUrl = null
  );

  /// <summary>
  /// Removes an episode GUID from the persisted state file.
  /// </summary>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <param name="episodeGuid">Episode GUID to remove.</param>
  void RemoveEpisode(string podcastDir, string episodeGuid);

  /// <summary>
  /// Determines whether an episode GUID is recorded in the dismissed tombstones map.
  /// </summary>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <param name="episodeGuid">Episode GUID to check.</param>
  /// <param name="includeUserDeleted">Whether tombstones created by user UI deletions should match.</param>
  /// <returns><c>true</c> if the episode should be skipped due to a dismissal tombstone; otherwise, <c>false</c>.</returns>
  bool IsEpisodeDismissed(string podcastDir, string episodeGuid, bool includeUserDeleted);

  /// <summary>
  /// Removes an episode from active tracking and records a dismissal tombstone.
  /// </summary>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <param name="episodeGuid">Episode GUID to dismiss.</param>
  /// <param name="reason">Dismissal reason constant.</param>
  void RecordDismissedEpisode(string podcastDir, string episodeGuid, string reason);

  /// <summary>
  /// Looks up a tracked episode GUID by its local audio filename within <paramref name="podcastDir"/>.
  /// </summary>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <param name="fileName">Audio filename (or relative filename).</param>
  /// <returns>The matching episode GUID, or <c>null</c> if not found.</returns>
  string? FindEpisodeGuidByFileName(string podcastDir, string fileName);

  /// <summary>
  /// Records or clears a user's played timestamp for a tracked episode.
  /// </summary>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <param name="episodeGuid">Episode GUID.</param>
  /// <param name="userId">Jellyfin user identifier.</param>
  /// <param name="isPlayed">Whether the episode is currently marked played for the user.</param>
  /// <param name="playedAtUtc">Optional UTC timestamp when playback completed or toggled.</param>
  /// <returns><c>true</c> if persisted state was updated; otherwise, <c>false</c>.</returns>
  bool RecordUserPlayedState(
    string podcastDir,
    string episodeGuid,
    Guid userId,
    bool isPlayed,
    DateTime? playedAtUtc = null
  );

  /// <summary>
  /// Clears all dismissed episode tombstones for <paramref name="podcastDir"/>.
  /// </summary>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <returns>The number of cleared tombstone entries.</returns>
  int ClearDismissedEpisodes(string podcastDir);
}

/// <summary>
/// Persists downloaded podcast episode manifests to <c>.podcast_state.json</c> using atomic file replacement.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="PodcastStateStore"/> class.
/// </remarks>
/// <param name="timeProvider">Optional time provider; defaults to <see cref="TimeProvider.System"/>.</param>
public class PodcastStateStore(TimeProvider? timeProvider = null) : IPodcastStateStore
{
  /// <summary>
  /// Filename of the per-podcast episode tracking manifest.
  /// </summary>
  public const string StateFileName = ".podcast_state.json";

  private static readonly JsonSerializerOptions SerializerOptions = new()
  {
    WriteIndented = true,
  };

  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public PodcastState Load(string podcastDir)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);

    var statePath = Path.Combine(podcastDir, StateFileName);

    if (File.Exists(statePath) is false)
    {
      return new PodcastState();
    }

    try
    {
      var json = File.ReadAllText(statePath);
      var loaded = JsonSerializer.Deserialize<PodcastState>(json, SerializerOptions);

      if (loaded is null)
      {
        return new PodcastState();
      }

      loaded.Episodes = loaded.Episodes is not null
        ? new Dictionary<string, PodcastEpisodeState>(loaded.Episodes, StringComparer.Ordinal)
        : new Dictionary<string, PodcastEpisodeState>(StringComparer.Ordinal);

      foreach (var entry in loaded.Episodes.Values)
      {
        entry.PlayedByUsersUtc ??= [];
      }

      loaded.DismissedEpisodes = loaded.DismissedEpisodes is not null
        ? new Dictionary<string, DismissedEpisodeState>(loaded.DismissedEpisodes, StringComparer.Ordinal)
        : new Dictionary<string, DismissedEpisodeState>(StringComparer.Ordinal);

      return loaded;
    }
    catch (JsonException)
    {
      return new PodcastState();
    }
    catch (IOException)
    {
      return new PodcastState();
    }
  }

  /// <inheritdoc />
  public void Save(string podcastDir, PodcastState state)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);
    ArgumentNullException.ThrowIfNull(state);

    Directory.CreateDirectory(podcastDir);

    var statePath = Path.Combine(podcastDir, StateFileName);
    var tempPath = Path.Combine(podcastDir, $"{StateFileName}.tmp");

    var json = JsonSerializer.Serialize(state, SerializerOptions);
    File.WriteAllText(tempPath, json);
    File.Move(tempPath, statePath, overwrite: true);
  }

  /// <inheritdoc />
  public bool IsEpisodeDownloaded(string podcastDir, string episodeGuid)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);

    if (string.IsNullOrWhiteSpace(episodeGuid))
    {
      return false;
    }

    var state = Load(podcastDir);

    if (
      state.Episodes.TryGetValue(episodeGuid, out var entry) is false ||
      string.IsNullOrWhiteSpace(entry.FileName)
    )
    {
      return false;
    }

    var filePath = Path.Combine(podcastDir, entry.FileName);
    return File.Exists(filePath);
  }

  /// <inheritdoc />
  [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Feed URLs are persisted directly as raw strings.")]
  public void RecordDownloadedEpisode(
    string podcastDir,
    string episodeGuid,
    string fileName,
    DateTime pubDate = default,
    string? feedId = null,
    string? feedUrl = null
  )
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);
    ArgumentException.ThrowIfNullOrWhiteSpace(episodeGuid);
    ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

    var state = Load(podcastDir);

    if (string.IsNullOrWhiteSpace(feedId) is false)
    {
      state.FeedId = feedId;
    }

    if (string.IsNullOrWhiteSpace(feedUrl) is false)
    {
      state.FeedUrl = feedUrl;
    }

    state.DismissedEpisodes.Remove(episodeGuid);

    state.Episodes[episodeGuid] = new PodcastEpisodeState
    {
      Guid = episodeGuid,
      FileName = fileName,
      PubDate = pubDate,
      DownloadedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
    };

    Save(podcastDir, state);
  }

  /// <inheritdoc />
  public void RemoveEpisode(string podcastDir, string episodeGuid)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);
    ArgumentException.ThrowIfNullOrWhiteSpace(episodeGuid);

    var state = Load(podcastDir);

    if (state.Episodes.Remove(episodeGuid))
    {
      Save(podcastDir, state);
    }
  }

  /// <inheritdoc />
  public bool IsEpisodeDismissed(string podcastDir, string episodeGuid, bool includeUserDeleted)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);

    if (string.IsNullOrWhiteSpace(episodeGuid))
    {
      return false;
    }

    var state = Load(podcastDir);

    if (state.DismissedEpisodes.TryGetValue(episodeGuid, out var entry) is false)
    {
      return false;
    }

    if (string.Equals(entry.Reason, DismissedEpisodeReason.Played, StringComparison.OrdinalIgnoreCase))
    {
      return true;
    }

    return includeUserDeleted;
  }

  /// <inheritdoc />
  public void RecordDismissedEpisode(string podcastDir, string episodeGuid, string reason)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);
    ArgumentException.ThrowIfNullOrWhiteSpace(episodeGuid);
    ArgumentException.ThrowIfNullOrWhiteSpace(reason);

    var state = Load(podcastDir);
    state.Episodes.Remove(episodeGuid);
    state.DismissedEpisodes[episodeGuid] = new DismissedEpisodeState
    {
      Guid = episodeGuid,
      Reason = reason,
      DismissedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
    };

    Save(podcastDir, state);
  }

  /// <inheritdoc />
  public string? FindEpisodeGuidByFileName(string podcastDir, string fileName)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);

    if (string.IsNullOrWhiteSpace(fileName))
    {
      return null;
    }

    var state = Load(podcastDir);

    foreach (var (guid, entry) in state.Episodes)
    {
      if (string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase))
      {
        return guid;
      }
    }

    return null;
  }

  /// <inheritdoc />
  public bool RecordUserPlayedState(
    string podcastDir,
    string episodeGuid,
    Guid userId,
    bool isPlayed,
    DateTime? playedAtUtc = null
  )
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);
    ArgumentException.ThrowIfNullOrWhiteSpace(episodeGuid);

    var state = Load(podcastDir);

    if (state.Episodes.TryGetValue(episodeGuid, out var entry) is false)
    {
      return false;
    }

    entry.PlayedByUsersUtc ??= [];

    if (isPlayed)
    {
      var timestamp = playedAtUtc?.ToUniversalTime() ?? _timeProvider.GetUtcNow().UtcDateTime;

      if (entry.PlayedByUsersUtc.TryGetValue(userId, out var existing) && existing == timestamp)
      {
        return false;
      }

      entry.PlayedByUsersUtc[userId] = timestamp;
      Save(podcastDir, state);
      return true;
    }

    if (entry.PlayedByUsersUtc.Remove(userId))
    {
      Save(podcastDir, state);
      return true;
    }

    return false;
  }

  /// <inheritdoc />
  public int ClearDismissedEpisodes(string podcastDir)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);

    var state = Load(podcastDir);

    if (state.DismissedEpisodes.Count is 0)
    {
      return 0;
    }

    var clearedCount = state.DismissedEpisodes.Count;
    state.DismissedEpisodes.Clear();
    Save(podcastDir, state);

    return clearedCount;
  }
}
using System.Diagnostics.CodeAnalysis;

using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Podcast.Configuration;

/// <summary>
/// Plugin configuration for the Podcast plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
  /// <summary>
  /// Gets or sets the list of podcast feed URLs.
  /// </summary>
  [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Required for Jellyfin XML and JSON configuration serialization.")]
  [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Required for Jellyfin XML and JSON configuration serialization.")]
  public List<PodcastFeed> PodcastFeeds { get; set; } = [];

  /// <summary>
  /// Gets or sets the library path where downloaded podcast audio files and cover art will be stored.
  /// </summary>
  public string LibraryPath { get; set; } = "/config/data/podcasts";

  /// <summary>
  /// Gets or sets a value indicating whether to enable automatic cleanup of old episodes.
  /// </summary>
  public bool EnableAutoCleanup { get; set; }

  /// <summary>
  /// Gets or sets the number of days to keep episodes before cleanup.
  /// </summary>
  public int DaysToKeepEpisodes { get; set; } = 30;

  /// <summary>
  /// Gets or sets the maximum number of episodes to keep per podcast.
  /// </summary>
  public int MaxEpisodesPerPodcast { get; set; } = 50;

  /// <summary>
  /// Gets or sets a value indicating whether to download and embed cover artwork.
  /// </summary>
  public bool DownloadThumbnails { get; set; } = true;
}

/// <summary>
/// Represents a podcast feed configuration.
/// </summary>
public class PodcastFeed
{
  /// <summary>
  /// Gets or sets the unique identifier for this feed.
  /// </summary>
  public string Id { get; set; } = Guid.NewGuid().ToString();

  /// <summary>
  /// Gets or sets the podcast feed URL.
  /// </summary>
  [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Serialized as string in Jellyfin configuration XML and web UI JSON.")]
  public string Url { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the display name for this podcast (optional, will use feed title if empty).
  /// </summary>
  public string? CustomName { get; set; }

  /// <summary>
  /// Gets or sets a value indicating whether this feed is enabled.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Gets or sets a value indicating whether auto cleanup is enabled for this feed (null = use global setting).
  /// </summary>
  public bool? EnableAutoCleanup { get; set; }

  /// <summary>
  /// Gets or sets the number of days to keep episodes for this feed (null = use global setting).
  /// </summary>
  public int? DaysToKeepEpisodes { get; set; }
}
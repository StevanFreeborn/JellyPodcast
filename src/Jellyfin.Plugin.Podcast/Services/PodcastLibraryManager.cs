using System.Diagnostics.CodeAnalysis;

using Jellyfin.Plugin.Podcast.Api;
using Jellyfin.Plugin.Podcast.Configuration;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcast.Services;

/// <summary>
/// Summary of filesystem changes performed during a podcast library refresh.
/// </summary>
public sealed class LibraryRefreshResult
{
  /// <summary>
  /// Gets the total number of newly downloaded episodes.
  /// </summary>
  public int EpisodesDownloaded { get; init; }

  /// <summary>
  /// Gets the total number of deleted episode audio files.
  /// </summary>
  public int EpisodesDeleted { get; init; }

  /// <summary>
  /// Gets a value indicating whether any audio files were added or removed on disk.
  /// </summary>
  public bool HasChanges => EpisodesDownloaded is > 0 || EpisodesDeleted is > 0;
}

/// <summary>
/// Manages the podcast library by downloading audio enclosures, embedding metadata tags, and pruning old episodes.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="PodcastLibraryManager"/> class.
/// </remarks>
/// <param name="feedParser">Podcast feed parser.</param>
/// <param name="mediaDownloader">HTTP media downloader.</param>
/// <param name="metadataTagger">Audio metadata tagger.</param>
/// <param name="stateStore">Episode state persistence store.</param>
/// <param name="logger">Logger instance.</param>
/// <param name="timeProvider">Optional time provider; defaults to <see cref="TimeProvider.System"/>.</param>
public partial class PodcastLibraryManager(
  IPodcastFeedParser feedParser,
  IPodcastMediaDownloader mediaDownloader,
  IAudioMetadataTagger metadataTagger,
  IPodcastStateStore stateStore,
  ILogger<PodcastLibraryManager> logger,
  TimeProvider? timeProvider = null
)
{
  private readonly IPodcastFeedParser _feedParser = feedParser;
  private readonly IPodcastMediaDownloader _mediaDownloader = mediaDownloader;
  private readonly IAudioMetadataTagger _metadataTagger = metadataTagger;
  private readonly IPodcastStateStore _stateStore = stateStore;
  private readonly ILogger<PodcastLibraryManager> _logger = logger;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <summary>
  /// Refreshes the podcast library using the active plugin configuration.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <param name="progress">Optional progress reporter (0 to 100).</param>
  /// <returns>A <see cref="LibraryRefreshResult"/> describing downloaded and deleted episodes.</returns>
  public Task<LibraryRefreshResult> RefreshLibraryAsync(CancellationToken cancellationToken, IProgress<double>? progress = null)
  {
    var config = Plugin.Instance?.Configuration;

    if (config is null)
    {
      LogConfigNull(_logger);
      progress?.Report(100);
      return Task.FromResult(new LibraryRefreshResult());
    }

    return RefreshLibraryAsync(config, cancellationToken, progress);
  }

  /// <summary>
  /// Refreshes the podcast library using the supplied <paramref name="config"/>.
  /// </summary>
  /// <param name="config">Plugin configuration to execute against.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <param name="progress">Optional progress reporter (0 to 100).</param>
  /// <returns>A <see cref="LibraryRefreshResult"/> describing downloaded and deleted episodes.</returns>
  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Feed processing errors are isolated and logged so remaining feeds continue syncing.")]
  public async Task<LibraryRefreshResult> RefreshLibraryAsync(
    PluginConfiguration config,
    CancellationToken cancellationToken,
    IProgress<double>? progress = null
  )
  {
    ArgumentNullException.ThrowIfNull(config);

    progress?.Report(0);

    var libraryPath = config.LibraryPath;

    if (string.IsNullOrWhiteSpace(libraryPath))
    {
      LogLibraryPathMissing(_logger);
      progress?.Report(100);
      return new LibraryRefreshResult();
    }

    Directory.CreateDirectory(libraryPath);

    var enabledFeeds = config.PodcastFeeds.Where(f => f.Enabled && string.IsNullOrWhiteSpace(f.Url) is false).ToList();
    LogRefreshStarting(_logger, enabledFeeds.Count);

    if (enabledFeeds.Count is 0)
    {
      progress?.Report(100);
      return new LibraryRefreshResult();
    }

    var totalDownloaded = 0;
    var totalDeleted = 0;

    for (var feedIndex = 0; feedIndex < enabledFeeds.Count; feedIndex++)
    {
      cancellationToken.ThrowIfCancellationRequested();

      var feedConfig = enabledFeeds[feedIndex];
      var sliceStart = (double)feedIndex / enabledFeeds.Count * 100.0;
      var sliceEnd = (double)(feedIndex + 1) / enabledFeeds.Count * 100.0;

      try
      {
        var feedResult = await ProcessPodcastFeedAsync(
          feedConfig,
          libraryPath,
          config,
          sliceStart,
          sliceEnd,
          progress,
          cancellationToken
        ).ConfigureAwait(false);

        totalDownloaded += feedResult.EpisodesDownloaded;
        totalDeleted += feedResult.EpisodesDeleted;
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        LogFeedError(_logger, feedConfig.Url, ex);
      }

      progress?.Report(sliceEnd);
    }

    progress?.Report(100);
    LogRefreshCompleted(_logger);

    return new LibraryRefreshResult
    {
      EpisodesDownloaded = totalDownloaded,
      EpisodesDeleted = totalDeleted,
    };
  }

  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Episode download/tagging errors are isolated and logged so remaining episodes continue syncing.")]
  private async Task<LibraryRefreshResult> ProcessPodcastFeedAsync(
    PodcastFeed feedConfig,
    string libraryPath,
    PluginConfiguration config,
    double sliceStart,
    double sliceEnd,
    IProgress<double>? progress,
    CancellationToken cancellationToken
  )
  {
    LogProcessingFeed(_logger, feedConfig.Url);

    var feedData = await _feedParser.ParseFeedAsync(feedConfig.Url, cancellationToken).ConfigureAwait(false);

    if (feedData is null)
    {
      LogFeedParseFailed(_logger, feedConfig.Url);
      return new LibraryRefreshResult();
    }

    var podcastTitle = string.IsNullOrWhiteSpace(feedConfig.CustomName) is false
      ? feedConfig.CustomName.Trim()
      : feedData.Title;

    var podcastDir = Path.Combine(libraryPath, AudioFileNaming.SanitizeFileName(podcastTitle));
    Directory.CreateDirectory(podcastDir);

    EnsureFeedIdentityPersisted(podcastDir, feedConfig);

    byte[]? coverArtBytes = null;

    if (config.DownloadThumbnails && string.IsNullOrWhiteSpace(feedData.ImageUrl) is false)
    {
      coverArtBytes = await _mediaDownloader.DownloadCoverArtAsync(
        feedData.ImageUrl,
        podcastDir,
        cancellationToken
      ).ConfigureAwait(false);
    }

    var enableCleanup = feedConfig.EnableAutoCleanup ?? config.EnableAutoCleanup;
    var daysToKeep = Math.Max(1, feedConfig.DaysToKeepEpisodes ?? config.DaysToKeepEpisodes);
    var cutoffUtc = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-daysToKeep);

    var maxEpisodes = config.MaxEpisodesPerPodcast is > 0 ? config.MaxEpisodesPerPodcast : int.MaxValue;
    var episodesToProcess = feedData.Episodes
      .OrderByDescending(e => e.PubDate)
      .Take(maxEpisodes)
      .ToList();

    var author = string.IsNullOrWhiteSpace(feedData.Author) is false
      ? feedData.Author
      : podcastTitle;

    var totalEpisodes = episodesToProcess.Count;
    var downloadedCount = 0;

    for (var index = 0; index < totalEpisodes; index++)
    {
      cancellationToken.ThrowIfCancellationRequested();

      var episode = episodesToProcess[index];
      var trackNumber = (uint)(index + 1);

      if (enableCleanup && IsEpisodeExpired(episode.PubDate, cutoffUtc))
      {
        LogSkippingExpired(_logger, episode.Title, episode.PubDate);
        ReportEpisodeProgress(progress, sliceStart, sliceEnd, index + 1, totalEpisodes);
        continue;
      }

      if (string.IsNullOrWhiteSpace(episode.MediaUrl))
      {
        LogSkippingMissingUrl(_logger, episode.Title);
        ReportEpisodeProgress(progress, sliceStart, sliceEnd, index + 1, totalEpisodes);
        continue;
      }

      var episodeGuid = string.IsNullOrWhiteSpace(episode.Guid) is false
        ? episode.Guid
        : episode.MediaUrl;

      if (_stateStore.IsEpisodeDownloaded(podcastDir, episodeGuid))
      {
        LogSkippingDownloaded(_logger, episode.Title);
        ReportEpisodeProgress(progress, sliceStart, sliceEnd, index + 1, totalEpisodes);
        continue;
      }

      try
      {
        var episodeCoverBytes = coverArtBytes;

        if (
          config.DownloadThumbnails &&
          string.IsNullOrWhiteSpace(episode.ImageUrl) is false &&
          string.Equals(episode.ImageUrl, feedData.ImageUrl, StringComparison.OrdinalIgnoreCase) is false
        )
        {
          episodeCoverBytes = await _mediaDownloader.DownloadImageBytesAsync(
            episode.ImageUrl,
            cancellationToken
          ).ConfigureAwait(false) ?? coverArtBytes;
        }

        await DownloadAndTagEpisodeAsync(
          podcastDir,
          podcastTitle,
          author,
          episode,
          episodeGuid,
          trackNumber,
          episodeCoverBytes,
          feedConfig,
          cancellationToken
        ).ConfigureAwait(false);

        downloadedCount++;
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        LogEpisodeError(_logger, episode.Title, ex);
      }

      ReportEpisodeProgress(progress, sliceStart, sliceEnd, index + 1, totalEpisodes);
    }

    var deletedCount = 0;

    if (enableCleanup)
    {
      deletedCount += PruneExpiredEpisodes(podcastDir, podcastTitle, cutoffUtc);
    }

    if (config.MaxEpisodesPerPodcast is > 0)
    {
      deletedCount += PruneExcessEpisodes(podcastDir, podcastTitle, config.MaxEpisodesPerPodcast);
    }

    return new LibraryRefreshResult
    {
      EpisodesDownloaded = downloadedCount,
      EpisodesDeleted = deletedCount,
    };
  }

  private async Task DownloadAndTagEpisodeAsync(
    string podcastDir,
    string podcastTitle,
    string author,
    PodcastEpisode episode,
    string episodeGuid,
    uint trackNumber,
    byte[]? coverArtBytes,
    PodcastFeed feedConfig,
    CancellationToken cancellationToken
  )
  {
    var fileName = AudioFileNaming.BuildEpisodeFileName(
      episode.PubDate,
      episode.Title,
      episode.MediaType,
      episode.MediaUrl
    );

    var destinationPath = Path.Combine(podcastDir, fileName);

    var responseContentType = await _mediaDownloader.DownloadEpisodeAsync(
      episode.MediaUrl,
      destinationPath,
      cancellationToken
    ).ConfigureAwait(false);

    // If the URL had no extension and the server sent a more specific audio MIME type, rename before tagging.
    if (string.IsNullOrWhiteSpace(responseContentType) is false)
    {
      var resolvedFileName = AudioFileNaming.BuildEpisodeFileName(
        episode.PubDate,
        episode.Title,
        responseContentType,
        episode.MediaUrl
      );

      if (string.Equals(resolvedFileName, fileName, StringComparison.OrdinalIgnoreCase) is false)
      {
        var resolvedPath = Path.Combine(podcastDir, resolvedFileName);
        File.Move(destinationPath, resolvedPath, overwrite: true);
        fileName = resolvedFileName;
        destinationPath = resolvedPath;
      }
    }

    var metadata = new EpisodeAudioMetadata
    {
      PodcastTitle = podcastTitle,
      Author = author,
      EpisodeTitle = episode.Title,
      TrackNumber = trackNumber,
      Year = episode.PubDate != DateTime.MinValue ? (uint)episode.PubDate.Year : 0u,
      Description = episode.Description,
      CoverArtBytes = coverArtBytes,
    };

    _metadataTagger.TagAudioFile(destinationPath, metadata);

    _stateStore.RecordDownloadedEpisode(
      podcastDir,
      episodeGuid,
      fileName,
      episode.PubDate,
      feedConfig.Id,
      feedConfig.Url
    );

    LogEpisodeDownloaded(_logger, fileName);
  }

  private void EnsureFeedIdentityPersisted(string podcastDir, PodcastFeed feedConfig)
  {
    var state = _stateStore.Load(podcastDir);
    var updated = false;

    if (
      string.IsNullOrWhiteSpace(feedConfig.Id) is false &&
      string.Equals(state.FeedId, feedConfig.Id, StringComparison.Ordinal) is false
    )
    {
      state.FeedId = feedConfig.Id;
      updated = true;
    }

    if (
      string.IsNullOrWhiteSpace(feedConfig.Url) is false &&
      string.Equals(state.FeedUrl, feedConfig.Url, StringComparison.Ordinal) is false
    )
    {
      state.FeedUrl = feedConfig.Url;
      updated = true;
    }

    if (updated)
    {
      _stateStore.Save(podcastDir, state);
    }
  }

  private int PruneExpiredEpisodes(string podcastDir, string podcastTitle, DateTime cutoffUtc)
  {
    if (Directory.Exists(podcastDir) is false)
    {
      return 0;
    }

    var state = _stateStore.Load(podcastDir);
    var trackedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var keysToRemove = new List<string>();
    var deletedCount = 0;

    foreach (var (guid, entry) in state.Episodes)
    {
      if (string.IsNullOrWhiteSpace(entry.FileName))
      {
        keysToRemove.Add(guid);
        continue;
      }

      var filePath = Path.Combine(podcastDir, entry.FileName);
      trackedFiles.Add(filePath);

      var effectiveDateUtc = entry.PubDate != DateTime.MinValue
        ? entry.PubDate.ToUniversalTime()
        : (File.Exists(filePath) ? File.GetLastWriteTimeUtc(filePath) : _timeProvider.GetUtcNow().UtcDateTime);

      if (effectiveDateUtc < cutoffUtc)
      {
        if (DeleteFileIfExists(filePath))
        {
          deletedCount++;
        }

        keysToRemove.Add(guid);
      }
    }

    if (keysToRemove.Count is > 0)
    {
      foreach (var key in keysToRemove)
      {
        state.Episodes.Remove(key);
      }

      _stateStore.Save(podcastDir, state);
    }

    foreach (var filePath in Directory.GetFiles(podcastDir))
    {
      if (AudioFileNaming.IsAudioFile(filePath) is false || trackedFiles.Contains(filePath))
      {
        continue;
      }

      if (File.GetLastWriteTimeUtc(filePath) < cutoffUtc && DeleteFileIfExists(filePath))
      {
        deletedCount++;
      }
    }

    if (deletedCount is > 0)
    {
      LogExpiredPruned(_logger, deletedCount, podcastTitle);
    }

    return deletedCount;
  }

  private int PruneExcessEpisodes(string podcastDir, string podcastTitle, int maxEpisodes)
  {
    if (Directory.Exists(podcastDir) is false || maxEpisodes is <= 0)
    {
      return 0;
    }

    var state = _stateStore.Load(podcastDir);

    if (state.Episodes.Count <= maxEpisodes)
    {
      return 0;
    }

    var ordered = state.Episodes
      .OrderByDescending(kvp => kvp.Value.PubDate != DateTime.MinValue ? kvp.Value.PubDate : kvp.Value.DownloadedAtUtc)
      .ThenByDescending(kvp => kvp.Value.DownloadedAtUtc)
      .ToList();

    var toPrune = ordered.Skip(maxEpisodes).ToList();

    if (toPrune.Count is 0)
    {
      return 0;
    }

    var deletedCount = 0;

    foreach (var (guid, entry) in toPrune)
    {
      if (string.IsNullOrWhiteSpace(entry.FileName) is false)
      {
        var filePath = Path.Combine(podcastDir, entry.FileName);

        if (DeleteFileIfExists(filePath))
        {
          deletedCount++;
        }
      }

      state.Episodes.Remove(guid);
    }

    _stateStore.Save(podcastDir, state);

    if (deletedCount is > 0)
    {
      LogExcessPruned(_logger, deletedCount, podcastTitle, maxEpisodes);
    }

    return deletedCount;
  }

  private static void ReportEpisodeProgress(
    IProgress<double>? progress,
    double sliceStart,
    double sliceEnd,
    int completedEpisodes,
    int totalEpisodes
  )
  {
    if (progress is null || totalEpisodes is <= 0)
    {
      return;
    }

    var fraction = (double)completedEpisodes / totalEpisodes;
    progress.Report(sliceStart + ((sliceEnd - sliceStart) * fraction));
  }

  private static bool IsEpisodeExpired(DateTime pubDate, DateTime cutoffUtc)
    => pubDate != DateTime.MinValue && pubDate.ToUniversalTime() < cutoffUtc;

  private static bool DeleteFileIfExists(string path)
  {
    if (File.Exists(path) is false)
    {
      return false;
    }

    File.Delete(path);
    return true;
  }

  [LoggerMessage(Level = LogLevel.Warning, Message = "Plugin configuration is null")]
  private static partial void LogConfigNull(ILogger logger);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Library path is not configured")]
  private static partial void LogLibraryPathMissing(ILogger logger);

  [LoggerMessage(Level = LogLevel.Information, Message = "Starting podcast library refresh. Processing {Count} enabled feeds")]
  private static partial void LogRefreshStarting(ILogger logger, int count);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error processing podcast feed: {Url}")]
  private static partial void LogFeedError(ILogger logger, string url, Exception exception);

  [LoggerMessage(Level = LogLevel.Information, Message = "Podcast library refresh completed")]
  private static partial void LogRefreshCompleted(ILogger logger);

  [LoggerMessage(Level = LogLevel.Information, Message = "Processing podcast feed: {Url}")]
  private static partial void LogProcessingFeed(ILogger logger, string url);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to parse feed: {Url}")]
  private static partial void LogFeedParseFailed(ILogger logger, string url);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping expired episode {Title} published {PubDate}")]
  private static partial void LogSkippingExpired(ILogger logger, string title, DateTime pubDate);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping episode without enclosure URL: {Title}")]
  private static partial void LogSkippingMissingUrl(ILogger logger, string title);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping already downloaded episode: {Title}")]
  private static partial void LogSkippingDownloaded(ILogger logger, string title);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error downloading or tagging episode: {Title}")]
  private static partial void LogEpisodeError(ILogger logger, string title, Exception exception);

  [LoggerMessage(Level = LogLevel.Information, Message = "Downloaded and tagged episode: {FileName}")]
  private static partial void LogEpisodeDownloaded(ILogger logger, string fileName);

  [LoggerMessage(Level = LogLevel.Information, Message = "Auto-cleanup removed {Count} expired episodes from {Podcast}")]
  private static partial void LogExpiredPruned(ILogger logger, int count, string podcast);

  [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Count} oldest episodes from {Podcast} (limit: {Limit})")]
  private static partial void LogExcessPruned(ILogger logger, int count, string podcast, int limit);
}
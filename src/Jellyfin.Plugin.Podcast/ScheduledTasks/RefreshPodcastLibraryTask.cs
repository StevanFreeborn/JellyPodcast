using Jellyfin.Plugin.Podcast.Api;
using Jellyfin.Plugin.Podcast.Services;

using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcast.ScheduledTasks;

/// <summary>
/// Scheduled task to refresh the podcast library.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="RefreshPodcastLibraryTask"/> class.
/// </remarks>
/// <param name="httpClientFactory">HTTP client factory.</param>
/// <param name="loggerFactory">Logger factory.</param>
/// <param name="libraryManager">Optional Jellyfin library manager used to queue a library scan when files change.</param>
/// <param name="timeProvider">Optional time provider; defaults to <see cref="TimeProvider.System"/>.</param>
public partial class RefreshPodcastLibraryTask(
  IHttpClientFactory httpClientFactory,
  ILoggerFactory loggerFactory,
  ILibraryManager? libraryManager = null,
  TimeProvider? timeProvider = null
) : IScheduledTask
{
  private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
  private readonly ILoggerFactory _loggerFactory = loggerFactory;
  private readonly ILibraryManager? _libraryManager = libraryManager;
  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public string Name => "Refresh Podcast Library";

  /// <inheritdoc />
  public string Description => "Fetches new episodes from configured podcast feeds, downloads audio files, and embeds metadata";

  /// <inheritdoc />
  public string Category => "Podcast";

  /// <inheritdoc />
  public string Key => "RefreshPodcastLibrary";

  /// <inheritdoc />
  public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
  {
    var logger = _loggerFactory.CreateLogger<RefreshPodcastLibraryTask>();
    LogTaskStarting(logger);

    progress?.Report(0);

    try
    {
      var feedParser = new PodcastFeedParser(_httpClientFactory, _loggerFactory.CreateLogger<PodcastFeedParser>());
      var mediaDownloader = new PodcastMediaDownloader(_httpClientFactory, _loggerFactory.CreateLogger<PodcastMediaDownloader>());
      var metadataTagger = new AudioMetadataTagger();
      var stateStore = new PodcastStateStore(_timeProvider);
      var libraryManager = new PodcastLibraryManager(
        feedParser,
        mediaDownloader,
        metadataTagger,
        stateStore,
        _loggerFactory.CreateLogger<PodcastLibraryManager>(),
        _timeProvider
      );

      var result = await libraryManager.RefreshLibraryAsync(cancellationToken, progress).ConfigureAwait(false);

      if (result.HasChanges && _libraryManager is not null)
      {
        LogQueueingLibraryScan(logger, result.EpisodesDownloaded, result.EpisodesDeleted);
        _libraryManager.QueueLibraryScan();
      }

      progress?.Report(100);

      LogTaskCompleted(logger);
    }
    catch (Exception ex)
    {
      LogTaskFailed(logger, ex);
      throw;
    }
  }

  /// <inheritdoc />
  public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
  {
    return
    [
      new TaskTriggerInfo
      {
        Type = TaskTriggerInfoType.IntervalTrigger,
        IntervalTicks = TimeSpan.FromHours(2).Ticks,
      },
    ];
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "Starting podcast library refresh task")]
  private static partial void LogTaskStarting(ILogger logger);

  [LoggerMessage(Level = LogLevel.Information, Message = "Podcast library refresh task completed successfully")]
  private static partial void LogTaskCompleted(ILogger logger);

  [LoggerMessage(Level = LogLevel.Information, Message = "Queueing Jellyfin library scan after podcast sync ({Downloaded} downloaded, {Deleted} deleted)")]
  private static partial void LogQueueingLibraryScan(ILogger logger, int downloaded, int deleted);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error during podcast library refresh task")]
  private static partial void LogTaskFailed(ILogger logger, Exception exception);
}
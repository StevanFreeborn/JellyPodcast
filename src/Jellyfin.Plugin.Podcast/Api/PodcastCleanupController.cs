using System.Diagnostics.CodeAnalysis;

using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Services;

using MediaBrowser.Controller.Library;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcast.Api;

/// <summary>
/// Result payload returned by podcast cleanup endpoints.
/// </summary>
public class CleanupResult
{
  /// <summary>
  /// Gets or sets the total number of files deleted.
  /// </summary>
  public int FilesDeleted { get; set; }

  /// <summary>
  /// Gets or sets the total number of directories deleted.
  /// </summary>
  public int DirectoriesDeleted { get; set; }
}

/// <summary>
/// Controller for podcast maintenance and cleanup operations.
/// </summary>
[ApiController]
[Route("Podcast")]
[Authorize(Policy = "RequiresElevation")]
public partial class PodcastCleanupController : ControllerBase
{
  private readonly ILogger<PodcastCleanupController> _logger;
  private readonly Func<PluginConfiguration?> _configProvider;
  private readonly IPodcastStateStore _stateStore;
  private readonly ILibraryManager? _libraryManager;

  /// <summary>
  /// Initializes a new instance of the <see cref="PodcastCleanupController"/> class.
  /// </summary>
  /// <param name="logger">Instance of the <see cref="ILogger{PodcastCleanupController}"/> interface.</param>
  /// <param name="libraryManager">Optional Jellyfin library manager used to queue a library scan when files are removed.</param>
  [ActivatorUtilitiesConstructor]
  public PodcastCleanupController(ILogger<PodcastCleanupController> logger, ILibraryManager? libraryManager = null) : this(
    logger,
    () => Plugin.Instance?.Configuration,
    new PodcastStateStore(),
    libraryManager
  )
  {
  }

  internal PodcastCleanupController(
    ILogger<PodcastCleanupController> logger,
    Func<PluginConfiguration?> configProvider,
    IPodcastStateStore? stateStore = null,
    ILibraryManager? libraryManager = null
  )
  {
    _logger = logger;
    _configProvider = configProvider;
    _stateStore = stateStore ?? new PodcastStateStore();
    _libraryManager = libraryManager;
  }

  /// <summary>
  /// Cleans up all downloaded episode audio files and state manifests while keeping podcast cover art for configured feeds,
  /// and removes orphaned directories for feeds that are no longer configured.
  /// </summary>
  /// <returns>A <see cref="CleanupResult"/> summary on success.</returns>
  [HttpPost("Cleanup")]
  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Controller boundary logs unexpected filesystem errors and returns HTTP 500.")]
  public IActionResult CleanupEpisodes()
  {
    try
    {
      LogStartingCleanup(_logger);

      var config = _configProvider();

      if (config is null)
      {
        LogConfigNull(_logger);
        return StatusCode(500, "Plugin configuration not available");
      }

      var libraryPath = config.LibraryPath;

      if (string.IsNullOrWhiteSpace(libraryPath) || Directory.Exists(libraryPath) is false)
      {
        LogLibraryPathMissing(_logger, libraryPath);
        return NotFound("Library path not found");
      }

      var filesDeleted = 0;
      var directoriesDeleted = 0;

      foreach (var podcastDir in Directory.GetDirectories(libraryPath))
      {
        if (BelongsToConfiguredFeed(podcastDir, config) is false)
        {
          var removedFiles = DeleteDirectoryRecursively(podcastDir);
          filesDeleted += removedFiles;
          directoriesDeleted++;
          LogDeletedOrphanDir(_logger, podcastDir);
          continue;
        }

        foreach (var file in Directory.GetFiles(podcastDir))
        {
          var fileName = Path.GetFileName(file);

          if (
            AudioFileNaming.IsAudioFile(file) ||
            string.Equals(fileName, PodcastStateStore.StateFileName, StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
          )
          {
            System.IO.File.Delete(file);
            filesDeleted++;
            LogDeletedFile(_logger, file);
          }
        }
      }

      if ((filesDeleted is > 0 || directoriesDeleted is > 0) && _libraryManager is not null)
      {
        _libraryManager.QueueLibraryScan();
      }

      LogCleanupCompleted(_logger, filesDeleted, directoriesDeleted);

      return Ok(
        new CleanupResult
        {
          FilesDeleted = filesDeleted,
          DirectoriesDeleted = directoriesDeleted,
        }
      );
    }
    catch (Exception ex)
    {
      LogCleanupError(_logger, ex);
      return StatusCode(500, "Cleanup failed");
    }
  }

  /// <summary>
  /// Removes podcast directories and downloaded files that no longer belong to any configured feed.
  /// </summary>
  /// <returns>A <see cref="CleanupResult"/> summary on success.</returns>
  [HttpPost("CleanupOrphans")]
  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Controller boundary logs unexpected filesystem errors and returns HTTP 500.")]
  public IActionResult CleanupOrphanedFeeds()
  {
    try
    {
      LogStartingOrphanCleanup(_logger);

      var config = _configProvider();

      if (config is null)
      {
        LogConfigNull(_logger);
        return StatusCode(500, "Plugin configuration not available");
      }

      var libraryPath = config.LibraryPath;

      if (string.IsNullOrWhiteSpace(libraryPath) || Directory.Exists(libraryPath) is false)
      {
        LogLibraryPathMissing(_logger, libraryPath);
        return NotFound("Library path not found");
      }

      var filesDeleted = 0;
      var directoriesDeleted = 0;

      foreach (var podcastDir in Directory.GetDirectories(libraryPath))
      {
        if (BelongsToConfiguredFeed(podcastDir, config))
        {
          continue;
        }

        filesDeleted += DeleteDirectoryRecursively(podcastDir);
        directoriesDeleted++;
        LogDeletedOrphanDir(_logger, podcastDir);
      }

      if ((filesDeleted is > 0 || directoriesDeleted is > 0) && _libraryManager is not null)
      {
        _libraryManager.QueueLibraryScan();
      }

      LogOrphanCleanupCompleted(_logger, filesDeleted, directoriesDeleted);

      return Ok(
        new CleanupResult
        {
          FilesDeleted = filesDeleted,
          DirectoriesDeleted = directoriesDeleted,
        }
      );
    }
    catch (Exception ex)
    {
      LogOrphanCleanupError(_logger, ex);
      return StatusCode(500, "Orphan cleanup failed");
    }
  }

  private bool BelongsToConfiguredFeed(string podcastDir, PluginConfiguration config)
  {
    var state = _stateStore.Load(podcastDir);
    var dirName = Path.GetFileName(podcastDir);

    return config.PodcastFeeds.Any(feed =>
      (string.IsNullOrWhiteSpace(state.FeedId) is false && string.Equals(feed.Id, state.FeedId, StringComparison.OrdinalIgnoreCase)) ||
      (string.IsNullOrWhiteSpace(state.FeedUrl) is false && string.Equals(feed.Url, state.FeedUrl, StringComparison.OrdinalIgnoreCase)) ||
      (string.IsNullOrWhiteSpace(feed.CustomName) is false && string.Equals(AudioFileNaming.SanitizeFileName(feed.CustomName), dirName, StringComparison.OrdinalIgnoreCase))
    );
  }

  private static int DeleteDirectoryRecursively(string directoryPath)
  {
    var fileCount = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories).Length;
    Directory.Delete(directoryPath, recursive: true);
    return fileCount;
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "Starting podcast cleanup")]
  private static partial void LogStartingCleanup(ILogger logger);

  [LoggerMessage(Level = LogLevel.Information, Message = "Starting orphaned podcast feed cleanup")]
  private static partial void LogStartingOrphanCleanup(ILogger logger);

  [LoggerMessage(Level = LogLevel.Error, Message = "Plugin configuration is null")]
  private static partial void LogConfigNull(ILogger logger);

  [LoggerMessage(Level = LogLevel.Error, Message = "Library path does not exist: {Path}")]
  private static partial void LogLibraryPathMissing(ILogger logger, string? path);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Deleted orphaned podcast directory: {Dir}")]
  private static partial void LogDeletedOrphanDir(ILogger logger, string dir);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Deleted: {File}")]
  private static partial void LogDeletedFile(ILogger logger, string file);

  [LoggerMessage(Level = LogLevel.Information, Message = "Cleanup completed. Deleted {FileCount} files and {DirectoryCount} directories")]
  private static partial void LogCleanupCompleted(ILogger logger, int fileCount, int directoryCount);

  [LoggerMessage(Level = LogLevel.Information, Message = "Orphan cleanup completed. Deleted {FileCount} files and {DirectoryCount} directories")]
  private static partial void LogOrphanCleanupCompleted(ILogger logger, int fileCount, int directoryCount);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error during cleanup")]
  private static partial void LogCleanupError(ILogger logger, Exception exception);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error during orphaned feed cleanup")]
  private static partial void LogOrphanCleanupError(ILogger logger, Exception exception);
}
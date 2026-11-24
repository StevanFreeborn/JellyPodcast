using System.Diagnostics.CodeAnalysis;

using Jellyfin.Plugin.Podcast.Configuration;

using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcast.Services;

/// <summary>
/// Monitors Jellyfin library deletions and user play-state updates to maintain episode tombstones
/// and accurate played timestamps inside <c>.podcast_state.json</c>.
/// </summary>
public sealed partial class PodcastLifecycleMonitor : IHostedService
{
  private readonly ILibraryManager _libraryManager;
  private readonly IUserDataManager _userDataManager;
  private readonly ILogger<PodcastLifecycleMonitor> _logger;
  private readonly Func<PluginConfiguration?> _configProvider;
  private readonly IPodcastStateStore _stateStore;
  private readonly Lock _stateSyncLock = new();

  /// <summary>
  /// Initializes a new instance of the <see cref="PodcastLifecycleMonitor"/> class.
  /// </summary>
  /// <param name="libraryManager">Jellyfin library manager.</param>
  /// <param name="userDataManager">Jellyfin user data manager.</param>
  /// <param name="logger">Logger instance.</param>
  [ActivatorUtilitiesConstructor]
  public PodcastLifecycleMonitor(
    ILibraryManager libraryManager,
    IUserDataManager userDataManager,
    ILogger<PodcastLifecycleMonitor> logger
  )
    : this(
      libraryManager,
      userDataManager,
      logger,
      () => Plugin.Instance?.Configuration,
      new PodcastStateStore()
    )
  {
  }

  internal PodcastLifecycleMonitor(
    ILibraryManager libraryManager,
    IUserDataManager userDataManager,
    ILogger<PodcastLifecycleMonitor> logger,
    Func<PluginConfiguration?> configProvider,
    IPodcastStateStore stateStore
  )
  {
    _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
  }

  /// <inheritdoc />
  public Task StartAsync(CancellationToken cancellationToken)
  {
    _libraryManager.ItemRemoved += OnItemRemoved;
    _userDataManager.UserDataSaved += OnUserDataSaved;
    return Task.CompletedTask;
  }

  /// <inheritdoc />
  public Task StopAsync(CancellationToken cancellationToken)
  {
    _libraryManager.ItemRemoved -= OnItemRemoved;
    _userDataManager.UserDataSaved -= OnUserDataSaved;
    return Task.CompletedTask;
  }

  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Jellyfin event callbacks must isolate unexpected errors to protect server operations.")]
  private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
  {
    try
    {
      if (e.Item is not Audio audio || audio.IsShortcut || string.IsNullOrWhiteSpace(audio.Path))
      {
        return;
      }

      var config = _configProvider();

      if (config is null || IsWithinLibraryPath(audio.Path, config.LibraryPath) is false)
      {
        return;
      }

      var podcastDir = Path.GetDirectoryName(audio.Path);
      var fileName = Path.GetFileName(audio.Path);

      if (
        string.IsNullOrWhiteSpace(podcastDir) ||
        string.IsNullOrWhiteSpace(fileName) ||
        Directory.Exists(podcastDir) is false
      )
      {
        return;
      }

      lock (_stateSyncLock)
      {
        var episodeGuid = _stateStore.FindEpisodeGuidByFileName(podcastDir, fileName);

        if (episodeGuid is null)
        {
          return;
        }

        if (File.Exists(audio.Path) is false)
        {
          PodcastLibraryManager.DeleteEpisodeSidecarImages(audio.Path);
        }

        if (config.SkipDeletedEpisodes)
        {
          _stateStore.RecordDismissedEpisode(podcastDir, episodeGuid, DismissedEpisodeReason.UserDeleted);
          LogEpisodeDismissed(_logger, fileName, episodeGuid);
        }
        else
        {
          _stateStore.RemoveEpisode(podcastDir, episodeGuid);
          LogEpisodeUntracked(_logger, fileName, episodeGuid);
        }
      }
    }
    catch (Exception ex)
    {
      LogItemRemovedError(_logger, ex);
    }
  }

  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Jellyfin event callbacks must isolate unexpected errors to protect server operations.")]
  private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
  {
    try
    {
      if (
        e.SaveReason is not (
          UserDataSaveReason.PlaybackFinished or
          UserDataSaveReason.TogglePlayed or
          UserDataSaveReason.UpdateUserData or
          UserDataSaveReason.Import
        )
      )
      {
        return;
      }

      if (e.Item is not Audio audio || audio.IsShortcut || string.IsNullOrWhiteSpace(audio.Path))
      {
        return;
      }

      var config = _configProvider();

      if (config is null || IsWithinLibraryPath(audio.Path, config.LibraryPath) is false)
      {
        return;
      }

      var podcastDir = Path.GetDirectoryName(audio.Path);
      var fileName = Path.GetFileName(audio.Path);

      if (
        string.IsNullOrWhiteSpace(podcastDir) ||
        string.IsNullOrWhiteSpace(fileName) ||
        Directory.Exists(podcastDir) is false
      )
      {
        return;
      }

      lock (_stateSyncLock)
      {
        var episodeGuid = _stateStore.FindEpisodeGuidByFileName(podcastDir, fileName);

        if (episodeGuid is null)
        {
          return;
        }

        _stateStore.RecordUserPlayedState(
          podcastDir,
          episodeGuid,
          e.UserId,
          e.UserData.Played,
          e.UserData.LastPlayedDate?.ToUniversalTime()
        );
      }
    }
    catch (Exception ex)
    {
      LogUserDataSavedError(_logger, ex);
    }
  }

  private static bool IsWithinLibraryPath(string itemPath, string? libraryPath)
  {
    if (
      string.IsNullOrWhiteSpace(libraryPath) ||
      Path.IsPathFullyQualified(itemPath) is false ||
      Path.IsPathFullyQualified(libraryPath) is false
    )
    {
      return false;
    }

    var normalizedLibrary = Path.GetFullPath(libraryPath)
      .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var libraryPrefix = $"{normalizedLibrary}{Path.DirectorySeparatorChar}";
    var normalizedItem = Path.GetFullPath(itemPath);

    return normalizedItem.StartsWith(libraryPrefix, StringComparison.OrdinalIgnoreCase);
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "Recorded dismissal tombstone for user-deleted episode {FileName} ({EpisodeGuid})")]
  private static partial void LogEpisodeDismissed(ILogger logger, string fileName, string episodeGuid);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Removed untracked state entry for deleted episode {FileName} ({EpisodeGuid})")]
  private static partial void LogEpisodeUntracked(ILogger logger, string fileName, string episodeGuid);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error handling Jellyfin library item removal event")]
  private static partial void LogItemRemovedError(ILogger logger, Exception exception);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error handling Jellyfin user data save event")]
  private static partial void LogUserDataSavedError(ILogger logger, Exception exception);
}
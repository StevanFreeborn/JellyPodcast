using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Podcast.Configuration;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Podcast.Services;

/// <summary>
/// Result of evaluating whether a downloaded podcast episode is eligible for played-episode auto-cleanup.
/// </summary>
public sealed class PlayedEpisodeEvaluation
{
  /// <summary>
  /// Gets a value indicating whether the episode has satisfied user play and retention rules and can be deleted.
  /// </summary>
  public bool ShouldDelete { get; init; }

  /// <summary>
  /// Gets a value indicating whether any eligible user marked the episode as a favorite, protecting it from removal.
  /// </summary>
  public bool IsProtectedByFavorite { get; init; }

  /// <summary>
  /// Gets per-user UTC timestamps newly observed during evaluation when Jellyfin's <c>LastPlayedDate</c> was absent.
  /// </summary>
  public IReadOnlyDictionary<Guid, DateTime> ObservedPlayedByUsersUtc { get; init; } = new Dictionary<Guid, DateTime>();
}

/// <summary>
/// Evaluates Jellyfin user play/favorite state to determine when podcast episodes can be auto-removed.
/// </summary>
public interface IPodcastPlayedEpisodeEvaluator
{
  /// <summary>
  /// Evaluates <paramref name="audioFilePath"/> against enabled Jellyfin users and retention rules.
  /// </summary>
  /// <param name="audioFilePath">Full local path to the downloaded episode audio file.</param>
  /// <param name="episodeState">Persisted episode state entry.</param>
  /// <param name="scope">Multi-user completion scope (<see cref="PlayedEpisodeCleanupScope.AllUsers"/> or <see cref="PlayedEpisodeCleanupScope.AnyUser"/>).</param>
  /// <param name="retentionHours">Minimum delay in hours after playback completion before deleting.</param>
  /// <param name="utcNow">Current UTC timestamp.</param>
  /// <returns>The evaluation outcome.</returns>
  PlayedEpisodeEvaluation Evaluate(
    string audioFilePath,
    PodcastEpisodeState episodeState,
    PlayedEpisodeCleanupScope scope,
    int retentionHours,
    DateTime utcNow
  );
}

/// <summary>
/// Default <see cref="IPodcastPlayedEpisodeEvaluator"/> backed by Jellyfin's library, user, and user-data managers.
/// </summary>
public class PodcastPlayedEpisodeEvaluator : IPodcastPlayedEpisodeEvaluator
{
  private readonly ILibraryManager _libraryManager;
  private readonly IUserManager _userManager;
  private readonly IUserDataManager _userDataManager;
  private readonly Func<BaseItem, User, bool> _isItemVisible;

  /// <summary>
  /// Initializes a new instance of the <see cref="PodcastPlayedEpisodeEvaluator"/> class.
  /// </summary>
  /// <param name="libraryManager">Jellyfin library manager.</param>
  /// <param name="userManager">Jellyfin user manager.</param>
  /// <param name="userDataManager">Jellyfin user data manager.</param>
  public PodcastPlayedEpisodeEvaluator(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager
  )
    : this(libraryManager, userManager, userDataManager, (item, user) => item.IsVisibleStandalone(user))
  {
  }

  internal PodcastPlayedEpisodeEvaluator(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    Func<BaseItem, User, bool> isItemVisible
  )
  {
    _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
    _userDataManager = userDataManager ?? throw new ArgumentNullException(nameof(userDataManager));
    _isItemVisible = isItemVisible ?? throw new ArgumentNullException(nameof(isItemVisible));
  }

  /// <inheritdoc />
  public PlayedEpisodeEvaluation Evaluate(
    string audioFilePath,
    PodcastEpisodeState episodeState,
    PlayedEpisodeCleanupScope scope,
    int retentionHours,
    DateTime utcNow
  )
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
    ArgumentNullException.ThrowIfNull(episodeState);

    var item = _libraryManager.FindByPath(audioFilePath, isFolder: false);

    if (item is null)
    {
      return new PlayedEpisodeEvaluation();
    }

    var eligibleUsers = _userManager.GetUsers()
      .Where(user => IsUserEnabled(user) && _isItemVisible(item, user))
      .ToList();

    if (eligibleUsers.Count is 0)
    {
      return new PlayedEpisodeEvaluation();
    }

    var playedTimestamps = new Dictionary<Guid, DateTime>();
    var newlyObserved = new Dictionary<Guid, DateTime>();

    foreach (var user in eligibleUsers)
    {
      var userData = _userDataManager.GetUserData(user, item);

      if (userData is null)
      {
        continue;
      }

      if (userData.IsFavorite)
      {
        return new PlayedEpisodeEvaluation
        {
          IsProtectedByFavorite = true,
          ObservedPlayedByUsersUtc = newlyObserved,
        };
      }

      if (userData.Played is false)
      {
        continue;
      }

      DateTime effectivePlayedUtc;

      if (userData.LastPlayedDate is DateTime lastPlayed)
      {
        effectivePlayedUtc = lastPlayed.ToUniversalTime();
      }
      else if (episodeState.PlayedByUsersUtc.TryGetValue(user.Id, out var recordedUtc))
      {
        effectivePlayedUtc = recordedUtc.ToUniversalTime();
      }
      else
      {
        effectivePlayedUtc = utcNow;
        newlyObserved[user.Id] = utcNow;
      }

      playedTimestamps[user.Id] = effectivePlayedUtc;
    }

    var retention = TimeSpan.FromHours(Math.Max(0, retentionHours));
    var shouldDelete = scope switch
    {
      PlayedEpisodeCleanupScope.AnyUser => playedTimestamps.Values.Any(ts => utcNow - ts >= retention),
      _ => playedTimestamps.Count == eligibleUsers.Count && playedTimestamps.Values.All(ts => utcNow - ts >= retention),
    };

    return new PlayedEpisodeEvaluation
    {
      ShouldDelete = shouldDelete,
      ObservedPlayedByUsersUtc = newlyObserved,
    };
  }

  private static bool IsUserEnabled(User user) =>
    user.Permissions.Any(p => p.Kind is PermissionKind.IsDisabled && p.Value) is false;
}
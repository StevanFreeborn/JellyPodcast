using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Services;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

using Moq;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class PodcastPlayedEpisodeEvaluatorTests
{
  private static readonly DateTime NowUtc = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
  private const string SampleAudioPath = "/podcasts/Show/ep1.mp3";

  [Fact]
  public void Evaluate_AllUsersAndAnyUserScopes_RespectMultiUserCompletion()
  {
    var alice = new User("alice", "default", "default");
    var bob = new User("bob", "default", "default");
    var item = new AudioBook { Path = SampleAudioPath };

    var userDataMap = new Dictionary<Guid, UserItemData>
    {
      [alice.Id] = new() { Key = SampleAudioPath, Played = true, LastPlayedDate = NowUtc.AddHours(-30) },
      [bob.Id] = new() { Key = SampleAudioPath, Played = false },
    };

    var evaluator = CreateEvaluator(item, [alice, bob], userDataMap, visibleUserIds: [alice.Id, bob.Id]);
    var state = new PodcastEpisodeState { Guid = "ep-1", FileName = "ep1.mp3" };

    var allUsersBeforeBob = evaluator.Evaluate(SampleAudioPath, state, PlayedEpisodeCleanupScope.AllUsers, 24, NowUtc);
    Assert.False(allUsersBeforeBob.ShouldDelete);

    var anyUserReady = evaluator.Evaluate(SampleAudioPath, state, PlayedEpisodeCleanupScope.AnyUser, 24, NowUtc);
    Assert.True(anyUserReady.ShouldDelete);

    userDataMap[bob.Id] = new() { Key = SampleAudioPath, Played = true, LastPlayedDate = NowUtc.AddHours(-10) };
    var allUsersRecentBob = evaluator.Evaluate(SampleAudioPath, state, PlayedEpisodeCleanupScope.AllUsers, 24, NowUtc);
    Assert.False(allUsersRecentBob.ShouldDelete);

    userDataMap[bob.Id] = new() { Key = SampleAudioPath, Played = true, LastPlayedDate = NowUtc.AddHours(-25) };
    var allUsersReady = evaluator.Evaluate(SampleAudioPath, state, PlayedEpisodeCleanupScope.AllUsers, 24, NowUtc);
    Assert.True(allUsersReady.ShouldDelete);
  }

  [Fact]
  public void Evaluate_IgnoresDisabledAndInvisibleUsers_AndProtectsFavorites()
  {
    var alice = new User("alice", "default", "default");
    var disabledUser = new User("disabled", "default", "default");
    disabledUser.Permissions.Add(new Permission(PermissionKind.IsDisabled, true));
    var hiddenLibraryUser = new User("hidden", "default", "default");

    var item = new AudioBook { Path = SampleAudioPath };
    var userDataMap = new Dictionary<Guid, UserItemData>
    {
      [alice.Id] = new() { Key = SampleAudioPath, Played = true, LastPlayedDate = NowUtc.AddHours(-25), IsFavorite = false },
      [disabledUser.Id] = new() { Key = SampleAudioPath, Played = false },
      [hiddenLibraryUser.Id] = new() { Key = SampleAudioPath, Played = false },
    };

    var evaluator = CreateEvaluator(
      item,
      [alice, disabledUser, hiddenLibraryUser],
      userDataMap,
      visibleUserIds: [alice.Id, disabledUser.Id]
    );
    var state = new PodcastEpisodeState { Guid = "ep-1", FileName = "ep1.mp3" };

    Assert.True(evaluator.Evaluate(SampleAudioPath, state, PlayedEpisodeCleanupScope.AllUsers, 24, NowUtc).ShouldDelete);

    userDataMap[alice.Id].IsFavorite = true;
    var favResult = evaluator.Evaluate(SampleAudioPath, state, PlayedEpisodeCleanupScope.AnyUser, 0, NowUtc);
    Assert.False(favResult.ShouldDelete);
    Assert.True(favResult.IsProtectedByFavorite);
  }

  [Fact]
  public void Evaluate_UsesStateRecordedTimestampOrSeedsObservation_WhenLastPlayedDateIsNull()
  {
    var alice = new User("alice", "default", "default");
    var item = new AudioBook { Path = SampleAudioPath };
    var userDataMap = new Dictionary<Guid, UserItemData>
    {
      [alice.Id] = new() { Key = SampleAudioPath, Played = true, LastPlayedDate = null },
    };

    var evaluator = CreateEvaluator(item, [alice], userDataMap, visibleUserIds: [alice.Id]);

    var freshState = new PodcastEpisodeState { Guid = "ep-1", FileName = "ep1.mp3" };
    var firstCheck = evaluator.Evaluate(SampleAudioPath, freshState, PlayedEpisodeCleanupScope.AllUsers, 24, NowUtc);
    Assert.False(firstCheck.ShouldDelete);
    Assert.Equal(NowUtc, firstCheck.ObservedPlayedByUsersUtc[alice.Id]);

    var zeroDelayCheck = evaluator.Evaluate(SampleAudioPath, freshState, PlayedEpisodeCleanupScope.AllUsers, 0, NowUtc);
    Assert.True(zeroDelayCheck.ShouldDelete);

    var stateWithRecordedTime = new PodcastEpisodeState
    {
      Guid = "ep-1",
      FileName = "ep1.mp3",
      PlayedByUsersUtc = new Dictionary<Guid, DateTime>
      {
        [alice.Id] = NowUtc.AddHours(-25),
      },
    };
    var delayedReady = evaluator.Evaluate(SampleAudioPath, stateWithRecordedTime, PlayedEpisodeCleanupScope.AllUsers, 24, NowUtc);
    Assert.True(delayedReady.ShouldDelete);
    Assert.Empty(delayedReady.ObservedPlayedByUsersUtc);
  }

  [Fact]
  public void Evaluate_ReturnsFalse_WhenItemNotFoundInJellyfinLibraryOrNoEligibleUsers()
  {
    var alice = new User("alice", "default", "default");
    var missingLibraryMock = new Mock<ILibraryManager>();
    missingLibraryMock.Setup(l => l.FindByPath(SampleAudioPath, false)).Returns((BaseItem?)null);

    var evaluatorMissing = new PodcastPlayedEpisodeEvaluator(
      missingLibraryMock.Object,
      new Mock<IUserManager>().Object,
      new Mock<IUserDataManager>().Object,
      (_, _) => true
    );
    var state = new PodcastEpisodeState { Guid = "ep-1", FileName = "ep1.mp3" };
    Assert.False(evaluatorMissing.Evaluate(SampleAudioPath, state, PlayedEpisodeCleanupScope.AnyUser, 0, NowUtc).ShouldDelete);

    var item = new AudioBook { Path = SampleAudioPath };
    var evaluatorNoUsers = CreateEvaluator(item, [alice], new Dictionary<Guid, UserItemData>(), visibleUserIds: []);
    Assert.False(evaluatorNoUsers.Evaluate(SampleAudioPath, state, PlayedEpisodeCleanupScope.AnyUser, 0, NowUtc).ShouldDelete);
  }

  private static PodcastPlayedEpisodeEvaluator CreateEvaluator(
    BaseItem item,
    IReadOnlyList<User> users,
    Dictionary<Guid, UserItemData> userDataMap,
    HashSet<Guid> visibleUserIds
  )
  {
    var libraryManagerMock = new Mock<ILibraryManager>();
    libraryManagerMock.Setup(l => l.FindByPath(SampleAudioPath, false)).Returns(item);

    var userManagerMock = new Mock<IUserManager>();
    userManagerMock.Setup(u => u.GetUsers()).Returns(users);

    var userDataManagerMock = new Mock<IUserDataManager>();
    userDataManagerMock
      .Setup(d => d.GetUserData(It.IsAny<User>(), item))
      .Returns<User, BaseItem>((user, _) => userDataMap.TryGetValue(user.Id, out var data) ? data : null!);

    return new PodcastPlayedEpisodeEvaluator(
      libraryManagerMock.Object,
      userManagerMock.Object,
      userDataManagerMock.Object,
      (_, user) => visibleUserIds.Contains(user.Id)
    );
  }
}
namespace Jellyfin.Plugin.Podcast.Tests;

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
  public override DateTimeOffset GetUtcNow() => utcNow;
}
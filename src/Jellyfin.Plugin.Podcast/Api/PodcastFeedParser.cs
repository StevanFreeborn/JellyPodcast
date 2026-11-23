using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml.Linq;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcast.Api;

/// <summary>
/// Defines podcast feed parsing operations.
/// </summary>
public interface IPodcastFeedParser
{
  /// <summary>
  /// Parses a podcast feed from the given URL.
  /// </summary>
  /// <param name="feedUrl">The feed URL to parse.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Parsed podcast feed data.</returns>
  [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Feed URLs are configured as raw strings and may include Apple Podcasts web URLs.")]
  Task<PodcastFeedData?> ParseFeedAsync(string feedUrl, CancellationToken cancellationToken);
}

/// <summary>
/// Podcast feed parser for RSS and Atom feeds.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="PodcastFeedParser"/> class.
/// </remarks>
/// <param name="httpClientFactory">HTTP client factory.</param>
/// <param name="logger">Logger instance.</param>
public partial class PodcastFeedParser(IHttpClientFactory httpClientFactory, ILogger<PodcastFeedParser> logger) : IPodcastFeedParser
{
  private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
  private readonly ILogger<PodcastFeedParser> _logger = logger;

  /// <summary>
  /// Parses a podcast feed from the given URL.
  /// </summary>
  /// <param name="feedUrl">The feed URL to parse.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Parsed podcast feed data.</returns>
  [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Feed URLs are configured as raw strings and may include Apple Podcasts web URLs.")]
  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Malformed or unreachable feeds must log and return null so other feeds still process.")]
  public async Task<PodcastFeedData?> ParseFeedAsync(string feedUrl, CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(feedUrl);

    try
    {
      // Check if this is an Apple Podcasts URL and resolve to RSS feed
      var resolvedUrl = await ResolveApplePodcastsUrlAsync(feedUrl, cancellationToken).ConfigureAwait(false);

      LogFetchingFeed(_logger, resolvedUrl);

      using var httpClient = _httpClientFactory.CreateClient();
      var response = await httpClient.GetStringAsync(new Uri(resolvedUrl, UriKind.RelativeOrAbsolute), cancellationToken).ConfigureAwait(false);

      var doc = XDocument.Parse(response);
      var root = doc.Root;

      if (root is null)
      {
        LogMissingRoot(_logger, feedUrl);
        return null;
      }

      if (root.Name.LocalName is "rss")
      {
        return ParseRssFeed(doc, feedUrl);
      }

      if (root.Name.LocalName is "feed")
      {
        return ParseAtomFeed(doc, feedUrl);
      }

      LogUnknownFormat(_logger, feedUrl);
      return null;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      LogParseError(_logger, feedUrl, ex);
      return null;
    }
  }

  private PodcastFeedData? ParseRssFeed(XDocument doc, string feedUrl)
  {
    var channel = doc.Descendants("channel").FirstOrDefault();

    if (channel is null)
    {
      return null;
    }

    var itunesNs = XNamespace.Get("http://www.itunes.com/dtds/podcast-1.0.dtd");

    var feed = new PodcastFeedData
    {
      FeedUrl = feedUrl,
      Title = channel.Element("title")?.Value ?? "Unknown Podcast",
      Description = channel.Element("description")?.Value ?? string.Empty,
      Author = channel.Element(itunesNs + "author")?.Value ?? channel.Element("author")?.Value ?? string.Empty,
      ImageUrl = channel.Element(itunesNs + "image")?.Attribute("href")?.Value
        ?? channel.Element("image")?.Element("url")?.Value
        ?? string.Empty,
      Language = channel.Element("language")?.Value ?? "en",
      Episodes = [],
    };

    foreach (var item in channel.Descendants("item"))
    {
      var enclosure = item.Element("enclosure");

      if (enclosure is null)
      {
        continue;
      }

      var mediaUrl = enclosure.Attribute("url")?.Value ?? string.Empty;
      var rawGuid = item.Element("guid")?.Value;

      var episode = new PodcastEpisode
      {
        Title = item.Element("title")?.Value ?? "Untitled Episode",
        Description = item.Element("description")?.Value
          ?? item.Element(itunesNs + "summary")?.Value
          ?? string.Empty,
        PubDate = ParsePubDate(item.Element("pubDate")?.Value),
        MediaUrl = mediaUrl,
        MediaType = enclosure.Attribute("type")?.Value ?? "audio/mpeg",
        MediaLength = ParseLong(enclosure.Attribute("length")?.Value),
        Duration = ParseDuration(item.Element(itunesNs + "duration")?.Value),
        ImageUrl = item.Element(itunesNs + "image")?.Attribute("href")?.Value ?? feed.ImageUrl,
        Guid = string.IsNullOrWhiteSpace(rawGuid) is false ? rawGuid.Trim() : mediaUrl,
      };

      feed.Episodes.Add(episode);
    }

    LogParsedRss(_logger, feed.Title, feed.Episodes.Count);
    return feed;
  }

  private PodcastFeedData? ParseAtomFeed(XDocument doc, string feedUrl)
  {
    var atomNs = XNamespace.Get("http://www.w3.org/2005/Atom");
    var root = doc.Root;

    if (root is null)
    {
      return null;
    }

    var feed = new PodcastFeedData
    {
      FeedUrl = feedUrl,
      Title = root.Element(atomNs + "title")?.Value ?? "Unknown Podcast",
      Description = root.Element(atomNs + "subtitle")?.Value ?? string.Empty,
      Author = root.Element(atomNs + "author")?.Element(atomNs + "name")?.Value ?? string.Empty,
      ImageUrl = root.Elements(atomNs + "link")
        .FirstOrDefault(e => e.Attribute("rel")?.Value is "icon")
        ?.Attribute("href")?.Value ?? string.Empty,
      Language = "en",
      Episodes = [],
    };

    foreach (var entry in root.Descendants(atomNs + "entry"))
    {
      var link = entry.Elements(atomNs + "link")
        .FirstOrDefault(e => e.Attribute("rel")?.Value is "enclosure");

      if (link is null)
      {
        continue;
      }

      var mediaUrl = link.Attribute("href")?.Value ?? string.Empty;
      var rawId = entry.Element(atomNs + "id")?.Value;

      var episode = new PodcastEpisode
      {
        Title = entry.Element(atomNs + "title")?.Value ?? "Untitled Episode",
        Description = entry.Element(atomNs + "summary")?.Value ?? string.Empty,
        PubDate = ParseAtomDate(entry.Element(atomNs + "published")?.Value),
        MediaUrl = mediaUrl,
        MediaType = link.Attribute("type")?.Value ?? "audio/mpeg",
        MediaLength = ParseLong(link.Attribute("length")?.Value),
        ImageUrl = feed.ImageUrl,
        Guid = string.IsNullOrWhiteSpace(rawId) is false ? rawId.Trim() : mediaUrl,
      };

      feed.Episodes.Add(episode);
    }

    LogParsedAtom(_logger, feed.Title, feed.Episodes.Count);
    return feed;
  }

  private static DateTime ParsePubDate(string? dateStr)
  {
    if (string.IsNullOrEmpty(dateStr))
    {
      return DateTime.MinValue;
    }

    return DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
      ? date
      : DateTime.MinValue;
  }

  private static DateTime ParseAtomDate(string? dateStr)
  {
    if (string.IsNullOrEmpty(dateStr))
    {
      return DateTime.MinValue;
    }

    return DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
      ? date.DateTime
      : DateTime.MinValue;
  }

  private static long ParseLong(string? value)
  {
    if (string.IsNullOrEmpty(value))
    {
      return 0;
    }

    return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
      ? result
      : 0;
  }

  private static TimeSpan ParseDuration(string? duration)
  {
    if (string.IsNullOrEmpty(duration))
    {
      return TimeSpan.Zero;
    }

    if (int.TryParse(duration, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
    {
      return TimeSpan.FromSeconds(seconds);
    }

    return TimeSpan.TryParse(duration, CultureInfo.InvariantCulture, out var timespan)
      ? timespan
      : TimeSpan.Zero;
  }

  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Falls back to the original URL if Apple Podcasts resolution fails.")]
  private async Task<string> ResolveApplePodcastsUrlAsync(string url, CancellationToken cancellationToken)
  {
    if (url.Contains("podcasts.apple.com", StringComparison.OrdinalIgnoreCase) is false)
    {
      return url;
    }

    try
    {
      LogResolvingAppleUrl(_logger, url);

      using var httpClient = _httpClientFactory.CreateClient();
      var htmlContent = await httpClient.GetStringAsync(new Uri(url, UriKind.RelativeOrAbsolute), cancellationToken).ConfigureAwait(false);

      const string feedUrlMarker = "\"feedUrl\":\"";
      var startIndex = htmlContent.IndexOf(feedUrlMarker, StringComparison.Ordinal);

      if (startIndex is -1)
      {
        LogAppleFeedNotFound(_logger);
        return url;
      }

      startIndex += feedUrlMarker.Length;
      var endIndex = htmlContent.IndexOf('"', startIndex, StringComparison.Ordinal);

      if (endIndex is -1)
      {
        LogAppleFeedExtractFailed(_logger);
        return url;
      }

      var rssUrl = htmlContent[startIndex..endIndex].Replace("\\/", "/", StringComparison.Ordinal);

      LogAppleResolved(_logger, rssUrl);
      return rssUrl;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      throw;
    }
    catch (Exception ex)
    {
      LogAppleResolveError(_logger, ex);
      return url;
    }
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "Fetching podcast feed from {Url}")]
  private static partial void LogFetchingFeed(ILogger logger, string url);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Feed has no root element: {Url}")]
  private static partial void LogMissingRoot(ILogger logger, string url);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Unknown feed format for {Url}")]
  private static partial void LogUnknownFormat(ILogger logger, string url);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error parsing podcast feed from {Url}")]
  private static partial void LogParseError(ILogger logger, string url, Exception exception);

  [LoggerMessage(Level = LogLevel.Information, Message = "Parsed RSS feed: {Title} with {Count} episodes")]
  private static partial void LogParsedRss(ILogger logger, string title, int count);

  [LoggerMessage(Level = LogLevel.Information, Message = "Parsed Atom feed: {Title} with {Count} episodes")]
  private static partial void LogParsedAtom(ILogger logger, string title, int count);

  [LoggerMessage(Level = LogLevel.Information, Message = "Detected Apple Podcasts URL, resolving to RSS feed: {Url}")]
  private static partial void LogResolvingAppleUrl(ILogger logger, string url);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Could not find RSS feed URL in Apple Podcasts page, using original URL")]
  private static partial void LogAppleFeedNotFound(ILogger logger);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Could not extract RSS feed URL from Apple Podcasts page")]
  private static partial void LogAppleFeedExtractFailed(ILogger logger);

  [LoggerMessage(Level = LogLevel.Information, Message = "Resolved Apple Podcasts URL to RSS feed: {RssUrl}")]
  private static partial void LogAppleResolved(ILogger logger, string rssUrl);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error resolving Apple Podcasts URL, using original URL")]
  private static partial void LogAppleResolveError(ILogger logger, Exception exception);
}

/// <summary>
/// Represents parsed podcast feed data.
/// </summary>
public class PodcastFeedData
{
  /// <summary>
  /// Gets or sets the feed URL.
  /// </summary>
  [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Feed URLs are stored as strings matching RSS/config inputs.")]
  public string FeedUrl { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the podcast title.
  /// </summary>
  public string Title { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the podcast description.
  /// </summary>
  public string Description { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the podcast author.
  /// </summary>
  public string Author { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the podcast image URL.
  /// </summary>
  [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Feed artwork URLs are parsed directly as raw strings.")]
  public string ImageUrl { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the podcast language.
  /// </summary>
  public string Language { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the list of episodes.
  /// </summary>
  [SuppressMessage("Design", "CA1002:Do not expose generic lists", Justification = "Mutated during feed parsing and consumed by tests and library manager.")]
  [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Initialized and populated cleanly by callers and tests.")]
  public List<PodcastEpisode> Episodes { get; set; } = [];
}

/// <summary>
/// Represents a podcast episode.
/// </summary>
public class PodcastEpisode
{
  /// <summary>
  /// Gets or sets the episode title.
  /// </summary>
  public string Title { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the episode description.
  /// </summary>
  public string Description { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the publication date.
  /// </summary>
  public DateTime PubDate { get; set; }

  /// <summary>
  /// Gets or sets the media URL.
  /// </summary>
  [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Enclosure URLs are parsed directly as raw strings.")]
  public string MediaUrl { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the media type (MIME type).
  /// </summary>
  public string MediaType { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the media length in bytes.
  /// </summary>
  public long MediaLength { get; set; }

  /// <summary>
  /// Gets or sets the episode duration.
  /// </summary>
  public TimeSpan Duration { get; set; }

  /// <summary>
  /// Gets or sets the episode image URL.
  /// </summary>
  [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "Artwork URLs are parsed directly as raw strings.")]
  public string ImageUrl { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the episode GUID.
  /// </summary>
  [SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Matches RSS <guid> element name.")]
  public string Guid { get; set; } = string.Empty;
}
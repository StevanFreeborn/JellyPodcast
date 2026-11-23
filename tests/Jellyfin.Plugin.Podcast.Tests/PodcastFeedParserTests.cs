using System.Net;
using System.Text;

using Jellyfin.Plugin.Podcast.Api;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class PodcastFeedParserTests
{
  [Fact]
  public async Task ParseFeedAsync_ParsesRssFeedWithAllMetadataAndDurationFormats()
  {
    const string rss = """
      <?xml version="1.0" encoding="UTF-8"?>
      <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
        <channel>
          <title>Engineering Weekly</title>
          <description>Deep dives into systems</description>
          <itunes:author>Jane Doe</itunes:author>
          <itunes:image href="https://cdn.example.com/show.jpg" />
          <language>en-us</language>
          <item>
            <title>Episode 1</title>
            <description>First episode notes</description>
            <pubDate>Mon, 23 Nov 2026 10:00:00 GMT</pubDate>
            <enclosure url="https://cdn.example.com/ep1.mp3" type="audio/mpeg" length="123456" />
            <itunes:duration>3661</itunes:duration>
            <itunes:image href="https://cdn.example.com/ep1.jpg" />
            <guid>guid-1</guid>
          </item>
          <item>
            <title>Episode 2</title>
            <itunes:summary>Summary fallback</itunes:summary>
            <pubDate>not-a-date</pubDate>
            <enclosure url="https://cdn.example.com/ep2.m4a" type="audio/mp4" length="bad-long" />
            <itunes:duration>01:02:03</itunes:duration>
          </item>
          <item>
            <title>No Enclosure Item</title>
          </item>
        </channel>
      </rss>
      """;

    var parser = CreateParser((_, _) => OkTextResponse(rss));

    var result = await parser.ParseFeedAsync("https://example.com/feed.xml", TestContext.Current.CancellationToken);

    Assert.NotNull(result);
    Assert.Equal("https://example.com/feed.xml", result.FeedUrl);
    Assert.Equal("Engineering Weekly", result.Title);
    Assert.Equal("Deep dives into systems", result.Description);
    Assert.Equal("Jane Doe", result.Author);
    Assert.Equal("https://cdn.example.com/show.jpg", result.ImageUrl);
    Assert.Equal("en-us", result.Language);
    Assert.Equal(2, result.Episodes.Count);

    var ep1 = result.Episodes[0];
    Assert.Equal("Episode 1", ep1.Title);
    Assert.Equal("First episode notes", ep1.Description);
    Assert.Equal(2026, ep1.PubDate.Year);
    Assert.Equal("https://cdn.example.com/ep1.mp3", ep1.MediaUrl);
    Assert.Equal("audio/mpeg", ep1.MediaType);
    Assert.Equal(123456L, ep1.MediaLength);
    Assert.Equal(TimeSpan.FromSeconds(3661), ep1.Duration);
    Assert.Equal("https://cdn.example.com/ep1.jpg", ep1.ImageUrl);
    Assert.Equal("guid-1", ep1.Guid);

    var ep2 = result.Episodes[1];
    Assert.Equal("Summary fallback", ep2.Description);
    Assert.Equal(DateTime.MinValue, ep2.PubDate);
    Assert.Equal(0L, ep2.MediaLength);
    Assert.Equal(new TimeSpan(1, 2, 3), ep2.Duration);
    Assert.Equal("https://cdn.example.com/show.jpg", ep2.ImageUrl);
    Assert.Equal("https://cdn.example.com/ep2.m4a", ep2.Guid);
  }

  [Fact]
  public async Task ParseFeedAsync_HandlesMissingOptionalRssChannelAndItemFields()
  {
    const string rss = """
      <?xml version="1.0" encoding="UTF-8"?>
      <rss version="2.0">
        <channel>
          <author>Fallback Author</author>
          <image><url>https://cdn.example.com/legacy-cover.jpg</url></image>
          <item>
            <enclosure />
          </item>
        </channel>
      </rss>
      """;

    var parser = CreateParser((_, _) => OkTextResponse(rss));

    var result = await parser.ParseFeedAsync("https://example.com/minimal.xml", TestContext.Current.CancellationToken);

    Assert.NotNull(result);
    Assert.Equal("Unknown Podcast", result.Title);
    Assert.Equal(string.Empty, result.Description);
    Assert.Equal("Fallback Author", result.Author);
    Assert.Equal("https://cdn.example.com/legacy-cover.jpg", result.ImageUrl);
    Assert.Equal("en", result.Language);
    var ep = Assert.Single(result.Episodes);
    Assert.Equal("Untitled Episode", ep.Title);
    Assert.Equal(string.Empty, ep.Description);
    Assert.Equal(DateTime.MinValue, ep.PubDate);
    Assert.Equal("audio/mpeg", ep.MediaType);
    Assert.Equal(0L, ep.MediaLength);
    Assert.Equal(TimeSpan.Zero, ep.Duration);
  }

  [Fact]
  public async Task ParseFeedAsync_ParsesRssWithMissingAuthorAndImageAndInvalidDuration()
  {
    const string rss = """
      <?xml version="1.0" encoding="UTF-8"?>
      <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
        <channel>
          <title>Bare Show</title>
          <item>
            <title>Ep</title>
            <enclosure url="https://cdn.example.com/a.mp3" />
            <itunes:duration>invalid-duration</itunes:duration>
          </item>
        </channel>
      </rss>
      """;

    var parser = CreateParser((_, _) => OkTextResponse(rss));

    var result = await parser.ParseFeedAsync("https://example.com/bare.xml", TestContext.Current.CancellationToken);

    Assert.NotNull(result);
    Assert.Equal(string.Empty, result.Author);
    Assert.Equal(string.Empty, result.ImageUrl);
    Assert.Equal(TimeSpan.Zero, result.Episodes[0].Duration);
  }

  [Fact]
  public async Task ParseFeedAsync_ReturnsNull_WhenRssHasNoChannel()
  {
    const string rss = """<rss version="2.0"></rss>""";
    var parser = CreateParser((_, _) => OkTextResponse(rss));

    var result = await parser.ParseFeedAsync("https://example.com/nochannel.xml", TestContext.Current.CancellationToken);

    Assert.Null(result);
  }

  [Fact]
  public async Task ParseFeedAsync_ParsesAtomFeedWithFallbacks()
  {
    const string atom = """
      <?xml version="1.0" encoding="UTF-8"?>
      <feed xmlns="http://www.w3.org/2005/Atom">
        <title>Atom Podcast</title>
        <subtitle>Atom subtitle</subtitle>
        <author><name>Atom Author</name></author>
        <link rel="icon" href="https://cdn.example.com/icon.jpg" />
        <entry>
          <title>Atom Ep 1</title>
          <summary>Atom summary</summary>
          <published>2026-11-23T12:00:00Z</published>
          <link rel="enclosure" href="https://cdn.example.com/atom1.ogg" type="audio/ogg" length="9876" />
          <id>atom-id-1</id>
        </entry>
        <entry>
          <link rel="enclosure" href="https://cdn.example.com/atom2.mp3" />
          <published>bad-date</published>
        </entry>
        <entry>
          <title>No enclosure</title>
        </entry>
      </feed>
      """;

    var parser = CreateParser((_, _) => OkTextResponse(atom));

    var result = await parser.ParseFeedAsync("https://example.com/atom.xml", TestContext.Current.CancellationToken);

    Assert.NotNull(result);
    Assert.Equal("Atom Podcast", result.Title);
    Assert.Equal("Atom subtitle", result.Description);
    Assert.Equal("Atom Author", result.Author);
    Assert.Equal("https://cdn.example.com/icon.jpg", result.ImageUrl);
    Assert.Equal(2, result.Episodes.Count);

    Assert.Equal("Atom Ep 1", result.Episodes[0].Title);
    Assert.Equal("Atom summary", result.Episodes[0].Description);
    Assert.Equal(2026, result.Episodes[0].PubDate.Year);
    Assert.Equal("audio/ogg", result.Episodes[0].MediaType);
    Assert.Equal(9876L, result.Episodes[0].MediaLength);
    Assert.Equal("atom-id-1", result.Episodes[0].Guid);

    Assert.Equal("Untitled Episode", result.Episodes[1].Title);
    Assert.Equal(DateTime.MinValue, result.Episodes[1].PubDate);
    Assert.Equal("https://cdn.example.com/atom2.mp3", result.Episodes[1].Guid);
  }

  [Fact]
  public async Task ParseFeedAsync_ParsesMinimalAtomFeed()
  {
    const string atom = """
      <?xml version="1.0" encoding="UTF-8"?>
      <feed xmlns="http://www.w3.org/2005/Atom">
        <entry>
          <link rel="enclosure" />
        </entry>
      </feed>
      """;

    var parser = CreateParser((_, _) => OkTextResponse(atom));

    var result = await parser.ParseFeedAsync("https://example.com/atom-min.xml", TestContext.Current.CancellationToken);

    Assert.NotNull(result);
    Assert.Equal("Unknown Podcast", result.Title);
    Assert.Equal(string.Empty, result.Author);
    Assert.Equal(string.Empty, result.ImageUrl);
    Assert.Single(result.Episodes);
  }

  [Fact]
  public async Task ParseFeedAsync_ReturnsNull_ForUnknownRootOrMalformedXmlOrHttpFailure()
  {
    var unknownParser = CreateParser((_, _) => OkTextResponse("<html><body /></html>"));
    Assert.Null(await unknownParser.ParseFeedAsync("https://example.com/html", TestContext.Current.CancellationToken));

    var badXmlParser = CreateParser((_, _) => OkTextResponse("<<<not xml>>>"));
    Assert.Null(await badXmlParser.ParseFeedAsync("https://example.com/bad.xml", TestContext.Current.CancellationToken));

    var httpErrParser = CreateParser((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
    Assert.Null(await httpErrParser.ParseFeedAsync("https://example.com/404.xml", TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task ParseFeedAsync_RethrowsOperationCanceledException_WhenCancelled()
  {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var parser = CreateParser(
      (_, ct) =>
      {
        ct.ThrowIfCancellationRequested();
        return OkTextResponse("<rss />");
      }
    );

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ParseFeedAsync("https://example.com/feed.xml", cts.Token));
  }

  [Fact]
  public async Task ParseFeedAsync_ResolvesApplePodcastsUrlToRssFeed()
  {
    const string appleHtml = """<html><script>{"feedUrl":"https:\/\/feeds.example.com\/resolved.xml"}</script></html>""";
    const string rss = """<rss version="2.0"><channel><title>Resolved Show</title></channel></rss>""";

    var parser = CreateParser(
      (req, _) =>
      {
        var url = req.RequestUri!.ToString();
        return url.Contains("podcasts.apple.com", StringComparison.OrdinalIgnoreCase)
          ? OkTextResponse(appleHtml)
          : OkTextResponse(rss);
      }
    );

    var result = await parser.ParseFeedAsync("https://podcasts.apple.com/us/podcast/show/id123", TestContext.Current.CancellationToken);

    Assert.NotNull(result);
    Assert.Equal("Resolved Show", result.Title);
  }

  [Theory]
  [InlineData("<html>no feed url marker</html>")]
  [InlineData("<html>\"feedUrl\":\"https://unclosed.example.com/feed.xml</html>")]
  public async Task ParseFeedAsync_FallsBackToOriginalUrl_WhenAppleFeedUrlCannotBeExtracted(string appleHtml)
  {
    var requestedUrls = new List<string>();
    var parser = CreateParser(
      (req, _) =>
      {
        requestedUrls.Add(req.RequestUri!.ToString());
        return OkTextResponse(appleHtml);
      }
    );

    var result = await parser.ParseFeedAsync("https://podcasts.apple.com/us/podcast/show/id123", TestContext.Current.CancellationToken);

    Assert.Null(result);
    Assert.Equal(2, requestedUrls.Count);
  }

  [Fact]
  public async Task ParseFeedAsync_FallsBackGracefully_WhenAppleResolutionHttpRequestFails()
  {
    var parser = CreateParser((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));

    var result = await parser.ParseFeedAsync("https://podcasts.apple.com/us/podcast/show/id123", TestContext.Current.CancellationToken);

    Assert.Null(result);
  }

  [Fact]
  public async Task ParseFeedAsync_RethrowsCancellation_DuringApplePodcastsResolution()
  {
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var parser = CreateParser(
      (_, ct) =>
      {
        ct.ThrowIfCancellationRequested();
        return OkTextResponse("<html />");
      }
    );

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ParseFeedAsync("https://podcasts.apple.com/us/podcast/show/id123", cts.Token));
  }

  [Fact]
  public async Task ParseFeedAsync_SendsUserAgentHeader_ForFeedAndApplePodcastsRequests()
  {
    const string appleHtml = """<html><script>{"feedUrl":"https:\/\/feeds.example.com\/resolved.xml"}</script></html>""";
    const string rss = """<rss version="2.0"><channel><title>Agent Show</title></channel></rss>""";
    var observedUserAgents = new List<string>();

    var parser = CreateParser(
      (req, _) =>
      {
        observedUserAgents.Add(req.Headers.UserAgent.ToString());
        var url = req.RequestUri!.ToString();
        return url.Contains("podcasts.apple.com", StringComparison.OrdinalIgnoreCase)
          ? OkTextResponse(appleHtml)
          : OkTextResponse(rss);
      }
    );

    var result = await parser.ParseFeedAsync("https://podcasts.apple.com/us/podcast/show/id123", TestContext.Current.CancellationToken);

    Assert.NotNull(result);
    Assert.Equal(2, observedUserAgents.Count);
    Assert.All(observedUserAgents, ua => Assert.StartsWith("Jellyfin.Plugin.Podcast/", ua, StringComparison.Ordinal));
  }

  [Fact]
  public async Task ParseFeedAsync_IgnoresDtdEntities_AndParsesBenignDoctypeFeeds()
  {
    const string benignDoctypeRss = """
      <?xml version="1.0" encoding="UTF-8"?>
      <!DOCTYPE rss>
      <rss version="2.0">
        <channel>
          <title>Benign Doctype Show</title>
        </channel>
      </rss>
      """;

    const string dtdEntityRss = """
      <?xml version="1.0" encoding="UTF-8"?>
      <!DOCTYPE rss [<!ENTITY bomb "expanded">]>
      <rss version="2.0">
        <channel>
          <title>&bomb;</title>
        </channel>
      </rss>
      """;

    var benignParser = CreateParser((_, _) => OkTextResponse(benignDoctypeRss));
    var benignResult = await benignParser.ParseFeedAsync("https://example.com/benign.xml", TestContext.Current.CancellationToken);

    Assert.NotNull(benignResult);
    Assert.Equal("Benign Doctype Show", benignResult.Title);

    var entityParser = CreateParser((_, _) => OkTextResponse(dtdEntityRss));
    var entityResult = await entityParser.ParseFeedAsync("https://example.com/entity.xml", TestContext.Current.CancellationToken);

    Assert.Null(entityResult);
  }

  [Fact]
  public async Task ParseFeedAsync_ExtractsEpisodeArtworkAcrossPodcastTags_AndFallsBackWhenEmpty()
  {
    const string rss = """
      <?xml version="1.0" encoding="UTF-8"?>
      <rss version="2.0"
           xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd"
           xmlns:media="http://search.yahoo.com/mrss/"
           xmlns:googleplay="http://www.google.com/schemas/play-podcasts/1.0"
           xmlns:podcast="https://podcastindex.org/namespace/1.0">
        <channel>
          <title>Artwork Test Show</title>
          <podcast:images srcset="https://cdn.example.com/show-p2.webp 1200w, https://cdn.example.com/show-small.webp 300w" />
          <item>
            <title>Empty Href</title>
            <enclosure url="https://cdn.example.com/1.mp3" />
            <itunes:image href="   " />
          </item>
          <item>
            <title>Inner Itunes</title>
            <enclosure url="https://cdn.example.com/2.mp3" />
            <itunes:image>https://cdn.example.com/ep2.jpg</itunes:image>
          </item>
          <item>
            <title>Media Thumbnail</title>
            <enclosure url="https://cdn.example.com/3.mp3" />
            <media:thumbnail url="https://cdn.example.com/ep3.png" />
          </item>
          <item>
            <title>Media Content</title>
            <enclosure url="https://cdn.example.com/4.mp3" />
            <media:content url="https://cdn.example.com/ep4.mp3" medium="audio" />
            <media:content url="https://cdn.example.com/ep4.gif" type="image/gif" />
          </item>
          <item>
            <title>Google Play</title>
            <enclosure url="https://cdn.example.com/5.mp3" />
            <googleplay:image href="https://cdn.example.com/ep5.jpg" />
          </item>
          <item>
            <title>Podcast Images Srcset</title>
            <enclosure url="https://cdn.example.com/6.mp3" />
            <podcast:images srcset="https://cdn.example.com/ep6.webp 1400w" />
          </item>
          <item>
            <title>Item Image Element</title>
            <enclosure url="https://cdn.example.com/7.mp3" />
            <image><url>https://cdn.example.com/ep7.jpg</url></image>
          </item>
        </channel>
      </rss>
      """;

    var parser = CreateParser((_, _) => OkTextResponse(rss));
    var result = await parser.ParseFeedAsync("https://example.com/art.xml", TestContext.Current.CancellationToken);

    Assert.NotNull(result);
    Assert.Equal("https://cdn.example.com/show-p2.webp", result.ImageUrl);
    Assert.Equal(7, result.Episodes.Count);
    Assert.Equal("https://cdn.example.com/show-p2.webp", result.Episodes[0].ImageUrl);
    Assert.Equal("https://cdn.example.com/ep2.jpg", result.Episodes[1].ImageUrl);
    Assert.Equal("https://cdn.example.com/ep3.png", result.Episodes[2].ImageUrl);
    Assert.Equal("https://cdn.example.com/ep4.gif", result.Episodes[3].ImageUrl);
    Assert.Equal("https://cdn.example.com/ep5.jpg", result.Episodes[4].ImageUrl);
    Assert.Equal("https://cdn.example.com/ep6.webp", result.Episodes[5].ImageUrl);
    Assert.Equal("https://cdn.example.com/ep7.jpg", result.Episodes[6].ImageUrl);
  }

  [Fact]
  public async Task ParseFeedAsync_ExtractsAtomFeedAndEntryArtworkTags()
  {
    const string atom = """
      <?xml version="1.0" encoding="UTF-8"?>
      <feed xmlns="http://www.w3.org/2005/Atom"
            xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd"
            xmlns:media="http://search.yahoo.com/mrss/">
        <title>Atom Artwork Show</title>
        <logo>https://cdn.example.com/atom-logo.png</logo>
        <entry>
          <title>Entry Thumbnail</title>
          <id>atom-1</id>
          <link rel="enclosure" href="https://cdn.example.com/atom1.mp3" />
          <media:thumbnail url="https://cdn.example.com/atom-ep1.png" />
        </entry>
        <entry>
          <title>Entry Icon Link</title>
          <id>atom-2</id>
          <link rel="enclosure" href="https://cdn.example.com/atom2.mp3" />
          <link rel="icon" href="https://cdn.example.com/atom-ep2.jpg" />
        </entry>
        <entry>
          <title>Entry Fallback</title>
          <id>atom-3</id>
          <link rel="enclosure" href="https://cdn.example.com/atom3.mp3" />
        </entry>
      </feed>
      """;

    var parser = CreateParser((_, _) => OkTextResponse(atom));
    var result = await parser.ParseFeedAsync("https://example.com/atom.xml", TestContext.Current.CancellationToken);

    Assert.NotNull(result);
    Assert.Equal("https://cdn.example.com/atom-logo.png", result.ImageUrl);
    Assert.Equal(3, result.Episodes.Count);
    Assert.Equal("https://cdn.example.com/atom-ep1.png", result.Episodes[0].ImageUrl);
    Assert.Equal("https://cdn.example.com/atom-ep2.jpg", result.Episodes[1].ImageUrl);
    Assert.Equal("https://cdn.example.com/atom-logo.png", result.Episodes[2].ImageUrl);
  }

  private static PodcastFeedParser CreateParser(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
  {
    var handler = new StubHttpMessageHandler(responder);
    var factoryMock = new Mock<IHttpClientFactory>();
    factoryMock
      .Setup(f => f.CreateClient(It.IsAny<string>()))
      .Returns(() => new HttpClient(handler, disposeHandler: false));

    return new PodcastFeedParser(factoryMock.Object, NullLogger<PodcastFeedParser>.Instance);
  }

  private static Task<HttpResponseMessage> OkTextResponse(string body) =>
    Task.FromResult(
      new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(body, Encoding.UTF8, "application/xml"),
      }
    );

  private sealed class StubHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder
  ) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      => responder(request, cancellationToken);
  }
}
using System.Text;
using System.Xml;

using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Services;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class PodcastOpmlServiceTests
{
  private static readonly DateTimeOffset FixedUtcNow = new(2026, 11, 23, 15, 0, 0, TimeSpan.Zero);

  private readonly PodcastOpmlService _service = new(new FixedTimeProvider(FixedUtcNow));

  [Fact]
  public void ParseOpml_ExtractsFlatNestedCaseInsensitiveAndDeduplicatedHttpFeeds()
  {
    const string opml = """
      <?xml version="1.0" encoding="utf-8"?>
      <opml version="2.0">
        <head>
          <title>Pocket Casts Feeds</title>
        </head>
        <body>
          <outline text="feeds">
            <outline type="rss" text="Tech Weekly" xmlUrl="https://feeds.example.com/tech.xml" />
            <outline type="rss" text="Fallback Text Title" title="Explicit Title &amp; More" XMLURL=" http://feeds.example.com/explicit.xml " />
            <outline type="rss" text="Duplicate Feed" xmlUrl="HTTPS://FEEDS.EXAMPLE.COM/tech.xml" />
            <outline type="rss" text="Folder Without Url">
              <outline type="rss" text="   " xmlUrl="https://feeds.example.com/untitled.xml" />
            </outline>
            <outline type="rss" text="Unsupported Scheme" xmlUrl="ftp://feeds.example.com/audio.xml" />
            <outline type="rss" text="Relative Url" xmlUrl="/relative/feed.xml" />
          </outline>
        </body>
      </opml>
      """;

    var entries = _service.ParseOpml(opml);

    Assert.Equal(3, entries.Count);

    Assert.Equal("https://feeds.example.com/tech.xml", entries[0].Url);
    Assert.Equal("Tech Weekly", entries[0].Title);

    Assert.Equal("http://feeds.example.com/explicit.xml", entries[1].Url);
    Assert.Equal("Explicit Title & More", entries[1].Title);

    Assert.Equal("https://feeds.example.com/untitled.xml", entries[2].Url);
    Assert.Null(entries[2].Title);
  }

  [Theory]
  [InlineData("<rss><channel /></rss>")]
  [InlineData("<opml version=\"2.0\"><head /></opml>")]
  public void ParseOpml_ThrowsFormatException_WhenStructureIsNotOpml(string xml)
  {
    Assert.Throws<FormatException>(() => _service.ParseOpml(xml));
  }

  [Theory]
  [InlineData("not valid xml")]
  [InlineData("<?xml version=\"1.0\"?><!DOCTYPE opml [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><opml><body><outline xmlUrl=\"https://example.com\" /></body></opml>")]
  public void ParseOpml_ThrowsXmlException_OnMalformedXmlOrDtd(string xml)
  {
    Assert.Throws<XmlException>(() => _service.ParseOpml(xml));
  }

  [Fact]
  public void ParseOpml_ThrowsArgumentException_WhenInputIsWhitespace()
  {
    Assert.Throws<ArgumentException>(() => _service.ParseOpml("   "));
  }

  [Fact]
  public void GenerateOpml_ProducesValidOpml20XmlWithTimestampAndEscaping_AndRoundTrips()
  {
    PodcastFeed[] feeds =
    [
      new() { Id = "1", Url = " https://example.com/feed1.xml ", CustomName = "Show & Friends" },
      new() { Id = "2", Url = "https://example.com/feed2.xml", CustomName = null },
      new() { Id = "3", Url = "   ", CustomName = "Ignored Empty Url" },
    ];

    var xml = _service.GenerateOpml(feeds);

    Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.Ordinal);
    Assert.Contains("<opml version=\"2.0\">", xml, StringComparison.Ordinal);
    Assert.Contains("<dateCreated>Mon, 23 Nov 2026 15:00:00 GMT</dateCreated>", xml, StringComparison.Ordinal);
    Assert.Contains("Show &amp; Friends", xml, StringComparison.Ordinal);

    var roundTripped = _service.ParseOpml(xml);
    Assert.Equal(2, roundTripped.Count);
    Assert.Equal("https://example.com/feed1.xml", roundTripped[0].Url);
    Assert.Equal("Show & Friends", roundTripped[0].Title);
    Assert.Equal("https://example.com/feed2.xml", roundTripped[1].Url);
    Assert.Equal("https://example.com/feed2.xml", roundTripped[1].Title);

    // Ensure no UTF-8 BOM character precedes the XML declaration.
    Assert.DoesNotContain('\uFEFF', xml);
    Assert.Equal('<', Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(xml))[0]);
  }

  [Fact]
  public void GenerateOpml_ThrowsArgumentNullException_WhenFeedsIsNull()
  {
    Assert.Throws<ArgumentNullException>(() => _service.GenerateOpml(null!));
  }
}
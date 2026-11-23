using System.Text;
using System.Xml;

using Jellyfin.Plugin.Podcast.Api;
using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Services;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class PodcastOpmlControllerTests
{
  [Fact]
  public void ImportOpml_ReturnsOkWithParsedFeeds_WhenOpmlIsValid()
  {
    const string opml = """
      <?xml version="1.0" encoding="utf-8"?>
      <opml version="2.0">
        <body>
          <outline type="rss" text="Daily Brief" xmlUrl="https://example.com/daily.xml" />
        </body>
      </opml>
      """;

    var controller = new PodcastOpmlController(NullLogger<PodcastOpmlController>.Instance, () => new PluginConfiguration());

    var actionResult = controller.ImportOpml(new OpmlImportRequest { OpmlContent = opml });

    var okResult = Assert.IsType<OkObjectResult>(actionResult);
    var payload = Assert.IsType<OpmlImportResponse>(okResult.Value);
    var entry = Assert.Single(payload.Feeds);
    Assert.Equal("https://example.com/daily.xml", entry.Url);
    Assert.Equal("Daily Brief", entry.Title);
  }

  [Fact]
  public void ImportOpml_ReturnsBadRequest_WhenPayloadMissingOversizedOrInvalid()
  {
    var controller = new PodcastOpmlController(NullLogger<PodcastOpmlController>.Instance, () => new PluginConfiguration());

    Assert.IsType<BadRequestObjectResult>(controller.ImportOpml(null));
    Assert.IsType<BadRequestObjectResult>(controller.ImportOpml(new OpmlImportRequest { OpmlContent = "   " }));

    var oversized = new string('a', (2 * 1024 * 1024) + 1);
    Assert.IsType<BadRequestObjectResult>(controller.ImportOpml(new OpmlImportRequest { OpmlContent = oversized }));

    Assert.IsType<BadRequestObjectResult>(controller.ImportOpml(new OpmlImportRequest { OpmlContent = "<not-valid-xml" }));
    Assert.IsType<BadRequestObjectResult>(controller.ImportOpml(new OpmlImportRequest { OpmlContent = "<rss><channel /></rss>" }));
  }

  [Fact]
  public void ImportOpml_Returns500_WhenServiceThrowsUnexpectedException()
  {
    var serviceMock = new Mock<IPodcastOpmlService>();
    serviceMock
      .Setup(s => s.ParseOpml(It.IsAny<string>()))
      .Throws(new InvalidOperationException("unexpected"));

    var controller = new PodcastOpmlController(
      NullLogger<PodcastOpmlController>.Instance,
      () => new PluginConfiguration(),
      serviceMock.Object
    );

    var result = Assert.IsType<ObjectResult>(controller.ImportOpml(new OpmlImportRequest { OpmlContent = "<opml><body /></opml>" }));
    Assert.Equal(500, result.StatusCode);
  }

  [Fact]
  public void ExportOpml_ReturnsFileDownload_WithSavedFeeds()
  {
    var config = new PluginConfiguration
    {
      PodcastFeeds =
      [
        new PodcastFeed { Id = "f1", Url = "https://example.com/show.xml", CustomName = "My Show" },
      ],
    };

    var controller = new PodcastOpmlController(
      NullLogger<PodcastOpmlController>.Instance,
      () => config,
      new PodcastOpmlService(new FixedTimeProvider(new DateTimeOffset(2026, 11, 23, 15, 0, 0, TimeSpan.Zero)))
    );

    var fileResult = Assert.IsType<FileContentResult>(controller.ExportOpml());
    Assert.Equal("text/x-opml; charset=utf-8", fileResult.ContentType);
    Assert.Equal("jellyfin-podcasts.opml", fileResult.FileDownloadName);

    var xml = Encoding.UTF8.GetString(fileResult.FileContents);
    Assert.Contains("https://example.com/show.xml", xml, StringComparison.Ordinal);
    Assert.Contains("My Show", xml, StringComparison.Ordinal);
  }

  [Fact]
  public void ExportOpml_Returns500_WhenConfigIsNullOrProviderThrows()
  {
    var nullController = new PodcastOpmlController(NullLogger<PodcastOpmlController>.Instance, () => null);
    var nullResult = Assert.IsType<ObjectResult>(nullController.ExportOpml());
    Assert.Equal(500, nullResult.StatusCode);

    var throwingController = new PodcastOpmlController(
      NullLogger<PodcastOpmlController>.Instance,
      () => throw new XmlException("boom")
    );
    var errorResult = Assert.IsType<ObjectResult>(throwingController.ExportOpml());
    Assert.Equal(500, errorResult.StatusCode);
  }
}
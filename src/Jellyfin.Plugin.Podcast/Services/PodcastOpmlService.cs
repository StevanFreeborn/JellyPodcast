using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Jellyfin.Plugin.Podcast.Configuration;

namespace Jellyfin.Plugin.Podcast.Services;

/// <summary>
/// Represents a single podcast subscription entry parsed from an OPML document.
/// </summary>
public sealed class OpmlFeedEntry
{
  /// <summary>
  /// Gets the RSS or Atom feed URL.
  /// </summary>
  [SuppressMessage("Design", "CA1056:URI-like properties should not be strings", Justification = "OPML xmlUrl attributes are transferred directly as raw strings matching PodcastFeed.Url.")]
  public string Url { get; init; } = string.Empty;

  /// <summary>
  /// Gets the optional podcast title from the OPML outline.
  /// </summary>
  public string? Title { get; init; }
}

/// <summary>
/// Defines OPML 2.0 import and export operations for podcast subscriptions.
/// </summary>
public interface IPodcastOpmlService
{
  /// <summary>
  /// Parses podcast feed subscriptions from an OPML 2.0 XML document.
  /// </summary>
  /// <param name="opmlContent">Raw OPML XML text.</param>
  /// <returns>Distinct HTTP/HTTPS podcast feed entries extracted from the OPML outline tree.</returns>
  IReadOnlyList<OpmlFeedEntry> ParseOpml(string opmlContent);

  /// <summary>
  /// Generates an OPML 2.0 XML document containing the supplied podcast feeds.
  /// </summary>
  /// <param name="feeds">Configured podcast feeds to export.</param>
  /// <returns>UTF-8 OPML 2.0 XML string.</returns>
  string GenerateOpml(IEnumerable<PodcastFeed> feeds);
}

/// <summary>
/// Parses and generates OPML 2.0 podcast subscription lists compatible with Pocket Casts and standard podcast clients.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="PodcastOpmlService"/> class.
/// </remarks>
/// <param name="timeProvider">Optional time provider; defaults to <see cref="TimeProvider.System"/>.</param>
public sealed class PodcastOpmlService(TimeProvider? timeProvider = null) : IPodcastOpmlService
{
  private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

  private static readonly XmlReaderSettings SafeXmlReaderSettings = new()
  {
    DtdProcessing = DtdProcessing.Prohibit,
    XmlResolver = null,
  };

  private static readonly XmlWriterSettings OutputWriterSettings = new()
  {
    Encoding = Utf8NoBom,
    Indent = true,
  };

  private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

  /// <inheritdoc />
  public IReadOnlyList<OpmlFeedEntry> ParseOpml(string opmlContent)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(opmlContent);

    using var stringReader = new StringReader(opmlContent);
    using var xmlReader = XmlReader.Create(stringReader, SafeXmlReaderSettings);
    var doc = XDocument.Load(xmlReader);

    if (
      doc.Root is null ||
      string.Equals(doc.Root.Name.LocalName, "opml", StringComparison.OrdinalIgnoreCase) is false
    )
    {
      throw new FormatException("Root element must be <opml>.");
    }

    var body = doc.Root
      .Elements()
      .FirstOrDefault(e => string.Equals(e.Name.LocalName, "body", StringComparison.OrdinalIgnoreCase));

    if (body is null)
    {
      throw new FormatException("OPML document is missing a <body> element.");
    }

    var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var entries = new List<OpmlFeedEntry>();
    var outlines = body
      .Descendants()
      .Where(e => string.Equals(e.Name.LocalName, "outline", StringComparison.OrdinalIgnoreCase));

    foreach (var outline in outlines)
    {
      var rawUrl = GetAttributeValue(outline, "xmlUrl");

      if (string.IsNullOrWhiteSpace(rawUrl))
      {
        continue;
      }

      if (
        Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) is false ||
        uri.Scheme is not ("http" or "https")
      )
      {
        continue;
      }

      if (seenUrls.Add(rawUrl) is false)
      {
        continue;
      }

      var rawTitle = GetAttributeValue(outline, "title");

      if (string.IsNullOrWhiteSpace(rawTitle))
      {
        rawTitle = GetAttributeValue(outline, "text");
      }

      entries.Add(
        new OpmlFeedEntry
        {
          Url = rawUrl,
          Title = string.IsNullOrWhiteSpace(rawTitle) ? null : rawTitle,
        }
      );
    }

    return entries;
  }

  /// <inheritdoc />
  public string GenerateOpml(IEnumerable<PodcastFeed> feeds)
  {
    ArgumentNullException.ThrowIfNull(feeds);

    var dateCreated = _timeProvider.GetUtcNow().ToString("R", CultureInfo.InvariantCulture);

    var bodyElement = new XElement("body");

    foreach (var feed in feeds)
    {
      if (string.IsNullOrWhiteSpace(feed.Url))
      {
        continue;
      }

      var trimmedUrl = feed.Url.Trim();
      var label = string.IsNullOrWhiteSpace(feed.CustomName) is false
        ? feed.CustomName.Trim()
        : trimmedUrl;

      bodyElement.Add(
        new XElement(
          "outline",
          new XAttribute("type", "rss"),
          new XAttribute("text", label),
          new XAttribute("title", label),
          new XAttribute("xmlUrl", trimmedUrl)
        )
      );
    }

    var doc = new XDocument(
      new XDeclaration("1.0", "utf-8", null),
      new XElement(
        "opml",
        new XAttribute("version", "2.0"),
        new XElement(
          "head",
          new XElement("title", "Jellyfin Podcast Subscriptions"),
          new XElement("dateCreated", dateCreated)
        ),
        bodyElement
      )
    );

    using var memoryStream = new MemoryStream();

    using (var xmlWriter = XmlWriter.Create(memoryStream, OutputWriterSettings))
    {
      doc.Save(xmlWriter);
    }

    return Utf8NoBom.GetString(memoryStream.ToArray());
  }

  private static string? GetAttributeValue(XElement element, string name) =>
    element.Attributes()
      .FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
      ?.Value
      .Trim();
}
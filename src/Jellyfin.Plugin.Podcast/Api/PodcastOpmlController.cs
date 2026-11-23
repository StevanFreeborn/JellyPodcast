using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Xml;

using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcast.Api;

/// <summary>
/// Request payload for importing an OPML subscription list.
/// </summary>
public sealed class OpmlImportRequest
{
  /// <summary>
  /// Gets or sets the raw OPML XML document text.
  /// </summary>
  public string OpmlContent { get; set; } = string.Empty;
}

/// <summary>
/// Response payload containing podcast feed entries parsed from an OPML document.
/// </summary>
public sealed class OpmlImportResponse
{
  /// <summary>
  /// Gets the parsed podcast feed entries.
  /// </summary>
  public IReadOnlyList<OpmlFeedEntry> Feeds { get; init; } = [];
}

/// <summary>
/// Controller for importing and exporting podcast subscriptions as OPML 2.0 documents.
/// </summary>
[ApiController]
[Route("Podcast")]
[Authorize(Policy = "RequiresElevation")]
public partial class PodcastOpmlController : ControllerBase
{
  private const int MaxOpmlLength = 2 * 1024 * 1024;

  private readonly ILogger<PodcastOpmlController> _logger;
  private readonly Func<PluginConfiguration?> _configProvider;
  private readonly IPodcastOpmlService _opmlService;

  /// <summary>
  /// Initializes a new instance of the <see cref="PodcastOpmlController"/> class.
  /// </summary>
  /// <param name="logger">Instance of the <see cref="ILogger{PodcastOpmlController}"/> interface.</param>
  /// <param name="timeProvider">Optional time provider; defaults to <see cref="TimeProvider.System"/>.</param>
  [ActivatorUtilitiesConstructor]
  public PodcastOpmlController(ILogger<PodcastOpmlController> logger, TimeProvider? timeProvider = null) : this(
    logger,
    () => Plugin.Instance?.Configuration,
    new PodcastOpmlService(timeProvider)
  )
  {
  }

  internal PodcastOpmlController(
    ILogger<PodcastOpmlController> logger,
    Func<PluginConfiguration?> configProvider,
    IPodcastOpmlService? opmlService = null
  )
  {
    _logger = logger;
    _configProvider = configProvider;
    _opmlService = opmlService ?? new PodcastOpmlService();
  }

  /// <summary>
  /// Parses an OPML 2.0 XML payload and returns distinct HTTP/HTTPS podcast feeds for review in the UI.
  /// </summary>
  /// <param name="request">OPML import payload.</param>
  /// <returns>An <see cref="OpmlImportResponse"/> on success, or HTTP 400 on invalid OPML input.</returns>
  [HttpPost("ImportOpml")]
  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Controller boundary logs unexpected parsing failures and returns HTTP 500.")]
  public IActionResult ImportOpml([FromBody] OpmlImportRequest? request)
  {
    if (request is null || string.IsNullOrWhiteSpace(request.OpmlContent))
    {
      return BadRequest("OPML content is required");
    }

    if (request.OpmlContent.Length is > MaxOpmlLength)
    {
      return BadRequest("OPML content exceeds maximum allowed size");
    }

    try
    {
      var feeds = _opmlService.ParseOpml(request.OpmlContent);
      LogOpmlImported(_logger, feeds.Count);

      return Ok(
        new OpmlImportResponse
        {
          Feeds = feeds,
        }
      );
    }
    catch (XmlException ex)
    {
      LogInvalidOpml(_logger, ex);
      return BadRequest($"Invalid OPML document: {ex.Message}");
    }
    catch (FormatException ex)
    {
      LogInvalidOpml(_logger, ex);
      return BadRequest($"Invalid OPML document: {ex.Message}");
    }
    catch (Exception ex)
    {
      LogImportError(_logger, ex);
      return StatusCode(500, "Failed to import OPML");
    }
  }

  /// <summary>
  /// Exports the currently saved podcast feeds as a downloadable OPML 2.0 XML file.
  /// </summary>
  /// <returns>An OPML XML file response on success, or HTTP 500 on failure.</returns>
  [HttpGet("ExportOpml")]
  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Controller boundary logs unexpected export failures and returns HTTP 500.")]
  public IActionResult ExportOpml()
  {
    try
    {
      var config = _configProvider();

      if (config is null)
      {
        LogConfigNull(_logger);
        return StatusCode(500, "Plugin configuration not available");
      }

      var opmlXml = _opmlService.GenerateOpml(config.PodcastFeeds);
      LogOpmlExported(_logger, config.PodcastFeeds.Count);

      return File(Encoding.UTF8.GetBytes(opmlXml), "text/x-opml; charset=utf-8", "jellyfin-podcasts.opml");
    }
    catch (Exception ex)
    {
      LogExportError(_logger, ex);
      return StatusCode(500, "Failed to export OPML");
    }
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "Parsed {Count} podcast feeds from uploaded OPML")]
  private static partial void LogOpmlImported(ILogger logger, int count);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid OPML upload rejected")]
  private static partial void LogInvalidOpml(ILogger logger, Exception exception);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error importing OPML document")]
  private static partial void LogImportError(ILogger logger, Exception exception);

  [LoggerMessage(Level = LogLevel.Error, Message = "Plugin configuration is null")]
  private static partial void LogConfigNull(ILogger logger);

  [LoggerMessage(Level = LogLevel.Information, Message = "Exported {Count} podcast feeds to OPML")]
  private static partial void LogOpmlExported(ILogger logger, int count);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error exporting OPML document")]
  private static partial void LogExportError(ILogger logger, Exception exception);
}
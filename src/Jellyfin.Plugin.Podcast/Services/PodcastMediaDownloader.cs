using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcast.Services;

/// <summary>
/// Defines HTTP download operations for podcast audio enclosures and cover artwork.
/// </summary>
public interface IPodcastMediaDownloader
{
  /// <summary>
  /// Streams a podcast audio enclosure to a temporary file and atomically moves it to <paramref name="destinationPath"/>.
  /// </summary>
  /// <param name="mediaUrl">Enclosure URL to download.</param>
  /// <param name="destinationPath">Final destination file path.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The HTTP response <c>Content-Type</c> media type, if provided.</returns>
  [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Enclosure URLs originate as raw strings from RSS feeds.")]
  Task<string?> DownloadEpisodeAsync(string mediaUrl, string destinationPath, CancellationToken cancellationToken);

  /// <summary>
  /// Downloads <c>cover.jpg</c> into <paramref name="podcastDir"/> if not already cached, and returns its bytes.
  /// </summary>
  /// <param name="imageUrl">Cover image URL.</param>
  /// <param name="podcastDir">Podcast directory path.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The cover image bytes, or <c>null</c> if unavailable or download failed.</returns>
  [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "Artwork URLs originate as raw strings from RSS feeds.")]
  Task<byte[]?> DownloadCoverArtAsync(string? imageUrl, string podcastDir, CancellationToken cancellationToken);
}

/// <summary>
/// Downloads podcast audio enclosures and caches cover artwork over HTTP using streaming I/O.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="PodcastMediaDownloader"/> class.
/// </remarks>
/// <param name="httpClientFactory">HTTP client factory.</param>
/// <param name="logger">Logger instance.</param>
public partial class PodcastMediaDownloader(
  IHttpClientFactory httpClientFactory,
  ILogger<PodcastMediaDownloader> logger
) : IPodcastMediaDownloader
{
  /// <summary>
  /// Standard cover art filename stored in each podcast directory.
  /// </summary>
  public const string CoverFileName = "cover.jpg";

  private const int StreamBufferSize = 81920;
  private static readonly string UserAgentHeaderValue =
    $"Jellyfin.Plugin.Podcast/{typeof(PodcastMediaDownloader).Assembly.GetName().Version?.ToString(3) ?? "2.0.0"}";
  private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromMinutes(15);

  private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
  private readonly ILogger<PodcastMediaDownloader> _logger = logger;

  /// <inheritdoc />
  public async Task<string?> DownloadEpisodeAsync(
    string mediaUrl,
    string destinationPath,
    CancellationToken cancellationToken
  )
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(mediaUrl);
    ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

    var parentDir = Path.GetDirectoryName(destinationPath);

    if (string.IsNullOrEmpty(parentDir) is false)
    {
      Directory.CreateDirectory(parentDir);
    }

    var tempPath = $"{destinationPath}.tmp";

    try
    {
      using var httpClient = CreateConfiguredClient();
      using var response = await httpClient.GetAsync(
        new Uri(mediaUrl, UriKind.RelativeOrAbsolute),
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken
      ).ConfigureAwait(false);

      response.EnsureSuccessStatusCode();

      var contentType = response.Content.Headers.ContentType?.MediaType;

      var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

      await using (responseStream.ConfigureAwait(false))
      {
        var fileStream = new FileStream(
          tempPath,
          FileMode.Create,
          FileAccess.Write,
          FileShare.None,
          StreamBufferSize,
          useAsync: true
        );

        await using (fileStream.ConfigureAwait(false))
        {
          await responseStream.CopyToAsync(fileStream, StreamBufferSize, cancellationToken).ConfigureAwait(false);
          await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
      }

      File.Move(tempPath, destinationPath, overwrite: true);
      LogDownloadedEpisode(_logger, destinationPath);
      return contentType;
    }
    catch
    {
      TryDeleteFile(tempPath);
      throw;
    }
  }

  /// <inheritdoc />
  [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Missing or failing cover art should not abort podcast episode syncing.")]
  public async Task<byte[]?> DownloadCoverArtAsync(
    string? imageUrl,
    string podcastDir,
    CancellationToken cancellationToken
  )
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(podcastDir);

    var coverPath = Path.Combine(podcastDir, CoverFileName);

    if (File.Exists(coverPath))
    {
      return await File.ReadAllBytesAsync(coverPath, cancellationToken).ConfigureAwait(false);
    }

    if (string.IsNullOrWhiteSpace(imageUrl))
    {
      return null;
    }

    Directory.CreateDirectory(podcastDir);
    var tempPath = $"{coverPath}.tmp";

    try
    {
      using var httpClient = CreateConfiguredClient();
      using var response = await httpClient.GetAsync(
        new Uri(imageUrl, UriKind.RelativeOrAbsolute),
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken
      ).ConfigureAwait(false);

      response.EnsureSuccessStatusCode();

      var imageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

      if (imageBytes.Length is 0)
      {
        return null;
      }

      await File.WriteAllBytesAsync(tempPath, imageBytes, cancellationToken).ConfigureAwait(false);
      File.Move(tempPath, coverPath, overwrite: true);

      LogCachedCoverArt(_logger, coverPath);
      return imageBytes;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
      TryDeleteFile(tempPath);
      throw;
    }
    catch (Exception ex)
    {
      TryDeleteFile(tempPath);
      LogCoverArtFailed(_logger, imageUrl, ex);
      return null;
    }
  }

  private HttpClient CreateConfiguredClient()
  {
    var client = _httpClientFactory.CreateClient();
    client.Timeout = DefaultRequestTimeout;
    client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentHeaderValue);

    return client;
  }

  private static void TryDeleteFile(string path)
  {
    try
    {
      if (File.Exists(path))
      {
        File.Delete(path);
      }
    }
    catch (IOException)
    {
      // Best-effort cleanup of failed temporary file.
    }
    catch (UnauthorizedAccessException)
    {
      // Best-effort cleanup of failed temporary file.
    }
  }

  [LoggerMessage(Level = LogLevel.Debug, Message = "Downloaded episode audio to {DestinationPath}")]
  private static partial void LogDownloadedEpisode(ILogger logger, string destinationPath);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Cached cover art at {CoverPath}")]
  private static partial void LogCachedCoverArt(ILogger logger, string coverPath);

  [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to download cover art from {ImageUrl}")]
  private static partial void LogCoverArtFailed(ILogger logger, string imageUrl, Exception exception);
}
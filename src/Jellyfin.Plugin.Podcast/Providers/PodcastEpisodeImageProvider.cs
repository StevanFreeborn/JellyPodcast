using System.Diagnostics.CodeAnalysis;

using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Services;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Podcast.Providers;

/// <summary>
/// Provides primary cover images for podcast episodes in Jellyfin libraries by reading embedded artwork
/// from episode audio files and falling back to the podcast directory's <c>cover.jpg</c>.
/// </summary>
public partial class PodcastEpisodeImageProvider : IDynamicImageProvider
{
  private readonly ILogger<PodcastEpisodeImageProvider> _logger;
  private readonly Func<PluginConfiguration?> _configProvider;

  /// <summary>
  /// Initializes a new instance of the <see cref="PodcastEpisodeImageProvider"/> class.
  /// </summary>
  /// <param name="logger">Logger instance.</param>
  [ActivatorUtilitiesConstructor]
  public PodcastEpisodeImageProvider(ILogger<PodcastEpisodeImageProvider> logger)
    : this(logger, () => Plugin.Instance?.Configuration)
  {
  }

  internal PodcastEpisodeImageProvider(
    ILogger<PodcastEpisodeImageProvider> logger,
    Func<PluginConfiguration?> configProvider
  )
  {
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
  }

  /// <inheritdoc />
  public string Name => "JellyPodcast";

  /// <inheritdoc />
  public bool Supports(BaseItem item)
  {
    if (item is not Audio audio)
    {
      return false;
    }

    if (audio.IsShortcut || string.IsNullOrWhiteSpace(audio.Path) || Path.IsPathFullyQualified(audio.Path) is false)
    {
      return false;
    }

    var libraryPath = _configProvider()?.LibraryPath;

    if (string.IsNullOrWhiteSpace(libraryPath) || Path.IsPathFullyQualified(libraryPath) is false)
    {
      return false;
    }

    var normalizedLibraryPrefix = NormalizeDirectoryPrefix(libraryPath);
    var normalizedItemPath = Path.GetFullPath(audio.Path);

    return normalizedItemPath.StartsWith(normalizedLibraryPrefix, StringComparison.OrdinalIgnoreCase);
  }

  /// <inheritdoc />
  public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => [ImageType.Primary];

  /// <inheritdoc />
  [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Stream ownership transfers to Jellyfin's ProviderManager.")]
  public Task<DynamicImageResponse> GetImage(BaseItem item, ImageType type, CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(item);
    cancellationToken.ThrowIfCancellationRequested();

    if (type is not ImageType.Primary || Supports(item) is false || File.Exists(item.Path) is false)
    {
      return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    var embeddedPictureBytes = TryReadEmbeddedPicture(item.Path);

    if (embeddedPictureBytes is { Length: > 0 })
    {
      return Task.FromResult(
        new DynamicImageResponse
        {
          HasImage = true,
          Stream = new MemoryStream(embeddedPictureBytes, writable: false),
          Format = ImageFormatDetector.DetectImageFormat(embeddedPictureBytes),
        }
      );
    }

    var podcastDir = Path.GetDirectoryName(item.Path);

    if (string.IsNullOrWhiteSpace(podcastDir) is false)
    {
      var coverPath = Path.Combine(podcastDir, PodcastMediaDownloader.CoverFileName);

      if (File.Exists(coverPath))
      {
        return Task.FromResult(
          new DynamicImageResponse
          {
            HasImage = true,
            Path = coverPath,
            Protocol = MediaProtocol.File,
            Format = DetectFileImageFormat(coverPath),
          }
        );
      }
    }

    return Task.FromResult(new DynamicImageResponse { HasImage = false });
  }

  private byte[]? TryReadEmbeddedPicture(string filePath)
  {
    try
    {
      using var tagFile = TagLib.File.Create(filePath);
      var pictures = tagFile.Tag?.Pictures;

      if (pictures is null or { Length: 0 })
      {
        return null;
      }

      var picture =
        pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover && p.Data is { Count: > 0 }) ??
        pictures.FirstOrDefault(p => p.Data is { Count: > 0 });

      return picture?.Data.Data;
    }
    catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or UnauthorizedAccessException)
    {
      LogEmbeddedPictureReadFailed(_logger, filePath, ex);
      return null;
    }
  }

  private static ImageFormat DetectFileImageFormat(string imagePath)
  {
    Span<byte> header = stackalloc byte[12];
    using var stream = File.OpenRead(imagePath);
    var bytesRead = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
    return ImageFormatDetector.DetectImageFormat(header[..bytesRead]);
  }

  private static string NormalizeDirectoryPrefix(string path)
  {
    var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return $"{fullPath}{Path.DirectorySeparatorChar}";
  }

  [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read embedded podcast artwork from {FilePath}")]
  private static partial void LogEmbeddedPictureReadFailed(ILogger logger, string filePath, Exception exception);
}
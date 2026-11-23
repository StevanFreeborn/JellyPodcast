using MediaBrowser.Model.Drawing;

namespace Jellyfin.Plugin.Podcast.Services;

/// <summary>
/// Detects image container formats and MIME types from encoded image headers.
/// </summary>
public static class ImageFormatDetector
{
  private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
  private static ReadOnlySpan<byte> Gif87aSignature => "GIF87a"u8;
  private static ReadOnlySpan<byte> Gif89aSignature => "GIF89a"u8;
  private static ReadOnlySpan<byte> RiffSignature => "RIFF"u8;
  private static ReadOnlySpan<byte> WebpSignature => "WEBP"u8;

  private static readonly HashSet<string> SupportedExtensions =
    new([".jpg", ".jpeg", ".png", ".webp", ".gif"], StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Gets the supported image file extensions recognized for local artwork files.
  /// </summary>
  public static IReadOnlyList<string> SupportedImageExtensions { get; } =
    [".jpg", ".jpeg", ".png", ".webp", ".gif"];

  /// <summary>
  /// Detects the <see cref="ImageFormat"/> from leading image magic bytes, defaulting to <see cref="ImageFormat.Jpg"/>.
  /// </summary>
  /// <param name="imageBytes">Encoded image header or payload bytes.</param>
  /// <returns>The detected <see cref="ImageFormat"/>.</returns>
  public static ImageFormat DetectImageFormat(ReadOnlySpan<byte> imageBytes)
  {
    if (imageBytes.StartsWith(PngSignature))
    {
      return ImageFormat.Png;
    }

    if (imageBytes.StartsWith(Gif87aSignature) || imageBytes.StartsWith(Gif89aSignature))
    {
      return ImageFormat.Gif;
    }

    if (
      imageBytes.Length is >= 12 &&
      imageBytes[..4].SequenceEqual(RiffSignature) &&
      imageBytes.Slice(8, 4).SequenceEqual(WebpSignature)
    )
    {
      return ImageFormat.Webp;
    }

    return ImageFormat.Jpg;
  }

  /// <summary>
  /// Detects the MIME type from leading image magic bytes, defaulting to <c>image/jpeg</c>.
  /// </summary>
  /// <param name="imageBytes">Encoded image header or payload bytes.</param>
  /// <returns>The detected image MIME type.</returns>
  public static string DetectMimeType(ReadOnlySpan<byte> imageBytes) => DetectImageFormat(imageBytes) switch
  {
    ImageFormat.Png => "image/png",
    ImageFormat.Gif => "image/gif",
    ImageFormat.Webp => "image/webp",
    _ => "image/jpeg",
  };

  /// <summary>
  /// Detects the lowercase file extension (with leading dot) from leading image magic bytes, defaulting to <c>.jpg</c>.
  /// </summary>
  /// <param name="imageBytes">Encoded image header or payload bytes.</param>
  /// <returns>The detected image file extension.</returns>
  public static string DetectFileExtension(ReadOnlySpan<byte> imageBytes) => DetectImageFormat(imageBytes) switch
  {
    ImageFormat.Png => ".png",
    ImageFormat.Gif => ".gif",
    ImageFormat.Webp => ".webp",
    _ => ".jpg",
  };

  /// <summary>
  /// Determines whether <paramref name="filePath"/> has a supported local image file extension.
  /// </summary>
  /// <param name="filePath">Path or filename to inspect.</param>
  /// <returns><c>true</c> if the file extension is a supported image format; otherwise, <c>false</c>.</returns>
  public static bool IsSupportedImageFile(string? filePath) =>
    string.IsNullOrWhiteSpace(filePath) is false &&
    SupportedExtensions.Contains(Path.GetExtension(filePath));
}
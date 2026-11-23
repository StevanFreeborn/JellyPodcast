using Jellyfin.Plugin.Podcast.Services;

using MediaBrowser.Model.Drawing;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class ImageFormatDetectorTests
{
  [Fact]
  public void DetectImageFormat_AndMimeType_IdentifiesPngGifWebpAndJpeg()
  {
    byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];
    byte[] gif87a = "GIF87a..."u8.ToArray();
    byte[] gif89a = "GIF89a..."u8.ToArray();
    byte[] webp = "RIFF\x24\x00\x00\x00WEBP"u8.ToArray();
    byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0];

    Assert.Equal(ImageFormat.Png, ImageFormatDetector.DetectImageFormat(png));
    Assert.Equal("image/png", ImageFormatDetector.DetectMimeType(png));

    Assert.Equal(ImageFormat.Gif, ImageFormatDetector.DetectImageFormat(gif87a));
    Assert.Equal(ImageFormat.Gif, ImageFormatDetector.DetectImageFormat(gif89a));
    Assert.Equal("image/gif", ImageFormatDetector.DetectMimeType(gif89a));

    Assert.Equal(ImageFormat.Webp, ImageFormatDetector.DetectImageFormat(webp));
    Assert.Equal("image/webp", ImageFormatDetector.DetectMimeType(webp));

    Assert.Equal(ImageFormat.Jpg, ImageFormatDetector.DetectImageFormat(jpeg));
    Assert.Equal("image/jpeg", ImageFormatDetector.DetectMimeType(jpeg));
  }

  [Fact]
  public void DetectImageFormat_AndMimeType_FallsBackToJpeg_WhenUnknownOrShort()
  {
    Assert.Equal(ImageFormat.Jpg, ImageFormatDetector.DetectImageFormat([]));
    Assert.Equal("image/jpeg", ImageFormatDetector.DetectMimeType([]));

    byte[] partialWebp = "RIFF\x00\x00\x00\x00WE"u8.ToArray();
    Assert.Equal(ImageFormat.Jpg, ImageFormatDetector.DetectImageFormat(partialWebp));
    Assert.Equal("image/jpeg", ImageFormatDetector.DetectMimeType([0x01, 0x02, 0x03]));
  }
}
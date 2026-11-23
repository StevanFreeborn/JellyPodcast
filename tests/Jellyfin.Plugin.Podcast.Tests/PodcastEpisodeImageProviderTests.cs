using Jellyfin.Plugin.Podcast.Configuration;
using Jellyfin.Plugin.Podcast.Providers;
using Jellyfin.Plugin.Podcast.Services;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class PodcastEpisodeImageProviderTests : IDisposable
{
  private readonly string _tempDir;
  private readonly string _libraryPath;
  private readonly string _otherPath;
  private readonly AudioMetadataTagger _tagger = new();

  public PodcastEpisodeImageProviderTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), $"jellypodcast-provider-{Guid.NewGuid():N}");
    _libraryPath = Path.Combine(_tempDir, "podcasts");
    _otherPath = Path.Combine(_tempDir, "podcasts2");
    Directory.CreateDirectory(_libraryPath);
    Directory.CreateDirectory(_otherPath);
  }

  public void Dispose()
  {
    if (Directory.Exists(_tempDir))
    {
      Directory.Delete(_tempDir, recursive: true);
    }
  }

  [Fact]
  public void MetadataProperties_ExposeExpectedProviderNameAndSupportedImages()
  {
    var provider = CreateProvider(_libraryPath);

    Assert.Equal("JellyPodcast", provider.Name);
    var imageType = Assert.Single(provider.GetSupportedImages(new AudioBook()));
    Assert.Equal(ImageType.Primary, imageType);
  }

  [Fact]
  public void Supports_ReturnsTrue_OnlyForLocalAudioInsideConfiguredLibraryPath()
  {
    var provider = CreateProvider(_libraryPath);
    var episodeInLibrary = new AudioBook { Path = Path.Combine(_libraryPath, "Show", "ep1.mp3") };
    var audioInLibrary = new Audio { Path = Path.Combine(_libraryPath, "Show", "ep2.mp3") };
    var siblingPrefixAudio = new AudioBook { Path = Path.Combine(_otherPath, "Show", "ep3.mp3") };
    var remoteAudio = new AudioBook { Path = "https://example.com/ep4.mp3" };
    var folderItem = new Folder { Path = Path.Combine(_libraryPath, "Show") };

    Assert.True(provider.Supports(episodeInLibrary));
    Assert.True(provider.Supports(audioInLibrary));
    Assert.False(provider.Supports(siblingPrefixAudio));
    Assert.False(provider.Supports(remoteAudio));
    Assert.False(provider.Supports(folderItem));
    Assert.False(CreateProvider(null).Supports(episodeInLibrary));
    Assert.False(CreateProvider("  ").Supports(episodeInLibrary));
  }

  [Fact]
  public async Task GetImage_ReturnsEmbeddedPictureStream_WithDetectedFormat()
  {
    var showDir = Path.Combine(_libraryPath, "ShowA");
    Directory.CreateDirectory(showDir);
    var mp3Path = Path.Combine(showDir, "ep1.mp3");
    await File.WriteAllBytesAsync(mp3Path, CreateMinimalValidMp3Bytes(), TestContext.Current.CancellationToken);

    byte[] pngCover = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x11, 0x22];
    _tagger.TagAudioFile(
      mp3Path,
      new EpisodeAudioMetadata
      {
        PodcastTitle = "ShowA",
        EpisodeTitle = "Ep 1",
        CoverArtBytes = pngCover,
      }
    );

    var provider = CreateProvider(_libraryPath);
    var item = new AudioBook { Path = mp3Path };

    var response = await provider.GetImage(item, ImageType.Primary, TestContext.Current.CancellationToken);

    Assert.True(response.HasImage);
    Assert.Equal(ImageFormat.Png, response.Format);
    Assert.NotNull(response.Stream);
    using var stream = response.Stream;
    using var copy = new MemoryStream();
    await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
    Assert.Equal(pngCover, copy.ToArray());
  }

  [Fact]
  public async Task GetImage_FallsBackToFolderCoverJpg_WhenNoEmbeddedArtwork()
  {
    var showDir = Path.Combine(_libraryPath, "ShowB");
    Directory.CreateDirectory(showDir);
    var mp3Path = Path.Combine(showDir, "ep1.mp3");
    await File.WriteAllBytesAsync(mp3Path, CreateMinimalValidMp3Bytes(), TestContext.Current.CancellationToken);

    var coverPath = Path.Combine(showDir, PodcastMediaDownloader.CoverFileName);
    byte[] webpCover = "RIFF\x24\x00\x00\x00WEBP"u8.ToArray();
    await File.WriteAllBytesAsync(coverPath, webpCover, TestContext.Current.CancellationToken);

    var provider = CreateProvider(_libraryPath);
    var item = new AudioBook { Path = mp3Path };

    var response = await provider.GetImage(item, ImageType.Primary, TestContext.Current.CancellationToken);

    Assert.True(response.HasImage);
    Assert.Equal(coverPath, response.Path);
    Assert.Equal(MediaProtocol.File, response.Protocol);
    Assert.Equal(ImageFormat.Webp, response.Format);
    Assert.Null(response.Stream);
  }

  [Fact]
  public async Task GetImage_ReturnsNoImage_WhenUnsupportedMissingOrWithoutFallback_AndFallsBackOnCorruptAudio()
  {
    var showDir = Path.Combine(_libraryPath, "ShowC");
    Directory.CreateDirectory(showDir);

    var missingItem = new AudioBook { Path = Path.Combine(showDir, "missing.mp3") };
    var corruptPath = Path.Combine(showDir, "corrupt.mp3");
    await File.WriteAllBytesAsync(corruptPath, [0x00, 0x01, 0x02], TestContext.Current.CancellationToken);
    var corruptItem = new AudioBook { Path = corruptPath };

    var provider = CreateProvider(_libraryPath);

    var wrongType = await provider.GetImage(corruptItem, ImageType.Backdrop, TestContext.Current.CancellationToken);
    var missingResult = await provider.GetImage(missingItem, ImageType.Primary, TestContext.Current.CancellationToken);
    var corruptNoCover = await provider.GetImage(corruptItem, ImageType.Primary, TestContext.Current.CancellationToken);

    Assert.False(wrongType.HasImage);
    Assert.False(missingResult.HasImage);
    Assert.False(corruptNoCover.HasImage);

    var coverPath = Path.Combine(showDir, PodcastMediaDownloader.CoverFileName);
    await File.WriteAllBytesAsync(coverPath, [0xFF, 0xD8, 0xFF, 0xE0], TestContext.Current.CancellationToken);

    var corruptWithCover = await provider.GetImage(corruptItem, ImageType.Primary, TestContext.Current.CancellationToken);
    Assert.True(corruptWithCover.HasImage);
    Assert.Equal(coverPath, corruptWithCover.Path);
    Assert.Equal(ImageFormat.Jpg, corruptWithCover.Format);
  }

  private static PodcastEpisodeImageProvider CreateProvider(string? libraryPath) => new(
    NullLogger<PodcastEpisodeImageProvider>.Instance,
    () => libraryPath is null ? null : new PluginConfiguration { LibraryPath = libraryPath }
  );

  private static byte[] CreateMinimalValidMp3Bytes()
  {
    const int frameSize = 417;
    var bytes = new byte[frameSize * 2];

    for (var frame = 0; frame < 2; frame++)
    {
      var offset = frame * frameSize;
      bytes[offset] = 0xFF;
      bytes[offset + 1] = 0xFB;
      bytes[offset + 2] = 0x90;
      bytes[offset + 3] = 0x00;
    }

    return bytes;
  }
}
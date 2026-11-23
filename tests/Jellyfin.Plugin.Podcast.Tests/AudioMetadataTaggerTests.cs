using Jellyfin.Plugin.Podcast.Services;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class AudioMetadataTaggerTests : IDisposable
{
  private readonly string _tempDir;
  private readonly AudioMetadataTagger _tagger = new();

  public AudioMetadataTaggerTests()
  {
    _tempDir = Path.Combine(Path.GetTempPath(), $"JellyPodcast_TagTests_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tempDir);
  }

  public void Dispose()
  {
    if (Directory.Exists(_tempDir))
    {
      Directory.Delete(_tempDir, recursive: true);
    }

    GC.SuppressFinalize(this);
  }

  [Fact]
  public void TagAudioFile_WritesAlbumArtistTitleTrackYearAndComment()
  {
    var mp3Path = WriteMinimalMp3("episode.mp3");
    var metadata = new EpisodeAudioMetadata
    {
      PodcastTitle = "Tech Deep Dive",
      Author = "Jane Host",
      EpisodeTitle = "Episode 42: Native Audio",
      TrackNumber = 42,
      Year = 2026,
      Description = "Show notes with details."
    };

    _tagger.TagAudioFile(mp3Path, metadata);

    using var tagged = TagLib.File.Create(mp3Path);
    Assert.Equal("Tech Deep Dive", tagged.Tag.Album);
    var item = Assert.Single(tagged.Tag.AlbumArtists);
    Assert.Equal("Jane Host", item);
    var item_2 = Assert.Single(tagged.Tag.Performers);
    Assert.Equal("Jane Host", item_2);
    Assert.Equal("Episode 42: Native Audio", tagged.Tag.Title);
    Assert.Equal(42u, tagged.Tag.Track);
    Assert.Equal(2026u, tagged.Tag.Year);
    Assert.Equal("Show notes with details.", tagged.Tag.Comment);
  }

  [Fact]
  public void TagAudioFile_EmbedsCoverArtPicture_WhenImageBytesProvided()
  {
    var mp3Path = WriteMinimalMp3("episode_cover.mp3");
    var fakeJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
    var metadata = new EpisodeAudioMetadata
    {
      PodcastTitle = "Art Show",
      Author = "Host",
      EpisodeTitle = "With Cover",
      TrackNumber = 1,
      Year = 2026,
      CoverArtBytes = fakeJpeg,
      CoverArtMimeType = "image/jpeg"
    };

    _tagger.TagAudioFile(mp3Path, metadata);

    using var tagged = TagLib.File.Create(mp3Path);
    var item = Assert.Single(tagged.Tag.Pictures);
    Assert.Equal("image/jpeg", item.MimeType);
    Assert.Equal(fakeJpeg, tagged.Tag.Pictures[0].Data.Data);
  }

  [Fact]
  public void TagAudioFile_DoesNotThrow_WhenCoverArtIsNullOrEmpty()
  {
    var mp3NullPath = WriteMinimalMp3("episode_null_cover.mp3");
    var mp3EmptyPath = WriteMinimalMp3("episode_empty_cover.mp3");

    _tagger.TagAudioFile(
      mp3NullPath,
      new EpisodeAudioMetadata
      {
        PodcastTitle = "Show",
        EpisodeTitle = "Null Cover",
        CoverArtBytes = null
      }
    );

    _tagger.TagAudioFile(
      mp3EmptyPath,
      new EpisodeAudioMetadata
      {
        PodcastTitle = "Show",
        EpisodeTitle = "Empty Cover",
        CoverArtBytes = Array.Empty<byte>()
      }
    );

    using var taggedNull = TagLib.File.Create(mp3NullPath);
    using var taggedEmpty = TagLib.File.Create(mp3EmptyPath);
    Assert.Empty(taggedNull.Tag.Pictures);
    Assert.Empty(taggedEmpty.Tag.Pictures);
  }

  [Fact]
  public void TagAudioFile_UsesDefaultJpegMimeType_AndSkipsEmptyMetadataFields()
  {
    var mp3Path = WriteMinimalMp3("episode_minimal.mp3");
    var fakeImage = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };

    _tagger.TagAudioFile(
      mp3Path,
      new EpisodeAudioMetadata
      {
        PodcastTitle = string.Empty,
        Author = string.Empty,
        EpisodeTitle = string.Empty,
        TrackNumber = 0,
        Year = 0,
        Description = string.Empty,
        CoverArtBytes = fakeImage,
        CoverArtMimeType = "   "
      }
    );

    using var tagged = TagLib.File.Create(mp3Path);
    Assert.Equal(["Podcast"], tagged.Tag.Genres);
    var item = Assert.Single(tagged.Tag.Pictures);
    Assert.Equal("image/jpeg", item.MimeType);
  }

  private string WriteMinimalMp3(string fileName)
  {
    var path = Path.Combine(_tempDir, fileName);
    File.WriteAllBytes(path, CreateMinimalValidMp3Bytes());
    return path;
  }

  private static byte[] CreateMinimalValidMp3Bytes()
  {
    // Two silent MPEG-1 Layer III frames (128 kbps, 44100 Hz => 417 bytes per frame)
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
using Jellyfin.Plugin.Podcast.Services;

namespace Jellyfin.Plugin.Podcast.Tests;

public sealed class AudioFileNamingTests
{
  [Fact]
  public void SanitizeFileName_StripsIllegalCharacters_AndTrimsWhitespace()
  {
    var raw = "  My <Podcast>: \"Episode\" / \\ | ? * \0\t\r\n\x1F Show...   ";

    var result = AudioFileNaming.SanitizeFileName(raw);

    Assert.Equal("My Podcast Episode Show", result);
  }

  [Theory]
  [InlineData(null, "Untitled")]
  [InlineData("", "Untitled")]
  [InlineData("   \t\r\n  ", "Untitled")]
  [InlineData("<>:\"/\\|?*...", "Untitled")]
  [InlineData("Hello\tWorld\r\nNext", "Hello World Next")]
  public void SanitizeFileName_HandlesEmptyControlAndAllIllegalInputs(string? input, string expected)
  {
    var result = AudioFileNaming.SanitizeFileName(input);

    Assert.Equal(expected, result);
  }

  [Fact]
  public void SanitizeFileName_ReturnsUntitled_WhenTruncationLeavesOnlyDots()
  {
    var result = AudioFileNaming.SanitizeFileName("..   abc", maxLength: 2);

    Assert.Equal("Untitled", result);
  }

  [Fact]
  public void BuildEpisodeFileName_FormatsDateAndTitle_WithCorrectExtension()
  {
    var pubDate = new DateTime(2026, 11, 23);

    var result = AudioFileNaming.BuildEpisodeFileName(pubDate, "Ep #42: Test/Show?", "audio/mpeg");

    Assert.Equal("2026-11-23 - Ep #42 TestShow.mp3", result);
  }

  [Fact]
  public void BuildEpisodeFileName_UsesUnknownPrefix_WhenPubDateIsMinValue()
  {
    var result = AudioFileNaming.BuildEpisodeFileName(DateTime.MinValue, "Special", "audio/mpeg");

    Assert.Equal("unknown - Special.mp3", result);
  }

  [Theory]
  [InlineData("https://cdn.example.com/ep1.m4a?token=abc", null, ".m4a")]
  [InlineData("https://cdn.example.com/EP1.MP3", null, ".mp3")]
  [InlineData("shows/ep1.m4a?token=1", null, ".m4a")]
  [InlineData("shows/ep1.unknown#x", null, ".mp3")]
  [InlineData("shows/noext", null, ".mp3")]
  [InlineData("https://cdn.example.com/stream", "audio/mpeg", ".mp3")]
  [InlineData("https://cdn.example.com/stream", "audio/mp3", ".mp3")]
  [InlineData("https://cdn.example.com/stream", "audio/mp4", ".m4a")]
  [InlineData("https://cdn.example.com/stream", "audio/x-m4a", ".m4a")]
  [InlineData("https://cdn.example.com/stream", "audio/m4a", ".m4a")]
  [InlineData("https://cdn.example.com/ep1.ogg#t=10", null, ".ogg")]
  [InlineData("https://cdn.example.com/stream", "audio/ogg", ".ogg")]
  [InlineData("https://cdn.example.com/ep1.opus?v=2", null, ".opus")]
  [InlineData("https://cdn.example.com/stream", "audio/opus; codecs=opus", ".opus")]
  [InlineData("https://cdn.example.com/stream", "audio/aac", ".aac")]
  [InlineData("https://cdn.example.com/stream", "audio/x-aac", ".aac")]
  [InlineData("https://cdn.example.com/stream", "application/ogg", ".ogg")]
  [InlineData("https://cdn.example.com/stream", null, ".mp3")]
  [InlineData("https://cdn.example.com/stream", "   ", ".mp3")]
  [InlineData("https://cdn.example.com/stream", "application/octet-stream", ".mp3")]
  public void ResolveExtension_HandlesM4aOggOpusAndQueryStrings(string? url, string? contentType, string expected)
  {
    var result = AudioFileNaming.ResolveExtension(url, contentType);

    Assert.Equal(expected, result);
  }

  [Fact]
  public void BuildEpisodeFileName_TruncatesOverlyLongTitles_WithinMaxLength()
  {
    var pubDate = new DateTime(2026, 11, 23);
    var longTitle = new string('A', 300);

    var result = AudioFileNaming.BuildEpisodeFileName(pubDate, longTitle, "audio/mpeg");

    Assert.True(result.Length is <= AudioFileNaming.MaxFileNameLength);
    Assert.StartsWith("2026-11-23 - ", result);
    Assert.EndsWith(".mp3", result);
  }

  [Theory]
  [InlineData(null, false)]
  [InlineData("", false)]
  [InlineData("   ", false)]
  [InlineData("noextension", false)]
  [InlineData("cover.jpg", false)]
  [InlineData("episode.mp3", true)]
  [InlineData("episode.OPUS", true)]
  public void IsAudioFile_DetectsSupportedAudioExtensions(string? path, bool expected)
  {
    Assert.Equal(expected, AudioFileNaming.IsAudioFile(path));
  }
}
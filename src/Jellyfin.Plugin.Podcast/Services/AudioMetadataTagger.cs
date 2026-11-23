using System.Diagnostics.CodeAnalysis;

using TagLib;

namespace Jellyfin.Plugin.Podcast.Services;

/// <summary>
/// Represents metadata to embed into a downloaded podcast episode audio file.
/// </summary>
public class EpisodeAudioMetadata
{
  /// <summary>
  /// Gets or sets the podcast title (written to Album).
  /// </summary>
  public string PodcastTitle { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the podcast author or host (written to AlbumArtists and Performers).
  /// </summary>
  public string Author { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the episode title (written to Title).
  /// </summary>
  public string EpisodeTitle { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the episode track number.
  /// </summary>
  public uint TrackNumber { get; set; }

  /// <summary>
  /// Gets or sets the release year.
  /// </summary>
  public uint Year { get; set; }

  /// <summary>
  /// Gets or sets the episode description or show notes (written to Comment).
  /// </summary>
  public string Description { get; set; } = string.Empty;

  /// <summary>
  /// Gets or sets the optional cover art image bytes.
  /// </summary>
  [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "DTO passing raw cover image bytes to TagLibSharp.")]
  public byte[]? CoverArtBytes { get; set; }

  /// <summary>
  /// Gets or sets the MIME type of <see cref="CoverArtBytes"/>.
  /// When empty, the MIME type is inferred automatically from the image bytes.
  /// </summary>
  public string CoverArtMimeType { get; set; } = string.Empty;
}

/// <summary>
/// Defines audio metadata tagging operations for downloaded podcast files.
/// </summary>
public interface IAudioMetadataTagger
{
  /// <summary>
  /// Writes ID3, Vorbis, or MP4 metadata tags and optional cover art to an audio file.
  /// </summary>
  /// <param name="filePath">Path to the downloaded audio file.</param>
  /// <param name="metadata">Metadata values to embed.</param>
  void TagAudioFile(string filePath, EpisodeAudioMetadata metadata);
}

/// <summary>
/// Tags podcast audio files using <c>TagLibSharp</c>.
/// </summary>
public class AudioMetadataTagger : IAudioMetadataTagger
{
  private static readonly string[] PodcastGenres = ["Podcast"];

  /// <inheritdoc />
  public void TagAudioFile(string filePath, EpisodeAudioMetadata metadata)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
    ArgumentNullException.ThrowIfNull(metadata);

    using var tagFile = TagLib.File.Create(filePath);

    if (string.IsNullOrWhiteSpace(metadata.PodcastTitle) is false)
    {
      tagFile.Tag.Album = metadata.PodcastTitle;
    }

    if (string.IsNullOrWhiteSpace(metadata.Author) is false)
    {
      var authors = new[] { metadata.Author };
      tagFile.Tag.AlbumArtists = authors;
      tagFile.Tag.Performers = authors;
    }

    if (string.IsNullOrWhiteSpace(metadata.EpisodeTitle) is false)
    {
      tagFile.Tag.Title = metadata.EpisodeTitle;
    }

    if (metadata.TrackNumber is > 0)
    {
      tagFile.Tag.Track = metadata.TrackNumber;
    }

    if (metadata.Year is > 0)
    {
      tagFile.Tag.Year = metadata.Year;
    }

    if (string.IsNullOrWhiteSpace(metadata.Description) is false)
    {
      tagFile.Tag.Comment = metadata.Description;
    }

    tagFile.Tag.Genres = PodcastGenres;

    if (metadata.CoverArtBytes is { Length: > 0 })
    {
      var mimeType = string.IsNullOrWhiteSpace(metadata.CoverArtMimeType)
        ? ImageFormatDetector.DetectMimeType(metadata.CoverArtBytes)
        : metadata.CoverArtMimeType;

      var picture = new Picture(new ByteVector(metadata.CoverArtBytes))
      {
        Type = PictureType.FrontCover,
        MimeType = mimeType,
        Description = "Cover",
      };

      tagFile.Tag.Pictures = [picture];
    }

    tagFile.Save();
  }
}
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Podcast.Services;

/// <summary>
/// Provides filename sanitization, episode filename formatting, and audio extension resolution.
/// </summary>
public static class AudioFileNaming
{
  /// <summary>
  /// Maximum allowed length for generated filenames to avoid path length issues across filesystems.
  /// </summary>
  public const int MaxFileNameLength = 200;

  private const string DefaultExtension = ".mp3";

  private static readonly SearchValues<char> IllegalChars =
    SearchValues.Create(['<', '>', ':', '"', '/', '\\', '|', '?', '*']);

  private static readonly SearchValues<char> QueryOrFragmentChars =
    SearchValues.Create(['?', '#']);

  private static readonly SearchValues<char> InvalidFileNameChars =
    SearchValues.Create(Path.GetInvalidFileNameChars());

  private static readonly string[] SupportedExtensions = [".mp3", ".m4a", ".aac", ".ogg", ".opus"];

  /// <summary>
  /// Gets the set of supported podcast audio file extensions.
  /// </summary>
  public static IReadOnlySet<string> SupportedAudioExtensions { get; } =
    new HashSet<string>(SupportedExtensions, StringComparer.OrdinalIgnoreCase);

  /// <summary>
  /// Sanitizes an arbitrary string into a safe filename for Linux, Windows, and SMB filesystems.
  /// </summary>
  /// <param name="input">The raw title or name.</param>
  /// <param name="maxLength">Maximum output character length.</param>
  /// <returns>A sanitized, non-empty filename segment.</returns>
  public static string SanitizeFileName(string? input, int maxLength = MaxFileNameLength)
  {
    if (string.IsNullOrWhiteSpace(input))
    {
      return "Untitled";
    }

    var builder = new StringBuilder(input.Length);
    var previousWasSpace = false;

    foreach (var ch in input)
    {
      if (char.IsControl(ch))
      {
        if (ch is '\t' or '\r' or '\n' && previousWasSpace is false && builder.Length is > 0)
        {
          builder.Append(' ');
          previousWasSpace = true;
        }

        continue;
      }

      if (IllegalChars.Contains(ch) || InvalidFileNameChars.Contains(ch))
      {
        continue;
      }

      if (char.IsWhiteSpace(ch))
      {
        if (previousWasSpace is false && builder.Length is > 0)
        {
          builder.Append(' ');
          previousWasSpace = true;
        }

        continue;
      }

      builder.Append(ch);
      previousWasSpace = false;
    }

    var sanitized = builder.ToString().Trim().TrimEnd('.');

    if (string.IsNullOrWhiteSpace(sanitized))
    {
      return "Untitled";
    }

    if (maxLength is > 0 && sanitized.Length > maxLength)
    {
      sanitized = sanitized[..maxLength].Trim().TrimEnd('.');

      if (string.IsNullOrWhiteSpace(sanitized))
      {
        return "Untitled";
      }
    }

    return sanitized;
  }

  /// <summary>
  /// Resolves the audio file extension from an enclosure URL path or HTTP MIME content type.
  /// </summary>
  /// <param name="mediaUrl">The enclosure media URL.</param>
  /// <param name="contentType">The MIME content type (e.g., <c>audio/mpeg</c> or <c>audio/mp4</c>).</param>
  /// <returns>A normalized lowercase audio extension starting with a dot (defaults to <c>.mp3</c>).</returns>
  [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "RSS enclosures and test inputs may provide raw or partial URL strings.")]
  public static string ResolveExtension(string? mediaUrl, string? contentType)
  {
    var urlExtension = TryGetExtensionFromUrl(mediaUrl);

    if (string.IsNullOrEmpty(urlExtension) is false)
    {
      return urlExtension;
    }

    if (string.IsNullOrWhiteSpace(contentType) is false)
    {
      var mimeType = contentType.Split(';', 2)[0].Trim().ToUpperInvariant();

      switch (mimeType)
      {
        case "AUDIO/MPEG":
        case "AUDIO/MP3":
          return ".mp3";
        case "AUDIO/MP4":
        case "AUDIO/X-M4A":
        case "AUDIO/M4A":
          return ".m4a";
        case "AUDIO/AAC":
        case "AUDIO/X-AAC":
          return ".aac";
        case "AUDIO/OGG":
        case "APPLICATION/OGG":
          return ".ogg";
        case "AUDIO/OPUS":
          return ".opus";
      }
    }

    return DefaultExtension;
  }

  /// <summary>
  /// Builds a formatted episode filename in the form <c>yyyy-MM-dd - Title.ext</c> truncated within <see cref="MaxFileNameLength"/>.
  /// </summary>
  /// <param name="pubDate">Episode publication date.</param>
  /// <param name="title">Episode title.</param>
  /// <param name="contentType">Enclosure MIME content type.</param>
  /// <param name="mediaUrl">Optional enclosure URL for extension inference.</param>
  /// <returns>A safe episode filename including extension.</returns>
  [SuppressMessage("Design", "CA1054:URI-like parameters should not be strings", Justification = "RSS enclosures may provide raw or partial URL strings.")]
  public static string BuildEpisodeFileName(DateTime pubDate, string title, string? contentType, string? mediaUrl = null)
  {
    var datePrefix = pubDate != DateTime.MinValue
      ? pubDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
      : "unknown";

    var extension = ResolveExtension(mediaUrl, contentType);
    const string separator = " - ";
    var titleBudget = Math.Max(16, MaxFileNameLength - datePrefix.Length - separator.Length - extension.Length);
    var safeTitle = SanitizeFileName(title, titleBudget);

    return $"{datePrefix}{separator}{safeTitle}{extension}";
  }

  /// <summary>
  /// Determines whether the specified file path has a supported podcast audio extension.
  /// </summary>
  /// <param name="filePath">Path or filename to inspect.</param>
  /// <returns><c>true</c> if the file extension is a supported audio extension; otherwise, <c>false</c>.</returns>
  public static bool IsAudioFile(string? filePath)
  {
    if (string.IsNullOrWhiteSpace(filePath))
    {
      return false;
    }

    var extension = Path.GetExtension(filePath);
    return string.IsNullOrEmpty(extension) is false && SupportedAudioExtensions.Contains(extension);
  }

  private static string? TryGetExtensionFromUrl(string? mediaUrl)
  {
    if (string.IsNullOrWhiteSpace(mediaUrl))
    {
      return null;
    }

    string pathToInspect;

    if (Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri))
    {
      pathToInspect = uri.AbsolutePath;
    }
    else
    {
      var queryIndex = mediaUrl.AsSpan().IndexOfAny(QueryOrFragmentChars);
      pathToInspect = queryIndex is >= 0 ? mediaUrl[..queryIndex] : mediaUrl;
    }

    var ext = Path.GetExtension(pathToInspect);

    if (string.IsNullOrEmpty(ext))
    {
      return null;
    }

    foreach (var supported in SupportedExtensions)
    {
      if (string.Equals(ext, supported, StringComparison.OrdinalIgnoreCase))
      {
        return supported;
      }
    }

    return null;
  }
}
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var mode = string.Empty;
var buildYamlPath = "build.yaml";
var manifestPath = "manifest.json";
var outputPath = "meta.json";
var versionArg = string.Empty;
var zipPath = string.Empty;
var sourceUrl = string.Empty;
var changelogArg = string.Empty;

for (var i = 0; i < args.Length; i++)
{
  var arg = args[i];
  switch (arg)
  {
    case "--write-meta":
      mode = "write-meta";
      break;
    case "--update-manifest":
      mode = "update-manifest";
      break;
    case "--build-yaml":
      buildYamlPath = RequireValue(args, ref i, arg);
      break;
    case "--manifest":
      manifestPath = RequireValue(args, ref i, arg);
      break;
    case "--output":
      outputPath = RequireValue(args, ref i, arg);
      break;
    case "--version":
      versionArg = RequireValue(args, ref i, arg);
      break;
    case "--zip":
      zipPath = RequireValue(args, ref i, arg);
      break;
    case "--source-url":
      sourceUrl = RequireValue(args, ref i, arg);
      break;
    case "--changelog":
      changelogArg = i + 1 < args.Length ? args[++i] : string.Empty;
      break;
    default:
      Console.Error.WriteLine($"Unknown argument '{arg}'.");
      return 1;
  }
}

if (string.IsNullOrEmpty(mode))
{
  Console.Error.WriteLine("Specify either --write-meta or --update-manifest.");
  return 1;
}

if (File.Exists(buildYamlPath) is false)
{
  Console.Error.WriteLine($"build.yaml not found at '{buildYamlPath}'.");
  return 1;
}

if (string.IsNullOrWhiteSpace(versionArg))
{
  Console.Error.WriteLine("--version is required.");
  return 1;
}

var metadata = ParseBuildYaml(File.ReadAllLines(buildYamlPath));
var normalizedVersion = NormalizeVersion(versionArg);
var timestamp = TimeProvider.System.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

if (mode is "write-meta")
{
  var meta = new PluginMeta
  {
    Guid = metadata.Guid,
    Name = metadata.Name,
    Description = metadata.Description,
    Overview = metadata.Overview,
    Owner = metadata.Owner,
    Category = metadata.Category,
    Version = normalizedVersion,
    TargetAbi = metadata.TargetAbi,
    Timestamp = timestamp,
    Changelog = ExtractChangelog(metadata.Changelog, normalizedVersion),
    Status = "Active",
    AutoUpdate = true,
    Assemblies = ["Jellyfin.Plugin.Podcast.dll", "TagLibSharp.dll"],
  };

  var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));

  if (string.IsNullOrEmpty(dir) is false)
  {
    Directory.CreateDirectory(dir);
  }

  File.WriteAllText(outputPath, JsonSerializer.Serialize(meta, ManifestJsonContext.Default.PluginMeta));
  Console.WriteLine($"Wrote plugin metadata to '{outputPath}' (v{normalizedVersion}).");
  return 0;
}

if (string.IsNullOrWhiteSpace(zipPath) || File.Exists(zipPath) is false)
{
  Console.Error.WriteLine($"Zip package not found at '{zipPath}'.");
  return 1;
}

if (string.IsNullOrWhiteSpace(sourceUrl))
{
  Console.Error.WriteLine("--source-url is required for --update-manifest.");
  return 1;
}

var zipBytes = File.ReadAllBytes(zipPath);
var checksum = ComputeJellyfinMd5(zipBytes);

var changelog = string.IsNullOrWhiteSpace(changelogArg) is false
  ? changelogArg.Trim()
  : ExtractChangelog(metadata.Changelog, normalizedVersion);

List<ManifestPlugin> plugins;

if (File.Exists(manifestPath))
{
  plugins = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), ManifestJsonContext.Default.ListManifestPlugin) ?? [];
}
else
{
  plugins = [];
}

var pluginEntry = plugins.FirstOrDefault(p => string.Equals(p.Guid, metadata.Guid, StringComparison.OrdinalIgnoreCase));

if (pluginEntry is null)
{
  pluginEntry = new ManifestPlugin
  {
    Guid = metadata.Guid,
    Versions = [],
  };
  plugins.Add(pluginEntry);
}

pluginEntry.Name = metadata.Name;
pluginEntry.Description = metadata.Description;
pluginEntry.Overview = metadata.Overview;
pluginEntry.Owner = metadata.Owner;
pluginEntry.Category = metadata.Category;

pluginEntry.Versions.RemoveAll(v => string.Equals(v.Version, normalizedVersion, StringComparison.OrdinalIgnoreCase));
pluginEntry.Versions.Insert(
  0,
  new ManifestVersion
  {
    Version = normalizedVersion,
    Changelog = changelog,
    TargetAbi = metadata.TargetAbi,
    SourceUrl = sourceUrl,
    Checksum = checksum,
    Timestamp = timestamp,
  }
);

File.WriteAllText(manifestPath, JsonSerializer.Serialize(plugins, ManifestJsonContext.Default.ListManifestPlugin));
Console.WriteLine($"Updated '{manifestPath}' for v{normalizedVersion} (md5={checksum}).");
return 0;

[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "Jellyfin plugin repository manifest.json schema requires MD5 package checksums.")]
static string ComputeJellyfinMd5(byte[] data) => Convert.ToHexStringLower(MD5.HashData(data));

static string RequireValue(string[] arguments, ref int index, string optionName)
{
  if (index + 1 >= arguments.Length)
  {
    throw new ArgumentException($"Missing value for '{optionName}'.");
  }

  return arguments[++index];
}

static string NormalizeVersion(string raw)
{
  var trimmed = raw.Trim().TrimStart('v', 'V');
  var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);

  if (parts.Length is < 3 or > 4 || parts.All(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)) is false)
  {
    throw new ArgumentException($"Invalid version '{raw}'. Expected X.Y.Z or X.Y.Z.W.");
  }

  return parts.Length is 3 ? $"{trimmed}.0" : trimmed;
}

static string ExtractChangelog(string fullChangelog, string normalizedVersion)
{
  if (string.IsNullOrWhiteSpace(fullChangelog))
  {
    return $"Release v{normalizedVersion}";
  }

  var shortVersion = normalizedVersion.EndsWith(".0", StringComparison.Ordinal)
    ? normalizedVersion[..^2]
    : normalizedVersion;

  var lines = fullChangelog.Split('\n');
  var collecting = false;
  var sb = new StringBuilder();

  foreach (var rawLine in lines)
  {
    var line = rawLine.TrimEnd('\r');

    if (line.StartsWith("## ", StringComparison.Ordinal))
    {
      if (collecting)
      {
        break;
      }

      var header = line[3..].Trim();

      if (
        string.Equals(header, normalizedVersion, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(header, shortVersion, StringComparison.OrdinalIgnoreCase)
      )
      {
        collecting = true;
      }

      continue;
    }

    if (collecting)
    {
      sb.Append(line).Append('\n');
    }
  }

  var extracted = sb.ToString().Trim();
  return string.IsNullOrWhiteSpace(extracted) ? $"Release v{normalizedVersion}" : extracted;
}

static BuildMetadata ParseBuildYaml(string[] lines)
{
  var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
  var changelogBuilder = new StringBuilder();
  var inChangelog = false;

  foreach (var rawLine in lines)
  {
    var line = rawLine.TrimEnd('\r');

    if (inChangelog)
    {
      if (line.Length is 0 || line.StartsWith("  ", StringComparison.Ordinal))
      {
        changelogBuilder.AppendLine(line.Length is >= 2 ? line[2..] : string.Empty);
        continue;
      }

      inChangelog = false;
    }

    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith('#'))
    {
      continue;
    }

    var colonIndex = line.IndexOf(':', StringComparison.Ordinal);
    if (colonIndex is <= 0)
    {
      continue;
    }

    var key = line[..colonIndex].Trim();
    var value = line[(colonIndex + 1)..].Trim();

    if (string.Equals(key, "changelog", StringComparison.OrdinalIgnoreCase) && value is "|")
    {
      inChangelog = true;
      continue;
    }

    if (value.Length is >= 2 && value.StartsWith('"') && value.EndsWith('"'))
    {
      value = value[1..^1];
    }

    values[key] = value;
  }

  return new BuildMetadata(
    Guid: values.GetValueOrDefault("guid", string.Empty),
    Name: values.GetValueOrDefault("name", string.Empty),
    TargetAbi: values.GetValueOrDefault("targetAbi", "12.0.0.0"),
    Owner: values.GetValueOrDefault("owner", string.Empty),
    Category: values.GetValueOrDefault("category", "General"),
    Description: values.GetValueOrDefault("description", string.Empty),
    Overview: values.GetValueOrDefault("overview", string.Empty),
    Changelog: changelogBuilder.ToString()
  );
}

[JsonSourceGenerationOptions(
  WriteIndented = true,
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(PluginMeta))]
[JsonSerializable(typeof(List<ManifestPlugin>))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;

internal sealed record BuildMetadata(
  string Guid,
  string Name,
  string TargetAbi,
  string Owner,
  string Category,
  string Description,
  string Overview,
  string Changelog
);

internal sealed class PluginMeta
{
  public string Guid { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public string Overview { get; set; } = string.Empty;
  public string Owner { get; set; } = string.Empty;
  public string Category { get; set; } = string.Empty;
  public string Version { get; set; } = string.Empty;
  public string TargetAbi { get; set; } = string.Empty;
  public string Timestamp { get; set; } = string.Empty;
  public string Changelog { get; set; } = string.Empty;
  public string Status { get; set; } = string.Empty;
  public bool AutoUpdate { get; set; } = true;
  public List<string> Assemblies { get; set; } = [];
}

internal sealed class ManifestPlugin
{
  public string Guid { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string Description { get; set; } = string.Empty;
  public string Overview { get; set; } = string.Empty;
  public string Owner { get; set; } = string.Empty;
  public string Category { get; set; } = string.Empty;
  public List<ManifestVersion> Versions { get; set; } = [];
}

internal sealed class ManifestVersion
{
  public string Version { get; set; } = string.Empty;
  public string Changelog { get; set; } = string.Empty;
  public string TargetAbi { get; set; } = string.Empty;
  public string SourceUrl { get; set; } = string.Empty;
  public string Checksum { get; set; } = string.Empty;
  public string Timestamp { get; set; } = string.Empty;
}
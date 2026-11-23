using Jellyfin.Plugin.Podcast.Configuration;

using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Podcast;

/// <summary>
/// The main plugin class.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "Required by Jellyfin plugin convention.")]
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
  /// <summary>
  /// Initializes a new instance of the <see cref="Plugin"/> class.
  /// </summary>
  /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
  /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
  public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
  {
    Instance = this;
  }

  /// <inheritdoc />
  public override string Name => "JellyPodcast";

  /// <inheritdoc />
  public override Guid Id => Guid.Parse("a3af35b9-8c3f-4349-8fcb-18561540fbfc");

  /// <summary>
  /// Gets the current plugin instance.
  /// </summary>
  public static Plugin? Instance { get; internal set; }

  /// <inheritdoc />
  public IEnumerable<PluginPageInfo> GetPages()
  {
    return
    [
      new PluginPageInfo
      {
        Name = Name,
        EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
      },
    ];
  }
}
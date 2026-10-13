using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SpotiSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SpotiSync;

/// <summary>The SpotiSync plugin.</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin id, also used in build.yaml and manifest.json.</summary>
    public const string PluginGuid = "c3789a51-d16f-4d05-9f4f-8baf5990764b";

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "SpotiSync";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public override string Description => "Syncs your Spotify playlists into Jellyfin playlists.";

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "SpotiSync",
                DisplayName = "SpotiSync",
                EmbeddedResourcePath = GetType().Namespace + ".Web.spotisync.html",
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "library_music",
            },
        ];
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.BulkMetadata.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.BulkMetadata;

/// <summary>
/// Bulk Metadata Editor plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Bulk Metadata Editor";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("52153ac0-2dcf-4319-a322-9c9415665d48");

    /// <inheritdoc />
    public override string Description => "Find items across all libraries and bulk edit genres, tags, studios, ratings, titles, dates and more — with preview, undo and CSV round-trip.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "BulkMetadata",
                DisplayName = "Bulk Metadata Editor",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Web.bulkmetadata.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "server",
                MenuIcon = "edit_note",
            },
        };
    }
}

// SMSLIBRE — which reader handles a path, and running it.
//
// This lives in the library rather than the CLI because more than one caller
// needs the answer and they must agree. The regression suite re-imports cards
// from a corpus; when it asked AdaptHost directly it saw only the ADAPT plugins,
// so every card belonging to one of our own readers silently skipped and the
// suite reported green while testing nothing.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SmsLibre.Import;

public static class CardImporter
{
    /// <summary>
    /// Every reader that can handle a path: the ADAPT plugins first, then the
    /// formats we read ourselves. Ours are offered only when no plugin claims
    /// the path, so a real vendor reader always wins.
    /// </summary>
    public static List<PluginInfo> Detect(AdaptHost host, string path)
    {
        var hits = host.Detect(path).ToList();
        if (hits.Count > 0) return hits;

        if (RavenReader.CanRead(path))
            hits.Add(new PluginInfo(RavenReader.FormatName, "built-in", "SMSLIBRE"));
        else if (RavenViperReader.CanRead(path))
            hits.Add(new PluginInfo(RavenViperReader.FormatName, "built-in", "SMSLIBRE"));
        // Loose logs before archives: a folder often holds both the .jdl files
        // and a zip of the same logs, and reading what is already on disk beats
        // unpacking a copy of it.
        else if (LooseGen4.CanRead(path))
            hits.Add(new PluginInfo(LooseGen4.FormatName, "built-in", "SMSLIBRE"));
        else if (ArchivedCard.CanRead(path))
            hits.Add(new PluginInfo(ArchivedCard.FormatName, "built-in", "SMSLIBRE"));
        return hits;
    }

    /// <summary>
    /// Import a path with whichever reader fits. Archives call back into this,
    /// so a card zipped inside a folder is imported exactly as it would be
    /// unzipped.
    /// </summary>
    /// <param name="pluginName">A reader named by the caller — the QGIS dialog
    /// passes back whatever detect reported, including our own format names.</param>
    /// <param name="depth">Guards against an archive that contains an archive.</param>
    public static (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries) Import(
        AdaptHost host, string path, string? pluginName = null, int depth = 0)
    {
        bool named(string format) =>
            string.Equals(pluginName, format, StringComparison.OrdinalIgnoreCase);

        // Resolve the ADAPT plugin once and carry it into ImportAll. Detect()
        // re-runs IsDataCardSupported across every loaded plugin and is slow on a
        // large card, so calling it here and again inside ImportAll would double
        // that cost on every import.
        string? adapt = pluginName ?? host.Detect(path).FirstOrDefault()?.Name;
        bool unclaimed = adapt is null;

        if (named(RavenReader.FormatName) || (unclaimed && RavenReader.CanRead(path)))
            return (RavenReader.Import(path), new List<BoundaryFeature>());

        if (named(RavenViperReader.FormatName)
            || (unclaimed && RavenViperReader.CanRead(path)))
            return (RavenViperReader.Import(path), new List<BoundaryFeature>());

        // Same order as Detect, so the import uses the reader detect promised.
        if (named(LooseGen4.FormatName) || (unclaimed && LooseGen4.CanRead(path)))
            return LooseGen4.Import(path, d => Import(host, d, null, depth + 1));

        if (depth < 2 && (named(ArchivedCard.FormatName)
                          || (unclaimed && ArchivedCard.CanRead(path))))
            return ArchivedCard.Import(path, d => Import(host, d, null, depth + 1));

        return host.ImportAll(path, adapt);
    }
}

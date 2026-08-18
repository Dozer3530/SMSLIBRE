// SMSLIBRE — a John Deere RCD folder that has been copied out of its card.
//
// A GS3 display writes `<card>/GS3_2630/<client>/RCD/…` and the Deere plugin
// looks for that shape. What travels between organisations is usually just the
// `RCD` folder, because that is where the data visibly is — so the display
// model layer is gone and the plugin declines the card at every level. Nothing
// is wrong with the data: a 172 MB lentil harvest card that no reader would
// touch gave 83 layers and 722,535 points once the layer was put back.
//
// So put it back, in a temp folder, and import that. Same idea as LooseGen4,
// which does it for Gen4 `.jdl` logs.
//
// Care is needed not to claim a card that is already intact. The folder holding
// RCD inside a real card is `<client>`, whose parent is the display model
// folder — that is the test used here. Without it this reader would claim the
// inner folder of all 53 working RCD cards in the vault and import each twice.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmsLibre.Import;

public static class LooseRcd
{
    public const string FormatName = "John Deere RCD folder (.rcd)";

    /// <summary>The folder a display writes above RCD, e.g. GS3_2630.</summary>
    private static bool IsDisplayFolder(string name) =>
        name.StartsWith("GS2_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("GS3_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("GS4_", StringComparison.OrdinalIgnoreCase);

    public static bool CanRead(string path) => Rcd(path) is not null;

    /// <summary>The stranded RCD folder under <paramref name="path"/>, if any.</summary>
    private static string? Rcd(string path)
    {
        if (!Directory.Exists(path)) return null;

        string rcd = Path.Combine(path, "RCD");
        if (!Directory.Exists(rcd)) return null;

        // A card that still has its display folder is the plugin's job.
        var parent = Directory.GetParent(path);
        if (parent is not null && IsDisplayFolder(parent.Name)) return null;

        // Nor is a card whose display folder sits further up.
        for (int i = 0; i < 6 && parent is not null; i++, parent = parent.Parent)
            if (IsDisplayFolder(parent.Name)) return null;

        // An RCD with nothing in it is a leftover, not a card.
        try
        {
            if (!Directory.EnumerateFileSystemEntries(rcd).Any()) return null;
        }
        catch { return null; }

        return rcd;
    }

    /// <summary>
    /// Rebuild `GS3_2630/<card>/RCD` in a temp folder, import it, clean up.
    /// Nothing in the source is modified.
    /// </summary>
    public static (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries) Import(
        string path,
        Func<string, (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries)> import)
    {
        string? rcd = Rcd(path);
        if (rcd is null) return (new(), new());

        string card = new DirectoryInfo(path).Name;
        string temp = Path.Combine(Path.GetTempPath(),
                                   "smslibre-rcd-" + Guid.NewGuid().ToString("N"));
        // GS3_2630 is what every working card in the vault uses, and the plugin
        // matches on the prefix rather than the exact model.
        string client = Path.Combine(temp, "GS3_2630", Sanitise(card));
        try
        {
            Directory.CreateDirectory(client);
            string dest = Path.Combine(client, "RCD");

            bool linked = TryLink(dest, rcd);
            if (!linked)
            {
                Console.Error.WriteLine("  [rcd] copying the card to rebuild its structure …");
                CopyTree(rcd, dest);
            }
            Console.Error.WriteLine(
                $"  [rcd] rebuilt GS3_2630/{card}/RCD ({(linked ? "linked" : "copied")})");

            var (layers, bounds) = import(temp);
            foreach (var l in layers)
                if (string.IsNullOrWhiteSpace(l.Field)) l.Field = card;
            return (layers, bounds);
        }
        finally
        {
            // Delete the link before the tree: removing a directory link
            // recursively would follow it and take the user's data with it.
            try
            {
                string dest = Path.Combine(client, "RCD");
                var info = new DirectoryInfo(dest);
                if (info.Exists && info.LinkTarget is not null) info.Delete();
            }
            catch { }
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Point at the card instead of copying it. Symbolic links need a privilege
    /// Windows does not grant by default, so a failure here is expected and the
    /// caller copies instead.
    /// </summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return Directory.Exists(link);
        }
        catch { return false; }
    }

    private static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
    }

    /// <summary>A folder name safe to create, keeping it recognisable.</summary>
    private static string Sanitise(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "card" : cleaned;
    }
}

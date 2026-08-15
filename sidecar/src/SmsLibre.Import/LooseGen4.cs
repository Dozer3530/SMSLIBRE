// SMSLIBRE — John Deere Gen4 logs that have been lifted out of their card.
//
// A Gen4 display writes `<card>/JD-Data/log/*.jdl`, and the Deere plugin looks
// for exactly that shape. People do not keep it: they copy the `.jdl` files into
// a folder named after the field, or unzip an export, and the layout is gone.
// The plugin then declines the folder and the data is invisible.
//
// A vault sweep found 30 such folders, including a whole season of 2025 silage
// harvest. Rebuilding the expected layout in a temp folder and pointing the
// plugin at that recovers them: the 1t silage folder went from nothing to
// 99,311 points across 6 layers, with ForageHarvesting carrying 603 channels.
//
// Nothing is modified in place — the vault is read-only as far as this tool is
// concerned, and the copy is thrown away afterwards.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmsLibre.Import;

public static class LooseGen4
{
    public const string FormatName = "John Deere Gen4 logs (.jdl)";

    /// <summary>Loose .jdl files in a folder the Deere plugin cannot read as-is.</summary>
    public static bool CanRead(string path) => Logs(path).Count > 0;

    private static List<string> Logs(string path)
    {
        if (!Directory.Exists(path)) return new List<string>();

        // A folder that still has its JD-Data tree is the plugin's job, not ours.
        if (Directory.Exists(Path.Combine(path, "JD-Data"))) return new List<string>();

        // Nor is a folder *inside* one. `JD-Data/log/2023_Delongwest` holds .jdl
        // files and looks exactly like a folder someone copied them into, but the
        // card above it is already handled — claiming it imported the same data a
        // second time, and a vault sweep counted 75 cards twice that way.
        if (InsideCard(path)) return new List<string>();

        try
        {
            return Directory.EnumerateFiles(path, "*.jdl", SearchOption.TopDirectoryOnly)
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                            .ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>True when some ancestor is a John Deere card folder.</summary>
    private static bool InsideCard(string path)
    {
        var dir = Directory.GetParent(path);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (dir.Name.Equals("JD-Data", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "JD-Data"))) return true;
            }
            catch { return false; }
        }
        return false;
    }

    /// <summary>
    /// Rebuild `JD-Data/log/` in a temp folder, import it, and clean up.
    /// </summary>
    public static (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries) Import(
        string path,
        Func<string, (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries)> import)
    {
        var logs = Logs(path);
        if (logs.Count == 0) return (new(), new());

        string temp = Path.Combine(Path.GetTempPath(),
                                   "smslibre-gen4-" + Guid.NewGuid().ToString("N"));
        string log = Path.Combine(temp, "JD-Data", "log");
        try
        {
            Directory.CreateDirectory(log);
            foreach (var f in logs)
                File.Copy(f, Path.Combine(log, Path.GetFileName(f)));

            Console.Error.WriteLine(
                $"  [gen4] rebuilt JD-Data/log from {logs.Count} loose .jdl file(s)");

            var (layers, bounds) = import(temp);
            // The card's own name is the folder the logs were found in; the
            // rebuilt temp folder must not leak into layer names.
            string card = new DirectoryInfo(path).Name;
            foreach (var l in layers)
                if (string.IsNullOrWhiteSpace(l.Field)) l.Field = card;
            return (layers, bounds);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }
}

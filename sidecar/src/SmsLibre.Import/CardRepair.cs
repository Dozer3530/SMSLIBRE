// SMSLIBRE — retrying a card that is damaged in a way we can route around.
//
// A John Deere GS3 card stores each work document as a .fdd/.fdl pair. Three
// Saskler PreSeed cards each carry one orphaned .fdl whose .fdd is gone — an
// incomplete copy, one file of ~280. The Deere plugin walks the pairs, opens
// the missing .fdd, and the FileNotFoundException kills the entire import,
// losing the ~140 intact documents beside it.
//
// The source is never touched (the vault is read-only to this tool). The card
// is copied to a temp folder without the orphans and imported from there, the
// same rebuild-in-temp pattern as LooseRcd and LooseGen4. This only runs after
// an import has already failed with a missing file, so intact cards never pay
// the copy.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SmsLibre.Import;

public static class CardRepair
{
    /// <summary>Files skipped by the last repair, for the import report.</summary>
    public static readonly List<string> SkippedFiles = new();

    /// <summary>
    /// True when the failure is worth a repair attempt: the missing file lives
    /// inside the card, so the card itself is incomplete — as opposed to a
    /// plugin dependency missing beside the executable.
    /// </summary>
    public static bool CanRepair(string cardPath, FileNotFoundException ex)
    {
        string missing = ex.FileName ?? ex.Message;
        return missing.Length > 0
               && missing.StartsWith(Path.GetFullPath(cardPath),
                                     StringComparison.OrdinalIgnoreCase)
               && Orphans(cardPath).Any();
    }

    /// <summary>
    /// Copy the card to a temp folder without its orphaned halves, import that,
    /// and clean up. Throws whatever the retry throws: one repair attempt, not a
    /// loop that hides a card with deeper problems.
    /// </summary>
    public static (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries) Import(
        string cardPath,
        Func<string, (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries)> import)
    {
        var skip = new HashSet<string>(Orphans(cardPath), StringComparer.OrdinalIgnoreCase);
        SkippedFiles.Clear();
        SkippedFiles.AddRange(skip.Select(Path.GetFileName)!);

        string temp = Path.Combine(Path.GetTempPath(),
                                   "smslibre-repair-" + Guid.NewGuid().ToString("N"));
        try
        {
            Console.Error.WriteLine(
                $"  [repair] card is missing files; retrying without " +
                $"{skip.Count} orphaned document(s): " +
                string.Join(", ", SkippedFiles.Take(3)) +
                (skip.Count > 3 ? ", …" : ""));

            foreach (var dir in Directory.EnumerateDirectories(
                         cardPath, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(
                    Path.Combine(temp, Path.GetRelativePath(cardPath, dir)));
            Directory.CreateDirectory(temp);
            foreach (var file in Directory.EnumerateFiles(
                         cardPath, "*", SearchOption.AllDirectories))
            {
                if (skip.Contains(file)) continue;
                File.Copy(file, Path.Combine(temp, Path.GetRelativePath(cardPath, file)));
            }

            return import(temp);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Document halves whose partner is missing: a .fdl without its .fdd, or a
    /// .fdd without its .fdl. The plugin needs the pair, so a lone half only
    /// ever produces a missing-file crash.
    /// </summary>
    private static IEnumerable<string> Orphans(string cardPath)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(cardPath, "*.fd?", SearchOption.AllDirectories)
                             .Where(f => f.EndsWith(".fdd", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(".fdl", StringComparison.OrdinalIgnoreCase))
                             .ToList();
        }
        catch { yield break; }

        var stems = new HashSet<string>(
            files.Select(f => f[..^1]), StringComparer.OrdinalIgnoreCase);

        foreach (var stem in stems)
        {
            bool fdd = File.Exists(stem + "d");
            bool fdl = File.Exists(stem + "l");
            if (fdd == fdl) continue;                    // pair intact (or neither)
            yield return fdd ? stem + "d" : stem + "l";
        }
    }
}

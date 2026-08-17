// SMSLIBRE — a machine data card that has been zipped up.
//
// People archive cards. On a shared drive the card is as likely to be a `.zip`
// as a folder, and every reader here wants a folder, so the data becomes
// invisible. Two shapes turned up in a vault-wide sweep:
//
//   * Raven's Viper 4 writes each job as a `.jdp` file. 224 of the 499 in the
//     vault are an ordinary zip holding a complete ISO 11783 TASKDATA.
//   * Ordinary `.zip` files holding a John Deere card. `2. Saskler\...\Combine
//     Data` contains nothing but `Case Combine.zip`, `JD 9770 #1.zip` and
//     `JD 9770 #2.zip` — 129 MB of 2022 combine data with no unzipped copy
//     anywhere in the vault.
//
// So unpack to a temp folder and run the normal import over it. Only archives
// that look like a card are claimed — TASKDATA, Gen4 `.jdl` logs, or an `RCD`
// folder — because a vault also holds thousands of shapefile and document zips
// that would otherwise be opened for nothing.
//
// The remaining 275 `.jdp` files hold Raven's native job layout (DDOP.XML plus
// .jdf/.jhf/.sct/.ab) with no TASKDATA, and are genuinely unsupported; they are
// deliberately not claimed here so the coverage report keeps reporting them as
// a gap rather than silently importing nothing.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace SmsLibre.Import;

public static class ArchivedCard
{
    public const string FormatName = "Card in an archive (.zip/.jdp)";

    /// <summary>Archives opened by the last Import call.</summary>
    public static int ArchivesRead;

    /// <summary>
    /// Archives that held only a prescription (a TZN treatment zone and its
    /// shapefile) and so produced nothing. In this vault that is 194 of 224
    /// archives, and without saying so the import looks broken rather than
    /// correctly declining to map a rate plan as logged work.
    /// </summary>
    public static int PrescriptionOnly;

    /// <summary>Extensions worth opening to look for a TASKDATA inside.</summary>
    private static readonly string[] Extensions = { ".jdp", ".zip" };

    /// <summary>True when the path is, or holds, an archive with ISOXML inside.</summary>
    public static bool CanRead(string path) => Archives(path).Any();

    /// <summary>Archives to import: the file itself, or those directly in a folder.</summary>
    public static IEnumerable<string> Archives(string path)
    {
        if (File.Exists(path))
        {
            if (IsCandidate(path) && HasCard(path)) yield return path;
            yield break;
        }
        if (!Directory.Exists(path)) yield break;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var f in files.Where(IsCandidate).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            // People keep the archive next to the folder they extracted it into.
            // Importing both is the same data twice — 32 cards in one vault
            // sweep — so let the folder win: a reader handles it without
            // unpacking 100 MB first.
            string twin = Path.Combine(path, Path.GetFileNameWithoutExtension(f));
            if (Directory.Exists(twin)) continue;

            if (HasCard(f)) yield return f;
        }
    }

    private static bool IsCandidate(string file)
        // ".jdp.zip" is RavenReader's format, not this one.
        => !file.EndsWith(".jdp.zip", StringComparison.OrdinalIgnoreCase)
           && Extensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the archive holds something a reader could import. Checked from
    /// the entry list alone, so nothing is extracted just to find out.
    /// </summary>
    private static bool HasCard(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var e in zip.Entries)
            {
                if (e.Name.Equals("TASKDATA.XML", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (e.Name.EndsWith(".jdl", StringComparison.OrdinalIgnoreCase))
                    return true;
                // An RCD folder is a John Deere GS3/GS4 card. Zip entries should
                // use forward slashes but not every writer obeys that.
                string full = e.FullName.Replace('\\', '/');
                if (full.Contains("/RCD/", StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith("RCD/", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return false; }   // not a zip, or unreadable — not ours
    }

    /// <summary>
    /// Unpack every archive and import it with <paramref name="import"/>, which
    /// takes the folder holding TASKDATA.XML. Extraction goes to a temporary
    /// folder that is always removed, so a 500 MB vault costs 14 MB at a time.
    /// </summary>
    public static (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries) Import(
        string path,
        Func<string, (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries)> import)
    {
        var layers = new List<OperationLayer>();
        var bounds = new List<BoundaryFeature>();

        foreach (var archive in Archives(path))
        {
            string temp = Path.Combine(Path.GetTempPath(),
                                       "smslibre-" + Guid.NewGuid().ToString("N"));
            try
            {
                // ExtractToDirectory rejects entries that escape the destination,
                // so a hostile archive cannot write outside the temp folder.
                ZipFile.ExtractToDirectory(archive, temp);

                ArchivesRead++;
                // Where the card sits inside the archive varies. ISOXML puts
                // TASKDATA at the top; a John Deere export wraps the card in a
                // folder named after the machine, so the level RCDPlugins claims
                // is `<temp>/9770 #1`, not `<temp>` and not the RCD folder itself.
                // Try the likely roots and keep the first that yields anything.
                string dir = temp;
                var (l, b) = (new List<OperationLayer>(), new List<BoundaryFeature>());
                int rxBefore = CardImporter.Prescriptions.Count;
                foreach (var root in CandidateRoots(temp))
                {
                    // Most candidates are not the card — the reader says so by
                    // throwing. That is the search working, not a failure, so it
                    // must not abandon the archive.
                    try { (l, b) = import(root); }
                    catch (NotSupportedException) { continue; }
                    // A prescription counts as a find. Without this the search
                    // ran on to the remaining roots, and since each sees the same
                    // TASKDATA it collected the same zones again for every one.
                    if (l.Count > 0 || b.Count > 0
                        || CardImporter.Prescriptions.Count > rxBefore) { dir = root; break; }
                }
                if (l.Count == 0 && b.Count == 0 && IsPrescription(dir)) PrescriptionOnly++;
                string job = Path.GetFileNameWithoutExtension(archive);
                foreach (var layer in l)
                {
                    // Job identity lives in the file name, not inside TASKDATA —
                    // without it every job in a folder produces layers called the
                    // same thing and they are impossible to tell apart in QGIS.
                    layer.Description = string.IsNullOrWhiteSpace(layer.Description)
                        ? job : $"{job}_{layer.Description}";
                    if (string.IsNullOrWhiteSpace(layer.Field)) layer.Field = job;
                }
                layers.AddRange(l);
                bounds.AddRange(b);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [archive] {Path.GetFileName(archive)}: {ex.Message}");
            }
            finally
            {
                try { Directory.Delete(temp, recursive: true); } catch { }
            }
        }
        return (layers, bounds);
    }

    /// <summary>
    /// True when TASKDATA declares a treatment zone but no boundary and no task
    /// log: a rate plan for a job that has not been done yet.
    /// </summary>
    private static bool IsPrescription(string taskDataDir)
    {
        try
        {
            string xml = File.ReadAllText(Path.Combine(taskDataDir, "TASKDATA.XML"));
            return xml.Contains("<TZN", StringComparison.OrdinalIgnoreCase)
                   && !xml.Contains("<TLG", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Folders inside an extracted archive worth offering to a reader, nearest
    /// first: the TASKDATA folder if there is one, then the extraction root, then
    /// a couple of levels down. Bounded deliberately — this runs detection on
    /// each candidate, which is not cheap, and a card is never buried deeply.
    /// </summary>
    private static IEnumerable<string> CandidateRoots(string temp, int maxDepth = 2)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (TaskDataFolder(temp) is string td && seen.Add(td)) yield return td;
        if (seen.Add(temp)) yield return temp;

        var level = new List<string> { temp };
        for (int d = 0; d < maxDepth; d++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                IEnumerable<string> kids;
                try { kids = Directory.EnumerateDirectories(dir); }
                catch { continue; }
                foreach (var k in kids)
                {
                    next.Add(k);
                    if (seen.Add(k)) yield return k;
                }
            }
            level = next;
            if (level.Count == 0) yield break;
        }
    }

    /// <summary>The folder holding TASKDATA.XML, which is what ISOv4 expects.</summary>
    private static string? TaskDataFolder(string root)
    {
        var hit = Directory.EnumerateFiles(root, "TASKDATA.XML", SearchOption.AllDirectories)
                           .FirstOrDefault();
        return hit is null ? null : Path.GetDirectoryName(hit);
    }
}

// SMSLIBRE — ISOXML delivered inside an archive.
//
// Raven's Viper 4 writes each job as a `.jdp` file, and a vault-wide scan found
// two different things wearing that extension:
//
//   * 95 `.jdp.zip` Slingshot job packages — jobdata.xml plus Product_N.tab.
//     RavenReader handles those.
//   * 499 plain `.jdp` files, of which 224 are an ordinary zip with a complete
//     ISO 11783 TASKDATA inside.
//
// Nothing read that second group. The ISOv4 plugin can read them perfectly well
// — it just needs a folder, not a zip. So unpack and hand it the folder.
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

public static class ArchivedIsoxml
{
    public const string FormatName = "ISOXML in archive (.jdp/.zip)";

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
            if (IsCandidate(path) && HasTaskData(path)) yield return path;
            yield break;
        }
        if (!Directory.Exists(path)) yield break;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var f in files.Where(IsCandidate).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            if (HasTaskData(f)) yield return f;
    }

    private static bool IsCandidate(string file)
        // ".jdp.zip" is RavenReader's format, not this one.
        => !file.EndsWith(".jdp.zip", StringComparison.OrdinalIgnoreCase)
           && Extensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static bool HasTaskData(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Any(e =>
                e.Name.Equals("TASKDATA.XML", StringComparison.OrdinalIgnoreCase));
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

                string? dir = TaskDataFolder(temp);
                if (dir is null) continue;

                ArchivesRead++;
                var (l, b) = import(dir);
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

    /// <summary>The folder holding TASKDATA.XML, which is what ISOv4 expects.</summary>
    private static string? TaskDataFolder(string root)
    {
        var hit = Directory.EnumerateFiles(root, "TASKDATA.XML", SearchOption.AllDirectories)
                           .FirstOrDefault();
        return hit is null ? null : Path.GetDirectoryName(hit);
    }
}

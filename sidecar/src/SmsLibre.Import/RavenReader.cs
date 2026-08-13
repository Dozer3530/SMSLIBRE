// SMSLIBRE — Raven Slingshot job reader.
//
// No ADAPT plugin exists for Raven, and none is needed: a Raven "job data
// package" (.jdp.zip, written by Viper 4 / Viper 4+) is an ordinary zip holding
//
//   <job>/<job>_fmis/jobdata.xml    job name, type, dates, software version
//   <job>/<job>_fmis/advanced.xml   customer / farm / field, products, and the
//                                   equipment sections with their DDI channels
//   <job>/<job>_fmis/Product_N.tab  the logged points, tab-separated
//
// A .tab row is:
//
//   id  timestamp  latitude  longitude  elevation  distance  speed  <values…>
//
// followed by one value per (equipment section × DDI) declared in advanced.xml
// for that product — e.g. 6 sections × 4 DDIs = 24 value columns. Columns are
// named Sec{n}_{DDI} accordingly.
//
// This reads data the user already owns, in a documented, self-describing
// layout; no vendor licence is involved.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace SmsLibre.Import;

public static class RavenReader
{
    public const string FormatName = "Raven Slingshot (.jdp)";

    /// <summary>True when the path is, or contains, a Raven job package.</summary>
    public static bool CanRead(string path)
        => JobPackages(path).Any();

    /// <summary>Every .jdp.zip involved: the file itself, or those in a folder.</summary>
    private static IEnumerable<string> JobPackages(string path)
    {
        if (File.Exists(path) && path.EndsWith(".jdp.zip", StringComparison.OrdinalIgnoreCase))
        {
            yield return path;
            yield break;
        }
        if (!Directory.Exists(path)) yield break;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(path, "*.jdp.zip", SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var f in files) yield return f;
    }

    public static List<OperationLayer> Import(string path)
    {
        var layers = new List<OperationLayer>();
        foreach (var pkg in JobPackages(path))
        {
            try { layers.AddRange(ReadPackage(pkg)); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [raven] {Path.GetFileName(pkg)}: {ex.Message}");
            }
        }
        return layers;
    }

    private static IEnumerable<OperationLayer> ReadPackage(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        var advEntry = zip.Entries.FirstOrDefault(e => e.Name.Equals("advanced.xml", StringComparison.OrdinalIgnoreCase));
        var jobEntry = zip.Entries.FirstOrDefault(e => e.Name.Equals("jobdata.xml", StringComparison.OrdinalIgnoreCase));
        var meta = ReadMetadata(advEntry, jobEntry);

        var tabs = zip.Entries
            .Where(e => e.Name.StartsWith("Product_", StringComparison.OrdinalIgnoreCase)
                        && e.Name.EndsWith(".tab", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name)
            .ToList();

        var results = new List<OperationLayer>();
        for (int i = 0; i < tabs.Count; i++)
        {
            var layer = ReadTab(tabs[i], meta, i);
            if (layer is not null && layer.Points.Count > 0) results.Add(layer);
        }
        return results;
    }

    private sealed class JobMeta
    {
        public string Job = "", Customer = "", Farm = "", Field = "", JobType = "";
        /// <summary>Per product index: the DDI codes declared for its sections.</summary>
        public List<(int Sections, List<string> Ddis)> Products = new();
    }

    private static JobMeta ReadMetadata(ZipArchiveEntry? adv, ZipArchiveEntry? job)
    {
        var m = new JobMeta();
        if (job is not null)
        {
            try
            {
                var x = XDocument.Load(job.Open());
                m.Job = Val(x.Root, "JobName");
                m.JobType = Val(x.Root, "JobType");
            }
            catch { }
        }
        if (adv is not null)
        {
            try
            {
                var x = XDocument.Load(adv.Open());
                var r = x.Root;
                if (string.IsNullOrEmpty(m.Job)) m.Job = Val(r, "JobName");
                m.Customer = ValIn(r, "Customer", "Name");
                m.Farm = ValIn(r, "Farm", "Name");
                m.Field = ValIn(r, "Field", "Name");

                // The .tab column layout is declared under
                //   Implements/Implement/tabFormat/SegmentFormat/EquipmentSection/DDI
                // — one SegmentFormat per Product_N.tab, each listing its
                // sections and the DDI channels logged for every section.
                // (Note <Segment> also contains EquipmentSection elements, but
                // those carry the physical geometry, not the column layout.)
                foreach (var fmt in Elements(r, "SegmentFormat"))
                {
                    var sections = Elements(fmt, "EquipmentSection").ToList();
                    var ddis = sections.FirstOrDefault() is XElement s0
                        ? Elements(s0, "DDI").Select(d => d.Value.Trim()).ToList()
                        : new List<string>();
                    m.Products.Add((sections.Count, ddis));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [raven] advanced.xml parse failed: {ex.Message}");
            }
        }
        if (m.Products.Count == 0)
            Console.Error.WriteLine("  [raven] no <SegmentFormat> in advanced.xml - "
                                    + "per-section channels unavailable for this job");
        return m;
    }

    private static OperationLayer? ReadTab(ZipArchiveEntry tab, JobMeta meta, int productIndex)
    {
        var layer = new OperationLayer
        {
            Grower = meta.Customer,
            Farm = meta.Farm,
            Field = meta.Field,
            OperationType = string.IsNullOrWhiteSpace(meta.JobType) ? "Raven" : meta.JobType,
            Description = string.IsNullOrWhiteSpace(meta.Job) ? tab.Name : $"{meta.Job}_{Path.GetFileNameWithoutExtension(tab.Name)}",
        };

        // Fixed leading columns. The last two are inferred from the data
        // (cumulative distance and speed) — named so that is visible.
        layer.Channels.AddRange(new[] { "elevation", "distance_inferred", "speed_inferred" });
        layer.Units.AddRange(new[] { "m", "m", "m/s" });

        var (sections, ddis) = productIndex < meta.Products.Count
            ? meta.Products[productIndex]
            : (0, new List<string>());
        for (int s = 1; s <= sections; s++)
            foreach (var ddi in ddis)
            {
                layer.Channels.Add($"Sec{s}_{ddi}");
                layer.Units.Add("");
            }

        const int fixedCols = 7;              // id, time, lat, lon, elev, dist, speed
        int expected = layer.Channels.Count;  // 3 leading + section values

        using var sr = new StreamReader(tab.Open());
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var f = line.Split('\t');
            if (f.Length < fixedCols) continue;

            if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
                !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                continue;
            if (!Coordinates.IsPlausible(lon, lat)) continue;

            DateTime.TryParse(f[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts);

            var vals = new double?[expected];
            vals[0] = Num(f, 4);   // elevation
            vals[1] = Num(f, 5);   // distance (inferred)
            vals[2] = Num(f, 6);   // speed (inferred)
            for (int c = 3; c < expected; c++)
                vals[c] = Num(f, fixedCols + (c - 3));

            layer.Points.Add(new LayerPoint { Lon = lon, Lat = lat, Timestamp = ts, Values = vals });
        }
        return layer;
    }

    private static double? Num(string[] f, int i)
        => i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : null;

    // -- small XML helpers, namespace-agnostic ------------------------------

    private static IEnumerable<XElement> Elements(XElement? root, string local)
        => root?.Descendants().Where(e => e.Name.LocalName == local) ?? Enumerable.Empty<XElement>();

    private static string Val(XElement? root, string local)
        => Elements(root, local).FirstOrDefault()?.Value.Trim() ?? "";

    private static string ValIn(XElement? root, string parent, string child)
        => Elements(root, parent).FirstOrDefault() is XElement p
            ? Elements(p, child).FirstOrDefault()?.Value.Trim() ?? "" : "";
}

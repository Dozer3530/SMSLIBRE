// SMSLIBRE — ISOXML variable-rate prescriptions.
//
// 194 of the 224 zipped-ISOXML jobs in the Olds College vault are a
// prescription: a plan for work not yet done. They imported as nothing, because
// the importer maps logged operations and boundaries and a prescription is
// neither. A rate map is worth having in QGIS on its own — and worth far more
// beside the as-applied map of the same field.
//
// TASKDATA describes the plan; the geometry lives in a shapefile beside it:
//
//   <TSK B="20W_WHEAT_2025" ...>
//     <TZN A="0">
//       <PDV A="0006" C="PDT-1" P151_rxmap="RXMAP001.ZIP"
//            P151_rxcolumn="Tgt_Rate_l" P151_rxunit="lb/ac"/>
//
// `P151_rxcolumn` names the .dbf attribute holding the target rate, so the
// units and the product come from the XML while the zones and their values come
// from the shapefile. The P151_ prefix is a manufacturer extension (Raven), not
// part of ISO 11783 — hence reading the attributes by name rather than
// expecting a schema.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace SmsLibre.Import;

/// <summary>A prescription zone: a polygon with its planned rate.</summary>
public sealed class PrescriptionZone
{
    public string Task { get; set; } = "";
    public string Field { get; set; } = "";
    public string Product { get; set; } = "";
    public string Unit { get; set; } = "";
    public double? Rate { get; set; }
    /// <summary>Rings of (lon, lat); the first is the exterior.</summary>
    public List<List<(double Lon, double Lat)>> Rings { get; } = new();
}

public static class PrescriptionReader
{
    /// <summary>Prescription zones in a folder holding TASKDATA.XML.</summary>
    public static List<PrescriptionZone> Read(string taskDataDir)
    {
        var zones = new List<PrescriptionZone>();
        string taskData = Path.Combine(taskDataDir, "TASKDATA.XML");
        if (!File.Exists(taskData))
        {
            taskData = Directory.EnumerateFiles(taskDataDir, "TASKDATA.XML",
                                                SearchOption.AllDirectories)
                                .FirstOrDefault() ?? "";
            if (taskData.Length == 0) return zones;
        }

        XDocument doc;
        try { doc = XDocument.Load(taskData); }
        catch { return zones; }
        var root = doc.Root;
        if (root is null) return zones;

        string dir = Path.GetDirectoryName(taskData) ?? taskDataDir;
        var products = root.Descendants("PDT")
                           .Where(p => p.Attribute("A") is not null)
                           .ToDictionary(p => p.Attribute("A")!.Value,
                                         p => p.Attribute("B")?.Value ?? "",
                                         StringComparer.OrdinalIgnoreCase);
        var fields = root.Descendants("PFD")
                         .Where(p => p.Attribute("A") is not null)
                         .ToDictionary(p => p.Attribute("A")!.Value,
                                       p => p.Attribute("C")?.Value ?? "",
                                       StringComparer.OrdinalIgnoreCase);

        foreach (var task in root.Descendants("TSK"))
        {
            string taskName = task.Attribute("B")?.Value ?? "";
            string fieldRef = task.Attribute("E")?.Value ?? "";
            fields.TryGetValue(fieldRef, out string? fieldName);

            // One shapefile read per task, not per variable. A task commonly
            // declares the same map under several PDV entries (one per product),
            // and reading it again for each multiplied every zone.
            var plans = task.Descendants("TZN").Descendants("PDV")
                .Select(pdv => new
                {
                    Column = pdv.Attribute("P151_rxcolumn")?.Value,
                    Unit = pdv.Attribute("P151_rxunit")?.Value ?? "",
                    Product = products.TryGetValue(pdv.Attribute("C")?.Value ?? "",
                                                   out string? pr) ? pr : "",
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Column))
                .GroupBy(x => x.Column!, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (plans.Count == 0) continue;      // a flat rate, no map

            foreach (var shape in RateShapes(dir))
                foreach (var f in shape)
                    foreach (var plan in plans)
                    {
                        if (!f.Attributes.TryGetValue(plan.Column!, out string? raw)) continue;
                        var zone = new PrescriptionZone
                        {
                            Task = taskName,
                            Field = fieldName ?? "",
                            // The shapefile names the product per zone; fall back
                            // to the task's product when it does not.
                            Product = f.Attributes.TryGetValue("Product", out string? p)
                                      && p.Length > 0 ? p : plan.Product,
                            Unit = plan.Unit,
                            Rate = Shapefile.Number(raw),
                        };
                        zone.Rings.AddRange(f.Rings);
                        if (zone.Rings.Count > 0) zones.Add(zone);
                    }
        }
        return zones;
    }

    /// <summary>
    /// Shapefiles beside TASKDATA. The name in P151_rxmap points at the archive
    /// the display received ("RXMAP001.ZIP"), which it has already unpacked to a
    /// GUID-named .shp, so the file is found by extension rather than by name.
    /// </summary>
    private static IEnumerable<List<ShapeFeature>> RateShapes(string dir)
    {
        IEnumerable<string> shps;
        try
        {
            shps = Directory.EnumerateFiles(dir, "*.shp", SearchOption.AllDirectories)
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }
        catch { yield break; }

        foreach (var shp in shps)
        {
            List<ShapeFeature> features;
            try { features = Shapefile.Read(shp); }
            catch { continue; }
            if (features.Count > 0) yield return features;
        }
    }
}

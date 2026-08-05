// SMSLIBRE — ADAPT reuse tool.
//
// Drives SMS's own AgGateway.ADAPT import engine on plain .NET (no SMS, no WPF,
// no Wine) to read real Vault data.
//
//   adapt_proof info    <taskDataDir>              catalogue summary (the reuse proof)
//   adapt_proof extract <taskDataDir> <out.geojson> pull yield points to GeoJSON
//
// The ADAPT assemblies are netstandard2.0, so the same DLLs run identically on
// Linux .NET; running here on Windows only reflects where the analysis box is.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.ApplicationDataModel.Shapes;
using AgGateway.ADAPT.ISOv4Plugin;

internal static class Program
{
    private const string SmsDir =
        @"C:\Program Files\Ag Leader Technology\SMS\NetCoreDependencies";

    private static readonly string DefaultData =
        @"C:\ProgramData\Ag Leader\SMS\Data\Data_2\Vault\AGCO ISO11783\2024\09_26\ISO_TASKDATA\0\TASKDATA";

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string path = Path.Combine(SmsDir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "info";
        string dataPath = args.Length > 1 ? args[1] : DefaultData;

        Console.WriteLine("SMSLIBRE — SMS's ADAPT engine on native .NET " + Environment.Version);
        var asm = typeof(Plugin).Assembly;
        Console.WriteLine($"Importer: {asm.GetName().Name} {asm.GetName().Version}");
        Console.WriteLine($"Dataset : {dataPath}");
        Console.WriteLine(new string('-', 70));

        if (!File.Exists(Path.Combine(dataPath, "TASKDATA.XML")))
        {
            Console.Error.WriteLine("No TASKDATA.XML at that path."); return 1;
        }

        var plugin = new Plugin();
        var models = plugin.Import(dataPath);
        if (models == null || models.Count == 0)
        {
            Console.Error.WriteLine("Import returned no data models."); return 2;
        }

        return mode switch
        {
            "extract" => Extract(models, args.Length > 2 ? args[2] : "vault_yield.geojson"),
            _ => Info(models),
        };
    }

    private static int Info(IList<ApplicationDataModel> models)
    {
        int i = 0;
        foreach (var adm in models)
        {
            i++;
            var c = adm.Catalog;
            Console.WriteLine($"\n=== ApplicationDataModel #{i} ===");
            Console.WriteLine($"  Growers {c?.Growers?.Count ?? 0}  Farms {c?.Farms?.Count ?? 0}  " +
                              $"Fields {c?.Fields?.Count ?? 0}  Crops {c?.Crops?.Count ?? 0}  " +
                              $"Products {c?.Products?.Count ?? 0}");
            Console.WriteLine($"  LoggedData docs: {adm.Documents?.LoggedData?.Count() ?? 0}");
            foreach (var f in (c?.Fields ?? Enumerable.Empty<AgGateway.ADAPT.ApplicationDataModel.Logistics.Field>()).Take(10))
                Console.WriteLine($"    • Field: {f.Description}");
        }
        Console.WriteLine("\nOK — SMS's real importer executed on native .NET.");
        return 0;
    }

    private static int Extract(IList<ApplicationDataModel> models, string outPath)
    {
        var features = new List<(double lon, double lat, double val)>();
        string chosenMeter = null, chosenUnit = null;

        foreach (var adm in models)
        {
            var logged = adm.Documents?.LoggedData;
            if (logged == null) continue;

            foreach (var ld in logged)
            foreach (var op in ld.OperationData ?? new List<OperationData>())
            {
                // Collect this operation's meters across all device-element depths.
                var meters = new List<WorkingData>();
                for (int depth = 0; depth <= op.MaxDepth; depth++)
                {
                    var uses = op.GetDeviceElementUses?.Invoke(depth);
                    if (uses == null) continue;
                    foreach (var use in uses)
                        meters.AddRange(use.GetWorkingDatas?.Invoke()
                                        ?? Enumerable.Empty<WorkingData>());
                }
                meters = meters
                    .GroupBy(m => m.Id.ReferenceId).Select(g => g.First()).ToList();

                var yield = PickYieldMeter(meters);
                if (yield == null) continue;

                if (chosenMeter == null)
                {
                    chosenMeter = $"{yield.Representation?.Code} / {yield.Representation?.Description}";
                    chosenUnit = (yield as NumericWorkingData)?.UnitOfMeasure?.Code;
                    Console.WriteLine($"Yield meter: {chosenMeter}  [{chosenUnit}]");
                    Console.WriteLine("All meters in this operation:");
                    foreach (var m in meters)
                        Console.WriteLine($"   - {m.Representation?.Code,-18} {m.Representation?.Description}");
                }

                var records = op.GetSpatialRecords?.Invoke();
                if (records == null) continue;
                foreach (var rec in records)
                {
                    if (rec.Geometry is not Point pt) continue;
                    if (rec.GetMeterValue(yield) is not NumericRepresentationValue v) continue;
                    features.Add((pt.X, pt.Y, v.Value.Value));
                }
            }
        }

        if (features.Count == 0)
        {
            Console.Error.WriteLine("No yield spatial records found."); return 3;
        }

        WriteGeoJson(outPath, features, chosenMeter, chosenUnit);

        var vals = features.Select(f => f.val).ToArray();
        Console.WriteLine($"\nExtracted {features.Count:N0} yield points");
        Console.WriteLine($"  value range {vals.Min():0.###} – {vals.Max():0.###} " +
                          $"(mean {vals.Average():0.###}) {chosenUnit}");
        Console.WriteLine($"Wrote {Path.GetFullPath(outPath)}");
        return 0;
    }

    // Prefer dry yield volume, then any yield-volume, then any yield meter.
    private static WorkingData PickYieldMeter(IEnumerable<WorkingData> meters)
    {
        bool Has(WorkingData m, string s) =>
            (m.Representation?.Code?.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) ||
            (m.Representation?.Description?.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);

        var yields = meters.Where(m => m is NumericWorkingData && Has(m, "yield")).ToList();
        return yields.FirstOrDefault(m => Has(m, "dry"))
            ?? yields.FirstOrDefault(m => Has(m, "vol"))
            ?? yields.FirstOrDefault();
    }

    private static void WriteGeoJson(
        string path, List<(double lon, double lat, double val)> feats,
        string meter, string unit)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("{\"type\":\"FeatureCollection\",");
        sb.Append($"\"properties\":{{\"meter\":{Json(meter)},\"unit\":{Json(unit)},\"source\":\"SMS ADAPT ISOv4Plugin\"}},");
        sb.Append("\"features\":[");
        for (int i = 0; i < feats.Count; i++)
        {
            var (lon, lat, val) = feats[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[");
            sb.Append(lon.ToString("R", ci)).Append(',').Append(lat.ToString("R", ci));
            sb.Append("]},\"properties\":{\"yield\":").Append(val.ToString("R", ci)).Append("}}");
        }
        sb.Append("]}");
        File.WriteAllText(path, sb.ToString());
    }

    private static string Json(string s) =>
        s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

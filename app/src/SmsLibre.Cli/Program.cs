// SMSLIBRE headless CLI — import a dataset via SMS's ADAPT engine and render its
// yield map to a PNG, entirely in native .NET. Verifies the Core + Import stack
// without a display, and doubles as a batch map-export feature.
//
//   SmsLibre.Cli <taskDataDir> <out.png>

using System;
using System.Linq;
using SmsLibre.Core;
using SmsLibre.Import;

string dataDir = args.Length > 0 ? args[0]
    : @"C:\ProgramData\Ag Leader\SMS\Data\Data_2\Vault\AGCO ISO11783\2024\09_26\ISO_TASKDATA\0\TASKDATA";
string outPng = args.Length > 1 ? args[1] : "yieldmap.png";

Console.WriteLine($"Importing via SMS ADAPT engine: {dataDir}");
var registry = Importers.CreateDefault();
var imp = registry.FindFor(dataDir);
if (imp is null) { Console.Error.WriteLine("No importer recognises that path."); return 1; }
Console.WriteLine($"Format: {imp.FormatName}");
var growers = imp.Import(dataDir);

Console.WriteLine("\nGrower / Farm / Field tree:");
Dataset? firstYield = null;
string? firstFieldName = null;
foreach (var g in growers)
{
    Console.WriteLine($"▸ {g.Name}");
    foreach (var farm in g.Farms)
    {
        Console.WriteLine($"  ▸ {farm.Name}");
        foreach (var field in farm.Fields)
        {
            var yld = field.Datasets.FirstOrDefault(d => d.Kind == DatasetKind.Yield);
            Console.WriteLine($"    • {field.Name}" +
                              (yld != null ? $"  [{yld.Points.Count:N0} yield pts]" : ""));
            if (firstYield == null && yld != null && yld.Points.Count > 0)
            { firstYield = yld; firstFieldName = field.Name; }
        }
    }
}

if (firstYield == null) { Console.Error.WriteLine("\nNo yield dataset found."); return 2; }

Console.WriteLine($"\nRendering field '{firstFieldName}' ({firstYield.Points.Count:N0} pts) -> {outPng}");
var res = YieldRaster.Render(firstYield.Points, width: 1100, height: 850, nClasses: 7, dotRadius: 1);
PngWriter.Save(res.Image, outPng);

Console.WriteLine($"  points {res.PointCount:N0}  value {res.Min:0.###}–{res.Max:0.###} " +
                  $"(mean {res.Mean:0.###})");
Console.WriteLine("  legend:");
foreach (var c in res.Legend.AsEnumerable().Reverse())
    Console.WriteLine($"    {c.Low,8:0.###} – {c.High,8:0.###}   {c.Count,7:N0} pts");
Console.WriteLine($"Wrote {System.IO.Path.GetFullPath(outPng)}");
return 0;

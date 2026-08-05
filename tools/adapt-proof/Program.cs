// SMSLIBRE — ADAPT reuse proof.
//
// Loads SMS's own AgGateway.ADAPT.ISOv4Plugin straight from the install and uses
// it to import a real ISO 11783 (ISOXML) task dataset from the Vault, printing
// what it found. If this runs, SMS's actual multi-vendor import engine is usable
// on native .NET (hence on Linux) without SMS, WPF, or Wine.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ISOv4Plugin;

internal static class Program
{
    // The ADAPT DLLs live here; resolve their siblings (e.g. Representation) at
    // runtime from the same folder.
    private const string SmsDir =
        @"C:\Program Files\Ag Leader Technology\SMS\NetCoreDependencies";

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            string path = Path.Combine(SmsDir, name);
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        string dataPath = args.Length > 0 ? args[0]
            : @"C:\ProgramData\Ag Leader\SMS\Data\Data_2\Vault\AGCO ISO11783\2024\09_26\ISO_TASKDATA\0\TASKDATA";

        Console.WriteLine("SMSLIBRE — running SMS's ACTUAL ADAPT ISOXML importer on plain .NET");
        Console.WriteLine("Runtime : .NET " + Environment.Version + " on " +
                          System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        var asm = typeof(Plugin).Assembly;
        Console.WriteLine($"Importer: {asm.GetName().Name} {asm.GetName().Version}  " +
                          $"({Path.GetFileName(asm.Location)})");
        Console.WriteLine($"Dataset : {dataPath}");
        Console.WriteLine(new string('-', 70));

        if (!File.Exists(Path.Combine(dataPath, "TASKDATA.XML")))
        {
            Console.Error.WriteLine("No TASKDATA.XML at that path.");
            return 1;
        }

        var plugin = new Plugin();
        Console.WriteLine($"Supports card? : {plugin.IsDataCardSupported(dataPath)}");

        var models = plugin.Import(dataPath);
        if (plugin.Errors != null && plugin.Errors.Count > 0)
            Console.WriteLine($"(importer reported {plugin.Errors.Count} non-fatal errors)");

        if (models == null || models.Count == 0)
        {
            Console.Error.WriteLine("Import returned no data models.");
            return 2;
        }

        int i = 0;
        foreach (ApplicationDataModel adm in models)
        {
            i++;
            var c = adm.Catalog;
            Console.WriteLine($"\n=== ApplicationDataModel #{i} ===");
            if (c == null) { Console.WriteLine("  (no catalog)"); continue; }
            Console.WriteLine($"  Growers        : {c.Growers?.Count ?? 0}");
            Console.WriteLine($"  Farms          : {c.Farms?.Count ?? 0}");
            Console.WriteLine($"  Fields         : {c.Fields?.Count ?? 0}");
            Console.WriteLine($"  FieldBoundaries: {c.FieldBoundaries?.Count ?? 0}");
            Console.WriteLine($"  Crops          : {c.Crops?.Count ?? 0}");
            Console.WriteLine($"  Products       : {c.Products?.Count ?? 0}");
            Console.WriteLine($"  Documents/LoggedData: {adm.Documents?.LoggedData?.Count() ?? 0}");

            foreach (var f in (c.Fields ?? Enumerable.Empty<AgGateway.ADAPT.ApplicationDataModel.Logistics.Field>()).Take(10))
                Console.WriteLine($"    • Field: {f.Description}  ({f.Area?.Value.Value:0.##} {f.Area?.Value.UnitOfMeasure?.Code})");
        }

        Console.WriteLine("\nOK — SMS's real importer executed on native .NET.");
        return 0;
    }
}

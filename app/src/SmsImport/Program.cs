// smsimport — SMSLIBRE sidecar for the QGIS plugin.
//
// Runs SMS's own ADAPT import plugins (John Deere, Climate, Precision Planting,
// Trimble, CNH, ISOXML, ADM) in a plain .NET process and converts a machine data
// card into a GeoPackage that QGIS opens natively.
//
// All output is a single JSON object on stdout so the Python plugin can parse it
// reliably; human-readable progress goes to stderr.
//
//   smsimport plugins --sms <dir>
//   smsimport detect  <cardPath> --sms <dir>
//   smsimport import  <cardPath> <out.gpkg> --sms <dir> [--plugin <name>]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmsLibre.Core;
using SmsLibre.Import;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static int Main(string[] args)
    {
        try { return Run(args); }
        catch (Exception ex)
        {
            Emit(new { ok = false, error = ex.Message, type = ex.GetType().Name });
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0) { Usage(); return 2; }
        string cmd = args[0].ToLowerInvariant();

        string smsDir = Opt(args, "--sms") ?? DefaultSmsDir();
        string pluginDir = Path.Combine(smsDir, "ADAPT");
        string coreDir = Path.Combine(smsDir, "NetCoreDependencies");

        if (!Directory.Exists(pluginDir))
        {
            Emit(new
            {
                ok = false,
                error = $"ADAPT plugin folder not found: {pluginDir}. " +
                        "Pass --sms <path to the SMS install folder>.",
            });
            return 1;
        }

        // The ISOv4 plugin reads its DDI tables from Resources/ beside the exe.
        EnsureResources(coreDir);

        var host = new AdaptHost(pluginDir, coreDir);

        switch (cmd)
        {
            case "plugins":
            {
                var plugins = host.ListPlugins();
                Emit(new
                {
                    ok = true, smsDir, pluginDir, count = plugins.Count, plugins,
                    // Non-fatal Initialize() failures, kept visible so a plugin
                    // that loads but cannot run is diagnosable.
                    initErrors = SmsLibre.Import.AdaptHost.InitErrors,
                });
                return 0;
            }

            case "detect":
            {
                if (args.Length < 2) { Usage(); return 2; }
                string path = args[1];
                var hits = host.Detect(path);
                Emit(new { ok = true, path, supported = hits.Count > 0, count = hits.Count, plugins = hits });
                return 0;
            }

            case "scan":
            {
                // Users keep cards in deep, messy folder trees. Walk one and
                // report every directory an installed plugin can read.
                if (args.Length < 2) { Usage(); return 2; }
                string root = args[1];
                int depth = int.TryParse(Opt(args, "--depth"), out var d) ? d : 4;
                int maxDirs = int.TryParse(Opt(args, "--max"), out var m) ? m : 3000;

                var dirs = new List<string> { root };
                dirs.AddRange(Walk(root, depth, maxDirs));

                var found = new List<object>();
                foreach (var dir in dirs)
                {
                    var hits = host.Detect(dir);
                    if (hits.Count > 0)
                    {
                        Console.Error.WriteLine($"  {hits[0].Name}: {dir}");
                        found.Add(new { path = dir, plugins = hits });
                    }
                }
                Emit(new { ok = true, root, scanned = dirs.Count, found });
                return 0;
            }

            case "import":
            {
                if (args.Length < 3) { Usage(); return 2; }
                string path = args[1];
                string outGpkg = args[2];
                string? pluginName = Opt(args, "--plugin");

                Console.Error.WriteLine($"Importing {path} …");
                var layers = host.Import(path, pluginName);
                if (layers.Count == 0)
                {
                    Emit(new { ok = true, path, layers = Array.Empty<object>(), note = "No spatial operations found." });
                    return 0;
                }

                var written = new List<object>();
                using (var gpkg = new GeoPackageWriter(outGpkg))
                {
                    var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int opIndex = 0;
                    foreach (var layer in layers)
                    {
                        // A card usually holds many logged runs whose field and
                        // operation names are identical, so lead with a sequence
                        // number: layers stay distinguishable and sort naturally
                        // in the QGIS layer tree.
                        opIndex++;
                        string stem = string.IsNullOrWhiteSpace(layer.SuggestedName)
                            ? "operation" : layer.SuggestedName;
                        string table = UniqueName(used, GeoPackageWriter.Sanitize(
                            $"op{opIndex:D2}_{stem}"));

                        // Columns: timestamp + every recorded channel.
                        var fields = new List<GpkgField> { new("timestamp", GpkgType.Text) };
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var colNames = new List<string>();
                        for (int i = 0; i < layer.Channels.Count; i++)
                        {
                            string baseName = GeoPackageWriter.Sanitize(layer.Channels[i]);
                            string name = UniqueName(seen, baseName);
                            colNames.Add(name);
                            fields.Add(new GpkgField(name, GpkgType.Double));
                        }

                        int n = gpkg.WritePointLayer(table, fields,
                            layer.Points.Select(p =>
                            {
                                var vals = new object?[fields.Count];
                                vals[0] = p.Timestamp == default
                                    ? null : p.Timestamp.ToString("o");
                                for (int i = 0; i < layer.Channels.Count; i++)
                                    vals[i + 1] = i < p.Values.Length ? p.Values[i] : null;
                                return new GpkgFeature { Lon = p.Lon, Lat = p.Lat, Values = vals };
                            }),
                            description: $"{layer.Field} {layer.OperationType}".Trim());

                        Console.Error.WriteLine($"  {table}: {n:N0} points, {layer.Channels.Count} channels");
                        written.Add(new
                        {
                            table,
                            grower = layer.Grower,
                            farm = layer.Farm,
                            field = layer.Field,
                            operationType = layer.OperationType,
                            points = n,
                            channels = colNames.Select((c, i) => new
                            {
                                column = c,
                                name = layer.Channels[i],
                                unit = i < layer.Units.Count ? layer.Units[i] : "",
                            }),
                        });
                    }
                }

                Emit(new { ok = true, path, geopackage = Path.GetFullPath(outGpkg), layers = written });
                return 0;
            }

            default:
                Usage();
                return 2;
        }
    }

    /// <summary>Breadth-first directory walk, depth- and count-limited.</summary>
    private static IEnumerable<string> Walk(string root, int maxDepth, int maxDirs)
    {
        var result = new List<string>();
        var level = new List<string> { root };
        for (int d = 0; d < maxDepth && result.Count < maxDirs; d++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                IEnumerable<string> kids;
                try { kids = Directory.EnumerateDirectories(dir); }
                catch { continue; }
                foreach (var k in kids)
                {
                    result.Add(k);
                    next.Add(k);
                    if (result.Count >= maxDirs) break;
                }
                if (result.Count >= maxDirs) break;
            }
            level = next;
            if (level.Count == 0) break;
        }
        return result;
    }

    /// <summary>The ISOv4 plugin loads ddiExport.txt etc. from ./Resources.</summary>
    private static void EnsureResources(string coreDir)
    {
        string src = Path.Combine(coreDir, "Resources");
        string dst = Path.Combine(AppContext.BaseDirectory, "Resources");
        if (!Directory.Exists(src) || Directory.Exists(dst)) return;
        try
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        }
        catch { /* best effort — import will report a clearer error if it matters */ }
    }

    private static string UniqueName(HashSet<string> used, string name)
    {
        string candidate = name;
        int i = 2;
        while (!used.Add(candidate)) candidate = $"{name}_{i++}";
        return candidate;
    }

    private static string DefaultSmsDir()
    {
        string[] guesses =
        {
            @"C:\Program Files\Ag Leader Technology\SMS",
            @"C:\Program Files (x86)\Ag Leader Technology\SMS",
        };
        return guesses.FirstOrDefault(Directory.Exists) ?? guesses[0];
    }

    private static string? Opt(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void Emit(object o) => Console.WriteLine(JsonSerializer.Serialize(o, Json));

    private static void Usage()
    {
        Console.Error.WriteLine(@"smsimport — SMS machine-data importer (SMSLIBRE sidecar)

  smsimport plugins            --sms <smsInstallDir>
  smsimport detect <cardPath>  --sms <smsInstallDir>
  smsimport import <cardPath> <out.gpkg> --sms <smsInstallDir> [--plugin <name>]

Emits a JSON object on stdout. --sms defaults to the standard install path.");
    }
}

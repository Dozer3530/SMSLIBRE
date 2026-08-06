// SMSLIBRE — ADAPT plugin feasibility probe.
//
// Answers the question behind the QGIS-plugin pivot: can ONE generic host load
// ALL of SMS's vendor import plugins (John Deere, CNH, Precision Planting,
// Trimble, Climate, ISOXML...) and auto-detect which one reads a given folder?
//
// Uses AgGateway.ADAPT.PluginManager.PluginFactory, the same mechanism SMS uses.
//
//   probe list                 -> every plugin the factory can load
//   probe detect <dataPath>    -> which plugins claim support for that path
//   probe scan   <vaultRoot>   -> walk a Vault and report detected format per folder

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AgGateway.ADAPT.PluginManager;

internal static class Probe
{
    // SMS keeps the vendor plugins here; PluginManager + the core ADAPT
    // assemblies live in NetCoreDependencies.
    private const string PluginDir  = @"C:\Program Files\Ag Leader Technology\SMS\ADAPT";
    private const string NetCoreDir = @"C:\Program Files\Ag Leader Technology\SMS\NetCoreDependencies";

    internal static int Run(string[] args)
    {
        // Resolve ADAPT dependencies from both SMS folders.
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string file = new AssemblyName(e.Name).Name + ".dll";
            foreach (var dir in new[] { NetCoreDir, PluginDir })
            {
                string p = Path.Combine(dir, file);
                if (File.Exists(p))
                {
                    try { return Assembly.LoadFrom(p); } catch { }
                }
            }
            return null;
        };

        string mode = args.Length > 1 ? args[1].ToLowerInvariant() : "list";

        var factory = new PluginFactory(PluginDir);
        List<string> names;
        try
        {
            names = factory.AvailablePlugins;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PluginFactory failed: " + ex.Message);
            return 1;
        }

        Console.WriteLine($"Plugin directory : {PluginDir}");
        Console.WriteLine($"Plugins listed   : {names.Count}");
        foreach (var n in names.OrderBy(x => x))
        {
            // Listing a plugin only reads metadata; instantiating it is the real
            // test that it can run in our host.
            try
            {
                var p = factory.GetPlugin(n);
                Console.WriteLine($"   OK   {n,-32} v{p.Version}  owner={p.Owner}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   FAIL {n,-32} {Short(ex)}");
            }
        }

        if (mode == "list") return 0;

        if (mode == "detect")
        {
            string path = args.Length > 2 ? args[2] : "";
            Console.WriteLine($"\nDetecting for: {path}");
            foreach (var n in names)
            {
                try
                {
                    var p = factory.GetPlugin(n);
                    if (p.IsDataCardSupported(path))
                        Console.WriteLine($"   SUPPORTED by: {n}  (v{p.Version})");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   [{n}] probe error: {Short(ex)}");
                }
            }
            return 0;
        }

        if (mode == "scan")
        {
            string root = args.Length > 2 ? args[2] : "";
            Console.WriteLine($"\nScanning vault: {root}\n");
            // Vendor plugins encrypt their path constants (Dotfuscator), so the
            // expected card layout can't be read statically — walk every
            // directory and ask each plugin directly.
            var candidates = new List<string> { root };
            candidates.AddRange(WalkAll(root, maxDirs: 4000));
            Console.WriteLine($"  testing {candidates.Count} directories × {names.Count} plugins…\n");

            var hits = new Dictionary<string, List<string>>();
            foreach (var dir in candidates.Distinct())
            {
                foreach (var n in names)
                {
                    try
                    {
                        var p = factory.GetPlugin(n);
                        if (p.IsDataCardSupported(dir))
                        {
                            if (!hits.TryGetValue(n, out var l)) hits[n] = l = new List<string>();
                            l.Add(dir);
                        }
                    }
                    catch { }
                }
            }

            if (hits.Count == 0) Console.WriteLine("  (no folder matched any plugin at this depth)");
            foreach (var kv in hits.OrderByDescending(k => k.Value.Count))
            {
                Console.WriteLine($"  {kv.Key}: {kv.Value.Count} folder(s)");
                foreach (var d in kv.Value.Take(3))
                    Console.WriteLine($"      {Rel(root, d)}");
                if (kv.Value.Count > 3) Console.WriteLine($"      … +{kv.Value.Count - 3} more");
            }
            return 0;
        }

        Console.Error.WriteLine("modes: list | detect <path> | scan <vaultRoot>");
        return 2;
    }

    /// <summary>Breadth-first walk of every subdirectory, capped.</summary>
    private static IEnumerable<string> WalkAll(string root, int maxDirs)
    {
        var outp = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(root);
        while (queue.Count > 0 && outp.Count < maxDirs)
        {
            foreach (var d in SafeDirs(queue.Dequeue()))
            {
                outp.Add(d);
                queue.Enqueue(d);
                if (outp.Count >= maxDirs) break;
            }
        }
        return outp;
    }

    private static IEnumerable<string> SafeDirs(string p)
    {
        try { return Directory.EnumerateDirectories(p); }
        catch { return Enumerable.Empty<string>(); }
    }

    private static string Rel(string root, string p) =>
        p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p.Substring(root.Length).TrimStart('\\') : p;

    private static string Short(Exception ex)
    {
        var m = ex.Message;
        return m.Length > 90 ? m.Substring(0, 90) + "…" : m;
    }
}

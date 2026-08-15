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
        // Vendor plugins (John Deere's in particular) write progress chatter
        // straight to stdout, which would corrupt the single-JSON-object
        // contract the QGIS plugin parses. Route stdout to stderr for the whole
        // run; Emit() restores the real stdout just long enough to print JSON.
        _stdout = Console.Out;
        Console.SetOut(Console.Error);
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

        // Extra plugin folders, e.g. John Deere's own plugin release downloaded
        // from developer.deere.com. Preferred over the copies bundled inside SMS:
        // those are Ag Leader's licensed build, whereas our licence and
        // application id were issued for Deere's distribution.
        var extraDirs = new List<string> { coreDir };
        var priorityDirs = new List<string>();
        if (Opt(args, "--plugins") is string extra)
            // PluginFactory rejects relative paths, so normalise up front.
            priorityDirs.AddRange(extra.Split(';', StringSplitOptions.RemoveEmptyEntries)
                                       .Select(Path.GetFullPath));
        foreach (var d in CredentialPaths("AdaptPlugins"))
            if (Directory.Exists(d)) { priorityDirs.Add(d); break; }

        AdaptHost.ApplicationId = ResolveApplicationId(args);
        EnsureLicenceFile(args);

        var host = new AdaptHost(pluginDir, extraDirs.ToArray(), priorityDirs.ToArray());

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
                    pluginSources = host.PluginSources,
                });
                return 0;
            }

            case "detect":
            {
                if (args.Length < 2) { Usage(); return 2; }
                string path = args[1];
                var hits = DetectAll(host, path);
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
                // Readers search recursively, so asking one about the top of a
                // vault means walking the entire share for an answer that is
                // useless: a card is never the root or a section folder, and a
                // caller collapsing nested hits discards it anyway. Measured on
                // the Olds College vault, detection on "1. Smart Farm" (some
                // 20,000 directories beneath it) did not return in 150 s, while
                // every real card answered in under a second.
                int minDepth = int.TryParse(Opt(args, "--min-depth"), out var md) ? md : 0;

                var dirs = new List<string> { root };
                dirs.AddRange(Walk(root, depth, maxDirs));
                Console.Error.WriteLine($"walked {dirs.Count:N0} directories");
                if (minDepth > 0)
                {
                    int before = dirs.Count;
                    dirs = dirs.Where(d => Depth(root, d) >= minDepth).ToList();
                    Console.Error.WriteLine(
                        $"skipping {before - dirs.Count:N0} container directories " +
                        $"above depth {minDepth}");
                }

                var found = new List<object>();
                var unclaimed = new List<object>();
                int done = 0;
                foreach (var dir in dirs)
                {
                    // A vault-wide walk runs for many minutes and the JSON only
                    // appears at the end, so say something on the way through:
                    // otherwise a long scan is indistinguishable from a hung one.
                    if (++done % 500 == 0)
                        Console.Error.WriteLine($"  … {done:N0}/{dirs.Count:N0}, {found.Count} claimed");

                    // Same rules as `detect`, so a scan cannot report a different
                    // answer from the one the import will act on.
                    var hits = DetectAll(host, dir);
                    if (hits.Count > 0)
                    {
                        Console.Error.WriteLine($"  {hits[0].Name}: {dir}");
                        found.Add(new { path = dir, plugins = hits });
                    }
                    else
                    {
                        // Report what a rejected folder holds. A caller sweeping a
                        // vault needs this to tell a format gap from a folder of
                        // PDFs, and gathering it here saves walking the whole tree
                        // a second time — expensive on a network share.
                        var exts = FileTypes(dir);
                        if (exts.Count > 0) unclaimed.Add(new { path = dir, exts });
                    }
                }
                Emit(new { ok = true, root, scanned = dirs.Count, found, unclaimed });
                return 0;
            }

            case "import":
            {
                if (args.Length < 3) { Usage(); return 2; }
                string path = args[1];
                string outGpkg = args[2];
                string? pluginName = Opt(args, "--plugin");

                Console.Error.WriteLine($"Importing {path} …");
                List<OperationLayer> layers;
                List<BoundaryFeature> boundaries;

                (layers, boundaries) = ImportPath(host, path, pluginName);
                if (layers.Count == 0 && boundaries.Count == 0)
                {
                    Emit(new { ok = true, path, layers = Array.Empty<object>(),
                               archivesRead = ArchivedCard.ArchivesRead,
                               prescriptionOnly = ArchivedCard.PrescriptionOnly,
                               note = EmptyResultNote(path) });
                    return 0;
                }

                var written = new List<object>();
                using (var gpkg = new GeoPackageWriter(outGpkg))
                {
                    // Field boundaries first: a setup card may carry only these.
                    if (boundaries.Count > 0)
                    {
                        var bFields = new List<GpkgField>
                        {
                            new("field", GpkgType.Text),
                            new("farm", GpkgType.Text),
                            new("grower", GpkgType.Text),
                            new("description", GpkgType.Text),
                        };
                        int nb = gpkg.WritePolygonLayer("field_boundaries", bFields,
                            boundaries.Select(b => {
                                var pf = new GpkgPolygonFeature
                                {
                                    Values = new object?[] { b.Field, b.Farm, b.Grower, b.Description },
                                };
                                foreach (var rings in b.Polygons)
                                {
                                    var poly = new GpkgPolygon();
                                    for (int i = 0; i < rings.Count; i++)
                                    {
                                        var ring = new GpkgRing();
                                        ring.Points.AddRange(rings[i]);
                                        if (i == 0) poly.Exterior = ring; else poly.Interior.Add(ring);
                                    }
                                    pf.Polygons.Add(poly);
                                }
                                return pf;
                            }),
                            description: "Field boundaries");
                        Console.Error.WriteLine($"  field_boundaries: {nb} boundaries");
                        written.Add(new
                        {
                            table = "field_boundaries",
                            geometry = "MultiPolygon",
                            points = nb,
                            channels = Array.Empty<object>(),
                            field = "", operationType = "Boundary",
                        });
                    }

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

                        // Columns: timestamp + every recorded channel — but SQLite
                        // refuses a table wider than 1,999 columns, and a 2022
                        // forage harvester card already writes 1,535. Past that the
                        // whole import would die on "too many columns", losing the
                        // card; dropping the emptiest channels loses far less.
                        var keep = layer.ChannelsToKeep(OperationLayer.MaxGpkgChannels);
                        var fields = new List<GpkgField> { new("timestamp", GpkgType.Text) };
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var colNames = new List<string>();
                        foreach (int i in keep)
                        {
                            string baseName = GeoPackageWriter.Sanitize(layer.Channels[i]);
                            string name = UniqueName(seen, baseName);
                            colNames.Add(name);
                            fields.Add(new GpkgField(name, GpkgType.Double));
                        }
                        int dropped = layer.Channels.Count - keep.Count;
                        if (dropped > 0)
                        {
                            droppedChannels += dropped;
                            Console.Error.WriteLine(
                                $"  {table}: {layer.Channels.Count} channels exceeds the " +
                                $"{OperationLayer.MaxGpkgChannels}-column limit; kept the {keep.Count} with the " +
                                "most readings");
                        }

                        int n = gpkg.WritePointLayer(table, fields,
                            layer.Points.Select(p =>
                            {
                                var vals = new object?[fields.Count];
                                vals[0] = p.Timestamp == default
                                    ? null : p.Timestamp.ToString("o");
                                for (int c = 0; c < keep.Count; c++)
                                {
                                    int i = keep[c];
                                    vals[c + 1] = i < p.Values.Length ? p.Values[i] : null;
                                }
                                return new GpkgFeature { Lon = p.Lon, Lat = p.Lat, Values = vals };
                            }),
                            description: $"{layer.Field} {layer.OperationType}".Trim());

                        Console.Error.WriteLine($"  {table}: {n:N0} points, {colNames.Count} channels");
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

                Emit(new { ok = true, path, geopackage = Path.GetFullPath(outGpkg), layers = written,
                           skippedGeometries = AdaptHost.SkippedGeometries,
                           rejectedPoints = AdaptHost.RejectedPoints,
                           droppedChannels,
                           archivesRead = ArchivedCard.ArchivesRead });
                return 0;
            }

            default:
                Usage();
                return 2;
        }
    }

    /// <summary>
    /// Find the vendor application id, in order of precedence:
    /// <c>--app-id</c>, the <c>SMSLIBRE_APP_ID</c> environment variable, or a
    /// <c>johndeere.appid</c> file beside the executable or in <c>secrets/</c>.
    /// Returns null when unlicensed — licence-free plugins still work.
    /// </summary>
    private static string? ResolveApplicationId(string[] args)
    {
        string? id = Opt(args, "--app-id")
                     ?? Environment.GetEnvironmentVariable("SMSLIBRE_APP_ID");
        if (!string.IsNullOrWhiteSpace(id)) return id.Trim();

        foreach (var p in CredentialPaths("johndeere.appid"))
            if (File.Exists(p))
            {
                string s = File.ReadAllText(p).Trim();
                if (!string.IsNullOrWhiteSpace(s)) return Normalise(s);
            }
        return null;
    }

    /// <summary>The John Deere guide passes the id in braces —
    /// <c>Initialize("{00000000-0000-0000-0000-000000000000}")</c> — so accept a
    /// bare GUID and brace it.</summary>
    private static string Normalise(string id)
        => Guid.TryParse(id, out var g) ? g.ToString("B") : id;

    /// <summary>
    /// The John Deere plugins load their licence from the executable's own
    /// directory, so copy it there if it lives elsewhere.
    /// </summary>
    private static void EnsureLicenceFile(string[] args)
    {
        const string name = "johndeere.adaptplugins.lic";
        string dest = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(dest)) return;

        var candidates = new List<string>();
        if (Opt(args, "--licence") is string explicitPath) candidates.Add(explicitPath);
        candidates.AddRange(CredentialPaths(name));

        foreach (var src in candidates)
        {
            if (!File.Exists(src)) continue;
            try { File.Copy(src, dest, overwrite: true); return; }
            catch { /* read-only install dir: the plugin will report the licence error */ }
        }
    }

    /// <summary>Places a credential may live, nearest first.</summary>
    private static IEnumerable<string> CredentialPaths(string fileName)
    {
        string base_ = AppContext.BaseDirectory;
        yield return Path.Combine(base_, fileName);
        // walk up to find a repo-level secrets/ folder in dev use; the exe sits
        // ~6 levels below the repo root under bin/Release/<tfm>/
        var dir = new DirectoryInfo(base_);
        for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "secrets", fileName);
            yield return Path.Combine(dir.FullName, fileName);
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
                    if (SkipDir(Path.GetFileName(k))) continue;
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

    /// <summary>
    /// A name unique within <paramref name="used"/> that also survives the
    /// writer's own length cap. Machines can log hundreds of similarly-named
    /// channels (a Gen4 seeding file had 537), and naively appending "_2" after
    /// truncation loses the suffix and collides again.
    /// </summary>
    private static string UniqueName(HashSet<string> used, string name, int maxLen = 60)
    {
        if (name.Length > maxLen) name = name.Substring(0, maxLen);
        if (used.Add(name)) return name;

        for (int i = 2; ; i++)
        {
            string suffix = "_" + i;
            string stem = name.Length + suffix.Length > maxLen
                ? name.Substring(0, maxLen - suffix.Length)
                : name;
            string candidate = stem + suffix;
            if (used.Add(candidate)) return candidate;
        }
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

    private static TextWriter? _stdout;

    /// <summary>Write the single JSON result to the real stdout.</summary>
    private static void Emit(object o)
    {
        var json = JsonSerializer.Serialize(o, Json);
        var w = _stdout ?? Console.Out;
        w.WriteLine(json);
        w.Flush();
    }

    /// <summary>Channels dropped across the whole import, reported in the result.</summary>
    private static int droppedChannels;

    /// <summary>
    /// Every reader that can handle a path: the ADAPT plugins first, then the
    /// formats we read ourselves. Ours are offered only when no plugin claims the
    /// path, so a real vendor reader always wins.
    /// </summary>
    private static List<PluginInfo> DetectAll(AdaptHost host, string path)
    {
        var hits = host.Detect(path).ToList();
        if (hits.Count > 0) return hits;

        if (RavenReader.CanRead(path))
            hits.Add(new PluginInfo(RavenReader.FormatName, "built-in", "SMSLIBRE"));
        // Loose logs before archives: a folder often holds both the .jdl files
        // and a zip of the same logs, and reading what is already on disk beats
        // unpacking a copy of it.
        else if (LooseGen4.CanRead(path))
            hits.Add(new PluginInfo(LooseGen4.FormatName, "built-in", "SMSLIBRE"));
        else if (ArchivedCard.CanRead(path))
            hits.Add(new PluginInfo(ArchivedCard.FormatName, "built-in", "SMSLIBRE"));
        return hits;
    }

    /// <summary>File extensions directly inside a folder, commonest first.</summary>
    private static List<string> FileTypes(string dir, int cap = 400)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int i = 0;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (i++ >= cap) break;
                string ext = Path.GetExtension(f);
                if (ext.Length == 0) continue;
                counts[ext] = counts.GetValueOrDefault(ext) + 1;
            }
        }
        catch { return new List<string>(); }

        return counts.OrderByDescending(kv => kv.Value)
                     .Take(8)
                     .Select(kv => kv.Key.ToLowerInvariant())
                     .ToList();
    }

    /// <summary>
    /// Folders never worth descending into. On a network share every directory
    /// listing is a round trip, and a document library can be thousands of them.
    /// Matched on the leaf name, case-insensitively, anywhere in the tree.
    /// </summary>
    private static bool SkipDir(string name)
        => name.StartsWith('.')
           || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
           || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Training Videos", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Inventory Documents", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Vault Access", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why an import produced nothing. "No data found" is the least useful thing
    /// a tool can say when the reason is knowable, and here it usually is.
    /// </summary>
    private static string EmptyResultNote(string path)
    {
        if (ArchivedCard.PrescriptionOnly > 0)
            return $"No logged work here: {ArchivedCard.PrescriptionOnly} of " +
                   $"{ArchivedCard.ArchivesRead} archive(s) hold a prescription " +
                   "(a planned rate map), which is not imported as machine data.";

        if (Isoxml.PlaceholderTaskData(path) is string maker)
            return $"This card's TASKDATA is an empty placeholder written by " +
                   $"{maker}; the logged data is in the manufacturer's own files " +
                   "beside it, which no ADAPT plugin can read. Exporting ISOXML " +
                   "from the display produces a card that does import.";

        return "No spatial operations or boundaries found.";
    }

    /// <summary>
    /// Import a path with whichever reader fits: an ADAPT plugin if one claims
    /// it, otherwise one of ours. Archives call back into this, so a card zipped
    /// inside a folder is imported exactly as it would be unzipped.
    /// </summary>
    /// <param name="pluginName">A reader named by the caller — the QGIS dialog
    /// passes back whatever detect reported, including our own format names.</param>
    /// <param name="depth">Guards against an archive that contains an archive.</param>
    private static (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries)
        ImportPath(AdaptHost host, string path, string? pluginName = null, int depth = 0)
    {
        bool named(string format) =>
            string.Equals(pluginName, format, StringComparison.OrdinalIgnoreCase);

        // Resolve the ADAPT plugin once and carry it into ImportAll. Detect()
        // re-runs IsDataCardSupported across every loaded plugin and is slow on a
        // large card, so calling it here and again inside ImportAll would double
        // that cost on every import.
        string? adapt = pluginName ?? host.Detect(path).FirstOrDefault()?.Name;
        bool unclaimed = adapt is null;

        if (named(RavenReader.FormatName) || (unclaimed && RavenReader.CanRead(path)))
            return (RavenReader.Import(path), new List<BoundaryFeature>());

        // Same order as DetectAll, so the import uses the reader detect promised.
        if (named(LooseGen4.FormatName) || (unclaimed && LooseGen4.CanRead(path)))
            return LooseGen4.Import(path, d => ImportPath(host, d, null, depth + 1));

        if (depth < 2 && (named(ArchivedCard.FormatName)
                          || (unclaimed && ArchivedCard.CanRead(path))))
            return ArchivedCard.Import(path, d => ImportPath(host, d, null, depth + 1));

        return host.ImportAll(path, adapt);
    }

    /// <summary>How many levels <paramref name="dir"/> sits below the root.</summary>
    private static int Depth(string root, string dir)
    {
        string rel = Path.GetRelativePath(root, dir);
        if (rel == ".") return 0;
        return rel.Count(c => c == Path.DirectorySeparatorChar
                              || c == Path.AltDirectorySeparatorChar) + 1;
    }

    private static void Usage()
    {
        Console.Error.WriteLine(@"smsimport — SMS machine-data importer (SMSLIBRE sidecar)

  smsimport plugins            --sms <smsInstallDir>
  smsimport detect <cardPath>  --sms <smsInstallDir>
  smsimport import <cardPath> <out.gpkg> --sms <smsInstallDir> [--plugin <name>]

Emits a JSON object on stdout. --sms defaults to the standard install path.");
    }
}

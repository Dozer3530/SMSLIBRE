// SMSLIBRE — generic ADAPT plugin host.
//
// Loads EVERY vendor import plugin SMS ships (John Deere GS2/GS3/GS4, Climate,
// Precision Planting, Trimble, CNH, ISOXML, ADM) through ADAPT's own
// PluginFactory, auto-detects which one reads a given card, and flattens the
// result into layers ready for GeoPackage output.
//
// The proprietary vendor DLLs are NOT redistributed: we point at the user's own
// SMS installation. Only the open-source AgGateway assemblies could be bundled.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.ApplicationDataModel.Shapes;
using AgGateway.ADAPT.PluginManager;

namespace SmsLibre.Import;

public sealed record PluginInfo(string Name, string Version, string Owner);

/// <summary>One output layer: a logged operation with every sensor channel.</summary>
public sealed class OperationLayer
{
    public string Grower { get; set; } = "";
    public string Farm { get; set; } = "";
    public string Field { get; set; } = "";
    public string OperationType { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Channel names in column order (e.g. "Yield Volume Per Area").</summary>
    public List<string> Channels { get; } = new();
    /// <summary>Unit code per channel, same order.</summary>
    public List<string> Units { get; } = new();
    public List<LayerPoint> Points { get; } = new();

    public string SuggestedName =>
        string.Join("_", new[] { Field, OperationType, Description }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>A field boundary: outer ring plus any interior exclusions.</summary>
public sealed class BoundaryFeature
{
    public string Grower { get; set; } = "";
    public string Farm { get; set; } = "";
    public string Field { get; set; } = "";
    public string Description { get; set; } = "";
    public double AreaHa { get; set; }
    /// <summary>Polygons; each is [exterior, interior…] rings of (lon, lat).</summary>
    public List<List<List<(double Lon, double Lat)>>> Polygons { get; } = new();
}

public sealed class LayerPoint
{
    public double Lon { get; init; }
    public double Lat { get; init; }
    public DateTime Timestamp { get; init; }
    public double?[] Values { get; init; } = Array.Empty<double?>();
}

public sealed class AdaptHost
{
    // SMS splits its plugins across folders: the vendor plugins live in
    // …\SMS\ADAPT while ISOv4Plugin and CNHVoyager2 sit in NetCoreDependencies.
    // A PluginFactory scans a single directory, so we aggregate one per folder.
    private readonly List<PluginFactory> _factories = new();
    private static bool _resolverInstalled;
    private static readonly object _lock = new();

    /// <param name="pluginDir">SMS's ADAPT plugin folder (…\SMS\ADAPT).</param>
    /// <param name="supportDirs">Extra folders that hold both ADAPT core
    /// assemblies and further plugins (…\SMS\NetCoreDependencies).</param>
    public AdaptHost(string pluginDir, params string[] supportDirs)
    {
        var probeDirs = new List<string> { pluginDir };
        probeDirs.AddRange(supportDirs);
        probeDirs.Add(AppContext.BaseDirectory);

        lock (_lock)
        {
            if (!_resolverInstalled)
            {
                AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
                {
                    var wanted = new AssemblyName(e.Name).Name;
                    if (wanted is null) return null;

                    // Critical: hand back an already-loaded assembly when the
                    // simple name matches. Vendor plugins are loaded from the
                    // user's SMS folder while we may also ship copies of the
                    // ADAPT assemblies; letting both load would create two
                    // identities for the same type and every IPlugin cast would
                    // fail (symptom: most plugins silently disappear).
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                        if (string.Equals(a.GetName().Name, wanted, StringComparison.OrdinalIgnoreCase))
                            return a;

                    foreach (var d in probeDirs)
                    {
                        string p = Path.Combine(d, wanted + ".dll");
                        if (File.Exists(p))
                        {
                            try { return Assembly.LoadFrom(p); } catch { }
                        }
                    }
                    return null;
                };
                _resolverInstalled = true;
            }
        }

        // Scan for plugins in SMS's folders only. The app's own directory is a
        // dependency probe path, not a plugin source — treating our bundled
        // ADAPT copies as plugins is what duplicates assembly identities.
        foreach (var dir in probeDirs.Take(1 + supportDirs.Length)
                                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            try { _factories.Add(new PluginFactory(dir)); } catch { }
        }
    }

    /// <summary>
    /// Plugins we reference directly rather than discover. ISOv4Plugin is
    /// open-source and compiled against, so binding it explicitly guarantees it
    /// is present and sidesteps the assembly-identity pitfalls of loading the
    /// same DLL twice through PluginFactory.
    /// </summary>
    private static IEnumerable<AgGateway.ADAPT.ApplicationDataModel.ADM.IPlugin> BuiltIns()
    {
        yield return new AgGateway.ADAPT.ISOv4Plugin.Plugin();
    }

    /// <summary>Every plugin instance available, built-in or discovered.</summary>
    private IEnumerable<(string Name, AgGateway.ADAPT.ApplicationDataModel.ADM.IPlugin Plugin)> AllPlugins()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in BuiltIns())
        {
            string name = p.Name ?? p.GetType().Name;
            if (seen.Add(name) && TryInitialize(p)) yield return (name, p);
        }

        foreach (var f in _factories)
        {
            List<string> names;
            try { names = f.AvailablePlugins; } catch { continue; }
            foreach (var n in names)
            {
                if (!seen.Add(n)) continue;
                AgGateway.ADAPT.ApplicationDataModel.ADM.IPlugin? inst = null;
                try { inst = f.GetPlugin(n); } catch { }
                if (inst != null && TryInitialize(inst)) yield return (n, inst);
            }
        }
    }

    // Plugins must be initialised before IsDataCardSupported/Import; several
    // vendors otherwise throw "Plugin is not initialized". Initialise once per
    // instance, best-effort: a plugin whose Initialize throws is still offered,
    // because the failure may be benign — but the reason is kept for diagnostics.
    private static readonly HashSet<object> _initialized =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Plugin name → why Initialize() failed, when it did.</summary>
    public static IReadOnlyDictionary<string, string> InitErrors => _initErrors;
    private static readonly Dictionary<string, string> _initErrors = new();

    private static bool TryInitialize(AgGateway.ADAPT.ApplicationDataModel.ADM.IPlugin p)
    {
        lock (_initialized)
        {
            if (!_initialized.Add(p)) return true;
            try { p.Initialize(); }
            catch (Exception ex)
            {
                string name = SafeName(p);
                _initErrors[name] = ex.Message;
            }
            return true;
        }
    }

    private static string SafeName(AgGateway.ADAPT.ApplicationDataModel.ADM.IPlugin p)
    {
        try { return p.Name ?? p.GetType().Name; }
        catch { return p.GetType().Name; }
    }

    /// <summary>Every plugin that loads successfully.</summary>
    public IReadOnlyList<PluginInfo> ListPlugins()
    {
        var list = new List<PluginInfo>();
        foreach (var (name, p) in AllPlugins())
        {
            try { list.Add(new PluginInfo(name, p.Version ?? "", p.Owner ?? "")); }
            catch { /* a plugin that will not report itself is unusable */ }
        }
        return list;
    }

    /// <summary>Plugins that claim they can read <paramref name="path"/>.</summary>
    public IReadOnlyList<PluginInfo> Detect(string path)
    {
        var hits = new List<PluginInfo>();
        foreach (var (name, p) in AllPlugins())
        {
            try
            {
                if (p.IsDataCardSupported(path))
                    hits.Add(new PluginInfo(name, p.Version ?? "", p.Owner ?? ""));
            }
            catch { }
        }
        return hits;
    }

    /// <summary>Import a card, returning both logged operations and boundaries.
    /// Setup/prescription cards often carry boundaries and no logs at all.</summary>
    public (List<OperationLayer> Layers, List<BoundaryFeature> Boundaries) ImportAll(
        string path, string? pluginName = null)
    {
        var chosen = pluginName ?? Detect(path).FirstOrDefault()?.Name
            ?? throw new NotSupportedException("No installed ADAPT plugin can read: " + path);

        var plugin = AllPlugins()
            .FirstOrDefault(x => string.Equals(x.Name, chosen, StringComparison.OrdinalIgnoreCase))
            .Plugin ?? throw new NotSupportedException("Plugin not available: " + chosen);

        var models = plugin.Import(path);
        var layers = new List<OperationLayer>();
        var bounds = new List<BoundaryFeature>();
        if (models == null) return (layers, bounds);

        foreach (var adm in models)
        {
            layers.AddRange(Flatten(adm));
            bounds.AddRange(FlattenBoundaries(adm));
        }
        return (layers, bounds);
    }

    /// <summary>Field boundaries from the catalogue, as lon/lat rings.</summary>
    private static IEnumerable<BoundaryFeature> FlattenBoundaries(ApplicationDataModel adm)
    {
        var cat = adm.Catalog;
        if (cat?.FieldBoundaries == null) yield break;

        var growerById = (cat.Growers ?? new()).ToDictionary(g => g.Id.ReferenceId, g => g.Name ?? "");
        var farmById = (cat.Farms ?? new()).ToDictionary(f => f.Id.ReferenceId, f => f.Description ?? "");
        var fields = (cat.Fields ?? new()).ToDictionary(f => f.Id.ReferenceId, f => f);

        foreach (var fb in cat.FieldBoundaries)
        {
            if (fb.SpatialData?.Polygons == null) continue;

            var feature = new BoundaryFeature { Description = fb.Description ?? "" };
            if (fields.TryGetValue(fb.FieldId, out var fld))
            {
                feature.Field = fld.Description ?? "";
                feature.Grower = Lookup(growerById, fld.GrowerId);
                feature.Farm = Lookup(farmById, fld.FarmId);
                try
                {
                    if (fld.Area?.Value?.Value is double a)
                        feature.AreaHa = fld.Area.Value.UnitOfMeasure?.Code == "ha" ? a : a;
                }
                catch { }
            }

            foreach (var poly in fb.SpatialData.Polygons)
            {
                var rings = new List<List<(double, double)>>();
                if (poly.ExteriorRing?.Points is { Count: > 2 } ext)
                    rings.Add(ext.Select(p => (p.X, p.Y)).ToList());
                else
                    continue;   // a polygon without a usable outer ring is unusable
                foreach (var inner in poly.InteriorRings ?? new List<LinearRing>())
                    if (inner?.Points is { Count: > 2 } ip)
                        rings.Add(ip.Select(p => (p.X, p.Y)).ToList());
                feature.Polygons.Add(rings);
            }

            if (feature.Polygons.Count > 0) yield return feature;
        }
    }

    /// <summary>Import a card with a named plugin (or the first that supports it).</summary>
    public List<OperationLayer> Import(string path, string? pluginName = null)
    {
        var chosen = pluginName ?? Detect(path).FirstOrDefault()?.Name
            ?? throw new NotSupportedException("No installed ADAPT plugin can read: " + path);

        var plugin = AllPlugins()
            .FirstOrDefault(x => string.Equals(x.Name, chosen, StringComparison.OrdinalIgnoreCase))
            .Plugin ?? throw new NotSupportedException("Plugin not available: " + chosen);

        var models = plugin.Import(path);
        var layers = new List<OperationLayer>();
        if (models == null) return layers;

        foreach (var adm in models)
            layers.AddRange(Flatten(adm));
        return layers;
    }

    /// <summary>Turn an ADAPT model into one layer per logged operation, keeping
    /// every numeric channel the machine recorded.</summary>
    private static IEnumerable<OperationLayer> Flatten(ApplicationDataModel adm)
    {
        var cat = adm.Catalog;
        var growerById = (cat?.Growers ?? new()).ToDictionary(g => g.Id.ReferenceId, g => g.Name ?? "");
        var farmById = (cat?.Farms ?? new()).ToDictionary(f => f.Id.ReferenceId, f => f.Description ?? "");
        var fieldById = (cat?.Fields ?? new()).ToDictionary(f => f.Id.ReferenceId, f => f.Description ?? "");

        var logged = adm.Documents?.LoggedData;
        if (logged == null) yield break;

        foreach (var ld in logged)
        {
            foreach (var op in ld.OperationData ?? new List<OperationData>())
            {
                var layer = new OperationLayer
                {
                    Grower = Lookup(growerById, ld.GrowerId),
                    Farm = Lookup(farmById, ld.FarmId),
                    Field = Lookup(fieldById, ld.FieldId),
                    OperationType = op.OperationType.ToString(),
                    Description = ld.Description ?? "",
                };

                // Collect the numeric channels for this operation, in a stable order.
                var meters = new List<WorkingData>();
                for (int depth = 0; depth <= op.MaxDepth; depth++)
                {
                    var uses = op.GetDeviceElementUses?.Invoke(depth);
                    if (uses == null) continue;
                    foreach (var use in uses)
                        meters.AddRange(use.GetWorkingDatas?.Invoke() ?? Enumerable.Empty<WorkingData>());
                }
                var numeric = meters
                    .OfType<NumericWorkingData>()
                    .GroupBy(m => m.Id.ReferenceId).Select(g => g.First())
                    .ToList();
                if (numeric.Count == 0) continue;

                foreach (var m in numeric)
                {
                    layer.Channels.Add(ChannelName(m));
                    layer.Units.Add(m.UnitOfMeasure?.Code ?? "");
                }

                var records = op.GetSpatialRecords?.Invoke();
                if (records == null) continue;

                foreach (var rec in records)
                {
                    if (rec.Geometry is not Point pt) continue;
                    // Raw logs contain (0,0) fixes before GPS lock; they would
                    // otherwise stretch every layer's extent to null island.
                    if (Math.Abs(pt.X) < 1e-9 && Math.Abs(pt.Y) < 1e-9) continue;
                    var vals = new double?[numeric.Count];
                    for (int i = 0; i < numeric.Count; i++)
                        vals[i] = rec.GetMeterValue(numeric[i]) is NumericRepresentationValue v
                            ? v.Value.Value : null;
                    layer.Points.Add(new LayerPoint
                    {
                        Lon = pt.X, Lat = pt.Y, Timestamp = rec.Timestamp, Values = vals,
                    });
                }

                if (layer.Points.Count > 0) yield return layer;
            }
        }
    }

    private static string Lookup(Dictionary<int, string> map, int? id)
        => id is int i && map.TryGetValue(i, out var v) ? v : "";

    /// <summary>Prefer the human description, fall back to the DDI/code.</summary>
    private static string ChannelName(WorkingData m)
    {
        var r = m.Representation;
        string name = !string.IsNullOrWhiteSpace(r?.Description) ? r!.Description
                    : !string.IsNullOrWhiteSpace(r?.Code) ? r!.Code
                    : "channel";
        return name.Trim();
    }
}

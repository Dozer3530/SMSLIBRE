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
                    string file = new AssemblyName(e.Name).Name + ".dll";
                    foreach (var d in probeDirs)
                    {
                        string p = Path.Combine(d, file);
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

        foreach (var dir in probeDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            try { _factories.Add(new PluginFactory(dir)); } catch { }
        }
    }

    /// <summary>Every (factory, plugin-name) pair that loads successfully.</summary>
    private IEnumerable<(PluginFactory Factory, string Name)> AllPlugins()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in _factories)
        {
            List<string> names;
            try { names = f.AvailablePlugins; } catch { continue; }
            foreach (var n in names)
                if (seen.Add(n)) yield return (f, n);
        }
    }

    /// <summary>Every plugin that loads successfully.</summary>
    public IReadOnlyList<PluginInfo> ListPlugins()
    {
        var list = new List<PluginInfo>();
        foreach (var (f, name) in AllPlugins())
        {
            try
            {
                var p = f.GetPlugin(name);
                list.Add(new PluginInfo(name, p.Version ?? "", p.Owner ?? ""));
            }
            catch { /* a plugin that will not instantiate is simply unavailable */ }
        }
        return list;
    }

    /// <summary>Plugins that claim they can read <paramref name="path"/>.</summary>
    public IReadOnlyList<PluginInfo> Detect(string path)
    {
        var hits = new List<PluginInfo>();
        foreach (var (f, name) in AllPlugins())
        {
            try
            {
                var p = f.GetPlugin(name);
                if (p.IsDataCardSupported(path))
                    hits.Add(new PluginInfo(name, p.Version ?? "", p.Owner ?? ""));
            }
            catch { }
        }
        return hits;
    }

    /// <summary>Import a card with a named plugin (or the first that supports it).</summary>
    public List<OperationLayer> Import(string path, string? pluginName = null)
    {
        var chosen = pluginName ?? Detect(path).FirstOrDefault()?.Name
            ?? throw new NotSupportedException("No installed ADAPT plugin can read: " + path);

        var entry = AllPlugins().FirstOrDefault(x =>
            string.Equals(x.Name, chosen, StringComparison.OrdinalIgnoreCase));
        if (entry.Factory is null)
            throw new NotSupportedException("Plugin not available: " + chosen);

        var plugin = entry.Factory.GetPlugin(entry.Name);
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

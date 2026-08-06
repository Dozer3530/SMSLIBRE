// SMSLIBRE — import via SMS's own ADAPT engine.
//
// Wraps AgGateway.ADAPT.ISOv4Plugin (shipped with SMS) and maps its
// ApplicationDataModel onto SMSLIBRE's small domain model. This is the reuse
// win: SMS's actual, tested multi-vendor importer, running on native .NET.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.ApplicationDataModel.Shapes;
using AgGateway.ADAPT.ISOv4Plugin;
using SmsLibre.Core;
using AdaptField = AgGateway.ADAPT.ApplicationDataModel.Logistics.Field;

namespace SmsLibre.Import;

/// <summary>
/// Imports ISO 11783 (ISOXML) task datasets by reusing SMS's own
/// AgGateway.ADAPT.ISOv4Plugin on native .NET. This is the first concrete
/// <see cref="IFieldImporter"/>; JD / CNH / Precision Planting / Shapefile
/// importers plug in the same way.
/// </summary>
public sealed class IsoXmlImporter : IFieldImporter
{
    private static bool _resolverInstalled;
    private static readonly object _lock = new();

    public string FormatName => "ISOXML (ISO 11783)";

    /// <param name="pluginDir">Folder holding the ADAPT DLLs + Resources
    /// (copied next to the app at build time). Defaults to the app base dir.</param>
    public IsoXmlImporter(string? pluginDir = null)
    {
        string dir = pluginDir ?? AppContext.BaseDirectory;
        lock (_lock)
        {
            if (_resolverInstalled) return;
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                string path = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
            };
            _resolverInstalled = true;
        }
    }

    public bool CanImport(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, "TASKDATA.XML"));

    public IReadOnlyList<Grower> Import(string taskDataDir)
    {
        var plugin = new Plugin();
        var models = plugin.Import(taskDataDir);
        var result = new List<Grower>();
        if (models == null) return result;
        foreach (var adm in models)
            result.AddRange(MapModel(adm));
        return result;
    }

    private static IEnumerable<Grower> MapModel(ApplicationDataModel adm)
    {
        var cat = adm.Catalog;
        if (cat == null) return Enumerable.Empty<Grower>();

        // ISOXML often leaves the grower/farm/field cross-references sparse, so
        // the tree is built defensively: every field is surfaced, orphans land
        // under a synthesized "(imported)" grower/farm rather than disappearing.
        var growers = (cat.Growers ?? new()).ToDictionary(
            g => g.Id.ReferenceId, g => new Grower { Name = g.Name ?? "(grower)" });
        var defaultGrower = new Grower { Name = "(imported)" };

        var farms = new Dictionary<int, Farm>();
        foreach (var af in cat.Farms ?? new())
        {
            var farm = new Farm { Name = af.Description ?? "(farm)" };
            farms[af.Id.ReferenceId] = farm;
            OwnerGrower(af.GrowerId, growers, defaultGrower).Farms.Add(farm);
        }

        var yieldByField = ExtractYieldByField(adm);
        var fields = new Dictionary<int, Field>();

        foreach (AdaptField afld in cat.Fields ?? new())
        {
            var field = new Field { Name = afld.Description ?? "(field)" };
            fields[afld.Id.ReferenceId] = field;
            if (yieldByField.TryGetValue(afld.Id.ReferenceId, out var pts))
                AttachYield(field, pts);
            HostFarm(afld.FarmId, afld.GrowerId, farms, growers, defaultGrower).Fields.Add(field);
        }

        // Yield whose field id didn't resolve to a catalog field: if there is
        // exactly one field, it belongs to it; otherwise show it as unfiled.
        foreach (var kv in yieldByField)
        {
            if (fields.ContainsKey(kv.Key)) continue;
            if (fields.Count == 1) { AttachYield(fields.Values.First(), kv.Value); continue; }
            var f = new Field { Name = "(unfiled yield)" };
            AttachYield(f, kv.Value);
            DefaultFarm(defaultGrower).Fields.Add(f);
        }

        var all = growers.Values.Append(defaultGrower);
        return all.Where(g => g.Farms.Any(f => f.Fields.Count > 0)).ToList();
    }

    private static Grower OwnerGrower(int? growerId, Dictionary<int, Grower> growers, Grower fallback)
        => growerId is int gid && growers.TryGetValue(gid, out var g) ? g : fallback;

    private static Farm HostFarm(int? farmId, int? growerId,
        Dictionary<int, Farm> farms, Dictionary<int, Grower> growers, Grower fallback)
    {
        if (farmId is int fid && farms.TryGetValue(fid, out var farm)) return farm;
        return DefaultFarm(OwnerGrower(growerId, growers, fallback));
    }

    private static Farm DefaultFarm(Grower g)
        => g.Farms.FirstOrDefault(f => f.Name == "(unfiled)")
           ?? AddFarm(g, new Farm { Name = "(unfiled)" });

    private static Farm AddFarm(Grower g, Farm f) { g.Farms.Add(f); return f; }

    private static void AttachYield(Field field, List<YieldPoint> pts)
    {
        if (pts.Count == 0) return;
        var cleaned = Cleaning.Clean(pts);
        if (cleaned.Count == 0) return;
        field.AreaHa ??= null;
        field.Datasets.Add(new Dataset
        {
            Name = "Yield", Kind = DatasetKind.Yield, ValueLabel = "Yield", Points = cleaned,
        });
    }

    /// <summary>Walk LoggedData→OperationData→SpatialRecords, pull the yield
    /// meter, and group points by the logged data's field id.</summary>
    private static Dictionary<int, List<YieldPoint>> ExtractYieldByField(ApplicationDataModel adm)
    {
        var byField = new Dictionary<int, List<YieldPoint>>();
        var logged = adm.Documents?.LoggedData;
        if (logged == null) return byField;

        foreach (var ld in logged)
        {
            int fieldId = ld.FieldId ?? -1;
            foreach (var op in ld.OperationData ?? new List<OperationData>())
            {
                var meters = CollectMeters(op);
                var yieldMeter = PickYieldMeter(meters);
                if (yieldMeter == null) continue;
                var records = op.GetSpatialRecords?.Invoke();
                if (records == null) continue;

                if (!byField.TryGetValue(fieldId, out var list))
                    byField[fieldId] = list = new List<YieldPoint>();

                foreach (var rec in records)
                {
                    if (rec.Geometry is not Point pt) continue;
                    if (rec.GetMeterValue(yieldMeter) is not NumericRepresentationValue v) continue;
                    list.Add(new YieldPoint(pt.X, pt.Y, v.Value.Value));
                }
            }
        }
        return byField;
    }

    private static List<WorkingData> CollectMeters(OperationData op)
    {
        var meters = new List<WorkingData>();
        for (int depth = 0; depth <= op.MaxDepth; depth++)
        {
            var uses = op.GetDeviceElementUses?.Invoke(depth);
            if (uses == null) continue;
            foreach (var use in uses)
                meters.AddRange(use.GetWorkingDatas?.Invoke() ?? Enumerable.Empty<WorkingData>());
        }
        return meters.GroupBy(m => m.Id.ReferenceId).Select(g => g.First()).ToList();
    }

    private static WorkingData? PickYieldMeter(IEnumerable<WorkingData> meters)
    {
        bool Has(WorkingData m, string s) =>
            (m.Representation?.Code?.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0) ||
            (m.Representation?.Description?.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);
        var ys = meters.Where(m => m is NumericWorkingData && Has(m, "yield")).ToList();
        return ys.FirstOrDefault(m => Has(m, "dry"))
            ?? ys.FirstOrDefault(m => Has(m, "vol"))
            ?? ys.FirstOrDefault();
    }
}

// SMSLIBRE — core domain model.
//
// A deliberately small, UI- and format-agnostic model that the import layer
// fills and the UI renders. It mirrors the grower→farm→field→dataset hierarchy
// SMS uses (and that ADAPT and Main.mdb both express), so it can grow toward
// full parity without leaking any one source format into the UI.

using System.Collections.Generic;

namespace SmsLibre.Core;

/// <summary>Top of the tree: an operation/business the data belongs to.</summary>
public sealed class Grower
{
    public string Name { get; set; } = "";
    public List<Farm> Farms { get; } = new();
}

public sealed class Farm
{
    public string Name { get; set; } = "";
    public List<Field> Fields { get; } = new();
}

public sealed class Field
{
    public string Name { get; set; } = "";
    public double? AreaHa { get; set; }
    public List<Dataset> Datasets { get; } = new();
}

/// <summary>What SMS calls a "dataset": one spatial layer for a field/season —
/// here, a harvested yield layer. Other operation types (planting, application,
/// as-applied, soil samples, imagery) will become additional dataset kinds.</summary>
public sealed class Dataset
{
    public string Name { get; set; } = "";
    public DatasetKind Kind { get; set; } = DatasetKind.Yield;
    public string ValueLabel { get; set; } = "";   // e.g. "Yield (t/ha)"
    public IReadOnlyList<YieldPoint> Points { get; set; } = new List<YieldPoint>();
}

public enum DatasetKind { Yield, Planting, Application, SoilSample, Boundary, Imagery, Other }

/// <summary>A single logged reading: position in lon/lat (WGS84) + a value.</summary>
public readonly record struct YieldPoint(double Lon, double Lat, double Value);

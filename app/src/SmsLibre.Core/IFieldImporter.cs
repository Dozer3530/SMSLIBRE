// SMSLIBRE — importer abstraction.
//
// Every data format SMS reads becomes an IFieldImporter. A registry picks the
// right one for a given path, so adding John Deere / CNH / Precision Planting /
// Shapefile importers later is a matter of implementing this interface — the UI
// and the rest of the app never change.

using System.Collections.Generic;

namespace SmsLibre.Core;

public interface IFieldImporter
{
    /// <summary>Human-readable format name (e.g. "ISOXML (ISO 11783)").</summary>
    string FormatName { get; }

    /// <summary>Cheap check: could this importer read the given path?</summary>
    bool CanImport(string path);

    /// <summary>Import into the grower/farm/field tree. Throws on hard failure.</summary>
    IReadOnlyList<Grower> Import(string path);
}

/// <summary>Chooses an importer for a path and imports with it.</summary>
public sealed class ImporterRegistry
{
    private readonly List<IFieldImporter> _importers = new();

    public ImporterRegistry Register(IFieldImporter importer)
    {
        _importers.Add(importer);
        return this;
    }

    public IReadOnlyList<IFieldImporter> Importers => _importers;

    public IFieldImporter? FindFor(string path)
    {
        foreach (var imp in _importers)
            if (imp.CanImport(path)) return imp;
        return null;
    }

    public (IFieldImporter importer, IReadOnlyList<Grower> growers)? Import(string path)
    {
        var imp = FindFor(path);
        return imp is null ? null : (imp, imp.Import(path));
    }
}

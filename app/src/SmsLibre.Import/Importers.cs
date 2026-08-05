// SMSLIBRE — importer registry factory.
//
// One place that knows every format SMSLIBRE can read. As new IFieldImporter
// implementations are added (John Deere, CNH, Precision Planting, Shapefile),
// register them here and the whole app gains the format.

using SmsLibre.Core;

namespace SmsLibre.Import;

public static class Importers
{
    /// <summary>Build the registry of all available importers.</summary>
    /// <param name="pluginDir">ADAPT plugin/resources dir (defaults to app base).</param>
    public static ImporterRegistry CreateDefault(string? pluginDir = null)
    {
        return new ImporterRegistry()
            .Register(new IsoXmlImporter(pluginDir));
        // Future:
        //   .Register(new JohnDeereImporter(pluginDir))
        //   .Register(new CnhVoyagerImporter(pluginDir))
        //   .Register(new ShapefileImporter());
    }
}

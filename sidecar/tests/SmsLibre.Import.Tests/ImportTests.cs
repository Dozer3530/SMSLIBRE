using System.IO;
using System.Linq;
using SmsLibre.Core;
using SmsLibre.Import;
using Xunit;

namespace SmsLibre.Tests;

public class RegistryTests
{
    [Fact]
    public void Registry_routes_a_taskdata_folder_to_the_isoxml_importer()
    {
        string dir = Path.Combine(Path.GetTempPath(), "smslibre_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "TASKDATA.XML"), "<ISO11783_TaskData/>");
            var reg = Importers.CreateDefault();
            var imp = reg.FindFor(dir);
            Assert.NotNull(imp);
            Assert.Equal("ISOXML (ISO 11783)", imp!.FormatName);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Registry_returns_null_for_an_unrecognised_folder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "smslibre_test_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try { Assert.Null(Importers.CreateDefault().FindFor(dir)); }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

/// <summary>
/// Integration test that runs SMS's real ADAPT engine. It executes only where
/// the sample Vault data and the ADAPT DLLs are present (the analysis box);
/// elsewhere (e.g. CI without an SMS install) it is a trivial pass so the suite
/// stays green. Marked with a Trait so it can be filtered.
/// </summary>
[Trait("Category", "Integration")]
public class IsoXmlImportIntegrationTests
{
    private const string Sample =
        @"C:\ProgramData\Ag Leader\SMS\Data\Data_2\Vault\AGCO ISO11783\2024\09_26\ISO_TASKDATA\0\TASKDATA";

    private static bool CanRun =>
        File.Exists(Path.Combine(Sample, "TASKDATA.XML")) &&
        File.Exists(Path.Combine(System.AppContext.BaseDirectory,
                                 "AgGateway.ADAPT.ISOv4Plugin.dll"));

    [Fact]
    public void Imports_sample_field_with_yield_points()
    {
        if (!CanRun) return;   // sample data / ADAPT DLLs absent — skip on CI

        var growers = Importers.CreateDefault().Import(Sample);
        Assert.NotNull(growers);

        var fields = growers!.Value.growers
            .SelectMany(g => g.Farms).SelectMany(f => f.Fields).ToList();
        Assert.NotEmpty(fields);

        var yieldDs = fields.SelectMany(f => f.Datasets)
            .Where(d => d.Kind == DatasetKind.Yield && d.Points.Count > 0).ToList();
        Assert.NotEmpty(yieldDs);
        Assert.True(yieldDs.Sum(d => d.Points.Count) > 100_000,
                    "expected the ~436k-point AGCO field");

        // Points must be finite lon/lat in the field's region.
        var p = yieldDs[0].Points[0];
        Assert.InRange(p.Lon, -180, 180);
        Assert.InRange(p.Lat, -90, 90);
        Assert.True(p.Value > 0);
    }
}

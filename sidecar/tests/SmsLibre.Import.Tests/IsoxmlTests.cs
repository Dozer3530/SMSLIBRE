using System;
using System.IO;
using SmsLibre.Import;
using Xunit;

namespace SmsLibre.Tests;

/// <summary>
/// A placeholder TASKDATA is why 18 directories in the vault detected as ISOXML
/// and imported nothing. The stub below is the real one from a New Holland
/// Voyager2 card, byte for byte.
/// </summary>
public class IsoxmlTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "smslibre-isoxml-" + Guid.NewGuid().ToString("N"));

    public IsoxmlTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void WriteTaskData(string xml, params string[] subdirs)
    {
        string dir = subdirs.Length == 0 ? _dir : Path.Combine(_dir, Path.Combine(subdirs));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "TASKDATA.XML"), xml);
    }

    [Fact]
    public void Names_the_manufacturer_of_an_empty_taskdata()
    {
        WriteTaskData(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<ISO11783_TaskData VersionMajor=\"2\" VersionMinor=\"0\" " +
            "TaskControllerManufacturer=\"CNH\" TaskControllerVersion=\"30.27.0.0\" " +
            "DataTransferOrigin=\"2\" >\n</ISO11783_TaskData>");

        Assert.Equal("CNH", Isoxml.PlaceholderTaskData(_dir));
    }

    [Fact]
    public void Finds_a_taskdata_nested_the_way_a_cn1_card_nests_it()
    {
        WriteTaskData(
            "<ISO11783_TaskData TaskControllerManufacturer=\"CNH\"></ISO11783_TaskData>",
            "230516k6.cn1", "xml");

        Assert.Equal("CNH", Isoxml.PlaceholderTaskData(_dir));
    }

    [Fact]
    public void A_taskdata_with_content_is_not_a_placeholder()
    {
        WriteTaskData(
            "<ISO11783_TaskData TaskControllerManufacturer=\"Raven Industries\">" +
            "<CTR A=\"CTR-1\" B=\"OLDS COLLEGE\"/></ISO11783_TaskData>");

        Assert.Null(Isoxml.PlaceholderTaskData(_dir));
    }

    [Fact]
    public void No_taskdata_and_malformed_taskdata_both_yield_nothing()
    {
        Assert.Null(Isoxml.PlaceholderTaskData(_dir));

        WriteTaskData("<ISO11783_TaskData not xml at all");
        Assert.Null(Isoxml.PlaceholderTaskData(_dir));
    }
}

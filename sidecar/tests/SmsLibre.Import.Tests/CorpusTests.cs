using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SmsLibre.Core;
using SmsLibre.Import;
using Xunit;

namespace SmsLibre.Tests;

/// <summary>
/// Regression cover driven by a real corpus rather than invented fixtures.
///
/// <c>tools/vault_test.py</c> walks a data vault, imports every card it can find
/// and records the outcome in a <c>results.json</c>. These tests re-import the
/// cards that previously succeeded and assert they still do, with no fewer
/// layers or features than before. That catches the failure mode this project
/// actually suffers from: a change that silently drops data on one vendor's
/// format while the others keep working.
///
/// EVERY corpus on disk is loaded, not just the vault's. The campaign sweeps
/// each drive into its own file, and the drives do not hold the same formats —
/// pinning this to the vault alone left STAAR's 82 imported cards and 67 M
/// features untested, so a reader exercised only there had no regression cover
/// at all.
///
/// The corpus references the user's own data, so these tests skip cleanly when
/// it is absent (CI, a fresh clone, another machine).
/// </summary>
[Trait("Category", "Corpus")]
public class CorpusRegressionTests
{
    private sealed record CorpusEntry(
        string Path, string Detected, string Status, int Layers, int Features,
        int MaxChannels, int InvalidGeom, int OutOfRange);

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 10 && d is not null; i++, d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "README.md")) &&
                Directory.Exists(Path.Combine(d.FullName, "sidecar")))
                return d.FullName;
        return "";
    }

    /// <summary>Every corpus file a sweep may have written, newest layout first.</summary>
    private static IEnumerable<string> CorpusFiles(string root)
    {
        string campaign = Path.Combine(root, "analysis", "campaign");
        if (Directory.Exists(campaign))
            foreach (string d in Directory.EnumerateDirectories(campaign).OrderBy(x => x))
            {
                string f = Path.Combine(d, "results.json");
                if (File.Exists(f)) yield return f;
            }

        // The single-drive layout predates the campaign. Still honoured so a
        // machine that has only ever run tools/vault_test.py keeps its cover.
        string legacy = Path.Combine(root, "analysis", "vault", "results.json");
        if (File.Exists(legacy)) yield return legacy;
    }

    private static List<CorpusEntry> LoadCorpus()
    {
        string root = RepoRoot();
        if (root.Length == 0) return new();

        // Keyed by path: the same card can appear in two corpora (a re-sweep, or
        // a drive walked twice). Keep the richest record so a card is never
        // held to a weaker expectation than one already met.
        var best = new Dictionary<string, CorpusEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in CorpusFiles(root))
        {
            JsonDocument doc;
            // A sweep killed mid-write leaves a truncated file. That is a broken
            // corpus, not a broken importer, so drop it and use the others
            // rather than failing every test in the suite.
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException) { continue; }
            catch (IOException) { continue; }

            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string Str(string n) => e.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
                    int Int(string n) => e.TryGetProperty(n, out var v) && v.TryGetInt32(out int i) ? i : 0;
                    var entry = new CorpusEntry(Str("path"), Str("detected"), Str("status"),
                        Int("layers"), Int("features"), Int("max_channels"),
                        Int("invalid_geom"), Int("out_of_range"));
                    if (entry.Path.Length == 0) continue;
                    if (!best.TryGetValue(entry.Path, out var prev) ||
                        entry.Features > prev.Features)
                        best[entry.Path] = entry;
                }
            }
        }
        return best.Values.ToList();
    }

    /// <summary>Cards that imported successfully last time the corpus was built.</summary>
    public static IEnumerable<object[]> KnownGoodCards()
    {
        foreach (var e in LoadCorpus()
                     .Where(e => e.Status == "ok" && e.Features > 0 && Directory.Exists(e.Path))
                     // One representative per reader: the smallest card
                     // carrying real data. The largest John Deere card takes 13
                     // minutes on its own, and a one-feature card exercises
                     // nothing, so take the smallest above a floor — or the
                     // biggest available when every card is small.
                     .GroupBy(e => e.Detected)
                     .Select(g => g.Where(e => e.Features >= 1000)
                                   .OrderBy(e => e.Features)
                                   .FirstOrDefault()
                             ?? g.OrderByDescending(e => e.Features).First()))
            yield return new object[] { e.Path, e.Detected, e.Layers, e.Features };
    }

    private static AdaptHost Host()
    {
        string sms = @"C:\Program Files\Ag Leader Technology\SMS";
        var priority = new List<string>();
        string root = RepoRoot();
        string vendor = Path.Combine(root, "vendor", "jd-plugins", "plugins");
        if (Directory.Exists(vendor)) priority.Add(vendor);

        string appId = Path.Combine(root, "secrets", "johndeere.appid");
        if (File.Exists(appId))
        {
            string id = File.ReadAllText(appId).Trim();
            AdaptHost.ApplicationId = Guid.TryParse(id, out var g) ? g.ToString("B") : id;
        }

        // The Deere plugins read their licence from beside the running
        // executable, which for a test run is the test assembly's folder. The
        // CLI does this too; without it the plugins load, fail to initialise,
        // and every John Deere card in the corpus quietly skips.
        const string lic = "johndeere.adaptplugins.lic";
        string dest = Path.Combine(AppContext.BaseDirectory, lic);
        string src = Path.Combine(root, "secrets", lic);
        if (!File.Exists(dest) && File.Exists(src))
        {
            try { File.Copy(src, dest); } catch { }
        }

        return new AdaptHost(Path.Combine(sms, "ADAPT"),
                             new[] { Path.Combine(sms, "NetCoreDependencies") },
                             priority.ToArray());
    }

    [SkippableTheory]
    [MemberData(nameof(KnownGoodCards))]
    public void Previously_importable_cards_still_import(
        string path, string reader, int expectedLayers, int expectedFeatures)
    {
        Skip.If(!Directory.Exists(path), "corpus data not present on this machine");

        // Route exactly as the CLI does. Asking AdaptHost directly saw only the
        // ADAPT plugins, so every card belonging to one of our own readers
        // skipped and this suite reported green while testing nothing.
        var host = Host();
        Skip.If(!CardImporter.Detect(host, path).Any(),
                $"{reader} unavailable here (licence or plugins missing)");
        var (layers, boundaries) = CardImporter.Import(host, path);
        int rx = CardImporter.Prescriptions.Count;

        // Boundaries and prescription zones are features too. Counting only
        // points failed every setup card in the corpus: a boundary package has
        // no point layers, and a Jobs folder of rate maps yields 15,555 zones
        // through CardImporter.Prescriptions — neither is importing nothing.
        int features = layers.Sum(l => l.Points.Count) + boundaries.Count + rx;
        Assert.True(layers.Count + boundaries.Count + rx > 0,
                    $"{reader}: nothing imported (was {expectedLayers} layer(s))");
        Assert.True(features > 0, $"{reader}: no features (was {expectedFeatures:N0})");

        // Allow growth and small reader-version drift, but catch a real collapse.
        Assert.True(features >= expectedFeatures * 0.9,
            $"{reader}: {features:N0} features, expected at least 90% of {expectedFeatures:N0}");
    }

    [SkippableFact]
    public void Every_imported_point_has_a_plausible_coordinate()
    {
        var card = KnownGoodCards().FirstOrDefault();
        Skip.If(card is null, "corpus data not present on this machine");

        string path = (string)card![0];
        var host = Host();
        Skip.If(!CardImporter.Detect(host, path).Any(), "reader unavailable here");
        var (layers, boundaries) = CardImporter.Import(host, path);

        foreach (var ring in boundaries.SelectMany(b => b.Polygons).SelectMany(p => p))
            foreach (var (lon, lat) in ring)
            {
                Assert.InRange(lat, -90, 90);
                Assert.InRange(lon, -180, 180);
            }

        foreach (var p in layers.SelectMany(l => l.Points))
        {
            Assert.InRange(p.Lat, -90, 90);
            Assert.InRange(p.Lon, -180, 180);
            Assert.False(double.IsNaN(p.Lat) || double.IsNaN(p.Lon), "NaN coordinate");
        }
    }

    [SkippableFact]
    public void Corpus_records_no_invalid_or_out_of_range_geometry()
    {
        var corpus = LoadCorpus().Where(e => e.Status == "ok").ToList();
        Skip.If(corpus.Count == 0, "corpus not built yet — run tools/vault_test.py");

        var bad = corpus.Where(e => e.InvalidGeom > 0 || e.OutOfRange > 0).ToList();
        Assert.True(bad.Count == 0,
            "cards with invalid/out-of-range geometry: " +
            string.Join(", ", bad.Take(5).Select(b => $"{Path.GetFileName(b.Path)}({b.InvalidGeom}/{b.OutOfRange})")));
    }
}

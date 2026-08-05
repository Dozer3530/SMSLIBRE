// SMSLIBRE — main window.
//
// Wires SMS's ADAPT importer (native .NET) to a native tree + map UI. This is
// the seed of the full-parity app: a management tree on the left, the map in the
// centre, a legend on the right — the SMS layout, rebuilt in Avalonia.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SmsLibre.Core;
using SmsLibre.Import;

namespace SmsLibre.App;

public partial class MainWindow : Window
{
    // Bundled sample dataset (present on the analysis box). On Linux, File > Open.
    private const string SampleData =
        @"C:\ProgramData\Ag Leader\SMS\Data\Data_2\Vault\AGCO ISO11783\2024\09_26\ISO_TASKDATA\0\TASKDATA";

    private Dataset? _current;
    private int _nClasses = 7;

    public MainWindow()
    {
        InitializeComponent();
        if (Directory.Exists(SampleData))
            LoadDataset(SampleData);
        else
            StatusText.Text = "No sample data on this machine — use File ▸ Open ISOXML dataset…";
    }

    private void LoadDataset(string taskDataDir)
    {
        try
        {
            StatusText.Text = $"Importing via SMS ADAPT engine: {taskDataDir} …";
            var importer = new AdaptImporter();
            if (!importer.CanImport(taskDataDir))
            {
                StatusText.Text = "No TASKDATA.XML in that folder.";
                return;
            }
            var growers = importer.ImportIsoXml(taskDataDir);
            var roots = BuildTree(growers);
            FieldTree.ItemsSource = roots;
            int fields = growers.SelectMany(g => g.Farms).SelectMany(f => f.Fields).Count();
            StatusText.Text = $"Imported {growers.Count} grower(s), {fields} field(s) " +
                              "via SMS's ADAPT engine (native .NET).";

            // Auto-open the first yield dataset so the map shows on load.
            var first = FirstYieldNode(roots);
            if (first is not null)
            {
                _current = first.Dataset;
                RenderCurrent();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Import failed: " + ex.Message;
        }
    }

    private static ObservableCollection<TreeNode> BuildTree(IReadOnlyList<Grower> growers)
    {
        var roots = new ObservableCollection<TreeNode>();
        foreach (var g in growers)
        {
            var gn = new TreeNode { Header = g.Name, Glyph = "👤" };
            foreach (var farm in g.Farms)
            {
                var fn = new TreeNode { Header = farm.Name, Glyph = "🏠" };
                foreach (var field in farm.Fields)
                {
                    var fieldNode = new TreeNode { Header = field.Name, Glyph = "▦" };
                    foreach (var ds in field.Datasets)
                        fieldNode.Children.Add(new TreeNode
                        {
                            Header = $"{ds.Name} ({ds.Points.Count:N0} pts)",
                            Glyph = "▤", Dataset = ds,
                        });
                    fn.Children.Add(fieldNode);
                }
                gn.Children.Add(fn);
            }
            roots.Add(gn);
        }
        return roots;
    }

    private static TreeNode? FirstYieldNode(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.Dataset is { Kind: DatasetKind.Yield } && n.Dataset.Points.Count > 0)
                return n;
            var found = FirstYieldNode(n.Children);
            if (found is not null) return found;
        }
        return null;
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FieldTree.SelectedItem is TreeNode { Dataset: { Kind: DatasetKind.Yield } ds }
            && ds.Points.Count > 0)
        {
            _current = ds;
            RenderCurrent();
        }
    }

    private void RenderCurrent()
    {
        if (_current is null) return;
        var res = YieldRaster.Render(_current.Points, width: 1400, height: 1000,
                                     nClasses: _nClasses, dotRadius: 1);

        using var ms = new MemoryStream(PngWriter.Encode(res.Image));
        MapImage.Source = new Bitmap(ms);
        MapPlaceholder.IsVisible = false;

        // Legend (high value at top).
        var rows = new List<LegendRow>();
        foreach (var c in res.Legend.AsEnumerable().Reverse())
        {
            var col = Color.FromRgb(c.Color.R, c.Color.G, c.Color.B);
            rows.Add(new LegendRow
            {
                Swatch = new SolidColorBrush(col),
                Text = $"{c.Low:0.##} – {c.High:0.##}  ({c.Count:N0})",
            });
        }
        LegendItems.ItemsSource = rows;
        LegendTitle.Text = $"{_current.ValueLabel}  ·  {res.PointCount:N0} pts";

        StatsBox.IsVisible = true;
        StatsText.Text =
            $"Points : {res.PointCount:N0}\n" +
            $"Min–Max: {res.Min:0.##} – {res.Max:0.##}\n" +
            $"Mean   : {res.Mean:0.##}\n" +
            $"Median : {res.Median:0.##}\n" +
            $"Classes: {_nClasses}";
        StatusText.Text = $"Rendered {res.PointCount:N0} points (native SMSLIBRE renderer).";
    }

    // ---- menu handlers ------------------------------------------------------

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select an ISOXML TASKDATA folder",
            AllowMultiple = false,
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is string path)
            LoadDataset(path);
    }

    private async void OnExportImage(object? sender, RoutedEventArgs e)
    {
        if (_current is null) { StatusText.Text = "Select a dataset first."; return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export map image",
            SuggestedFileName = "yieldmap.png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG image") { Patterns = new[] { "*.png" } } },
        });
        if (file?.TryGetLocalPath() is string path)
        {
            var res = YieldRaster.Render(_current.Points, 1600, 1200, _nClasses);
            PngWriter.Save(res.Image, path);
            StatusText.Text = "Exported " + path;
        }
    }

    private void OnClasses(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string t } && int.TryParse(t, out int n))
        {
            _nClasses = n;
            RenderCurrent();
        }
    }

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnAbout(object? sender, RoutedEventArgs e) =>
        StatusText.Text = "SMSLIBRE — native Linux SMS, reusing SMS's ADAPT engine.";
}

// SMSLIBRE — small view models for the UI (tree nodes + legend rows).

using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using SmsLibre.Core;

namespace SmsLibre.App;

/// <summary>One node in the Grower/Farm/Field/Dataset navigation tree.</summary>
public sealed class TreeNode
{
    public string Header { get; init; } = "";
    public string Glyph { get; init; } = "";      // simple text icon
    public ObservableCollection<TreeNode> Children { get; } = new();
    public Dataset? Dataset { get; init; }         // set on selectable leaf datasets

    public string Display => string.IsNullOrEmpty(Glyph) ? Header : $"{Glyph}  {Header}";
}

/// <summary>One row in the legend panel.</summary>
public sealed class LegendRow
{
    public IBrush Swatch { get; init; } = Brushes.Transparent;
    public string Text { get; init; } = "";
}

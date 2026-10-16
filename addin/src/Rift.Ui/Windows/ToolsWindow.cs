// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

using Rift.Ui.Kit;

namespace Rift.Ui.Windows;

/// <summary>
/// The Tools window (UI gate 3, tranche 5): numeral = number of tools, full pastille = shipped, hollow = planned, a
/// filter by risk; the same catalogue as the README and the pamphlet (embedded <c>tools.json</c>).
/// </summary>
public sealed class ToolsWindow : RiftWindow
{
    private readonly ToolCatalog _catalog;
    private string? _risk;

    public ToolsWindow(ToolCatalog catalog, IntPtr owner)
        : base("tools", owner, 900, 720, TimeSpan.FromSeconds(30))
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    protected override object Fingerprint() => _risk ?? "*";

    protected override UIElement Build()
    {
        StackPanel body = Heading("tools", _catalog.Tools.Count.ToString(CultureInfo.InvariantCulture),
            N(_catalog.Shipped) + " shipped · " + N(_catalog.Tools.Count - _catalog.Shipped) + " planned · current wave " + _catalog.CurrentWave);

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        filters.Children.Add(Filter("all", null));
        foreach (string risk in _catalog.Risks)
        {
            filters.Children.Add(Filter(risk.Replace('_', ' '), risk));
        }
        body.Children.Add(filters);
        TextBlock legend = Z.Aside("auto: reads only · ask: your approval in the assistant · never auto: always asked, writes the model", Theme.Size.BodySmall);
        legend.Margin = new Thickness(0, 0, 0, 16);
        body.Children.Add(legend);

        var list = new StackPanel();
        foreach (ToolEntry t in _catalog.Filter(_risk))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = Z.Pastille(t.Shipped ? PastilleState.Done : PastilleState.Paused, 14);
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Thickness(0, 3, 0, 0);
            row.Children.Add(dot);
            var text = new StackPanel();
            text.Children.Add(Z.Value(t.Name));
            text.Children.Add(Z.Aside(t.Wave + " · " + t.Toolset + " · " + t.Risk.Replace('_', ' ') + (t.Shipped ? "" : " · planned"), Theme.Size.BodySmall));
            text.Children.Add(Z.Aside(t.Description, Theme.Size.BodySmall));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            list.Children.Add(row);
        }
        body.Children.Add(new Border
        {
            Background = Theme.Brush(Theme.Palette.Inset),
            CornerRadius = new CornerRadius(Theme.Radius.Panel),
            Padding = new Thickness(Theme.Pad.Compact),
            Child = list,
        });
        return body;
    }

    private Button Filter(string verb, string? risk)
    {
        Button b = Z.GhostButton(_risk == risk ? "[" + verb + "]" : verb, () =>
        {
            _risk = risk;
            Refresh(true);
        });
        b.Margin = new Thickness(0, 0, Theme.Size.Gutter, Theme.Size.Gutter);
        return b;
    }

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}

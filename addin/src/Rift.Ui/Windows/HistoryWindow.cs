// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

using Rift.Core.Activity;
using Rift.Ui.Kit;

namespace Rift.Ui.Windows;

/// <summary>
/// The History window (UI gate 3, tranches 4 and 6): numeral THIS SESSION, the pastille grid of the last 49 calls, then
/// one line per call — time, tool, result, code, duration and, for a change, what it added, modified or deleted.
/// A long job is one line that turns from the accent to its final state. Never script text, never model values.
/// </summary>
public sealed class HistoryWindow : RiftWindow
{
    private static readonly string[] s_headers = { "time", "tool", "result", "code", "duration", "changes" };
    private readonly IUiHost _host;

    public HistoryWindow(IUiHost host, IntPtr owner)
        : base("history", owner, 980, 680, TimeSpan.FromMilliseconds(500))
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    protected override object Fingerprint() => _host.Activity().Version;

    protected override UIElement Build()
    {
        ActivitySnapshot s = _host.Activity();
        StackPanel body = Heading("this session", s.Total.ToString(CultureInfo.InvariantCulture),
            s.Total == 0 ? "no call yet · every call of the assistant appears here" : HistoryView.Summary(s));
        if (s.Records.Count > 0)
        {
            Panel grid = Z.PastilleGrid(HistoryView.Grid(s));
            grid.Margin = new Thickness(0, 0, 0, 24);
            body.Children.Add(grid);
            body.Children.Add(Table(HistoryView.Rows(s)));
            if (s.Records.Count > HistoryView.RowsMax)
            {
                TextBlock more = Z.Aside("showing the last " + HistoryView.RowsMax.ToString(CultureInfo.InvariantCulture) + " calls", Theme.Size.BodySmall);
                more.Margin = new Thickness(0, 12, 0, 0);
                body.Children.Add(more);
            }
        }
        return body;
    }

    private static Border Table(IReadOnlyList<HistoryRow> rows)
    {
        var grid = new Grid();
        foreach (GridLength width in new[]
        {
            new GridLength(84), new GridLength(2, GridUnitType.Star), new GridLength(1, GridUnitType.Star),
            new GridLength(1.2, GridUnitType.Star), new GridLength(84), new GridLength(1.6, GridUnitType.Star),
        })
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int c = 0; c < s_headers.Length; c++)
        {
            FrameworkElement h = Z.MicroLabel(s_headers[c]);
            h.Margin = new Thickness(0, 0, 8, 10);
            Place(grid, h, 0, c);
        }
        int r = 1;
        foreach (HistoryRow row in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rule = new Border
            {
                BorderBrush = Theme.Brush(Theme.Palette.Hairline),
                BorderThickness = new Thickness(0, 1, 0, 0),
            };
            Grid.SetColumnSpan(rule, s_headers.Length);
            Place(grid, rule, r, 0);

            Place(grid, Cell(row.Time, quiet: true), r, 0);
            var tool = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = Z.Pastille(row.State, 12);
            dot.Margin = new Thickness(0, 0, 8, 0);
            tool.Children.Add(dot);
            tool.Children.Add(Cell(row.Tool));
            tool.Margin = new Thickness(0, 6, 8, 6);
            Place(grid, tool, r, 1);
            Place(grid, Cell(row.Result), r, 2);
            TextBlock code = Cell(row.Code ?? "", quiet: true);
            if (row.Message is not null)
            {
                code.ToolTip = row.Message;
            }
            Place(grid, code, r, 3);
            Place(grid, Cell(row.Duration, quiet: true), r, 4);
            TextBlock changes = Cell(row.Changes ?? "");
            if (row.ChangeDetail is not null)
            {
                changes.ToolTip = row.ChangeDetail;
                changes.TextDecorations = TextDecorations.Underline;
            }
            Place(grid, changes, r, 5);
            r++;
        }
        return new Border
        {
            Background = Theme.Brush(Theme.Palette.Inset),
            CornerRadius = new CornerRadius(Theme.Radius.Panel),
            Padding = new Thickness(Theme.Pad.Compact),
            Child = grid,
        };
    }

    private static TextBlock Cell(string text, bool quiet = false)
    {
        TextBlock t = quiet ? Z.Aside(text, Theme.Size.BodySmall) : Z.Value(text, Theme.Size.BodySmall);
        t.Margin = new Thickness(0, 6, 8, 6);
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }

    private static void Place(Grid grid, UIElement e, int row, int column)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, column);
        grid.Children.Add(e);
    }
}

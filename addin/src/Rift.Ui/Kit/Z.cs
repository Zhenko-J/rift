// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Rift.Ui.Kit;

/// <summary>
/// The Z_D01 primitives of the windows (UI gate 1 and 3), built in code: card, numeral, micro-label, aside, pastille,
/// pastille grid, inset tile, ghost button. Every colour, size and radius comes from <see cref="Theme"/>.
/// </summary>
public static class Z
{
    public const double NumeralSize = 96;
    public const double GridPastille = 34;
    public const double LabelTrackingEm = 0.2;

    /// <summary>The main panel: card paper, rounded corners, generous padding.</summary>
    public static Border Card(UIElement child, double radius = Theme.Radius.Window, bool compact = false) => new()
    {
        Background = Theme.Brush(Theme.Palette.Card),
        CornerRadius = new CornerRadius(radius),
        Padding = new Thickness(compact ? Theme.Pad.Compact : Theme.Pad.Card),
        Child = child,
    };

    /// <summary>The big number of a view: bare digits, heavy, slightly tight.</summary>
    public static TextBlock Numeral(string digits, double size = NumeralSize) => new()
    {
        Text = digits,
        FontFamily = Theme.Mono,
        FontSize = size,
        FontWeight = FontWeights.Black,
        Foreground = Theme.Brush(Theme.Palette.Ink),
        Margin = new Thickness(-4, -12, 0, -6),
    };

    /// <summary>Upper-case label tracked by 0.2 em (WPF has no letter-spacing: one glyph per cell).</summary>
    public static FrameworkElement MicroLabel(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        string upper = text.ToUpper(CultureInfo.InvariantCulture);
        double gap = Theme.Size.Label * LabelTrackingEm;
        for (int i = 0; i < upper.Length; i++)
        {
            row.Children.Add(new TextBlock
            {
                Text = upper[i].ToString(),
                FontFamily = Theme.Mono,
                FontSize = Theme.Size.Label,
                Foreground = Theme.Brush(Theme.Palette.Quiet),
                Margin = new Thickness(0, 0, i == upper.Length - 1 ? 0 : gap, 0),
            });
        }
        System.Windows.Automation.AutomationProperties.SetName(row, upper);
        return row;
    }

    /// <summary>The lower-case aside: the voice that explains, without a final period.</summary>
    public static TextBlock Aside(string text, double size = Theme.Size.Body) => new()
    {
        Text = text,
        FontFamily = Theme.Mono,
        FontSize = size,
        Foreground = Theme.Brush(Theme.Palette.SubInk),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Plain text in ink, for values in tiles and tables.</summary>
    public static TextBlock Value(string text, double size = Theme.Size.Body) => new()
    {
        Text = text,
        FontFamily = Theme.Mono,
        FontSize = size,
        Foreground = Theme.Brush(Theme.Palette.Ink),
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>Full ink = done, accent = running (the one accent of a view), full red = failed or refused, hollow = paused or planned.</summary>
    public static Ellipse Pastille(PastilleState state, double size = Theme.Size.Pastille)
    {
        (Brush fill, Color stroke) = state switch
        {
            PastilleState.Running => (Theme.Brush(Theme.Palette.Accent), Theme.Palette.Accent),
            PastilleState.Failed => (Theme.Brush(Theme.Palette.Failure), Theme.Palette.Failure),
            PastilleState.Paused => (Brushes.Transparent, Theme.Palette.Stroke),
            _ => (Theme.Brush(Theme.Palette.Ink), Theme.Palette.Ink),
        };
        return new Ellipse
        {
            Width = size,
            Height = size,
            Fill = fill,
            Stroke = Theme.Brush(stroke),
            StrokeThickness = Theme.Size.StrokeWidth,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>Pastilles in rows of <paramref name="perRow"/>, the last row left ragged.</summary>
    public static Panel PastilleGrid(IReadOnlyList<PastilleState> states, int perRow = 7, double size = GridPastille)
    {
        var grid = new WrapPanel { Width = perRow * (size + 8), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (PastilleState s in states)
        {
            Ellipse dot = Pastille(s, size);
            dot.Margin = new Thickness(0, 0, 8, 8);
            grid.Children.Add(dot);
        }
        return grid;
    }

    /// <summary>A recessed tile: micro-label, value, optional aside; no shadow.</summary>
    public static Border InsetTile(string label, string value, string? aside = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(MicroLabel(label));
        TextBlock v = Value(value);
        v.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(v);
        if (!string.IsNullOrEmpty(aside))
        {
            TextBlock a = Aside(aside, Theme.Size.BodySmall);
            a.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(a);
        }
        return new Border
        {
            Background = Theme.Brush(Theme.Palette.Inset),
            CornerRadius = new CornerRadius(Theme.Radius.Panel),
            Padding = new Thickness(Theme.Pad.Compact),
            Margin = new Thickness(0, 0, Theme.Size.Gutter, Theme.Size.Gutter),
            Child = stack,
        };
    }

    /// <summary>
    /// A verb in lower case in a round 2 px outline, at least 44 px high. <paramref name="selected"/> fills it with the
    /// accent and writes the verb on it (the chosen option of a question or a filter).
    /// </summary>
    public static Button GhostButton(string verb, Action onClick, bool selected = false)
    {
        ArgumentNullException.ThrowIfNull(onClick);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BorderBrushProperty, Theme.Brush(selected ? Theme.Palette.Accent : Theme.Palette.Stroke));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(Theme.Size.StrokeWidth));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(Theme.Size.HitMin / 2));
        border.SetValue(Border.BackgroundProperty, selected ? Theme.Brush(Theme.Palette.Accent) : Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(18, 0, 18, 0));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var button = new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
            Content = selected ? OnAccent(verb) : Value(verb),
            MinHeight = Theme.Size.HitMin,
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(0, 0, Theme.Size.Gutter, 0),
        };
        button.Click += (_, _) => onClick();
        System.Windows.Automation.AutomationProperties.SetName(button, selected ? verb + " (selected)" : verb);
        return button;
    }

    private static TextBlock OnAccent(string text)
    {
        TextBlock t = Value(text);
        t.Foreground = Theme.Brush(Theme.Palette.OnAccent);
        return t;
    }
}

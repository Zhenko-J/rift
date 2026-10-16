// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
//
// Icon outlines from Lucide (https://lucide.dev), ISC License, Copyright (c) Lucide Contributors 2026.
// Path data copied from the Lucide SVG sources on a 24 x 24 box, rewritten with explicit separators for WPF.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rift.Revit.Ui;

/// <summary>
/// Ribbon icons (ADR-029 §4): Lucide outlines kept as path data and rendered at start-up to 16 and 32 px bitmaps at the
/// monitor's scale — stroke 1.75 on the 24 box, round caps and joins, ink, never filled. No PNG in the repository, no
/// generated files, and nothing loaded through <c>pack://</c> (unsafe in the isolated load context, ADR-023).
/// Must be called on a thread with a WPF dispatcher (Revit's UI thread in <c>Host.Start</c>).
/// </summary>
internal static class Icons
{
    /// <summary>Lucide "gauge": the Status button.</summary>
    public const string Gauge = "gauge";

    /// <summary>Lucide "flask-conical": the developer-only Probe Python button.</summary>
    public const string Flask = "flask-conical";

    /// <summary>Lucide "power": the server toggle while the server is on (painted in the accent).</summary>
    public const string Power = "power";

    /// <summary>Lucide "pause": the server toggle while paused.</summary>
    public const string Pause = "pause";

    /// <summary>Z#D01 ink <c>#191918</c>, on Revit's light theme (the theme file arrives with the UI work stream).</summary>
    public static readonly Color InkOnLight = Color.FromRgb(0x19, 0x19, 0x18);

    /// <summary>Z#D01 card paper <c>#EAE9E6</c>, used as ink on Revit's dark theme (Z#D01 has no dark palette).</summary>
    public static readonly Color InkOnDark = Color.FromRgb(0xEA, 0xE9, 0xE6);

    private const double Box = 24;
    private const double Stroke = 1.75;

    private static readonly Dictionary<string, string[]> s_paths = new(StringComparer.Ordinal)
    {
        [Gauge] = new[]
        {
            "M12 14 L16 10",
            "M3.34 19 A10 10 0 1 1 20.66 19",
        },
        [Flask] = new[]
        {
            "M14 2 V8 A2 2 0 0 0 14.245 8.96 L19.755 19.04 A2 2 0 0 1 18 22 H6 A2 2 0 0 1 4.245 19.04 L9.755 8.96 A2 2 0 0 0 10 8 V2",
            "M6.453 15 H17.547",
            "M8.5 2 H15.5",
        },
        [Power] = new[]
        {
            "M12 2 V12",
            "M18.4 6.6 A9 9 0 1 1 5.63 6.64",
        },
        [Pause] = new[]
        {
            "M15 4 H17 A1 1 0 0 1 18 5 V19 A1 1 0 0 1 17 20 H15 A1 1 0 0 1 14 19 V5 A1 1 0 0 1 15 4 Z",
            "M7 4 H9 A1 1 0 0 1 10 5 V19 A1 1 0 0 1 9 20 H7 A1 1 0 0 1 6 19 V5 A1 1 0 0 1 7 4 Z",
        },
    };

    public static IReadOnlyCollection<string> Names => s_paths.Keys;

    /// <summary>
    /// Renders <paramref name="name"/> as a frozen bitmap of <paramref name="px"/> device-independent pixels, i.e.
    /// <c>round(px × scale)</c> physical pixels at the matching DPI, so that WPF shows it at its nominal size without
    /// resampling on a scaled monitor. <paramref name="ink"/> defaults to <see cref="InkOnLight"/>.
    /// </summary>
    public static BitmapSource Render(string name, int px, double scale, Color? ink = null)
    {
        if (!s_paths.TryGetValue(name, out string[]? paths))
        {
            throw new ArgumentException("unknown icon: " + name, nameof(name));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(px);
        scale = scale is > 0 and <= 4 ? scale : 1.0;
        int size = Math.Max(1, (int)Math.Round(px * scale));
        // DPI derived from the rounded size, so that the bitmap is exactly px device-independent pixels wide.
        double dpi = 96.0 * size / px;
        // The visual is drawn in device-independent units; the bitmap DPI maps them to physical pixels.
        double factor = px / Box;

        var group = new GeometryGroup();
        foreach (string data in paths)
        {
            group.Children.Add(Geometry.Parse(data));
        }
        group.Transform = new ScaleTransform(factor, factor);

        var pen = new Pen(new SolidColorBrush(ink ?? InkOnLight), Stroke * factor)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawGeometry(null, pen, group);
        }
        var bitmap = new RenderTargetBitmap(size, size, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

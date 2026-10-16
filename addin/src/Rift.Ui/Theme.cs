// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System.Collections.Generic;
using System.Windows.Media;

namespace Rift.Ui;

/// <summary>
/// The only file with colours, radii, sizes and the font of the interface (UI gate 1, "Langage visuel", values of the
/// Z_D01 system taken verbatim). Brushes are cached and frozen. Tranche 1 holds what the activity card needs; the kit of
/// tranche 3 adds the shadow, the motion and the tracking.
/// </summary>
public static class Theme
{
    public static class Palette
    {
        /// <summary>Window background (the "ground").</summary>
        public static readonly Color Ground = Color.FromRgb(0xCF, 0xCD, 0xC7);

        /// <summary>Card and main panel.</summary>
        public static readonly Color Card = Color.FromRgb(0xEA, 0xE9, 0xE6);

        /// <summary>Inset areas (tables, tiles).</summary>
        public static readonly Color Inset = Color.FromRgb(0xE0, 0xDF, 0xDB);

        public static readonly Color Ink = Color.FromRgb(0x19, 0x19, 0x18);

        public static readonly Color SubInk = Color.FromRgb(0x3C, 0x3C, 0x39);

        public static readonly Color Quiet = Color.FromRgb(0x64, 0x63, 0x5E);

        /// <summary>1 px rules.</summary>
        public static readonly Color Hairline = Color.FromRgb(0xC6, 0xC5, 0xBF);

        /// <summary>2 px strokes, reserved for pastilles and ghost buttons.</summary>
        public static readonly Color Stroke = Color.FromRgb(0x1F, 0x1F, 0x1E);

        /// <summary>The single "now" accent: running, and the "server on" icon.</summary>
        public static readonly Color Accent = Color.FromRgb(0xD9, 0x36, 0x11);

        /// <summary>Text on the accent only.</summary>
        public static readonly Color OnAccent = Color.FromRgb(0xFF, 0xFF, 0xFF);

        public static readonly Color Success = Color.FromRgb(0x3E, 0x7A, 0x5E);

        public static readonly Color Warning = Color.FromRgb(0xB9, 0x8A, 0x22);

        public static readonly Color Failure = Color.FromRgb(0xB2, 0x45, 0x2E);
    }

    public static class Size
    {
        public const double Body = 15;
        public const double BodySmall = 13;
        public const double Label = 11;
        public const double Pastille = 22;
        public const double StrokeWidth = 2;
        public const double CardMinWidth = 260;
        public const double CardMaxWidth = 420;
        public const double HitMin = 44;
        public const double Gutter = 12;
    }

    public static class Radius
    {
        public const double Window = 28;
        public const double Panel = 20;
        public const double Control = 14;
    }

    public static class Pad
    {
        public const double Card = 34;
        public const double Compact = 20;
    }

    /// <summary>The one shadow of the interface: under the floating activity card only (the windows sit on the ground).</summary>
    public static System.Windows.Media.Effects.DropShadowEffect CardShadow()
    {
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Palette.Ink,
            BlurRadius = 18,
            ShadowDepth = 4,
            Direction = 270,
            Opacity = 0.18,
        };
        shadow.Freeze();
        return shadow;
    }

    /// <summary>Margin the card window keeps around the card so that its shadow is not clipped.</summary>
    public const double ShadowMargin = 14;

    /// <summary>Consolas everywhere.</summary>
    public static readonly FontFamily Mono = new("Consolas, Courier New");

    private static readonly Dictionary<Color, SolidColorBrush> s_brushes = new();
    private static readonly object s_gate = new();

    /// <summary>A frozen brush of <paramref name="color"/>, created once.</summary>
    public static Brush Brush(Color color)
    {
        lock (s_gate)
        {
            if (!s_brushes.TryGetValue(color, out SolidColorBrush? brush))
            {
                brush = new SolidColorBrush(color);
                brush.Freeze();
                s_brushes[color] = brush;
            }
            return brush;
        }
    }
}

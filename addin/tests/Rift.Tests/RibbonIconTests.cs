// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Xunit;

using Rift.Revit.Ui;

namespace Rift.Tests;

/// <summary>
/// UI plan test 16 (early, M1 tranche 6 item "icons for the existing buttons"): every ribbon icon renders at 16 and 32 px
/// on a scaled monitor, with ink and nothing else; the ribbon names commands that exist in the bootstrap.
/// </summary>
public sealed class RibbonIconTests
{
    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("on STA: " + failure);
        }
    }

    [Theory]
    [InlineData(1.0, 16, 16)]
    [InlineData(1.0, 32, 32)]
    [InlineData(1.25, 16, 20)]
    [InlineData(1.5, 16, 24)]
    [InlineData(1.5, 32, 48)]
    [InlineData(1.75, 32, 56)]
    [InlineData(2.0, 32, 64)]
    [InlineData(1.1, 16, 18)]
    public void Every_icon_renders_at_the_monitor_scale_in_ink_outlined_and_unclipped(double scale, int px, int expected)
    {
        OnSta(() =>
        {
            foreach (string name in Icons.Names)
            {
                foreach (Color ink in new[] { Icons.InkOnLight, Icons.InkOnDark })
                {
                    BitmapSource bmp = Icons.Render(name, px, scale, ink);
                    Assert.Equal((expected, expected), (bmp.PixelWidth, bmp.PixelHeight));
                    // Shown at its nominal size in device-independent pixels, whatever the rounding of the scale.
                    Assert.Equal(px, bmp.Width, 3);
                    Assert.Equal(px, bmp.Height, 3);
                    Assert.True(bmp.IsFrozen);
                    Check(name + " " + ink + " " + px + "px x" + scale, bmp, expected, ink);
                }
            }
        });
    }

    /// <summary>Unpremultiplied colour of every drawn pixel is the ink (±3); the 1-px border is empty; the drawing spans the box.</summary>
    private static void Check(string what, BitmapSource bmp, int n, Color ink)
    {
        var pixels = new byte[n * n * 4];
        bmp.CopyPixels(pixels, n * 4, 0);
        int inked = 0, minX = n, maxX = -1, minY = n, maxY = -1;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = ((y * n) + x) * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                if (a == 0)
                {
                    continue;
                }
                // Lucide leaves a 1-unit margin minus half the stroke: at 16 px the antialiased edge may touch the border
                // pixel, but a clipped drawing would paint it solid.
                bool border = x == 0 || y == 0 || x == n - 1 || y == n - 1;
                Assert.False(border && a > 128, what + ": solid on the border (clipped) at " + x + "," + y);
                inked++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                if (a >= 64)
                {
                    Assert.InRange(r * 255 / a, ink.R - 3, ink.R + 3);
                    Assert.InRange(g * 255 / a, ink.G - 3, ink.G + 3);
                    Assert.InRange(b * 255 / a, ink.B - 3, ink.B + 3);
                }
            }
        }
        Assert.True(inked > n, what + ": almost nothing drawn");
        Assert.True(inked < n * n / 2, what + ": filled instead of outlined");
        // The larger extent: some outlines are narrow by design (Lucide "pause" spans ~57 % of the width, ~70 % of the height).
        Assert.True(Math.Max(maxX - minX, maxY - minY) + 1 >= n * 0.6, what + ": drawing smaller than 60 % of the box (wrong scale)");
    }

    [Fact]
    public void Without_an_ink_the_light_theme_ink_is_used()
    {
        OnSta(() =>
        {
            BitmapSource bmp = Icons.Render(Icons.Gauge, 32, 1.0);
            Check("default ink", bmp, 32, Icons.InkOnLight);
        });
    }

    [Fact]
    public void An_unknown_icon_is_an_argument_error()
    {
        OnSta(() => Assert.Throws<ArgumentException>(() => Icons.Render("nope", 16, 1.0)));
    }

    [Fact]
    public void The_ribbon_names_commands_declared_in_the_bootstrap_and_gives_every_button_an_icon()
    {
        string src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        string ribbon = File.ReadAllText(Path.Combine(src, "Rift.Revit", "Ui", "Ribbon.cs"));
        string bootstrap = string.Join("\n", Directory.GetFiles(Path.Combine(src, "Rift.Addin"), "*.cs").Select(File.ReadAllText));
        string[] commands = Regex.Matches(ribbon, "\"Rift\\.Addin\\.(\\w+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.NotEmpty(commands);
        foreach (string command in commands)
        {
            // A command (directly or through RelayCommand), or the availability class named by AvailabilityClassName.
            Assert.Matches(@"public sealed class " + command + @"\s*:\s*(IExternalCommand|RelayCommand|IExternalCommandAvailability)\b", bootstrap);
        }
        int buttons = Regex.Matches(ribbon, "new PushButtonData\\(").Count;
        int images = Regex.Matches(ribbon, @"^\s*SetImages\(\w+, Icons\.\w+, [\w.]+\);", RegexOptions.Multiline).Count;
        Assert.Equal(buttons, images);
        Assert.DoesNotContain("BuildRibbon", bootstrap, StringComparison.Ordinal);
    }
}

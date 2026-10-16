// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media;

using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

using Rift.Core;
using Rift.Core.Logging;
using Rift.Ui;

namespace Rift.Revit.Ui;

/// <summary>
/// The RIFT panel of the shared NORN tab (ADR-028 §7), built on the isolated side (ADR-029 §1): a
/// <see cref="PushButtonData"/> names its command by assembly path and class name only, so the commands stay in the
/// bootstrap while images and layout live here. Every button has an icon (no blank slot), inked for Revit's light or dark
/// theme and repainted when the theme changes; an icon that cannot be drawn never costs its button. "Probe Python" is a
/// development tool shown only with <c>developer: true</c> in <c>config.json</c>.
/// </summary>
internal static partial class Ribbon
{
    // Texts of the buttons (UI gate 1, voice Z_D01: lower case, no final period). The bold title of a tooltip is the
    // button's ItemText, shown by Revit itself: the tooltip does not repeat it.
    internal const string ServerOnText = "server\non";
    internal const string PausedText = "paused";
    internal const string ServerOnTip = "the assistant can reach this model\nclick to pause it";
    internal static string PausedTip(string at) => "paused by you at " + at + " · every call gets PAUSED\nclick to resume";
    internal const string ServerLong =
        "pausing refuses every assistant call with PAUSED before anything runs\n"
        + "a call already running is allowed to finish; queued calls are cancelled\n"
        + "Revit always starts with the server on: the pause is not remembered\n"
        + "nothing in the model changes when you pause or resume";
    internal const string PausedLong =
        "while paused the assistant holds no lease and no queued job runs\n"
        + "resuming lets its next call through; it does not replay refused calls";
    internal const string StatusTip = "who is connected, since when, and where the log is";
    internal const string StatusLong =
        "shows the add-in version, the server state, the pipe, the client holding the lease, the last refusal and the script engine\n"
        + "holds the activity card switch and copies the log path: the add-in never opens a program";
    internal const string ProbeTip = "developer check of the embedded IronPython 3.4 engine";
    internal const string ProbeLong = "shown only with developer: true in config.json\nreads nothing from the model and changes nothing";

    private static readonly List<(PushButton Button, string Icon)> s_buttons = new();
    private static double s_scale = 1.0;
    private static FileLog s_log = FileLog.Null;
    private static PushButton? s_toggle;
    private static bool s_paused;

    public static void Build(UIControlledApplication app, string bootstrapPath, bool developer, FileLog log)
    {
        s_log = log;
        // Computed first: nothing between the panel's creation and its buttons may throw.
        s_scale = Scale(app.MainWindowHandle);

        // Whichever plugin of the suite loads first creates the tab; the others reuse it.
        try
        {
            app.CreateRibbonTab(Brand.RibbonTab);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
        }
        RibbonPanel panel = app.CreateRibbonPanel(Brand.RibbonTab, Brand.RibbonPanel);
        Color ink = Ink();

        // The pause (ADR-029 §2): the only orange element of the ribbon while the server is on.
        var toggle = new PushButtonData(Brand.Id + ".server.toggle", ServerOnText, bootstrapPath, "Rift.Addin.ServerToggleCommand")
        {
            ToolTip = ServerOnTip,
            LongDescription = ServerLong,
            AvailabilityClassName = Availability,
        };
        SetImages(toggle, Icons.Power, Theme.Palette.Accent);
        s_toggle = panel.AddItem(toggle) as PushButton;

        var status = new PushButtonData(Brand.Id + ".status", "status", bootstrapPath, "Rift.Addin.StatusCommand")
        {
            ToolTip = StatusTip,
            LongDescription = StatusLong,
            AvailabilityClassName = Availability,
        };
        SetImages(status, Icons.Gauge, ink);
        Add(panel, status, Icons.Gauge);

        if (developer)
        {
            var probe = new PushButtonData(Brand.Id + ".probe.python", "probe\npython", bootstrapPath, "Rift.Addin.ProbePythonCommand")
            {
                ToolTip = ProbeTip,
                LongDescription = ProbeLong,
            };
            SetImages(probe, Icons.Flask, ink);
            Add(panel, probe, Icons.Flask);
        }

        try
        {
            app.ThemeChanged += OnThemeChanged;
        }
        catch (Exception ex)
        {
            log.Write("ribbon: theme events unavailable: " + ex.Message);
        }
        log.Write("ribbon: tab=" + Brand.RibbonTab + " panel=" + Brand.RibbonPanel + " developer=" + developer
            + " scale=" + s_scale.ToString("0.##", CultureInfo.InvariantCulture) + " ink=" + ink);
    }

    /// <summary>Class of the bootstrap that keeps the server and status buttons usable with no document open.</summary>
    private const string Availability = "Rift.Addin.AlwaysAvailable";

    /// <summary>
    /// Shows the pause on the toggle: text, tooltip and icon (accent "power" when on, ink "pause" when paused). API or UI
    /// thread only (a command, or <c>EnqueueUi</c>); an open tooltip refreshes on the next hover. Never throws.
    /// </summary>
    public static void SetPaused(bool paused, DateTimeOffset? sinceUtc)
    {
        s_paused = paused;
        PushButton? toggle = s_toggle;
        if (toggle is null)
        {
            return;
        }
        try
        {
            toggle.ItemText = paused ? PausedText : ServerOnText;
            toggle.ToolTip = paused
                ? PausedTip(sinceUtc?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) ?? "?")
                : ServerOnTip;
            toggle.LongDescription = paused ? PausedLong : ServerLong;
            PaintToggle(toggle, Ink());
        }
        catch (Exception ex)
        {
            s_log.Write("ribbon: pause display failed: " + ex.Message);
        }
    }

    private static void PaintToggle(PushButton toggle, Color ink)
    {
        string icon = s_paused ? Icons.Pause : Icons.Power;
        Color color = s_paused ? ink : Theme.Palette.Accent;
        toggle.LargeImage = Icons.Render(icon, 32, s_scale, color);
        toggle.Image = Icons.Render(icon, 16, s_scale, color);
    }

    private static void Add(RibbonPanel panel, PushButtonData data, string icon)
    {
        if (panel.AddItem(data) is PushButton button)
        {
            s_buttons.Add((button, icon));
        }
    }

    private static void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        Color ink = Ink();
        foreach ((PushButton button, string icon) in s_buttons)
        {
            try
            {
                button.LargeImage = Icons.Render(icon, 32, s_scale, ink);
                button.Image = Icons.Render(icon, 16, s_scale, ink);
            }
            catch (Exception ex)
            {
                s_log.Write("ribbon: icon " + icon + " repaint failed: " + ex.Message);
            }
        }
        if (s_toggle is not null)
        {
            try
            {
                PaintToggle(s_toggle, ink);
            }
            catch (Exception ex)
            {
                s_log.Write("ribbon: toggle repaint failed: " + ex.Message);
            }
        }
        s_log.Write("ribbon: theme changed, icons repainted with ink " + ink);
    }

    /// <summary>Dark ink on Revit's light theme, paper on its dark theme (Z#D01 has no dark palette: ADR-029 keeps it minimal).</summary>
    private static Color Ink()
    {
        try
        {
            return UIThemeManager.CurrentTheme == UITheme.Dark ? Icons.InkOnDark : Icons.InkOnLight;
        }
        catch (Exception)
        {
            return Icons.InkOnLight;
        }
    }

    private static void SetImages(PushButtonData button, string icon, Color ink)
    {
        try
        {
            button.LargeImage = Icons.Render(icon, 32, s_scale, ink);
            button.Image = Icons.Render(icon, 16, s_scale, ink);
        }
        catch (Exception ex)
        {
            // An icon must never cost its button: the button keeps its text.
            s_log.Write("ribbon: icon " + icon + " failed: " + ex.Message);
        }
    }

    /// <summary>Monitor scale of Revit's main window, else of the system (1.0 at 96 DPI); 1.0 when unknown.</summary>
    private static double Scale(IntPtr hwnd)
    {
        try
        {
            uint dpi = hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd);
            if (dpi == 0)
            {
                // At OnStartup the main window may not exist yet.
                dpi = GetDpiForSystem();
            }
            return ScaleOf(dpi);
        }
        catch (Exception)
        {
            return 1.0;
        }
    }

    /// <summary>96 → 1.0, 144 → 1.5; anything outside 96..384 (0 = unknown) → 1.0.</summary>
    internal static double ScaleOf(uint dpi) => dpi is >= 96 and <= 384 ? dpi / 96.0 : 1.0;

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();
}

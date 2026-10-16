// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

using Rift.Core;
using Rift.Ui.Kit;

namespace Rift.Ui.Windows;

/// <summary>
/// Base of the RIFT windows (UI gate 3): modeless, owned by Revit's main window, ground behind one Z_D01 card, Consolas,
/// Esc closes. A refresh timer redraws the body only when <see cref="Fingerprint"/> changes; it stops with the window.
/// </summary>
public abstract class RiftWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly ScrollViewer _scroll;
    private object? _last;

    protected RiftWindow(string title, IntPtr owner, double width, double height, TimeSpan refresh)
    {
        Title = Brand.Product + " · " + title;
        Width = width;
        Height = height;
        MinWidth = 420;
        MinHeight = 320;
        Background = Theme.Brush(Theme.Palette.Ground);
        FontFamily = Theme.Mono;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowStartupLocation = owner == IntPtr.Zero ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        if (owner != IntPtr.Zero)
        {
            _ = new WindowInteropHelper(this) { Owner = owner };
        }
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Content = new Border { Padding = new Thickness(16), Child = Z.Card(_scroll) };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
        _timer = new DispatcherTimer(refresh, DispatcherPriority.Background, (_, _) => Refresh(false), Dispatcher);
        Loaded += (_, _) =>
        {
            Refresh(true);
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>A value equal between two refreshes when nothing visible changed.</summary>
    protected abstract object Fingerprint();

    /// <summary>The body of the card, rebuilt when <see cref="Fingerprint"/> changes.</summary>
    protected abstract UIElement Build();

    /// <summary>Redraws now (after a button), keeping the scroll position.</summary>
    protected void Refresh(bool force)
    {
        object now;
        try
        {
            now = Fingerprint();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }
        if (!force && Equals(now, _last))
        {
            return;
        }
        _last = now;
        double offset = _scroll.VerticalOffset;
        _scroll.Content = Build();
        _scroll.ScrollToVerticalOffset(offset);
    }

    /// <summary>A heading block: micro-label, the numeral, an aside.</summary>
    protected static StackPanel Heading(string label, string numeral, string aside)
    {
        var stack = new StackPanel();
        stack.Children.Add(Z.MicroLabel(label));
        stack.Children.Add(Z.Numeral(numeral));
        TextBlock a = Z.Aside(aside);
        a.Margin = new Thickness(0, 0, 0, 24);
        stack.Children.Add(a);
        return stack;
    }

    /// <summary>Copies <paramref name="text"/>, with one retry when another process holds the clipboard.</summary>
    protected static bool Copy(string text)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(80);
            }
        }
        return false;
    }
}

/// <summary>One window of each kind at a time (UI gate 3): a second click brings the open one to the front. UI thread only.</summary>
public static class UiWindows
{
    private static readonly Dictionary<string, Window> s_open = new(StringComparer.Ordinal);

    public static Window Show(string key, Func<Window> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        if (s_open.TryGetValue(key, out Window? open))
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }
            open.Activate();
            return open;
        }
        Window created = create();
        s_open[key] = created;
        created.Closed += (_, _) => s_open.Remove(key);
        created.Show();
        return created;
    }

    public static void Close(string key)
    {
        if (s_open.TryGetValue(key, out Window? open))
        {
            open.Close();
        }
    }

    public static void CloseAll()
    {
        foreach (Window w in new List<Window>(s_open.Values))
        {
            w.Close();
        }
    }
}

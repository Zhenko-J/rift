// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using Rift.Core;
using Rift.Core.Logging;

namespace Rift.Ui;

/// <summary>
/// The in-Revit activity card (ADR-023, UI gate 1): a small non-modal WPF window built in code (no XAML, no BAML lookup
/// from the isolated load context), owned by Revit's main window, never activated, updated in place, hidden by a timer.
/// Z_D01 card: paper <c>#EAE9E6</c>, 20 px corners, Consolas, one pastille, a micro-label, a lower-case aside; the code
/// and the message only on failure. No numeral (accepted exception, gate 1). Never script text, never model data.
/// </summary>
public sealed partial class ActivityCard : IDisposable
{
    private const int GwlExStyle = -20;
    internal const int WsExNoActivate = 0x08000000;
    internal const int WsExToolWindow = 0x00000080;

    private readonly Window _window;
    private readonly Ellipse _pastille;
    private readonly TextBlock _label;
    private readonly TextBlock _aside;
    private readonly TextBlock _code;
    private readonly TextBlock _message;
    private readonly DispatcherTimer _hide;
    private readonly FileLog _log;
    private readonly IntPtr _owner;

    public ActivityCard(IntPtr revitMainWindow, FileLog log)
    {
        _log = log;
        _owner = revitMainWindow;
        _pastille = new Ellipse
        {
            Width = Theme.Size.Pastille,
            Height = Theme.Size.Pastille,
            StrokeThickness = Theme.Size.StrokeWidth,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _label = Text(Theme.Size.Label, Theme.Palette.Quiet);
        _label.Margin = new Thickness(10, 0, 0, 0);
        _label.VerticalAlignment = VerticalAlignment.Center;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(_pastille);
        head.Children.Add(_label);

        _aside = Text(Theme.Size.Body, Theme.Palette.Ink);
        _aside.Margin = new Thickness(0, 10, 0, 0);
        _aside.TextWrapping = TextWrapping.Wrap;
        _code = Text(Theme.Size.Label, Theme.Palette.Quiet);
        _code.Margin = new Thickness(0, 10, 0, 0);
        _message = Text(Theme.Size.BodySmall, Theme.Palette.SubInk);
        _message.Margin = new Thickness(0, 4, 0, 0);
        _message.TextWrapping = TextWrapping.Wrap;

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(_aside);
        stack.Children.Add(_code);
        stack.Children.Add(_message);
        var card = new Border
        {
            Background = Theme.Brush(Theme.Palette.Card),
            CornerRadius = new CornerRadius(Theme.Radius.Panel),
            Padding = new Thickness(Theme.Pad.Compact),
            Child = stack,
        };
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Topmost = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = Theme.Size.CardMinWidth,
            MaxWidth = Theme.Size.CardMaxWidth,
            Content = card,
            Title = Brand.Product + " activity",
        };
        _window.SourceInitialized += (_, _) => ApplyNoActivate();
        _hide = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = TimeSpan.FromSeconds(6) };
        _hide.Tick += (_, _) => Hide();
        Enabled = true;
    }

    public bool Enabled { get; set; }

    /// <summary>For the tests: the window, the pastille and the texts as last shown.</summary>
    internal Window Window => _window;

    internal Ellipse Pastille => _pastille;

    internal string Shown => _label.Text + "\n" + _aside.Text + "\n" + _code.Text + "\n" + _message.Text;

    public void Show(CardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Enabled)
        {
            return;
        }
        try
        {
            Fill(content);
            if (!_window.IsVisible)
            {
                if (_owner != IntPtr.Zero && new WindowInteropHelper(_window).Owner == IntPtr.Zero)
                {
                    new WindowInteropHelper(_window).Owner = _owner;
                }
                _window.Show();
            }
            Place();
            _hide.Stop();
            _hide.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.Write("card show failed: " + ex.Message);
        }
    }

    public void Hide()
    {
        try
        {
            _hide.Stop();
            if (_window.IsVisible)
            {
                _window.Hide();
            }
        }
        catch (InvalidOperationException ex)
        {
            _log.Write("card hide failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            _hide.Stop();
            _window.Close();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>Puts <paramref name="content"/> in place without showing the window (used by <see cref="Show"/> and the tests).</summary>
    internal void Fill(CardContent content)
    {
        (Color fill, Color stroke) = content.State switch
        {
            PastilleState.Running => (Theme.Palette.Accent, Theme.Palette.Accent),
            PastilleState.Failed => (Theme.Palette.Failure, Theme.Palette.Failure),
            PastilleState.Paused => (Colors.Transparent, Theme.Palette.Stroke),
            _ => (Theme.Palette.Ink, Theme.Palette.Ink),
        };
        _pastille.Fill = content.State == PastilleState.Paused ? Brushes.Transparent : Theme.Brush(fill);
        _pastille.Stroke = Theme.Brush(stroke);
        _label.Text = (Brand.Product + " · " + content.Label).ToUpper(CultureInfo.InvariantCulture);
        _aside.Text = content.Aside;
        bool failed = content.Code is not null;
        _code.Text = content.Code ?? string.Empty;
        _code.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
        _message.Text = content.Message ?? string.Empty;
        _message.Visibility = failed && !string.IsNullOrEmpty(content.Message) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Creates the native window without showing it; returns its extended style (for the tests).</summary>
    internal int EnsureHandleAndReadStyle()
    {
        IntPtr hwnd = new WindowInteropHelper(_window).EnsureHandle();
        return GetWindowLongW(hwnd, GwlExStyle);
    }

    private static TextBlock Text(double size, Color ink) => new()
    {
        FontFamily = Theme.Mono,
        FontSize = size,
        Foreground = Theme.Brush(ink),
        TextWrapping = TextWrapping.NoWrap,
    };

    private void Place()
    {
        if (_owner == IntPtr.Zero || !GetWindowRect(_owner, out Rect r))
        {
            return;
        }
        _window.UpdateLayout();
        PresentationSource? source = PresentationSource.FromVisual(_window);
        double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double width = _window.ActualWidth > 0 ? _window.ActualWidth : _window.MinWidth;
        double height = _window.ActualHeight > 0 ? _window.ActualHeight : 60;
        _window.Left = (r.Right / scale) - width - 24;
        _window.Top = (r.Bottom / scale) - height - 48;
    }

    private void ApplyNoActivate()
    {
        IntPtr hwnd = new WindowInteropHelper(_window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }
        int style = GetWindowLongW(hwnd, GwlExStyle);
        _ = SetWindowLongW(hwnd, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowLongW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowLongW(IntPtr hwnd, int index, int value);
}

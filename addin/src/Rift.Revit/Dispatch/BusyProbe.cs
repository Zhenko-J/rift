// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Runtime.InteropServices;

using Rift.Core.Json;

namespace Rift.Revit.Dispatch;

/// <summary>
/// Why Revit did not start a job in time (ADR-008/025, <c>REVIT_BUSY</c>): a disabled main window means a modal dialog,
/// named by its title. Called from the pipe thread while the Revit thread is blocked, so it never sends a window message:
/// <c>InternalGetWindowText</c> reads the title without <c>WM_GETTEXT</c>. Titles are sanitised and shortened; they
/// reach the client and the activity card.
/// </summary>
internal sealed partial class BusyProbe
{
    private const uint GwEnabledPopup = 6;

    private readonly IntPtr _mainWindow;

    public BusyProbe(IntPtr revitMainWindow)
    {
        _mainWindow = revitMainWindow;
    }

    public string? Reason()
    {
        if (_mainWindow == IntPtr.Zero)
        {
            return null;
        }
        if (IsWindowEnabled(_mainWindow))
        {
            return "Revit did not start the request in time (a command, an edit mode or a long operation is in progress)";
        }
        IntPtr popup = GetWindow(_mainWindow, GwEnabledPopup);
        if (popup == IntPtr.Zero || popup == _mainWindow)
        {
            popup = GetLastActivePopup(_mainWindow);
        }
        string title = popup != IntPtr.Zero && popup != _mainWindow ? Title(popup) : string.Empty;
        return "Revit is showing a modal dialog" + (title.Length > 0 ? " \"" + title + "\"" : string.Empty)
            + "; close it, then retry";
    }

    private static string Title(IntPtr hwnd)
    {
        char[] buffer = new char[256];
        int n = InternalGetWindowText(hwnd, buffer, buffer.Length);
        return n <= 0 ? string.Empty : Sanitizer.Sanitize(new string(buffer, 0, Math.Min(n, buffer.Length)).Trim(), 80);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowEnabled(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetLastActivePopup(IntPtr hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int InternalGetWindowText(IntPtr hwnd, [Out] char[] text, int maxCount);
}

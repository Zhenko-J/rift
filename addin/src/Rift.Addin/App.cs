// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Globalization;
using System.IO;
using System.Reflection;

using Autodesk.Revit.UI;

using Rift.Core;

namespace Rift.Addin;

/// <summary>
/// The only class Revit instantiates (rift.addin). It never throws towards Revit and never references
/// anything but the Revit API: it loads Rift.Revit into an isolated context and relays Start/Stop.
/// </summary>
public sealed class App : IExternalApplication
{
    internal static object? Host { get; private set; }

    private static MethodInfo? s_stop;
    private static MethodInfo? s_invoke;
    private static MethodInfo? s_status;
    private static MethodInfo? s_probePython;
    private static MethodInfo? s_setActivityCard;
    private static PropertyInfo? s_activityCardEnabled;
    private static PropertyInfo? s_logPath;

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            string dir = Path.GetDirectoryName(typeof(App).Assembly.Location) ?? throw new InvalidOperationException("assembly location unknown");
            string revitDll = Path.Combine(dir, "Rift.Revit.dll");
            var context = new RiftLoadContext(revitDll);
            Assembly revitAssembly = context.LoadFromAssemblyPath(revitDll);
            Type hostType = revitAssembly.GetType("Rift.Revit.Host", throwOnError: true)!;
            Host = Activator.CreateInstance(hostType) ?? throw new InvalidOperationException("Host could not be created");
            s_stop = hostType.GetMethod("Stop");
            s_invoke = hostType.GetMethod("Invoke");
            s_status = hostType.GetMethod("Status");
            s_probePython = hostType.GetMethod("ProbePython");
            s_setActivityCard = hostType.GetMethod("SetActivityCard");
            s_activityCardEnabled = hostType.GetProperty("ActivityCardEnabled");
            s_logPath = hostType.GetProperty("LogPath");
            // The ribbon is built by the host (ADR-029 §1); it only needs this assembly's path for the commands.
            hostType.GetMethod("Start")!.Invoke(Host, new object[] { application, Path.Combine(dir, "Rift.Addin.dll") });
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            BootstrapLog.Write("OnStartup failed: " + ex);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        try
        {
            s_stop?.Invoke(Host, null);
        }
        catch (Exception ex)
        {
            BootstrapLog.Write("OnShutdown failed: " + ex);
        }
        return Result.Succeeded;
    }

    /// <summary>Relays a ribbon command to the host (UI gate 3): an error text to show, or null. Never throws.</summary>
    internal static string? Invoke(string command, object? arg)
    {
        try
        {
            if (s_invoke is null)
            {
                return Brand.Product + ": host not loaded";
            }
            return s_invoke.Invoke(Host, new[] { command, arg }) as string;
        }
        catch (Exception ex)
        {
            BootstrapLog.Write("Invoke " + command + " failed: " + ex);
            return Brand.Product + ": " + command + " failed: " + ex.Message;
        }
    }

    internal static string StatusText()
    {
        try
        {
            return s_status?.Invoke(Host, null) as string ?? Brand.Product + ": host not loaded";
        }
        catch (Exception ex)
        {
            return Brand.Product + ": status failed: " + ex.Message;
        }
    }

    internal static bool ActivityCardEnabled()
    {
        try
        {
            return s_activityCardEnabled?.GetValue(Host) is true;
        }
        catch (Exception ex)
        {
            BootstrapLog.Write("ActivityCardEnabled failed: " + ex.Message);
            return false;
        }
    }

    internal static void SetActivityCard(bool enabled)
    {
        try
        {
            s_setActivityCard?.Invoke(Host, new object[] { enabled });
        }
        catch (Exception ex)
        {
            BootstrapLog.Write("SetActivityCard failed: " + ex.Message);
        }
    }

    internal static string? LogPath()
    {
        try
        {
            return s_logPath?.GetValue(Host) as string;
        }
        catch (Exception ex)
        {
            BootstrapLog.Write("LogPath failed: " + ex.Message);
            return null;
        }
    }

    internal static string ProbePythonText(UIApplication uiapp)
    {
        try
        {
            return s_probePython?.Invoke(Host, new object[] { uiapp }) as string ?? Brand.Product + ": host not loaded";
        }
        catch (Exception ex)
        {
            return Brand.Product + ": probe failed: " + ex;
        }
    }
}

internal static class BootstrapLog
{
    public static void Write(string line)
    {
        try
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.Id, "logs");
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "bootstrap-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".log"),
                DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

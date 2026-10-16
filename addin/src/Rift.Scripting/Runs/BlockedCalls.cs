// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text.RegularExpressions;

namespace Rift.Scripting.Runs;

/// <summary>
/// ADR-006: under a transaction group, <c>Document.Save</c>, <c>SaveAs</c>, <c>Close</c>, <c>SynchronizeWithCentral</c> and
/// <c>SaveCloudModel</c> throw before any pre-event is raised, so the guard cannot see them. The runner names them from the
/// exception that reached the script: the Revit frame at the top of the .NET stack trace. A script that catches the
/// exception hides it (documented limit); exports and documents the script opened itself are caught by the pre-events.
/// </summary>
public static partial class BlockedCalls
{
    /// <summary>The blocked document operation behind <paramref name="error"/> (save, save_as, close, sync, export), or null.</summary>
    public static string? Classify(Exception? error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            string? name = ClassifyText(e.StackTrace) ?? ClassifyText(e.ToString());
            if (name is not null)
            {
                return name;
            }
        }
        return null;
    }

    /// <summary>Same decision on the text of a stack trace (pure, tested without Revit).</summary>
    public static string? ClassifyText(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
        {
            return null;
        }
        Match m = DocumentCall().Match(stackTrace);
        if (!m.Success)
        {
            return null;
        }
        return m.Groups["op"].Value switch
        {
            "SaveAs" => "save_as",
            "Save" or "SaveCloudModel" => "save",
            "Close" => "close",
            "SynchronizeWithCentral" => "sync",
            _ => "export",
        };
    }

    [GeneratedRegex(@"Autodesk\.Revit\.DB\.Document\.(?<op>SaveAs|SaveCloudModel|Save|Close|SynchronizeWithCentral|Export)\(")]
    private static partial Regex DocumentCall();
}

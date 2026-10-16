// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Rift.Core;

namespace Rift.Addin;

/// <summary>
/// Base of the ribbon commands that only relay to the host (UI gate 3): <see cref="Command"/> goes to
/// <see cref="App.Invoke"/>; a TaskDialog appears only when the host returns an error text. Never throws towards Revit.
/// </summary>
public abstract class RelayCommand : IExternalCommand
{
    protected abstract string Command { get; }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            string? error = App.Invoke(Command, commandData?.Application);
            if (error is not null)
            {
                TaskDialog.Show(Brand.Product, error);
            }
        }
        catch (Exception ex)
        {
            BootstrapLog.Write(Command + " failed: " + ex);
        }
        return Result.Succeeded;
    }
}

/// <summary>The server toggle (ADR-029 §2): pauses the assistant, or lets it through again.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ServerToggleCommand : RelayCommand
{
    protected override string Command => "server.toggle";
}

/// <summary>The Status window (UI gate 3): server state, client, pipe, engine, pause, activity card switch, log path.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class StatusCommand : RelayCommand
{
    protected override string Command => "status.open";
}

/// <summary>The History window (UI gate 3): every call of the session with its result and change receipt.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class HistoryCommand : RelayCommand
{
    protected override string Command => "history.open";
}

/// <summary>The Tools window (UI gate 3): the tool catalogue with its waves and risks.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ToolsCommand : RelayCommand
{
    protected override string Command => "tools.open";
}

/// <summary>Keeps the server and status buttons usable with no document open (Revit greys out commands otherwise).</summary>
public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

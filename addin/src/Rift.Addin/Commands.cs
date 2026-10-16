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

/// <summary>Keeps the server and status buttons usable with no document open (Revit greys out commands otherwise).</summary>
public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}

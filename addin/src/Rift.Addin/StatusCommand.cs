// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Rift.Core;

namespace Rift.Addin;

/// <summary>
/// The Status button (ADR-023, ADR-029 point 5): a TaskDialog with the host's report and two command links — switch the
/// activity card on or off (persisted in <c>config.json</c> by the host) and copy the log path to the clipboard. No
/// process is launched from here.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class StatusCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        bool cardOn = App.ActivityCardEnabled();
        var dialog = new TaskDialog(Brand.Product)
        {
            MainInstruction = Brand.Product + " add-in",
            MainContent = App.StatusText(),
            CommonButtons = TaskDialogCommonButtons.Close,
            DefaultButton = TaskDialogResult.Close,
        };
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
            cardOn ? "Turn the activity card off" : "Turn the activity card on",
            "Remembered in config.json (activity_card)");
        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Copy the log path", App.LogPath() ?? "-");

        TaskDialogResult result = dialog.Show();
        if (result == TaskDialogResult.CommandLink1)
        {
            App.SetActivityCard(!cardOn);
        }
        else if (result == TaskDialogResult.CommandLink2)
        {
            string? path = App.LogPath();
            if (path is not null)
            {
                try
                {
                    System.Windows.Clipboard.SetText(path);
                }
                catch (Exception)
                {
                    // The clipboard can be held by another process: the path is still visible in the dialog.
                }
            }
        }
        return Result.Succeeded;
    }
}

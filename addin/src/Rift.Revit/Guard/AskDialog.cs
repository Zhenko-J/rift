// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Rift.Core;
using Rift.Core.Logging;
using Rift.Scripting.Runs;

namespace Rift.Revit.Guard;

/// <summary>
/// The Keep/Discard dialog of policy <c>ask</c> (ADR-009), shown on the API thread while the run's group is still open so
/// that the model shows the change: the touched elements are selected as a preview, a <see cref="TaskDialog"/> with id
/// <c>rift.ask</c> (allow-listed by the write guard) offers Keep and Discard with <b>Discard as default</b>, then the
/// selection is cleared. Never throws towards the run: any failure is a Discard.
/// </summary>
internal sealed class AskDialog : IAskDialog
{
    private readonly UIDocument? _uidoc;
    private readonly FileLog _log;

    public AskDialog(UIDocument? uidoc, FileLog log)
    {
        _uidoc = uidoc;
        _log = log;
    }

    public AskResult Ask(string title, ChangeReceipt changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ICollection<ElementId>? previous = null;
        try
        {
            previous = Preview(changes);
            var dialog = new TaskDialog(Brand.Product + " — keep this change?")
            {
                Id = WriteGuard.AskDialogId,
                MainInstruction = title,
                MainContent = "The assistant changed the model: "
                    + changes.Added.ToString(CultureInfo.InvariantCulture) + " added, "
                    + changes.Modified.ToString(CultureInfo.InvariantCulture) + " modified, "
                    + changes.Deleted.ToString(CultureInfo.InvariantCulture) + " deleted"
                    + (changes.ByCategory.Count > 0 ? " (" + string.Join(", ", changes.ByCategory.Keys.Take(6)) + (changes.ByCategory.Count > 6 ? ", …" : string.Empty) + ")" : string.Empty)
                    + ". The touched elements are selected. Keep makes one undo entry named \"Agent: " + title + "\"; Discard rolls everything back.",
                CommonButtons = TaskDialogCommonButtons.None,
                AllowCancellation = true,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Keep", "One undo entry; nothing is saved to disk.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Discard", "Roll everything back (default).");
            // The default must name a button that exists: set before the command links, Revit throws "Corresponding
            // button not found" and the dialog never shows (acceptance, 2026-10-16).
            dialog.DefaultButton = TaskDialogResult.CommandLink2;
            TaskDialogResult result = dialog.Show();
            return result == TaskDialogResult.CommandLink1 ? AskResult.Keep : AskResult.Discard;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or ArgumentException)
        {
            _log.Write("ask dialog failed, rolling back: " + ex.GetType().Name + ": " + ex.Message);
            return AskResult.Failed;
        }
        finally
        {
            Restore(previous);
        }
    }

    private ICollection<ElementId>? Preview(ChangeReceipt changes)
    {
        if (_uidoc is null)
        {
            return null;
        }
        try
        {
            ICollection<ElementId> previous = _uidoc.Selection.GetElementIds();
            var ids = new List<ElementId>();
            foreach (ChangeSample s in changes.Samples.Where(s => s.Kind != "deleted"))
            {
                ids.Add(new ElementId(s.Id));
            }
            if (ids.Count > 0)
            {
                _uidoc.Selection.SetElementIds(ids);
            }
            return previous;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            _log.Write("ask preview failed: " + ex.Message);
            return null;
        }
    }

    private void Restore(ICollection<ElementId>? previous)
    {
        if (_uidoc is null)
        {
            return;
        }
        try
        {
            _uidoc.Selection.SetElementIds(previous ?? new List<ElementId>());
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            _log.Write("ask selection restore failed: " + ex.Message);
        }
    }
}

// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Rift.Scripting;

/// <summary>
/// ADR-036: the Revit members and .NET types an assistant script may not name, in either language. They either leave code
/// that runs after the run and its guard have ended (events, posted commands, updaters, external events, dockable panes:
/// F-R1), or open, create or copy documents behind the guard's back (F-R14). C# refuses them on the bound symbol
/// (<c>CSharpPolicy</c>); Python on the syntax tree and again at run time through the guarded <c>getattr</c>
/// (<c>PythonPolicy</c>, <c>ImportGuard</c>). The event names here are the Revit 2026 API's; the add-in adds the ones it
/// finds by reflection on the loaded API (<see cref="WithEvents"/>), so a newer Revit cannot slip one through.
/// </summary>
public static class ScriptPolicy
{
    /// <summary>Members that schedule deferred code or act on documents outside the run.</summary>
    public static readonly IReadOnlySet<string> DeferredMembers = new HashSet<string>(StringComparer.Ordinal)
    {
        "PostCommand",
        "RegisterUpdater",
        "RegisterDockablePane",
        "OpenDocumentFile",
        "OpenAndActivateDocument",
        "OpenCloudDocument",
        "OpenBuildingComponentDocument",
        "CopyModel",
        "NewProjectDocument",
        "NewProjectTemplateDocument",
        "NewFamilyDocument",
        "NewTitleBlockDocument",
        "ExternalEvent",
        "IExternalEventHandler",
        "UpdaterRegistry",
        "IUpdater",
    };

    /// <summary>Events of <c>UIApplication</c>, <c>Application</c>, their controlled forms and <c>Document</c> (Revit 2026).</summary>
    public static readonly IReadOnlySet<string> RevitEvents = new HashSet<string>(StringComparer.Ordinal)
    {
        "ApplicationClosing", "ApplicationInitialized", "DialogBoxShowing", "DisplayingOptionsDialog",
        "DockableFrameFocusChanged", "DockableFrameVisibilityChanged", "DocumentChanged", "DocumentClosed", "DocumentClosing",
        "DocumentCreated", "DocumentCreating", "DocumentOpened", "DocumentOpening", "DocumentPrinted", "DocumentPrinting",
        "DocumentReloadedLatest", "DocumentReloadingLatest", "DocumentSaved", "DocumentSavedAs", "DocumentSaving",
        "DocumentSavingAs", "DocumentSynchronizedWithCentral", "DocumentSynchronizingWithCentral", "DocumentWorksharingEnabled",
        "ElementTypeDuplicated", "ElementTypeDuplicating", "FabricationPartBrowserChanged", "FailuresProcessing",
        "FamilyLoadedIntoDocument", "FamilyLoadingIntoDocument", "FileExported", "FileExporting", "FileImported", "FileImporting",
        "FormulaEditing", "Idling", "LinkedResourceOpened", "LinkedResourceOpening", "ProgressChanged", "SelectionChanged",
        "ThemeChanged", "TransferredProjectStandards", "TransferringProjectStandards", "ViewActivated", "ViewActivating",
        "ViewExported", "ViewExporting", "ViewPrinted", "ViewPrinting", "WorksharedOperationProgressChanged",
    };

    /// <summary>Every name refused in an assistant script: <see cref="DeferredMembers"/> and <see cref="RevitEvents"/>.</summary>
    public static IReadOnlySet<string> Default { get; } = WithEvents(Array.Empty<string>());

    /// <summary><see cref="Default"/> plus event names found on the loaded Revit API.</summary>
    public static IReadOnlySet<string> WithEvents(IEnumerable<string> extraEvents)
    {
        ArgumentNullException.ThrowIfNull(extraEvents);
        return new HashSet<string>(DeferredMembers.Concat(RevitEvents).Concat(extraEvents), StringComparer.Ordinal);
    }

    /// <summary>Why <paramref name="name"/> is refused, or null (the same sentence in both languages).</summary>
    public static string? Reason(string name, IReadOnlySet<string> refused)
    {
        ArgumentNullException.ThrowIfNull(refused);
        if (!refused.Contains(name))
        {
            return null;
        }
        if (RevitEvents.Contains(name) || !DeferredMembers.Contains(name))
        {
            return "Revit events are not available to scripts: the handler would run after the run and its guard";
        }
        return name.StartsWith("Open", StringComparison.Ordinal) || name.StartsWith("New", StringComparison.Ordinal) || name == "CopyModel"
            ? "scripts may not open, create or copy documents (use open_document)"
            : "deferred code is not available to scripts: it would run after the run and its guard";
    }
}

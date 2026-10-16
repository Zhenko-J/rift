// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;

using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

using Rift.Core;
using Rift.Core.Logging;
using Rift.Scripting.Runs;

namespace Rift.Revit.Guard;

/// <summary>
/// The read guard of <c>mode: query</c> (ADR-006/007/010). <see cref="Arm"/> opens a <see cref="TransactionGroup"/> on every
/// open, non-linked document (the primary barrier: under a group Revit refuses save, save-as, close and synchronisation) and
/// subscribes, for the duration of the run only, <c>DocumentChanged</c> (a committed transaction is a write), the cancellable
/// pre-events (save, save-as, close, synchronise, export — cancelled and named) and <c>FailuresProcessing</c> (a commit with
/// failures is rolled back without a dialog). <see cref="Disarm"/> unsubscribes <b>first</b> (a group rollback raises
/// <c>DocumentChanged</c> too), rolls back what the script left open (sub-transaction, transaction, group, found among the
/// script's top-level values), then rolls back every group, unconditionally. A document that refuses a group is watched only
/// (<c>guard: watch</c>). Nothing reaches the undo stack.
/// </summary>
internal sealed class ReadGuard : IRunGuard
{
    private const string GroupName = Brand.Product + " query guard";

    private readonly UIApplication _uiapp;
    private readonly FileLog _log;
    private readonly List<(Document Doc, TransactionGroup Group)> _groups = new();
    private readonly List<string> _blocked = new();
    private readonly List<string> _notes = new();
    private readonly List<string> _opened = new();
    private Application? _app;
    private bool _write;
    private bool _watch;
    private int _transactions;

    public ReadGuard(UIApplication uiapp, FileLog log)
    {
        _uiapp = uiapp;
        _log = log;
    }

    public void Arm()
    {
        _app = _uiapp.Application;
        _app.DocumentChanged += OnDocumentChanged;
        _app.DocumentSaving += OnSaving;
        _app.DocumentSavingAs += OnSavingAs;
        _app.DocumentClosing += OnClosing;
        _app.DocumentSynchronizingWithCentral += OnSynchronizing;
        _app.FileExporting += OnFileExporting;
        _app.ViewExporting += OnViewExporting;
        _app.FailuresProcessing += OnFailuresProcessing;
        _app.DocumentOpened += OnDocumentOpened;
        _app.DocumentCreated += OnDocumentCreated;
        foreach (Document doc in _app.Documents)
        {
            if (doc.IsLinked)
            {
                continue;
            }
            try
            {
                if (doc.IsReadOnly || doc.IsModifiable)
                {
                    // Read-only, or a transaction already open (should not happen on the API thread at rest): watch only.
                    _watch = true;
                    _notes.Add(doc.IsReadOnly ? "watch:read_only_document" : "watch:document_already_modifiable");
                    continue;
                }
                var group = new TransactionGroup(doc, GroupName);
                if (group.Start() == TransactionStatus.Started)
                {
                    _groups.Add((doc, group));
                }
                else
                {
                    group.Dispose();
                    _watch = true;
                    _notes.Add("watch:group_refused");
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                _watch = true;
                _notes.Add("watch:group_refused");
                _log.Write("read guard: group refused on a document: " + ex.GetType().Name);
            }
        }
    }

    public GuardReport Disarm(IReadOnlyList<object> leftovers)
    {
        Unsubscribe();
        RollBackLeftovers(leftovers);
        var rolledBack = new List<string>();
        for (int i = _groups.Count - 1; i >= 0; i--)
        {
            (Document doc, TransactionGroup group) = _groups[i];
            try
            {
                if (doc.IsValidObject && doc.IsModifiable)
                {
                    // A transaction is still open in this document and was not found among the script's values.
                    _write = true;
                    _notes.Add("open_transaction_left");
                }
                if (group.IsValidObject && group.HasStarted() && !group.HasEnded())
                {
                    group.RollBack();
                    rolledBack.Add(doc.IsValidObject ? "document" : "closed_document");
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                _notes.Add("group_rollback_failed");
                _log.Write("read guard: group rollback failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                group.Dispose();
            }
        }
        _groups.Clear();
        return new GuardReport(_write || _opened.Count > 0, _transactions, _blocked.ToArray(), rolledBack, _watch ? "watch" : "group", _notes.ToArray())
        {
            DocumentsOpened = _opened.ToArray(),
        };
    }

    private void RollBackLeftovers(IReadOnlyList<object> leftovers)
    {
        // Innermost first: sub-transactions, then transactions, then groups the script opened itself (ADR-007).
        foreach (object o in leftovers)
        {
            if (o is SubTransaction st && Try(() => st.IsValidObject && st.HasStarted() && !st.HasEnded()))
            {
                _write = true;
                _notes.Add("sub_transaction_left_open");
                Try(() => st.RollBack() == TransactionStatus.RolledBack);
            }
        }
        foreach (object o in leftovers)
        {
            if (o is Transaction t && Try(() => t.IsValidObject && t.HasStarted() && !t.HasEnded()))
            {
                _write = true;
                _notes.Add("transaction_left_open");
                Try(() => t.RollBack() == TransactionStatus.RolledBack);
            }
        }
        foreach (object o in leftovers)
        {
            if (o is TransactionGroup g && Try(() => g.IsValidObject && g.HasStarted() && !g.HasEnded()))
            {
                _notes.Add("group_left_open");
                Try(() => g.RollBack() == TransactionStatus.RolledBack);
            }
        }
    }

    private bool Try(Func<bool> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            _log.Write("read guard: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private void Unsubscribe()
    {
        if (_app is null)
        {
            return;
        }
        _app.DocumentChanged -= OnDocumentChanged;
        _app.DocumentSaving -= OnSaving;
        _app.DocumentSavingAs -= OnSavingAs;
        _app.DocumentClosing -= OnClosing;
        _app.DocumentSynchronizingWithCentral -= OnSynchronizing;
        _app.FileExporting -= OnFileExporting;
        _app.ViewExporting -= OnViewExporting;
        _app.FailuresProcessing -= OnFailuresProcessing;
        _app.DocumentOpened -= OnDocumentOpened;
        _app.DocumentCreated -= OnDocumentCreated;
        _app = null;
    }

    private void OnDocumentChanged(object? sender, DocumentChangedEventArgs e)
    {
        // Conservative: anything but a rollback (committed, undone, redone) changed the model during the run.
        if (e.Operation is not (UndoOperation.TransactionRolledBack or UndoOperation.TransactionGroupRolledBack))
        {
            _write = true;
            _transactions++;
        }
    }

    private void Block(RevitAPIPreEventArgs e, string what)
    {
        _write = true;
        if (!_blocked.Contains(what))
        {
            _blocked.Add(what);
        }
        if (e.Cancellable)
        {
            e.Cancel();
        }
    }

    private void OnSaving(object? sender, DocumentSavingEventArgs e) => Block(e, "save");

    private void OnSavingAs(object? sender, DocumentSavingAsEventArgs e) => Block(e, "save_as");

    private void OnClosing(object? sender, DocumentClosingEventArgs e) => Block(e, "close");

    private void OnSynchronizing(object? sender, DocumentSynchronizingWithCentralEventArgs e) => Block(e, "sync");

    private void OnFileExporting(object? sender, FileExportingEventArgs e) => Block(e, "export");

    private void OnViewExporting(object? sender, ViewExportingEventArgs e) => Block(e, "export");

    private void OnDocumentOpened(object? sender, DocumentOpenedEventArgs e) => NoteOpened(e.Document);

    private void OnDocumentCreated(object? sender, DocumentCreatedEventArgs e) => NoteOpened(e.Document);

    private void NoteOpened(Document? doc)
    {
        // F-R14: opening or creating a document is a change the rollback cannot undo; it is reported, never closed here.
        string title = doc is null ? "?" : doc.Title;
        if (!_opened.Contains(title))
        {
            _opened.Add(title);
        }
        if (!_blocked.Contains("open_document"))
        {
            _blocked.Add("open_document");
        }
    }

    private void OnFailuresProcessing(object? sender, FailuresProcessingEventArgs e)
    {
        // ADR-010: a commit that raises failures is a write attempt; warnings are dropped, errors roll back silently
        // (no dialog in the user's face during an agent's query).
        FailuresAccessor fa = e.GetFailuresAccessor();
        if (!fa.IsTransactionBeingCommitted())
        {
            return;
        }
        _write = true;
        fa.DeleteAllWarnings();
        if (fa.GetFailureMessages().Count > 0)
        {
            FailureHandlingOptions options = fa.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            fa.SetFailureHandlingOptions(options);
            e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
            _notes.Add("failure_rolled_back");
        }
    }
}

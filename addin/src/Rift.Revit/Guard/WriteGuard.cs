// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

using Rift.Core;
using Rift.Core.Logging;
using Rift.Scripting.Runs;

namespace Rift.Revit.Guard;

/// <summary>
/// The write guard of <c>mode: modify</c> (M2, ADR-006/007/009/010). <see cref="Arm"/> opens a <see cref="TransactionGroup"/>
/// <b>named <c>Agent: &lt;title&gt;</c></b> on the target document and read-style groups on every other open, non-linked
/// document, and subscribes for the duration of the run: <c>DocumentChanged</c> (element ids by kind on the target, by
/// category; a change elsewhere is noted as another document written), the cancellable pre-events (save, save-as, close,
/// synchronise, export — cancelled and named), <c>FailuresProcessing</c> (ADR-010: warnings deleted, errors rolled back
/// without a dialog and the whole run marked for rollback) and <c>DialogBoxShowing</c> (ADR-009: a dialog raised by the
/// script is overridden with Cancel and journaled; the Keep/Discard dialog <c>rift.ask</c> is ours and passes).
/// <see cref="Disarm"/> unsubscribes <b>first</b>, rolls back what the script left open, then assimilates the target group
/// (one undo entry) when asked to keep and nothing was left open, or rolls it back; the other groups are always rolled back.
/// </summary>
internal sealed class WriteGuard : IWriteGuard, IDisposable
{
    public const string AskDialogId = "rift.ask";
    private const string ReadGroupName = Brand.Product + " modify guard (other document)";

    private readonly UIApplication _uiapp;
    private readonly Document _target;
    private readonly FileLog _log;
    private readonly List<(Document Doc, TransactionGroup Group)> _others = new();
    private readonly List<string> _blocked = new();
    private readonly List<string> _notes = new();
    private readonly List<string> _dialogs = new();
    private readonly Dictionary<string, int> _otherWritten = new(StringComparer.Ordinal);
    private readonly List<string> _opened = new();
    private readonly Dictionary<long, (string Category, string? Name)> _added = new();
    private readonly Dictionary<long, (string Category, string? Name)> _modified = new();
    private readonly HashSet<long> _deleted = new();
    private Application? _app;
    private TransactionGroup? _group;
    private bool _watch;
    private int _transactions;

    public WriteGuard(UIApplication uiapp, Document target, FileLog log)
    {
        _uiapp = uiapp;
        _target = target;
        _log = log;
    }

    public bool FailureRolledBack { get; private set; }

    /// <summary>Releases a group that <see cref="Disarm"/> never saw (an Arm that threw half-way); Disarm already disposes it.</summary>
    public void Dispose()
    {
        _group?.Dispose();
        _group = null;
    }

    public IReadOnlyList<string> Dialogs => _dialogs;

    public IReadOnlyList<OtherDocumentWrite> OtherDocuments => _otherWritten.Select(kv => new OtherDocumentWrite(kv.Key, kv.Value)).ToArray();

    public IReadOnlyList<string> DocumentsOpened => _opened;

    public ChangeReceipt Changes => ChangeReceipt.From(
        _added.Select(kv => (kv.Key, kv.Value.Category, kv.Value.Name)),
        _modified.Select(kv => (kv.Key, kv.Value.Category, kv.Value.Name)),
        _deleted,
        _transactions);

    public void Arm(string title)
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
        _uiapp.DialogBoxShowing += OnDialogBoxShowing;

        if (_target.IsReadOnly || _target.IsModifiable)
        {
            throw new InvalidOperationException(_target.IsReadOnly ? "the target document is read-only" : "a transaction is already open on the target document");
        }
        _group = new TransactionGroup(_target, "Agent: " + title);
        if (_group.Start() != TransactionStatus.Started)
        {
            _group.Dispose();
            _group = null;
            throw new InvalidOperationException("the target document refused a transaction group");
        }
        foreach (Document doc in _app.Documents)
        {
            if (doc.IsLinked || doc.Equals(_target))
            {
                continue;
            }
            try
            {
                if (doc.IsReadOnly || doc.IsModifiable)
                {
                    _watch = true;
                    _notes.Add("watch:other_document");
                    continue;
                }
                var group = new TransactionGroup(doc, ReadGroupName);
                if (group.Start() == TransactionStatus.Started)
                {
                    _others.Add((doc, group));
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
                _log.Write("write guard: group refused on another document: " + ex.GetType().Name);
            }
        }
    }

    public GuardReport Disarm(IReadOnlyList<object> leftovers, bool keep)
    {
        Unsubscribe();
        bool leftOpen = RollBackLeftovers(leftovers);
        var rolledBack = new List<string>();
        bool kept = false;
        try
        {
            if (_target.IsValidObject && _target.IsModifiable)
            {
                leftOpen = true;
                _notes.Add("open_transaction_left");
            }
            if (_group is not null && _group.IsValidObject && _group.HasStarted() && !_group.HasEnded())
            {
                if (keep && !leftOpen)
                {
                    // One undo entry named after the run (ADR-009): the script's transactions are merged into the group.
                    kept = _group.Assimilate() == TransactionStatus.Committed;
                    if (!kept)
                    {
                        _notes.Add("assimilate_failed");
                        if (_group.HasStarted() && !_group.HasEnded())
                        {
                            _group.RollBack();
                        }
                        rolledBack.Add("target");
                    }
                }
                else
                {
                    _group.RollBack();
                    rolledBack.Add("target");
                }
            }
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            kept = false;
            _notes.Add("target_group_failed");
            _log.Write("write guard: target group " + (keep ? "assimilate" : "rollback") + " failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _group?.Dispose();
            _group = null;
        }
        for (int i = _others.Count - 1; i >= 0; i--)
        {
            (Document doc, TransactionGroup group) = _others[i];
            try
            {
                if (group.IsValidObject && group.HasStarted() && !group.HasEnded())
                {
                    group.RollBack();
                    rolledBack.Add(doc.IsValidObject ? "other_document" : "closed_document");
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                _notes.Add("group_rollback_failed");
                _log.Write("write guard: other group rollback failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                group.Dispose();
            }
        }
        _others.Clear();
        bool changed = _added.Count + _modified.Count + _deleted.Count > 0 || _transactions > 0;
        return new GuardReport(changed, _transactions, _blocked.ToArray(), rolledBack, _watch ? "watch" : "group", _notes.ToArray())
        {
            Kept = kept,
            OtherDocumentsWritten = OtherDocuments,
            DocumentsOpened = _opened.ToArray(),
        };
    }

    private bool RollBackLeftovers(IReadOnlyList<object> leftovers)
    {
        bool any = false;
        foreach (object o in leftovers)
        {
            if (o is SubTransaction st && Try(() => st.IsValidObject && st.HasStarted() && !st.HasEnded()))
            {
                any = true;
                _notes.Add("sub_transaction_left_open");
                Try(() => st.RollBack() == TransactionStatus.RolledBack);
            }
        }
        foreach (object o in leftovers)
        {
            if (o is Transaction t && Try(() => t.IsValidObject && t.HasStarted() && !t.HasEnded()))
            {
                any = true;
                _notes.Add("transaction_left_open");
                Try(() => t.RollBack() == TransactionStatus.RolledBack);
            }
        }
        foreach (object o in leftovers)
        {
            if (o is TransactionGroup g && !ReferenceEquals(g, _group) && Try(() => g.IsValidObject && g.HasStarted() && !g.HasEnded()))
            {
                any = true;
                _notes.Add("group_left_open");
                Try(() => g.RollBack() == TransactionStatus.RolledBack);
            }
        }
        return any;
    }

    private bool Try(Func<bool> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            _log.Write("write guard: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private void Unsubscribe()
    {
        try
        {
            _uiapp.DialogBoxShowing -= OnDialogBoxShowing;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            _log.Write("write guard: dialog unsubscribe failed: " + ex.Message);
        }
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
        if (e.Operation is UndoOperation.TransactionRolledBack or UndoOperation.TransactionGroupRolledBack)
        {
            return;
        }
        Document? doc;
        try
        {
            doc = e.GetDocument();
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            doc = null;
        }
        if (doc is null || !doc.Equals(_target))
        {
            string title = doc is null ? "?" : doc.Title;
            _otherWritten[title] = _otherWritten.TryGetValue(title, out int n) ? n + 1 : 1;
            return;
        }
        _transactions++;
        try
        {
            foreach (ElementId id in e.GetAddedElementIds())
            {
                long v = Value(id);
                _added[v] = Describe(doc, id);
            }
            foreach (ElementId id in e.GetModifiedElementIds())
            {
                long v = Value(id);
                if (!_added.ContainsKey(v))
                {
                    _modified[v] = Describe(doc, id);
                }
            }
            foreach (ElementId id in e.GetDeletedElementIds())
            {
                long v = Value(id);
                // An element the script created and deleted in the same run never existed for the user.
                if (_added.Remove(v))
                {
                    continue;
                }
                _modified.Remove(v);
                _deleted.Add(v);
            }
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            _notes.Add("receipt_incomplete");
            _log.Write("write guard: receipt collection failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static (string Category, string? Name) Describe(Document doc, ElementId id)
    {
        try
        {
            Element? el = doc.GetElement(id);
            if (el is null)
            {
                return ("(unknown)", null);
            }
            string category = el.Category?.Name ?? el.GetType().Name;
            string? name;
            try
            {
                name = el.Name;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                name = null;
            }
            return (category, string.IsNullOrEmpty(name) ? null : name);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return ("(unknown)", null);
        }
    }

    private static long Value(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value;
#else
        return id.IntegerValue;
#endif
    }

    private void Block(RevitAPIPreEventArgs e, string what)
    {
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
        // F-R14: a document opened or created by the script has no group; it is reported, never closed by the guard.
        string title = doc is null ? "?" : doc.Title;
        if (!_opened.Contains(title))
        {
            _opened.Add(title);
        }
    }

    private void OnFailuresProcessing(object? sender, FailuresProcessingEventArgs e)
    {
        // ADR-010: warnings are dropped; errors roll the transaction back silently and condemn the whole run.
        FailuresAccessor fa = e.GetFailuresAccessor();
        if (!fa.IsTransactionBeingCommitted())
        {
            return;
        }
        fa.DeleteAllWarnings();
        if (fa.GetFailureMessages().Count > 0)
        {
            FailureHandlingOptions options = fa.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            fa.SetFailureHandlingOptions(options);
            e.SetProcessingResult(FailureProcessingResult.ProceedWithRollBack);
            FailureRolledBack = true;
            _notes.Add("failure_rolled_back");
        }
    }

    private void OnDialogBoxShowing(object? sender, DialogBoxShowingEventArgs e)
    {
        // ADR-009: a dialog the script raises would block the API thread under the agent's run; it is cancelled and journaled.
        // Our own Keep/Discard dialog (shown after the script, before Disarm) carries the id rift.ask and passes.
        try
        {
            if (e is TaskDialogShowingEventArgs t && string.Equals(t.DialogId, AskDialogId, StringComparison.Ordinal))
            {
                return;
            }
            string id = e is TaskDialogShowingEventArgs td ? td.DialogId : e is MessageBoxShowingEventArgs ? "messagebox" : e.GetType().Name;
            _dialogs.Add(Rift.Core.Json.Sanitizer.Sanitize(id, 120) + ":cancel");
            if (!e.OverrideResult(2))
            {
                _notes.Add("dialog_override_refused");
            }
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
        {
            _notes.Add("dialog_override_failed");
            _log.Write("write guard: dialog override failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}

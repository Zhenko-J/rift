// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;

namespace Rift.Scripting.Runs;

/// <summary>
/// The read guard of a run (ADR-006/007): armed just before the script, disarmed just after, whatever happened. In
/// <c>mode: query</c> disarming always rolls back. Implemented on the Revit side with transaction groups and document
/// events; faked in tests.
/// </summary>
public interface IRunGuard
{
    /// <summary>Opens the groups and subscribes the events. Throws when the guard cannot be armed: the script is then not run.</summary>
    void Arm();

    /// <summary>
    /// Unsubscribes first, then rolls back what the script left open (found among <paramref name="leftovers"/>) and every
    /// group. Never throws.
    /// </summary>
    GuardReport Disarm(IReadOnlyList<object> leftovers);
}

/// <summary>
/// The write guard of <c>mode: modify</c> (M2, ADR-006/007/009/010): a transaction group <b>named after the run</b> on the
/// target document (one undo entry when kept), read-style groups on every other open document (always rolled back), the
/// same blocked operations as the read guard, the change receipt collected from <c>DocumentChanged</c>, failures handled
/// silently (ADR-010) and script dialogs overridden (ADR-009). <see cref="Disarm"/> decides: keep (assimilate) or discard.
/// </summary>
public interface IWriteGuard
{
    /// <summary>Opens the named group on the target and the read groups elsewhere; subscribes the events.</summary>
    void Arm(string title);

    /// <summary>What the script changed on the target so far (read after the script, before any decision).</summary>
    ChangeReceipt Changes { get; }

    /// <summary>True when a commit raised failures the guard rolled back (ADR-010): the whole run must be discarded.</summary>
    bool FailureRolledBack { get; }

    /// <summary>Dialogs the script raised and the guard overrode (ADR-009), for the run result.</summary>
    IReadOnlyList<string> Dialogs { get; }

    /// <summary>
    /// Other open documents the script committed transactions into, so far (read after the script, before any decision).
    /// Their groups are always rolled back; a run that wrote into another document is never kept (F-R15).
    /// </summary>
    IReadOnlyList<OtherDocumentWrite> OtherDocuments => Array.Empty<OtherDocumentWrite>();

    /// <summary>Documents opened or created while the script ran (F-R14): a rollback cannot close them.</summary>
    IReadOnlyList<string> DocumentsOpened => Array.Empty<string>();

    /// <summary>
    /// Unsubscribes first, rolls back what the script left open, then assimilates the target group when
    /// <paramref name="keep"/> is true (and nothing was left open) or rolls it back; the other documents' groups are always
    /// rolled back. Never throws. <see cref="GuardReport.WriteDetected"/> then means "something changed on the target".
    /// </summary>
    GuardReport Disarm(IReadOnlyList<object> leftovers, bool keep);
}

/// <summary>Keep/Discard asked in Revit (policy <c>ask</c>, ADR-009): the user's answer, or <see cref="Failed"/> when the
/// dialog could not be shown (rolled back like a Discard, but reported as <c>ask_failed</c>, not as the user's choice).</summary>
public enum AskResult
{
    Discard,
    Keep,
    Failed,
}

/// <summary>The Keep/Discard dialog of ADR-009, shown on the API thread while the groups are still open (the model shows the change).</summary>
public interface IAskDialog
{
    AskResult Ask(string title, ChangeReceipt changes);
}

/// <summary>What the guard saw: committed transactions, blocked document operations, groups rolled back.</summary>
/// <param name="Mode"><c>group</c> when every document was under a transaction group, <c>watch</c> when at least one
/// refused it and only change events protected it (ADR-007).</param>
public sealed record GuardReport(
    bool WriteDetected,
    int Transactions,
    IReadOnlyList<string> Blocked,
    IReadOnlyList<string> GroupsRolledBack,
    string Mode,
    IReadOnlyList<string> Notes)
{
    public static GuardReport Clean(string mode = "group") =>
        new(false, 0, Array.Empty<string>(), Array.Empty<string>(), mode, Array.Empty<string>());

    /// <summary>Write guard only: true when the target group was assimilated (one undo entry), false when it was rolled back.</summary>
    public bool Kept { get; init; }

    /// <summary>Write guard only: other open documents the script wrote to (their groups were rolled back).</summary>
    public IReadOnlyList<OtherDocumentWrite> OtherDocumentsWritten { get; init; } = Array.Empty<OtherDocumentWrite>();

    /// <summary>Documents opened or created during the run (F-R14). They stay open: the guard never closes a document.</summary>
    public IReadOnlyList<string> DocumentsOpened { get; init; } = Array.Empty<string>();
}

/// <summary>A document other than the target that a modify run committed transactions into (F-R15).</summary>
public sealed record OtherDocumentWrite(string Title, int Transactions);

/// <summary>The document a run is aimed at, as seen on the API thread just before the run.</summary>
/// <param name="Coexistence">Other assistant add-ins loaded in Revit (ADR-019); null when the host cannot tell.</param>
/// <param name="CoexistAllowed"><c>coexist_with_other_agents</c> from config.json: keep changes even when another agent is loaded.</param>
public sealed record RunTarget(object UiApp, object App, object? UiDoc, object? Doc, string? Title, string? Path, string? DocumentClass,
    Rift.Core.Policy.CoexistenceReport? Coexistence = null, bool CoexistAllowed = false);

/// <summary>Host services for one run, created on the API thread (Revit: from the dispatcher's UIApplication).</summary>
public interface IRunHost
{
    RunTarget Target { get; }

    /// <summary>Variables added to the script namespace (<c>DB, UI, System, eid</c>).</summary>
    IReadOnlyDictionary<string, object?> Extras { get; }

    /// <summary>Host object conversion (Revit types, ADR-015).</summary>
    ForeignConverter? Converter { get; }

    IRunGuard CreateGuard();

    /// <summary>The write guard of <c>mode: modify</c> (M2). Hosts that cannot write (PipeHost stand-ins) throw.</summary>
    IWriteGuard CreateWriteGuard() => throw new NotSupportedException("this host cannot run mode: modify");

    /// <summary>The Keep/Discard dialog, or null when none can be shown (policy <c>ask</c> is then refused).</summary>
    IAskDialog? Ask => null;
}

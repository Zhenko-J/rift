// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;

using Rift.Core.Protocol;

namespace Rift.Scripting.Runs;

/// <summary>What the document policy decided for a write run: the policy applied, or the code that refuses the run.</summary>
/// <param name="Applied"><c>auto</c> (kept without asking) or <c>ask</c> (Keep/Discard in Revit); null when refused.</param>
/// <param name="RefusalCode"><c>SANDBOX_REQUIRED</c> or null.</param>
/// <param name="Reason">Why the policy differs from the one requested, when it does (<c>unsaved_document</c>, <c>reference_document</c>).</param>
public sealed record PolicyDecision(string? Applied, string? RefusalCode, string? Reason)
{
    public bool Refused => RefusalCode is not null;
}

/// <summary>
/// The session the user chose in Revit at the start of the assistant session, as the write policy sees it.
/// </summary>
/// <param name="Chosen">False while the question is unanswered: every change is refused with <c>SESSION_PENDING</c>.</param>
/// <param name="InProject">The target is the project the user chose with "active project" (any class of document).</param>
/// <param name="Supervised">Every kept change shows Keep/Discard in Revit.</param>
/// <param name="Text">"sandbox copies · supervised", for the run result.</param>
public sealed record SessionPolicy(bool Chosen, bool InProject, bool Supervised, string? Text);

/// <summary>
/// ADR-009, as a pure function of the document class (from its file name, <c>DocumentClassifier</c>) and the request:
/// <c>sandbox</c> → the requested policy (<c>auto</c> by default); <c>unsaved</c> → <c>ask</c> forced; <c>reference</c> →
/// <c>ask</c> forced when <c>allow_reference</c> is set, refused otherwise; <c>unmarked</c> → refused. A run can only make the
/// policy stricter than requested, never looser. With a session (ADR-033): unanswered → <c>SESSION_PENDING</c>; the chosen
/// active project is written with the requested policy whatever its class; "supervised" turns <c>auto</c> into <c>ask</c>.
/// </summary>
public static class WritePolicy
{
    public const string Auto = "auto";
    public const string Ask = "ask";

    public static PolicyDecision Decide(string? documentClass, string requested, bool allowReference, SessionPolicy? session = null)
    {
        if (requested is not (Auto or Ask))
        {
            throw new ArgumentException("policy must be auto or ask", nameof(requested));
        }
        if (session is { Chosen: false })
        {
            return new PolicyDecision(null, ErrorCodes.SessionPending, "session_pending");
        }
        PolicyDecision decision = session is { InProject: true }
            ? new PolicyDecision(requested, null, null)
            : ByClass(documentClass, requested, allowReference);
        if (!decision.Refused && session is { Supervised: true } && decision.Applied == Auto)
        {
            return new PolicyDecision(Ask, null, "supervised_session");
        }
        return decision;
    }

    private static PolicyDecision ByClass(string? documentClass, string requested, bool allowReference)
    {
        return documentClass switch
        {
            "sandbox" => new PolicyDecision(requested, null, null),
            "unsaved" => new PolicyDecision(Ask, null, requested == Ask ? null : "unsaved_document"),
            "reference" when allowReference => new PolicyDecision(Ask, null, requested == Ask ? null : "reference_document"),
            "reference" => new PolicyDecision(null, ErrorCodes.SandboxRequired, "reference_document"),
            _ => new PolicyDecision(null, ErrorCodes.SandboxRequired, "unmarked_document"),
        };
    }

    /// <summary>The message of a <c>SANDBOX_REQUIRED</c> refusal, without the document name (it reaches the activity card).</summary>
    public static string RefusalMessage(PolicyDecision decision) => decision.Reason switch
    {
        "session_pending" => "the user has not answered the RIFT session window in Revit yet (sandbox copies or the active project, full auto or supervised): ask them to answer it, then call again",
        "reference_document" => "the active document is a reference file: writes need allow_reference (then Keep/Discard in Revit) or a SANDBOX copy",
        _ => "the active document is not a SANDBOX copy (no marker in its file name): work on a copy whose name contains SANDBOX, JETABLE, COPIE…",
    };
}

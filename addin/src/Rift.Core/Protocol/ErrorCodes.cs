// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
namespace Rift.Core.Protocol;

/// <summary>Error codes emitted by the add-in (docs/PROTOCOL.md). Server-side codes live in the Python package.</summary>
public static class ErrorCodes
{
    public const string Unauthorized = "UNAUTHORIZED";
    public const string ProtocolMismatch = "PROTOCOL_MISMATCH";
    public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
    public const string RateLimited = "RATE_LIMITED";
    public const string LeaseHeld = "LEASE_HELD";
    public const string Paused = "PAUSED";
    public const string RevitBusy = "REVIT_BUSY";
    public const string ListenerDead = "LISTENER_DEAD";
    public const string AgentConflict = "AGENT_CONFLICT";

    /// <summary>The user has not answered the session question in Revit yet: no change runs until they do.</summary>
    public const string SessionPending = "SESSION_PENDING";
    public const string NoDocument = "NO_DOCUMENT";
    public const string DocumentMismatch = "DOCUMENT_MISMATCH";
    public const string SandboxRequired = "SANDBOX_REQUIRED";
    public const string ExpectedDocumentRequired = "EXPECTED_DOCUMENT_REQUIRED";
    public const string ConfirmTitleMismatch = "CONFIRM_TITLE_MISMATCH";
    public const string ScriptError = "SCRIPT_ERROR";
    public const string Timeout = "TIMEOUT";
    public const string Cancelled = "CANCELLED";
    public const string TimeoutUnavailable = "TIMEOUT_UNAVAILABLE";
    public const string JobNotFound = "JOB_NOT_FOUND";
    /// <summary>Not an error code but a rollback reason (ADR-025): the read guard saw a write in <c>mode: query</c>.</summary>
    public const string WriteInQuery = "WRITE_IN_QUERY";
    /// <summary>
    /// A modify run that wrote into another open document (F-R15) or opened or created one (F-R14): rolled back entirely,
    /// returned as this code with the whole run result (<c>other_documents</c>, <c>documents_left_open</c>) as details.
    /// </summary>
    public const string OtherDocumentWrite = "OTHER_DOCUMENT_WRITE";
    /// <summary>Malformed envelope or unknown operation.</summary>
    public const string BadRequest = "BAD_REQUEST";
    public const string Internal = "INTERNAL";
}

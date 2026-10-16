// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text.Json.Nodes;
using System.Threading;

namespace Rift.Core.Protocol;

/// <summary>
/// One handler per <c>op</c>. <see cref="NeedsApiThread"/> decides whether the router runs it on the pipe thread
/// (lease, job, compile) or queues it as a job for the Revit API thread (context, run, tool).
/// </summary>
public interface IOperationHandler
{
    string Op { get; }

    bool NeedsApiThread { get; }

    /// <summary>Returns the <c>result</c> node. Throws <see cref="OperationException"/> for a protocol-level error or refusal.</summary>
    JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation);
}

/// <summary>An error or a refusal with a protocol code; the router turns it into the response envelope.</summary>
public sealed class OperationException : Exception
{
    public OperationException(string code, string? message = null, JsonNode? details = null, bool refused = false)
        : base(message ?? code)
    {
        Code = code;
        Details = details;
        Refused = refused;
    }

    public string Code { get; }

    public JsonNode? Details { get; }

    public bool Refused { get; }
}

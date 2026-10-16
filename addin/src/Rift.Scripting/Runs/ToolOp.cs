// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Json;
using Rift.Core.Protocol;

namespace Rift.Scripting.Runs;

/// <summary>A family of fixed tools behind <c>op: tool</c>, chosen by the prefix of <c>args.name</c> (<c>audit.</c>, <c>history.</c>).</summary>
public interface IToolHandler
{
    string Prefix { get; }

    /// <summary>Every tool name the handler serves, for the refusal message.</summary>
    IEnumerable<string> Names { get; }

    /// <summary>Whether this handler serves <paramref name="name"/> (by default: the name starts with <see cref="Prefix"/>).</summary>
    bool Serves(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);

    JsonNode? Execute(string name, RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation);
}

/// <summary>
/// <c>op: tool</c> (M2): routes <c>args.name</c> to the handler whose prefix matches; an unknown name is refused with the
/// list of every tool served. Runs on the API thread (every family reads at least the active document).
/// </summary>
public sealed class ToolOp : IOperationHandler
{
    private readonly IReadOnlyList<IToolHandler> _handlers;

    public ToolOp(IEnumerable<IToolHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        _handlers = handlers.ToArray();
    }

    public string Op => "tool";

    public bool NeedsApiThread => true;

    public IEnumerable<string> Names => _handlers.SelectMany(h => h.Names);

    public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        string name = request.Args?["name"] is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;
        foreach (IToolHandler handler in _handlers)
        {
            if (handler.Serves(name))
            {
                return handler.Execute(name, request, progress, cancellation);
            }
        }
        throw new OperationException(ErrorCodes.BadRequest, "tool " + Sanitizer.Sanitize(name, 64) + " is not available in this build (available: "
            + string.Join(", ", Names) + ")", refused: true);
    }
}

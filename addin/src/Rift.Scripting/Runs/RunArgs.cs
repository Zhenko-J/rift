// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text.Json.Nodes;

using Rift.Core.Json;
using Rift.Core.Protocol;

namespace Rift.Scripting.Runs;

/// <summary>Validated arguments of <c>op: compile</c> and <c>op: run</c> (docs/PROTOCOL.md).</summary>
internal static class RunArgs
{
    public const int DefaultTimeoutS = 60;
    public const int MaxTimeoutS = 3600;

    public const string Query = "query";
    public const string Modify = "modify";

    public static string Script(RequestEnvelope request)
    {
        string? script = String(request, "script");
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new OperationException(ErrorCodes.BadRequest, "args.script is required", refused: true);
        }
        return script;
    }

    public static string Language(RequestEnvelope request)
    {
        string language = String(request, "language") ?? "python";
        if (!string.Equals(language, "python", StringComparison.Ordinal))
        {
            throw new OperationException(ErrorCodes.BadRequest, "language " + Sanitizer.Sanitize(language, 32) + " is not available (python only until M4)", refused: true);
        }
        return language;
    }

    /// <summary><c>query</c> (M1) or <c>modify</c> (M2); anything else is refused without running.</summary>
    public static string Mode(RequestEnvelope request)
    {
        string mode = String(request, "mode") ?? Query;
        if (mode is not (Query or Modify))
        {
            throw new OperationException(ErrorCodes.BadRequest, "mode " + Sanitizer.Sanitize(mode, 32) + " is not available (query or modify)", refused: true);
        }
        return mode;
    }

    /// <summary><c>args.dry_run</c>, true by default (ADR-009): a modify run is rolled back unless the client said otherwise.</summary>
    public static bool DryRun(RequestEnvelope request) => Bool(request, "dry_run") ?? true;

    /// <summary><c>args.policy</c>: <c>auto</c> (default) or <c>ask</c>.</summary>
    public static string Policy(RequestEnvelope request)
    {
        string policy = String(request, "policy") ?? WritePolicy.Auto;
        if (policy is not (WritePolicy.Auto or WritePolicy.Ask))
        {
            throw new OperationException(ErrorCodes.BadRequest, "policy " + Sanitizer.Sanitize(policy, 32) + " is not available (auto or ask)", refused: true);
        }
        return policy;
    }

    /// <summary><c>args.allow_reference</c>, false by default (ADR-009).</summary>
    public static bool AllowReference(RequestEnvelope request) => Bool(request, "allow_reference") ?? false;

    public static string Title(RequestEnvelope request, string fallback = "query")
    {
        string? title = String(request, "title");
        return string.IsNullOrWhiteSpace(title) ? fallback : Sanitizer.Sanitize(title.Trim(), 120);
    }

    public static TimeSpan Timeout(RequestEnvelope request)
    {
        int seconds = request.Args?["timeout_s"] is JsonValue v && v.TryGetValue(out int s) ? s
            : request.TimeoutS > 0 ? request.TimeoutS : DefaultTimeoutS;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, MaxTimeoutS));
    }

    public static JsonNode Inputs(RequestEnvelope request)
    {
        JsonNode? inputs = request.Args?["inputs"];
        if (inputs is null)
        {
            return new JsonObject();
        }
        if (inputs is not JsonObject)
        {
            throw new OperationException(ErrorCodes.BadRequest, "args.inputs must be an object", refused: true);
        }
        return inputs.DeepClone();
    }

    private static string? String(RequestEnvelope request, string name) =>
        request.Args?[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static bool? Bool(RequestEnvelope request, string name) =>
        request.Args?[name] is JsonValue v && v.TryGetValue(out bool b) ? b : null;
}

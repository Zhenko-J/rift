// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Rift.Core.Protocol;

/// <summary>Request body (docs/PROTOCOL.md § Envelopes). Properties are snake_case on the wire (JsonCodec).</summary>
public sealed record RequestEnvelope(
    [property: JsonPropertyName("v")] string V,
    string Id,
    string Op,
    [property: JsonPropertyName("timeout_s")] int TimeoutS,
    JsonNode? Client,
    [property: JsonPropertyName("expected_document")] JsonNode? ExpectedDocument,
    JsonNode? Args);

public sealed record ErrorInfo(string Code, string? Type = null, string? Message = null, string? Traceback = null);

public sealed record TargetInfo(int Pid, int Generation, JsonNode? Document);

/// <summary>Response body. <c>Status</c> is one of ok, error, refused, accepted, running.</summary>
public sealed record ResponseEnvelope(
    [property: JsonPropertyName("v")] string V,
    string? Id,
    string Status,
    TargetInfo? Target,
    JsonNode? Result,
    ErrorInfo? Error,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs)
{
    public const string Ok = "ok";
    public const string ErrorStatus = "error";
    public const string Refused = "refused";
    public const string Accepted = "accepted";
    public const string Running = "running";

    /// <param name="details">Typed details of the failure (a run result, for instance), returned in <c>result</c>.</param>
    public static ResponseEnvelope Fail(string? id, string code, string? message = null, TargetInfo? target = null, long elapsedMs = 0, JsonNode? details = null) =>
        new(ProtocolVersion.Current, id, ErrorStatus, target, details, new ErrorInfo(code, null, message), elapsedMs);

    public static ResponseEnvelope Refuse(string? id, string code, string? message, JsonNode? details, TargetInfo? target, long elapsedMs = 0) =>
        new(ProtocolVersion.Current, id, Refused, target, details, new ErrorInfo(code, null, message), elapsedMs);
}

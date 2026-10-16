// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Lease;

namespace Rift.Core.Protocol.Ops;

/// <summary><c>op: lease</c> (ADR-004): acquire or renew the single-client lease. Runs on the pipe thread.</summary>
public sealed class LeaseOp : IOperationHandler
{
    private readonly LeaseManager _lease;

    public LeaseOp(LeaseManager lease)
    {
        _lease = lease;
    }

    public string Op => "lease";

    public bool NeedsApiThread => false;

    public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        string? clientId = request.Args?["client_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(clientId))
        {
            throw new OperationException(ErrorCodes.BadRequest, "lease: client_id is required");
        }
        JsonNode? client = request.Args?["client"];
        var candidate = new LeaseHolder(clientId, client?["name"]?.GetValue<string>() ?? "unknown", client?["version"]?.GetValue<string>() ?? "?", DateTimeOffset.UtcNow);
        LeaseResult result = _lease.Acquire(candidate);
        if (!result.Granted)
        {
            throw new OperationException(ErrorCodes.LeaseHeld, "held by " + result.Holder!.Name, new JsonObject { ["holder"] = ToJson(result.Holder) }, refused: true);
        }
        return new JsonObject { ["lease_until_utc"] = result.Until!.Value.ToString("O"), ["holder"] = ToJson(result.Holder!) };
    }

    public static JsonObject ToJson(LeaseHolder holder) => new()
    {
        ["client_id"] = holder.ClientId,
        ["name"] = holder.Name,
        ["version"] = holder.Version,
        ["since_utc"] = holder.Since.ToString("O"),
    };
}

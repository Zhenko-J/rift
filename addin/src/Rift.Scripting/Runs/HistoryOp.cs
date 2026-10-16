// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Json;
using Rift.Core.Journal;
using Rift.Core.Protocol;

namespace Rift.Scripting.Runs;

/// <summary>
/// <c>op: tool</c> with <c>history.list</c> and <c>history.record</c> (M2, ADR-026 §2): the change receipts of the active
/// document, read from the per-document history kept by <see cref="RunJournal"/>, and the reason an assistant attaches to one
/// of them. Never touches the model.
/// </summary>
public sealed class HistoryOp : IToolHandler
{
    public const string List = "history.list";
    public const string Record = "history.record";
    public const int DefaultLimit = 20;
    public const int MaxLimit = 200;

    private static readonly string[] s_names = { List, Record };

    private readonly RunJournal _journal;
    private readonly Func<IRunHost> _host;

    public HistoryOp(RunJournal journal, Func<IRunHost> host)
    {
        _journal = journal;
        _host = host;
    }

    public string Prefix => "history.";

    public IEnumerable<string> Names => s_names;

    public JsonNode? Execute(string name, RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        RunTarget target = _host().Target;
        if (target.Doc is null)
        {
            throw new OperationException(ErrorCodes.NoDocument, "no active document", refused: true);
        }
        string key = RunJournal.HistoryKey(target.Path ?? target.Title);
        string? title = target.Title is null ? null : Sanitizer.Sanitize(target.Title, 260);
        switch (name)
        {
            case List:
            {
                int limit = request.Args?["limit"] is JsonValue v && v.TryGetValue(out int l) ? Math.Clamp(l, 1, MaxLimit) : DefaultLimit;
                var records = new JsonArray();
                foreach (JsonObject line in _journal.ReadHistory(key, limit))
                {
                    records.Add(line);
                }
                return new JsonObject { ["document"] = new JsonObject { ["title"] = title }, ["records"] = records };
            }
            case Record:
            {
                string? reason = request.Args?["reason"] is JsonValue r && r.TryGetValue(out string? rs) ? rs : null;
                if (string.IsNullOrWhiteSpace(reason))
                {
                    throw new OperationException(ErrorCodes.BadRequest, "args.reason is required", refused: true);
                }
                string? id = request.Args?["history_id"] is JsonValue h && h.TryGetValue(out string? hs) && !string.IsNullOrWhiteSpace(hs) ? hs : null;
                JsonObject? updated = _journal.AttachReason(key, id, Sanitizer.Sanitize(reason.Trim(), 2000));
                if (updated is null)
                {
                    throw new OperationException(ErrorCodes.BadRequest, id is null
                        ? "no change receipt yet for the active document: run a modify first"
                        : "no change receipt " + Sanitizer.Sanitize(id, 32) + " for the active document", refused: true);
                }
                return new JsonObject { ["document"] = new JsonObject { ["title"] = title }, ["record"] = updated };
            }
            default:
                throw new OperationException(ErrorCodes.BadRequest, "tool " + Sanitizer.Sanitize(name, 64) + " is not available (available: " + string.Join(", ", s_names) + ")", refused: true);
        }
    }
}

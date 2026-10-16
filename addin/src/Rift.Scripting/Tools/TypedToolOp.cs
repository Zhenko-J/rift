// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Json;
using Rift.Core.Protocol;
using Rift.Scripting.Runs;

namespace Rift.Scripting.Tools;

/// <summary>
/// <c>op: tool</c> for the typed tools of M3 (ADR-032). <c>args.name</c> is the tool name, <c>args.inputs</c> its arguments.
/// A read tool runs through exactly the path of <c>run_query</c> (read guard, always rolled back; a write is
/// <c>WRITE_IN_QUERY</c>); a write tool runs through the path of <c>run_modify</c> (expected document required, document
/// policy, dry run by default, one undo entry named after the tool when kept, change receipt, history) with the write
/// arguments of the request (<c>dry_run</c>, <c>policy</c>, <c>allow_reference</c>). <c>batch</c> chains several write tools
/// in one modify run: one undo entry, one receipt, the first failure rolls everything back.
/// </summary>
public sealed class TypedToolOp : IToolHandler
{
    public const string BatchName = "batch";
    public const int MaxBatchItems = 50;

    private const string BatchDriver = """
_rift_out = []
for _rift_i, _rift_item in enumerate(inputs['items']):
    _rift_name = _rift_item['tool']
    report_progress(int(100 * _rift_i / max(1, len(inputs['items']))), _rift_name)
    _rift_g = {'doc': doc, 'uidoc': uidoc, 'app': app, 'uiapp': uiapp, 'report_progress': report_progress, 'rift_dry_run': rift_dry_run,
               'inputs': _rift_item.get('inputs') or {}, '__builtins__': __builtins__}
    exec(rift_tool_sources[_rift_name], _rift_g)
    _rift_out.append({'tool': _rift_name, 'result': _rift_g.get('result')})
result = {'items': _rift_out}
""";

    private readonly TypedToolCatalog _catalog;
    private readonly RunQueryOp _runner;

    public TypedToolOp(TypedToolCatalog catalog, RunQueryOp runner)
    {
        _catalog = catalog;
        _runner = runner;
    }

    public string Prefix => string.Empty;

    public IEnumerable<string> Names => _catalog.Names.Append(BatchName);

    public bool Serves(string name) => name == BatchName || _catalog.Find(name) is not null;

    public JsonNode? Execute(string name, RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        JsonObject inputs = request.Args?["inputs"] is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();
        if (name == BatchName)
        {
            return Batch(request, inputs, progress, cancellation);
        }
        TypedTool tool = _catalog.Find(name)
            ?? throw new OperationException(ErrorCodes.BadRequest, "tool " + Sanitizer.Sanitize(name, 64) + " is not available", refused: true);
        TimeSpan timeout = Timeout(request);
        JsonObject run = tool.Writes
            ? _runner.RunFixedModify(request, "tool:" + tool.Name, tool.Source, tool.Name, inputs, timeout, DryRunExtra(request), progress, cancellation)
            : _runner.RunFixed(request, "tool:" + tool.Name, tool.Source, tool.Name, timeout, inputs, null, progress, cancellation);
        JsonNode? value = run["result"]?.DeepClone();
        run.Remove("result");
        var answer = new JsonObject { ["tool"] = tool.Name, ["writes"] = tool.Writes, ["result"] = value, ["run"] = run };
        if (run["result_ref"] is not null)
        {
            answer["result_ref"] = run["result_ref"]!.DeepClone();
        }
        return answer;
    }

    private JsonObject Batch(RequestEnvelope request, JsonObject inputs, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        if (inputs["items"] is not JsonArray items || items.Count == 0)
        {
            throw new OperationException(ErrorCodes.BadRequest, "batch needs inputs.items: [{tool, inputs}]", refused: true);
        }
        if (items.Count > MaxBatchItems)
        {
            throw new OperationException(ErrorCodes.BadRequest, "batch is limited to " + MaxBatchItems + " items", refused: true);
        }
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonNode? item in items)
        {
            string tool = item?["tool"] is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;
            TypedTool? t = _catalog.Find(tool);
            if (t is null || !t.Writes)
            {
                throw new OperationException(ErrorCodes.BadRequest, "batch items must be typed write tools; '" + Sanitizer.Sanitize(tool, 64) + "' is not one", refused: true);
            }
            sources[t.Name] = t.Source;
        }
        string title = "batch: " + string.Join(", ", items.Select(i => i?["tool"]?.GetValue<string>()).Distinct().Take(4));
        var extras = new Dictionary<string, object?>(StringComparer.Ordinal) { ["rift_tool_sources"] = sources, ["rift_dry_run"] = RunArgs.DryRun(request) };
        JsonObject run = _runner.RunFixedModify(request, "tool:batch", BatchDriver, title, inputs, Timeout(request), extras, progress, cancellation);
        JsonNode? value = run["result"]?.DeepClone();
        run.Remove("result");
        return new JsonObject { ["tool"] = BatchName, ["writes"] = true, ["result"] = value, ["run"] = run };
    }

    /// <summary>
    /// <c>rift_dry_run</c> for write tools whose effect lies outside the model (the shared parameter file): the guard cannot
    /// roll a file back, so such a tool reads it and only describes what it would do during a dry run.
    /// </summary>
    private static Dictionary<string, object?> DryRunExtra(RequestEnvelope request) =>
        new(StringComparer.Ordinal) { ["rift_dry_run"] = RunArgs.DryRun(request) };

    private static TimeSpan Timeout(RequestEnvelope request)
    {
        int seconds = request.Args?["timeout_s"] is JsonValue t && t.TryGetValue(out int ts) ? ts : request.TimeoutS > 0 ? request.TimeoutS : 120;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, RunArgs.MaxTimeoutS));
    }
}

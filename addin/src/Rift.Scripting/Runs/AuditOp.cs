// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Json;
using Rift.Core.Journal;
using Rift.Core.Protocol;
using Rift.Scripting.Presets;

namespace Rift.Scripting.Runs;

/// <summary>
/// <c>op: tool</c> with <c>args.name = "audit.pN"</c> or <c>"audit.all"</c> (ADR-024, M1 tranches 4 and 5; served through
/// <see cref="ToolOp"/> since M2). Runs the
/// embedded preset(s) through <see cref="RunQueryOp.RunFixed"/> — same timeout invariant, read guard, rollback and journal
/// as <c>run_query</c>, one journaled run per preset — with a collector bound to the script variable
/// <c>rift_collector</c>, then returns the dumped documents. <c>audit.all</c> runs every preset of the catalogue in order
/// inside one job, sharing one collector so that the merge step reads the earlier documents; progress is reported across
/// the whole job; the first failing preset stops the job with its own error (the earlier runs are listed in the details).
/// Documents are sanitised (ADR-020) but keep the number text the preset wrote. When they would not fit in one frame they
/// are journaled next to the last run and returned as <c>documents_ref</c> (paths relative to the rift data folder)
/// instead of <c>documents</c>.
/// </summary>
public sealed class AuditOp : IToolHandler
{
    public const int DefaultTimeoutS = 300;

    /// <summary>Above this total (UTF-8 JSON) the documents go to the journal (frame limit 1 MiB, ADR-026 §7).</summary>
    public const int InlineThresholdBytes = RunQueryOp.ResultRefThresholdBytes;

    private readonly PresetCatalog _presets;
    private readonly RunQueryOp _runner;
    private readonly RunJournal? _journal;

    public AuditOp(PresetCatalog presets, RunQueryOp runner, RunJournal? journal)
    {
        _presets = presets;
        _runner = runner;
        _journal = journal;
    }

    public string Prefix => "audit.";

    public IEnumerable<string> Names => _presets.Names;

    public JsonNode? Execute(string name, RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        IReadOnlyList<Preset> presets;
        if (name == PresetCatalog.AllName)
        {
            presets = _presets.All;
        }
        else
        {
            Preset one = _presets.Find(name)
                ?? throw new OperationException(ErrorCodes.BadRequest, "tool " + Sanitizer.Sanitize(name, 64) + " is not available in this build (available: "
                    + string.Join(", ", _presets.Names) + ")", refused: true);
            presets = new[] { one };
        }
        int seconds = request.Args?["timeout_s"] is JsonValue t && t.TryGetValue(out int ts) ? ts
            : request.TimeoutS > 0 ? request.TimeoutS : DefaultTimeoutS;
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, RunArgs.MaxTimeoutS));

        var collector = new MemoryResultCollector();
        var extras = new Dictionary<string, object?>(StringComparer.Ordinal) { ["rift_collector"] = collector };
        var runs = new JsonObject();
        var timing = new JsonObject();
        var clock = Stopwatch.StartNew();
        JsonObject? last = null;
        for (int i = 0; i < presets.Count; i++)
        {
            Preset preset = presets[i];
            int index = i;
            // The whole timeout applies to each preset (a preset is one script); the job's own deadline bounds the sum.
            var slice = new Progress<(int Percent, string Message)>(p =>
                progress.Report(((index * 100 + Math.Clamp(p.Percent, 0, 100)) / presets.Count, preset.Name + ": " + p.Message)));
            long started = clock.ElapsedMilliseconds;
            JsonObject run;
            try
            {
                run = _runner.RunFixed(request, "tool:" + preset.Name, _presets.Source(preset), preset.Title, timeout, extras, slice, cancellation);
            }
            catch (OperationException ex) when (presets.Count > 1 && ex.Details is JsonObject failed)
            {
                // audit.all: the failing preset's run result is the error's details, the earlier runs come with it.
                failed["preset"] = preset.Name;
                failed["runs"] = runs.DeepClone();
                throw;
            }
            run.Remove("result");
            if (!collector.Documents.ContainsKey(preset.Document))
            {
                throw new OperationException(ErrorCodes.Internal, "preset " + preset.Name + " did not produce " + preset.Document, run);
            }
            timing[preset.Name] = clock.ElapsedMilliseconds - started;
            runs[preset.Name] = run;
            last = run;
            cancellation.ThrowIfCancellationRequested();
        }

        var result = new JsonObject { ["tool"] = name };
        if (presets.Count == 1)
        {
            // A JsonNode has one parent: take the run back out of the (single-entry) map.
            runs.Remove(presets[0].Name);
            result["run"] = last;
        }
        else
        {
            result["runs"] = runs;
            result["timing"] = new JsonObject { ["total_ms"] = clock.ElapsedMilliseconds, ["presets"] = timing };
        }
        int total = collector.Sizes.Values.Sum();
        string? runLog = last?["run_log"] is JsonValue rl && rl.TryGetValue(out string? log) ? log : null;
        if (total <= InlineThresholdBytes || _journal is null || runLog is null)
        {
            var documents = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> kv in collector.Documents)
            {
                documents[kv.Key] = MemoryResultCollector.Sanitized(kv.Value);
            }
            result["documents"] = documents;
        }
        else
        {
            // The runs are already closed in the journal: the documents go next to the last one, redacted like every
            // journal file (ADR-005), which the response says so that a parity check knows where a difference may come from.
            string folder = runLog["runs/".Length..];
            var refs = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> kv in collector.Documents)
            {
                JsonNode? clean = MemoryResultCollector.Sanitized(kv.Value);
                refs[kv.Key] = _journal.WriteToFolder(folder, kv.Key + ".json", clean?.ToJsonString(JsonCodec.Options) ?? "null");
            }
            result["documents_ref"] = refs;
            result["documents_bytes"] = total;
            result["documents_redacted"] = true;
        }
        return result;
    }
}

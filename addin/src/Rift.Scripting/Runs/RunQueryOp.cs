// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Json;
using Rift.Core.Journal;
using Rift.Core.Logging;
using Rift.Core.Protocol;

namespace Rift.Scripting.Runs;

/// <summary>
/// <c>op: run</c>. In <c>mode: query</c> (M1), on the Revit API thread: refuses when the timeout is unavailable, compiles
/// (a syntax error never runs), journals the request, arms the read guard, runs with the cooperative deadline, disarms
/// (always a rollback), then builds the run result. A write the guard saw — a committed transaction, a save, an export, a
/// close — turns the run into <c>rolled_back</c> with reason <c>WRITE_IN_QUERY</c>, returned as that error code with the
/// whole run result as details.
/// In <c>mode: modify</c> (M2, ADR-009): <c>expected_document</c> is required, the document policy decides before anything
/// is compiled (<c>SANDBOX_REQUIRED</c> outside a SANDBOX copy), the write guard opens a group named after the run, the
/// change receipt is collected, and the run is kept (one undo entry) only when it succeeded, is not a dry run (the default)
/// and the policy is <c>auto</c> or the user chose Keep in Revit; everything else is rolled back with its reason. Every
/// modify run, dry run included, is appended to the per-document history.
/// Results above 700 KiB are journaled and returned as <c>result_ref</c> + <c>preview</c>; stdout/stderr (bounded to 1 MiB
/// by the engine) are cut to what fits in the 1 MiB frame, the rest is journaled. Error messages carry codes, types and line
/// numbers only (they reach the activity card); script text and model data stay in the typed details.
/// </summary>
public sealed class RunQueryOp : IOperationHandler
{
    /// <summary>Above this size (UTF-8 JSON) the result is journaled and replaced by a reference (ADR-026 §7).</summary>
    public const int ResultRefThresholdBytes = 700 * 1024;

    public const int PreviewChars = 2048;

    /// <summary>Room kept for the envelope, the error and the other fields when sizing stdout/stderr into one frame.</summary>
    private const int FrameMarginBytes = 48 * 1024;

    /// <summary>Rollback reasons of a modify run that touched another document (F-R15) or opened one (F-R14).</summary>
    public const string ReasonOtherDocumentWrite = "other_document_write";

    public const string ReasonDocumentOpened = "document_opened";

    private static readonly string[] s_disarmFailed = { "guard_disarm_failed" };

    private readonly Func<IScriptEngine> _engine;
    private readonly Func<IScriptEngine>? _csharp;
    private readonly Func<IRunHost> _host;
    private readonly RunJournal? _journal;
    private readonly FileLog _log;

    /// <param name="csharp">The C# engine (ADR-035), or null when this host has none (<c>language: csharp</c> is then refused).</param>
    public RunQueryOp(Func<IScriptEngine> engine, Func<IRunHost> host, RunJournal? journal, FileLog log, Func<IScriptEngine>? csharp = null)
    {
        _engine = engine;
        _csharp = csharp;
        _host = host;
        _journal = journal;
        _log = log;
    }

    public string Op => "run";

    public bool NeedsApiThread => true;

    public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        string mode = RunArgs.Mode(request);
        string language = RunArgs.Language(request);
        string script = RunArgs.Script(request);
        TimeSpan timeout = RunArgs.Timeout(request);
        JsonNode inputs = RunArgs.Inputs(request);
        if (mode == RunArgs.Modify)
        {
            return RunModify(request, "run", language, script, RunArgs.Title(request, "modify"), timeout, inputs, null, ScriptTrust.Agent, progress, cancellation);
        }
        return Run(request, "run", mode, language, script, RunArgs.Title(request), timeout, inputs, null, ScriptTrust.Agent, progress, cancellation);
    }

    /// <summary>
    /// Runs a fixed script shipped with the add-in (an audit preset, ADR-024) through exactly the same path as
    /// <c>op: run</c> in <c>mode: query</c>: timeout invariant, compile, journal, read guard, rollback, outcome.
    /// <paramref name="extras"/> are added to the host's script variables (the preset collector). Throws
    /// <see cref="OperationException"/> like <see cref="Execute"/>; returns the run result otherwise.
    /// </summary>
    public JsonObject RunFixed(RequestEnvelope request, string op, string script, string title, TimeSpan timeout,
        IReadOnlyDictionary<string, object?> extras, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        return Run(request, op, RunArgs.Query, "python", script, title, timeout, new JsonObject(), extras, ScriptTrust.Shipped, progress, cancellation);
    }

    /// <summary>
    /// A fixed read script with arguments (a typed read tool, M3; a baked command, ADR-034): same path as <c>mode: query</c>,
    /// <paramref name="inputs"/> as <c>inputs</c>. <paramref name="trust"/> is <see cref="ScriptTrust.Agent"/> for a baked
    /// command, which the assistant wrote (ADR-036).
    /// </summary>
    public JsonObject RunFixed(RequestEnvelope request, string op, string script, string title, TimeSpan timeout, JsonObject inputs,
        IReadOnlyDictionary<string, object?>? extras, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation,
        ScriptTrust trust = ScriptTrust.Shipped)
    {
        return Run(request, op, RunArgs.Query, "python", script, title, timeout, inputs, extras ?? new Dictionary<string, object?>(), trust, progress, cancellation);
    }

    /// <summary>
    /// A fixed write script with arguments (a typed write tool or a batch, M3): exactly the path of <c>mode: modify</c> —
    /// expected document, document policy, dry run by default (from the request's <c>args.dry_run</c>), write guard, receipt,
    /// history. Returns the run result, or throws like <see cref="Execute"/>.
    /// </summary>
    public JsonObject RunFixedModify(RequestEnvelope request, string op, string script, string title, JsonObject inputs, TimeSpan timeout,
        IReadOnlyDictionary<string, object?>? extras, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation,
        ScriptTrust trust = ScriptTrust.Shipped)
    {
        return RunModify(request, op, "python", script, title, timeout, inputs, extras, trust, progress, cancellation);
    }

    // ── mode: query ──────────────────────────────────────────────────────────────────────────

    private JsonObject Run(RequestEnvelope request, string op, string mode, string language, string script, string title, TimeSpan timeout,
        JsonNode inputs, IReadOnlyDictionary<string, object?>? extras, ScriptTrust trust, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        IScriptEngine engine = RequireTimeout(language);
        IRunHost host = _host();
        RunTarget target = host.Target;
        CheckExpectedDocument(request, target);
        if (extras is not null && target.Doc is null)
        {
            // A fixed script (preset) reads the active document: without one it would only fail inside Python.
            throw new OperationException(ErrorCodes.NoDocument, "no active document", refused: true);
        }

        string runId = NewRunId();
        JsonObject result = Head(runId, mode, language, title, target);
        result["policy_applied"] = "read_only";
        result["dry_run"] = false;

        CompileResult compiled = CompileOrThrow(engine, script, result, trust);
        string? folder = DocumentFolder(target.Path);
        Begin(runId, op, script, mode, title, timeout, inputs, compiled, folder, result, null);

        IRunGuard guard = host.CreateGuard();
        RunResult? run = null;
        GuardReport report;
        bool timeoutUnavailable = false;
        try
        {
            guard.Arm();
            run = engine.Run(compiled.Script!, Context(target, inputs, progress, host, extras), timeout, cancellation);
        }
        catch (TimeoutUnavailableException)
        {
            timeoutUnavailable = true;
        }
        finally
        {
            report = SafeDisarm(() => guard.Disarm(run?.Leftovers ?? Array.Empty<object>()));
        }

        if (timeoutUnavailable || run is null)
        {
            throw Refused(runId, result);
        }
        return Finish(runId, result, run, report, timeout, engine, write: report.WriteDetected || BlockedCalls.Classify(run.Error?.Inner) is not null, modify: null, cancellation);
    }

    // ── mode: modify (M2) ────────────────────────────────────────────────────────────────────

    private JsonObject RunModify(RequestEnvelope request, string op, string language, string script, string title, TimeSpan timeout, JsonNode inputs,
        IReadOnlyDictionary<string, object?>? extras, ScriptTrust trust, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        bool dryRun = RunArgs.DryRun(request);
        string requested = RunArgs.Policy(request);
        bool allowReference = RunArgs.AllowReference(request);

        IScriptEngine engine = RequireTimeout(language);
        IRunHost host = _host();
        RunTarget target = host.Target;
        if (!HasExpectedDocument(request))
        {
            throw new OperationException(ErrorCodes.ExpectedDocumentRequired, "mode: modify needs expected_document (path or title of the document to change)", refused: true);
        }
        CheckExpectedDocument(request, target);
        if (target.Doc is null)
        {
            throw new OperationException(ErrorCodes.NoDocument, "no active document", refused: true);
        }
        // ADR-019: another assistant add-in in the same Revit could act outside these guards. A dry run keeps nothing and
        // still runs; a change is not kept while such an add-in is loaded, unless config.json allows it.
        if (!dryRun && target.Coexistence is { OtherAgentActive: true } coexistence && !target.CoexistAllowed)
        {
            throw new OperationException(ErrorCodes.AgentConflict,
                "another assistant add-in is loaded in this Revit (" + string.Join(", ", coexistence.OtherAgents)
                + "): changes are not kept while it is (ADR-019); dry runs still work. Unload it, or set coexist_with_other_agents in config.json",
                new JsonObject { ["other_agents"] = new JsonArray(coexistence.OtherAgents.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()) },
                refused: true);
        }
        PolicyDecision policy = WritePolicy.Decide(target.DocumentClass, requested, allowReference, target.Session);
        if (policy.Refused)
        {
            throw new OperationException(policy.RefusalCode!, WritePolicy.RefusalMessage(policy),
                new JsonObject { ["document_class"] = target.DocumentClass, ["reason"] = policy.Reason, ["policy_requested"] = requested }, refused: true);
        }
        bool asks = !dryRun && policy.Applied == WritePolicy.Ask;
        if (asks && host.Ask is null)
        {
            throw new OperationException(ErrorCodes.BadRequest, "policy ask needs the Keep/Discard dialog, which this host cannot show", refused: true);
        }

        string runId = NewRunId();
        JsonObject result = Head(runId, RunArgs.Modify, language, title, target);
        result["policy_applied"] = policy.Applied;
        result["policy_requested"] = requested;
        if (policy.Reason is not null)
        {
            result["policy_reason"] = policy.Reason;
        }
        if (target.Session is { Text: not null } session)
        {
            result["session"] = session.Text;
        }
        result["dry_run"] = dryRun;
        result["history_id"] = runId;

        CompileResult compiled = CompileOrThrow(engine, script, result, trust);
        string? folder = DocumentFolder(target.Path);
        var extraArgs = new JsonObject { ["dry_run"] = dryRun, ["policy"] = requested, ["policy_applied"] = policy.Applied, ["allow_reference"] = allowReference };
        Begin(runId, op, script, RunArgs.Modify, title, timeout, inputs, compiled, folder, result, extraArgs);

        IWriteGuard guard = host.CreateWriteGuard();
        RunResult? run = null;
        GuardReport report;
        bool timeoutUnavailable = false;
        ChangeReceipt changes = ChangeReceipt.Empty;
        string? reason = null;
        try
        {
            guard.Arm(title);
            run = engine.Run(compiled.Script!, Context(target, inputs, progress, host, extras), timeout, cancellation);
        }
        catch (TimeoutUnavailableException)
        {
            timeoutUnavailable = true;
        }
        finally
        {
            changes = SafeChanges(guard);
            (IReadOnlyList<OtherDocumentWrite> others, IReadOnlyList<string> opened) = SafeElsewhere(guard);
            bool keep = false;
            if (run is not null && !run.TimedOut && run.Error is null && !guard.FailureRolledBack && (others.Count > 0 || opened.Count > 0))
            {
                // F-R15 / F-R14: what the script did elsewhere was rolled back (or cannot be): the run is never kept.
                reason = others.Count > 0 ? ReasonOtherDocumentWrite : ReasonDocumentOpened;
            }
            else if (run is not null && !run.TimedOut && run.Error is null && !guard.FailureRolledBack)
            {
                if (dryRun)
                {
                    reason = "dry_run";
                }
                else if (asks)
                {
                    AskResult answer = SafeAsk(host.Ask!, title, changes);
                    keep = answer == AskResult.Keep;
                    reason = answer switch
                    {
                        AskResult.Keep => null,
                        AskResult.Failed => "ask_failed",
                        _ => "user_discarded",
                    };
                }
                else
                {
                    keep = true;
                }
            }
            else if (guard.FailureRolledBack)
            {
                reason = "revit_failure";
            }
            report = SafeDisarm(() => guard.Disarm(run?.Leftovers ?? Array.Empty<object>(), keep));
            if (keep && !report.Kept)
            {
                reason = report.Notes.Count > 0 ? report.Notes[0] : "guard_discarded";
            }
        }

        result["changes"] = changes.ToJson();
        if (guard.Dialogs.Count > 0)
        {
            result["dialogs"] = ToArray(guard.Dialogs);
        }
        result["failures"] = guard.FailureRolledBack;
        if (report.OtherDocumentsWritten.Count > 0)
        {
            var others = new JsonArray();
            foreach (OtherDocumentWrite w in report.OtherDocumentsWritten)
            {
                others.Add(new JsonObject { ["title"] = Sanitizer.Sanitize(w.Title, 260), ["transactions"] = w.Transactions, ["rolled_back"] = true });
            }
            result["other_documents"] = others;
        }
        if (timeoutUnavailable || run is null)
        {
            throw Refused(runId, result);
        }
        return Finish(runId, result, run, report, timeout, engine, write: false, modify: (report.Kept, reason), cancellation);
    }

    // ── shared ───────────────────────────────────────────────────────────────────────────────

    private IScriptEngine RequireTimeout(string language)
    {
        IScriptEngine engine = language == "csharp"
            ? (_csharp ?? throw new OperationException(ErrorCodes.BadRequest, "C# scripts are not available in this host", refused: true))()
            : _engine();
        if (!engine.TimeoutAvailable)
        {
            throw new OperationException(ErrorCodes.TimeoutUnavailable, "the cooperative timeout is unavailable in this Revit session: scripts are refused");
        }
        return engine;
    }

    private static string NewRunId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private static JsonObject Head(string runId, string mode, string language, string title, RunTarget target) => new()
    {
        ["run_id"] = runId,
        ["mode"] = mode,
        ["language"] = language,
        ["title"] = title,
        ["document"] = new JsonObject { ["title"] = target.Title is null ? null : Sanitizer.Sanitize(target.Title, 260), ["class"] = target.DocumentClass },
    };

    private static CompileResult CompileOrThrow(IScriptEngine engine, string script, JsonObject result, ScriptTrust trust)
    {
        CompileResult compiled = engine.Compile(script, "<agent-script>", trust);
        if (!compiled.Ok)
        {
            result["outcome"] = "error";
            result["reason"] = "syntax_error";
            string type = compiled.ErrorType ?? (engine.Language == "csharp" ? "CompileError" : "SyntaxError");
            result["error"] = new JsonObject { ["type"] = type, ["message"] = Sanitizer.Sanitize(compiled.Message, 2048), ["line"] = compiled.Line, ["column"] = compiled.Column };
            throw new OperationException(ErrorCodes.ScriptError, type + " at line " + compiled.Line.ToString(CultureInfo.InvariantCulture) + " (nothing was run)", result);
        }
        return compiled;
    }

    private void Begin(string runId, string op, string script, string mode, string title, TimeSpan timeout, JsonNode inputs, CompileResult compiled,
        string? folder, JsonObject result, JsonObject? extraArgs)
    {
        var journalRequest = new JsonObject { ["op"] = op, ["mode"] = mode, ["title"] = title, ["timeout_s"] = (int)timeout.TotalSeconds, ["inputs"] = inputs.DeepClone(), ["sha256"] = compiled.Script!.Sha256 };
        if (extraArgs is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in extraArgs)
            {
                journalRequest[kv.Key] = kv.Value?.DeepClone();
            }
        }
        string? runFolder = _journal?.Begin(runId, op, script, journalRequest, folder);
        if (runFolder is not null)
        {
            result["run_log"] = "runs/" + runFolder;
        }
    }

    private static ScriptContext Context(RunTarget target, JsonNode inputs, IProgress<(int Percent, string Message)> progress, IRunHost host,
        IReadOnlyDictionary<string, object?>? extras) =>
        new(target.UiApp, target.App, target.UiDoc, target.Doc, inputs,
            (pct, msg) => progress.Report((Math.Clamp(pct, 0, 100), Sanitizer.Sanitize(msg, 200))), TextWriter.Null, TextWriter.Null)
        {
            Extras = Merge(host.Extras, extras),
            Converter = host.Converter,
        };

    private OperationException Refused(string runId, JsonObject result)
    {
        result["outcome"] = "refused";
        result["reason"] = ErrorCodes.TimeoutUnavailable;
        End(runId, result);
        return new OperationException(ErrorCodes.TimeoutUnavailable, "the trace hook is not live: the script was not run", result);
    }

    /// <param name="write">Query mode: the guard saw a write (WRITE_IN_QUERY).</param>
    /// <param name="modify">Modify mode: (kept, reason) — null in query mode.</param>
    private JsonObject Finish(string runId, JsonObject result, RunResult run, GuardReport report, TimeSpan timeout, IScriptEngine engine,
        bool write, (bool Kept, string? Reason)? modify, CancellationToken cancellation)
    {
        var blocked = new List<string>(report.Blocked);
        string? fromError = BlockedCalls.Classify(run.Error?.Inner);
        if (fromError is not null && !blocked.Contains(fromError))
        {
            blocked.Add(fromError);
        }
        foreach (string module in run.RefusedImports)
        {
            blocked.Add("import:" + Sanitizer.Sanitize(module, 120));
        }
        bool cancelled = run.TimedOut && cancellation.IsCancellationRequested;

        if (modify is null)
        {
            result["changes"] = new JsonObject { ["transactions"] = report.Transactions, ["groups_rolled_back"] = report.GroupsRolledBack.Count };
        }
        result["blocked"] = ToArray(blocked);
        result["engine"] = new JsonObject
        {
            ["name"] = engine.Language == "python" ? "ironpython-3.4" : engine.Language,
            ["timeout"] = "cooperative",
            ["guard"] = report.Mode,
        };
        if (report.Notes.Count > 0)
        {
            result["guard_notes"] = ToArray(report.Notes);
        }
        result["timing"] = new JsonObject { ["run_ms"] = (long)run.Elapsed.TotalMilliseconds };
        if (report.DocumentsOpened.Count > 0)
        {
            result["documents_left_open"] = ToArray(report.DocumentsOpened.Select(t => Sanitizer.Sanitize(t, 260)));
        }
        if (run.Error is not null)
        {
            result["error"] = new JsonObject
            {
                ["type"] = run.Error.Type,
                ["message"] = Sanitizer.Sanitize(run.Error.Message, 4096),
                ["line"] = run.Error.Line,
                ["traceback"] = run.Error.Traceback is null ? null : Sanitizer.Sanitize(run.Error.Traceback, 16 * 1024),
            };
        }
        if (run.TimedOut)
        {
            result["timed_out"] = !cancelled;
        }

        string? code;
        string? message;
        if (write)
        {
            result["outcome"] = "rolled_back";
            result["reason"] = ErrorCodes.WriteInQuery;
            code = ErrorCodes.WriteInQuery;
            message = "the script wrote to the model in mode: query; every transaction was rolled back"
                + (blocked.Count > 0 ? " (blocked: " + string.Join(", ", blocked) + ")" : string.Empty)
                + LeftOpen(report);
        }
        else if (cancelled)
        {
            result["outcome"] = modify is null ? "timeout" : "rolled_back";
            result["reason"] = "cancelled";
            code = ErrorCodes.Cancelled;
            message = "the script was cancelled by the client; nothing was kept";
        }
        else if (run.TimedOut)
        {
            result["outcome"] = "timeout";
            result["reason"] = "cooperative_timeout";
            code = ErrorCodes.Timeout;
            message = "the script was stopped after " + ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s (cooperative timeout); nothing was kept";
        }
        else if (run.Error is not null)
        {
            result["outcome"] = "error";
            result["reason"] = "script_error";
            code = ErrorCodes.ScriptError;
            message = Sanitizer.Sanitize(run.Error.Type, 64) + (run.Error.Line is int line ? " at line " + line.ToString(CultureInfo.InvariantCulture) : string.Empty)
                + (modify is null ? string.Empty : "; everything was rolled back");
        }
        else if (modify is { Reason: ReasonOtherDocumentWrite or ReasonDocumentOpened } elsewhere)
        {
            // F-R15 / F-R14: never "committed" with an empty receipt; the answer, the card and the history say why.
            result["outcome"] = "rolled_back";
            result["reason"] = elsewhere.Reason;
            code = ErrorCodes.OtherDocumentWrite;
            message = elsewhere.Reason == ReasonOtherDocumentWrite
                ? "the script wrote to another open document; those writes and the target's were rolled back, nothing was kept"
                : "the script opened or created a document; the target was rolled back, nothing was kept" + LeftOpen(report);
        }
        else if (modify is { } m)
        {
            result["outcome"] = m.Kept ? "committed" : "rolled_back";
            result["reason"] = m.Kept ? null : m.Reason;
            code = null;
            message = null;
        }
        else
        {
            // Nothing was written: the group was rolled back all the same, the model is untouched.
            result["outcome"] = "ok";
            result["reason"] = null;
            code = null;
            message = null;
        }

        if (!run.TimedOut && run.Error is null)
        {
            PlaceResult(runId, result, run.Value);
        }
        PlaceOutput(runId, result, run);
        if (modify is not null)
        {
            AppendHistory(result);
        }
        End(runId, result);
        if (code is not null)
        {
            throw new OperationException(code, message, result);
        }
        return result;
    }

    private void AppendHistory(JsonObject result)
    {
        if (_journal is null)
        {
            return;
        }
        string? title = result["document"]?["title"]?.GetValue<string>();
        string key = RunJournal.HistoryKey(_host().Target.Path ?? title);
        var line = new JsonObject
        {
            ["utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["history_id"] = result["history_id"]?.GetValue<string>(),
            ["title"] = result["title"]?.GetValue<string>(),
            ["outcome"] = result["outcome"]?.GetValue<string>(),
            ["reason"] = result["reason"]?.DeepClone(),
            ["dry_run"] = result["dry_run"]?.GetValue<bool>() ?? true,
            ["policy_applied"] = result["policy_applied"]?.GetValue<string>(),
            ["document"] = new JsonObject { ["title"] = title },
            ["changes"] = result["changes"]?.DeepClone(),
            ["other_documents"] = result["other_documents"]?.DeepClone(),
            ["documents_left_open"] = result["documents_left_open"]?.DeepClone(),
            ["run_log"] = result["run_log"]?.DeepClone(),
            ["reason_given"] = null,
        };
        _journal.AppendHistory(key, line);
    }

    private void PlaceResult(string runId, JsonObject result, JsonNode? value)
    {
        string json = value?.ToJsonString(JsonCodec.Options) ?? "null";
        int bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes <= ResultRefThresholdBytes)
        {
            result["result"] = value?.DeepClone();
            return;
        }
        string? reference = _journal?.WriteAttachment(runId, "result.json", json);
        result["result"] = null;
        result["result_ref"] = reference;
        result["result_bytes"] = bytes;
        result["preview"] = json.Length <= PreviewChars ? json : json[..PreviewChars];
        if (reference is null)
        {
            result["result_ref_error"] = "the result could not be journaled";
        }
    }

    /// <summary>stdout/stderr: what fits in the frame inline, the rest journaled (<c>stdout_ref</c>, <c>stderr_ref</c>).</summary>
    private void PlaceOutput(string runId, JsonObject result, RunResult run)
    {
        int used = Encoding.UTF8.GetByteCount(result.ToJsonString(JsonCodec.Options));
        int room = Math.Max(0, ProtocolVersion.MaxBodyBytes - FrameMarginBytes - used);
        int errRoom = Math.Min(JsonSize(run.Stderr), room / 2);
        (string err, bool errCut) = Fit(run.Stderr, errRoom);
        int outRoom = Math.Max(0, room - JsonSize(err));
        (string @out, bool outCut) = Fit(run.Stdout, outRoom);
        Put(runId, result, "stdout", @out, run.Stdout, outCut, run.StdoutTruncated);
        Put(runId, result, "stderr", err, run.Stderr, errCut, run.StderrTruncated);
    }

    private void Put(string runId, JsonObject result, string name, string inline, string full, bool cut, bool engineTruncated)
    {
        result[name] = inline;
        result[name + "_truncated"] = cut || engineTruncated;
        if (cut)
        {
            result[name + "_ref"] = _journal?.WriteAttachment(runId, name + ".txt", full);
        }
    }

    private static (string Text, bool Cut) Fit(string text, int maxJsonBytes)
    {
        string clean = Sanitizer.Sanitize(text, int.MaxValue);
        if (JsonSize(clean) <= maxJsonBytes)
        {
            return (clean, false);
        }
        int length = Math.Min(clean.Length, maxJsonBytes);
        while (length > 0)
        {
            if (char.IsHighSurrogate(clean[length - 1]))
            {
                length--;
                continue;
            }
            if (JsonSize(clean[..length]) <= maxJsonBytes)
            {
                break;
            }
            length = length * 9 / 10;
        }
        return (clean[..Math.Max(0, length)], true);
    }

    private static int JsonSize(string s) => Encoding.UTF8.GetByteCount(JsonValue.Create(s).ToJsonString(JsonCodec.Options));

    private void End(string runId, JsonObject result)
    {
        _journal?.End(runId, result, result["outcome"]?.GetValue<string>(), result["timing"]?["run_ms"]?.GetValue<long>() ?? 0);
    }

    private GuardReport SafeDisarm(Func<GuardReport> disarm)
    {
        try
        {
            return disarm();
        }
        catch (Exception ex)
        {
            // Disarm must not throw; if it does, the run is reported as a write (and never as kept) so that nobody trusts its result.
            _log.Write("run guard disarm failed: " + ex);
            return new GuardReport(true, 0, Array.Empty<string>(), Array.Empty<string>(), "failed", s_disarmFailed);
        }
    }

    private static string LeftOpen(GuardReport report) =>
        report.DocumentsOpened.Count == 0
            ? string.Empty
            : "; " + report.DocumentsOpened.Count.ToString(CultureInfo.InvariantCulture) + " document(s) opened by the script are still open in Revit (documents_left_open)";

    private (IReadOnlyList<OtherDocumentWrite> Others, IReadOnlyList<string> Opened) SafeElsewhere(IWriteGuard guard)
    {
        try
        {
            return (guard.OtherDocuments, guard.DocumentsOpened);
        }
        catch (Exception ex)
        {
            // Unknown is not "nothing": condemn the run rather than keep it on a guess.
            _log.Write("write guard other documents failed: " + ex);
            return (new[] { new OtherDocumentWrite("?", 0) }, Array.Empty<string>());
        }
    }

    private ChangeReceipt SafeChanges(IWriteGuard guard)
    {
        try
        {
            return guard.Changes;
        }
        catch (Exception ex)
        {
            _log.Write("write guard changes failed: " + ex);
            return ChangeReceipt.Empty;
        }
    }

    private AskResult SafeAsk(IAskDialog ask, string title, ChangeReceipt changes)
    {
        try
        {
            return ask.Ask(title, changes);
        }
        catch (Exception ex)
        {
            // A dialog that could not be shown is rolled back: never keep without the user's answer.
            _log.Write("ask dialog failed: " + ex);
            return AskResult.Failed;
        }
    }

    private static bool HasExpectedDocument(RequestEnvelope request)
    {
        if (request.ExpectedDocument is not JsonObject expected)
        {
            return false;
        }
        bool path = expected["path"] is JsonValue p && p.TryGetValue(out string? ps) && !string.IsNullOrWhiteSpace(ps);
        bool title = expected["title"] is JsonValue t && t.TryGetValue(out string? ts) && !string.IsNullOrWhiteSpace(ts);
        return path || title;
    }

    /// <summary>Refuses with DOCUMENT_MISMATCH when the request names another document (also used by the M4 operations).</summary>
    public static void CheckExpectedDocument(RequestEnvelope request, RunTarget target)
    {
        if (request.ExpectedDocument is not JsonObject expected)
        {
            return;
        }
        string? path = expected["path"] is JsonValue p && p.TryGetValue(out string? ps) ? ps : null;
        string? title = expected["title"] is JsonValue t && t.TryGetValue(out string? ts) ? ts : null;
        if (path is null && title is null)
        {
            return;
        }
        if (target.Doc is null)
        {
            throw new OperationException(ErrorCodes.NoDocument, "no active document", refused: true);
        }
        bool pathOk = path is null || string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase);
        bool titleOk = title is null || string.Equals(title, target.Title, StringComparison.Ordinal);
        if (!pathOk || !titleOk)
        {
            throw new OperationException(ErrorCodes.DocumentMismatch, "the active document is not the expected one", refused: true);
        }
    }

    private static string? DocumentFolder(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        int cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return cut > 0 ? path[..cut] : null;
    }

    private static IReadOnlyDictionary<string, object?> Merge(IReadOnlyDictionary<string, object?> host, IReadOnlyDictionary<string, object?>? extras)
    {
        if (extras is null || extras.Count == 0)
        {
            return host;
        }
        var merged = new Dictionary<string, object?>(host, StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> kv in extras)
        {
            merged[kv.Key] = kv.Value;
        }
        return merged;
    }

    private static JsonArray ToArray(IEnumerable<string> items)
    {
        var arr = new JsonArray();
        foreach (string s in items)
        {
            arr.Add(s);
        }
        return arr;
    }
}

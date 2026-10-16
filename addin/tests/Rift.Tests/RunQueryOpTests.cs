// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using Rift.Core.Jobs;
using Rift.Core.Journal;
using Rift.Core.Json;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Core.Protocol.Ops;
using Rift.Scripting;
using Rift.Scripting.Python;
using Rift.Scripting.Runs;

namespace Rift.Tests;

/// <summary>One engine for the whole class: creating IronPython costs about half a second.</summary>
public sealed class EngineFixture : IDisposable
{
    public IronPythonEngine Engine { get; } = new(Array.Empty<System.Reflection.Assembly>(), Path.Combine(AppContext.BaseDirectory, "lib"));

    public void Dispose() => Engine.Dispose();
}

/// <summary>
/// Plan M1, tests 8 (run part), 15c (result_ref, bounded output) and criterion 3 outside Revit: <c>op: run</c> through the
/// router, the real IronPython engine, a fake Revit API thread, a fake document and a fake read guard.
/// </summary>
public sealed class RunQueryOpTests : IClassFixture<EngineFixture>, IDisposable
{
    private readonly EngineFixture _fixture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rift-runs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHost _host = new();

    public RunQueryOpTests(EngineFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>What a script sees as <c>doc</c>: Commit and Save stand for a disguised write and a blocked save.</summary>
    public sealed class FakeDocument
    {
        private readonly FakeGuard _guard;

        internal FakeDocument(FakeGuard guard)
        {
            _guard = guard;
        }

        public string Title => "FAKE_SANDBOX";

#pragma warning disable CA1822 // called from Python
        public void Commit() => _guard.Transactions++;

        public void Save() => _guard.Blocked.Add("save");

        /// <summary>Stands for the DocumentOpened event the read guard sees when a script opens a document (F-R14).</summary>
        public void OpenOther(string title)
        {
            _guard.Opened.Add(title);
            _guard.Blocked.Add("open_document");
        }
#pragma warning restore CA1822
    }

    public sealed class FakeGuard : IRunGuard
    {
        public int Armed { get; private set; }

        public int Disarmed { get; private set; }

        public int Transactions { get; set; }

        public List<string> Blocked { get; } = new();

        public List<string> Opened { get; } = new();

        public void Arm() => Armed++;

        public GuardReport Disarm(IReadOnlyList<object> leftovers)
        {
            Disarmed++;
            return new GuardReport(Transactions > 0 || Blocked.Count > 0, Transactions, Blocked.ToArray(), new[] { "document" }, "group", Array.Empty<string>())
            {
                DocumentsOpened = Opened.ToArray(),
            };
        }
    }

    private sealed class FakeHost : IRunHost
    {
        public FakeHost()
        {
            Doc = new FakeDocument(Guard);
            Target = new RunTarget(new object(), new object(), null, Doc, "FAKE_SANDBOX", @"C:\work\FAKE_SANDBOX.rvt", "sandbox");
        }

        public FakeGuard Guard { get; } = new();

        public FakeDocument Doc { get; }

        public RunTarget Target { get; }

        public IReadOnlyDictionary<string, object?> Extras { get; } = new Dictionary<string, object?> { ["System"] = new ScriptNamespace("System") };

        public ForeignConverter? Converter => null;

        public IRunGuard CreateGuard() => Guard;
    }

    private sealed class Runner : IJobRunner
    {
        public void Enqueue(Job job) => _ = Task.Run(() =>
        {
            if (!JobTable.MarkStarted(job))
            {
                return;
            }
            try
            {
                JobTable.MarkDone(job, job.Work(new Progress<(int, string)>(), CancellationToken.None), null);
            }
            catch (OperationException ex)
            {
                JobTable.MarkDone(job, null, new ErrorInfo(ex.Code, null, ex.Message), ex.Refused, ex.Details);
            }
        });

        public string? BusyReason() => null;
    }

    private RequestRouter Router(IScriptEngine? engine = null, TimeSpan? wait = null)
    {
        IScriptEngine e = engine ?? _fixture.Engine;
        var jobs = new JobTable(TimeSpan.FromMinutes(1));
        var journal = new RunJournal(_dir, new RedactionContext("someone", "somehost"), TimeSpan.FromDays(14), FileLog.Null);
        return new RequestRouter(
            new IOperationHandler[] { new CompileOp(() => e), new RunQueryOp(() => e, () => _host, journal, FileLog.Null), new JobOp(jobs) },
            jobs, new Runner(), () => new TargetInfo(1, 1, null), FileLog.Null, TimeSpan.FromSeconds(10), wait ?? TimeSpan.FromSeconds(60));
    }

    private static ResponseEnvelope Run(RequestRouter router, string script, int timeoutS = 30, string mode = "query", JsonNode? expected = null)
    {
        var args = new JsonObject { ["script"] = script, ["mode"] = mode, ["title"] = "test", ["timeout_s"] = timeoutS, ["inputs"] = new JsonObject { ["k"] = 2 } };
        var body = new JsonObject { ["v"] = "2.0", ["id"] = "r", ["op"] = "run", ["timeout_s"] = timeoutS + 30, ["args"] = args, ["expected_document"] = expected };
        return router.Handle(Encoding.UTF8.GetBytes(body.ToJsonString()), CancellationToken.None);
    }

    [Fact]
    public void A_clean_query_returns_its_result_and_the_group_is_rolled_back_all_the_same()
    {
        ResponseEnvelope r = Run(Router(), "print('reading')\nresult = {'title': doc.Title, 'k': inputs['k']}\n");
        Assert.Equal("ok", r.Status);
        Assert.Equal("ok", r.Result!["outcome"]!.GetValue<string>());
        Assert.Equal("FAKE_SANDBOX", r.Result["result"]!["title"]!.GetValue<string>());
        Assert.Equal(2, r.Result["result"]!["k"]!.GetValue<long>());
        Assert.Equal("reading\n", r.Result["stdout"]!.GetValue<string>());
        Assert.False(r.Result["stdout_truncated"]!.GetValue<bool>());
        Assert.Equal("read_only", r.Result["policy_applied"]!.GetValue<string>());
        Assert.Equal((1, 1), (_host.Guard.Armed, _host.Guard.Disarmed));
        Assert.StartsWith("runs/", r.Result["run_log"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_csharp_query_runs_under_the_same_guard_and_journal()
    {
        using var csharp = new Rift.Scripting.CSharp.CSharpEngine(Rift.Scripting.CSharp.CSharpHostShape.Plain);
        IScriptEngine e = _fixture.Engine;
        var jobs = new JobTable(TimeSpan.FromMinutes(1));
        var journal = new RunJournal(_dir, new RedactionContext("someone", "somehost"), TimeSpan.FromDays(14), FileLog.Null);
        var router = new RequestRouter(
            new IOperationHandler[] { new RunQueryOp(() => e, () => _host, journal, FileLog.Null, () => csharp), new JobOp(jobs) },
            jobs, new Runner(), () => new TargetInfo(1, 1, null), FileLog.Null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(60));
        var args = new JsonObject
        {
            ["script"] = "print(\"reading\"); return new { k = inputs[\"k\"], doc = doc != null };",
            ["language"] = "csharp",
            ["mode"] = "query",
            ["title"] = "cs",
            ["timeout_s"] = 30,
            ["inputs"] = new JsonObject { ["k"] = 2 },
        };
        var body = new JsonObject { ["v"] = "2.0", ["id"] = "r", ["op"] = "run", ["timeout_s"] = 60, ["args"] = args };
        ResponseEnvelope r = router.Handle(Encoding.UTF8.GetBytes(body.ToJsonString()), CancellationToken.None);
        Assert.Equal("ok", r.Status);
        Assert.Equal("csharp", r.Result!["language"]!.GetValue<string>());
        Assert.Equal(2, r.Result["result"]!["k"]!.GetValue<long>());
        Assert.True(r.Result["result"]!["doc"]!.GetValue<bool>());
        Assert.Equal("reading", r.Result["stdout"]!.GetValue<string>().TrimEnd());
        Assert.Equal((1, 1), (_host.Guard.Armed, _host.Guard.Disarmed));

        args["script"] = "System.IO.File.Delete(\"x\"); return 1;";
        ResponseEnvelope refused = router.Handle(Encoding.UTF8.GetBytes(body.ToJsonString()), CancellationToken.None);
        Assert.Equal(ErrorCodes.ScriptError, refused.Error!.Code);
        Assert.StartsWith("CompileError", refused.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disguised_write_is_rolled_back_and_reported_as_write_in_query()
    {
        ResponseEnvelope r = Run(Router(), "doc.Commit()\ndoc.Save()\nresult = 'done'\n");
        Assert.Equal("error", r.Status);
        Assert.Equal(ErrorCodes.WriteInQuery, r.Error!.Code);
        Assert.Contains("rolled back", r.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("doc.", r.Error.Message, StringComparison.Ordinal);   // never script text on the card
        Assert.Equal("rolled_back", r.Result!["outcome"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.WriteInQuery, r.Result["reason"]!.GetValue<string>());
        Assert.Equal(1, r.Result["changes"]!["transactions"]!.GetValue<int>());
        Assert.Contains("save", r.Result["blocked"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(1, _host.Guard.Disarmed);
    }

    [Fact]
    public void A_query_that_opens_a_document_says_the_document_is_still_open()
    {
        ResponseEnvelope r = Run(Router(), "doc.OpenOther('B_SANDBOX')\nresult = 1\n");
        Assert.Equal(ErrorCodes.WriteInQuery, r.Error!.Code);
        Assert.Contains("still open", r.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("everything was rolled back", r.Error.Message, StringComparison.Ordinal);
        Assert.Equal("B_SANDBOX", r.Result!["documents_left_open"]![0]!.GetValue<string>());
        Assert.Contains("open_document", r.Result["blocked"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void A_syntax_error_returns_its_line_and_nothing_runs()
    {
        ResponseEnvelope r = Run(Router(), "x = 1\nif (y := 2):\n    pass\n");
        Assert.Equal(ErrorCodes.ScriptError, r.Error!.Code);
        Assert.Equal(2, r.Result!["error"]!["line"]!.GetValue<int>());
        Assert.Equal(0, _host.Guard.Armed);

        RequestRouter router = Router();
        var body = new JsonObject { ["v"] = "2.0", ["id"] = "c", ["op"] = "compile", ["args"] = new JsonObject { ["script"] = "a = (\n" } };
        ResponseEnvelope c = router.Handle(Encoding.UTF8.GetBytes(body.ToJsonString()), CancellationToken.None);
        Assert.Equal("ok", c.Status);
        Assert.False(c.Result!["ok"]!.GetValue<bool>());
        Assert.True(c.Result["line"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public void A_runtime_error_is_a_script_error_with_type_and_line_only_in_the_message()
    {
        ResponseEnvelope r = Run(Router(), "x = 1\nraise ValueError('secret model value')\n");
        Assert.Equal(ErrorCodes.ScriptError, r.Error!.Code);
        Assert.Equal("ValueError at line 2", r.Error.Message);
        Assert.Contains("secret model value", r.Result!["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_cooperative_timeout_ends_the_run_and_disarms()
    {
        ResponseEnvelope r = Run(Router(), "while True:\n    pass\n", timeoutS: 1);
        Assert.Equal(ErrorCodes.Timeout, r.Error!.Code);
        Assert.Equal("timeout", r.Result!["outcome"]!.GetValue<string>());
        Assert.Equal(1, _host.Guard.Disarmed);
    }

    [Fact]
    public void Import_clr_is_refused_and_named_in_blocked()
    {
        ResponseEnvelope r = Run(Router(), "import clr\n");
        Assert.Equal(ErrorCodes.ScriptError, r.Error!.Code);
        Assert.Contains("import:clr", r.Result!["blocked"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void A_large_result_is_journaled_and_returned_by_reference()
    {
        // 800 strings of 1 KiB: a single 800 KiB string would be cut to 64 Ki characters by the ADR-020 sanitiser.
        ResponseEnvelope r = Run(Router(), "result = ['z' * 1024 for i in range(800)]\n");
        Assert.Equal("ok", r.Status);
        Assert.Null(r.Result!["result"]);
        string reference = r.Result["result_ref"]!.GetValue<string>();
        Assert.StartsWith("runs/", reference, StringComparison.Ordinal);
        Assert.EndsWith("/result.json", reference, StringComparison.Ordinal);
        Assert.Equal(RunQueryOp.PreviewChars, r.Result["preview"]!.GetValue<string>().Length);
        Assert.True(File.Exists(Path.Combine(_dir, reference["runs/".Length..])));
        Assert.True(JsonCodec.Serialize(r).Length < ProtocolVersion.MaxBodyBytes);
    }

    [Fact]
    public void Two_mebibytes_of_stdout_fit_in_one_frame_with_the_rest_journaled()
    {
        ResponseEnvelope r = Run(Router(), "s = 'y' * 1024\nfor i in range(2048):\n    print(s, end='')\nresult = 1\n");
        Assert.Equal("ok", r.Status);
        Assert.True(r.Result!["stdout_truncated"]!.GetValue<bool>());
        Assert.NotNull(r.Result["stdout_ref"]);
        Assert.True(JsonCodec.Serialize(r).Length <= ProtocolVersion.MaxBodyBytes);
        string journaled = File.ReadAllText(Path.Combine(_dir, r.Result["stdout_ref"]!.GetValue<string>()["runs/".Length..]));
        Assert.Equal(BoundedWriter.DefaultMaxBytes, Encoding.UTF8.GetByteCount(journaled));
    }

    [Fact]
    public void Without_a_live_trace_hook_every_run_is_refused_before_the_guard()
    {
        using var dead = new IronPythonEngine(Array.Empty<System.Reflection.Assembly>(), null, BoundedWriter.DefaultMaxBytes, (e, _) => IronPython.Hosting.Python.SetTrace(e, null));
        ResponseEnvelope r = Run(Router(dead), "result = 1\n");
        Assert.Equal(ErrorCodes.TimeoutUnavailable, r.Error!.Code);
        Assert.Equal(0, _host.Guard.Armed);
    }

    [Fact]
    public void An_unknown_mode_is_refused_and_modify_without_expected_document_too()
    {
        ResponseEnvelope r = Run(Router(), "result = 1\n", mode: "delete");
        Assert.Equal("refused", r.Status);
        Assert.Equal(ErrorCodes.BadRequest, r.Error!.Code);
        Assert.Equal(0, _host.Guard.Armed);

        // M2: modify exists, but never without naming the document it is meant for (ADR-009).
        ResponseEnvelope m = Run(Router(), "result = 1\n", mode: "modify");
        Assert.Equal("refused", m.Status);
        Assert.Equal(ErrorCodes.ExpectedDocumentRequired, m.Error!.Code);
        Assert.Equal(0, _host.Guard.Armed);
    }

    [Fact]
    public void An_unexpected_document_is_refused_without_running()
    {
        ResponseEnvelope r = Run(Router(), "result = 1\n", expected: new JsonObject { ["title"] = "OTHER" });
        Assert.Equal(ErrorCodes.DocumentMismatch, r.Error!.Code);
        Assert.Equal(0, _host.Guard.Armed);
        ResponseEnvelope ok = Run(Router(), "result = 1\n", expected: new JsonObject { ["path"] = @"c:\WORK\fake_sandbox.rvt" });
        Assert.Equal("ok", ok.Status);
    }

    [Fact]
    public void A_long_run_is_accepted_then_completed_through_job()
    {
        RequestRouter router = Router(wait: TimeSpan.FromMilliseconds(200));
        var body = new JsonObject
        {
            ["v"] = "2.0", ["id"] = "l", ["op"] = "run",
            ["args"] = new JsonObject { ["script"] = "import time\ntime.sleep(1.5)\nresult = 'late'\n", ["timeout_s"] = 30 },
        };
        // No envelope timeout_s: the router's default pipe wait (200 ms here) applies; the script deadline is args.timeout_s.
        ResponseEnvelope r = router.Handle(Encoding.UTF8.GetBytes(body.ToJsonString()), CancellationToken.None);
        Assert.Equal("accepted", r.Status);
        string jobId = r.Result!["job_id"]!.GetValue<string>();
        var poll = new JsonObject { ["v"] = "2.0", ["id"] = "p", ["op"] = "job", ["args"] = new JsonObject { ["job_id"] = jobId, ["wait_s"] = 30 } };
        ResponseEnvelope done = router.Handle(Encoding.UTF8.GetBytes(poll.ToJsonString()), CancellationToken.None);
        Assert.Equal("ok", done.Status);
        Assert.Equal("late", done.Result!["result"]!["result"]!.GetValue<string>());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

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
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Core.Protocol.Ops;
using Rift.Scripting;
using Rift.Scripting.Runs;

namespace Rift.Tests;

/// <summary>
/// M2 plan tests 2 (run_modify), 4 (history) and 5 (job cancellation) outside Revit: <c>op: run</c> in <c>mode: modify</c>
/// through the router, the real IronPython engine, a fake document whose <c>Add/Change/Delete</c> feed a fake write guard,
/// a fake Keep/Discard dialog and the real run journal.
/// </summary>
public sealed class RunModifyOpTests : IClassFixture<EngineFixture>, IDisposable
{
    private readonly EngineFixture _fixture;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rift-modify-" + Guid.NewGuid().ToString("N"));

    public RunModifyOpTests(EngineFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>What a script sees as <c>doc</c>: Add/Change/Delete stand for element changes, Fail for a Revit failure.</summary>
    public sealed class FakeDocument
    {
        private readonly FakeWriteGuard _guard;

        internal FakeDocument(FakeWriteGuard guard)
        {
            _guard = guard;
        }

        public string Title => "PLAN_SANDBOX";

        public void Add(long id, string category) => _guard.Added.Add((id, category, "new " + id));

        public void Change(long id, string category) => _guard.Modified.Add((id, category, "el " + id));

        public void Delete(long id) => _guard.Deleted.Add(id);

        public void Fail() => _guard.Failure = true;

        public void Save() => _guard.Blocked.Add("save");

        /// <summary>A transaction committed into another open document (scenario 7 of the 2026-10-16 benchmark).</summary>
        public void WriteOther(string title) => _guard.OtherWrites[title] = _guard.OtherWrites.TryGetValue(title, out int n) ? n + 1 : 1;

        public void OpenOther(string title) => _guard.Opened.Add(title);
    }

    public sealed class FakeWriteGuard : IWriteGuard
    {
        public List<(long, string, string?)> Added { get; } = new();

        public List<(long, string, string?)> Modified { get; } = new();

        public List<long> Deleted { get; } = new();

        public List<string> Blocked { get; } = new();

        public bool Failure { get; set; }

        public Dictionary<string, int> OtherWrites { get; } = new();

        public List<string> Opened { get; } = new();

        public IReadOnlyList<OtherDocumentWrite> OtherDocuments => OtherWrites.Select(kv => new OtherDocumentWrite(kv.Key, kv.Value)).ToArray();

        public IReadOnlyList<string> DocumentsOpened => Opened;

        public string? ArmedTitle { get; private set; }

        public bool? KeepAsked { get; private set; }

        public int Armed { get; private set; }

        public ChangeReceipt Changes => ChangeReceipt.From(Added, Modified, Deleted, Added.Count + Modified.Count + Deleted.Count > 0 ? 1 : 0);

        public bool FailureRolledBack => Failure;

        public IReadOnlyList<string> Dialogs { get; } = new List<string>();

        public void Arm(string title)
        {
            Armed++;
            ArmedTitle = title;
        }

        public GuardReport Disarm(IReadOnlyList<object> leftovers, bool keep)
        {
            KeepAsked = keep;
            return new GuardReport(Changes.Total > 0, Changes.Transactions, Blocked.ToArray(), keep ? Array.Empty<string>() : new[] { "target" }, "group", Array.Empty<string>())
            {
                Kept = keep,
                OtherDocumentsWritten = OtherDocuments,
                DocumentsOpened = Opened.ToArray(),
            };
        }
    }

    private sealed class FakeAsk : IAskDialog
    {
        public AskResult Answer { get; set; } = AskResult.Keep;

        public int Asked { get; private set; }

        public AskResult Ask(string title, ChangeReceipt changes)
        {
            Asked++;
            return Answer;
        }
    }

    private sealed class Host : IRunHost
    {
        public Host(string? documentClass, string? path, bool canAsk = true)
        {
            Doc = new FakeDocument(Guard);
            Target = new RunTarget(new object(), new object(), null, Doc, "PLAN_SANDBOX", path, documentClass);
            AskDialog = canAsk ? new FakeAsk() : null;
        }

        public FakeWriteGuard Guard { get; } = new();

        public FakeDocument Doc { get; }

        public FakeAsk? AskDialog { get; }

        public RunTarget Target { get; }

        public IReadOnlyDictionary<string, object?> Extras { get; } = new Dictionary<string, object?>();

        public ForeignConverter? Converter => null;

        public IRunGuard CreateGuard() => throw new InvalidOperationException("query guard not expected");

        public IWriteGuard CreateWriteGuard() => Guard;

        public IAskDialog? Ask => AskDialog;
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
                JobTable.MarkDone(job, job.Work(new Progress<(int, string)>(), job.Token), null);
            }
            catch (OperationException ex)
            {
                JobTable.MarkDone(job, null, new ErrorInfo(ex.Code, null, ex.Message), ex.Refused, ex.Details);
            }
        });

        public string? BusyReason() => null;
    }

    private RunJournal Journal() => new(Path.Combine(_dir, "runs"), new RedactionContext("someone", "somehost"), TimeSpan.FromDays(14), FileLog.Null);

    private (RequestRouter Router, RunJournal Journal) Router(Host host, TimeSpan? wait = null)
    {
        IScriptEngine e = _fixture.Engine;
        var jobs = new JobTable(TimeSpan.FromMinutes(1));
        RunJournal journal = Journal();
        var run = new RunQueryOp(() => e, () => host, journal, FileLog.Null);
        var router = new RequestRouter(
            new IOperationHandler[] { run, new JobOp(jobs), new ToolOp(new IToolHandler[] { new HistoryOp(journal, () => host) }) },
            jobs, new Runner(), () => new TargetInfo(1, 1, null), FileLog.Null, TimeSpan.FromSeconds(10), wait ?? TimeSpan.FromSeconds(60));
        return (router, journal);
    }

    private static ResponseEnvelope Modify(RequestRouter router, string script, JsonObject? extra = null, bool expected = true, int timeoutS = 30, int waitS = 60)
    {
        var args = new JsonObject { ["script"] = script, ["mode"] = "modify", ["title"] = "rename templates", ["timeout_s"] = timeoutS };
        foreach (KeyValuePair<string, JsonNode?> kv in extra ?? new JsonObject())
        {
            args[kv.Key] = kv.Value?.DeepClone();
        }
        var body = new JsonObject
        {
            ["v"] = "2.0", ["id"] = "m", ["op"] = "run", ["timeout_s"] = waitS, ["args"] = args,
            ["expected_document"] = expected ? new JsonObject { ["title"] = "PLAN_SANDBOX" } : null,
        };
        return router.Handle(Encoding.UTF8.GetBytes(body.ToJsonString()), CancellationToken.None);
    }

    private static ResponseEnvelope Tool(RequestRouter router, string name, JsonObject? extra = null)
    {
        var args = new JsonObject { ["name"] = name };
        foreach (KeyValuePair<string, JsonNode?> kv in extra ?? new JsonObject())
        {
            args[kv.Key] = kv.Value?.DeepClone();
        }
        var body = new JsonObject { ["v"] = "2.0", ["id"] = "t", ["op"] = "tool", ["timeout_s"] = 30, ["args"] = args };
        return router.Handle(Encoding.UTF8.GetBytes(body.ToJsonString()), CancellationToken.None);
    }

    private const string ThreeChanges = "doc.Add(10, 'Views')\ndoc.Change(11, 'Views')\ndoc.Delete(12)\nresult = 'renamed'\n";

    // ── (a) (b) refusals before anything runs ─────────────────────────────────────────────────

    [Fact]
    public void Without_expected_document_nothing_is_compiled_or_armed()
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        ResponseEnvelope r = Modify(Router(host).Router, "x = (\n", expected: false);
        Assert.Equal("refused", r.Status);
        Assert.Equal(ErrorCodes.ExpectedDocumentRequired, r.Error!.Code);
        Assert.Equal(0, host.Guard.Armed);
    }

    [Theory]
    [InlineData("unmarked", @"C:\w\PLAN.rvt")]
    [InlineData("reference", @"C:\w\TEMPLATE.rte")]
    public void Outside_a_sandbox_the_run_is_refused_before_the_guard(string documentClass, string path)
    {
        var host = new Host(documentClass, path);
        ResponseEnvelope r = Modify(Router(host).Router, ThreeChanges, new JsonObject { ["dry_run"] = false });
        Assert.Equal("refused", r.Status);
        Assert.Equal(ErrorCodes.SandboxRequired, r.Error!.Code);
        Assert.Equal(documentClass, r.Result!["document_class"]!.GetValue<string>());
        Assert.Equal(0, host.Guard.Armed);
        Assert.DoesNotContain(path, r.Error.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    // ── (c) (d) dry run by default, then a kept run ──────────────────────────────────────────

    [Fact]
    public void By_default_a_modify_is_a_dry_run_rolled_back_with_its_receipt()
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        ResponseEnvelope r = Modify(Router(host).Router, ThreeChanges);
        Assert.Equal("ok", r.Status);
        JsonNode run = r.Result!;
        Assert.Equal("rolled_back", run["outcome"]!.GetValue<string>());
        Assert.Equal("dry_run", run["reason"]!.GetValue<string>());
        Assert.True(run["dry_run"]!.GetValue<bool>());
        Assert.Equal("auto", run["policy_applied"]!.GetValue<string>());
        Assert.Equal((1, 1, 1), (run["changes"]!["added"]!.GetValue<int>(), run["changes"]!["modified"]!.GetValue<int>(), run["changes"]!["deleted"]!.GetValue<int>()));
        Assert.Equal(2, run["changes"]!["by_category"]!["Views"]!["added"]!.GetValue<int>() + run["changes"]!["by_category"]!["Views"]!["modified"]!.GetValue<int>());
        Assert.Equal("renamed", run["result"]!.GetValue<string>());
        Assert.False(host.Guard.KeepAsked);
        Assert.Equal("rename templates", host.Guard.ArmedTitle);
    }

    [Fact]
    public void Not_a_dry_run_on_a_sandbox_with_policy_auto_is_committed_and_journaled_in_the_history()
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        (RequestRouter router, RunJournal journal) = Router(host);
        ResponseEnvelope r = Modify(router, ThreeChanges, new JsonObject { ["dry_run"] = false });
        Assert.Equal("ok", r.Status);
        Assert.Equal("committed", r.Result!["outcome"]!.GetValue<string>());
        Assert.Null(r.Result["reason"]);
        Assert.True(host.Guard.KeepAsked);
        string historyId = r.Result["history_id"]!.GetValue<string>();
        Assert.Equal(r.Result["run_id"]!.GetValue<string>(), historyId);
        Assert.Equal(0, host.AskDialog!.Asked);

        IReadOnlyList<JsonObject> history = journal.ReadHistory(RunJournal.HistoryKey(@"C:\w\PLAN_SANDBOX.rvt"), 10);
        JsonObject line = Assert.Single(history);
        Assert.Equal(historyId, line["history_id"]!.GetValue<string>());
        Assert.Equal("committed", line["outcome"]!.GetValue<string>());
        Assert.Equal(1, line["changes"]!["added"]!.GetValue<int>());
    }

    // ── F-R15 / F-R14: writes elsewhere are never reported as committed ──────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_run_that_wrote_into_another_document_is_rolled_back_entirely_and_says_so(bool dryRun)
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        (RequestRouter router, RunJournal journal) = Router(host);
        ResponseEnvelope r = Modify(router, "doc.WriteOther('B_SANDBOX')\ndoc.WriteOther('B_SANDBOX')\nresult = 'done'\n", new JsonObject { ["dry_run"] = dryRun });
        Assert.Equal("error", r.Status);
        Assert.Equal(ErrorCodes.OtherDocumentWrite, r.Error!.Code);
        JsonNode run = r.Result!;
        Assert.Equal("rolled_back", run["outcome"]!.GetValue<string>());
        Assert.Equal(RunQueryOp.ReasonOtherDocumentWrite, run["reason"]!.GetValue<string>());
        JsonNode other = Assert.Single(run["other_documents"]!.AsArray())!;
        Assert.Equal("B_SANDBOX", other["title"]!.GetValue<string>());
        Assert.Equal(2, other["transactions"]!.GetValue<int>());
        Assert.False(host.Guard.KeepAsked);

        JsonObject line = Assert.Single(journal.ReadHistory(RunJournal.HistoryKey(@"C:\w\PLAN_SANDBOX.rvt"), 10));
        Assert.Equal("rolled_back", line["outcome"]!.GetValue<string>());
        Assert.Equal(RunQueryOp.ReasonOtherDocumentWrite, line["reason"]!.GetValue<string>());
        Assert.Equal("B_SANDBOX", line["other_documents"]![0]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void A_run_that_opened_a_document_is_not_kept_and_names_the_document_left_open()
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        ResponseEnvelope r = Modify(Router(host).Router, ThreeChanges + "doc.OpenOther('C')\n", new JsonObject { ["dry_run"] = false });
        Assert.Equal(ErrorCodes.OtherDocumentWrite, r.Error!.Code);
        Assert.Equal(RunQueryOp.ReasonDocumentOpened, r.Result!["reason"]!.GetValue<string>());
        Assert.Equal("C", r.Result["documents_left_open"]![0]!.GetValue<string>());
        Assert.Contains("still open", r.Error.Message, StringComparison.Ordinal);
        Assert.False(host.Guard.KeepAsked);
    }

    // ── (e) (f) (j) policy ask ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AskResult.Keep, "committed", null)]
    [InlineData(AskResult.Discard, "rolled_back", "user_discarded")]
    [InlineData(AskResult.Failed, "rolled_back", "ask_failed")]
    public void Policy_ask_keeps_or_discards_as_the_user_answers(AskResult answer, string outcome, string? reason)
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        host.AskDialog!.Answer = answer;
        ResponseEnvelope r = Modify(Router(host).Router, ThreeChanges, new JsonObject { ["dry_run"] = false, ["policy"] = "ask" });
        Assert.Equal(outcome, r.Result!["outcome"]!.GetValue<string>());
        Assert.Equal(reason, r.Result["reason"]?.GetValue<string>());
        Assert.Equal(1, host.AskDialog.Asked);
        Assert.Equal(answer == AskResult.Keep, host.Guard.KeepAsked);
    }

    [Fact]
    public void An_unsaved_document_forces_ask_even_when_auto_is_requested()
    {
        var host = new Host("unsaved", null);
        host.AskDialog!.Answer = AskResult.Discard;
        ResponseEnvelope r = Modify(Router(host).Router, ThreeChanges, new JsonObject { ["dry_run"] = false });
        Assert.Equal("ask", r.Result!["policy_applied"]!.GetValue<string>());
        Assert.Equal("unsaved_document", r.Result["policy_reason"]!.GetValue<string>());
        Assert.Equal(1, host.AskDialog.Asked);
        Assert.Equal("user_discarded", r.Result["reason"]!.GetValue<string>());
    }

    [Fact]
    public void A_reference_document_with_allow_reference_goes_through_keep_or_discard()
    {
        var host = new Host("reference", @"C:\w\TEMPLATE.rte");
        ResponseEnvelope r = Modify(Router(host).Router, ThreeChanges, new JsonObject { ["dry_run"] = false, ["allow_reference"] = true });
        Assert.Equal("committed", r.Result!["outcome"]!.GetValue<string>());
        Assert.Equal("ask", r.Result["policy_applied"]!.GetValue<string>());
        Assert.Equal(1, host.AskDialog!.Asked);
    }

    [Fact]
    public void Policy_ask_without_a_dialog_is_refused_before_the_guard()
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt", canAsk: false);
        ResponseEnvelope r = Modify(Router(host).Router, ThreeChanges, new JsonObject { ["dry_run"] = false, ["policy"] = "ask" });
        Assert.Equal(ErrorCodes.BadRequest, r.Error!.Code);
        Assert.Equal(0, host.Guard.Armed);
    }

    [Fact]
    public void A_dry_run_never_asks_even_on_an_unsaved_document()
    {
        var host = new Host("unsaved", null);
        ResponseEnvelope r = Modify(Router(host).Router, ThreeChanges);
        Assert.Equal("dry_run", r.Result!["reason"]!.GetValue<string>());
        Assert.Equal(0, host.AskDialog!.Asked);
    }

    // ── (g) (h) (i) failures roll everything back ────────────────────────────────────────────

    [Fact]
    public void A_script_error_rolls_back_and_says_so()
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        ResponseEnvelope r = Modify(Router(host).Router, "doc.Add(1, 'Views')\nraise ValueError('boom')\n", new JsonObject { ["dry_run"] = false });
        Assert.Equal(ErrorCodes.ScriptError, r.Error!.Code);
        Assert.Contains("rolled back", r.Error.Message, StringComparison.Ordinal);
        Assert.Equal("error", r.Result!["outcome"]!.GetValue<string>());
        Assert.False(host.Guard.KeepAsked);
        Assert.Equal(1, r.Result["changes"]!["added"]!.GetValue<int>());
    }

    [Fact]
    public void A_timeout_rolls_back()
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        ResponseEnvelope r = Modify(Router(host).Router, "doc.Add(1, 'Views')\nwhile True:\n    pass\n", new JsonObject { ["dry_run"] = false }, timeoutS: 1);
        Assert.Equal(ErrorCodes.Timeout, r.Error!.Code);
        Assert.False(host.Guard.KeepAsked);
    }

    [Fact]
    public void A_revit_failure_condemns_the_run_even_without_dry_run()
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        ResponseEnvelope r = Modify(Router(host).Router, "doc.Add(1, 'Views')\ndoc.Fail()\nresult = 1\n", new JsonObject { ["dry_run"] = false });
        Assert.Equal("ok", r.Status);
        Assert.Equal("rolled_back", r.Result!["outcome"]!.GetValue<string>());
        Assert.Equal("revit_failure", r.Result["reason"]!.GetValue<string>());
        Assert.True(r.Result["failures"]!.GetValue<bool>());
        Assert.False(host.Guard.KeepAsked);
    }

    // ── history ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void History_lists_the_receipts_newest_first_and_record_attaches_a_reason()
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        RequestRouter router = Router(host).Router;
        Assert.Equal(ErrorCodes.BadRequest, Tool(router, "history.record", new JsonObject { ["reason"] = "x" }).Error!.Code);

        string first = Modify(router, ThreeChanges).Result!["history_id"]!.GetValue<string>();
        string second = Modify(router, ThreeChanges, new JsonObject { ["dry_run"] = false }).Result!["history_id"]!.GetValue<string>();

        ResponseEnvelope list = Tool(router, "history.list");
        Assert.Equal("ok", list.Status);
        string[] ids = list.Result!["records"]!.AsArray().Select(n => n!["history_id"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { second, first }, ids);

        ResponseEnvelope rec = Tool(router, "history.record", new JsonObject { ["reason"] = "user asked to rename someone's templates" });
        Assert.Equal("ok", rec.Status);
        Assert.Equal(second, rec.Result!["record"]!["history_id"]!.GetValue<string>());
        Assert.Equal("user asked to rename <USER>'s templates", rec.Result["record"]!["reason_given"]!.GetValue<string>());

        ResponseEnvelope recFirst = Tool(router, "history.record", new JsonObject { ["reason"] = "trial", ["history_id"] = first });
        Assert.Equal(first, recFirst.Result!["record"]!["history_id"]!.GetValue<string>());
        JsonArray records = Tool(router, "history.list", new JsonObject { ["limit"] = 1 }).Result!["records"]!.AsArray();
        Assert.Single(records);
        Assert.Contains("<USER>", records[0]!["reason_given"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Equal(ErrorCodes.BadRequest, Tool(router, "history.record", new JsonObject { ["reason"] = "x", ["history_id"] = "nope" }).Error!.Code);
        Assert.Equal(ErrorCodes.BadRequest, Tool(router, "history.record").Error!.Code);
        Assert.Equal(ErrorCodes.BadRequest, Tool(router, "history.nope").Error!.Code);
    }

    [Fact]
    public void The_history_key_never_shows_the_path()
    {
        string key = RunJournal.HistoryKey(@"D:\projects\someone\PLAN_SANDBOX.rvt");
        Assert.Matches("^[0-9a-f]{16}$", key);
        Assert.Equal(key, RunJournal.HistoryKey(@"d:\PROJECTS\SOMEONE\plan_sandbox.RVT"));
    }

    // ── job cancellation ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cancelling_a_running_modify_rolls_it_back_with_cancelled()
    {
        var host = new Host("sandbox", @"C:\w\PLAN_SANDBOX.rvt");
        RequestRouter router = Router(host, wait: TimeSpan.FromMilliseconds(500)).Router;
        ResponseEnvelope accepted = Modify(router, "doc.Add(1, 'Views')\nwhile True:\n    pass\n", new JsonObject { ["dry_run"] = false }, timeoutS: 60, waitS: 1);
        Assert.Equal("accepted", accepted.Status);
        string jobId = accepted.Result!["job_id"]!.GetValue<string>();

        ResponseEnvelope cancel = Job(router, new JsonObject { ["job_id"] = jobId, ["cancel"] = true });
        Assert.Equal("ok", cancel.Status);
        Assert.Equal("requested", cancel.Result!["cancel"]!.GetValue<string>());

        ResponseEnvelope done = Job(router, new JsonObject { ["job_id"] = jobId, ["wait_s"] = 20 });
        Assert.Equal(ErrorCodes.Cancelled, done.Error!.Code);
        Assert.Equal("cancelled", done.Result!["reason"]!.GetValue<string>());
        Assert.Equal("rolled_back", done.Result["outcome"]!.GetValue<string>());
        Assert.False(host.Guard.KeepAsked);

        ResponseEnvelope again = Job(router, new JsonObject { ["job_id"] = jobId, ["cancel"] = true });
        Assert.Equal("finished", again.Result!["cancel"]!.GetValue<string>());
    }

    [Fact]
    public void Cancelling_a_queued_job_ends_it_without_running()
    {
        var jobs = new JobTable(TimeSpan.FromMinutes(1));
        int ran = 0;
        Job job = jobs.Create("run", (_, _) =>
        {
            ran++;
            return null;
        });
        Assert.Equal(CancelOutcome.Cancelled, JobTable.TryCancel(job, new ErrorInfo(ErrorCodes.Cancelled)));
        Assert.False(JobTable.MarkStarted(job));
        Assert.Equal(0, ran);
        Assert.Equal(ErrorCodes.Cancelled, job.Error!.Code);
    }

    private static ResponseEnvelope Job(RequestRouter router, JsonObject args)
    {
        var body = new JsonObject { ["v"] = "2.0", ["id"] = "j", ["op"] = "job", ["timeout_s"] = 30, ["args"] = args };
        return router.Handle(Encoding.UTF8.GetBytes(body.ToJsonString()), CancellationToken.None);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

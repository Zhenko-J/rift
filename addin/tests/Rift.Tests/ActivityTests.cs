// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Xunit;

using Rift.Core.Activity;
using Rift.Core.Jobs;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Ui;
using Rift.Ui.Windows;

namespace Rift.Tests;

/// <summary>UI work stream, tranches 3 to 6 (plan tests 5 to 8, 11 to 14, 17): the activity log, History, Status and Tools.</summary>
public sealed class ActivityTests
{
    private sealed class Handler : IOperationHandler
    {
        private readonly Func<RequestEnvelope, JsonNode?> _fn;

        public Handler(string op, bool api, Func<RequestEnvelope, JsonNode?> fn)
        {
            Op = op;
            NeedsApiThread = api;
            _fn = fn;
        }

        public string Op { get; }

        public bool NeedsApiThread { get; }

        public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation) => _fn(request);
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

    private static RequestRecord Rec(string key, string status, string? code = null) =>
        new(key, DateTimeOffset.UtcNow, "run_query", status, code, code is null ? null : "message", 12, null, null);

    private static ResponseEnvelope Send(RequestRouter router, string op, string tool, int timeout = 5) =>
        router.Handle(Encoding.UTF8.GetBytes("{\"v\":\"2.0\",\"id\":\"x\",\"op\":\"" + op + "\",\"timeout_s\":" + timeout
            + ",\"client\":{\"tool\":\"" + tool + "\"}}"), CancellationToken.None);

    [Fact]
    public void A_key_is_one_line_updated_in_place_and_never_set_back_to_running()
    {
        var log = new ActivityLog();
        log.Upsert(Rec("j1", "accepted"));
        log.Upsert(Rec("r1", "ok"));
        log.Upsert(Rec("j1", "ok"));
        log.Upsert(Rec("j1", "accepted"));
        ActivitySnapshot s = log.Snapshot();
        Assert.Equal(2, s.Records.Count);
        Assert.Equal(2, s.Total);
        Assert.Equal("ok", s.Records[0].Status);
        Assert.Equal(2, s.Succeeded);
        Assert.Equal(0, s.Running);
    }

    [Fact]
    public void The_ring_keeps_the_last_lines_and_counts_every_request()
    {
        var log = new ActivityLog(3);
        for (int i = 0; i < 5; i++)
        {
            log.Upsert(Rec("r" + i, i == 4 ? "refused" : "ok", i == 4 ? ErrorCodes.LeaseHeld : null));
        }
        ActivitySnapshot s = log.Snapshot();
        Assert.Equal(new[] { "r2", "r3", "r4" }, s.Records.Select(r => r.Key));
        Assert.Equal(5, s.Total);
        Assert.Equal(1, s.Failed);
        Assert.Contains("last 3 kept", HistoryView.Summary(s), StringComparison.Ordinal);
        Assert.StartsWith("u", log.NextKey(), StringComparison.Ordinal);
    }

    [Fact]
    public void Change_receipts_are_summed_by_category_and_bounded()
    {
        var result = JsonNode.Parse("{\"outcome\":\"kept\",\"changes\":{\"by_category\":{\"Walls\":{\"added\":3,\"modified\":1},\"Doors\":{\"deleted\":2},\"Bad\":7}}}");
        ChangeSummary? c = ChangeSummary.From(result);
        Assert.NotNull(c);
        Assert.Equal(2, c!.Categories.Count);
        Assert.Equal("3 added, 1 modified, 2 deleted", c.Text());
        Assert.Null(ChangeSummary.From(JsonNode.Parse("{\"rows\":[]}")));
        Assert.Equal("no change", new ChangeSummary(Array.Empty<CategoryChange>()).Text());

        HistoryRow row = HistoryView.Row(new RequestRecord("r1", DateTimeOffset.UtcNow, "run_modify", "ok", null, null, 2400, "dry_run", c));
        Assert.Equal("dry run", row.Result);
        Assert.Equal("2.4 s", row.Duration);
        Assert.Contains("Walls  +3  ~1  -0", row.ChangeDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_router_records_each_request_once_and_never_the_polling()
    {
        var records = new ConcurrentQueue<RequestRecord>();
        var router = new RequestRouter(
            new IOperationHandler[]
            {
                new Handler("context", false, _ => new JsonObject()),
                new Handler("job", false, _ => new JsonObject()),
                new Handler("run", true, _ => new JsonObject { ["outcome"] = "kept", ["changes"] = new JsonObject { ["by_category"] = new JsonObject { ["Walls"] = new JsonObject { ["added"] = 1 } } } }),
            },
            new JobTable(TimeSpan.FromMinutes(1)), new Runner(), () => new TargetInfo(1, 1, null), FileLog.Null);
        router.Recorded += records.Enqueue;

        Send(router, "context", "get_context");
        Send(router, "job", "job");
        Send(router, "run", "run_modify");

        Assert.Equal(2, records.Count);
        RequestRecord[] all = records.ToArray();
        Assert.Equal("get_context", all[0].Label);
        Assert.Equal("run_modify", all[1].Label);
        Assert.Equal("kept", all[1].Outcome);
        Assert.Equal(1, all[1].Changes!.Added);
        Assert.NotEqual(all[0].Key, all[1].Key);
    }

    [Fact]
    public void A_long_job_is_recorded_as_accepted_then_finished_under_the_same_key()
    {
        using var gate = new ManualResetEventSlim();
        var log = new ActivityLog();
        var router = new RequestRouter(
            new IOperationHandler[] { new Handler("run", true, _ => { gate.Wait(TimeSpan.FromSeconds(10)); return new JsonObject(); }) },
            new JobTable(TimeSpan.FromMinutes(1)), new Runner(), () => new TargetInfo(1, 1, null), FileLog.Null,
            defaultTimeout: TimeSpan.FromMilliseconds(100));
        router.Recorded += log.Upsert;

        ResponseEnvelope r = router.Handle(Encoding.UTF8.GetBytes("{\"v\":\"2.0\",\"id\":\"x\",\"op\":\"run\",\"client\":{\"tool\":\"run_query\"}}"), CancellationToken.None);
        Assert.Equal(ResponseEnvelope.Accepted, r.Status);
        Assert.Equal(1, log.Snapshot().Running);
        Assert.Equal(PastilleState.Running, HistoryView.StateOf(log.Snapshot().Records[0]));

        gate.Set();
        SpinWait.SpinUntil(() => log.Snapshot().Running == 0, TimeSpan.FromSeconds(5));
        ActivitySnapshot s = log.Snapshot();
        Assert.Single(s.Records);
        Assert.Equal(r.Result!["job_id"]!.GetValue<string>(), s.Records[0].Key);
        Assert.Equal("ok", s.Records[0].Status);
    }

    [Fact]
    public void History_formats_states_durations_and_order()
    {
        Assert.Equal(PastilleState.Paused, HistoryView.StateOf(Rec("a", "refused", ErrorCodes.Paused)));
        Assert.Equal(PastilleState.Failed, HistoryView.StateOf(Rec("a", "error", ErrorCodes.Internal)));
        Assert.Equal("paused", HistoryView.Row(Rec("a", "refused", ErrorCodes.Paused)).Result);
        Assert.Equal("340 ms", HistoryView.Duration(340));
        Assert.Equal("3 min 05 s", HistoryView.Duration(185_000));

        var log = new ActivityLog();
        for (int i = 0; i < 60; i++)
        {
            log.Upsert(Rec("r" + i, "ok"));
        }
        ActivitySnapshot s = log.Snapshot();
        Assert.Equal(HistoryView.GridMax, HistoryView.Grid(s).Count);
        Assert.Equal("r59", HistoryView.Rows(s)[0].Key);
    }

    [Fact]
    public void The_embedded_catalogue_is_the_pamphlet_file()
    {
        ToolCatalog catalog = ToolCatalog.Load();
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "docs", "pamphlet", "tools.json"));
        JsonArray tools = JsonNode.Parse(System.IO.File.ReadAllText(path))!["tools"]!.AsArray();
        Assert.Equal(tools.Count, catalog.Tools.Count);
        Assert.Equal(tools.Count(t => t!["status"]!.GetValue<string>() == "implemented"), catalog.Shipped);
        Assert.Equal("auto", catalog.Risks[0]);
        Assert.All(catalog.Filter("auto"), t => Assert.Equal("auto", t.Risk));
        Assert.True(catalog.Filter(null)[0].Shipped);
        Assert.Same(ToolCatalog.Empty, ToolCatalog.Parse("{"));
    }

    private sealed class FakeHost : IUiHost
    {
        public ActivityLog Log { get; } = new();

        public bool Paused { get; private set; }

        public bool Card { get; private set; } = true;

        public StatusSnapshot Status() => new("1.2.3", Paused, Paused ? DateTimeOffset.UtcNow : null, "rift-1", 7, "claude-code 2.0",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(60), 1, null, "IronPython 3.4", Card, "C:\\logs\\addin.log", null);

        public ActivitySnapshot Activity() => Log.Snapshot();

        public void TogglePause() => Paused = !Paused;

        public void SetActivityCard(bool enabled) => Card = enabled;

        public (Rift.Core.Control.WorkScope, Rift.Core.Control.Supervision)? Chosen { get; private set; }

        public bool ChooseSession(Rift.Core.Control.WorkScope scope, Rift.Core.Control.Supervision supervision)
        {
            Chosen = (scope, supervision);
            return true;
        }

        public void OpenSession()
        {
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException("on STA: " + failure);
        }
    }

    [Fact]
    public void The_windows_build_their_body_without_Revit()
    {
        OnSta(() =>
        {
            var host = new FakeHost();
            host.Log.Upsert(Rec("r1", "ok"));
            host.Log.Upsert(Rec("r2", "error", ErrorCodes.Internal));
            foreach (RiftWindowProbe w in new[]
            {
                new RiftWindowProbe(new StatusWindow(host, IntPtr.Zero)),
                new RiftWindowProbe(new HistoryWindow(host, IntPtr.Zero)),
                new RiftWindowProbe(new ToolsWindow(ToolCatalog.Load(), IntPtr.Zero)),
                new RiftWindowProbe(new SessionWindow(host, new SessionQuestion("claude-code 2.0", "PLAN", "unmarked", null), IntPtr.Zero)),
                new RiftWindowProbe(new SessionWindow(host, new SessionQuestion("claude-code 2.0", null, null, null), IntPtr.Zero)),
            })
            {
                Assert.StartsWith("RIFT · ", w.Window.Title, StringComparison.Ordinal);
                Assert.NotNull(w.Body());
                w.Window.Close();
            }
        });
    }

    /// <summary>Calls the protected Build of a window through reflection (the windows are never shown in tests).</summary>
    private sealed class RiftWindowProbe
    {
        public RiftWindowProbe(RiftWindow window)
        {
            Window = window;
        }

        public RiftWindow Window { get; }

        public UIElement? Body() => (UIElement?)typeof(RiftWindow).GetMethod("Build", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(Window, null);
    }
}

// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

using Rift.Core.Jobs;
using Rift.Core.Logging;
using Rift.Core.Protocol;

namespace Rift.Tests;

/// <summary>Plan M1, test 8: envelope validation, pipe-thread versus API-thread routing, accepted/job.</summary>
public sealed class RequestRouterTests
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

    /// <summary>Runs jobs on a thread pool thread after an optional delay (a fake Revit API thread).</summary>
    private sealed class FakeRunner : IJobRunner
    {
        private readonly TimeSpan _startDelay;

        public FakeRunner(TimeSpan startDelay)
        {
            _startDelay = startDelay;
        }

        public int Enqueued { get; private set; }

        public void Enqueue(Job job)
        {
            Enqueued++;
            _ = Task.Run(async () =>
            {
                await Task.Delay(_startDelay);
                if (!JobTable.MarkStarted(job))
                {
                    return;
                }
                try
                {
                    JsonNode? r = job.Work(new Progress<(int, string)>(), CancellationToken.None);
                    JobTable.MarkDone(job, r, null);
                }
                catch (OperationException ex)
                {
                    JobTable.MarkDone(job, null, new ErrorInfo(ex.Code, null, ex.Message), ex.Refused, ex.Details);
                }
            });
        }

        public string? BusyReason() => "fake modal dialog";
    }

    private static RequestRouter Router(FakeRunner runner, TimeSpan? start = null, TimeSpan? timeout = null, params IOperationHandler[] handlers) =>
        new(handlers, new JobTable(TimeSpan.FromMinutes(1)), runner, () => new TargetInfo(42, 1, null), FileLog.Null, start, timeout);

    private static ResponseEnvelope Send(RequestRouter router, string json) => router.Handle(Encoding.UTF8.GetBytes(json), CancellationToken.None);

    [Fact]
    public void Protocol_major_mismatch_and_unknown_op_are_errors()
    {
        var runner = new FakeRunner(TimeSpan.Zero);
        RequestRouter router = Router(runner, null, null, new Handler("ping", false, _ => new JsonObject { ["pong"] = true }));

        // A protocol 1.x client (before ADR-028) is refused.
        ResponseEnvelope r1 = Send(router, "{\"v\":\"1.0\",\"id\":\"a\",\"op\":\"ping\"}");
        Assert.Equal("error", r1.Status);
        Assert.Equal(ErrorCodes.ProtocolMismatch, r1.Error!.Code);

        ResponseEnvelope r2 = Send(router, "{\"v\":\"2.7\",\"id\":\"b\",\"op\":\"nope\"}");
        Assert.Equal(ErrorCodes.BadRequest, r2.Error!.Code);
        Assert.Equal("b", r2.Id);

        ResponseEnvelope r3 = Send(router, "not json");
        Assert.Equal(ErrorCodes.BadRequest, r3.Error!.Code);

        ResponseEnvelope ok = Send(router, "{\"v\":\"2.0\",\"id\":\"c\",\"op\":\"ping\"}");
        Assert.Equal("ok", ok.Status);
        Assert.True(ok.Result!["pong"]!.GetValue<bool>());
        Assert.Equal(42, ok.Target!.Pid);
        Assert.Equal(0, runner.Enqueued);
    }

    [Fact]
    public void Api_thread_ops_are_queued_and_awaited()
    {
        var runner = new FakeRunner(TimeSpan.FromMilliseconds(50));
        RequestRouter router = Router(runner, null, null, new Handler("context", true, _ => new JsonObject { ["doc"] = "Sandbox" }));
        ResponseEnvelope r = Send(router, "{\"v\":\"2.0\",\"id\":\"d\",\"op\":\"context\",\"timeout_s\":5}");
        Assert.Equal("ok", r.Status);
        Assert.Equal("Sandbox", r.Result!["doc"]!.GetValue<string>());
        Assert.Equal(1, runner.Enqueued);
    }

    [Fact]
    public void Job_not_started_in_time_is_revit_busy_with_reason()
    {
        var runner = new FakeRunner(TimeSpan.FromSeconds(2));
        RequestRouter router = Router(runner, TimeSpan.FromMilliseconds(100), null, new Handler("context", true, _ => null));
        ResponseEnvelope r = Send(router, "{\"v\":\"2.0\",\"id\":\"e\",\"op\":\"context\"}");
        Assert.Equal(ErrorCodes.RevitBusy, r.Error!.Code);
        Assert.Equal("fake modal dialog", r.Error.Message);
    }

    [Fact]
    public void Long_job_is_accepted_then_found_by_job_op()
    {
        var runner = new FakeRunner(TimeSpan.Zero);
        var jobs = new JobTable(TimeSpan.FromMinutes(1));
        var router = new RequestRouter(
            new IOperationHandler[] { new Handler("run", true, _ => { Thread.Sleep(300); return new JsonObject { ["done"] = true }; }), new Rift.Core.Protocol.Ops.JobOp(jobs) },
            jobs, runner, () => new TargetInfo(1, 1, null), FileLog.Null, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50));
        ResponseEnvelope r = Send(router, "{\"v\":\"2.0\",\"id\":\"f\",\"op\":\"run\"}");
        Assert.Equal("accepted", r.Status);
        string jobId = r.Result!["job_id"]!.GetValue<string>();

        ResponseEnvelope polled = Send(router, "{\"v\":\"2.0\",\"id\":\"g\",\"op\":\"job\",\"args\":{\"job_id\":\"" + jobId + "\",\"wait_s\":5}}");
        Assert.Equal("ok", polled.Status);
        Assert.Equal("done", polled.Result!["state"]!.GetValue<string>());
        Assert.True(polled.Result!["result"]!["done"]!.GetValue<bool>());

        ResponseEnvelope missing = Send(router, "{\"v\":\"2.0\",\"id\":\"h\",\"op\":\"job\",\"args\":{\"job_id\":\"nope\"}}");
        Assert.Equal(ErrorCodes.JobNotFound, missing.Error!.Code);
    }

    [Fact]
    public void Refusals_keep_their_details()
    {
        var runner = new FakeRunner(TimeSpan.Zero);
        RequestRouter router = Router(runner, null, null,
            new Handler("lease", false, _ => throw new OperationException(ErrorCodes.LeaseHeld, "held", new JsonObject { ["holder"] = "other" }, refused: true)));
        ResponseEnvelope r = Send(router, "{\"v\":\"2.0\",\"id\":\"i\",\"op\":\"lease\"}");
        Assert.Equal("refused", r.Status);
        Assert.Equal(ErrorCodes.LeaseHeld, r.Error!.Code);
        Assert.Equal("other", r.Result!["holder"]!.GetValue<string>());
    }
    [Fact]
    public void Completed_reports_the_mcp_tool_name_and_the_error_message()
    {
        var runner = new FakeRunner(TimeSpan.Zero);
        RequestRouter router = Router(runner, null, null,
            new Handler("ping", false, _ => new JsonObject()),
            new Handler("lease", false, _ => throw new OperationException(ErrorCodes.LeaseHeld, "held by other", null, refused: true)));
        var seen = new System.Collections.Generic.List<(string Label, string Status, string? Code, string? Message)>();
        router.Completed += (label, status, code, message) => seen.Add((label, status, code, message));

        Send(router, "{\"v\":\"2.0\",\"id\":\"a\",\"op\":\"ping\",\"client\":{\"session_id\":\"s\",\"tool\":\"get_context\"}}");
        Send(router, "{\"v\":\"2.0\",\"id\":\"b\",\"op\":\"ping\"}");
        Send(router, "{\"v\":\"2.0\",\"id\":\"c\",\"op\":\"lease\",\"client\":{\"tool\":\"" + new string('x', 100) + "\"}}");

        Assert.Equal(("get_context", "ok", (string?)null, (string?)null), seen[0]);
        Assert.Equal("ping", seen[1].Label);
        Assert.Equal(64, seen[2].Label.Length);
        Assert.Equal(("refused", ErrorCodes.LeaseHeld, "held by other"), (seen[2].Status, seen[2].Code, seen[2].Message));
    }
}

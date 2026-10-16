// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using Xunit;

using Rift.Core.Control;
using Rift.Core.Jobs;
using Rift.Core.Lease;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Core.Protocol.Ops;

namespace Rift.Tests;

/// <summary>UI work stream, tranche 1 (plan tests 1-4): the pause gate, its effect on the router, the lease and the queued jobs.</summary>
public sealed class PauseGateTests
{
    private sealed class Counting : IOperationHandler
    {
        public Counting(string op, bool api)
        {
            Op = op;
            NeedsApiThread = api;
        }

        public string Op { get; }

        public bool NeedsApiThread { get; }

        public int Calls { get; private set; }

        public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
        {
            Calls++;
            return new JsonObject { ["op"] = Op };
        }
    }

    /// <summary>Runs every job at once on the calling thread (a responsive Revit), and counts what it was given.</summary>
    private sealed class InlineRunner : IJobRunner
    {
        public int Enqueued { get; private set; }

        public void Enqueue(Job job)
        {
            Enqueued++;
            if (JobTable.MarkStarted(job))
            {
                JobTable.MarkDone(job, job.Work(new Progress<(int, string)>(), CancellationToken.None), null);
            }
        }

        public string? BusyReason() => null;
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    private static (PauseGate Gate, LeaseManager Lease, JobTable Jobs) NewGate()
    {
        var lease = new LeaseManager(TimeSpan.FromSeconds(60), () => T0);
        var jobs = new JobTable(TimeSpan.FromMinutes(1));
        return (new PauseGate(lease, jobs, () => T0), lease, jobs);
    }

    private static ResponseEnvelope Send(RequestRouter router, string op, string session = "s1") =>
        router.Handle(Encoding.UTF8.GetBytes("{\"v\":\"2.0\",\"id\":\"x\",\"op\":\"" + op + "\",\"client\":{\"session_id\":\"" + session + "\",\"name\":\"test\",\"version\":\"1\"}}"), CancellationToken.None);

    // ── 1 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pause_revokes_the_lease_cancels_queued_jobs_only_and_resume_runs_again()
    {
        (PauseGate gate, LeaseManager lease, JobTable jobs) = NewGate();
        Assert.True(lease.Acquire(new LeaseHolder("s1", "test", "1", T0)).Granted);
        Job queued = jobs.Create("run", (_, _) => throw new InvalidOperationException("must never run"));
        Job running = jobs.Create("run", (_, _) => null);
        Assert.True(JobTable.MarkStarted(running));
        int changes = 0;
        gate.Changed += _ => changes++;

        Assert.Equal(1, gate.Pause("you"));
        Assert.Equal(0, gate.Pause("you"));

        Assert.True(gate.IsPaused);
        Assert.Equal("you", gate.State.By);
        Assert.Equal(T0, gate.State.SinceUtc);
        Assert.Null(lease.Holder);
        Assert.Equal(JobState.Done, queued.State);
        Assert.Equal(ErrorCodes.Paused, queued.Error!.Code);
        Assert.True(queued.Refused);
        Assert.Equal(JobState.Running, running.State);
        Assert.Equal(1, changes);

        gate.Resume();
        gate.Resume();
        Assert.False(gate.IsPaused);
        Assert.Equal(2, changes);
        Assert.Equal(ServerState.Paused, gate.Toggle("you").State);
        Assert.Equal(ServerState.Running, gate.Toggle("you").State);
    }

    // ── 2 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Paused_refuses_everything_before_lease()
    {
        (PauseGate gate, LeaseManager lease, JobTable jobs) = NewGate();
        var context = new Counting("context", api: true);
        var compile = new Counting("compile", api: false);
        var runner = new InlineRunner();
        var router = new RequestRouter(new IOperationHandler[] { new LeaseOp(lease), context, compile }, jobs, runner,
            () => new TargetInfo(7, 1, null), FileLog.Null, lease: lease, pause: gate);
        gate.Pause("you");

        foreach (string op in new[] { "lease", "context", "compile", "nope" })
        {
            ResponseEnvelope r = Send(router, op);
            Assert.Equal(ResponseEnvelope.Refused, r.Status);
            Assert.Equal(ErrorCodes.Paused, r.Error!.Code);
            Assert.Equal(PauseGate.Message, r.Error.Message);
            Assert.Equal(T0, DateTimeOffset.Parse(r.Result!["paused_since_utc"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
        }
        Assert.Equal(0, context.Calls + compile.Calls);
        Assert.Equal(0, runner.Enqueued);
        Assert.Equal(0, jobs.Count);
        Assert.Null(lease.Holder);
    }

    // ── 3 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Resume_admits_again()
    {
        (PauseGate gate, LeaseManager lease, JobTable jobs) = NewGate();
        var context = new Counting("context", api: true);
        var router = new RequestRouter(new IOperationHandler[] { context }, jobs, new InlineRunner(),
            () => new TargetInfo(7, 1, null), FileLog.Null, lease: lease, pause: gate);
        Assert.Equal("ok", Send(router, "context").Status);
        Assert.Equal("s1", lease.Holder!.ClientId);

        gate.Pause("you");
        Assert.Equal(ErrorCodes.Paused, Send(router, "context").Error!.Code);
        Assert.Null(lease.Holder);

        gate.Resume();
        ResponseEnvelope again = Send(router, "context", session: "s2");
        Assert.Equal("ok", again.Status);
        Assert.Equal("s2", lease.Holder!.ClientId);
        Assert.Equal(2, context.Calls);
    }

    // ── 4 ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Cancel_not_started_cancels_queued_jobs_and_raises_finished_for_each()
    {
        var jobs = new JobTable(TimeSpan.FromMinutes(1));
        Job a = jobs.Create("run", (_, _) => null);
        Job b = jobs.Create("run", (_, _) => null);
        Job started = jobs.Create("run", (_, _) => null);
        Assert.True(JobTable.MarkStarted(started));
        Job done = jobs.Create("run", (_, _) => null);
        JobTable.MarkDone(done, null, null);
        int finished = 0;
        foreach (Job j in new[] { a, b, started, done })
        {
            j.Finished += _ => Interlocked.Increment(ref finished);
        }

        Assert.Equal(2, jobs.CancelNotStarted(new ErrorInfo(ErrorCodes.Paused, null, PauseGate.Message)));

        Assert.Equal(2, finished);
        Assert.Equal(ErrorCodes.Paused, a.Error!.Code);
        Assert.Equal(ErrorCodes.Paused, b.Error!.Code);
        Assert.False(JobTable.MarkStarted(a));
        Assert.Equal(JobState.Running, started.State);
        Assert.Null(done.Error);
        Assert.Equal(0, jobs.CancelNotStarted(new ErrorInfo(ErrorCodes.Paused)));
    }
}

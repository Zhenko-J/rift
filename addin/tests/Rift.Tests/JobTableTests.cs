// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using Xunit;

using Rift.Core.Jobs;
using Rift.Core.Logging;
using Rift.Core.Protocol;

namespace Rift.Tests;

/// <summary>Plan M1, test 6, and the REVIT_BUSY rule: a job the client was told failed never runs later.</summary>
public sealed class JobTableTests
{
    [Fact]
    public void Wait_started_gives_up_after_the_delay()
    {
        var table = new JobTable(TimeSpan.FromMinutes(1));
        Job job = table.Create("context", (_, _) => null);
        Assert.False(job.WaitStarted(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(JobState.Queued, job.State);
    }

    [Fact]
    public void A_finished_job_is_found_until_the_retention_then_forgotten()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var table = new JobTable(TimeSpan.FromMinutes(10), () => now);
        Job job = table.Create("run", (_, _) => new JsonObject());
        Assert.True(JobTable.MarkStarted(job));
        JobTable.MarkDone(job, new JsonObject { ["x"] = 1 }, null);
        now += TimeSpan.FromMinutes(9);
        Assert.Same(job, table.Find(job.Id));
        now += TimeSpan.FromMinutes(2);
        Assert.Null(table.Find(job.Id));
    }

    [Fact]
    public void A_job_cancelled_before_it_started_is_skipped_and_a_started_one_cannot_be_cancelled()
    {
        var table = new JobTable(TimeSpan.FromMinutes(1));
        Job queued = table.Create("run", (_, _) => null);
        Assert.True(JobTable.TryCancelBeforeStart(queued, new ErrorInfo(ErrorCodes.RevitBusy)));
        Assert.False(JobTable.MarkStarted(queued));
        Assert.Equal(ErrorCodes.RevitBusy, queued.Error!.Code);

        Job running = table.Create("run", (_, _) => null);
        Assert.True(JobTable.MarkStarted(running));
        Assert.False(JobTable.TryCancelBeforeStart(running, new ErrorInfo(ErrorCodes.RevitBusy)));
        Assert.Equal(JobState.Running, running.State);
    }

    private sealed class NeverRunner : IJobRunner
    {
        public Job? Last { get; private set; }

        public void Enqueue(Job job) => Last = job;

        public string? BusyReason() => "Revit is showing a modal dialog \"Save File\"";
    }

    private sealed class Handler : IOperationHandler
    {
        public int Runs { get; private set; }

        public string Op => "run";

        public bool NeedsApiThread => true;

        public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
        {
            Runs++;
            return null;
        }
    }

    [Fact]
    public void Revit_busy_names_the_dialog_and_the_job_never_runs_afterwards()
    {
        var runner = new NeverRunner();
        var handler = new Handler();
        var router = new RequestRouter(new IOperationHandler[] { handler }, new JobTable(TimeSpan.FromMinutes(1)), runner,
            () => new TargetInfo(1, 1, null), FileLog.Null, TimeSpan.FromMilliseconds(50));
        ResponseEnvelope r = router.Handle(Encoding.UTF8.GetBytes("{\"v\":\"2.0\",\"id\":\"b\",\"op\":\"run\"}"), CancellationToken.None);
        Assert.Equal(ErrorCodes.RevitBusy, r.Error!.Code);
        Assert.Contains("Save File", r.Error.Message, StringComparison.Ordinal);
        Assert.True(r.Result!["cancelled"]!.GetValue<bool>());

        // Revit becomes idle and the dispatcher reaches the job: it is skipped.
        Assert.False(JobTable.MarkStarted(runner.Last!));
        Assert.Equal(0, handler.Runs);
    }
}

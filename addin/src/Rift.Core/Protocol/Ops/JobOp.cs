// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Jobs;

namespace Rift.Core.Protocol.Ops;

/// <summary>
/// <c>op: job</c> (ADR-025): state, progress and result of a queued, running or finished job. With <c>args.cancel: true</c>
/// (M2) the job is cancelled instead: queued → done with <c>CANCELLED</c>, never run; running → token cancelled, the script
/// stops at its next trace event and the run reports <c>cancelled</c>; finished → nothing. The answer then carries
/// <c>cancel: cancelled|requested|finished</c> and is never an error.
/// </summary>
public sealed class JobOp : IOperationHandler
{
    private readonly JobTable _jobs;

    public JobOp(JobTable jobs)
    {
        _jobs = jobs;
    }

    public string Op => "job";

    public bool NeedsApiThread => false;

    public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        string? id = request.Args?["job_id"]?.GetValue<string>();
        Job? job = string.IsNullOrEmpty(id) ? null : _jobs.Find(id);
        if (job is null)
        {
            throw new OperationException(ErrorCodes.JobNotFound, "unknown or expired job");
        }
        bool cancel = request.Args?["cancel"] is JsonValue c && c.TryGetValue(out bool b) && b;
        CancelOutcome? cancelled = null;
        if (cancel)
        {
            cancelled = JobTable.TryCancel(job, new ErrorInfo(ErrorCodes.Cancelled, null, "cancelled by the client before it started"));
        }
        int waitS = request.Args?["wait_s"]?.GetValue<int>() ?? 0;
        if (waitS > 0 && !cancel)
        {
            job.WaitFinished(TimeSpan.FromSeconds(Math.Min(waitS, 300)));
        }
        var node = new JsonObject
        {
            ["job_id"] = job.Id,
            ["op"] = job.Op,
            ["state"] = job.State.ToString().ToLowerInvariant(),
            ["progress"] = job.Progress.Percent,
            ["message"] = job.Progress.Message,
            ["queued_utc"] = job.QueuedUtc.ToString("O"),
            ["started_utc"] = job.StartedUtc?.ToString("O"),
            ["finished_utc"] = job.FinishedUtc?.ToString("O"),
        };
        if (cancelled is { } outcome)
        {
            node["cancel"] = outcome.ToString().ToLowerInvariant();
            node["cancel_requested"] = job.CancelRequested;
            return node;
        }
        if (job.State == JobState.Done)
        {
            if (job.Error is not null)
            {
                throw new OperationException(job.Error.Code, job.Error.Message, job.Details, job.Refused);
            }
            node["result"] = job.Result?.DeepClone();
        }
        return node;
    }
}

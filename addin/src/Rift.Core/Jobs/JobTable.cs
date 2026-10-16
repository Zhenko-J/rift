// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Protocol;

namespace Rift.Core.Jobs;

/// <summary>What <see cref="JobTable.TryCancel"/> did.</summary>
public enum CancelOutcome
{
    /// <summary>The job had not started: it is done with CANCELLED and will never run.</summary>
    Cancelled,

    /// <summary>The job is running: its token is cancelled, the run ends on its own.</summary>
    Requested,

    /// <summary>The job was already finished: nothing to cancel.</summary>
    Finished,
}

/// <summary>Jobs by id, kept for <c>retention</c> after completion so that <c>op: job</c> can poll them (ADR-025).</summary>
public sealed class JobTable
{
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly TimeSpan _retention;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public JobTable(TimeSpan retention, Func<DateTimeOffset>? clock = null)
    {
        _retention = retention;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Job Create(string op, Func<IProgress<(int Percent, string Message)>, CancellationToken, JsonNode?> work)
    {
        var job = new Job(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(), op, work);
        lock (_gate)
        {
            Purge();
            _jobs[job.Id] = job;
        }
        return job;
    }

    public Job? Find(string id)
    {
        lock (_gate)
        {
            Purge();
            return _jobs.TryGetValue(id, out Job? job) ? job : null;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Count;
            }
        }
    }

    /// <summary>False when the job was cancelled before it started (the runner skips it).</summary>
    public static bool MarkStarted(Job job) => job.MarkStarted();

    public static bool TryCancelBeforeStart(Job job, ErrorInfo error) => job.TryCancelBeforeStart(error);

    public static void MarkDone(Job job, JsonNode? result, ErrorInfo? error, bool refused = false, JsonNode? details = null) =>
        job.MarkDone(result, error, refused, details);

    /// <summary>
    /// <c>op: job {cancel: true}</c> (M2): a queued job ends now with <paramref name="error"/> (CANCELLED) and never runs; a
    /// running job gets its token cancelled and finishes on its own (the run reports <c>cancelled</c>); a finished job is left alone.
    /// </summary>
    public static CancelOutcome TryCancel(Job job, ErrorInfo error)
    {
        if (job.TryCancelBeforeStart(error))
        {
            return CancelOutcome.Cancelled;
        }
        return job.RequestCancel() ? CancelOutcome.Requested : CancelOutcome.Finished;
    }

    /// <summary>
    /// The pause (ADR-029 §2): every job that has not started ends now with <paramref name="error"/> as a refusal and never
    /// runs; running jobs are left to finish. Returns the number of jobs cancelled.
    /// </summary>
    public int CancelNotStarted(ErrorInfo error)
    {
        var queued = new List<Job>();
        lock (_gate)
        {
            foreach (Job job in _jobs.Values)
            {
                if (job.State == JobState.Queued)
                {
                    queued.Add(job);
                }
            }
        }
        int cancelled = 0;
        foreach (Job job in queued)
        {
            // Outside the lock: Finished subscribers may call back into the table.
            if (job.TryCancelBeforeStart(error, refused: true))
            {
                cancelled++;
            }
        }
        return cancelled;
    }

    private void Purge()
    {
        DateTimeOffset now = _clock();
        var expired = new List<string>();
        foreach (KeyValuePair<string, Job> kv in _jobs)
        {
            if (kv.Value.State == JobState.Done && kv.Value.FinishedUtc is { } finished && now - finished > _retention)
            {
                expired.Add(kv.Key);
            }
        }
        foreach (string id in expired)
        {
            _jobs.Remove(id);
        }
    }
}

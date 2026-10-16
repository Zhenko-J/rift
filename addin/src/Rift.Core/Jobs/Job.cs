// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text.Json.Nodes;
using System.Threading;

using Rift.Core.Protocol;

namespace Rift.Core.Jobs;

public enum JobState
{
    Queued,
    Running,
    Done,
}

/// <summary>A unit of work queued by the pipe thread and executed on the Revit API thread (ADR-025).</summary>
public sealed class Job : IDisposable
{
    private readonly ManualResetEventSlim _started = new(false);
    private readonly ManualResetEventSlim _finished = new(false);
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _gate = new();

    internal Job(string id, string op, Func<IProgress<(int Percent, string Message)>, CancellationToken, JsonNode?> work)
    {
        Id = id;
        Op = op;
        Work = work;
        QueuedUtc = DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    public string Op { get; }

    public JobState State { get; private set; }

    public (int Percent, string Message) Progress { get; private set; }

    public JsonNode? Result { get; private set; }

    public ErrorInfo? Error { get; private set; }

    public bool Refused { get; private set; }

    public JsonNode? Details { get; private set; }

    public DateTimeOffset QueuedUtc { get; }

    public DateTimeOffset? StartedUtc { get; private set; }

    public DateTimeOffset? FinishedUtc { get; private set; }

    public Func<IProgress<(int Percent, string Message)>, CancellationToken, JsonNode?> Work { get; }

    /// <summary>Cancelled by <c>op: job {cancel: true}</c> (M2): the engine's trace hook sees it and stops the script.</summary>
    public CancellationToken Token => _cancel.Token;

    /// <summary>True once a cancellation was requested while the job was running.</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>Raised once, on the thread that ended the job (the API thread for a run, the caller for a cancellation), never under the lock.</summary>
    public event Action<Job>? Finished;

    public void Dispose()
    {
        _started.Dispose();
        _finished.Dispose();
        _cancel.Dispose();
    }

    public bool WaitStarted(TimeSpan timeout) => _started.Wait(timeout);

    public bool WaitFinished(TimeSpan timeout) => _finished.Wait(timeout);

    public void Report((int Percent, string Message) value)
    {
        lock (_gate)
        {
            Progress = value;
        }
    }

    /// <summary>Queued → running. False when the job was cancelled before it started: the runner must skip it.</summary>
    internal bool MarkStarted()
    {
        lock (_gate)
        {
            if (State != JobState.Queued)
            {
                return false;
            }
            State = JobState.Running;
            StartedUtc = DateTimeOffset.UtcNow;
        }
        _started.Set();
        return true;
    }

    /// <summary>
    /// Ends a job that has not started yet with <paramref name="error"/> (REVIT_BUSY: the client was told it failed, so it must
    /// never run later behind its back). False when it already started: it is then never abandoned (ADR-008).
    /// </summary>
    internal bool TryCancelBeforeStart(ErrorInfo error, bool refused = false)
    {
        lock (_gate)
        {
            if (State != JobState.Queued)
            {
                return false;
            }
            Error = error;
            Refused = refused;
            State = JobState.Done;
            FinishedUtc = DateTimeOffset.UtcNow;
        }
        _finished.Set();
        RaiseFinished();
        return true;
    }

    /// <summary>Asks a running job to stop (cooperative: the script is interrupted at its next trace event). False unless running.</summary>
    internal bool RequestCancel()
    {
        lock (_gate)
        {
            if (State != JobState.Running)
            {
                return false;
            }
            CancelRequested = true;
        }
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        return true;
    }

    internal void MarkDone(JsonNode? result, ErrorInfo? error, bool refused, JsonNode? details)
    {
        lock (_gate)
        {
            Result = result;
            Error = error;
            Refused = refused;
            Details = details;
            State = JobState.Done;
            FinishedUtc = DateTimeOffset.UtcNow;
        }
        _started.Set();
        _finished.Set();
        RaiseFinished();
    }

    private void RaiseFinished()
    {
        try
        {
            Finished?.Invoke(this);
        }
        catch (Exception)
        {
            // A subscriber (activity log, card) must never undo the end of a job.
        }
    }
}

// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;

using Rift.Core.Jobs;
using Rift.Core.Lease;
using Rift.Core.Protocol;

namespace Rift.Core.Control;

public enum ServerState
{
    Running,
    Paused,
}

/// <summary>The pause as the user set it: state, since when, by whom (<c>you</c> for the ribbon).</summary>
public sealed record PauseInfo(ServerState State, DateTimeOffset? SinceUtc, string? By);

/// <summary>
/// The pause of ADR-029 §2: one click in Revit and every assistant call is refused with <c>PAUSED</c> before any handler,
/// lease or job. Pausing revokes the lease and cancels the queued jobs (a running job is allowed to finish); resuming lets
/// the next call through. Not remembered between Revit sessions. Thread-safe; <see cref="Pause"/> is synchronous.
/// </summary>
public sealed class PauseGate
{
    /// <summary>Message of every refusal while paused, also the error of the jobs cancelled by the pause.</summary>
    public const string Message = "paused by the user in Revit";

    private readonly LeaseManager _lease;
    private readonly JobTable _jobs;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private PauseInfo _state = new(ServerState.Running, null, null);

    public PauseGate(LeaseManager lease, JobTable jobs, Func<DateTimeOffset>? clock = null)
    {
        _lease = lease;
        _jobs = jobs;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised on the caller's thread after the state changed; never under the lock, never for a no-op.</summary>
    public event Action<PauseInfo>? Changed;

    public PauseInfo State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public bool IsPaused => State.State == ServerState.Paused;

    /// <summary>Pauses (idempotent): revokes the lease, cancels the queued jobs with <c>PAUSED</c>; returns how many were cancelled.</summary>
    public int Pause(string by)
    {
        PauseInfo now;
        lock (_gate)
        {
            if (_state.State == ServerState.Paused)
            {
                return 0;
            }
            now = new PauseInfo(ServerState.Paused, _clock(), by);
            _state = now;
        }
        _lease.Revoke();
        int cancelled = _jobs.CancelNotStarted(new ErrorInfo(ErrorCodes.Paused, null, Message));
        Changed?.Invoke(now);
        return cancelled;
    }

    /// <summary>Resumes (idempotent): the next call leases again and runs.</summary>
    public void Resume()
    {
        PauseInfo now;
        lock (_gate)
        {
            if (_state.State == ServerState.Running)
            {
                return;
            }
            now = new PauseInfo(ServerState.Running, null, null);
            _state = now;
        }
        Changed?.Invoke(now);
    }

    /// <summary>The ribbon button: pauses when running, resumes when paused; returns the new state.</summary>
    public PauseInfo Toggle(string by)
    {
        if (IsPaused)
        {
            Resume();
        }
        else
        {
            Pause(by);
        }
        return State;
    }
}

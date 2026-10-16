// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;

namespace Rift.Core.Lease;

public sealed record LeaseHolder(string ClientId, string Name, string Version, DateTimeOffset Since);

public sealed record LeaseResult(bool Granted, DateTimeOffset? Until, LeaseHolder? Holder);

/// <summary>
/// One client per Revit instance (ADR-004): a lease of <c>duration</c> (60 s in the add-in) held by a single client,
/// renewed by every signed request of its holder (<see cref="Admit"/>), taken by another client only once it expired.
/// The lease is coordination, not authentication: every caller already proved it holds the secret.
/// </summary>
public sealed class LeaseManager
{
    private readonly TimeSpan _duration;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private LeaseHolder? _holder;
    private DateTimeOffset _until;

    public LeaseManager(TimeSpan duration, Func<DateTimeOffset>? clock = null)
    {
        _duration = duration;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised on the calling thread (the pipe thread) when a new holder obtains the lease; never while the lock is held.</summary>
    public event Action<LeaseHolder>? Granted;

    /// <summary>The live holder, or null when nobody holds the lease or it expired.</summary>
    public LeaseHolder? Holder
    {
        get
        {
            lock (_gate)
            {
                return IsExpired(_clock()) ? null : _holder;
            }
        }
    }

    /// <summary>End of the current lease, or null when nobody holds it.</summary>
    public DateTimeOffset? Until
    {
        get
        {
            lock (_gate)
            {
                return IsExpired(_clock()) ? null : _until;
            }
        }
    }

    /// <summary><c>op: lease</c>: grants (or renews) the lease to <paramref name="candidate"/>, or reports the live holder.</summary>
    public LeaseResult Acquire(LeaseHolder candidate)
    {
        LeaseHolder? granted = null;
        LeaseResult result;
        lock (_gate)
        {
            DateTimeOffset now = _clock();
            if (!IsExpired(now) && !IsHolder(candidate.ClientId))
            {
                return new LeaseResult(false, null, _holder);
            }
            if (IsExpired(now) || !IsHolder(candidate.ClientId))
            {
                _holder = candidate with { Since = now };
                granted = _holder;
            }
            _until = now + _duration;
            result = new LeaseResult(true, _until, _holder);
        }
        if (granted is not null)
        {
            Granted?.Invoke(granted);
        }
        return result;
    }

    /// <summary>
    /// Any other signed request: renews the lease when <paramref name="clientId"/> holds it, refuses (<c>Granted=false</c>)
    /// when another client holds a live lease, and grants it when nobody does (a client idle past its lease simply
    /// takes it back). A request without a client id is admitted only when nobody holds the lease, and takes nothing.
    /// </summary>
    public LeaseResult Admit(string? clientId, string? name, string? version)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            lock (_gate)
            {
                return IsExpired(_clock()) ? new LeaseResult(true, null, null) : new LeaseResult(false, null, _holder);
            }
        }
        return Acquire(new LeaseHolder(clientId, string.IsNullOrEmpty(name) ? clientId : name, string.IsNullOrEmpty(version) ? "?" : version, _clock()));
    }

    /// <summary>Renews when <paramref name="clientId"/> is the live holder; returns false otherwise.</summary>
    public bool Renew(string clientId)
    {
        lock (_gate)
        {
            DateTimeOffset now = _clock();
            if (IsExpired(now) || !IsHolder(clientId))
            {
                return false;
            }
            _until = now + _duration;
            return true;
        }
    }

    public void Revoke()
    {
        lock (_gate)
        {
            _holder = null;
        }
    }

    private bool IsHolder(string clientId) => _holder is not null && string.Equals(_holder.ClientId, clientId, StringComparison.Ordinal);

    private bool IsExpired(DateTimeOffset now) => _holder is null || now > _until;
}

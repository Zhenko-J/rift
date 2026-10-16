// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;

namespace Rift.Core.Security;

/// <summary>Nonces seen within <c>lifetime</c> (ADR-002: a nonce is valid once within 5 minutes).</summary>
public sealed class NonceCache
{
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly TimeSpan _lifetime;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private DateTimeOffset _lastPurge = DateTimeOffset.MinValue;

    public NonceCache(TimeSpan lifetime, Func<DateTimeOffset>? clock = null)
    {
        _lifetime = lifetime;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Number of nonces currently remembered (tests and diagnostics).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _seen.Count;
            }
        }
    }

    /// <summary>Returns false when the nonce was already seen within the lifetime.</summary>
    public bool TryAdd(string nonceHex)
    {
        DateTimeOffset now = _clock();
        lock (_gate)
        {
            // Lazy purge: at most once per lifetime, so the cost stays proportional to the traffic.
            if (now - _lastPurge > _lifetime)
            {
                Purge(now);
                _lastPurge = now;
            }
            if (_seen.TryGetValue(nonceHex, out DateTimeOffset seenAt) && now - seenAt <= _lifetime)
            {
                return false;
            }
            _seen[nonceHex] = now;
            return true;
        }
    }

    private void Purge(DateTimeOffset now)
    {
        var expired = new List<string>();
        foreach (KeyValuePair<string, DateTimeOffset> kv in _seen)
        {
            if (now - kv.Value > _lifetime)
            {
                expired.Add(kv.Key);
            }
        }
        foreach (string n in expired)
        {
            _seen.Remove(n);
        }
    }
}

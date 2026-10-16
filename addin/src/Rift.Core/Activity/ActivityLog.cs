// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Rift.Core.Activity;

/// <summary>A change receipt reduced to what History shows (UI gate 3): counts by category, at most 50 categories.</summary>
public sealed record CategoryChange(string Category, int Added, int Modified, int Deleted);

public sealed record ChangeSummary(IReadOnlyList<CategoryChange> Categories)
{
    private const int MaxCategories = 50;

    public int Added => Categories.Sum(c => c.Added);

    public int Modified => Categories.Sum(c => c.Modified);

    public int Deleted => Categories.Sum(c => c.Deleted);

    /// <summary>Reads <c>result.changes.by_category</c> (format fixed by M2); null when absent or malformed.</summary>
    public static ChangeSummary? From(JsonNode? result)
    {
        if (result is not JsonObject r || r["changes"] is not JsonObject changes || changes["by_category"] is not JsonObject byCategory)
        {
            return null;
        }
        var list = new List<CategoryChange>();
        foreach (KeyValuePair<string, JsonNode?> kv in byCategory)
        {
            if (list.Count >= MaxCategories)
            {
                break;
            }
            if (kv.Value is not JsonObject counts)
            {
                continue;
            }
            list.Add(new CategoryChange(Clean(kv.Key), Count(counts, "added"), Count(counts, "modified"), Count(counts, "deleted")));
        }
        return new ChangeSummary(list);
    }

    /// <summary>"7 added, 1 modified" (zero counts left out), or "no change".</summary>
    public string Text()
    {
        var parts = new List<string>();
        if (Added > 0)
        {
            parts.Add(Added.ToString(System.Globalization.CultureInfo.InvariantCulture) + " added");
        }
        if (Modified > 0)
        {
            parts.Add(Modified.ToString(System.Globalization.CultureInfo.InvariantCulture) + " modified");
        }
        if (Deleted > 0)
        {
            parts.Add(Deleted.ToString(System.Globalization.CultureInfo.InvariantCulture) + " deleted");
        }
        return parts.Count == 0 ? "no change" : string.Join(", ", parts);
    }

    private static int Count(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue(out int n) ? Math.Max(0, n) : 0;

    // Category names come from the model: control characters out, bounded length (ADR-020).
    private static string Clean(string s)
    {
        string kept = new(s.Where(c => !char.IsControl(c)).Take(64).ToArray());
        return kept.Length == 0 ? "?" : kept;
    }
}

/// <summary>One line of the session's activity (UI gate 3): never script text, never model values.</summary>
/// <param name="Key">The job id when the request became a job, otherwise a sequence number.</param>
public sealed record RequestRecord(string Key, DateTimeOffset Utc, string Label, string Status, string? Code, string? Message,
    long ElapsedMs, string? Outcome, ChangeSummary? Changes);

public sealed record ActivitySnapshot(IReadOnlyList<RequestRecord> Records, long Total, int Succeeded, int Failed, int Running, long Version);

/// <summary>
/// The session's activity for History and the card (UI gate 3): a ring of the last <see cref="DefaultCapacity"/>
/// requests, updated in place by key (a job seen as <c>accepted</c> and then finished is one line). Thread-safe.
/// </summary>
public sealed class ActivityLog
{
    public const int DefaultCapacity = 500;

    private readonly int _capacity;
    private readonly List<RequestRecord> _records = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _total;
    private long _version;
    private long _sequence;

    public ActivityLog(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : DefaultCapacity;
    }

    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    public string NextKey() => "u" + System.Threading.Interlocked.Increment(ref _sequence).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Inserts, or replaces in place the record with the same key; <see cref="ActivitySnapshot.Total"/> counts distinct keys.</summary>
    public void Upsert(RequestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            int i = _records.FindIndex(r => r.Key == record.Key);
            if (i >= 0)
            {
                // A job's final line may arrive before its "accepted" line: a finished line is never set back to running.
                if (IsRunning(record.Status) && !IsRunning(_records[i].Status))
                {
                    return;
                }
                _records[i] = record;
            }
            else
            {
                if (_keys.Add(record.Key))
                {
                    _total++;
                }
                _records.Add(record);
                while (_records.Count > _capacity)
                {
                    _keys.Remove(_records[0].Key);
                    _records.RemoveAt(0);
                }
            }
            _version++;
        }
    }

    public ActivitySnapshot Snapshot()
    {
        lock (_gate)
        {
            RequestRecord[] copy = _records.ToArray();
            int running = copy.Count(r => IsRunning(r.Status));
            int failed = copy.Count(r => IsFailed(r.Status));
            return new ActivitySnapshot(copy, _total, copy.Length - running - failed, failed, running, _version);
        }
    }

    public static bool IsRunning(string status) => status is "accepted" or "running";

    public static bool IsFailed(string status) => status is "error" or "refused";
}

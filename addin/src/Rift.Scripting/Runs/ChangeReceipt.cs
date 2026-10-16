// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

using Rift.Core.Json;

namespace Rift.Scripting.Runs;

/// <summary>One element touched by a run, shown as a sample of the receipt (ADR-020: name sanitised, nothing else).</summary>
public sealed record ChangeSample(long Id, string Category, string? Name, string Kind);

/// <summary>Counts of one category in a receipt.</summary>
public sealed record CategoryChanges(int Added, int Modified, int Deleted);

/// <summary>
/// The change receipt of a write run (ADR-026 §2): what the guard saw between arming and the decision, from
/// <c>DocumentChanged</c> on the target document — element counts, by category, a few samples, the number of transactions
/// the script committed. Returned on every <c>run_modify</c>, dry run included, and kept in the per-document history.
/// </summary>
public sealed record ChangeReceipt(
    int Added,
    int Modified,
    int Deleted,
    int Transactions,
    IReadOnlyDictionary<string, CategoryChanges> ByCategory,
    IReadOnlyList<ChangeSample> Samples)
{
    public const int MaxSamples = 20;

    public static ChangeReceipt Empty { get; } =
        new(0, 0, 0, 0, new Dictionary<string, CategoryChanges>(StringComparer.Ordinal), Array.Empty<ChangeSample>());

    public int Total => Added + Modified + Deleted;

    public JsonObject ToJson()
    {
        var byCategory = new JsonObject();
        foreach (KeyValuePair<string, CategoryChanges> kv in ByCategory.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            byCategory[Sanitizer.Sanitize(kv.Key, 120)] = new JsonObject
            {
                ["added"] = kv.Value.Added,
                ["modified"] = kv.Value.Modified,
                ["deleted"] = kv.Value.Deleted,
            };
        }
        var samples = new JsonArray();
        foreach (ChangeSample s in Samples.Take(MaxSamples))
        {
            samples.Add(new JsonObject
            {
                ["id"] = s.Id,
                ["category"] = Sanitizer.Sanitize(s.Category, 120),
                ["name"] = s.Name is null ? null : Sanitizer.Sanitize(s.Name, 260),
                ["kind"] = s.Kind,
            });
        }
        return new JsonObject
        {
            ["added"] = Added,
            ["modified"] = Modified,
            ["deleted"] = Deleted,
            ["transactions"] = Transactions,
            ["by_category"] = byCategory,
            ["samples"] = samples,
        };
    }

    /// <summary>Builds a receipt from raw sets (ids by kind and category), the way the Revit guard collects them.</summary>
    public static ChangeReceipt From(
        IEnumerable<(long Id, string Category, string? Name)> added,
        IEnumerable<(long Id, string Category, string? Name)> modified,
        IEnumerable<long> deleted,
        int transactions)
    {
        var by = new Dictionary<string, (int A, int M, int D)>(StringComparer.Ordinal);
        var samples = new List<ChangeSample>();
        int a = 0;
        int m = 0;
        foreach ((long id, string category, string? name) in added)
        {
            a++;
            by[category] = by.TryGetValue(category, out (int A, int M, int D) c) ? (c.A + 1, c.M, c.D) : (1, 0, 0);
            if (samples.Count < MaxSamples)
            {
                samples.Add(new ChangeSample(id, category, name, "added"));
            }
        }
        foreach ((long id, string category, string? name) in modified)
        {
            m++;
            by[category] = by.TryGetValue(category, out (int A, int M, int D) c) ? (c.A, c.M + 1, c.D) : (0, 1, 0);
            if (samples.Count < MaxSamples)
            {
                samples.Add(new ChangeSample(id, category, name, "modified"));
            }
        }
        int d = 0;
        foreach (long id in deleted)
        {
            d++;
            // A deleted element has no category any more: counted under "(deleted)".
            by["(deleted)"] = by.TryGetValue("(deleted)", out (int A, int M, int D) c) ? (c.A, c.M, c.D + 1) : (0, 0, 1);
            if (samples.Count < MaxSamples)
            {
                samples.Add(new ChangeSample(id, "(deleted)", null, "deleted"));
            }
        }
        return new ChangeReceipt(a, m, d, transactions,
            by.ToDictionary(kv => kv.Key, kv => new CategoryChanges(kv.Value.A, kv.Value.M, kv.Value.D), StringComparer.Ordinal), samples);
    }
}

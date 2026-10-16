// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

using Rift.Core.IO;
using Rift.Core.Json;
using Rift.Core.Logging;

namespace Rift.Core.Config;

/// <summary>
/// <c>%LOCALAPPDATA%\rift\config.json</c>: activity card switch (ADR-023), SANDBOX markers and reference names (ADR-009),
/// other-agent window patterns (ADR-019), the start timeout of the dispatcher (ADR-008/025) and <c>developer</c>, which
/// shows the development buttons of the ribbon (ADR-029), and <c>session_prompt</c>, the session question asked in Revit at
/// the start of each assistant session (ADR-033; off = SANDBOX copies only, policy as requested). Loading never throws: a
/// missing or invalid file gives <see cref="Default"/>, an invalid field keeps its default value.
/// </summary>
public sealed record RiftConfig(
    bool ActivityCard,
    IReadOnlyList<string> SandboxMarkers,
    IReadOnlyList<string> ReferenceNames,
    IReadOnlyList<string> AgentPatterns,
    int StartTimeoutS,
    bool Developer = false,
    bool CoexistWithOtherAgents = false,
    bool SessionPrompt = true)
{
    public const int MinStartTimeoutS = 5;
    public const int MaxStartTimeoutS = 300;

    public static IReadOnlyList<string> DefaultSandboxMarkers { get; } =
        new[] { "SANDBOX", "JETABLE", "_INJ_", "TESTRUN", "COPIE", "COPY", "SCRATCH" };

    public static RiftConfig Default { get; } =
        new(true, DefaultSandboxMarkers, Array.Empty<string>(), Policy.Coexistence.DefaultAgentPatterns, 30);

    /// <summary>Reads the file; returns <see cref="Default"/> (and logs why) when it is absent or not a JSON object.</summary>
    public static RiftConfig Load(string path, FileLog? log = null)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                return Default;
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Write("config: cannot read " + Path.GetFileName(path) + " (" + ex.GetType().Name + "), defaults used");
            return Default;
        }
        if (JsonCodec.TryParseNode(bytes) is not JsonObject obj)
        {
            log?.Write("config: " + Path.GetFileName(path) + " is not a JSON object, defaults used");
            return Default;
        }
        RiftConfig d = Default;
        try
        {
            return Read(obj, d, log);
        }
        catch (ArgumentException ex)
        {
            // A key written twice by hand makes JsonObject throw on first access: never let config.json stop the add-in.
            log?.Write("config: " + Path.GetFileName(path) + " is invalid (" + ex.Message + "), defaults used");
            return Default;
        }
    }

    private static RiftConfig Read(JsonObject obj, RiftConfig d, FileLog? log)
    {
        return new RiftConfig(
            Bool(obj, "activity_card", d.ActivityCard, log),
            Markers(Strings(obj, "sandbox_markers", d.SandboxMarkers, log), log),
            Strings(obj, "reference_names", d.ReferenceNames, log),
            Strings(obj, "agent_patterns", d.AgentPatterns, log),
            Math.Clamp(Int(obj, "start_timeout_s", d.StartTimeoutS, log), MinStartTimeoutS, MaxStartTimeoutS),
            Bool(obj, "developer", d.Developer, log),
            Bool(obj, "coexist_with_other_agents", d.CoexistWithOtherAgents, log),
            Bool(obj, "session_prompt", d.SessionPrompt, log));
    }

    /// <summary>Writes the file atomically (temp file + move).</summary>
    public void Save(string path)
    {
        var obj = new JsonObject
        {
            ["activity_card"] = ActivityCard,
            ["sandbox_markers"] = new JsonArray(SandboxMarkers.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["reference_names"] = new JsonArray(ReferenceNames.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["agent_patterns"] = new JsonArray(AgentPatterns.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["start_timeout_s"] = StartTimeoutS,
            ["developer"] = Developer,
            ["coexist_with_other_agents"] = CoexistWithOtherAgents,
            ["session_prompt"] = SessionPrompt,
        };
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = path + "." + Environment.TickCount64.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(tmp, obj.ToJsonString(new JsonSerializerOptions(JsonCodec.Options) { WriteIndented = true }));
        AtomicFile.MoveReplacing(tmp, path);
    }

    /// <summary>F-R6: a marker that is too short or punctuation only is dropped (and logged), never widened to every file.</summary>
    private static IReadOnlyList<string> Markers(IReadOnlyList<string> markers, FileLog? log)
    {
        var valid = new List<string>();
        foreach (string m in markers)
        {
            if (Policy.DocumentClassifier.IsValidMarker(m))
            {
                valid.Add(m);
            }
            else
            {
                log?.Write("config: sandbox marker of " + m.Length.ToString(CultureInfo.InvariantCulture) + " character(s) ignored (at least "
                    + Policy.DocumentClassifier.MinMarkerLength.ToString(CultureInfo.InvariantCulture) + " characters, three of them letters or digits)");
            }
        }
        return valid;
    }

    private static bool Bool(JsonObject obj, string name, bool fallback, FileLog? log)
    {
        if (!obj.TryGetPropertyValue(name, out JsonNode? node) || node is null)
        {
            return fallback;
        }
        if (node is JsonValue v && v.TryGetValue(out bool b))
        {
            return b;
        }
        log?.Write("config: " + name + " is not a boolean, default kept");
        return fallback;
    }

    private static int Int(JsonObject obj, string name, int fallback, FileLog? log)
    {
        if (!obj.TryGetPropertyValue(name, out JsonNode? node) || node is null)
        {
            return fallback;
        }
        if (node is JsonValue v && v.TryGetValue(out int i))
        {
            return i;
        }
        log?.Write("config: " + name + " is not an integer, default kept");
        return fallback;
    }

    private static IReadOnlyList<string> Strings(JsonObject obj, string name, IReadOnlyList<string> fallback, FileLog? log)
    {
        if (!obj.TryGetPropertyValue(name, out JsonNode? node) || node is null)
        {
            return fallback;
        }
        if (node is not JsonArray arr)
        {
            log?.Write("config: " + name + " is not a list, default kept");
            return fallback;
        }
        var list = new List<string>();
        foreach (JsonNode? item in arr)
        {
            if (item is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                list.Add(s.Trim());
            }
        }
        return list;
    }
}

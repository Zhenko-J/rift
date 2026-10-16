// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

using Rift.Core.Activity;
using Rift.Core.Baked;
using Rift.Core.Control;
using Rift.Core.Protocol;

namespace Rift.Ui;

/// <summary>What the Status window shows (UI gate 3), taken by the host at each refresh. Never model data.</summary>
public sealed record StatusSnapshot(
    string Version,
    bool Paused,
    DateTimeOffset? PausedSinceUtc,
    string? PipeName,
    int Served,
    string? Client,
    DateTimeOffset? ClientSinceUtc,
    DateTimeOffset? LeaseUntilUtc,
    int SecretGeneration,
    string? LastRefusal,
    string Engine,
    bool CardOn,
    string? LogPath,
    string? Coexistence,
    string? Session = null);

/// <summary>What the windows ask of the host (UI gate 3). Called on Revit's UI thread only.</summary>
public interface IUiHost
{
    StatusSnapshot Status();

    ActivitySnapshot Activity();

    /// <summary>Same effect as the ribbon's server button.</summary>
    void TogglePause();

    /// <summary>Switches the activity card and remembers it in <c>config.json</c>.</summary>
    void SetActivityCard(bool enabled);

    /// <summary>The user answered the session window; false when another session started meanwhile.</summary>
    bool ChooseSession(WorkScope scope, Supervision supervision);

    /// <summary>Opens the session window again (to change the answer, or to give it after closing the window).</summary>
    void OpenSession();

    /// <summary>Baked command proposals waiting for the user (ADR-034).</summary>
    IReadOnlyList<BakedProposal> Proposals();

    /// <summary>Baked commands the user accepted.</summary>
    IReadOnlyList<BakedCommand> BakedCommands();

    /// <summary>The user accepted a proposal in the inbox window.</summary>
    void AcceptBaked(string id);

    /// <summary>The user rejected a proposal in the inbox window.</summary>
    void RejectBaked(string id);
}

/// <summary>One line of History, formatted (UI gate 3): every text is ready to show.</summary>
public sealed record HistoryRow(string Key, string Time, string Tool, PastilleState State, string Result, string? Code, string? Message,
    string Duration, string? Changes, string? ChangeDetail);

/// <summary>Pure formatting of the activity for History and its pastille grid. No WPF, no Revit API.</summary>
public static class HistoryView
{
    public const int GridMax = 49;
    public const int RowsMax = 200;

    public static PastilleState StateOf(RequestRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Code == ErrorCodes.Paused)
        {
            return PastilleState.Paused;
        }
        if (ActivityLog.IsRunning(r.Status))
        {
            return PastilleState.Running;
        }
        return ActivityLog.IsFailed(r.Status) ? PastilleState.Failed : PastilleState.Done;
    }

    public static HistoryRow Row(RequestRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        string result = r.Status switch
        {
            ResponseEnvelope.Ok => string.IsNullOrEmpty(r.Outcome) ? "ok" : r.Outcome.Replace('_', ' '),
            ResponseEnvelope.Accepted or ResponseEnvelope.Running => "running",
            ResponseEnvelope.Refused => r.Code == ErrorCodes.Paused ? "paused" : "refused",
            _ => "failed",
        };
        string? detail = r.Changes is null || r.Changes.Categories.Count == 0
            ? null
            : string.Join("\n", r.Changes.Categories.Select(c => c.Category + "  +" + N(c.Added) + "  ~" + N(c.Modified) + "  -" + N(c.Deleted)));
        return new HistoryRow(r.Key, r.Utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), r.Label, StateOf(r), result,
            r.Code, r.Message, Duration(r.ElapsedMs), r.Changes?.Text(), detail);
    }

    /// <summary>Newest first, at most <see cref="RowsMax"/>.</summary>
    public static IReadOnlyList<HistoryRow> Rows(ActivitySnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Records.Reverse().Take(RowsMax).Select(Row).ToArray();
    }

    /// <summary>The last <see cref="GridMax"/> requests, oldest first (seven per row in the window).</summary>
    public static IReadOnlyList<PastilleState> Grid(ActivitySnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Records.Skip(Math.Max(0, s.Records.Count - GridMax)).Select(StateOf).ToArray();
    }

    /// <summary>"12 succeeded · 1 failed · 2 running" (zero counts after the first left out).</summary>
    public static string Summary(ActivitySnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var parts = new List<string> { N(s.Succeeded) + " succeeded" };
        if (s.Failed > 0)
        {
            parts.Add(N(s.Failed) + " failed or refused");
        }
        if (s.Running > 0)
        {
            parts.Add(N(s.Running) + " running");
        }
        if (s.Total > s.Records.Count)
        {
            parts.Add("last " + N(s.Records.Count) + " kept");
        }
        return string.Join(" · ", parts);
    }

    /// <summary>"340 ms", "2.4 s", "3 min 05 s".</summary>
    public static string Duration(long ms)
    {
        if (ms < 0)
        {
            return "-";
        }
        if (ms < 1000)
        {
            return N(ms) + " ms";
        }
        if (ms < 60_000)
        {
            return (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
        }
        long s = ms / 1000;
        return N(s / 60) + " min " + (s % 60).ToString("00", CultureInfo.InvariantCulture) + " s";
    }

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One tool of the catalogue (the same file as the README table and the pamphlet).</summary>
public sealed record ToolEntry(string Name, string Wave, string Toolset, string Risk, bool Shipped, string Description);

/// <summary>The tool catalogue embedded from <c>docs/pamphlet/tools.json</c> (UI gate 3, tranche 5).</summary>
public sealed record ToolCatalog(string CurrentWave, IReadOnlyList<ToolEntry> Tools)
{
    public const string ResourceName = "Rift.Ui.tools.json";

    public static readonly ToolCatalog Empty = new("?", Array.Empty<ToolEntry>());

    public int Shipped => Tools.Count(t => t.Shipped);

    public IReadOnlyList<string> Risks => Tools.Select(t => t.Risk).Distinct(StringComparer.Ordinal).OrderBy(Order).ToArray();

    /// <summary>The embedded catalogue, or <see cref="Empty"/> when it is missing or malformed.</summary>
    public static ToolCatalog Load()
    {
        using Stream? stream = typeof(ToolCatalog).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            return Empty;
        }
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    public static ToolCatalog Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string wave = root.TryGetProperty("current_wave", out JsonElement w) ? w.GetString() ?? "?" : "?";
            var tools = new List<ToolEntry>();
            if (root.TryGetProperty("tools", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement t in list.EnumerateArray())
                {
                    tools.Add(new ToolEntry(S(t, "name"), S(t, "wave"), S(t, "toolset"), S(t, "risk"), S(t, "status") == "implemented",
                        FirstSentence(S(t, "description"))));
                }
            }
            return new ToolCatalog(wave, tools);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>Tools of <paramref name="risk"/>, or all when null; shipped first, then by wave and name.</summary>
    public IReadOnlyList<ToolEntry> Filter(string? risk) => Tools
        .Where(t => risk is null || t.Risk == risk)
        .OrderBy(t => t.Shipped ? 0 : 1)
        .ThenBy(t => t.Wave, StringComparer.Ordinal)
        .ThenBy(t => t.Name, StringComparer.Ordinal)
        .ToArray();

    private static int Order(string risk) => risk switch
    {
        "auto" => 0,
        "ask" => 1,
        _ => 2,
    };

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string FirstSentence(string text)
    {
        int dot = text.IndexOf(". ", StringComparison.Ordinal);
        string first = dot > 0 ? text[..dot] : text.TrimEnd('.');
        return first.Length <= 160 ? first : first[..157] + "...";
    }
}

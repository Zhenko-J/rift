// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Rift.Core.Json;
using Rift.Core.Logging;

namespace Rift.Core.Journal;

/// <summary>
/// ADR-005: one folder per run under <c>runs\&lt;utc&gt;-&lt;run_id&gt;\</c> (script, request, response, large outputs),
/// written by the add-in because the server may never receive the response, redacted at write time, kept
/// <c>retention</c> (14 days); a compact <c>runs-index.jsonl</c> is kept 90 days. Never throws: a journal that cannot be
/// written is logged and the run goes on.
/// </summary>
public sealed class RunJournal
{
    public const string IndexFileName = "runs-index.jsonl";

    private static readonly TimeSpan s_indexRetention = TimeSpan.FromDays(90);

    private readonly string _runsDir;
    private readonly RedactionContext _redaction;
    private readonly TimeSpan _retention;
    private readonly FileLog _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, (string Dir, string? DocumentFolder)> _open = new(StringComparer.Ordinal);
    private readonly object _indexGate = new();

    public RunJournal(string runsDir, RedactionContext redaction, TimeSpan retention, FileLog log, Func<DateTimeOffset>? clock = null)
    {
        _runsDir = runsDir;
        _redaction = redaction;
        _retention = retention;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string RunsDir => _runsDir;

    /// <summary>Creates the run folder and writes the (redacted) script and request. Returns the folder name.</summary>
    public string Begin(string runId, string op, string? script, JsonNode? request, string? documentFolder)
    {
        string name = _clock().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Safe(runId);
        _open[runId] = (name, documentFolder);
        Try(() =>
        {
            string dir = Path.Combine(_runsDir, name);
            Directory.CreateDirectory(dir);
            if (script is not null)
            {
                File.WriteAllText(Path.Combine(dir, "script.py"), Redact(script, documentFolder), Encoding.UTF8);
            }
            var head = new JsonObject { ["run_id"] = runId, ["op"] = op, ["request"] = request?.DeepClone() };
            File.WriteAllText(Path.Combine(dir, "request.json"), Redact(head.ToJsonString(JsonCodec.Options), documentFolder), Encoding.UTF8);
        }, "begin " + runId);
        return name;
    }

    /// <summary>
    /// Writes a large output next to the request (result.json, stdout.txt…) and returns its reference relative to the
    /// rift data folder (<c>runs/&lt;folder&gt;/&lt;file&gt;</c>), or null when it could not be written.
    /// </summary>
    public string? WriteAttachment(string runId, string fileName, string content)
    {
        if (!_open.TryGetValue(runId, out (string Dir, string? DocumentFolder) run))
        {
            return null;
        }
        string file = Safe(fileName);
        bool ok = Try(() =>
        {
            string dir = Path.Combine(_runsDir, run.Dir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, file), Redact(content, run.DocumentFolder), Encoding.UTF8);
        }, "attachment " + runId);
        return ok ? "runs/" + run.Dir + "/" + file : null;
    }

    /// <summary>
    /// Writes a file into the folder of a run that is already closed (<c>runs/&lt;folder&gt;</c> as returned in <c>run_log</c>),
    /// redacted without a document folder. Returns its reference, or null when it could not be written.
    /// </summary>
    public string? WriteToFolder(string folder, string fileName, string content)
    {
        string dirName = Safe(folder);
        string file = Safe(fileName);
        bool ok = Try(() =>
        {
            string dir = Path.Combine(_runsDir, dirName);
            if (!Directory.Exists(dir))
            {
                throw new IOException("run folder not found: " + dirName);
            }
            File.WriteAllText(Path.Combine(dir, file), Redact(content, null), Encoding.UTF8);
        }, "attachment " + dirName);
        return ok ? "runs/" + dirName + "/" + file : null;
    }

    /// <summary>Writes the response and one line of the index; forgets the run.</summary>
    public void End(string runId, JsonNode? response, string? outcome, long elapsedMs)
    {
        if (!_open.TryRemove(runId, out (string Dir, string? DocumentFolder) run))
        {
            return;
        }
        Try(() =>
        {
            string dir = Path.Combine(_runsDir, run.Dir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "response.json"), Redact(response?.ToJsonString(JsonCodec.Options) ?? "null", run.DocumentFolder), Encoding.UTF8);
            var line = new JsonObject
            {
                ["utc"] = _clock().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                ["run_id"] = runId,
                ["folder"] = run.Dir,
                ["outcome"] = outcome,
                ["elapsed_ms"] = elapsedMs,
            };
            lock (_indexGate)
            {
                File.AppendAllText(Path.Combine(_runsDir, IndexFileName), line.ToJsonString(JsonCodec.Options) + "\n", Encoding.UTF8);
            }
        }, "end " + runId);
    }

    // ── per-document history (M2, ADR-026 §2) ────────────────────────────────────────────────

    public const string HistoryDirName = "history";

    /// <summary>The history folder, next to the runs folder (<c>%LOCALAPPDATA%\rift\history</c>).</summary>
    public string HistoryDir => Path.Combine(Path.GetDirectoryName(_runsDir.TrimEnd('\\', '/')) ?? _runsDir, HistoryDirName);

    /// <summary>
    /// The history file key of a document: the first 16 hex characters of the SHA-256 of its path (or its title when it has
    /// none), so that no path appears in a file name.
    /// </summary>
    public static string HistoryKey(string? pathOrTitle)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes((pathOrTitle ?? string.Empty).ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>Appends one change receipt (redacted) to the document's history. Never throws.</summary>
    public void AppendHistory(string key, JsonObject line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string file = HistoryFile(key);
        Try(() =>
        {
            Directory.CreateDirectory(HistoryDir);
            lock (_indexGate)
            {
                File.AppendAllText(file, Redact(line.ToJsonString(JsonCodec.Options), null) + "\n", Encoding.UTF8);
            }
        }, "history append");
    }

    /// <summary>The newest <paramref name="limit"/> receipts of a document, newest first. Empty when there is no history.</summary>
    public IReadOnlyList<JsonObject> ReadHistory(string key, int limit)
    {
        var lines = new List<JsonObject>();
        string file = HistoryFile(key);
        Try(() =>
        {
            if (!File.Exists(file))
            {
                return;
            }
            lock (_indexGate)
            {
                foreach (string line in File.ReadAllLines(file, Encoding.UTF8))
                {
                    if (JsonCodec.TryParseNode(Encoding.UTF8.GetBytes(line)) is JsonObject o)
                    {
                        lines.Add(o);
                    }
                }
            }
        }, "history read");
        lines.Reverse();
        return lines.Count <= limit ? lines : lines.GetRange(0, Math.Max(0, limit));
    }

    /// <summary>
    /// Sets <c>reason_given</c> on one receipt — the newest when <paramref name="historyId"/> is null — and rewrites the file.
    /// Returns the updated receipt, or null when there is none.
    /// </summary>
    public JsonObject? AttachReason(string key, string? historyId, string reason)
    {
        string file = HistoryFile(key);
        JsonObject? updated = null;
        Try(() =>
        {
            if (!File.Exists(file))
            {
                return;
            }
            lock (_indexGate)
            {
                string[] raw = File.ReadAllLines(file, Encoding.UTF8);
                var parsed = new List<JsonObject?>();
                foreach (string line in raw)
                {
                    parsed.Add(JsonCodec.TryParseNode(Encoding.UTF8.GetBytes(line)) as JsonObject);
                }
                int index = -1;
                for (int i = parsed.Count - 1; i >= 0; i--)
                {
                    JsonObject? o = parsed[i];
                    if (o is null)
                    {
                        continue;
                    }
                    if (historyId is null || (o["history_id"] is JsonValue v && v.TryGetValue(out string? id) && id == historyId))
                    {
                        index = i;
                        break;
                    }
                }
                if (index < 0)
                {
                    return;
                }
                parsed[index]!["reason_given"] = Redact(reason, null);
                parsed[index]!["reason_given_utc"] = _clock().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
                var sb = new StringBuilder();
                for (int i = 0; i < raw.Length; i++)
                {
                    sb.Append(parsed[i] is JsonObject o ? o.ToJsonString(JsonCodec.Options) : raw[i]).Append('\n');
                }
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
                updated = parsed[index];
            }
        }, "history reason");
        return updated;
    }

    private string HistoryFile(string key) => Path.Combine(HistoryDir, Safe(key) + ".jsonl");

    /// <summary>Deletes run folders older than the retention and index lines older than 90 days. Returns the folders deleted.</summary>
    public int Purge()
    {
        int deleted = 0;
        Try(() =>
        {
            if (!Directory.Exists(_runsDir))
            {
                return;
            }
            DateTimeOffset limit = _clock() - _retention;
            foreach (string dir in Directory.GetDirectories(_runsDir))
            {
                string name = Path.GetFileName(dir);
                if (name.Length >= 16
                    && DateTime.TryParseExact(name[..16], "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime started)
                    && new DateTimeOffset(started, TimeSpan.Zero) < limit)
                {
                    Directory.Delete(dir, recursive: true);
                    deleted++;
                }
            }
            PurgeIndex();
        }, "purge");
        return deleted;
    }

    private void PurgeIndex()
    {
        string index = Path.Combine(_runsDir, IndexFileName);
        lock (_indexGate)
        {
            if (!File.Exists(index))
            {
                return;
            }
            DateTimeOffset limit = _clock() - s_indexRetention;
            var kept = new List<string>();
            foreach (string line in File.ReadAllLines(index, Encoding.UTF8))
            {
                if (JsonCodec.TryParseNode(Encoding.UTF8.GetBytes(line)) is JsonObject o
                    && o["utc"] is JsonValue v && v.TryGetValue(out string? utc)
                    && DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when)
                    && when < limit)
                {
                    continue;
                }
                kept.Add(line);
            }
            File.WriteAllText(index, kept.Count == 0 ? string.Empty : string.Join("\n", kept) + "\n", Encoding.UTF8);
        }
    }

    private string Redact(string text, string? documentFolder) => Redactor.Redact(text, _redaction, documentFolder);

    private static string Safe(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        }
        return sb.Length == 0 ? "_" : sb.ToString();
    }

    private bool Try(Action action, string what)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Write("journal " + what + " failed: " + ex.Message);
            return false;
        }
    }
}

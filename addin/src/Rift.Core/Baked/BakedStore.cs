// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Rift.Core.IO;
using Rift.Core.Json;
using Rift.Core.Protocol;

namespace Rift.Core.Baked;

/// <summary>A script the assistant proposed to freeze, waiting in the inbox for the user's decision in Revit.</summary>
public sealed record BakedProposal(string Id, string Name, string Description, string Mode, string Script, string Sha256, DateTimeOffset ProposedUtc);

/// <summary>A command the user accepted in Revit: the script is frozen with its SHA-256.</summary>
public sealed record BakedCommand(string Name, string Description, string Mode, string Script, string Sha256, DateTimeOffset AcceptedUtc);

/// <summary>
/// Baked commands (ADR-034): the assistant proposes (<c>inbox/&lt;id&gt;.json</c>), only the user accepts in Revit
/// (<c>accepted/&lt;name&gt;.json</c>), and a command runs only when its script still has the SHA-256 recorded at
/// acceptance. Names are lower-case identifiers; scripts are bounded; at most <see cref="MaxProposals"/> wait at once.
/// Files are written atomically; a malformed file is ignored. Thread-safe.
/// </summary>
public sealed partial class BakedStore
{
    public const int MaxScriptBytes = 64 * 1024;
    public const int MaxProposals = 50;
    public const int MaxDescription = 500;

    private readonly string _inbox;
    private readonly string _accepted;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    public BakedStore(string root, Func<DateTimeOffset>? clock = null)
    {
        _inbox = Path.Combine(root, "inbox");
        _accepted = Path.Combine(root, "accepted");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised after a proposal is stored (the add-in tells the user on the activity card).</summary>
    public event Action<BakedProposal>? Proposed;

    public static bool IsValidName(string? name) => name is not null && NameRegex().IsMatch(name);

    public static string Hash(string script) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant();

    public BakedProposal Propose(string? name, string? description, string? mode, string? script)
    {
        if (!IsValidName(name))
        {
            throw Bad("name must be 3 to 40 lower-case letters, digits or underscores, starting with a letter");
        }
        if (mode is not ("query" or "modify"))
        {
            throw Bad("mode must be query or modify");
        }
        if (string.IsNullOrWhiteSpace(script) || Encoding.UTF8.GetByteCount(script) > MaxScriptBytes)
        {
            throw Bad("script is empty or above 64 KiB");
        }
        string text = Sanitizer.Sanitize(description ?? string.Empty, MaxDescription);
        lock (_gate)
        {
            if (Proposals().Count >= MaxProposals)
            {
                throw Bad("the inbox already holds " + MaxProposals.ToString(CultureInfo.InvariantCulture) + " proposals: ask the user to review them in Revit");
            }
            var proposal = new BakedProposal(Guid.NewGuid().ToString("N")[..12], name!, text, mode, script, Hash(script), _clock());
            Write(Path.Combine(_inbox, proposal.Id + ".json"), ToJson(proposal));
            Proposed?.Invoke(proposal);
            return proposal;
        }
    }

    public IReadOnlyList<BakedProposal> Proposals()
    {
        lock (_gate)
        {
            return Read(_inbox, ProposalOf).OrderBy(p => p.ProposedUtc).ToArray();
        }
    }

    public IReadOnlyList<BakedCommand> Commands()
    {
        lock (_gate)
        {
            return Read(_accepted, CommandOf).OrderBy(c => c.Name, StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>The user accepted <paramref name="id"/> in Revit: frozen under its name (replacing an older version).</summary>
    public BakedCommand Accept(string id)
    {
        lock (_gate)
        {
            BakedProposal p = Proposals().FirstOrDefault(x => x.Id == id) ?? throw Bad("no proposal " + id);
            if (Hash(p.Script) != p.Sha256)
            {
                throw new OperationException(ErrorCodes.HashMismatch, "the proposal changed since it was proposed; it was not accepted", refused: true);
            }
            var command = new BakedCommand(p.Name, p.Description, p.Mode, p.Script, p.Sha256, _clock());
            Write(Path.Combine(_accepted, p.Name + ".json"), ToJson(command));
            File.Delete(Path.Combine(_inbox, p.Id + ".json"));
            return command;
        }
    }

    public void Reject(string id)
    {
        lock (_gate)
        {
            string path = Path.Combine(_inbox, id + ".json");
            if (IdRegex().IsMatch(id) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>The accepted command <paramref name="name"/>, after checking its script against the recorded SHA-256.</summary>
    public BakedCommand Find(string? name)
    {
        if (!IsValidName(name))
        {
            throw Bad("no baked command " + Sanitizer.Sanitize(name ?? string.Empty, 40));
        }
        BakedCommand c = Commands().FirstOrDefault(x => x.Name == name)
            ?? throw Bad("no baked command " + name + " (list_baked_tools lists them; a proposal runs only once the user accepted it in Revit)");
        if (Hash(c.Script) != c.Sha256)
        {
            throw new OperationException(ErrorCodes.HashMismatch, "the script of " + name + " changed since the user accepted it: it does not run", refused: true);
        }
        return c;
    }

    private static OperationException Bad(string message) => new(ErrorCodes.BadRequest, message, refused: true);

    private static void Write(string path, JsonObject json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Environment.TickCount64.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(tmp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        AtomicFile.MoveReplacing(tmp, path);
    }

    private static IEnumerable<T> Read<T>(string folder, Func<JsonObject, T?> parse)
        where T : class
    {
        if (!Directory.Exists(folder))
        {
            yield break;
        }
        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            T? item = null;
            try
            {
                item = JsonNode.Parse(File.ReadAllText(file)) is JsonObject o ? parse(o) : null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or FormatException or InvalidOperationException)
            {
            }
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    private static JsonObject ToJson(BakedProposal p) => new()
    {
        ["id"] = p.Id, ["name"] = p.Name, ["description"] = p.Description, ["mode"] = p.Mode,
        ["sha256"] = p.Sha256, ["proposed_utc"] = p.ProposedUtc.ToString("O", CultureInfo.InvariantCulture), ["script"] = p.Script,
    };

    private static JsonObject ToJson(BakedCommand c) => new()
    {
        ["name"] = c.Name, ["description"] = c.Description, ["mode"] = c.Mode,
        ["sha256"] = c.Sha256, ["accepted_utc"] = c.AcceptedUtc.ToString("O", CultureInfo.InvariantCulture), ["script"] = c.Script,
    };

    private static BakedProposal? ProposalOf(JsonObject o) =>
        S(o, "id") is { } id && IdRegex().IsMatch(id) && S(o, "name") is { } n && IsValidName(n) && S(o, "mode") is "query" or "modify" && S(o, "script") is { } script
            ? new BakedProposal(id, n, S(o, "description") ?? string.Empty, S(o, "mode")!, script, S(o, "sha256") ?? string.Empty,
                DateTimeOffset.Parse(S(o, "proposed_utc") ?? "2000-01-01T00:00:00Z", CultureInfo.InvariantCulture))
            : null;

    private static BakedCommand? CommandOf(JsonObject o) =>
        S(o, "name") is { } n && IsValidName(n) && S(o, "mode") is "query" or "modify" && S(o, "script") is { } script
            ? new BakedCommand(n, S(o, "description") ?? string.Empty, S(o, "mode")!, script, S(o, "sha256") ?? string.Empty,
                DateTimeOffset.Parse(S(o, "accepted_utc") ?? "2000-01-01T00:00:00Z", CultureInfo.InvariantCulture))
            : null;

    private static string? S(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    [GeneratedRegex("^[a-z][a-z0-9_]{2,39}$")]
    private static partial Regex NameRegex();

    [GeneratedRegex("^[0-9a-f]{12}$")]
    private static partial Regex IdRegex();
}

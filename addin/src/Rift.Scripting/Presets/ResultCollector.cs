// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Rift.Core.Json;

namespace Rift.Scripting.Presets;

/// <summary>
/// Where an audit preset's <c>dump(name, obj)</c> lands instead of a file (ADR-024). The preset serialises its document
/// itself with Python's <c>json.dumps</c>, so that the number text of the former chain survives (<c>594.0</c> stays
/// <c>594.0</c>, which System.Text.Json would write <c>594</c>); the collector parses it and keeps that text.
/// </summary>
public interface IResultCollector
{
    /// <summary>Called from the preset (Python). A second document with the same name replaces the first.</summary>
    void Put(string name, string json);

    /// <summary>
    /// The JSON text of a document dumped earlier in the same job, or null. Called from the merge preset (Python), which
    /// reads the documents of the presets that ran before it in <c>audit.all</c> instead of files.
    /// </summary>
    string? Text(string name);

    IReadOnlyDictionary<string, JsonNode?> Documents { get; }
}

/// <summary>
/// In-memory collector, one per preset run or one per <c>audit.all</c> job. Bounded: names like
/// <c>01_vues_lignes_cartouches</c> or <c>00_TOUT</c>, 64 MiB in all.
/// </summary>
public sealed partial class MemoryResultCollector : IResultCollector
{
    public const int MaxTotalBytes = 64 * 1024 * 1024;

    private readonly Dictionary<string, JsonNode?> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _sizes = new(StringComparer.Ordinal);
    private int _total;

    public IReadOnlyDictionary<string, JsonNode?> Documents => _documents;

    /// <summary>UTF-8 size of the JSON text received for each document.</summary>
    public IReadOnlyDictionary<string, int> Sizes => _sizes;

    public static bool IsDocumentName(string? name) => name is not null && DocumentName().IsMatch(name);

    public string? Text(string name) => name is not null && _texts.TryGetValue(name, out string? text) ? text : null;

    public void Put(string name, string json)
    {
        if (!IsDocumentName(name))
        {
            throw new ArgumentException("document name must look like 01_some_name", nameof(name));
        }
        ArgumentNullException.ThrowIfNull(json);
        int size = Encoding.UTF8.GetByteCount(json);
        int previous = _sizes.TryGetValue(name, out int s) ? s : 0;
        if (_total - previous + size > MaxTotalBytes)
        {
            throw new InvalidOperationException("preset documents exceed " + (MaxTotalBytes / (1024 * 1024)) + " MiB");
        }
        JsonNode? node;
        try
        {
            // JsonNode.Parse keeps each number's original text (JsonElement-backed values).
            node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("document " + name + " is not valid JSON: " + ex.Message, nameof(json), ex);
        }
        _documents[name] = node;
        _texts[name] = json;
        _sizes[name] = size;
        _total += size - previous;
    }

    /// <summary>
    /// A copy of a document where every string — values and property names, which come from the model — went through the
    /// ADR-020 sanitiser. Numbers, booleans and nulls are cloned as they are (their text is kept).
    /// </summary>
    public static JsonNode? Sanitized(JsonNode? node) => node switch
    {
        null => null,
        JsonObject o => SanitizedObject(o),
        JsonArray a => SanitizedArray(a),
        JsonValue v when v.TryGetValue(out string? text) => JsonValue.Create(Sanitizer.Sanitize(text)),
        _ => node.DeepClone(),
    };

    private static JsonObject SanitizedObject(JsonObject o)
    {
        var copy = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> kv in o)
        {
            // Two keys that only differ by stripped characters collapse into one: the later wins, as in a JSON parser.
            copy[Sanitizer.Sanitize(kv.Key, 1024)] = Sanitized(kv.Value);
        }
        return copy;
    }

    private static JsonArray SanitizedArray(JsonArray a)
    {
        var copy = new JsonArray();
        foreach (JsonNode? item in a)
        {
            copy.Add(Sanitized(item));
        }
        return copy;
    }

    [GeneratedRegex("^[0-9]{2}_[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentName();
}

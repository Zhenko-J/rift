// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Rift.Core.Json;

/// <summary>The single JSON façade of the add-in (ADR-015): UTF-8 unescaped, depth 32, snake_case, nulls omitted.</summary>
public static class JsonCodec
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 32,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static readonly JsonDocumentOptions s_documentOptions = new() { MaxDepth = 32 };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string SerializeToString<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options);

    /// <summary>Parses to a node tree; returns null on malformed input instead of throwing.</summary>
    public static JsonNode? TryParseNode(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return JsonNode.Parse(utf8, null, s_documentOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

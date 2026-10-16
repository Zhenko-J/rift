// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

using IronPython.Runtime;

using Rift.Core.Json;

namespace Rift.Scripting.Python;

/// <summary>
/// DLR objects → JSON (ADR-015) and JSON → Python for <c>inputs</c>. Revit types are converted by the host's
/// <see cref="ForeignConverter"/>; every string (keys included) goes through the ADR-020 sanitiser; NaN and infinities
/// become null; depth is capped at 32.
/// </summary>
public static class PyResultConverter
{
    private const int MaxDepth = 32;

    public static JsonNode? ToJson(object? value, ForeignConverter? foreign = null) => Convert(value, 0, foreign);

    /// <summary>JSON → Python objects (dict, list, str, int, float, bool, None) for the <c>inputs</c> variable.</summary>
    public static object? ToPython(JsonNode? node, int depth = 0)
    {
        if (node is null || depth > MaxDepth)
        {
            return null;
        }
        switch (node)
        {
            case JsonObject obj:
                {
                    var dict = new PythonDictionary();
                    foreach (var kv in obj)
                    {
                        dict[kv.Key] = ToPython(kv.Value, depth + 1);
                    }
                    return dict;
                }
            case JsonArray arr:
                {
                    var list = new PythonList();
                    foreach (JsonNode? item in arr)
                    {
                        list.append(ToPython(item, depth + 1));
                    }
                    return list;
                }
            default:
                {
                    JsonElement e = node.GetValue<JsonElement>();
                    return e.ValueKind switch
                    {
                        JsonValueKind.String => e.GetString(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Number when e.TryGetInt32(out int i) => i,
                        JsonValueKind.Number when e.TryGetInt64(out long l) => new BigInteger(l),
                        JsonValueKind.Number => e.GetDouble(),
                        _ => null,
                    };
                }
        }
    }

    private static JsonNode? Convert(object? value, int depth, ForeignConverter? foreign)
    {
        if (value is null || depth > MaxDepth)
        {
            return null;
        }
        switch (value)
        {
            case string s:
                return JsonValue.Create(Sanitizer.Sanitize(s));
            case bool b:
                return JsonValue.Create(b);
            case int i:
                return JsonValue.Create((long)i);   // every integer is a long: one JSON number type for callers
            case long l:
                return JsonValue.Create(l);
            case BigInteger bi:
                return bi >= long.MinValue && bi <= long.MaxValue ? JsonValue.Create((long)bi) : JsonValue.Create(bi.ToString(CultureInfo.InvariantCulture));
            case double d:
                return double.IsFinite(d) ? JsonValue.Create(d) : null;
            case float f:
                return float.IsFinite(f) ? JsonValue.Create((double)f) : null;
            case decimal m:
                return JsonValue.Create((double)m);
            case short or ushort or byte or sbyte or uint:
                return JsonValue.Create(System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case ulong ul:
                return ul <= long.MaxValue ? JsonValue.Create((long)ul) : JsonValue.Create(ul.ToString(CultureInfo.InvariantCulture));
            case Enum en:
                return JsonValue.Create(en.ToString());
            case Guid g:
                return JsonValue.Create(g.ToString());
            case DateTime dt:
                return JsonValue.Create(dt.ToString("O", CultureInfo.InvariantCulture));
            case DateTimeOffset dto:
                return JsonValue.Create(dto.ToString("O", CultureInfo.InvariantCulture));
            case JsonNode node:
                return node.DeepClone();
        }
        if (foreign is not null)
        {
            if (foreign(value, out JsonNode? converted))
            {
                return converted;
            }
        }
        switch (value)
        {
            case IDictionary dict:
                {
                    var obj = new JsonObject();
                    foreach (DictionaryEntry e in dict)
                    {
                        obj[Sanitizer.Sanitize(e.Key?.ToString() ?? "null", 256)] = Convert(e.Value, depth + 1, foreign);
                    }
                    return obj;
                }
            case IEnumerable seq:
                {
                    var arr = new JsonArray();
                    foreach (object? item in seq)
                    {
                        arr.Add(Convert(item, depth + 1, foreign));
                    }
                    return arr;
                }
            default:
                return JsonValue.Create(Sanitizer.Sanitize(value.ToString()));
        }
    }
}

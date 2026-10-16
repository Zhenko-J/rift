// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Text.Json.Nodes;

using Autodesk.Revit.DB;

using Rift.Core.Json;

namespace Rift.Revit.Serialization;

/// <summary>
/// The converter table of ADR-015 for Revit objects found in a script result: <c>ElementId</c> → integer, <c>XYZ</c> →
/// <c>{x, y, z}</c> (internal units), <c>Element</c> → <c>{id, class, category, name, type_id}</c>, <c>Parameter</c> →
/// <c>{name, bip, storage, value, display}</c>, <c>Color</c> → <c>[r, g, b]</c>, <c>BoundingBoxXYZ</c> → <c>{min, max}</c>,
/// <c>Category</c> → <c>{id, name}</c>, <c>Document</c> → <c>{title}</c>. Every string passes the ADR-020 sanitiser; a
/// property Revit refuses to give becomes null. Other types fall back to the generic rules of the engine.
/// </summary>
internal static class RevitJson
{
    public static string Sanitize(string? s) => Sanitizer.Sanitize(s);

    /// <summary><see cref="Rift.Scripting.ForeignConverter"/> for Revit types.</summary>
    public static bool TryConvert(object value, out JsonNode? node)
    {
        node = null;
        switch (value)
        {
            case ElementId id:
                node = JsonValue.Create(id.Value);
                return true;
            case XYZ p:
                node = Point(p);
                return true;
            case BoundingBoxXYZ bb:
                node = new JsonObject { ["min"] = Point(bb.Min), ["max"] = Point(bb.Max) };
                return true;
            case Color c:
                node = Safe(() => c.IsValid ? new JsonArray(c.Red, c.Green, c.Blue) : null);
                return true;
            case Parameter prm:
                node = ParameterJson(prm);
                return true;
            case Category cat:
                node = new JsonObject { ["id"] = Safe(() => JsonValue.Create(cat.Id.Value)), ["name"] = Safe(() => JsonValue.Create(Sanitize(cat.Name))) };
                return true;
            case Element e:
                node = new JsonObject
                {
                    ["id"] = Safe(() => JsonValue.Create(e.Id.Value)),
                    ["class"] = e.GetType().Name,
                    ["category"] = Safe(() => e.Category is { } cat2 ? JsonValue.Create(Sanitize(cat2.Name)) : null),
                    ["name"] = Safe(() => JsonValue.Create(Sanitize(e.Name))),
                    ["type_id"] = Safe(() => e.GetTypeId() is { } t && t != ElementId.InvalidElementId ? JsonValue.Create(t.Value) : null),
                };
                return true;
            case Document d:
                node = new JsonObject { ["title"] = Safe(() => JsonValue.Create(Sanitize(d.Title))) };
                return true;
            default:
                return false;
        }
    }

    private static JsonObject Point(XYZ p) => new() { ["x"] = Finite(p.X), ["y"] = Finite(p.Y), ["z"] = Finite(p.Z) };

    private static JsonNode? Finite(double d) => double.IsFinite(d) ? JsonValue.Create(d) : null;

    private static JsonObject ParameterJson(Parameter prm)
    {
        JsonNode? value = Safe(() => prm.StorageType switch
        {
            StorageType.Double => Finite(prm.AsDouble()),
            StorageType.Integer => JsonValue.Create(prm.AsInteger()),
            StorageType.String => prm.AsString() is { } s ? JsonValue.Create(Sanitize(s)) : null,
            StorageType.ElementId => JsonValue.Create(prm.AsElementId().Value),
            _ => null,
        });
        return new JsonObject
        {
            ["name"] = Safe(() => JsonValue.Create(Sanitize(prm.Definition?.Name))),
            ["bip"] = Safe(() => prm.Definition is InternalDefinition def && def.BuiltInParameter != BuiltInParameter.INVALID ? JsonValue.Create(def.BuiltInParameter.ToString()) : null),
            ["storage"] = prm.StorageType.ToString(),
            ["value"] = value,
            ["display"] = Safe(() => prm.AsValueString() is { } vs ? JsonValue.Create(Sanitize(vs)) : null),
        };
    }

    private static JsonNode? Safe(Func<JsonNode?> f)
    {
        try
        {
            return f();
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }
}

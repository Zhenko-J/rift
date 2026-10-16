// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Rift.Scripting.Tools;

/// <summary>One typed tool (M3, ADR-032): its name, whether it writes, and its embedded Python source (prelude included).</summary>
public sealed record TypedTool(string Name, bool Writes, string Source);

/// <summary>
/// The typed tools shipped with the add-in, embedded as <c>Rift.Scripting.Tools.&lt;name&gt;.py</c> resources. The first line
/// of each script declares <c># rift-tool: read</c> or <c># rift-tool: write</c>; <c>_common.py</c> is prepended to every
/// source (helpers: ids, names, categories, parameters in millimetres, <c>tx()</c>). Built once, read-only.
/// </summary>
public sealed class TypedToolCatalog
{
    public const string ResourcePrefix = "Rift.Scripting.Tools.";
    public const string CommonName = "_common";

    private readonly Dictionary<string, TypedTool> _byName;

    public TypedToolCatalog(IEnumerable<TypedTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public static TypedToolCatalog Default { get; } = Load(typeof(TypedToolCatalog).Assembly);

    public IReadOnlyCollection<TypedTool> All => _byName.Values;

    public IEnumerable<string> Names => _byName.Keys.OrderBy(n => n, StringComparer.Ordinal);

    public TypedTool? Find(string name) => _byName.TryGetValue(name, out TypedTool? t) ? t : null;

    /// <summary>Number of prelude lines, so that a line reported by the engine can be mapped back to the tool's own file.</summary>
    public static int PreludeLines { get; private set; }

    public static TypedToolCatalog Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string common = Read(assembly, ResourcePrefix + CommonName + ".py")
            ?? throw new InvalidOperationException("typed tool prelude missing: " + CommonName + ".py");
        PreludeLines = common.Split('\n').Length;
        var tools = new List<TypedTool>();
        foreach (string resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(ResourcePrefix, StringComparison.Ordinal) && r.EndsWith(".py", StringComparison.Ordinal)))
        {
            string name = resource[ResourcePrefix.Length..^3];
            if (name == CommonName)
            {
                continue;
            }
            string body = Read(assembly, resource)!;
            string head = body.Split('\n', 2)[0].Trim();
            bool writes = head switch
            {
                "# rift-tool: write" => true,
                "# rift-tool: read" => false,
                _ => throw new InvalidOperationException("typed tool " + name + " must start with '# rift-tool: read' or '# rift-tool: write'"),
            };
            tools.Add(new TypedTool(name, writes, common + "\n" + body));
        }
        return new TypedToolCatalog(tools);
    }

    private static string? Read(Assembly assembly, string resource)
    {
        using Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
        {
            return null;
        }
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

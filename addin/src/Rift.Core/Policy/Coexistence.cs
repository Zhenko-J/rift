// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Rift.Core.Policy;

/// <summary>What else is loaded in the Revit process (ADR-019): pyRevit, and other assistant add-ins matched by name.</summary>
public sealed record CoexistenceReport(bool PyRevitLoaded, IReadOnlyList<string> OtherAgents)
{
    public static CoexistenceReport None { get; } = new(false, Array.Empty<string>());

    public bool OtherAgentActive => OtherAgents.Count > 0;
}

/// <summary>
/// ADR-019: another assistant add-in in the same Revit can act on the model outside RIFT's guards, so RIFT reports it in
/// <c>get_context</c> and refuses to keep a change while it is loaded, unless <c>coexist_with_other_agents</c> is set.
/// Detection is by assembly name; this function is pure so that it is tested without Revit.
/// </summary>
public static class Coexistence
{
    /// <summary>
    /// Assistant add-ins known to drive Revit from an MCP client. The broad <c>*Agent*</c> of the ADR's first wording is
    /// not used: it would match unrelated assemblies.
    /// </summary>
    public static IReadOnlyList<string> DefaultAgentPatterns { get; } =
        new[] { "RvtMcp*", "revit_mcp*", "RevitMCP*", "*mcp-server-for-revit*" };

    private const int MaxReported = 20;

    public static CoexistenceReport Detect(IEnumerable<string?> assemblyNames, IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(assemblyNames);
        ArgumentNullException.ThrowIfNull(patterns);
        bool pyRevit = false;
        var agents = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        Regex[] compiled = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Compile).ToArray();
        foreach (string? name in assemblyNames)
        {
            if (string.IsNullOrEmpty(name) || name.StartsWith(Brand.Product + ".", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (name.StartsWith("pyRevitLoader", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("pyRevitLabs", StringComparison.OrdinalIgnoreCase))
            {
                pyRevit = true;
            }
            if (compiled.Any(r => r.IsMatch(name)) && agents.Count < MaxReported)
            {
                agents.Add(name);
            }
        }
        return new CoexistenceReport(pyRevit, agents.ToArray());
    }

    /// <summary>Glob on an assembly name: <c>*</c> any run of characters, <c>?</c> one character, case-insensitive.</summary>
    public static bool Matches(string name, string pattern) => Compile(pattern).IsMatch(name);

    private static Regex Compile(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}

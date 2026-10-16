// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Rift.Scripting.Presets;

/// <summary>One audit preset: its <c>op: tool</c> name, its embedded Python source and the document it dumps.</summary>
/// <param name="Name"><c>audit.p1</c> …</param>
/// <param name="ResourceName">Manifest resource holding the source, or null when <see cref="Source"/> is given directly (tests).</param>
/// <param name="Document">Name passed to <c>dump()</c>, which is also the file name written by <c>audit_template_write</c>.</param>
public sealed record Preset(string Name, string? ResourceName, string Document, string Title)
{
    /// <summary>Source given inline instead of a resource (tests only).</summary>
    public string? Source { get; init; }

    /// <summary>
    /// A merge step: reads the documents the other presets dumped in the same job (<c>rift_collector.Text</c>). It is run
    /// last by <c>audit.all</c>; on its own it only sees an empty collector.
    /// </summary>
    public bool Merge { get; init; }
}

/// <summary>
/// The audit presets shipped with the add-in (ADR-024), embedded in this assembly as Python resources, in the order
/// <c>audit.all</c> runs them (p1 … p5, then the merge p6). M1 tranche 4 shipped p1-p3; tranche 5 adds p4, p5, p6 and
/// <see cref="AllName"/>.
/// </summary>
public sealed class PresetCatalog
{
    /// <summary>The tool name that runs every preset of the catalogue in one job, sharing one collector.</summary>
    public const string AllName = "audit.all";

    private readonly Dictionary<string, Preset> _byName;
    private readonly Assembly _assembly;

    public PresetCatalog(IEnumerable<Preset> presets, Assembly? assembly = null)
    {
        ArgumentNullException.ThrowIfNull(presets);
        All = presets.ToArray();
        if (All.Any(p => p.Name == AllName))
        {
            throw new ArgumentException(AllName + " is reserved for the whole catalogue", nameof(presets));
        }
        _byName = All.ToDictionary(p => p.Name, StringComparer.Ordinal);
        _assembly = assembly ?? typeof(PresetCatalog).Assembly;
    }

    public static PresetCatalog Default { get; } = new(new[]
    {
        new Preset("audit.p1", "Rift.Scripting.Presets.audit_p1_views_lines_titleblocks.py", "01_vues_lignes_cartouches", "audit p1 view templates, legends, title blocks, lines"),
        new Preset("audit.p2", "Rift.Scripting.Presets.audit_p2_text_dimensions.py", "02_notes_cotations", "audit p2 text and dimension types"),
        new Preset("audit.p3", "Rift.Scripting.Presets.audit_p3_filled_regions.py", "03_pochages", "audit p3 filled regions and fill patterns"),
        new Preset("audit.p4", "Rift.Scripting.Presets.audit_p4_schedules.py", "04_nomenclatures", "audit p4 schedules"),
        new Preset("audit.p5", "Rift.Scripting.Presets.audit_p5_parameters.py", "05_parametres", "audit p5 project and shared parameters"),
        new Preset("audit.p6", "Rift.Scripting.Presets.audit_p6_merge.py", "00_TOUT", "audit p6 merged report and legends on sheets") { Merge = true },
    });

    /// <summary>Every preset, in the order <c>audit.all</c> runs them.</summary>
    public IReadOnlyList<Preset> All { get; }

    /// <summary>Tool names a client may ask for: each preset, then <see cref="AllName"/>.</summary>
    public IEnumerable<string> Names => All.Select(p => p.Name).Append(AllName);

    public Preset? Find(string name) => _byName.TryGetValue(name, out Preset? p) ? p : null;

    /// <summary>The Python source of a preset. Throws when the resource is missing (a build error, not a user error).</summary>
    public string Source(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (preset.Source is not null)
        {
            return preset.Source;
        }
        using Stream stream = _assembly.GetManifestResourceStream(preset.ResourceName!)
            ?? throw new InvalidOperationException("preset resource missing: " + preset.ResourceName);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

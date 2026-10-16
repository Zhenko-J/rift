// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Rift.Core.Config;
using Rift.Core.Logging;
using Rift.Core.Policy;
using Rift.Revit.Dispatch;
using Rift.Revit.Guard;
using Rift.Revit.Serialization;
using Rift.Scripting;
using Rift.Scripting.Runs;

namespace Rift.Revit.Ops;

/// <summary>
/// What a run sees inside Revit (ADR-015): the dispatcher's <see cref="UIApplication"/>, the active document and its class
/// (ADR-009), the extras <c>DB</c>, <c>UI</c>, <c>System</c> and <c>eid</c>, the Revit converter table, a fresh
/// <see cref="ReadGuard"/> or <see cref="WriteGuard"/> (M2) and the Keep/Discard dialog. Created on the API thread by
/// <see cref="RunQueryOp"/>.
/// </summary>
internal sealed class RevitRunHost : IRunHost
{
    private static readonly Func<long, ElementId> s_eid = id => new ElementId(id);

    private readonly UIApplication _uiapp;
    private readonly FileLog _log;

    public RevitRunHost(UIApplication uiapp, Func<RiftConfig> config, FileLog log, Func<string?, string?, SessionPolicy?>? session = null)
    {
        _uiapp = uiapp;
        _log = log;
        UIDocument? uidoc = uiapp.ActiveUIDocument;
        Document? doc = uidoc?.Document;
        string? path = doc is null ? null : DocumentPath(doc);
        RiftConfig settings = config();
        Target = new RunTarget(uiapp, uiapp.Application, uidoc, doc, doc?.Title, path,
            doc is null ? null : DocumentClassifier.Name(DocumentClassifier.Classify(path, settings)),
            Coexistence.Detect(LoadedAssemblyNames(), settings.AgentPatterns), settings.CoexistWithOtherAgents,
            session?.Invoke(path, doc?.Title));
        Extras = new Dictionary<string, object?>
        {
            ["DB"] = new ScriptNamespace("Autodesk.Revit.DB"),
            ["UI"] = new ScriptNamespace("Autodesk.Revit.UI"),
            ["System"] = new ScriptNamespace("System"),
            ["eid"] = s_eid,
        };
    }

    public RunTarget Target { get; }

    public IReadOnlyDictionary<string, object?> Extras { get; }

    public ForeignConverter? Converter => RevitJson.TryConvert;

    public IRunGuard CreateGuard() => new ReadGuard(_uiapp, _log);

    public IWriteGuard CreateWriteGuard() =>
        new WriteGuard(_uiapp, (Document?)Target.Doc ?? throw new InvalidOperationException("no active document"), _log);

    public IAskDialog? Ask => new AskDialog((UIDocument?)Target.UiDoc, _log);

    public static IRunHost Current(Func<RiftConfig> config, FileLog log, Func<string?, string?, SessionPolicy?>? session = null) =>
        new RevitRunHost(RevitDispatcher.Current, config, log, session);

    /// <summary>Names of the assemblies loaded in the process, every load context included (ADR-019).</summary>
    public static IEnumerable<string?> LoadedAssemblyNames() =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name);

    public static string? DocumentPath(Document d)
    {
        try
        {
            return string.IsNullOrEmpty(d.PathName) ? null : d.PathName;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }
}

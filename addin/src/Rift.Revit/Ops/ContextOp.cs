// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Rift.Core.Config;
using Rift.Core.Json;
using Rift.Core.Policy;
using Rift.Core.Protocol;
using Rift.Revit.Dispatch;

namespace Rift.Revit.Ops;

/// <summary>
/// <c>op: context</c>: Revit version and build, the active document (title, path, state, class of ADR-009), the open
/// documents, the write policy the active document would get (M2) and the state of the script engine and the other assistant add-ins loaded (ADR-019). Selection and levels come later.
/// </summary>
internal sealed class ContextOp : IOperationHandler
{
    private readonly string _addinVersion;
    private readonly Func<RiftConfig> _config;
    private readonly Func<JsonNode?> _engineState;

    public ContextOp(string addinVersion, Func<RiftConfig> config, Func<JsonNode?> engineState)
    {
        _addinVersion = addinVersion;
        _config = config;
        _engineState = engineState;
    }

    public string Op => "context";

    public bool NeedsApiThread => true;

    public JsonNode? Execute(RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        UIApplication uiapp = RevitDispatcher.Current;
        Autodesk.Revit.ApplicationServices.Application app = uiapp.Application;
        Document? active = uiapp.ActiveUIDocument?.Document;
        var documents = new JsonArray();
        foreach (Document d in app.Documents)
        {
            if (d.IsLinked)
            {
                continue;
            }
            documents.Add(Describe(d, active, _config()));
        }
        return new JsonObject
        {
            ["revit"] = new JsonObject
            {
                ["version"] = app.VersionNumber,
                ["build"] = app.VersionBuild,
                ["product"] = app.VersionName,
                ["language"] = app.Language.ToString(),
            },
            ["addin"] = new JsonObject { ["version"] = _addinVersion, ["pid"] = Environment.ProcessId },
            ["document"] = active is null ? null : Describe(active, active, _config()),
            ["documents"] = documents,
            ["policy"] = Policy(active, _config()),
            ["coexistence"] = CoexistenceOf(_config()),
            ["engine"] = _engineState(),
        };
    }

    /// <summary>ADR-019: pyRevit and other assistant add-ins loaded in this Revit, and whether changes are kept anyway.</summary>
    private static JsonObject CoexistenceOf(RiftConfig config)
    {
        CoexistenceReport report = Coexistence.Detect(RevitRunHost.LoadedAssemblyNames(), config.AgentPatterns);
        return new JsonObject
        {
            ["pyrevit_loaded"] = report.PyRevitLoaded,
            ["other_agents"] = new JsonArray(report.OtherAgents.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["coexist_with_other_agents"] = config.CoexistWithOtherAgents,
            ["changes_kept"] = !report.OtherAgentActive || config.CoexistWithOtherAgents,
        };
    }

    /// <summary>What a write would get on the active document (ADR-009), so that an assistant can plan before sending a script.</summary>
    private static JsonObject Policy(Document? active, RiftConfig config)
    {
        string? documentClass = active is null ? null : DocumentClassifier.Name(DocumentClassifier.Classify(RevitRunHost.DocumentPath(active), config));
        Scripting.Runs.PolicyDecision decision = Scripting.Runs.WritePolicy.Decide(documentClass, Scripting.Runs.WritePolicy.Auto, allowReference: false);
        return new JsonObject
        {
            ["read_only"] = false,
            ["milestone"] = "M2",
            ["run_modes"] = new JsonArray("query", "modify"),
            ["document_class"] = documentClass,
            ["write_policy"] = decision.Refused ? decision.RefusalCode : decision.Applied,
            ["dry_run_default"] = true,
        };
    }

    private static JsonObject Describe(Document d, Document? active, RiftConfig config)
    {
        string? path = RevitRunHost.DocumentPath(d);
        return new JsonObject
        {
            ["title"] = Sanitizer.Sanitize(d.Title, 260),
            ["path"] = path,
            ["class"] = DocumentClassifier.Name(DocumentClassifier.Classify(path, config)),
            ["is_family"] = d.IsFamilyDocument,
            ["is_modified"] = d.IsModified,
            ["is_workshared"] = d.IsWorkshared,
            ["is_active"] = active is not null && d.Equals(active),
            ["active_view"] = active is not null && d.Equals(active) && d.ActiveView is { } v ? v.Name + " (" + v.ViewType.ToString() + ", id " + v.Id.Value.ToString(CultureInfo.InvariantCulture) + ")" : null,
        };
    }
}

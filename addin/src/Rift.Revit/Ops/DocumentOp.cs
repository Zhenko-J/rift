// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Rift.Core;
using Rift.Core.Json;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Revit.Dispatch;
using Rift.Scripting.Runs;

namespace Rift.Revit.Ops;

/// <summary>
/// M4 document life cycle, <c>op: tool</c> names <c>document.open|save|close|sync</c>. Opening changes no model. Saving,
/// closing and synchronising are the user's decisions (ADR-033 §3): each one is <b>always</b> confirmed in Revit by a
/// dialog whose default is "no", whatever the session mode and the client's permission rules; "no" answers
/// <c>USER_DECLINED</c>. Runs on the API thread between jobs, so never while a script runs. Revit cannot close the active
/// document through its API: <c>close</c> takes another open document.
/// </summary>
internal sealed class DocumentOp : IToolHandler
{
    public const string Open = "document.open";
    public const string Save = "document.save";
    public const string Close = "document.close";
    public const string Sync = "document.sync";
    public const string ConfirmDialogId = "rift.confirm";

    private static readonly string[] s_names = { Open, Save, Close, Sync };
    private static readonly string[] s_extensions = { ".rvt", ".rte", ".rfa", ".rft" };

    private readonly FileLog _log;
    private readonly Func<string, string, string, bool> _confirm;

    /// <param name="confirm">The dialog (instruction, content, verb) → true only on an explicit yes; replaced in tests.</param>
    public DocumentOp(FileLog log, Func<string, string, string, bool>? confirm = null)
    {
        _log = log;
        _confirm = confirm ?? Confirm;
    }

    public string Prefix => "document.";

    public IEnumerable<string> Names => s_names;

    public JsonNode? Execute(string name, RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        UIApplication uiapp = RevitDispatcher.Current;
        JsonObject inputs = request.Args?["inputs"] as JsonObject ?? new JsonObject();
        return name switch
        {
            Open => OpenDocument(uiapp, inputs),
            Save => SaveDocument(uiapp, inputs),
            Close => CloseDocument(uiapp, inputs),
            Sync => SyncDocument(uiapp, inputs),
            _ => throw new OperationException(ErrorCodes.BadRequest, "unknown document operation " + name, refused: true),
        };
    }

    private JsonNode OpenDocument(UIApplication uiapp, JsonObject inputs)
    {
        string? path = Str(inputs, "path");
        if (path is null || !Path.IsPathFullyQualified(path) || !s_extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            throw new OperationException(ErrorCodes.BadRequest, "path must be the absolute path of a .rvt, .rte, .rfa or .rft file", refused: true);
        }
        if (!File.Exists(path))
        {
            throw new OperationException(ErrorCodes.BadRequest, "no such file", refused: true);
        }
        UIDocument opened = uiapp.OpenAndActivateDocument(path);
        _log.Write("document opened by the assistant");
        return Describe(opened.Document, "opened");
    }

    private JsonNode SaveDocument(UIApplication uiapp, JsonObject inputs)
    {
        Document doc = Find(uiapp, Str(inputs, "document"));
        if (string.IsNullOrEmpty(RevitRunHost.DocumentPath(doc)))
        {
            throw new OperationException(ErrorCodes.BadRequest, "the document was never saved: save it once yourself (Save As) first", refused: true);
        }
        Ask("save " + doc.Title + "?", "The assistant asks to save this document to disk. Nothing is saved unless you click save.", "save");
        doc.Save();
        _log.Write("document saved after the user's confirmation");
        return Describe(doc, "saved");
    }

    private JsonNode CloseDocument(UIApplication uiapp, JsonObject inputs)
    {
        string? reference = Str(inputs, "document") ?? throw new OperationException(ErrorCodes.BadRequest, "document is required (title or path of an open document that is not the active one)", refused: true);
        Document doc = Find(uiapp, reference);
        if (uiapp.ActiveUIDocument?.Document is { } active && active.Equals(doc))
        {
            throw new OperationException(ErrorCodes.BadRequest, "Revit cannot close the active document through its API: activate another document first", refused: true);
        }
        bool save = inputs["save"] is JsonValue s && s.TryGetValue(out bool b) && b;
        Ask("close " + doc.Title + "?",
            save ? "The assistant asks to save and close this document." : "The assistant asks to close this document without saving: unsaved changes will be lost.",
            save ? "save and close" : "close without saving");
        JsonNode described = Describe(doc, save ? "saved and closed" : "closed");
        doc.Close(save);
        _log.Write("document closed after the user's confirmation (save=" + save + ")");
        return described;
    }

    private JsonNode SyncDocument(UIApplication uiapp, JsonObject inputs)
    {
        Document doc = Find(uiapp, Str(inputs, "document"));
        if (!doc.IsWorkshared)
        {
            throw new OperationException(ErrorCodes.BadRequest, "the document is not workshared", refused: true);
        }
        string comment = Sanitizer.Sanitize(Str(inputs, "comment") ?? "synchronised by the assistant after confirmation", 500);
        bool relinquish = !(inputs["relinquish"] is JsonValue r && r.TryGetValue(out bool keep) && !keep);
        Ask("synchronise " + doc.Title + " with central?", "The assistant asks to synchronise with central (comment: " + comment + ").", "synchronise");
        var options = new SynchronizeWithCentralOptions { Comment = comment, SaveLocalBefore = true, SaveLocalAfter = true };
        options.SetRelinquishOptions(new RelinquishOptions(relinquish));
        doc.SynchronizeWithCentral(new TransactWithCentralOptions(), options);
        _log.Write("document synchronised after the user's confirmation");
        return Describe(doc, "synchronised");
    }

    private void Ask(string instruction, string content, string verb)
    {
        if (!_confirm(instruction, content, verb))
        {
            throw new OperationException(ErrorCodes.UserDeclined, "the user answered no in Revit", refused: true);
        }
    }

    private bool Confirm(string instruction, string content, string verb)
    {
        try
        {
            var dialog = new TaskDialog(Brand.Product + " — confirm")
            {
                Id = ConfirmDialogId,
                MainInstruction = instruction,
                MainContent = content,
                CommonButtons = TaskDialogCommonButtons.None,
                AllowCancellation = true,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, verb);
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "no", "Default: nothing happens.");
            dialog.DefaultButton = TaskDialogResult.CommandLink2;
            return dialog.Show() == TaskDialogResult.CommandLink1;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException or ArgumentException)
        {
            _log.Write("confirm dialog failed, treated as no: " + ex.Message);
            return false;
        }
    }

    private static Document Find(UIApplication uiapp, string? reference)
    {
        if (reference is null)
        {
            return uiapp.ActiveUIDocument?.Document ?? throw new OperationException(ErrorCodes.NoDocument, "no active document", refused: true);
        }
        Document[] matches = uiapp.Application.Documents.Cast<Document>()
            .Where(d => !d.IsLinked && (string.Equals(d.Title, reference, StringComparison.Ordinal)
                || string.Equals(RevitRunHost.DocumentPath(d), reference, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new OperationException(ErrorCodes.BadRequest, matches.Length == 0 ? "no open document with that title or path" : "several open documents match; pass the path", refused: true);
    }

    private static JsonObject Describe(Document doc, string done) => new()
    {
        ["done"] = done,
        ["title"] = Sanitizer.Sanitize(doc.Title, 260),
        ["path"] = RevitRunHost.DocumentPath(doc),
        ["is_workshared"] = doc.IsWorkshared,
    };

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}

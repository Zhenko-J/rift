// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Rift.Core.IO;
using Rift.Core.Json;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Revit.Dispatch;
using Rift.Scripting.Runs;

namespace Rift.Revit.Ops;

/// <summary>
/// M4 exports, <c>op: tool</c> names <c>export.pdf|dwg|ifc|nwc|image</c>: fixed calls to Revit's exporters, outside the
/// read and write guards (which block exports during scripts). They write files, never the model: IFC needs an open
/// transaction, which is always rolled back. The folder is one the user chose (<see cref="ExportPaths"/>: absolute, never
/// RIFT's own folders), the file stem is sanitised, existing files are kept unless <c>overwrite</c>. Views are given by id
/// or exact name; the active view by default.
/// </summary>
internal sealed class ExportOp : IToolHandler
{
    public const string Pdf = "export.pdf";
    public const string Dwg = "export.dwg";
    public const string Ifc = "export.ifc";
    public const string Nwc = "export.nwc";
    public const string Image = "export.image";

    private static readonly string[] s_names = { Pdf, Dwg, Ifc, Nwc, Image };

    private readonly IReadOnlyList<string> _forbidden;
    private readonly FileLog _log;

    /// <param name="forbidden">Folders an export never writes into (RIFT's data folder, the add-in's folder).</param>
    public ExportOp(IReadOnlyList<string> forbidden, FileLog log)
    {
        _forbidden = forbidden;
        _log = log;
    }

    public string Prefix => "export.";

    public IEnumerable<string> Names => s_names;

    public JsonNode? Execute(string name, RequestEnvelope request, IProgress<(int Percent, string Message)> progress, CancellationToken cancellation)
    {
        UIApplication uiapp = RevitDispatcher.Current;
        Document doc = uiapp.ActiveUIDocument?.Document ?? throw new OperationException(ErrorCodes.NoDocument, "no active document", refused: true);
        JsonObject inputs = request.Args?["inputs"] as JsonObject ?? new JsonObject();
        CheckExpected(request, doc);

        string folder = ExportPaths.Folder(Str(inputs, "folder"), _forbidden);
        bool overwrite = inputs["overwrite"] is JsonValue ov && ov.TryGetValue(out bool o) && o;
        IReadOnlyList<View> views = Views(doc, inputs["views"]);
        string fallback = name is Ifc or Nwc ? doc.Title : views.Count == 1 ? views[0].Name : doc.Title;
        string stem = ExportPaths.Stem(Str(inputs, "name"), fallback);
        ExportPaths.CheckOverwrite(folder, stem, overwrite);
        Directory.CreateDirectory(folder);

        DateTime started = DateTime.UtcNow;
        progress.Report((10, name));
        bool ok;
        try
        {
            ok = name switch
            {
                Pdf => ExportPdf(doc, folder, stem, views, inputs),
                Dwg => ExportDwg(doc, folder, stem, views, inputs),
                Ifc => ExportIfc(doc, folder, stem, inputs),
                Nwc => ExportNwc(doc, folder, stem, views, inputs),
                Image => ExportImage(doc, folder, stem, views, inputs),
                _ => throw new OperationException(ErrorCodes.BadRequest, "unknown export " + name, refused: true),
            };
        }
        catch (OperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException or IOException)
        {
            _log.Write(name + " failed: " + ex.GetType().Name + " " + ex.Message);
            throw new OperationException(ErrorCodes.ExportFailed, name + " failed: " + Sanitizer.Sanitize(ex.Message, 300));
        }
        IReadOnlyList<string> written = ExportPaths.WrittenSince(folder, stem, started);
        if (!ok || written.Count == 0)
        {
            throw new OperationException(ErrorCodes.ExportFailed, name + ": Revit wrote no file (check the views and the settings)",
                new JsonObject { ["exporter_returned"] = ok });
        }
        progress.Report((100, name));
        return new JsonObject
        {
            ["format"] = name[(name.IndexOf('.') + 1)..],
            ["folder"] = folder,
            ["files"] = new JsonArray(written.Select(f => (JsonNode?)JsonValue.Create(Path.GetFileName(f))).ToArray()),
            ["bytes"] = written.Sum(f => new FileInfo(f).Length),
            ["views"] = new JsonArray(views.Select(v => (JsonNode?)new JsonObject
            {
                ["id"] = v.Id.Value,
                ["name"] = Sanitizer.Sanitize(v.Name, 260),
            }).ToArray()),
            ["model_changed"] = false,
        };
    }

    private static bool ExportPdf(Document doc, string folder, string stem, IReadOnlyList<View> views, JsonObject inputs)
    {
        var options = new PDFExportOptions
        {
            FileName = stem,
            Combine = !(inputs["combine"] is JsonValue c && c.TryGetValue(out bool combine) && !combine),
        };
        return doc.Export(folder, views.Select(v => v.Id).ToList(), options);
    }

    private static bool ExportDwg(Document doc, string folder, string stem, IReadOnlyList<View> views, JsonObject inputs)
    {
        string? setup = Str(inputs, "setup");
        DWGExportOptions options = setup is null ? new DWGExportOptions() : DWGExportOptions.GetPredefinedOptions(doc, setup)
            ?? throw new OperationException(ErrorCodes.BadRequest, "no DWG export setup named " + setup, refused: true);
        return doc.Export(folder, stem, views.Select(v => v.Id).ToList(), options);
    }

    private static bool ExportIfc(Document doc, string folder, string stem, JsonObject inputs)
    {
        var options = new IFCExportOptions
        {
            FileVersion = Str(inputs, "version") switch
            {
                null or "IFC4" => IFCVersion.IFC4,
                "IFC2x3" => IFCVersion.IFC2x3CV2,
                "IFC4x3" => IFCVersion.IFC4x3,
                string other => throw new OperationException(ErrorCodes.BadRequest, "version must be IFC2x3, IFC4 or IFC4x3, not " + other, refused: true),
            },
        };
        if (inputs["view"] is JsonNode viewRef)
        {
            options.FilterViewId = View(doc, viewRef).Id;
        }
        // The IFC exporter needs an open transaction; nothing of it is kept.
        using var t = new Transaction(doc, "RIFT IFC export");
        t.Start();
        try
        {
            return doc.Export(folder, stem, options);
        }
        finally
        {
            t.RollBack();
        }
    }

    private static bool ExportNwc(Document doc, string folder, string stem, IReadOnlyList<View> views, JsonObject inputs)
    {
        if (!OptionalFunctionalityUtils.IsNavisworksExporterAvailable())
        {
            throw new OperationException(ErrorCodes.ExporterUnavailable, "the Navisworks NWC exporter is not installed in this Revit", refused: true);
        }
        var options = new NavisworksExportOptions();
        if (inputs["views"] is JsonArray { Count: > 0 })
        {
            options.ExportScope = NavisworksExportScope.View;
            options.ViewId = views[0].Id;
        }
        else
        {
            options.ExportScope = NavisworksExportScope.Model;
        }
        doc.Export(folder, stem, options);
        return true;
    }

    private static bool ExportImage(Document doc, string folder, string stem, IReadOnlyList<View> views, JsonObject inputs)
    {
        int pixels = inputs["pixel_size"] is JsonValue p && p.TryGetValue(out int px) ? Math.Clamp(px, 256, 8192) : 2000;
        ImageFileType type = Str(inputs, "image_type")?.ToLowerInvariant() switch
        {
            null or "png" => ImageFileType.PNG,
            "jpg" or "jpeg" => ImageFileType.JPEGLossless,
            "tif" or "tiff" => ImageFileType.TIFF,
            string other => throw new OperationException(ErrorCodes.BadRequest, "image_type must be png, jpg or tif, not " + other, refused: true),
        };
        var options = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(folder, stem),
            HLRandWFViewsFileType = type,
            ShadowViewsFileType = type,
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = pixels,
            ImageResolution = ImageResolution.DPI_150,
        };
        options.SetViewsAndSheets(views.Select(v => v.Id).ToList());
        doc.ExportImage(options);
        return true;
    }

    private static IReadOnlyList<View> Views(Document doc, JsonNode? refs)
    {
        if (refs is not JsonArray list || list.Count == 0)
        {
            View active = doc.ActiveView ?? throw new OperationException(ErrorCodes.BadRequest, "no active view; pass views", refused: true);
            return new[] { active };
        }
        return list.Select(r => View(doc, r)).ToArray();
    }

    private static View View(Document doc, JsonNode? reference)
    {
        if (reference is JsonValue v && v.TryGetValue(out long id))
        {
            return doc.GetElement(new ElementId(id)) as View is { IsTemplate: false } byId
                ? byId
                : throw new OperationException(ErrorCodes.BadRequest, "no view with id " + id.ToString(CultureInfo.InvariantCulture), refused: true);
        }
        string text = reference?.ToString() ?? string.Empty;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return View(doc, JsonValue.Create(parsed));
        }
        View[] named = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(x => !x.IsTemplate && string.Equals(x.Name, text, StringComparison.Ordinal)).ToArray();
        return named.Length switch
        {
            1 => named[0],
            0 => throw new OperationException(ErrorCodes.BadRequest, "view '" + Sanitizer.Sanitize(text, 120) + "' not found", refused: true),
            _ => throw new OperationException(ErrorCodes.BadRequest, "view '" + Sanitizer.Sanitize(text, 120) + "' is ambiguous; pass its id", refused: true),
        };
    }

    private static void CheckExpected(RequestEnvelope request, Document doc) =>
        RunQueryOp.CheckExpectedDocument(request, new RunTarget(new object(), new object(), null, doc, doc.Title, RevitRunHost.DocumentPath(doc), null));

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}

// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;

using Rift.Core.Config;

namespace Rift.Core.Policy;

/// <summary>Document classes of ADR-009.</summary>
public enum DocumentClass
{
    Sandbox,
    Reference,
    Unsaved,
    Unmarked,
}

/// <summary>
/// ADR-009: a document is classified from its <b>file name</b> (never its title): <c>sandbox</c> when the name contains a
/// configured marker as a delimited word (case-insensitive; F-R6: the characters around it are not letters or digits, so
/// <c>Plan_SANDBOX.rvt</c> and <c>Plan - Copy.rvt</c> are copies, <c>Copyright.rte</c> is not), <c>reference</c> when the
/// name is in the local <c>reference_names</c> list, <c>unsaved</c> without a path, <c>unmarked</c> otherwise. Markers
/// that are not valid (<see cref="IsValidMarker"/>) are ignored. Pure function: the Revit side only passes <c>PathName</c>.
/// </summary>
public static class DocumentClassifier
{
    /// <summary>Shortest marker accepted (F-R6).</summary>
    public const int MinMarkerLength = 4;

    /// <summary>
    /// A marker is at least <see cref="MinMarkerLength"/> characters long and holds at least three letters or digits: a
    /// punctuation-only or very short marker would turn most file names into SANDBOX copies.
    /// </summary>
    public static bool IsValidMarker(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker) || marker.Trim().Length < MinMarkerLength)
        {
            return false;
        }
        int alnum = 0;
        foreach (char c in marker)
        {
            if (char.IsLetterOrDigit(c))
            {
                alnum++;
            }
        }
        return alnum >= 3;
    }

    /// <summary>True when <paramref name="marker"/> occurs in <paramref name="fileName"/> as a delimited word.</summary>
    public static bool ContainsMarker(string fileName, string marker)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (!IsValidMarker(marker))
        {
            return false;
        }
        string m = marker.Trim();
        for (int at = fileName.IndexOf(m, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = fileName.IndexOf(m, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = at + m.Length;
            bool before = at == 0 || !char.IsLetterOrDigit(fileName[at - 1]) || !char.IsLetterOrDigit(m[0]);
            bool after = end == fileName.Length || !char.IsLetterOrDigit(fileName[end]) || !char.IsLetterOrDigit(m[^1]);
            if (before && after)
            {
                return true;
            }
        }
        return false;
    }

    public static DocumentClass Classify(string? pathName, RiftConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(pathName))
        {
            return DocumentClass.Unsaved;
        }
        string file = FileNameOf(pathName);
        foreach (string marker in config.SandboxMarkers)
        {
            if (ContainsMarker(file, marker))
            {
                return DocumentClass.Sandbox;
            }
        }
        string stem = Path.GetFileNameWithoutExtension(file);
        foreach (string reference in config.ReferenceNames)
        {
            if (string.Equals(reference, file, StringComparison.OrdinalIgnoreCase)
                || string.Equals(reference, stem, StringComparison.OrdinalIgnoreCase))
            {
                return DocumentClass.Reference;
            }
        }
        return DocumentClass.Unmarked;
    }

    public static string Name(DocumentClass c) => c switch
    {
        DocumentClass.Sandbox => "sandbox",
        DocumentClass.Reference => "reference",
        DocumentClass.Unsaved => "unsaved",
        _ => "unmarked",
    };

    /// <summary>File name of a local, UNC or cloud path (both separators), without touching the file system.</summary>
    private static string FileNameOf(string path)
    {
        int cut = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return cut < 0 ? path : path[(cut + 1)..];
    }
}

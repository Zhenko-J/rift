// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Rift.Core.Protocol;

namespace Rift.Core.IO;

/// <summary>
/// Where an export may write (M4): an absolute local or UNC folder the user chose, never RIFT's own data folder (keys,
/// discovery, journal) nor the add-in's folder; a file stem without separators or reserved characters; an existing file
/// of that stem is kept unless the caller asked to overwrite. Pure: the Revit side only calls the exporter.
/// </summary>
public static class ExportPaths
{
    public const int MaxStem = 120;

    /// <summary>Validates <paramref name="folder"/> and returns its full path, or throws <c>BAD_REQUEST</c>.</summary>
    public static string Folder(string? folder, IEnumerable<string> forbidden)
    {
        ArgumentNullException.ThrowIfNull(forbidden);
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            throw new OperationException(ErrorCodes.BadRequest, "folder must be an absolute path the user chose", refused: true);
        }
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        foreach (string root in forbidden.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (string.Equals(full, r, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new OperationException(ErrorCodes.BadRequest, "exports never go into RIFT's own folders; choose another folder", refused: true);
            }
        }
        return full;
    }

    /// <summary>A file stem: invalid characters and separators replaced by '_', trimmed, bounded; <paramref name="fallback"/> when empty.</summary>
    public static string Stem(string? name, string fallback)
    {
        string source = string.IsNullOrWhiteSpace(name) ? fallback : name;
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':' };
        var sb = new StringBuilder(source.Length);
        foreach (char c in source)
        {
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
        }
        string stem = sb.ToString().Trim(' ', '.');
        if (stem.Length > MaxStem)
        {
            stem = stem[..MaxStem];
        }
        return stem.Length == 0 ? "export" : stem;
    }

    /// <summary>Files of <paramref name="folder"/> whose name starts with <paramref name="stem"/> (Revit appends view names).</summary>
    public static IReadOnlyList<string> Existing(string folder, string stem)
    {
        if (!Directory.Exists(folder))
        {
            return Array.Empty<string>();
        }
        return Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Refuses with <c>FILE_EXISTS</c> when files of that stem exist and <paramref name="overwrite"/> is false.</summary>
    public static void CheckOverwrite(string folder, string stem, bool overwrite)
    {
        IReadOnlyList<string> existing = Existing(folder, stem);
        if (existing.Count > 0 && !overwrite)
        {
            throw new OperationException(ErrorCodes.FileExists,
                existing.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " file(s) named " + stem + "* already in the folder; pass overwrite true to replace them",
                new System.Text.Json.Nodes.JsonObject { ["existing"] = existing.Count }, refused: true);
        }
    }

    /// <summary>Files of that stem written or changed at or after <paramref name="sinceUtc"/>.</summary>
    public static IReadOnlyList<string> WrittenSince(string folder, string stem, DateTime sinceUtc) =>
        Existing(folder, stem).Where(f => File.GetLastWriteTimeUtc(f) >= sinceUtc.AddSeconds(-2)).ToArray();
}

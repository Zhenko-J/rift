// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;

namespace Rift.Core.Journal;

/// <summary>Who and where to hide in a run journal (ADR-005): the Windows user name and the machine name.</summary>
public sealed record RedactionContext(string UserName, string MachineName)
{
    public static RedactionContext Current { get; } = new(Environment.UserName, Environment.MachineName);
}

/// <summary>
/// ADR-005: the journal is redacted at write time. The document folder becomes <c>.</c> (paths relative to the document),
/// the user name <c>&lt;USER&gt;</c> and the machine name <c>&lt;HOST&gt;</c>, case-insensitively, in plain text and in
/// JSON-escaped text (doubled backslashes). Everything else is left intact.
/// </summary>
public static class Redactor
{
    public const string UserMark = "<USER>";
    public const string HostMark = "<HOST>";

    public static string Redact(string text, string userName, string machineName, string? documentFolder)
    {
        ArgumentNullException.ThrowIfNull(text);
        string result = text;
        if (!string.IsNullOrWhiteSpace(documentFolder))
        {
            string folder = documentFolder.TrimEnd('\\', '/');
            if (folder.Length >= 3)
            {
                foreach (string variant in new[] { folder.Replace('/', '\\'), folder.Replace('\\', '/'), folder.Replace("/", "\\", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal) })
                {
                    result = result.Replace(variant, ".", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        result = ReplaceName(result, userName, UserMark);
        result = ReplaceName(result, machineName, HostMark);
        return result;
    }

    public static string Redact(string text, RedactionContext context, string? documentFolder)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Redact(text, context.UserName, context.MachineName, documentFolder);
    }

    private static string ReplaceName(string text, string? name, string mark)
    {
        // A one-letter name would redact half of the journal: such names are left alone.
        return string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2 ? text : text.Replace(name.Trim(), mark, StringComparison.OrdinalIgnoreCase);
    }
}

// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System.Globalization;
using System.Text;

namespace Rift.Core.Json;

/// <summary>
/// ADR-020: every outgoing string built from model data or script output is reduced to visible text and truncated to a
/// bounded length. Kept: letters, marks, numbers, punctuation, symbols, spaces, tab, line feed and carriage return.
/// Removed (F-R4): control characters (C0, DEL, C1), every format character (Unicode category <c>Cf</c>: bidirectional
/// controls, zero-width characters, the TAG block U+E0000–E007F…), private-use characters (<c>Co</c>), variation selectors,
/// noncharacters and lone surrogates. Line and paragraph separators (U+2028, U+2029) become a line feed.
/// </summary>
public static class Sanitizer
{
    /// <summary>Longest string kept in a field (characters); longer ones end with <see cref="TruncationMark"/>.</summary>
    public const int MaxFieldChars = 64 * 1024;

    public const string TruncationMark = "…[truncated]";

    public static string Sanitize(string? value, int maxChars = MaxFieldChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }
        StringBuilder? sb = null;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            int width = 1;
            int codePoint;
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                codePoint = char.ConvertToUtf32(c, value[i + 1]);
                width = 2;
            }
            else if (char.IsSurrogate(c))
            {
                codePoint = -1;   // lone surrogate
            }
            else
            {
                codePoint = c;
            }

            if (codePoint is 0x2028 or 0x2029)
            {
                sb ??= new StringBuilder(value, 0, i, value.Length);
                sb.Append('\n');
            }
            else if (codePoint < 0 || IsStrippedCodePoint(codePoint))
            {
                sb ??= new StringBuilder(value, 0, i, value.Length);
            }
            else
            {
                sb?.Append(value, i, width);
            }
            i += width - 1;
        }
        string clean = sb?.ToString() ?? value;
        if (clean.Length <= maxChars)
        {
            return clean;
        }
        int cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(clean[cut - 1]))
        {
            cut--;
        }
        return clean[..cut] + TruncationMark;
    }

    /// <summary>True when <paramref name="c"/>, taken alone, is removed (a lone surrogate always is).</summary>
    public static bool IsStripped(char c) => char.IsSurrogate(c) || IsStrippedCodePoint(c);

    /// <summary>True when the code point is removed from outgoing text (see the class summary).</summary>
    public static bool IsStrippedCodePoint(int codePoint)
    {
        if (codePoint is '\t' or '\n' or '\r')
        {
            return false;
        }
        if (codePoint < 0x20 || (codePoint >= 0x7F && codePoint <= 0x9F))
        {
            return true;   // C0, DEL, C1
        }
        if (codePoint is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF)
        {
            return true;   // variation selectors (they can carry hidden data)
        }
        if (codePoint is >= 0xFDD0 and <= 0xFDEF || (codePoint & 0xFFFE) == 0xFFFE)
        {
            return true;   // noncharacters
        }
        if (codePoint > 0x10FFFF)
        {
            return true;
        }
        UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(codePoint);
        return category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate;
    }
}

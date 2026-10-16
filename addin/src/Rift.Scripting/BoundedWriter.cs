// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Text;

namespace Rift.Scripting;

/// <summary>
/// A <see cref="TextWriter"/> that keeps at most <c>maxBytes</c> of UTF-8 text (ADR-016, ADR-026 §7: stdout and stderr of a
/// run are bounded to 1 MiB) and records that the rest was dropped. Never splits a surrogate pair. Optionally forwards what
/// it keeps to an inner writer. Not thread-safe: one run writes from one thread.
/// </summary>
public sealed class BoundedWriter : TextWriter
{
    /// <summary>Default bound of a run's stdout and of its stderr.</summary>
    public const int DefaultMaxBytes = 1024 * 1024;

    private readonly StringBuilder _text = new();
    private readonly int _maxBytes;
    private readonly TextWriter? _inner;
    private int _bytes;
    private char? _pendingHigh;

    public BoundedWriter(int maxBytes = DefaultMaxBytes, TextWriter? inner = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        _maxBytes = maxBytes;
        _inner = inner;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public bool Truncated { get; private set; }

    /// <summary>UTF-8 size of the text kept.</summary>
    public int ByteCount => _bytes;

    public override void Write(char value)
    {
        if (Truncated)
        {
            return;
        }
        if (char.IsHighSurrogate(value))
        {
            _pendingHigh = value;
            return;
        }
        if (_pendingHigh is char high)
        {
            _pendingHigh = null;
            if (char.IsLowSurrogate(value))
            {
                Keep(new string(new[] { high, value }), 4);
                return;
            }
            Keep(high.ToString(), 3);   // lone surrogate: encoded as U+FFFD (3 bytes)
            if (Truncated)
            {
                return;
            }
        }
        Keep(value.ToString(), value < 0x80 ? 1 : value < 0x800 ? 2 : 3);
    }

    public override void Write(string? value)
    {
        if (value is null || Truncated)
        {
            return;
        }
        if (_pendingHigh is null && !ContainsSurrogate(value))
        {
            int size = Encoding.UTF8.GetByteCount(value);
            if (_bytes + size <= _maxBytes)
            {
                Keep(value, size);
                return;
            }
        }
        foreach (char c in value)
        {
            Write(c);
            if (Truncated)
            {
                return;
            }
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(new string(buffer, index, count));
    }

    public override string ToString() => _text.ToString();

    private void Keep(string s, int size)
    {
        if (_bytes + size > _maxBytes)
        {
            Truncated = true;
            return;
        }
        _bytes += size;
        _text.Append(s);
        _inner?.Write(s);
    }

    private static bool ContainsSurrogate(string s)
    {
        foreach (char c in s)
        {
            if (char.IsSurrogate(c))
            {
                return true;
            }
        }
        return false;
    }
}

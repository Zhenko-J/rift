// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using Rift.Core.Protocol;

namespace Rift.Core.Transport;

/// <summary>
/// One ASCII header line (≤ 256 bytes, '\n'-terminated) in front of exactly <see cref="Length"/> bytes of UTF-8 JSON.
/// Request:  RIFT/1 ts=&lt;unix&gt; nonce=&lt;32 hex&gt; mac=&lt;64 hex&gt; len=&lt;bytes&gt;
/// Response: RIFT/1 proof=&lt;64 hex&gt; len=&lt;bytes&gt;
/// Health:   RIFT/1 health len=0
/// </summary>
public sealed record FrameHeader(FrameKind Kind, int Length, long Timestamp, string? Nonce, string? Mac, string? Proof)
{
    public static FrameHeader Request(long timestamp, string nonce, string mac, int length) =>
        new(FrameKind.Request, length, timestamp, nonce, mac, null);

    public static FrameHeader Response(string proof, int length) => new(FrameKind.Response, length, 0, null, null, proof);

    public static FrameHeader Health() => new(FrameKind.Health, 0, 0, null, null, null);

    public string Serialize()
    {
        var sb = new StringBuilder(ProtocolVersion.FrameMagic);
        switch (Kind)
        {
            case FrameKind.Request:
                sb.Append(" ts=").Append(Timestamp.ToString(CultureInfo.InvariantCulture));
                sb.Append(" nonce=").Append(Nonce);
                sb.Append(" mac=").Append(Mac);
                break;
            case FrameKind.Response:
                sb.Append(" proof=").Append(Proof);
                break;
            case FrameKind.Health:
                sb.Append(" health");
                break;
            default:
                throw new InvalidOperationException("unknown frame kind");
        }
        sb.Append(" len=").Append(Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Parses a header line (without the trailing '\n'). Returns null when malformed; the caller disconnects.</summary>
    public static FrameHeader? TryParse(string line)
    {
        if (string.IsNullOrEmpty(line) || line.Length > ProtocolVersion.MaxHeaderBytes)
        {
            return null;
        }
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], ProtocolVersion.FrameMagic, StringComparison.Ordinal))
        {
            return null;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        bool health = false;
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i] == "health")
            {
                health = true;
                continue;
            }
            int eq = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                return null;
            }
            fields[parts[i][..eq]] = parts[i][(eq + 1)..];
        }
        if (!fields.TryGetValue("len", out string? lenText) || !int.TryParse(lenText, NumberStyles.None, CultureInfo.InvariantCulture, out int len)
            || len < 0 || len > ProtocolVersion.MaxBodyBytes)
        {
            return null;
        }
        if (health)
        {
            return len == 0 && fields.Count == 1 ? Health() : null;
        }
        if (fields.TryGetValue("proof", out string? proof))
        {
            return fields.Count == 2 && IsHex(proof, 64) ? Response(proof, len) : null;
        }
        if (fields.TryGetValue("ts", out string? tsText) && fields.TryGetValue("nonce", out string? nonce) && fields.TryGetValue("mac", out string? mac)
            && fields.Count == 4 && long.TryParse(tsText, NumberStyles.None, CultureInfo.InvariantCulture, out long ts)
            && IsHex(nonce, 32) && IsHex(mac, 64))
        {
            return Request(ts, nonce, mac, len);
        }
        return null;
    }

    private static bool IsHex(string s, int length)
    {
        if (s.Length != length)
        {
            return false;
        }
        foreach (char c in s)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }
        return true;
    }
}

public enum FrameKind
{
    Request,
    Response,
    Health,
}

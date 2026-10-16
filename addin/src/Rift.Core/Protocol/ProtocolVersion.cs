// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
namespace Rift.Core.Protocol;

/// <summary>Wire protocol version (docs/PROTOCOL.md). Major mismatch → PROTOCOL_MISMATCH.</summary>
public static class ProtocolVersion
{
    public const string Current = "2.0";
    public const string FrameMagic = "RIFT/1";
    public const int MaxHeaderBytes = 256;
    public const int MaxBodyBytes = 1024 * 1024;
    public const int MaxTimestampSkewSeconds = 120;
    public static readonly System.TimeSpan NonceLifetime = System.TimeSpan.FromMinutes(5);
}

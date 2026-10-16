// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Security.Cryptography;
using System.Text;

using Rift.Core.Protocol;

namespace Rift.Core.Security;

/// <summary>
/// Request/response signing (ADR-002, docs/PROTOCOL.md). The secret never travels:
/// mac   = HMAC-SHA256(secret, "RIFT/1|" + ts + "|" + nonce + "|" + sha256_hex(body))
/// proof = HMAC-SHA256(secret, mac + "|" + sha256_hex(response_body))
/// Shared test vectors: shared/schemas/test-vectors/hmac.json (checked by xUnit and pytest).
/// </summary>
public static class HmacSigner
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) => ToHex(SHA256.HashData(data));

    public static string ComputeMac(ReadOnlySpan<byte> secret, long unixSeconds, string nonceHex, string bodySha256Hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(nonceHex);
        ArgumentException.ThrowIfNullOrEmpty(bodySha256Hex);
        string message = ProtocolVersion.FrameMagic + "|" + unixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
                         + "|" + nonceHex + "|" + bodySha256Hex;
        return ToHex(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(message)));
    }

    public static string ComputeProof(ReadOnlySpan<byte> secret, string requestMacHex, string responseBodySha256Hex)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestMacHex);
        ArgumentException.ThrowIfNullOrEmpty(responseBodySha256Hex);
        string message = requestMacHex + "|" + responseBodySha256Hex;
        return ToHex(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(message)));
    }

    /// <summary>Constant-time comparison of two lowercase hex digests.</summary>
    public static bool HexEquals(string expectedHex, string actualHex)
    {
        if (expectedHex is null || actualHex is null || expectedHex.Length != actualHex.Length)
        {
            return false;
        }
        byte[] a = Encoding.ASCII.GetBytes(expectedHex);
        byte[] b = Encoding.ASCII.GetBytes(actualHex);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static byte[] NewSecret()
    {
        byte[] secret = new byte[32];
        RandomNumberGenerator.Fill(secret);
        return secret;
    }

    public static string NewNonceHex() => ToHex(RandomNumberGenerator.GetBytes(16));

    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}

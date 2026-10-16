// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Threading;

using Rift.Core.Protocol;
using Rift.Core.Transport;

namespace Rift.Core.Security;

public enum AuthResult
{
    Ok,
    BadTimestamp,
    ReplayedNonce,
    BadMac,
}

/// <summary>
/// Verifies a request frame (ADR-002): timestamp window, nonce uniqueness, constant-time mac comparison.
/// Ten consecutive failures rotate the secret.
/// </summary>
public sealed class RequestAuthenticator
{
    private readonly SecretStore _secrets;
    private readonly NonceCache _nonces;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _maxConsecutiveFailures;
    private int _failures;

    public RequestAuthenticator(SecretStore secrets, NonceCache nonces, Func<DateTimeOffset>? clock = null, int maxConsecutiveFailures = 10)
    {
        _secrets = secrets;
        _nonces = nonces;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _maxConsecutiveFailures = maxConsecutiveFailures;
    }

    public int ConsecutiveFailures => Volatile.Read(ref _failures);

    public AuthResult Verify(FrameHeader header, ReadOnlySpan<byte> body)
    {
        if (header.Kind != FrameKind.Request || header.Nonce is null || header.Mac is null)
        {
            return Fail(AuthResult.BadMac);
        }
        long now = _clock().ToUnixTimeSeconds();
        if (Math.Abs(now - header.Timestamp) > ProtocolVersion.MaxTimestampSkewSeconds)
        {
            return Fail(AuthResult.BadTimestamp);
        }
        byte[] secret = _secrets.Current;
        try
        {
            string expected = HmacSigner.ComputeMac(secret, header.Timestamp, header.Nonce, HmacSigner.Sha256Hex(body));
            if (!HmacSigner.HexEquals(expected, header.Mac))
            {
                return Fail(AuthResult.BadMac);
            }
        }
        finally
        {
            Array.Clear(secret);
        }
        // The nonce is consumed only once the mac is valid: an attacker cannot burn nonces it cannot sign.
        if (!_nonces.TryAdd(header.Nonce))
        {
            return Fail(AuthResult.ReplayedNonce);
        }
        Interlocked.Exchange(ref _failures, 0);
        return AuthResult.Ok;
    }

    public string Proof(string requestMac, ReadOnlySpan<byte> responseBody)
    {
        byte[] secret = _secrets.Current;
        try
        {
            return HmacSigner.ComputeProof(secret, requestMac, HmacSigner.Sha256Hex(responseBody));
        }
        finally
        {
            Array.Clear(secret);
        }
    }

    private AuthResult Fail(AuthResult result)
    {
        if (Interlocked.Increment(ref _failures) >= _maxConsecutiveFailures)
        {
            Interlocked.Exchange(ref _failures, 0);
            _secrets.Rotate();
        }
        return result;
    }
}

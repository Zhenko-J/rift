// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Xunit;

using Rift.Core.Logging;
using Rift.Core.Security;
using Rift.Core.Transport;

namespace Rift.Tests;

/// <summary>Plan M1, tests 3 and 4: window, replay, tampered body, rotation after ten consecutive failures (injected clock).</summary>
public sealed class RequestAuthenticatorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rift-tests", Guid.NewGuid().ToString("N"));
    private readonly SecretStore _secrets;
    private DateTimeOffset _now = T0;

    public RequestAuthenticatorTests()
    {
        Directory.CreateDirectory(_dir);
        _secrets = new SecretStore(Path.Combine(_dir, "revit-1.key"), FileLog.Null);
    }

    private RequestAuthenticator NewAuthenticator(int maxFailures = 10) =>
        new(_secrets, new NonceCache(TimeSpan.FromMinutes(5), () => _now), () => _now, maxFailures);

    private static (FrameHeader Header, byte[] Body) Sign(byte[] secret, long ts, string? nonce = null, string text = "{\"op\":\"context\"}")
    {
        byte[] body = Encoding.UTF8.GetBytes(text);
        nonce ??= HmacSigner.NewNonceHex();
        string mac = HmacSigner.ComputeMac(secret, ts, nonce, HmacSigner.Sha256Hex(body));
        return (FrameHeader.Request(ts, nonce, mac, body.Length), body);
    }

    [Fact]
    public void Valid_frame_is_accepted_and_its_nonce_cannot_be_replayed()
    {
        RequestAuthenticator auth = NewAuthenticator();
        (FrameHeader h, byte[] body) = Sign(_secrets.Current, T0.ToUnixTimeSeconds());
        Assert.Equal(AuthResult.Ok, auth.Verify(h, body));
        _now = T0.AddSeconds(30);
        Assert.Equal(AuthResult.ReplayedNonce, auth.Verify(h, body));
    }

    [Theory]
    [InlineData(-121)]
    [InlineData(121)]
    [InlineData(-3600)]
    public void Timestamp_outside_the_window_is_refused(int skewSeconds)
    {
        RequestAuthenticator auth = NewAuthenticator();
        (FrameHeader h, byte[] body) = Sign(_secrets.Current, T0.ToUnixTimeSeconds() + skewSeconds);
        Assert.Equal(AuthResult.BadTimestamp, auth.Verify(h, body));
    }

    [Theory]
    [InlineData(-120)]
    [InlineData(120)]
    public void Timestamp_at_the_edge_of_the_window_is_accepted(int skewSeconds)
    {
        RequestAuthenticator auth = NewAuthenticator();
        (FrameHeader h, byte[] body) = Sign(_secrets.Current, T0.ToUnixTimeSeconds() + skewSeconds);
        Assert.Equal(AuthResult.Ok, auth.Verify(h, body));
    }

    [Fact]
    public void Body_changed_by_one_byte_or_wrong_secret_is_a_bad_mac()
    {
        RequestAuthenticator auth = NewAuthenticator();
        (FrameHeader h, byte[] body) = Sign(_secrets.Current, T0.ToUnixTimeSeconds());
        body[^2] ^= 0x01;
        Assert.Equal(AuthResult.BadMac, auth.Verify(h, body));

        (FrameHeader h2, byte[] body2) = Sign(new byte[32], T0.ToUnixTimeSeconds());
        Assert.Equal(AuthResult.BadMac, auth.Verify(h2, body2));
    }

    [Fact]
    public void A_refused_mac_does_not_burn_the_nonce()
    {
        RequestAuthenticator auth = NewAuthenticator();
        string nonce = HmacSigner.NewNonceHex();
        (FrameHeader forged, byte[] body) = Sign(new byte[32], T0.ToUnixTimeSeconds(), nonce);
        Assert.Equal(AuthResult.BadMac, auth.Verify(forged, body));
        (FrameHeader genuine, byte[] body2) = Sign(_secrets.Current, T0.ToUnixTimeSeconds(), nonce);
        Assert.Equal(AuthResult.Ok, auth.Verify(genuine, body2));
    }

    [Fact]
    public void Nonce_is_accepted_again_once_its_lifetime_is_over()
    {
        var cache = new NonceCache(TimeSpan.FromMinutes(5), () => _now);
        Assert.True(cache.TryAdd("aa"));
        Assert.False(cache.TryAdd("aa"));
        _now = T0.AddMinutes(4);
        Assert.False(cache.TryAdd("aa"));
        _now = T0.AddMinutes(5).AddSeconds(1);
        Assert.True(cache.TryAdd("aa"));

        // Lazy purge: entries older than the lifetime are dropped on a later insertion.
        Assert.True(cache.TryAdd("bb"));
        _now = _now.AddMinutes(11);
        Assert.True(cache.TryAdd("cc"));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Ten_failures_rotate_the_secret_and_increment_generation()
    {
        RequestAuthenticator auth = NewAuthenticator();
        byte[] before = _secrets.Current;
        byte[] fileBefore = File.ReadAllBytes(_secrets.KeyPath);
        Assert.Equal(before, fileBefore);
        var rotations = new List<int>();
        _secrets.Rotated += g => rotations.Add(g);

        for (int i = 0; i < 9; i++)
        {
            (FrameHeader h, byte[] body) = Sign(new byte[32], T0.ToUnixTimeSeconds());
            Assert.Equal(AuthResult.BadMac, auth.Verify(h, body));
        }
        Assert.Equal(1, _secrets.Generation);
        Assert.Equal(9, auth.ConsecutiveFailures);
        Assert.Empty(rotations);

        // The tenth failure may be of any kind: a stale timestamp counts as well.
        (FrameHeader stale, byte[] staleBody) = Sign(_secrets.Current, T0.ToUnixTimeSeconds() - 600);
        Assert.Equal(AuthResult.BadTimestamp, auth.Verify(stale, staleBody));

        Assert.Equal(2, _secrets.Generation);
        Assert.Equal([2], rotations);
        Assert.Equal(0, auth.ConsecutiveFailures);
        byte[] after = _secrets.Current;
        Assert.NotEqual(before, after);
        Assert.Equal(after, File.ReadAllBytes(_secrets.KeyPath));

        // A client still signing with the old secret is refused; one that re-read the key file is served.
        (FrameHeader old, byte[] oldBody) = Sign(before, T0.ToUnixTimeSeconds());
        Assert.Equal(AuthResult.BadMac, auth.Verify(old, oldBody));
        (FrameHeader fresh, byte[] freshBody) = Sign(File.ReadAllBytes(_secrets.KeyPath), T0.ToUnixTimeSeconds());
        Assert.Equal(AuthResult.Ok, auth.Verify(fresh, freshBody));
    }

    [Fact]
    public void A_success_resets_the_failure_counter()
    {
        RequestAuthenticator auth = NewAuthenticator();
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 9; i++)
            {
                (FrameHeader h, byte[] body) = Sign(new byte[32], T0.ToUnixTimeSeconds());
                auth.Verify(h, body);
            }
            (FrameHeader ok, byte[] okBody) = Sign(_secrets.Current, T0.ToUnixTimeSeconds());
            Assert.Equal(AuthResult.Ok, auth.Verify(ok, okBody));
            Assert.Equal(0, auth.ConsecutiveFailures);
        }
        Assert.Equal(1, _secrets.Generation);
    }

    [Fact]
    public void Proof_uses_the_current_secret()
    {
        RequestAuthenticator auth = NewAuthenticator();
        byte[] response = Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
        string mac = new('a', 64);
        Assert.Equal(HmacSigner.ComputeProof(_secrets.Current, mac, HmacSigner.Sha256Hex(response)), auth.Proof(mac, response));
    }

    [Fact]
    public void Delete_removes_the_key_file()
    {
        Assert.True(File.Exists(_secrets.KeyPath));
        _secrets.Delete();
        Assert.False(File.Exists(_secrets.KeyPath));
        _secrets.Delete();   // idempotent
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

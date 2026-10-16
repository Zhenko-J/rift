// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Text;
using System.Text.Json;

using Xunit;

using Rift.Core.Security;

namespace Rift.Tests;

public sealed class HmacSignerTests
{
    private static JsonElement LoadVectors()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "test-vectors", "hmac.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Shared_vectors_match_python_reference()
    {
        JsonElement root = LoadVectors();
        foreach (JsonElement v in root.GetProperty("vectors").EnumerateArray())
        {
            byte[] secret = Convert.FromHexString(v.GetProperty("secret_hex").GetString()!);
            byte[] body = Encoding.UTF8.GetBytes(v.GetProperty("body").GetString()!);
            string bodyHash = HmacSigner.Sha256Hex(body);
            Assert.Equal(v.GetProperty("body_sha256").GetString(), bodyHash);

            string mac = HmacSigner.ComputeMac(secret, v.GetProperty("ts").GetInt64(), v.GetProperty("nonce").GetString()!, bodyHash);
            Assert.Equal(v.GetProperty("mac").GetString(), mac);

            byte[] response = Encoding.UTF8.GetBytes(v.GetProperty("response_body").GetString()!);
            string proof = HmacSigner.ComputeProof(secret, mac, HmacSigner.Sha256Hex(response));
            Assert.Equal(v.GetProperty("proof").GetString(), proof);
        }
    }

    [Fact]
    public void HexEquals_is_strict_on_length_and_content()
    {
        Assert.True(HmacSigner.HexEquals("abcd", "abcd"));
        Assert.False(HmacSigner.HexEquals("abcd", "abce"));
        Assert.False(HmacSigner.HexEquals("abcd", "abc"));
        Assert.False(HmacSigner.HexEquals("abcd", null!));
    }

    [Fact]
    public void Secrets_and_nonces_have_the_documented_sizes()
    {
        Assert.Equal(32, HmacSigner.NewSecret().Length);
        Assert.Equal(32, HmacSigner.NewNonceHex().Length);
        Assert.NotEqual(HmacSigner.NewNonceHex(), HmacSigner.NewNonceHex());
    }
}

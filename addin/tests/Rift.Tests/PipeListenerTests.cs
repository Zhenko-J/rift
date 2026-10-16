// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using Xunit;

using Rift.Core.Json;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Core.Security;
using Rift.Core.Transport;

namespace Rift.Tests;

/// <summary>The real listener against an in-process client (plan M1, tests 1 and 2).</summary>
public sealed class PipeListenerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rift-tests", Guid.NewGuid().ToString("N"));
    private readonly SecretStore _secrets;
    private readonly RequestAuthenticator _auth;
    private readonly string _name = "rift-test-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public PipeListenerTests()
    {
        Directory.CreateDirectory(_dir);
        _secrets = new SecretStore(Path.Combine(_dir, "t.key"), FileLog.Null);
        _auth = new RequestAuthenticator(_secrets, new NonceCache(TimeSpan.FromMinutes(5)));
    }

    private sealed class EchoHandler : IRequestHandler
    {
        public ResponseEnvelope Handle(ReadOnlySpan<byte> body, CancellationToken cancellation)
        {
            JsonNode? node = JsonCodec.TryParseNode(body);
            return new ResponseEnvelope(ProtocolVersion.Current, node?["id"]?.GetValue<string>(), ResponseEnvelope.Ok, null,
                new JsonObject { ["echo_op"] = node?["op"]?.GetValue<string>(), ["bytes"] = body.Length }, null, 1);
        }
    }

    [Fact]
    public void Single_instance_name_is_held_and_reused_across_connections()
    {
        using var listener = new PipeListener(_name, _auth, new EchoHandler(), FileLog.Null);
        listener.Start();
        Assert.Equal(_name, listener.PipeName);

        // A second listener on the same name gets a suffixed name (squat detection, P-D2).
        using var second = new PipeListener(_name, _auth, new EchoHandler(), FileLog.Null);
        Assert.NotEqual(_name, second.PipeName);
        Assert.StartsWith(_name + "-", second.PipeName, StringComparison.Ordinal);

        for (int i = 0; i < 3; i++)
        {
            (string header, byte[] body) = Call(listener.PipeName, SignedRequest("r" + i, "context"), _secrets.Current);
            Assert.Contains("proof=", header, StringComparison.Ordinal);
            JsonNode resp = JsonNode.Parse(body)!;
            Assert.Equal("ok", resp["status"]!.GetValue<string>());
            Assert.Equal("context", resp["result"]!["echo_op"]!.GetValue<string>());
        }
        Assert.Equal(3, listener.Served);
    }

    [Fact]
    public void Unsigned_request_is_refused_and_health_is_answered_without_proof()
    {
        using var listener = new PipeListener(_name, _auth, new EchoHandler(), FileLog.Null);
        listener.Start();

        (string hHeader, byte[] hBody) = Raw(listener.PipeName, Encoding.ASCII.GetBytes("RIFT/1 health len=0\n"));
        Assert.StartsWith("RIFT/1 proof=- len=", hHeader, StringComparison.Ordinal);
        JsonNode health = JsonNode.Parse(hBody)!;
        Assert.Equal(ProtocolVersion.Current, health["v"]!.GetValue<string>());
        Assert.Equal(Environment.ProcessId, health["pid"]!.GetValue<int>());

        byte[] body = Encoding.UTF8.GetBytes("{\"v\":\"2.0\",\"id\":\"x\",\"op\":\"context\"}");
        string badMac = new('0', 64);
        string line = "RIFT/1 ts=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + " nonce=" + new string('1', 32) + " mac=" + badMac + " len=" + body.Length.ToString(CultureInfo.InvariantCulture) + "\n";
        (string header, byte[] resp) = Raw(listener.PipeName, Concat(Encoding.ASCII.GetBytes(line), body));
        Assert.Contains("proof=", header, StringComparison.Ordinal);
        JsonNode r = JsonNode.Parse(resp)!;
        Assert.Equal("error", r["status"]!.GetValue<string>());
        Assert.Equal(ErrorCodes.Unauthorized, r["error"]!["code"]!.GetValue<string>());

        // Malformed header: the connection is closed without a response.
        using (var client = new NamedPipeClientStream(".", listener.PipeName, PipeDirection.InOut))
        {
            client.Connect(2000);
            byte[] junk = Encoding.ASCII.GetBytes("HELLO\n");
            client.Write(junk, 0, junk.Length);
            client.Flush();
            Assert.Equal(-1, client.ReadByte());
        }
    }

    private sealed class CountingHandler : IRequestHandler
    {
        public int Calls;

        public ResponseEnvelope Handle(ReadOnlySpan<byte> body, CancellationToken cancellation)
        {
            Interlocked.Increment(ref Calls);
            return new ResponseEnvelope(ProtocolVersion.Current, "x", ResponseEnvelope.Ok, null, new JsonObject(), null, 1);
        }
    }

    [Fact]
    public void Refused_frames_raise_unauthorized_and_never_reach_the_handler()
    {
        var handler = new CountingHandler();
        using var listener = new PipeListener(_name, _auth, handler, FileLog.Null);
        var reasons = new System.Collections.Concurrent.ConcurrentQueue<AuthResult>();
        listener.Unauthorized += reasons.Enqueue;
        listener.Start();

        byte[] frame = SignedRequest("ok", "context");
        (_, byte[] first) = Call(listener.PipeName, frame, _secrets.Current);
        Assert.Equal("ok", JsonNode.Parse(first)!["status"]!.GetValue<string>());

        // The very same frame again: replayed nonce.
        (_, byte[] replay) = Call(listener.PipeName, frame, _secrets.Current);
        JsonNode r = JsonNode.Parse(replay)!;
        Assert.Equal(ErrorCodes.Unauthorized, r["error"]!["code"]!.GetValue<string>());
        Assert.Equal(nameof(AuthResult.ReplayedNonce), r["error"]!["message"]!.GetValue<string>());

        // Signed correctly but three minutes old: out of the window.
        (byte[] body, _, _, string nonce) = SignedRequestParts("old", "context", _secrets.Current);
        long ts = DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeSeconds();
        string mac = HmacSigner.ComputeMac(_secrets.Current, ts, nonce, HmacSigner.Sha256Hex(body));
        (_, byte[] old) = Call(listener.PipeName, Concat(Encoding.ASCII.GetBytes(FrameHeader.Request(ts, nonce, mac, body.Length).Serialize()), body), _secrets.Current);
        Assert.Equal(nameof(AuthResult.BadTimestamp), JsonNode.Parse(old)!["error"]!["message"]!.GetValue<string>());

        Assert.Equal(1, handler.Calls);
        Assert.Equal(new[] { AuthResult.ReplayedNonce, AuthResult.BadTimestamp }, reasons.ToArray());
    }

    private static (byte[] Body, string Mac, long Ts, string Nonce) SignedRequestParts(string id, string op, byte[] secret)
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"v\":\"2.0\",\"id\":\"" + id + "\",\"op\":\"" + op + "\",\"args\":{\"text\":\"élévation\"}}");
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = HmacSigner.NewNonceHex();
        string mac = HmacSigner.ComputeMac(secret, ts, nonce, HmacSigner.Sha256Hex(body));
        return (body, mac, ts, nonce);
    }

    private byte[] SignedRequest(string id, string op)
    {
        (byte[] body, string mac, long ts, string nonce) = SignedRequestParts(id, op, _secrets.Current);
        string line = FrameHeader.Request(ts, nonce, mac, body.Length).Serialize();
        return Concat(Encoding.ASCII.GetBytes(line), body);
    }

    private static (string Header, byte[] Body) Call(string name, byte[] frame, byte[] secret)
    {
        (string header, byte[] body) = Raw(name, frame);
        string mac = Encoding.ASCII.GetString(frame, 0, Array.IndexOf(frame, (byte)'\n'));
        string reqMac = mac[(mac.IndexOf("mac=", StringComparison.Ordinal) + 4)..][..64];
        string proof = header[(header.IndexOf("proof=", StringComparison.Ordinal) + 6)..][..64];
        Assert.Equal(HmacSigner.ComputeProof(secret, reqMac, HmacSigner.Sha256Hex(body)), proof);
        return (header, body);
    }

    private static (string Header, byte[] Body) Raw(string name, byte[] frame)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        client.Connect(2000);
        client.Write(frame, 0, frame.Length);
        client.Flush();
        var line = new StringBuilder();
        int b;
        while ((b = client.ReadByte()) >= 0 && b != '\n')
        {
            line.Append((char)b);
        }
        string header = line.ToString();
        int len = int.Parse(header[(header.LastIndexOf("len=", StringComparison.Ordinal) + 4)..], CultureInfo.InvariantCulture);
        var body = new byte[len];
        int off = 0;
        while (off < len)
        {
            int r = client.Read(body, off, len - off);
            Assert.True(r > 0, "short read");
            off += r;
        }
        return (header, body);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
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
    }
}

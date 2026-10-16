// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using Rift.Core.Json;
using Rift.Core.Logging;
using Rift.Core.Protocol;
using Rift.Core.Security;

namespace Rift.Core.Transport;

/// <summary>Handles one authenticated request body and returns the response envelope. Never throws.</summary>
public interface IRequestHandler
{
    ResponseEnvelope Handle(ReadOnlySpan<byte> body, CancellationToken cancellation);
}

/// <summary>
/// The add-in's endpoint (ADR-001): one named-pipe instance, one connection per request, byte mode, kernel ACL.
/// The listener thread reads a frame, authenticates it, hands the body to the handler, writes the signed
/// response and disconnects. Malformed or oversized frames close the connection without a response.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PipeListener : IDisposable
{
    private readonly RequestAuthenticator _auth;
    private readonly IRequestHandler _handler;
    private readonly FileLog _log;
    private readonly CancellationTokenSource _stopping = new();
    private NamedPipeServerStream? _pipe;
    private Thread? _thread;

    public PipeListener(string preferredName, RequestAuthenticator auth, IRequestHandler handler, FileLog log)
    {
        _auth = auth;
        _handler = handler;
        _log = log;
        PipeName = Create(preferredName);
    }

    /// <summary>The effective name: the preferred one, or a random-suffixed one when the preferred name is taken (squat detection).</summary>
    public string PipeName { get; }

    public int Served { get; private set; }

    /// <summary>
    /// Raised on the listener thread for every request refused by the authenticator, with the reason. The handler was
    /// not called. The host shows <c>UNAUTHORIZED</c> on the activity card (ADR-023); subscribers must not block.
    /// </summary>
    public event Action<AuthResult>? Unauthorized;

    public static string DefaultName(int pid) => Brand.Id + "-revit-" + pid.ToString(CultureInfo.InvariantCulture);

    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("already started");
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = Brand.Id + "-pipe" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _pipe?.Dispose();   // unblocks WaitForConnection in the loop thread
        }
        catch (IOException)
        {
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
        _stopping.Dispose();
    }

    private string Create(string preferredName)
    {
        try
        {
            _pipe = Open(preferredName);
            return preferredName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string suffixed = preferredName + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            _log.Write("pipe name '" + preferredName + "' is taken (" + ex.GetType().Name + "); using '" + suffixed + "'");
            _pipe = Open(suffixed);
            return suffixed;
        }
    }

    private static NamedPipeServerStream Open(string name) =>
        NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, 64 * 1024, 64 * 1024, PipeAcl.Build(), HandleInheritability.None);

    private void Loop()
    {
        NamedPipeServerStream pipe = _pipe!;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                pipe.WaitForConnection();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }
            try
            {
                Served++;
                ServeOne(pipe);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.Write("pipe: connection dropped: " + ex.Message);
            }
            catch (Exception ex)
            {
                _log.Write("pipe: unexpected: " + ex);
            }
            finally
            {
                try
                {
                    if (pipe.IsConnected)
                    {
                        // DisconnectNamedPipe discards unread bytes: wait until the client has read the response.
                        pipe.WaitForPipeDrain();
                        pipe.Disconnect();
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
            }
        }
    }

    private void ServeOne(NamedPipeServerStream pipe)
    {
        string? line = ReadHeaderLine(pipe);
        FrameHeader? header = line is null ? null : FrameHeader.TryParse(line);
        if (header is null)
        {
            _log.Write("pipe: malformed header → disconnect");
            return;
        }
        if (header.Kind == FrameKind.Response)
        {
            return;
        }
        byte[] body = ReadExactly(pipe, header.Length);
        if (header.Kind == FrameKind.Health)
        {
            byte[] health = Encoding.UTF8.GetBytes("{\"v\":\"" + ProtocolVersion.Current + "\",\"pid\":" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "}");
            WriteFrame(pipe, ProtocolVersion.FrameMagic + " proof=- len=" + health.Length.ToString(CultureInfo.InvariantCulture) + "\n", health);
            return;
        }
        AuthResult auth = _auth.Verify(header, body);
        byte[] response;
        if (auth != AuthResult.Ok)
        {
            _log.Write("pipe: unauthorized (" + auth + ")");
            response = JsonCodec.Serialize(ResponseEnvelope.Fail(null, ErrorCodes.Unauthorized, auth.ToString()));
            try
            {
                Unauthorized?.Invoke(auth);
            }
            catch (Exception ex)
            {
                _log.Write("pipe: unauthorized subscriber failed: " + ex.Message);
            }
        }
        else
        {
            ResponseEnvelope envelope = _handler.Handle(body, _stopping.Token);
            response = JsonCodec.Serialize(envelope);
            if (response.Length > ProtocolVersion.MaxBodyBytes)
            {
                // The client refuses a body above the frame limit: answer with a typed error rather than a frame it drops.
                _log.Write("pipe: response of " + response.Length.ToString(CultureInfo.InvariantCulture) + " bytes exceeds the frame limit");
                response = JsonCodec.Serialize(ResponseEnvelope.Fail(envelope.Id, ErrorCodes.PayloadTooLarge,
                    "response of " + response.Length.ToString(CultureInfo.InvariantCulture) + " bytes exceeds the 1 MiB frame limit", envelope.Target, envelope.ElapsedMs));
            }
        }
        string proof = _auth.Proof(header.Mac!, response);
        WriteFrame(pipe, FrameHeader.Response(proof, response.Length).Serialize(), response);
    }

    private static void WriteFrame(Stream s, string headerLine, byte[] body)
    {
        byte[] h = Encoding.ASCII.GetBytes(headerLine);
        s.Write(h, 0, h.Length);
        s.Write(body, 0, body.Length);
        s.Flush();
    }

    private static string? ReadHeaderLine(Stream s)
    {
        var buf = new byte[ProtocolVersion.MaxHeaderBytes];
        int n = 0;
        while (n < buf.Length)
        {
            int b = s.ReadByte();
            if (b < 0)
            {
                return null;
            }
            if (b == '\n')
            {
                return Encoding.ASCII.GetString(buf, 0, n);
            }
            buf[n++] = (byte)b;
        }
        return null;   // longer than 256 bytes: malformed
    }

    private static byte[] ReadExactly(Stream s, int length)
    {
        var buf = new byte[length];
        int off = 0;
        while (off < length)
        {
            int r = s.Read(buf, off, length - off);
            if (r <= 0)
            {
                throw new IOException("short read");
            }
            off += r;
        }
        return buf;
    }
}

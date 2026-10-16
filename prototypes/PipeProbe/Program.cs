// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
// J0 prototypes: P-D1 (pipe ACL), P-D2 (single instance / squat), P-D8 (HttpListener without reservation).
using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Rift.Core.Protocol;
using Rift.Core.Security;
using Rift.Core.Transport;

namespace PipeProbe;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: PipeProbe serve <pipeName> <secretHex> | httplistener");
            return 2;
        }
        return args[0] switch
        {
            "serve" => Serve(args[1], Convert.FromHexString(args[2])),
            "httplistener" => HttpListenerProbe(),
            _ => 2,
        };
    }

    // ---------------------------------------------------------------- P-D1 / P-D2 / server side of P-D3
    private static int Serve(string name, byte[] secret)
    {
        var sec = new PipeSecurity();
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        sec.SetOwner(me);
        sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));

        using NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
            name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough,
            64 * 1024, 64 * 1024, sec, HandleInheritability.None);
        Console.WriteLine("P-D1 pipe created: \\\\.\\pipe\\" + name + " pid=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Console.WriteLine("P-D1 sddl: " + pipe.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner));

        // P-D2: a second creator of the same name must be refused.
        try
        {
            using NamedPipeServerStream second = NamedPipeServerStreamAcl.Create(
                name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec, HandleInheritability.None);
            Console.WriteLine("P-D2 FAIL: second instance of the same name was accepted");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.WriteLine("P-D2 ok: second instance refused (" + ex.GetType().Name + ": " + ex.Message + ")");
        }

        int served = 0;
        while (true)
        {
            pipe.WaitForConnection();
            served++;
            try
            {
                string? headerLine = ReadHeaderLine(pipe);
                FrameHeader? header = headerLine is null ? null : FrameHeader.TryParse(headerLine);
                if (header is null)
                {
                    Console.WriteLine("P-D3 server: malformed header → disconnect");
                    continue;
                }
                byte[] body = ReadExactly(pipe, header.Length);
                string bodyHash = HmacSigner.Sha256Hex(body);
                byte[] response;
                string proof;
                if (header.Kind == FrameKind.Health)
                {
                    response = Encoding.UTF8.GetBytes("{\"v\":\"" + ProtocolVersion.Current + "\",\"pid\":" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "}");
                    proof = "-";
                    Console.WriteLine("P-D3 server: health");
                }
                else
                {
                    string expected = HmacSigner.ComputeMac(secret, header.Timestamp, header.Nonce!, bodyHash);
                    bool ok = HmacSigner.HexEquals(expected, header.Mac!);
                    JsonNode? json = ok ? JsonNode.Parse(body) : null;
                    string op = json?["op"]?.GetValue<string>() ?? "?";
                    var reply = new JsonObject
                    {
                        ["v"] = ProtocolVersion.Current,
                        ["id"] = json?["id"]?.DeepClone(),
                        ["status"] = ok ? "ok" : "error",
                        ["result"] = ok ? new JsonObject { ["echo_op"] = op, ["body_bytes"] = body.Length, ["utf8_ok"] = json?["args"]?["text"]?.GetValue<string>() == "élévation 😀" } : null,
                        ["error"] = ok ? null : new JsonObject { ["code"] = ErrorCodes.Unauthorized },
                    };
                    response = JsonSerializer.SerializeToUtf8Bytes(reply, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                    proof = HmacSigner.ComputeProof(secret, header.Mac!, HmacSigner.Sha256Hex(response));
                    Console.WriteLine("P-D3 server: op=" + op + " bytes=" + body.Length.ToString(CultureInfo.InvariantCulture) + " mac=" + (ok ? "valid" : "INVALID"));
                    if (op == "quit")
                    {
                        Write(pipe, FrameHeader.Response(proof, response.Length), response);
                        pipe.Disconnect();
                        break;
                    }
                }
                Write(pipe, new FrameHeader(FrameKind.Response, response.Length, 0, null, null, proof), response);
            }
            finally
            {
                if (pipe.IsConnected)
                {
                    pipe.Disconnect();
                }
            }
        }
        Console.WriteLine("P-D2 ok: instance reused for " + served.ToString(CultureInfo.InvariantCulture) + " connections");
        return 0;
    }

    private static void Write(PipeStream pipe, FrameHeader header, byte[] body)
    {
        string head = header.Kind == FrameKind.Response && header.Proof == "-" ? ProtocolVersion.FrameMagic + " proof=- len=" + body.Length.ToString(CultureInfo.InvariantCulture) + "\n" : header.Serialize();
        byte[] h = Encoding.ASCII.GetBytes(head);
        pipe.Write(h, 0, h.Length);
        pipe.Write(body, 0, body.Length);
        pipe.Flush();
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
        return null;
    }

    private static byte[] ReadExactly(Stream s, int len)
    {
        var buf = new byte[len];
        int off = 0;
        while (off < len)
        {
            int r = s.Read(buf, off, len - off);
            if (r <= 0)
            {
                throw new IOException("short read");
            }
            off += r;
        }
        return buf;
    }

    // ---------------------------------------------------------------- P-D8
    private static int HttpListenerProbe()
    {
        foreach (string host in new[] { "127.0.0.1", "localhost" })
        {
            string prefix = "http://" + host + ":48891/";
            try
            {
                using var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                listener.Start();
                Console.WriteLine("P-D8 " + prefix + " → started WITHOUT a reservation (non-admin)");
                listener.Stop();
            }
            catch (HttpListenerException ex)
            {
                Console.WriteLine("P-D8 " + prefix + " → HttpListenerException " + ex.ErrorCode.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
            }
        }
        return 0;
    }
}

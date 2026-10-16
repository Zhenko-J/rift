// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using Xunit;

using Rift.Core.Transport;

namespace Rift.Tests;

public sealed class FrameHeaderTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private const string Mac = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Request_round_trips()
    {
        var header = FrameHeader.Request(1_700_000_000, Nonce, Mac, 42);
        string line = header.Serialize();
        Assert.EndsWith("\n", line, System.StringComparison.Ordinal);
        FrameHeader? parsed = FrameHeader.TryParse(line.TrimEnd('\n'));
        Assert.Equal(header, parsed);
    }

    [Fact]
    public void Response_and_health_round_trip()
    {
        Assert.Equal(FrameHeader.Response(Mac, 7), FrameHeader.TryParse(FrameHeader.Response(Mac, 7).Serialize().TrimEnd('\n')));
        Assert.Equal(FrameHeader.Health(), FrameHeader.TryParse("RIFT/1 health len=0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("RIFT/2 health len=0")]
    [InlineData("RIFT/1 health len=1")]
    [InlineData("RIFT/1 len=5")]
    [InlineData("RIFT/1 ts=1 nonce=zz mac=" + Mac + " len=5")]
    [InlineData("RIFT/1 ts=1 nonce=" + Nonce + " mac=" + Mac + " len=1048577")]
    [InlineData("RIFT/1 ts=-1 nonce=" + Nonce + " mac=" + Mac + " len=1")]
    [InlineData("RIFT/1 ts=1 nonce=" + Nonce + " mac=" + Mac + " len=1 extra=1")]
    [InlineData("RIFT/1 proof=" + Mac + " len=1 ts=1")]
    public void Malformed_headers_are_rejected(string line)
    {
        Assert.Null(FrameHeader.TryParse(line));
    }

    [Fact]
    public void Oversized_header_is_rejected()
    {
        string line = "RIFT/1 health len=0" + new string(' ', 300);
        Assert.Null(FrameHeader.TryParse(line));
    }
}

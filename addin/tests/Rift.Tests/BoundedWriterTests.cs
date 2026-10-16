// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System.IO;
using System.Text;

using Xunit;

using Rift.Scripting;

namespace Rift.Tests;

/// <summary>Plan M1, test 15c (writer part): output is cut at the byte limit, never inside a character, and flagged.</summary>
public sealed class BoundedWriterTests
{
    [Fact]
    public void Two_mebibytes_give_one_and_the_flag()
    {
        var w = new BoundedWriter();
        string chunk = new('a', 1024);
        for (int i = 0; i < 2048; i++)
        {
            w.Write(chunk);
        }
        Assert.True(w.Truncated);
        Assert.Equal(BoundedWriter.DefaultMaxBytes, w.ByteCount);
        Assert.Equal(BoundedWriter.DefaultMaxBytes, w.ToString().Length);
    }

    [Fact]
    public void Multi_byte_characters_are_never_split_and_inner_receives_what_is_kept()
    {
        var inner = new StringWriter();
        var w = new BoundedWriter(7, inner);
        w.Write("é😀é");   // 2 + 4 + 2 bytes
        Assert.True(w.Truncated);
        Assert.Equal("é😀", w.ToString());
        Assert.Equal(6, w.ByteCount);
        Assert.Equal("é😀", inner.ToString());
        w.Write("x");
        Assert.Equal("é😀", w.ToString());
    }

    [Fact]
    public void Under_the_limit_nothing_is_flagged()
    {
        var w = new BoundedWriter(16);
        w.Write("hello");
        w.Write('\n');
        Assert.False(w.Truncated);
        Assert.Equal(Encoding.UTF8.GetByteCount("hello\n"), w.ByteCount);
    }
}

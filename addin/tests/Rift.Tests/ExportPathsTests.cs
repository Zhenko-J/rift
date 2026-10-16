// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;

using Xunit;

using Rift.Core.IO;
using Rift.Core.Protocol;

namespace Rift.Tests;

/// <summary>M4 exports: where they may write and under which name.</summary>
public sealed class ExportPathsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rift-export-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_folder_is_absolute_and_never_one_of_rifts_own()
    {
        string data = Path.Combine(_dir, "rift");
        Assert.Equal(Path.Combine(_dir, "out"), ExportPaths.Folder(Path.Combine(_dir, "out") + Path.DirectorySeparatorChar, new[] { data }));
        foreach (string? bad in new[] { null, "", "out", @"..\out", data, Path.Combine(data, "instances") })
        {
            OperationException ex = Assert.Throws<OperationException>(() => ExportPaths.Folder(bad, new[] { data }));
            Assert.Equal(ErrorCodes.BadRequest, ex.Code);
        }
        Assert.Equal(Path.Combine(_dir, "rift-exports"), ExportPaths.Folder(Path.Combine(_dir, "rift-exports"), new[] { data }));
    }

    [Fact]
    public void The_stem_has_no_separator_nor_reserved_character()
    {
        Assert.Equal("A101 _ Plan_ level 1", ExportPaths.Stem("A101 / Plan: level 1", "x"));
        Assert.Equal("_evil", ExportPaths.Stem(@"..\evil", "x"));
        Assert.DoesNotContain('\\', ExportPaths.Stem(@"..\..\evil", "x"));
        Assert.DoesNotContain('/', ExportPaths.Stem("../../evil", "x"));
        Assert.Equal("Model", ExportPaths.Stem("  ", "Model"));
        Assert.Equal("export", ExportPaths.Stem("...", ""));
        Assert.Equal(ExportPaths.MaxStem, ExportPaths.Stem(new string('a', 300), "x").Length);
    }

    [Fact]
    public void Existing_files_of_the_stem_are_kept_unless_overwrite()
    {
        Directory.CreateDirectory(_dir);
        ExportPaths.CheckOverwrite(_dir, "Plan", overwrite: false);
        File.WriteAllText(Path.Combine(_dir, "Plan - Level 1.dwg"), "x");
        File.WriteAllText(Path.Combine(_dir, "Other.dwg"), "x");
        OperationException ex = Assert.Throws<OperationException>(() => ExportPaths.CheckOverwrite(_dir, "Plan", overwrite: false));
        Assert.Equal(ErrorCodes.FileExists, ex.Code);
        ExportPaths.CheckOverwrite(_dir, "Plan", overwrite: true);
        Assert.Single(ExportPaths.WrittenSince(_dir, "Plan", DateTime.UtcNow.AddMinutes(-1)));
        Assert.Empty(ExportPaths.WrittenSince(_dir, "Plan", DateTime.UtcNow.AddMinutes(5)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

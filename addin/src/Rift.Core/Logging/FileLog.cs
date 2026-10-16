// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Globalization;
using System.IO;

namespace Rift.Core.Logging;

/// <summary>Append-only file log. Never throws: logging must not take Revit down.</summary>
public sealed class FileLog
{
    private readonly string? _path;
    private readonly object _gate = new();

    public FileLog(string? path)
    {
        _path = path;
        if (path is not null)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public static FileLog Null { get; } = new(null);

    public string? Path => _path;

    public void Write(string line)
    {
        if (_path is null)
        {
            return;
        }
        try
        {
            lock (_gate)
            {
                File.AppendAllText(_path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.IO;
using System.Threading;

namespace Rift.Core.IO;

/// <summary>Final step of an atomic write (temp file + move, ADR-003), tolerant of a reader holding the target open.</summary>
public static class AtomicFile
{
    /// <summary>
    /// Moves <paramref name="tmp"/> over <paramref name="final"/>. A client reading the target at this instant blocks the
    /// replace (sharing violation): retry for about two seconds, then delete the temp file and rethrow.
    /// </summary>
    public static void MoveReplacing(string tmp, string final)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, final, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
            {
                Thread.Sleep(10 * attempt);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

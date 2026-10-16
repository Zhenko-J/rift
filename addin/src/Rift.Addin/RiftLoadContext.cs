// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Reflection;
using System.Runtime.Loader;

using Rift.Core;

namespace Rift.Addin;

/// <summary>
/// Isolated load context for Rift.Revit and its dependencies (ADR-021). Assemblies listed in
/// Rift.Revit.deps.json are loaded here; anything else (RevitAPI, RevitAPIUI, the shared framework)
/// returns null and therefore resolves in Revit's default context, which preserves type identity.
/// Non-collectible: the engine and the Revit event handlers live for the whole session.
/// </summary>
internal sealed class RiftLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public RiftLoadContext(string mainAssemblyPath)
        : base(Brand.Id, isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

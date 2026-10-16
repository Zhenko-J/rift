// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
namespace Rift.Core;

/// <summary>
/// Product names in one place (ADR-028). The display name feeds every visible string (ribbon, Status, activity card);
/// the identifier names the data folder, the pipe and the load context. This file is compiled into Rift.Core and,
/// as a linked source, into the Rift.Addin bootstrap, which must not reference Rift.Core (ADR-021).
/// </summary>
public static class Brand
{
    /// <summary>Display name of the MCP server and its add-in.</summary>
    public const string Product = "RIFT";

    /// <summary>Lowercase identifier: <c>%LOCALAPPDATA%\rift</c>, pipe <c>rift-revit-&lt;pid&gt;</c>, load context, button ids.</summary>
    public const string Id = "rift";

    /// <summary>Ribbon tab shared by the maintainer's suite of Revit plugins; each plugin creates it when missing, otherwise reuses it.</summary>
    public const string RibbonTab = "NORN";

    /// <summary>Panel of this add-in inside the shared tab.</summary>
    public const string RibbonPanel = Product;
}

// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rift.Scripting.CSharp;

/// <summary>One construct of a C# script that RIFT refuses (ADR-035), with its line in the script.</summary>
public sealed record CSharpViolation(int Line, string What, string Reason);

/// <summary>
/// What a C# script may use (ADR-035), checked on the syntax and on the bound symbols before anything is emitted. The
/// script is a method body: no type declarations. Refused: <c>goto</c>, <c>unsafe</c>, <c>fixed</c>, <c>stackalloc</c>,
/// <c>extern</c>, <c>dynamic</c>, <c>async</c>/<c>await</c>, attributes, preprocessor directives, the <c>__arglist</c> family,
/// identifiers starting with <c>__rift</c>, event subscriptions (code that would run after the guard), and every symbol whose
/// namespace (or the namespace of its type, return type or parameters) is outside an allow-list: the core of
/// <c>System</c>, collections, LINQ, text, globalization and the Revit API — no files, processes, network, reflection,
/// threads, interop or assembly loading. A few <c>System</c> and Revit types are refused by name (environment, activator,
/// external events, updaters).
/// </summary>
public static class CSharpPolicy
{
    public static readonly IReadOnlySet<string> AllowedNamespaces = new HashSet<string>(StringComparer.Ordinal)
    {
        "",
        "System",
        "System.Collections",
        "System.Collections.Generic",
        "System.Collections.ObjectModel",
        "System.Linq",
        "System.Text",
        "System.Text.RegularExpressions",
        "System.Globalization",
        "Autodesk.Revit.DB",
        "Autodesk.Revit.DB.Architecture",
        "Autodesk.Revit.DB.Structure",
        "Autodesk.Revit.DB.Mechanical",
        "Autodesk.Revit.DB.Electrical",
        "Autodesk.Revit.DB.Plumbing",
        "Autodesk.Revit.DB.Analysis",
        "Autodesk.Revit.DB.Visual",
        "Autodesk.Revit.DB.ExtensibleStorage",
        "Autodesk.Revit.Creation",
        "Autodesk.Revit.ApplicationServices",
        "Autodesk.Revit.Exceptions",
        "Autodesk.Revit.UI",
        "Autodesk.Revit.UI.Selection",
    };

    /// <summary>Types refused by full name even though their namespace is allowed.</summary>
    public static readonly IReadOnlySet<string> DeniedTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "System.Environment",
        "System.AppDomain",
        "System.AppContext",
        "System.Activator",
        "System.GC",
        "System.Console",
        "System.Runtime.CompilerServices.RuntimeHelpers",
        "Autodesk.Revit.UI.ExternalEvent",
        "Autodesk.Revit.UI.IExternalEventHandler",
        "Autodesk.Revit.DB.UpdaterRegistry",
        "Autodesk.Revit.DB.IUpdater",
    };

    /// <summary>Members of <see cref="Type"/> a script may use (it needs <c>typeof(Wall)</c>, not reflection).</summary>
    private static readonly HashSet<string> s_typeMembers = new(StringComparer.Ordinal)
    {
        "Name", "FullName", "Namespace", "IsAssignableFrom", "IsInstanceOfType", "Equals", "GetHashCode", "ToString",
        "op_Equality", "op_Inequality",
    };

    public static IReadOnlyList<CSharpViolation> CheckSyntax(SyntaxNode body, int lineOffset)
    {
        ArgumentNullException.ThrowIfNull(body);
        var found = new List<CSharpViolation>();
        void Add(SyntaxNode n, string what, string reason) => found.Add(new CSharpViolation(Line(n, lineOffset), what, reason));
        foreach (SyntaxTrivia trivia in body.DescendantTrivia(descendIntoTrivia: true))
        {
            if (trivia.IsDirective)
            {
                found.Add(new CSharpViolation(trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1 - lineOffset, "#directive", "preprocessor directives are not allowed"));
            }
        }
        foreach (SyntaxNode n in body.DescendantNodesAndSelf())
        {
            switch (n)
            {
                case GotoStatementSyntax:
                    Add(n, "goto", "goto is not allowed (loops are bounded by the timeout checks)");
                    break;
                case UnsafeStatementSyntax or FixedStatementSyntax or StackAllocArrayCreationExpressionSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax:
                    Add(n, n.Kind().ToString(), "unsafe code is not allowed");
                    break;
                case AttributeListSyntax:
                    Add(n, "attribute", "attributes are not allowed");
                    break;
                case AwaitExpressionSyntax:
                    Add(n, "await", "async code is not allowed");
                    break;
                case MakeRefExpressionSyntax or RefTypeExpressionSyntax or RefValueExpressionSyntax:
                    Add(n, n.Kind().ToString(), "not allowed");
                    break;
                case IdentifierNameSyntax id when id.Identifier.ValueText == "dynamic":
                    Add(n, "dynamic", "dynamic is not allowed");
                    break;
                case LocalFunctionStatementSyntax lf when lf.Modifiers.Any(m => m.IsKind(SyntaxKind.ExternKeyword) || m.IsKind(SyntaxKind.UnsafeKeyword) || m.IsKind(SyntaxKind.AsyncKeyword)):
                    Add(n, "local function modifier", "extern, unsafe and async are not allowed");
                    break;
                case AnonymousFunctionExpressionSyntax af when af.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword):
                    Add(n, "async lambda", "async code is not allowed");
                    break;
            }
        }
        foreach (SyntaxToken t in body.DescendantTokens())
        {
            if (t.IsKind(SyntaxKind.ArgListKeyword))
            {
                found.Add(new CSharpViolation(t.GetLocation().GetLineSpan().StartLinePosition.Line + 1 - lineOffset, "__arglist", "not allowed"));
            }
            if (t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText.StartsWith("__rift", StringComparison.Ordinal))
            {
                found.Add(new CSharpViolation(t.GetLocation().GetLineSpan().StartLinePosition.Line + 1 - lineOffset, t.ValueText, "names starting with __rift are reserved"));
            }
        }
        return found;
    }

    public static IReadOnlyList<CSharpViolation> CheckSymbols(SyntaxNode body, SemanticModel model, int lineOffset)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(model);
        var found = new List<CSharpViolation>();
        var seen = new HashSet<(int, string)>();
        foreach (SyntaxNode n in body.DescendantNodesAndSelf())
        {
            if (n is AssignmentExpressionSyntax a && (a.IsKind(SyntaxKind.AddAssignmentExpression) || a.IsKind(SyntaxKind.SubtractAssignmentExpression))
                && model.GetSymbolInfo(a.Left).Symbol is IEventSymbol ev)
            {
                found.Add(new CSharpViolation(Line(n, lineOffset), ev.ToDisplayString(), "event subscriptions are not allowed: the handler would run after the guard"));
                continue;
            }
            if (n is not (SimpleNameSyntax or MemberAccessExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
                or QualifiedNameSyntax or InvocationExpressionSyntax or ElementAccessExpressionSyntax or PredefinedTypeSyntax))
            {
                continue;
            }
            SymbolInfo info = model.GetSymbolInfo(n);
            IEnumerable<ISymbol> symbols = info.Symbol is { } s ? new[] { s } : info.CandidateSymbols;
            foreach (ISymbol symbol in symbols)
            {
                string? reason = Refusal(symbol);
                if (reason is not null && seen.Add((Line(n, lineOffset), symbol.ToDisplayString())))
                {
                    found.Add(new CSharpViolation(Line(n, lineOffset), symbol.ToDisplayString(), reason));
                }
            }
        }
        return found;
    }

    /// <summary>Why <paramref name="symbol"/> is refused, or null.</summary>
    public static string? Refusal(ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        switch (symbol)
        {
            case INamespaceSymbol or ILocalSymbol or IParameterSymbol or ILabelSymbol or IRangeVariableSymbol or IDiscardSymbol:
                return symbol is ILocalSymbol l ? TypeRefusal(l.Type) : symbol is IParameterSymbol p ? TypeRefusal(p.Type) : null;
            case ITypeSymbol t:
                return TypeRefusal(t);
            case IMethodSymbol m when m.MethodKind == MethodKind.LocalFunction || m.MethodKind == MethodKind.AnonymousFunction:
                return null;
        }
        INamedTypeSymbol? owner = symbol.ContainingType;
        if (owner is not null && TypeRefusal(owner) is { } ownerReason)
        {
            return ownerReason;
        }
        if (owner?.ToDisplayString() == "System.Type" && !s_typeMembers.Contains(symbol.Name))
        {
            return "reflection is not allowed (System.Type." + symbol.Name + ")";
        }
        switch (symbol)
        {
            case IMethodSymbol m:
                return TypeRefusal(m.ReturnType) ?? m.Parameters.Select(p => TypeRefusal(p.Type)).FirstOrDefault(r => r is not null);
            case IPropertySymbol p:
                return TypeRefusal(p.Type);
            case IFieldSymbol f:
                return TypeRefusal(f.Type);
            case IEventSymbol:
                return null;
        }
        return null;
    }

    private static string? TypeRefusal(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol a:
                return TypeRefusal(a.ElementType);
            case IPointerTypeSymbol or IFunctionPointerTypeSymbol:
                return "pointers are not allowed";
            case IDynamicTypeSymbol:
                return "dynamic is not allowed";
            case ITypeParameterSymbol or IErrorTypeSymbol:
                return null;
        }
        if (type is INamedTypeSymbol named)
        {
            if (named.IsTupleType || named.IsAnonymousType)
            {
                return named.TypeArguments.Select(TypeRefusal).FirstOrDefault(r => r is not null);
            }
            string full = named.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal);
            int generic = full.IndexOf('<', StringComparison.Ordinal);
            if (generic >= 0)
            {
                full = full[..generic];
            }
            if (DeniedTypes.Contains(full))
            {
                return full + " is not allowed";
            }
            string ns = named.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            if (named.ContainingNamespace is { IsGlobalNamespace: true })
            {
                ns = string.Empty;
            }
            if (!AllowedNamespaces.Contains(ns))
            {
                return "namespace " + ns + " is not allowed (" + full + ")";
            }
            foreach (ITypeSymbol arg in named.TypeArguments)
            {
                if (TypeRefusal(arg) is { } r)
                {
                    return r;
                }
            }
        }
        return null;
    }

    private static int Line(SyntaxNode n, int lineOffset) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1 - lineOffset;
}

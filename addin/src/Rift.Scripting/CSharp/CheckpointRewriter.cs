// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using System;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rift.Scripting.CSharp;

/// <summary>
/// The cooperative timeout of C# scripts (ADR-035, the C# form of ADR-016): a call to <c>__rift_check()</c> at the start of
/// every loop body, local function, lambda and anonymous method (it throws once the deadline passed, the run was cancelled
/// or the stack is nearly exhausted); a <c>when (__rift_ok())</c> filter on every <c>catch</c>, so that a script cannot
/// swallow the stop; and <c>__rift_track(...)</c> around every transaction the script creates, so that the guard can roll
/// back what it left open. Inserted without line breaks: the script's line numbers do not move.
/// </summary>
internal sealed class CheckpointRewriter : CSharpSyntaxRewriter
{
    private static readonly StatementSyntax s_check = SyntaxFactory.ParseStatement("__rift_check();");
    private static readonly ExpressionSyntax s_ok = SyntaxFactory.ParseExpression("__rift_ok()");
    private static readonly string[] s_tracked =
    {
        "Autodesk.Revit.DB.Transaction", "Autodesk.Revit.DB.SubTransaction", "Autodesk.Revit.DB.TransactionGroup",
    };

    private readonly SemanticModel _model;

    public CheckpointRewriter(SemanticModel model)
    {
        _model = model;
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return visited.WithStatement(Checked(visited.Statement));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return visited.WithStatement(Checked(visited.Statement));
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
        return visited.WithStatement(Checked(visited.Statement));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
        return visited.WithStatement(Checked(visited.Statement));
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
        return visited.WithStatement(Checked(visited.Statement));
    }

    public override SyntaxNode? VisitCatchClause(CatchClauseSyntax node)
    {
        var visited = (CatchClauseSyntax)base.VisitCatchClause(node)!;
        ExpressionSyntax condition = visited.Filter is null
            ? s_ok
            : SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, s_ok, SyntaxFactory.ParenthesizedExpression(visited.Filter.FilterExpression));
        return visited.WithFilter(SyntaxFactory.CatchFilterClause(condition).WithLeadingTrivia(SyntaxFactory.Space));
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        bool returnsVoid = node.ReturnType is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.VoidKeyword);
        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        if (visited.Body is { } body)
        {
            return visited.WithBody(Prepend(body));
        }
        if (visited.ExpressionBody is { } arrow)
        {
            return visited.WithExpressionBody(null).WithSemicolonToken(default).WithBody(BlockOf(arrow.Expression, returnsVoid));
        }
        return visited;
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        bool returnsVoid = ReturnsVoid(node);
        var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
        return visited.Block is { } block
            ? visited.WithBlock(Prepend(block))
            : visited.WithExpressionBody(null).WithBlock(BlockOf(visited.ExpressionBody!, returnsVoid));
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        bool returnsVoid = ReturnsVoid(node);
        var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
        return visited.Block is { } block
            ? visited.WithBlock(Prepend(block))
            : visited.WithExpressionBody(null).WithBlock(BlockOf(visited.ExpressionBody!, returnsVoid));
    }

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        var visited = (AnonymousMethodExpressionSyntax)base.VisitAnonymousMethodExpression(node)!;
        return visited.WithBlock(Prepend(visited.Block));
    }

    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        bool tracked = IsTracked(node);
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        return tracked ? Track(visited) : visited;
    }

    public override SyntaxNode? VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        bool tracked = IsTracked(node);
        var visited = (ImplicitObjectCreationExpressionSyntax)base.VisitImplicitObjectCreationExpression(node)!;
        return tracked ? Track(visited) : visited;
    }

    private bool IsTracked(ExpressionSyntax creation) =>
        _model.GetTypeInfo(creation).Type is { } t && Array.IndexOf(s_tracked, t.ToDisplayString()) >= 0;

    private static InvocationExpressionSyntax Track(ExpressionSyntax created) =>
        SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("__rift_track"),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(created.WithoutTrivia()))))
            .WithTriviaFrom(created);

    private bool ReturnsVoid(LambdaExpressionSyntax lambda) =>
        _model.GetSymbolInfo(lambda).Symbol is IMethodSymbol m && m.ReturnsVoid;

    private static StatementSyntax Checked(StatementSyntax body) => body is BlockSyntax block
        ? Prepend(block)
        : SyntaxFactory.Block(s_check, body).WithTriviaFrom(body);

    private static BlockSyntax Prepend(BlockSyntax block) => block.WithStatements(block.Statements.Insert(0, s_check));

    private static BlockSyntax BlockOf(ExpressionSyntax expression, bool returnsVoid)
    {
        StatementSyntax tail = returnsVoid
            ? SyntaxFactory.ExpressionStatement(expression.WithoutTrivia())
            : SyntaxFactory.ReturnStatement(expression.WithoutTrivia().WithLeadingTrivia(SyntaxFactory.Space));
        return SyntaxFactory.Block(s_check, tail).WithTriviaFrom(expression);
    }
}

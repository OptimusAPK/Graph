using CodeGraph.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeGraph.Analysis;

/// <summary>Extracts nodes and edges from one project using Roslyn's semantic model.</summary>
public static class SemanticProjectAnalyzer
{
    public static async Task AnalyzeAsync(Project project, string projectName, HashSet<string> solutionAssemblies,
        GraphBuilder graph, CancellationToken ct)
    {
        var compilation = await project.GetCompilationAsync(ct)
                          ?? throw new InvalidOperationException("Roslyn produced no compilation.");

        var analyzed = 0;
        foreach (var document in project.Documents)
        {
            ct.ThrowIfCancellationRequested();
            if (document.SourceCodeKind != SourceCodeKind.Regular || FileFilters.IsGenerated(document.FilePath))
                continue;

            var tree = await document.GetSyntaxTreeAsync(ct);
            if (tree is null) continue;
            var model = compilation.GetSemanticModel(tree);
            var walker = new Walker(model, projectName, document.FilePath!, solutionAssemblies, graph, ct);
            walker.Visit(await tree.GetRootAsync(ct));
            analyzed++;
        }

        if (analyzed == 0)
            throw new InvalidOperationException("The loaded project contains no C# source files.");
    }

    /// <summary>Stable id for a symbol declared in the solution: assembly + documentation comment id.</summary>
    public static string? SymbolId(ISymbol? symbol)
    {
        if (symbol is null) return null;
        symbol = symbol.OriginalDefinition;
        var assembly = symbol.ContainingAssembly?.Name;
        var docId = symbol.GetDocumentationCommentId();
        return assembly is null || docId is null ? null : assembly + "|" + docId;
    }

    private sealed class Walker(
        SemanticModel model,
        string projectName,
        string filePath,
        HashSet<string> solutionAssemblies,
        GraphBuilder graph,
        CancellationToken ct) : CSharpSyntaxWalker
    {
        private readonly string _projectId = GraphBuilder.ProjectId(projectName);
        private readonly Stack<string> _scope = new();

        /// <summary>The node that edges found "here" start from: enclosing method, else type, else project.</summary>
        private string Current => _scope.Count > 0 ? _scope.Peek() : _projectId;

        // ------------------------------------------------------------ types

        public override void VisitClassDeclaration(ClassDeclarationSyntax node) =>
            VisitType(node, NodeKinds.Class, () => base.VisitClassDeclaration(node));

        public override void VisitStructDeclaration(StructDeclarationSyntax node) =>
            VisitType(node, NodeKinds.Struct, () => base.VisitStructDeclaration(node));

        public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) =>
            VisitType(node, NodeKinds.Interface, () => base.VisitInterfaceDeclaration(node));

        public override void VisitRecordDeclaration(RecordDeclarationSyntax node) =>
            VisitType(node, NodeKinds.Record, () => base.VisitRecordDeclaration(node));

        public override void VisitEnumDeclaration(EnumDeclarationSyntax node) =>
            VisitType(node, NodeKinds.Enum, () => base.VisitEnumDeclaration(node));

        private void VisitType(BaseTypeDeclarationSyntax node, string kind, Action visitChildren)
        {
            var symbol = model.GetDeclaredSymbol(node, ct);
            var id = SymbolId(symbol);
            if (symbol is null || id is null)
            {
                visitChildren();
                return;
            }

            var parentId = ParentOf(symbol);
            var isNew = graph.AddNode(id, symbol.Name,
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                kind, projectName, filePath, LineOf(node.Identifier), parentId);

            if (isNew) // partial types: structure and bases only once
            {
                graph.AddEdge(parentId, id, EdgeTypes.Contains);

                if (symbol.BaseType is { SpecialType: SpecialType.None } baseType && symbol.TypeKind == TypeKind.Class)
                    AddTypeEdge(id, baseType, EdgeTypes.Inherits);

                var interfaceEdge = symbol.TypeKind == TypeKind.Interface ? EdgeTypes.Inherits : EdgeTypes.Implements;
                foreach (var iface in symbol.Interfaces)
                    AddTypeEdge(id, iface, interfaceEdge);
            }

            // Primary constructor parameters (records, C# 12 classes/structs).
            if (node is TypeDeclarationSyntax { ParameterList: { } parameters })
                foreach (var parameter in parameters.Parameters)
                    AddTypeReferences(id, model.GetDeclaredSymbol(parameter, ct)?.Type);

            _scope.Push(id);
            visitChildren();
            _scope.Pop();
        }

        /// <summary>Containing type, or the (per-project) namespace node, or the project for global types.</summary>
        private string ParentOf(INamedTypeSymbol symbol)
        {
            if (symbol.ContainingType is { } outer && SymbolId(outer) is { } outerId) return outerId;

            var ns = symbol.ContainingNamespace;
            if (ns is null || ns.IsGlobalNamespace) return _projectId;

            var name = ns.ToDisplayString();
            var nsId = projectName + "|N:" + name;
            if (graph.AddNode(nsId, name, name, NodeKinds.Namespace, projectName, null, 0, _projectId))
                graph.AddEdge(_projectId, nsId, EdgeTypes.Contains);
            return nsId;
        }

        // ------------------------------------------------------------ methods

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node) =>
            VisitMethod(node, node.Identifier, () => base.VisitMethodDeclaration(node));

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            VisitMethod(node, node.Identifier, () => base.VisitConstructorDeclaration(node));

        private void VisitMethod(BaseMethodDeclarationSyntax node, SyntaxToken identifier, Action visitChildren)
        {
            var symbol = model.GetDeclaredSymbol(node, ct);
            var id = SymbolId(symbol);
            var typeId = SymbolId(symbol?.ContainingType);
            if (symbol is null || id is null || typeId is null)
            {
                visitChildren();
                return;
            }

            var name = symbol.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor
                ? symbol.ContainingType.Name + ".ctor"
                : symbol.Name;

            if (graph.AddNode(id, name, symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                    NodeKinds.Method, projectName, filePath, LineOf(identifier), typeId))
            {
                graph.AddEdge(typeId, id, EdgeTypes.Contains);
                if (!symbol.ReturnsVoid) AddTypeReferences(id, symbol.ReturnType);
                foreach (var parameter in symbol.Parameters) AddTypeReferences(id, parameter.Type);
            }

            _scope.Push(id);
            visitChildren();
            _scope.Pop();
        }

        // ------------------------------------------------------------ member types

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            AddTypeReferences(Current, model.GetTypeInfo(node.Declaration.Type, ct).Type);
            base.VisitFieldDeclaration(node);
        }

        public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        {
            AddTypeReferences(Current, model.GetTypeInfo(node.Declaration.Type, ct).Type);
            base.VisitEventFieldDeclaration(node);
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            AddTypeReferences(Current, model.GetTypeInfo(node.Type, ct).Type);
            base.VisitPropertyDeclaration(node); // accessor bodies are attributed to the type
        }

        public override void VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            AddTypeReferences(Current, model.GetTypeInfo(node.Type, ct).Type);
            foreach (var parameter in node.ParameterList.Parameters)
                AddTypeReferences(Current, model.GetDeclaredSymbol(parameter, ct)?.Type);
            base.VisitIndexerDeclaration(node);
        }

        // ------------------------------------------------------------ bodies

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            AddCall(ResolveMethod(node));
            base.VisitInvocationExpression(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            AddCreation(node);
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
        {
            AddCreation(node);
            base.VisitImplicitObjectCreationExpression(node);
        }

        private void AddCreation(ExpressionSyntax node)
        {
            AddTypeReferences(Current, model.GetTypeInfo(node, ct).Type);
            AddCall(ResolveMethod(node)); // only lands if the constructor is declared explicitly
        }

        private IMethodSymbol? ResolveMethod(ExpressionSyntax node)
        {
            var info = model.GetSymbolInfo(node, ct);
            return (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
        }

        private void AddCall(IMethodSymbol? method)
        {
            if (method is null || method.MethodKind == MethodKind.LocalFunction) return;
            method = method.ReducedFrom ?? method;               // extension methods called as instance methods
            method = method.PartialDefinitionPart ?? method;
            if (!IsInSolution(method)) return;
            if (SymbolId(method) is { } id) graph.AddEdge(Current, id, EdgeTypes.Calls);
        }

        // ------------------------------------------------------------ helpers

        private bool IsInSolution(ISymbol symbol) =>
            symbol.ContainingAssembly is { } assembly && solutionAssemblies.Contains(assembly.Name);

        private void AddTypeEdge(string source, INamedTypeSymbol target, string edgeType)
        {
            if (target.TypeKind == TypeKind.Error || !IsInSolution(target)) return;
            if (SymbolId(target) is { } id) graph.AddEdge(source, id, edgeType);
        }

        /// <summary>References every solution type mentioned in a type, e.g. both in <c>List&lt;Order[]&gt;</c>.</summary>
        private void AddTypeReferences(string source, ITypeSymbol? type)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    AddTypeReferences(source, array.ElementType);
                    break;
                case IPointerTypeSymbol pointer:
                    AddTypeReferences(source, pointer.PointedAtType);
                    break;
                case INamedTypeSymbol named:
                    AddTypeEdge(source, named, EdgeTypes.References);
                    foreach (var argument in named.TypeArguments) AddTypeReferences(source, argument);
                    break;
            }
        }

        private static int LineOf(SyntaxToken token) =>
            token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }
}

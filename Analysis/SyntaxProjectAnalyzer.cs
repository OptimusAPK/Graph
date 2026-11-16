using System.Xml.Linq;
using CodeGraph.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeGraph.Analysis;

/// <summary>
/// Fallback for projects MSBuildWorkspace could not load: parses the .cs files under the project
/// folder and links things by simple name. Less accurate than the semantic analyzer.
/// </summary>
public static class SyntaxProjectAnalyzer
{
    public static void Analyze(string csprojPath, string projectName, GraphBuilder graph, CancellationToken ct)
    {
        graph.AddProjectNode(projectName, csprojPath);
        AddProjectReferences(csprojPath, projectName, graph);

        var directory = Path.GetDirectoryName(csprojPath)!;
        var files = InputResolver.FindFiles(directory,
            f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !FileFilters.IsGenerated(f),
            stopAtNestedProjects: true);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file, cancellationToken: ct);
                new Walker(projectName, file, graph).Visit(tree.GetRoot(ct));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                graph.Warn($"{projectName}: could not read {graph.RelativePath(file)} ({ex.Message}).");
            }
        }
    }

    private static void AddProjectReferences(string csprojPath, string projectName, GraphBuilder graph)
    {
        try
        {
            foreach (var reference in XDocument.Load(csprojPath).Descendants()
                         .Where(e => e.Name.LocalName == "ProjectReference"))
            {
                var include = ((string?)reference.Attribute("Include"))?.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(include)) continue;
                var target = Path.GetFileNameWithoutExtension(include);
                graph.AddEdge(GraphBuilder.ProjectId(projectName), GraphBuilder.ProjectId(target),
                    EdgeTypes.ProjectReference);
            }
        }
        catch (Exception)
        {
            // unreadable/odd csproj: no project-reference edges for it
        }
    }

    private sealed class Walker(string projectName, string filePath, GraphBuilder graph) : CSharpSyntaxWalker
    {
        private readonly string _projectId = GraphBuilder.ProjectId(projectName);
        private readonly Stack<string> _scope = new();
        private readonly Stack<string> _types = new();

        private string Current => _scope.Count > 0 ? _scope.Peek() : _projectId;
        private string? CurrentType => _types.Count > 0 ? _types.Peek() : null;

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
            var ns = NamespaceOf(node);
            var arity = (node as TypeDeclarationSyntax)?.TypeParameterList?.Parameters.Count ?? 0;
            var localName = node.Identifier.Text + (arity > 0 ? "`" + arity : "");

            string parentId, fullName, id;
            if (CurrentType is { } outer)
            {
                parentId = outer;
                id = outer + "." + localName;
                fullName = id[(id.IndexOf("|T:", StringComparison.Ordinal) + 3)..];
            }
            else
            {
                fullName = ns.Length > 0 ? ns + "." + localName : localName;
                id = projectName + "|T:" + fullName;
                parentId = ns.Length > 0 ? EnsureNamespace(ns) : _projectId;
            }

            if (graph.AddNode(id, node.Identifier.Text, fullName, kind, projectName, filePath,
                    LineOf(node.Identifier), parentId))
                graph.AddEdge(parentId, id, EdgeTypes.Contains);

            if (node.BaseList is { } bases)
                foreach (var baseType in bases.Types)
                    if (RightmostName(baseType.Type) is { } name)
                        graph.AddNameEdge(id, name, GraphBuilder.NameEdgeKind.BaseType, projectName, id);

            if (node is TypeDeclarationSyntax { ParameterList: { } parameters })
                foreach (var parameter in parameters.Parameters)
                    AddTypeReferences(id, parameter.Type);

            _scope.Push(id);
            _types.Push(id);
            visitChildren();
            _types.Pop();
            _scope.Pop();
        }

        private string EnsureNamespace(string ns)
        {
            var nsId = projectName + "|N:" + ns;
            if (graph.AddNode(nsId, ns, ns, NodeKinds.Namespace, projectName, null, 0, _projectId))
                graph.AddEdge(_projectId, nsId, EdgeTypes.Contains);
            return nsId;
        }

        private static string NamespaceOf(SyntaxNode node) =>
            string.Join(".", node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>()
                .Reverse().Select(n => n.Name.ToString()));

        // ------------------------------------------------------------ methods

        public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            VisitMethod(node, node.Identifier, node.Identifier.Text, node.ReturnType,
                () => base.VisitMethodDeclaration(node));
        }

        public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            VisitMethod(node, node.Identifier, node.Identifier.Text + ".ctor", null,
                () => base.VisitConstructorDeclaration(node));
        }

        private void VisitMethod(BaseMethodDeclarationSyntax node, SyntaxToken identifier, string name,
            TypeSyntax? returnType, Action visitChildren)
        {
            if (CurrentType is not { } typeId)
            {
                visitChildren();
                return;
            }

            var line = LineOf(identifier);
            var id = $"{typeId}|M:{name}@{Path.GetFileName(filePath)}:{line}";
            var signature = $"{name}({string.Join(", ", node.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?"))})";
            if (graph.AddNode(id, name, signature, NodeKinds.Method, projectName, filePath, line, typeId))
                graph.AddEdge(typeId, id, EdgeTypes.Contains);

            AddTypeReferences(id, returnType);
            foreach (var parameter in node.ParameterList.Parameters) AddTypeReferences(id, parameter.Type);

            _scope.Push(id);
            visitChildren();
            _scope.Pop();
        }

        // ------------------------------------------------------------ member types and bodies

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            AddTypeReferences(Current, node.Declaration.Type);
            base.VisitFieldDeclaration(node);
        }

        public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            AddTypeReferences(Current, node.Type);
            base.VisitPropertyDeclaration(node);
        }

        public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
        {
            AddTypeReferences(Current, node.Type);
            base.VisitObjectCreationExpression(node);
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var name = node.Expression switch
            {
                SimpleNameSyntax simple => simple.Identifier.Text,
                MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
                MemberBindingExpressionSyntax binding => binding.Name.Identifier.Text,
                _ => null,
            };
            if (name is not null && name != "nameof")
                graph.AddNameEdge(Current, name, GraphBuilder.NameEdgeKind.Call, projectName, CurrentType);
            base.VisitInvocationExpression(node);
        }

        // ------------------------------------------------------------ helpers

        /// <summary>Queues a by-name reference for every identifier in a type, e.g. List and Order in List&lt;Order&gt;.</summary>
        private void AddTypeReferences(string source, TypeSyntax? type)
        {
            if (type is null) return;
            foreach (var name in type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
                graph.AddNameEdge(source, name.Identifier.Text, GraphBuilder.NameEdgeKind.TypeReference,
                    projectName, CurrentType);
        }

        private static string? RightmostName(TypeSyntax type) => type switch
        {
            SimpleNameSyntax simple => simple.Identifier.Text,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
            _ => null,
        };

        private static int LineOf(SyntaxToken token) =>
            token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    }
}

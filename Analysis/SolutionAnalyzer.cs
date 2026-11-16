using System.Diagnostics;
using CodeGraph.Models;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeGraph.Analysis;

/// <summary>Finds an MSBuild installation (the .NET SDK or Visual Studio) for MSBuildWorkspace.</summary>
public static class MsBuildBootstrap
{
    public static bool IsAvailable { get; private set; }
    public static string? Error { get; private set; }

    /// <summary>Must run once at startup, before any MSBuildWorkspace code is touched.</summary>
    public static void Register()
    {
        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                var instance = MSBuildLocator.QueryVisualStudioInstances()
                    .OrderByDescending(i => i.Version)
                    .FirstOrDefault();
                if (instance is null)
                {
                    Error = "No MSBuild / .NET SDK installation was found.";
                    return;
                }
                MSBuildLocator.RegisterInstance(instance);
            }
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}

/// <summary>
/// Loads the solution with MSBuildWorkspace, runs the semantic analyzer per project and falls back
/// to syntax-only parsing for any project that did not load.
/// </summary>
public static class SolutionAnalyzer
{
    public static async Task<GraphResult> AnalyzeAsync(ResolvedInput input, bool excludeTests,
        Action<string> progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var graph = new GraphBuilder(input.RootDirectory);
        foreach (var warning in input.Warnings) graph.Warn(warning);

        // csproj paths that are done (semantically analyzed, or deliberately skipped).
        var handled = new HashSet<string>(InputResolver.PathComparer);

        if (MsBuildBootstrap.IsAvailable)
        {
            try
            {
                await AnalyzeWithWorkspaceAsync(input, excludeTests, graph, handled, progress, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                graph.Warn($"MSBuildWorkspace failed ({ex.GetType().Name}: {ex.Message}). " +
                           "Remaining projects were parsed syntax-only.");
            }
        }
        else
        {
            graph.Warn($"MSBuild could not be located ({MsBuildBootstrap.Error}); " +
                       "all projects were parsed syntax-only.");
        }

        // Syntax-only fallback for everything the workspace did not deliver.
        foreach (var csproj in input.ProjectPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (handled.Contains(csproj)) continue;
            var name = Path.GetFileNameWithoutExtension(csproj);
            if (excludeTests && FileFilters.IsTestProject(csproj))
            {
                graph.SkippedTestProjects++;
                continue;
            }

            progress($"Parsing {name} (syntax-only)…");
            if (MsBuildBootstrap.IsAvailable)
                graph.Warn($"{name}: could not be loaded by MSBuildWorkspace; parsed syntax-only " +
                           "(relationships are matched by name and may be incomplete).");
            SyntaxProjectAnalyzer.Analyze(csproj, name, graph, ct);
            graph.SyntaxOnlyProjects++;
        }

        progress("Detecting communities…");
        return graph.Build(input.SolutionPath ?? input.RootDirectory, stopwatch);
    }

    private static async Task AnalyzeWithWorkspaceAsync(ResolvedInput input, bool excludeTests, GraphBuilder graph,
        HashSet<string> handled, Action<string> progress, CancellationToken ct)
    {
        using var workspace = MSBuildWorkspace.Create();
        workspace.SkipUnrecognizedProjects = true;
        workspace.LoadMetadataForReferencedProjects = false;

        if (input.SolutionPath is { } sln && sln.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            progress($"Loading {Path.GetFileName(sln)} with MSBuild…");
            await workspace.OpenSolutionAsync(sln, cancellationToken: ct);
        }
        else
        {
            // Folder without a .sln (or a .slnx): open each project; references come along automatically.
            for (var i = 0; i < input.ProjectPaths.Count; i++)
            {
                var csproj = input.ProjectPaths[i];
                if (excludeTests && FileFilters.IsTestProject(csproj)) continue;
                if (workspace.CurrentSolution.Projects.Any(p => SamePath(p.FilePath, csproj))) continue;
                progress($"Loading project {i + 1}/{input.ProjectPaths.Count}: {Path.GetFileName(csproj)}…");
                try
                {
                    await workspace.OpenProjectAsync(csproj, cancellationToken: ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    graph.Warn($"{Path.GetFileNameWithoutExtension(csproj)}: MSBuild load failed ({ex.Message}).");
                }
            }
        }

        var solution = workspace.CurrentSolution;

        foreach (var diagnostic in workspace.Diagnostics
                     .Where(d => d.Kind == WorkspaceDiagnosticKind.Failure)
                     .Select(d => d.Message).Distinct().Take(25))
            graph.Warn("MSBuild: " + diagnostic);

        // Multi-targeted projects appear once per target framework: keep one per .csproj.
        var projects = solution.Projects
            .Where(p => p.Language == LanguageNames.CSharp && p.FilePath is not null)
            .GroupBy(p => p.FilePath!, InputResolver.PathComparer)
            .Select(g => g.First())
            .ToList();

        var included = new List<Project>();
        foreach (var project in projects)
        {
            if (excludeTests && FileFilters.IsTestProject(project.FilePath!))
            {
                handled.Add(project.FilePath!);
                graph.SkippedTestProjects++;
            }
            else
            {
                included.Add(project);
            }
        }

        // Only symbols from these assemblies become nodes/edge targets; everything else is external.
        var solutionAssemblies = included.Select(p => p.AssemblyName).ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < included.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var project = included[i];
            var name = Path.GetFileNameWithoutExtension(project.FilePath!);
            progress($"Analyzing {name} ({i + 1}/{included.Count})…");

            try
            {
                var hasSource = project.Documents.Any(d => !FileFilters.IsGenerated(d.FilePath));
                if (!hasSource && project.MetadataReferences.Count == 0)
                    continue; // empty shell: MSBuild could not evaluate it → syntax-only fallback below

                if (!hasSource)
                {
                    graph.AddProjectNode(name, project.FilePath!); // genuinely empty project
                    handled.Add(project.FilePath!);
                    graph.SemanticProjects++;
                    continue;
                }

                if (project.MetadataReferences.Count == 0)
                    graph.Warn($"{name}: no framework/package references were resolved (has the solution been " +
                               "restored with `dotnet restore`?). Results for this project may be incomplete.");

                await SemanticProjectAnalyzer.AnalyzeAsync(project, name, solutionAssemblies, graph, ct);

                graph.AddProjectNode(name, project.FilePath!);
                foreach (var reference in project.ProjectReferences)
                {
                    if (solution.GetProject(reference.ProjectId)?.FilePath is { } targetPath)
                        graph.AddEdge(GraphBuilder.ProjectId(name),
                            GraphBuilder.ProjectId(Path.GetFileNameWithoutExtension(targetPath)),
                            EdgeTypes.ProjectReference);
                }

                handled.Add(project.FilePath!);
                graph.SemanticProjects++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Not marked handled → picked up by the syntax-only fallback.
                graph.Warn($"{name}: semantic analysis failed ({ex.Message}).");
            }
        }
    }

    private static bool SamePath(string? a, string b) =>
        a is not null && InputResolver.PathComparer.Equals(Path.GetFullPath(a), b);
}

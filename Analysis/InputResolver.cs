using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodeGraph.Analysis;

/// <summary>What the user's path resolved to.</summary>
public sealed record ResolvedInput(
    string CacheKey,
    string RootDirectory,
    string? SolutionPath,
    IReadOnlyList<string> ProjectPaths,
    IReadOnlyList<string> Warnings);

public sealed class InputValidationException(string message) : Exception(message);

/// <summary>
/// Validates the user-supplied path and turns it into a solution and/or a list of .csproj files.
/// Only ever reads from disk.
/// </summary>
public static partial class InputResolver
{
    private static readonly string[] SkippedDirectories =
        ["bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages"];

    public static ResolvedInput Resolve(string? rawPath)
    {
        var path = (rawPath ?? "").Trim().Trim('"', '\'');
        if (path.Length == 0) throw new InputValidationException("Enter a folder path or a .sln path.");
        if (path.StartsWith('~'))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];

        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { throw new InputValidationException("That is not a valid path."); }

        var warnings = new List<string>();

        if (File.Exists(full))
        {
            var ext = Path.GetExtension(full);
            if (!ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
                throw new InputValidationException("The file must be a .sln (or .slnx) file. Otherwise give a folder.");
            return FromSolution(full, warnings);
        }

        if (!Directory.Exists(full))
            throw new InputValidationException($"Path not found: {full}");

        var solutions = FindFiles(full, f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                                          || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
        if (solutions.Count > 0)
        {
            // Prefer the solution closest to the folder root, .sln before .slnx, then alphabetical.
            var chosen = solutions
                .OrderBy(s => s.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(s => s.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(s => s, StringComparer.OrdinalIgnoreCase)
                .First();
            if (solutions.Count > 1)
                warnings.Add($"Found {solutions.Count} solution files; using {Path.GetRelativePath(full, chosen)}. " +
                             "Enter a .sln path directly to choose another.");
            return FromSolution(chosen, warnings);
        }

        var projects = FindFiles(full, f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (projects.Count == 0)
            throw new InputValidationException("No .sln or .csproj files were found under that folder.");

        warnings.Add($"No .sln file found; analyzing {projects.Count} .csproj file(s) found under the folder.");
        projects.Sort(StringComparer.OrdinalIgnoreCase);
        return new ResolvedInput(Normalize(full), full, null, projects, warnings);
    }

    private static ResolvedInput FromSolution(string solutionPath, List<string> warnings)
    {
        var dir = Path.GetDirectoryName(solutionPath)!;
        List<string> projects;
        try { projects = ReadSolutionProjects(solutionPath); }
        catch (Exception ex)
        {
            warnings.Add($"Could not read the project list from {Path.GetFileName(solutionPath)}: {ex.Message}");
            projects = [];
        }
        return new ResolvedInput(Normalize(solutionPath), dir, solutionPath, projects, warnings);
    }

    /// <summary>Lists the .csproj files a solution refers to, without MSBuild.</summary>
    private static List<string> ReadSolutionProjects(string solutionPath)
    {
        var dir = Path.GetDirectoryName(solutionPath)!;
        IEnumerable<string> relative;
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            relative = XDocument.Load(solutionPath).Descendants("Project")
                .Select(p => (string?)p.Attribute("Path") ?? "");
        }
        else
        {
            relative = SlnProjectLine().Matches(File.ReadAllText(solutionPath)).Select(m => m.Groups[1].Value);
        }

        return relative
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct(PathComparer)
            .ToList();
    }

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string Normalize(string path) =>
        OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path;

    /// <summary>Recursive file search that skips build output and tooling folders.</summary>
    public static List<string> FindFiles(string root, Func<string, bool> match, bool stopAtNestedProjects = false)
    {
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                var files = Directory.GetFiles(dir);
                if (stopAtNestedProjects && dir != root &&
                    files.Any(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
                    continue; // belongs to another project

                results.AddRange(files.Where(match));
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue; // no symlink loops
                    pending.Push(sub);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // unreadable folder: skip it
            }
        }
        return results;
    }

    [GeneratedRegex("""^Project\("\{[^}]+\}"\)\s*=\s*"[^"]*"\s*,\s*"([^"]+)"\s*,""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectLine();
}

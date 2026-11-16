using System.Text.RegularExpressions;

namespace CodeGraph.Analysis;

/// <summary>Rules for which files and projects are left out of the graph.</summary>
public static partial class FileFilters
{
    private static readonly string[] GeneratedSuffixes =
        [".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs", ".AssemblyInfo.cs", ".AssemblyAttributes.cs"];

    public static bool IsGenerated(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return true; // source-generator output has no real path
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var suffix in GeneratedSuffixes)
            if (filePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;

        var segments = filePath.Split('/', '\\');
        for (var i = 0; i < segments.Length - 1; i++)
            if (segments[i].Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segments[i].Equals("bin", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Heuristic: the project name looks like a test project, or the .csproj references a test framework.</summary>
    public static bool IsTestProject(string csprojPath)
    {
        var name = Path.GetFileNameWithoutExtension(csprojPath);
        if (TestName().IsMatch(name)) return true;
        try
        {
            var text = File.ReadAllText(csprojPath);
            return TestMarker().IsMatch(text);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [GeneratedRegex(@"(Tests?|Specs|TestHelpers|TestUtilities)$")]
    private static partial Regex TestName();

    [GeneratedRegex("""Microsoft\.NET\.Test\.Sdk|Include="(xunit|nunit|MSTest|TUnit)[^"]*"|<IsTestProject>\s*true""",
        RegexOptions.IgnoreCase)]
    private static partial Regex TestMarker();
}

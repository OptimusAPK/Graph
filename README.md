# CodeGraph

A local .NET 8 web app that reads a C# solution with Roslyn and shows an interactive
"communities" graph of its projects, namespaces, types and methods.

## File tree

```
CodeGraph/
├── CodeGraph.csproj
├── global.json                        pins the build to the .NET 8 SDK
├── nuget.config                       restore only from vendor/nuget (no internet)
├── .gitignore, .gitattributes         build output stays out, vendored files stay untouched
├── CodeGraph.zip                      snapshot for transfer (in the repository only)
├── Program.cs                         minimal API, localhost binding, cache, progress
├── Models/
│   └── GraphModels.cs                 request + graph JSON types
├── Analysis/
│   ├── InputResolver.cs               path validation, .sln / .csproj discovery
│   ├── FileFilters.cs                 generated-file and test-project rules
│   ├── SolutionAnalyzer.cs            MSBuildWorkspace loading + per-project fallback
│   ├── SemanticProjectAnalyzer.cs     nodes/edges from the semantic model
│   ├── SyntaxProjectAnalyzer.cs       syntax-only fallback (matches by name)
│   ├── GraphBuilder.cs                edge filtering, degrees, community naming/merging
│   └── Louvain.cs                     Louvain community detection
├── vendor/
│   └── nuget/                         the 45 NuGet packages the build needs
└── wwwroot/
    ├── index.html
    ├── app.js
    ├── styles.css
    └── lib/                           D3 7.9.0 and its licence
```

## Prerequisites

- **.NET 8 SDK** (8.0.100 or any later 8.0.x). The SDK, not just the runtime: Roslyn uses the
  SDK's MSBuild to read project files. It is the only thing that has to be installed. A newer
  SDK (9, 10) does not replace it, but the two can be installed side by side.
- **Visual Studio / Build Tools are not needed** for SDK-style projects (`<Project Sdk="...">`).
  They are only needed, on Windows, for old-style .NET Framework `.csproj` files; without them
  those projects fall back to syntax-only parsing and are listed under Warnings.
- For the most accurate results, run `dotnet restore` on the solution you want to analyze
  beforehand. CodeGraph never restores or builds it for you.
- **No internet access is needed.** The NuGet packages (`vendor/nuget`) and the D3 graph
  library (`wwwroot/lib`) are part of this folder.

## Run

From this folder:

```bash
dotnet run -c Release
```

Then open **http://localhost:5080**. Set `CODEGRAPH_PORT` to use another port.

## Move it to another device

The folder contains everything except the .NET 8 SDK, so it can be carried over as it is. This
repository already includes a ready-made `CodeGraph.zip` at its root (a snapshot of the project;
it does not update itself when the source changes), and cloning the repository gives the same
files.

1. To make a fresh zip, leave out `bin/` and `obj/` (build output, recreated on the first run;
   without them the zip is about 60 MB) and, in a git clone, `.git/` and the old zip. On
   Windows, from the folder that contains the project folder (called `CodeGraph` here; a clone
   is called `Graph`, so use that name instead):

   ```bash
   tar -a -c -f CodeGraph.zip --exclude "CodeGraph/bin" --exclude "CodeGraph/obj" --exclude "CodeGraph/.git" --exclude "CodeGraph/CodeGraph.zip" CodeGraph
   ```

2. On the other device, check that `dotnet --list-sdks` shows an 8.0.x line. If it does not,
   install the .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0.
3. Unzip, open a terminal in the `CodeGraph` folder and run `dotnet run -c Release`. The first
   run builds the app from the packages in `vendor/nuget`.

On Windows, unzip to a reasonably short location: the path of the `CodeGraph` folder should
stay under about 150 characters. In a much deeper folder the build can lose files to the
260-character path limit without reporting an error, and analysis then falls back to the less
accurate syntax-only mode (shown under Warnings).

This was tested on Windows by unzipping to a new location and building with an empty NuGet
cache and web access blocked. Linux and macOS have not been tested.

## Recreate the project from scratch

```bash
dotnet new web -n CodeGraph -f net8.0
```

```bash
cd CodeGraph
```

```bash
dotnet add package Microsoft.Build.Locator --version 1.7.8
```

```bash
dotnet add package Microsoft.CodeAnalysis.CSharp.Workspaces --version 4.11.0
```

```bash
dotnet add package Microsoft.CodeAnalysis.Workspaces.MSBuild --version 4.11.0
```

Replace `Program.cs` and add the `Models/`, `Analysis/` and `wwwroot/` files (plus
`global.json`, `nuget.config` and `vendor/` if the copy should also build without internet), then:

```bash
dotnet build
```

```bash
dotnet run
```

## API

`POST /api/analyze` with `{ "path": "...", "excludeTests": true, "refresh": false }` returns
`{ source, nodes, edges, communities, warnings, stats, analyzedAt, fromCache }`.

- Node: `id, name, fullName, kind, project, filePath, line, community, degree`
- Edge: `source, target, type, weight` where type is `inherits`, `implements`, `calls`,
  `references`, `projectReference` or `contains` (project → namespace → type → method)
- Results are cached in memory per resolved path and test-project setting; `refresh: true`
  (the Re-analyze button) recomputes.

## Safety

- Listens on `127.0.0.1` only; requests with a non-localhost `Host` or `Origin` get 403.
- Source files are only read. Nothing is compiled to disk, restored, or executed.
- No telemetry. Neither the server nor the page makes outbound calls; D3 is served from
  `wwwroot/lib`.

## Bundled third-party software

- **D3 7.9.0** (`wwwroot/lib/d3.min.js`), ISC licence, see `wwwroot/lib/d3.LICENSE.txt`.
- **NuGet packages** in `vendor/nuget`: unmodified copies from nuget.org of the three packages
  referenced in `CodeGraph.csproj` and their dependencies. Each package carries its own licence.

## Known limitations

- **Interface / DI calls**: a call through an interface (`_repo.Save(x)`) links to the interface
  method, not to the implementation that dependency injection supplies at runtime. Follow the
  `implements` edge to find candidates.
- **Reflection, `dynamic`, source-generated code, string-based dispatch** (e.g. MediatR handlers,
  MVC routing, `Activator.CreateInstance`) produce no edges.
- **Delegates and lambdas**: calls inside a lambda or local function are attributed to the
  enclosing method; invoking a delegate is not linked to its target.
- **Properties, fields, events, operators and local functions are not nodes**. Code in property
  accessors and initializers is attributed to the containing type.
- **Syntax-only fallback** matches by simple name; ambiguous names are dropped and overloads
  collapse to one method.
- **Multi-targeted projects** are analyzed for one target framework only.
- **Test-project detection is heuristic** (name ends in `Tests`/`Test`/`Specs`, or the `.csproj`
  references a test SDK/framework).
- **MSBuild evaluation**: loading a project evaluates its MSBuild files (a design-time build,
  no compilation). Custom targets in an untrusted repository could run at that point, so only
  analyze code you trust.
- **Scale**: above 3000 nodes methods start hidden. Graphs in the tens of thousands of visible
  nodes will lay out slowly; use the kind filters and the minimum-connections slider.
- Louvain is seeded, so results are stable for the same input, but small code changes can move
  community boundaries.

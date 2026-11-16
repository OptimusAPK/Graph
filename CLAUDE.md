# CodeGraph

Local .NET 8 web app that reads a C# solution with Roslyn and shows an interactive
"communities" graph (projects, namespaces, types, methods). Single ASP.NET Core minimal-API
project; the frontend is plain HTML/JS in `wwwroot` with no build step.

See `README.md` for the user-facing description, API shape and known limitations.

## Run

Open a terminal **inside this folder** (`cd` into it first), then:

```bash
dotnet run -c Release
```

The first run on a machine also builds the app, from the packages in `vendor/nuget`, so no
internet is needed. It is ready when it prints:

```
CodeGraph is running. Open http://localhost:5080
```

Open that address in a browser, enter a folder or `.sln` path and press Analyze. The app keeps
running until it is stopped.

- **Needs the .NET 8 SDK.** `dotnet --list-sdks` must show an 8.0.x line. If it does not, or the
  command stops with "A compatible .NET SDK was not found", see "First-time setup" below.
- **Stop it:** Ctrl+C in its terminal. From another terminal on Windows:
  `Get-Process CodeGraph | Stop-Process`.
- **Other port:** set `CODEGRAPH_PORT` before starting. PowerShell: `$env:CODEGRAPH_PORT = "5090"`.
  bash: `export CODEGRAPH_PORT=5090`.
- **Is it already running?** `http://localhost:5080/api/progress` answers `{"message":""}` when
  it is. Starting a second copy on the same port fails with "Failed to bind to address
  http://127.0.0.1:5080: address already in use"; use the running one or stop it first.
- **Why inside this folder:** `global.json` only applies to `dotnet` commands started here.
  Started from anywhere else (for example `dotnet run --project <path>`), the newest installed
  SDK is used instead and the build fails with
  "NU1101: Unable to find package Microsoft.NETCore.App.Ref".

When asked to run the app from a Claude session:

1. Check `dotnet --list-sdks` for an 8.0.x line, and check whether it is already running.
2. Start `dotnet run -c Release` as a background task. It never exits on its own, so a
   foreground call only ends by timing out.
3. Wait until it answers, run the quick check below, then give the user the address.
4. If C# code changed since it was started, stop the old copy before building
   (see "Applying changes").

## First-time setup on a new machine

1. Check the SDK: `dotnet --list-sdks` must show an 8.0.x SDK (the SDK, not just the runtime;
   Roslyn uses its MSBuild to load projects). If it is missing, tell the user to install the
   .NET 8 SDK from https://dotnet.microsoft.com/download/dotnet/8.0 rather than working around it.
   A newer SDK alone (e.g. only .NET 10) is not enough. `global.json` pins this folder to the
   8.0 SDK, so every `dotnet` command here then stops with "A compatible .NET SDK was not found
   … Requested SDK version: 8.0.100". Any 8.0.x SDK satisfies it; it can be installed next to
   newer ones. Do not loosen `global.json` to get past this (see "Portable and offline").
2. From this folder: `dotnet build` (restores from `vendor/nuget`, no internet needed; expect
   0 warnings, 0 errors).
3. Run it as described under "Run" above.
4. In the UI, enter a folder or `.sln` path and press Analyze. For best results the target
   solution should already be restored (`dotnet restore`, run by the user in that solution);
   CodeGraph never restores or builds it.

Visual Studio Build Tools are only needed on Windows for old-style .NET Framework `.csproj`
files. Without them those projects fall back to syntax-only parsing and show up under Warnings.

## Quick check without the browser

With the app running:

```bash
curl -s -X POST http://localhost:5080/api/analyze -H "Content-Type: application/json" -d '{"path":"."}'
```

Analyzing this folder itself should return about 100 nodes, 1 project and one warning
("No .sln file found…"). There is no automated test project.

In PowerShell use `Invoke-RestMethod -Method Post -Uri http://localhost:5080/api/analyze
-ContentType 'application/json' -Body '{"path":"."}'` instead of `curl`.

## Applying changes

- `wwwroot/` files are served from disk: edit, then reload the browser. No restart.
- C# changes need a restart. On Windows the running `CodeGraph.exe` locks the build output, so
  stop it first (`Get-Process CodeGraph | Stop-Process`), then `dotnet build -c Release` and run.
- To check the UI without a person at the browser, drive headless Edge/Chrome over the DevTools
  protocol (fill `#path`, click `#analyze`, wait for `#status` text, screenshot). Do not add a
  "path in the URL" auto-analyze shortcut for this: loading a project evaluates its MSBuild
  files, so a link must never be able to start an analysis.

## Portable and offline (the folder is the whole install)

The folder is meant to be zipped, moved to another device and run there with nothing but the
.NET 8 SDK installed, and with no internet access at build time or run time. Four things make
that work; keep them in step with each other.

- `vendor/nuget/` – every NuGet package the restore needs (45 `.nupkg` files, about 61 MB:
  the three direct references plus their dependencies), as a flat local feed.
- `nuget.config` – clears nuget.org, machine-wide sources and fallback folders, so a restore can
  only use `vendor/nuget`. A package missing from that folder fails the build (NU1101) instead
  of being downloaded quietly.
- `global.json` – pins the SDK to 8.0.x. With the 8.0 SDK the targeting and apphost packs come
  from the SDK itself. A newer SDK building `net8.0` has to download those packs, which the
  offline feed cannot supply.
- `wwwroot/lib/d3.min.js` – D3 7.9.0, checked against the SHA-512 that cdnjs publishes; its
  licence is beside it. The page loads nothing from outside localhost.

After changing a package version in `CodeGraph.csproj`, refresh the feed (PowerShell, needs
internet once), then check that `dotnet build -c Release` still gives 0 warnings:

```powershell
$tmp = "$env:TEMP\codegraph-packages"
Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
dotnet restore --packages $tmp --source https://api.nuget.org/v3/index.json --force
Remove-Item vendor\nuget\*.nupkg
Get-ChildItem "$tmp\*\*\*.nuspec" | ForEach-Object {
  $id = ([xml](Get-Content $_.FullName -Raw)).package.metadata.id
  Copy-Item "$($_.DirectoryName)\*.nupkg" "vendor\nuget\$id.$($_.Directory.Name).nupkg"
}
```

Why the feed looks the way it does:

- Flat, not the nested `id/version/` layout: nested paths reached 145 characters and broke zip
  extraction in deep folders. Flat, the longest is 78.
- File names use the id exactly as the package publishes it (`Microsoft.Build.Locator.1.7.8.nupkg`),
  matching how the project and the packages refer to each other, so lookups also work where
  file names are case-sensitive.
- The fresh `--packages` folder matters: it is how you get the complete set, not just what
  this machine's cache happens to lack.

Making the zip: leave out `bin/` and `obj/` (build output with machine-specific paths, recreated
by the first build), and `.git/` plus the previous `CodeGraph.zip` when the folder is a git
checkout. From the parent folder on Windows (use the checkout's own folder name instead of
`CodeGraph`; a clone is called `Graph`):
`tar -a -c -f CodeGraph.zip --exclude "CodeGraph/bin" --exclude "CodeGraph/obj" --exclude "CodeGraph/.git" --exclude "CodeGraph/CodeGraph.zip" CodeGraph`

Windows path length (long paths are off by default): keep the project folder's own path under
about 150 characters. The longest build-output path is 107 characters below the project folder
(`bin\Release\net8.0\BuildHost-netcore\…runtimeconfig.json`). Past the 260 limit MSBuild skips
files without an error, Roslyn's BuildHost process then fails to start, and every project
falls back to syntax-only with "MSBuild load failed" under Warnings. The same happens if
the NuGet cache folder is more than about 100 characters deep.

Re-checking the offline setup after a change: extract the zip to a new, short path, point the
`NUGET_PACKAGES` environment variable at an empty folder (also a short path) and run
`dotnet build -c Release` there. Then run it and confirm that the analyze response for `"."` has
`stats.syntaxOnlyProjects` equal to 0. Deeply nested temp folders give false failures for the
path-length reason above; if no short path is available, map the temp folder to a spare drive
letter with `subst` and work through that.

Not in the folder: the .NET 8 SDK itself (a separate install of roughly 200–300 MB per
operating system and CPU type).

## Git repository

- Remote: https://github.com/OptimusAPK/Graph, branch `main`. The repository is called Graph;
  the project and its folder are called CodeGraph.
- `.gitignore` keeps `bin/` and `obj/` out. `.gitattributes` stops line-ending conversion on
  `*.nupkg`, `*.zip` and `wwwroot/lib/d3.min.js`; keep those lines, because the vendored
  packages and the D3 checksum must stay byte-for-byte as downloaded.
- `CodeGraph.zip` at the repository root is a snapshot of the project for transfer. It does not
  update itself, so it goes stale as soon as the source changes. Every new version committed
  adds about 60 MB to the history for good (GitHub already warns above 50 MB per file), so do
  not recommit it with every change: refresh it only when asked, or publish new versions as a
  GitHub Release instead.

## Layout

- `global.json`, `nuget.config`, `vendor/nuget/` – offline build setup, see above.
- `Program.cs` – endpoints (`POST /api/analyze`, `GET /api/progress`), loopback binding,
  Host/Origin check, `AnalysisService` (in-memory cache per path, one analysis at a time).
- `Models/GraphModels.cs` – request and graph JSON types, node-kind and edge-type constants.
- `Analysis/InputResolver.cs` – validates the path; finds the `.sln`, else all `.csproj`.
- `Analysis/FileFilters.cs` – generated-file rules and the test-project heuristic.
- `Analysis/SolutionAnalyzer.cs` – `MsBuildBootstrap` (MSBuildLocator) and the orchestration:
  MSBuildWorkspace load → semantic analysis per project → syntax-only fallback for the rest.
- `Analysis/SemanticProjectAnalyzer.cs` – syntax walker using the semantic model.
- `Analysis/SyntaxProjectAnalyzer.cs` – fallback walker; emits by-name edges resolved later.
- `Analysis/GraphBuilder.cs` – collects nodes/edges, drops edges to unknown (external) targets,
  resolves by-name edges, computes degree, runs Louvain, names/merges communities, shortens ids.
- `Analysis/Louvain.cs` – community detection, no dependencies.
- `wwwroot/app.js` – canvas rendering with d3-force/zoom/drag, filters, search, details pane.
  Sections in order: vocabulary (kinds, shapes, relationship types), state, analysis request,
  graph loading, side panels, filtering, simulation, drawing, pointer input, details, camera, search.
- `wwwroot/index.html` – static layout, including the "How to read this graph" legend.
- `wwwroot/lib/` – the bundled D3 (`d3.min.js`) and its licence.

## How the graph is drawn (keep the legend true)

The legend in `index.html` describes these rules; change them together.

- Shape = node kind: square project, diamond namespace, hollow ring interface, small dot method,
  circle for every other type. Defined once in `KIND_SHAPE` (app.js) and mirrored by the
  `.glyph.*` classes in `styles.css`.
- Colour = community. Each community except "other" gets a shaded outline (`drawRegions`) with
  its name above it. Shading is skipped above 3000 visible nodes (`REGION_LIMIT`); names stay.
- Size = number of currently visible connections.
- Focus: hovering a node previews its neighbourhood, clicking pins it (`state.focus` is
  `selected || hover`). Hovering a row in the Communities list spotlights that community.
- Labels are drawn last, in screen pixels, and never overlap: community names first, then the
  selected/hovered node, then nodes by connection count (at most 300).
- Relationship wording lives in `EDGE_TYPES`: `label` for the filter list, `out` / `in` for the
  details pane ("Calls" / "Called by"), `hint` for the tooltip. The API edge-type ids are unchanged.

## How it fits together (things that are easy to break)

- Node ids during analysis are `assemblyName|docCommentId` (types, methods),
  `project|N:namespace` and `P:project`. Analyzers may add edges to ids that never become
  nodes; `GraphBuilder.Build` drops those, which is how framework/external symbols are excluded.
  Ids are replaced with short `n0, n1…` at the end of `Build`.
- A project is only "handled" once semantic analysis succeeds. Anything in
  `ResolvedInput.ProjectPaths` not handled goes through the syntax-only fallback with a warning.
- `MsBuildBootstrap.Register()` must stay the first thing in `Program.cs`, before any
  MSBuildWorkspace type is touched.
- Kestrel is bound with an explicit `Listen(IPAddress.Loopback, port)`, which deliberately
  overrides `--urls` / `ASPNETCORE_URLS`.
- Community rules: named after the highest-degree node, communities under 5 nodes merge into
  "other" (always the last community). The frontend colours the last community grey only if it
  is named "other". Naming detail (`GraphBuilder.CommunityName`): a method is always shown with
  its type ("Louvain.Detect"); other nodes are qualified with their parent
  ("SyntaxProjectAnalyzer.Walker") only when two communities would share a name.
- Frontend large-graph rule: more than 3000 nodes → the `method` kind starts unchecked.
- `styles.css` has a global `[hidden] { display: none !important; }`; the overlays depend on it.

## Constraints to keep

- Localhost only; no telemetry; no outbound calls from the server or from the page.
- Read-only towards the analyzed code: never write into, restore, build or execute it.
- No frontend build step and no JS dependencies beyond the bundled D3 in `wwwroot/lib`.
- The folder must keep building offline: no CDN links in the page, no extra sources in
  `nuget.config`, and `vendor/nuget` refreshed whenever a package version changes.
- Keep the Roslyn packages on the same version as each other (currently 4.11.0). Newer versions
  mark some MSBuildWorkspace APIs obsolete, so rebuild and re-test after any upgrade.

## Not yet verified

- Old-style .NET Framework projects, and multi-project `.sln` files on Windows.
- Graphs above 3000 nodes (largest test graph was about 100 nodes), including how the
  community outlines and label placement perform at that size.
- The offline setup on Linux and macOS, and on a device with no internet at all (see below for
  what was tested instead).

Verified on Windows 11 with .NET SDK 8.0.428 (November 2026): build, run and self-analysis of
this folder (about 105 nodes, 7 communities). The offline setup was verified by extracting the
zip to a new location and building with an empty NuGet cache and HTTP blocked for the build:
0 warnings, all 45 packages installed from `vendor/nuget`, semantic analysis working, and the
page making no request outside localhost (checked in headless Edge).

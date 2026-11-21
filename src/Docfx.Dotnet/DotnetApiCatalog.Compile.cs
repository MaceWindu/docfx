// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Docfx.Common;
using Docfx.Exceptions;
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.Logging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;

#nullable enable

namespace Docfx.Dotnet;

partial class DotnetApiCatalog
{
    private static async Task<(List<(IAssemblySymbol symbol, Compilation compilation)> assemblies, Dictionary<Compilation, string>? frameworks)> Compile(ExtractMetadataConfig config)
    {
        var files = config.Files?.Select(s => new FileInformation(s))
            .GroupBy(f => f.Type)
            .ToDictionary(s => s.Key, s => s.Distinct().ToList()) ?? [];

        var msbuildProperties = config.MSBuildProperties ?? [];
        msbuildProperties.TryAdd("Configuration", "Release");

        var hasProjectInput = files.ContainsKey(FileType.Solution) || files.ContainsKey(FileType.Project);
        var targetFrameworkOption = config.TargetFramework is not null ? "targetFramework"
            : config.TargetFrameworksFromAxes ? "axes"
            : config.TargetFrameworks is not null ? "targetFrameworks"
            : null;
        if (targetFrameworkOption is not null && !hasProjectInput)
        {
            // so they select nothing here, whatever their values
            Logger.LogInfo($"'{targetFrameworkOption}' applies to project and solution input only, and this metadata item has none.");
        }

        // A framework no project can target would select nothing, or, as a platform version that is not one, anything.
        List<(string Option, string? Framework, string? Reason)> invalid = !hasProjectInput ? [] : new List<(string Option, string? Framework)> { ("targetFramework", config.TargetFramework), ("defaultFramework", config.DefaultFramework) }
            .Concat((config.TargetFrameworks ?? []).Select(framework => (Option: "targetFrameworks", Framework: (string?)framework)))
            .Select(option => (option.Option, option.Framework, Reason: option.Framework is { } framework ? TargetFrameworkHelper.GetInvalidReason(framework) : null))
            .Where(option => option.Reason is not null)
            .ToList();
        foreach (var (option, framework, reason) in invalid)
        {
            // `targetFrameworks` takes a list, but of one framework per item.
            var why = option == "targetFrameworks" && framework!.IndexOfAny([';', ',']) >= 0
                ? "several frameworks in one item: list each framework as an item of its own, as in [\"net8.0\", \"netstandard2.0\"]"
                : reason;
            Logger.LogError($"'{option}' holds '{framework}', {why}.", code: ErrorCodes.Metadata.InvalidTargetFramework);
        }

        if (invalid.Count > 0)
        {
            return ([], null);
        }

        // `TargetFrameworks` retargets every referenced project as well, to the frameworks it lists.
        if (targetFrameworkOption is not null && hasProjectInput &&
            msbuildProperties.Keys.FirstOrDefault(key => key.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase) || key.Equals("TargetFrameworks", StringComparison.OrdinalIgnoreCase)) is { } propertyName)
        {
            var onlyOne = conflicting.Any(key => key.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase));
            var names = string.Join(" and ", conflicting.Select(key => $"'{key}'"));
            Logger.LogWarning(
                $"Both '{targetFrameworkOption}' and the MSBuild property '{propertyName}' are set. The property is passed to MSBuild as a global property, "
                + $"so it also applies to every referenced project{onlyOne}. Remove it from 'properties'.",
                code: WarningCodes.Metadata.ConflictingTargetFramework);
        }

        // Projects of a solution that do not target `targetFramework` are only reported as information, as
        // solutions commonly hold analyzers, tools and tests that target other frameworks; but if no project
        // of a solution does, the option most likely has a typo, which is worth a warning.
        var solutionProjectCount = 0;
        var solutionProjectMatches = 0;
        // A solution whose projects an earlier solution already reported has nothing new to warn about.
        var solutionProjectsReported = 0;

        // A project `src` lists is reported once, as a warning, also when a listed solution holds it. The solution
        // then counts it only if it matches: one that does not is the project's warning, not the solution's.
        var listedProjects = files.TryGetValue(FileType.Project, out var listed)
            ? listed.Select(f => f.NormalizedPath).ToHashSet(FilePathComparer.OSPlatformSensitiveComparer)
            : [];
        var reportedProjects = new HashSet<string>(FilePathComparer.OSPlatformSensitiveComparer);

        if (config.TargetFramework is not null && config.TargetFrameworks is not null)
        {
            Logger.LogWarning(
                "Both 'targetFramework' and 'targetFrameworks' are set. They are mutually exclusive, 'targetFrameworks' is ignored.",
                code: WarningCodes.Metadata.ConflictingTargetFramework);
        }

        var targetFrameworks = config.TargetFramework is null ? config.TargetFrameworks : null;
        // With an axis and `targetFramework`, each project is documented at that framework, or its default one,
        // and the values of the axis are merged.
        var frameworks = targetFrameworks is null && config.Axis is null ? null : new Dictionary<Compilation, string>();
        var projectPaths = new Dictionary<Compilation, string>();

        // TODO: Add BinaryLogger support and print log warnings/errors to console. (It requires Roslyn v5.0.0)
        // NOTE:
        // logger parameter is not works when using Roslyn 4.9.0 or later.
        // It'll be fixed in later releases.
        // - https://github.com/dotnet/roslyn/discussions/71950
        // - https://github.com/dotnet/roslyn/issues/72202
        var msbuildLogger = new ConsoleLogger(Logger.LogLevelThreshold switch
        {
            LogLevel.Verbose => LoggerVerbosity.Normal,
            LogLevel.Diagnostic => LoggerVerbosity.Diagnostic,
            _ => LoggerVerbosity.Quiet,
        });

        using var workspace = MSBuildWorkspace.Create(msbuildProperties);
        var projectFrameworks = config.TargetFramework is null && frameworks is null ? null : new ProjectFrameworks(msbuildProperties);
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            Logger.LogWarning($"{e.Diagnostic}");
        });

        if (files.TryGetValue(FileType.NotSupported, out var unsupportedFiles))
        {
            foreach (var file in unsupportedFiles)
            {
                Logger.LogWarning($"Skip unsupported file {file.NormalizedPath}");
            }
        }

        var hasCompilationError = false;
        var projectCompilations = new HashSet<Compilation>();
        var assemblies = new List<(IAssemblySymbol, Compilation)>();

        if (files.TryGetValue(FileType.Solution, out var solutionFiles))
        {
            foreach (var solution in solutionFiles.Select(s => s.NormalizedPath))
            {
                Logger.LogInfo($"Loading solution {solution}");
                solutionProjectCount = 0;
                solutionProjectMatches = 0;
                solutionProjectsReported = 0;
                var filtered = GetSolutionFilterProjects(solution);
                foreach (var project in SolutionFile.Parse(solution).ProjectsInOrder)
                {
                    if (project.ProjectType is SolutionProjectType.KnownToBeMSBuildFormat &&
                        (filtered is null || filtered.Contains(Path.GetFullPath(project.AbsolutePath))))
                    {
                        projectCompilations.UnionWith(await LoadCompilationsFromProject(project.AbsolutePath, fromSolution: true));
                    }
                }

                if (config.TargetFramework is { } solutionTargetFramework && solutionProjectCount > 0 && solutionProjectMatches == 0 && solutionProjectsReported > 0)
                {
                    Logger.LogWarning(
                        $"No project of solution '{solution}' is known to target '{solutionTargetFramework}', each is documented at its default target framework instead.",
                        code: WarningCodes.Metadata.TargetFrameworkNotFound);
                }

                if (targetFrameworks is not null && !targetFrameworks.Contains(TargetFrameworkHelper.AllTargetFrameworks) &&
                    solutionProjectCount > 0 && solutionProjectMatches == 0 && solutionProjectsReported > 0)
                {
                    Logger.LogWarning(
                        $"No project of solution '{solution}' is known to target any of {string.Join(", ", targetFrameworks.Select(tfm => $"'{tfm}'"))}, so none of it is documented.",
                        code: WarningCodes.Metadata.TargetFrameworkNotFound);
                }
            }
        }

        if (files.TryGetValue(FileType.Project, out var projectFiles))
        {
            foreach (var projectFile in projectFiles)
            {
                projectCompilations.UnionWith(await LoadCompilationsFromProject(projectFile.NormalizedPath));
            }
        }

        foreach (var compilation in projectCompilations)
        {
            hasCompilationError |= compilation.CheckDiagnostics(config.AllowCompilationErrors);
            assemblies.Add((compilation.Assembly, compilation));
        }

        var references = config.References ?? [];
        var metadataReferences = references.Select(CompilationHelper.CreateMetadataReference).ToArray();

        // LoadCompilationFrom C# source files
        if (files.TryGetValue(FileType.CSSourceCode, out var csFiles))
        {
            var compilation = CompilationHelper.CreateCompilationFromCSharpFiles(csFiles.Select(f => f.NormalizedPath), msbuildProperties, metadataReferences);
            hasCompilationError |= compilation.CheckDiagnostics(config.AllowCompilationErrors);
            assemblies.Add((compilation.Assembly, compilation));
        }

        // LoadCompilationFrom VB source files
        if (files.TryGetValue(FileType.VBSourceCode, out var vbFiles))
        {
            var compilation = CompilationHelper.CreateCompilationFromVBFiles(vbFiles.Select(f => f.NormalizedPath), msbuildProperties, metadataReferences);
            hasCompilationError |= compilation.CheckDiagnostics(config.AllowCompilationErrors);
            assemblies.Add((compilation.Assembly, compilation));
        }

        // Load Compilation from assembly files
        if (files.TryGetValue(FileType.Assembly, out var assemblyFiles))
        {
            foreach (var assemblyFile in assemblyFiles)
            {
                var normalizedAssemblyPath = assemblyFile.NormalizedPath;

                using var peReader = new PEReader(new FileStream(normalizedAssemblyPath, FileMode.Open, FileAccess.Read));
                if (!peReader.HasMetadata)
                {
                    Logger.LogInfo($"Skip non-managed assembly {normalizedAssemblyPath}");
                    continue;
                }

                Logger.LogInfo($"Loading assembly {normalizedAssemblyPath}");
                var (compilation, assembly) = CompilationHelper.CreateCompilationFromAssembly(normalizedAssemblyPath, peReader, config.IncludePrivateMembers, metadataReferences);
                hasCompilationError |= compilation.CheckDiagnostics(config.AllowCompilationErrors);
                assemblies.Add((assembly, compilation));
            }
        }

        // A project that names its framework without the platform version, `net8.0-windows`, targets the framework
        // another project names with the version, `net8.0-windows7.0`, if MSBuild evaluates it to that version.
        if (frameworks is { Count: > 1 })
        {
            var named = frameworks.Values.Select(unit => TargetFrameworkAxis.SplitUnit(unit).Framework).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var (compilation, unit) in frameworks.ToList())
            {
                var (value, framework) = TargetFrameworkAxis.SplitUnit(unit);

                // and a platform version written two ways, `net8.0-windows7` and `net8.0-windows7.0`, is one framework
                if (TargetFrameworkHelper.AsNamedFirst(framework, named) is var first && first != framework)
                {
                    frameworks[compilation] = value is null ? first : TargetFrameworkAxis.GetUnit(value, first);
                    continue;
                }

                if (projectPaths.TryGetValue(compilation, out var projectPath)
                    && await TargetFrameworkHelper.WithPlatformVersionOf(framework, projectPath, named, projectFrameworks!) is var versioned && versioned != framework)
                {
                    frameworks[compilation] = value is null ? versioned : TargetFrameworkAxis.GetUnit(value, versioned);
                }
            }
        }

        if (hasCompilationError)
        {
            return ([], null);
        }

        if (assemblies.Count <= 0)
        {
            Logger.LogWarning("No .NET API project detected.");
        }

        return (assemblies, frameworks);

        async Task<List<Compilation>> LoadCompilationsFromProject(string path, bool fromSolution = false)
        {
            var project = workspace.CurrentSolution.Projects.FirstOrDefault(
                p => FilePathComparer.OSPlatformSensitiveRelativePathComparer.Equals(p.FilePath, path));

            var isNewlyLoaded = project is null;
            if (project is null)
            {
                Logger.LogInfo($"Loading project {path}");
                if (!config.NoRestore)
                {
                    using var process = Process.Start("dotnet", $"restore \"{path}\"");
                    await process.WaitForExitAsync();
                    if (process.ExitCode != 0)
                    {
                        throw new DocfxException($"dotnet restore failed for '{path}' with exit code {process.ExitCode}.");
                    }
                }
                project = await workspace.OpenProjectAsync(path, msbuildLogger, projectFrameworks);
            }

            // Every framework of a file is loaded in its language, so the check holds for the one selected too,
            // and a project that is never documented is not reported or counted for `targetFramework`.
            if (!project.SupportsCompilation)
            {
                if (isNewlyLoaded)
                {
                    ReportAnalyzers(project);
                }

                Logger.LogInfo($"Skip unsupported project {project.FilePath}.");
                return [];
            }

            // Every framework of a project comes from the same file, so it matches the same value of the axis. Matched
            // before a framework is selected: a project off the axis is not documented, so whether it targets the
            // framework is neither reported nor counted toward its solution.
            string? axisValue = null;
            if (config.Axis is { } axis && (axisValue = axis.Match(project.FilePath!)) is null)
            {
                var message = $"Project '{project.FilePath}' matches none of the values of axis '{axis.Name}', so it is not documented.";
                // as with the frameworks, a solution commonly holds analyzers, tools and tests that are not on the axis
                if (fromSolution)
                {
                    Logger.LogInfo(message);
                }
                else
                {
                    Logger.LogError(message, code: ErrorCodes.Metadata.InvalidAxis);
                }
                return [];
            }

            // Selected after loading rather than passed to MSBuild as a global property: a global
            // `TargetFramework` would also retarget every project this one references.
            if (config.TargetFramework is { } targetFramework)
            {
                // A project `src` lists is reported by itself: one cause gives one warning. So it counts toward the
                // solution only when it matches, which still makes the solution one that targets the framework.
                var listedToo = fromSolution && listedProjects.Contains(path);
                var report = !listedToo && reportedProjects.Add(path);
                (project, var matched) = await TargetFrameworkHelper.SelectProject(
                    workspace.CurrentSolution, project, targetFramework, projectFrameworks!, fromSolution, report);
                if (fromSolution && (!listedToo || matched))
                {
                    solutionProjectCount++;
                    solutionProjectMatches += matched ? 1 : 0;
                    solutionProjectsReported += report ? 1 : 0;
                }
            }

            if (frameworks is null)
            {
                // the analyzers of the framework documented
                if (isNewlyLoaded)
                {
                    ReportAnalyzers(project);
                }

                return await project.GetCompilationAsync() is { } single ? [single] : [];
            }

            // With `targetFramework`, the project selected above, which is at its default framework if it does
            // not target that one. A project `src` lists is reported by itself, also when a listed solution holds it.
            var reportFailures = !(fromSolution && listedProjects.Contains(path));
            List<(Project Project, string? Framework)> selection;
            if (targetFrameworks is null)
            {
                var failures = new List<string>();
                selection = [(project, await TargetFrameworkHelper.GetFrameworkLabel(project, projectFrameworks!, failures))];
                if (reportFailures)
                {
                    failures.ForEach(failure => projectFrameworks!.ReportFailure(failure, fromSolution));
                }
            }
            else
            {
                selection = await TargetFrameworkHelper.SelectProjects(workspace.CurrentSolution, project, targetFrameworks, projectFrameworks!, fromSolution, reportFailures);
            }

            // As with `targetFramework`, a solution none of whose projects targets a framework of the list is worth a
            // warning; a project `src` lists too is reported by itself, so it counts only when it matches.
            if (fromSolution && targetFrameworks is not null && (!listedProjects.Contains(path) || selection.Count > 0))
            {
                solutionProjectCount++;
                solutionProjectMatches += selection.Any(s => s.Framework is not null) ? 1 : 0;
                solutionProjectsReported += !listedProjects.Contains(path) && reportedProjects.Add(path) ? 1 : 0;
            }

            // An analyzer every framework references is one cause, reported once.
            var reportedAnalyzers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var compilations = new List<Compilation>();
            foreach (var (selected, selectedFramework) in selection)
            {
                // Which framework to say it applies to is not known, which MSBuild has reported.
                if (selectedFramework is null)
                {
                    continue;
                }

                if (isNewlyLoaded)
                {
                    ReportAnalyzersOnce(selected, reportedAnalyzers);
                }

                if (await selected.GetCompilationAsync() is not { } compilation)
                {
                    continue;
                }

                // One spelling per framework, whichever project names it how: pages compare them as they are.
                var framework = selectedFramework.ToLowerInvariant();
                if (axisValue is not null)
                {
                    framework = TargetFrameworkAxis.GetUnit(axisValue, framework);
                }

                frameworks![compilation] = framework;
                projectPaths[compilation] = selected.FilePath!;
                compilations.Add(compilation);
            }

            return compilations;
        }

        // Before the project is compiled: source generators run in the compilation, and a load failure is
        // reported only to the handlers attached by then.
        static void ReportAnalyzers(Project project) => ReportAnalyzersOnce(project, null);

        // `reported` holds what the other frameworks of the same file reported already.
        static void ReportAnalyzersOnce(Project project, HashSet<string>? reported)
        {
            bool IsNew(string key)
            {
                if (reported is null)
                {
                    return true;
                }

                // A load failure is reported while a framework is compiled.
                lock (reported)
                {
                    return reported.Add(key);
                }
            }

            foreach (var unresolvedAnalyzer in project.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().Where(a => IsNew($"resolve {a.FullPath}")))
            {
                Logger.LogWarning($"There is .NET Analyzer that can't be resolved. "
                                + $"If this analyzer is .NET Source Generator project. "
                                + $"Try build with `dotnet build -c Release` command before running docfx. Path: {unresolvedAnalyzer.FullPath}",
                                code: WarningCodes.Metadata.FailedToResolveAnalyzer);
            }

            foreach (var analyzer in project.AnalyzerReferences.OfType<AnalyzerFileReference>())
            {
                analyzer.AnalyzerLoadFailed += (sender, e) =>
                {
                    if (!IsNew($"load {analyzer.FullPath} {e.ErrorCode} {e.TypeName}"))
                    {
                        return;
                    }

                    var analyzerName = analyzer.Display;
                    var errorCode = e.ErrorCode;
                    var referencedCompilerVersion = e.ReferencedCompilerVersion;

                    Logger.LogWarning($"Failed to load .NET Analyzer. AnalyzerName: {analyzerName}, ErrorCode: {errorCode}, ReferencedCompilerVersion: {referencedCompilerVersion}",
                                      code: WarningCodes.Metadata.FailedToLoadAnalyzer);
                };
            }
        }
    }

    /// <summary>
    /// The projects a solution filter (<c>.slnf</c>) builds, or null for a solution. <c>SolutionFile.Parse</c> reads a
    /// filter, but lists every project of its solution, and the filter's own check is not public. As MSBuild does, the
    /// filter's paths are relative to the solution.
    /// </summary>
    private static HashSet<string>? GetSolutionFilterProjects(string path)
    {
        if (!path.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        using var filter = JsonDocument.Parse(
            File.ReadAllText(path),
            new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var solution = filter.RootElement.GetProperty("solution");
        var solutionPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, FixSeparators(solution.GetProperty("path").GetString()!));
        var solutionFolder = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        return solution.GetProperty("projects").EnumerateArray()
            .Select(project => Path.GetFullPath(Path.Combine(solutionFolder, FixSeparators(project.GetString()!))))
            .ToHashSet(FilePathComparer.OSPlatformSensitiveComparer);

        static string FixSeparators(string path) => path.Replace('\\', Path.DirectorySeparatorChar);
    }
}

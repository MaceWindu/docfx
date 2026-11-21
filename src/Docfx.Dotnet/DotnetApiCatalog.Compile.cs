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
    private static async Task<List<(IAssemblySymbol symbol, Compilation compilation)>> Compile(ExtractMetadataConfig config)
    {
        var files = config.Files?.Select(s => new FileInformation(s))
            .GroupBy(f => f.Type)
            .ToDictionary(s => s.Key, s => s.Distinct().ToList()) ?? [];

        var msbuildProperties = config.MSBuildProperties ?? [];
        msbuildProperties.TryAdd("Configuration", "Release");

        var hasProjectInput = files.ContainsKey(FileType.Solution) || files.ContainsKey(FileType.Project);
        if (config.TargetFramework is not null && !hasProjectInput)
        {
            // so it selects nothing here, whatever its value
            Logger.LogInfo("'targetFramework' applies to project and solution input only, and this metadata item has none.");
        }
        else if (config.TargetFramework is { } invalid && TargetFrameworkHelper.GetInvalidReason(invalid) is { } reason)
        {
            // A framework no project can target would select nothing, or, as a platform version that is not one, anything.
            Logger.LogError(
                $"'targetFramework' is '{invalid}', {reason}.",
                code: ErrorCodes.Metadata.InvalidTargetFramework);
            return [];
        }

        // `TargetFrameworks` retargets every referenced project as well, to the frameworks it lists.
        if (config.TargetFramework is not null && hasProjectInput &&
            msbuildProperties.Keys.FirstOrDefault(key => key.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase) || key.Equals("TargetFrameworks", StringComparison.OrdinalIgnoreCase)) is { } propertyName)
        {
            var onlyOne = propertyName.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase) ? " and leaves only one target framework to select from" : "";
            Logger.LogWarning(
                $"Both 'targetFramework' and the MSBuild property '{propertyName}' are set. The property is passed to MSBuild as a global property, "
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
        var projectFrameworks = config.TargetFramework is null ? null : new ProjectFrameworks(msbuildProperties);
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
                        (filtered is null || filtered.Contains(Path.GetFullPath(project.AbsolutePath))) &&
                        await LoadCompilationFromProject(project.AbsolutePath, fromSolution: true) is { } compilation)
                    {
                        projectCompilations.Add(compilation);
                    }
                }

                if (config.TargetFramework is { } solutionTargetFramework && solutionProjectCount > 0 && solutionProjectMatches == 0 && solutionProjectsReported > 0)
                {
                    Logger.LogWarning(
                        $"No project of solution '{solution}' is known to target '{solutionTargetFramework}', each is documented at its default target framework instead.",
                        code: WarningCodes.Metadata.TargetFrameworkNotFound);
                }
            }
        }

        if (files.TryGetValue(FileType.Project, out var projectFiles))
        {
            foreach (var projectFile in projectFiles)
            {
                if (await LoadCompilationFromProject(projectFile.NormalizedPath) is { } compilation)
                {
                    projectCompilations.Add(compilation);
                }
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

        if (hasCompilationError)
        {
            return [];
        }

        if (assemblies.Count <= 0)
        {
            Logger.LogWarning("No .NET API project detected.");
        }

        return assemblies;

        async Task<Compilation?> LoadCompilationFromProject(string path, bool fromSolution = false)
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
                return null;
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

            // the analyzers of the framework documented
            if (isNewlyLoaded)
            {
                ReportAnalyzers(project);
            }

            return await project.GetCompilationAsync();
        }

        // Before the project is compiled: source generators run in the compilation, and a load failure is
        // reported only to the handlers attached by then.
        static void ReportAnalyzers(Project project)
        {
            foreach (var unresolvedAnalyzer in project.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>())
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

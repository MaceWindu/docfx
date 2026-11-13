// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection.PortableExecutable;
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
        var targetFrameworkOption = config.TargetFramework is not null ? "targetFramework" : config.TargetFrameworks is not null ? "targetFrameworks" : null;
        if (targetFrameworkOption is not null && !hasProjectInput)
        {
            Logger.LogInfo($"'{targetFrameworkOption}' applies to project and solution input only, and this metadata item has none.");
        }

        if (targetFrameworkOption is not null && hasProjectInput &&
            msbuildProperties.Keys.FirstOrDefault(key => key.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase)) is { } propertyName)
        {
            Logger.LogWarning(
                $"Both '{targetFrameworkOption}' and the MSBuild property '{propertyName}' are set. The property is passed to MSBuild as a global property, "
                + "so it also applies to every referenced project and leaves only one target framework to select from. Remove it from 'properties'.",
                code: WarningCodes.Metadata.ConflictingTargetFramework);
        }

        // Projects of a solution that do not target `targetFramework` are only reported as information, as
        // solutions commonly hold analyzers, tools and tests that target other frameworks; but if none of
        // them does, the option most likely has a typo, which is worth a warning.
        var solutionProjectCount = 0;
        var solutionProjectMatches = 0;

        if (config.TargetFramework is not null && config.TargetFrameworks is not null)
        {
            Logger.LogWarning(
                "Both 'targetFramework' and 'targetFrameworks' are set. They are mutually exclusive, 'targetFrameworks' is ignored.",
                code: WarningCodes.Metadata.ConflictingTargetFramework);
        }

        var targetFrameworks = config.TargetFramework is null ? config.TargetFrameworks : null;
        var frameworks = targetFrameworks is null ? null : new Dictionary<Compilation, string>();

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
                foreach (var project in SolutionFile.Parse(solution).ProjectsInOrder)
                {
                    if (project.ProjectType is SolutionProjectType.KnownToBeMSBuildFormat)
                    {
                        projectCompilations.UnionWith(await LoadCompilationsFromProject(project.AbsolutePath, fromSolution: true));
                    }
                }
            }

            if (config.TargetFramework is { } solutionTargetFramework && solutionProjectCount > 0 && solutionProjectMatches == 0)
            {
                Logger.LogWarning(
                    $"No project of the solutions targets '{solutionTargetFramework}', each is documented at its default target framework instead.",
                    code: WarningCodes.Metadata.TargetFrameworkNotFound);
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
                project = await workspace.OpenProjectAsync(path, msbuildLogger);
            }

            // Selected after loading rather than passed to MSBuild as a global property: a global
            // `TargetFramework` would also retarget every project this one references.
            if (config.TargetFramework is { } targetFramework)
            {
                (project, var matched) = TargetFrameworkHelper.SelectProject(workspace.CurrentSolution, project, targetFramework, fromSolution);
                if (fromSolution)
                {
                    solutionProjectCount++;
                    solutionProjectMatches += matched ? 1 : 0;
                }
            }

            if (isNewlyLoaded)
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

            if (!project.SupportsCompilation)
            {
                Logger.LogInfo($"Skip unsupported project {project.FilePath}.");
                return [];
            }

            if (targetFrameworks is null)
            {
                return await project.GetCompilationAsync() is { } single ? [single] : [];
            }

            var compilations = new List<Compilation>();
            foreach (var (selected, selectedFramework) in TargetFrameworkHelper.SelectProjects(workspace.CurrentSolution, project, targetFrameworks, fromSolution))
            {
                if (await selected.GetCompilationAsync() is not { } compilation)
                {
                    continue;
                }

                if ((selectedFramework ?? TargetFrameworkHelper.GetTargetFrameworkFromAttribute(compilation.Assembly)) is not { } framework)
                {
                    Logger.LogWarning(
                        $"The target framework of project '{selected.FilePath}' cannot be determined, so it is not documented.",
                        code: WarningCodes.Metadata.TargetFrameworkNotFound);
                    continue;
                }

                if (config.Axis is { } axis)
                {
                    if (axis.Match(selected.FilePath!) is not { } value)
                    {
                        Logger.LogError(
                            $"Project '{selected.FilePath}' matches none of the values of axis '{axis.Name}', so it is not documented.",
                            code: ErrorCodes.Metadata.InvalidAxis);
                        continue;
                    }

                    framework = TargetFrameworkAxis.GetUnit(value, framework);
                }

                frameworks![compilation] = framework;
                compilations.Add(compilation);
            }

            return compilations;
        }
    }
}

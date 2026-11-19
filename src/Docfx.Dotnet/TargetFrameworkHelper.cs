// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Docfx.Common;
using Microsoft.CodeAnalysis;

#nullable enable

namespace Docfx.Dotnet;

/// <summary>
/// Picks the target framework of a project to document.
/// </summary>
/// <remarks>
/// <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/> loads every target framework of a
/// multi-targeted project as a separate project, and names each one after its framework, e.g.
/// <c>Foo(net8.0)</c>. What MSBuild says beyond that comes from <see cref="ProjectFrameworks"/>.
/// </remarks>
internal static partial class TargetFrameworkHelper
{
    // The symbols the .NET SDK defines for the platform of an OS-specific framework such as net8.0-windows, of the
    // platforms it ships. It defines one for any other platform too, which only the framework asked for tells
    // apart from a project's own symbol.
    private static readonly string[] s_platformSymbols = ["ANDROID", "BROWSER", "IOS", "MACCATALYST", "MACOS", "TIZEN", "TVOS", "WASI", "WINDOWS"];

    // The platforms whose frameworks have no version, `net8.0-browser`: the SDK defines no `_OR_GREATER` symbol for
    // them, so their symbol alone names the platform.
    private static readonly string[] s_versionlessPlatformSymbols = ["BROWSER", "WASI"];

    // The symbols the .NET SDK defines for every framework of a family; a project without any of them
    // (a non-SDK project, or `DisableImplicitFrameworkDefines`) does not say which framework it targets.
    private static readonly string[] s_frameworkFamilySymbols = ["NET", "NETCOREAPP", "NETFRAMEWORK", "NETSTANDARD"];

    internal const string AllTargetFrameworks = "*";

    // The exact-version symbols only: `NET8_0_OR_GREATER`, `NET` and `NETFRAMEWORK` name no single framework.
    // Case-sensitive, as C# symbols are: a project's own `net8_0` is not the SDK's.
    [GeneratedRegex(@"^(NETSTANDARD\d+_\d+|NETCOREAPP\d+_\d+|NET\d+_\d+|NET\d{2,3})$")]
    private static partial Regex TargetFrameworkSymbolRegex();

    [GeneratedRegex(@"^\.(NETCoreApp|NETStandard|NETFramework),Version=v(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkNameRegex();

    // `WINDOWS10_0_19041_0_OR_GREATER`: the SDK defines one for every platform version up to the one targeted.
    [GeneratedRegex(@"^([A-Z]+)(\d+(?:_\d+)*)_OR_GREATER$")]
    private static partial Regex PlatformVersionSymbolRegex();

    [GeneratedRegex(@"^(netstandard|netcoreapp|net)(\d+(?:\.\d+)*)(?:-([a-z]*))?", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkMonikerRegex();

    private enum FrameworkFamily { Standard, Core, Framework }

    // The lowest .NET Framework version implementing each .NET Standard version that it implements at all.
    private static readonly (Version Standard, Version Framework)[] StandardOnFramework =
    [
        (new(1, 1), new(4, 5)),
        (new(1, 2), new(4, 5, 1)),
        (new(1, 3), new(4, 6)),
        (new(2, 0), new(4, 6, 1)),
    ];

    /// <summary>
    /// Orders <paramref name="frameworks"/> by how well each one stands in for <paramref name="target"/>:
    /// the target itself, then the frameworks a project targeting it could reference, nearest first, then
    /// the others in the order given.
    /// </summary>
    /// <remarks>
    /// A simplified form of the NuGet compatibility rules, which covers the frameworks a library targets.
    /// NuGet.Frameworks is not used because MSBuildWorkspace loads the copy that comes with the SDK.
    /// </remarks>
    internal static List<string> OrderByNearest(string target, IEnumerable<string> frameworks)
    {
        var parsedTarget = ParseMoniker(target);
        return frameworks
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((framework, index) => (framework, index, parsed: ParseMoniker(framework)))
            .OrderBy(f => IsSameFramework(f.framework, target) ? 0
                : parsedTarget is not { } t || f.parsed is not { } p || !CanReference(t, p) ? 3
                : p.Family == t.Family ? 1
                : 2)
            .ThenByDescending(f => f.parsed is { } p && parsedTarget is { } t && CanReference(t, p) ? p.Version : null)
            .ThenBy(f => f.index)
            .Select(f => f.framework)
            .ToList();
    }

    /// <summary>
    /// Whether two framework names name the same framework, either way round with the platform version one
    /// leaves out: <c>net8.0-windows</c> is the project loaded as <c>net8.0-windows10.0.19041.0</c>, and
    /// <c>net8.0-windows7.0</c> the one loaded as <c>net8.0-windows</c>.
    /// </summary>
    internal static bool IsSameFramework(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) || MatchesFramework(left, right) || MatchesFramework(right, left);
    }

    private static (FrameworkFamily Family, Version Version, string? Platform)? ParseMoniker(string moniker)
    {
        var match = TargetFrameworkMonikerRegex().Match(moniker);
        if (!match.Success)
        {
            return null;
        }

        var digits = match.Groups[2].Value;
        var platform = match.Groups[3].Success ? match.Groups[3].Value.ToLowerInvariant() : null;
        return match.Groups[1].Value.ToLowerInvariant() switch
        {
            "netstandard" when Version.TryParse(digits, out var version) => (FrameworkFamily.Standard, version, platform),
            "netcoreapp" when Version.TryParse(digits, out var version) => (FrameworkFamily.Core, version, platform),
            "net" when digits.Contains('.') && Version.TryParse(digits, out var version) => (FrameworkFamily.Core, version, platform),
            // `net462` is .NET Framework 4.6.2, one digit per part.
            "net" when !digits.Contains('.') && digits.Length is 2 or 3 => (FrameworkFamily.Framework, Version.Parse(string.Join('.', digits.ToCharArray())), platform),
            _ => null,
        };
    }

    private static bool CanReference((FrameworkFamily Family, Version Version, string? Platform) target, (FrameworkFamily Family, Version Version, string? Platform) framework)
    {
        if (framework.Platform is not null && framework.Platform != target.Platform)
        {
            return false;
        }

        return (target.Family, framework.Family) switch
        {
            _ when target.Family == framework.Family => framework.Version <= target.Version,
            (FrameworkFamily.Core, FrameworkFamily.Standard) => framework.Version <= (target.Version >= new Version(3, 0) ? new Version(2, 1) : target.Version >= new Version(2, 0) ? new Version(2, 0) : new Version(1, 6)),
            (FrameworkFamily.Framework, FrameworkFamily.Standard) => StandardOnFramework.Any(s => framework.Version <= s.Standard && target.Version >= s.Framework),
            _ => false,
        };
    }

    /// <summary>
    /// Returns the projects, among those loaded from the same file as <paramref name="project"/>, for each
    /// of <paramref name="targetFrameworks"/> in that order, with the framework each one targets.
    /// </summary>
    /// <returns>
    /// The projects found, which is none when the file targets none of the frameworks: unlike a single
    /// framework, a list states what to include, so nothing else is documented in place of a missing one.
    /// A framework is <see langword="null"/> when it can only be read from the compilation (see
    /// <see cref="GetFrameworkLabel"/>), or, for a project that says neither in a <c>TargetFrameworkAttribute</c>
    /// nor in its preprocessor symbols which framework it targets, not at all.
    /// </returns>
    /// <param name="beforeCompile">Called with the project before it is first compiled here.</param>
    public static async Task<List<(Project Project, string? Framework)>> SelectProjects(
        Solution solution, Project project, IReadOnlyCollection<string> targetFrameworks, bool fromSolution = false, Action<Project>? beforeCompile = null)
    {
        var candidates = GetProjectsFromSameFile(solution, project);
        var singleTarget = candidates.Count == 1 && GetTargetFrameworkFromName(project) is null;

        if (targetFrameworks.Contains(AllTargetFrameworks))
        {
            // The project the workspace opened goes first, so that the framework a project is documented
            // at by default comes first among its own. Which framework provides the content is decided per
            // entry, by the order the frameworks first appear across its projects, so with several projects
            // that list their frameworks in different orders, `defaultFramework` is what chooses it. A project that
            // targets a single framework is named from its compilation, which the caller builds.
            return candidates
                .OrderBy(p => p.Id == project.Id ? 0 : 1)
                .Select(p => (p, singleTarget ? null : GetFrameworkLabel(p, null)))
                .ToList();
        }

        // A project that targets a single framework is matched by the attributes of its compilation first, as
        // with `targetFramework`. That compilation is the one documented, so it is not built twice.
        IAssemblySymbol? assembly = null;
        var symbols = project.ParseOptions?.PreprocessorSymbolNames ?? [];
        if (singleTarget)
        {
            beforeCompile?.Invoke(project);
            assembly = (await project.GetCompilationAsync())?.Assembly;
            if (GetTargetFramework(assembly, symbols) is null)
            {
                // which framework to say it applies to is not known, so it is left to the caller, which reports it
                return [(project, null)];
            }
        }

        var selected = new List<(Project, string?)>();
        var missing = new List<string>();
        foreach (var targetFramework in targetFrameworks)
        {
            var available = candidates.Where(p => !selected.Any(s => s.Item1.Id == p.Id)).ToList();
            // An exact name wins over one that differs only in the platform version, as with `targetFramework`.
            if ((available.FirstOrDefault(p => string.Equals(GetTargetFrameworkFromName(p), targetFramework, StringComparison.OrdinalIgnoreCase))
                ?? available.FirstOrDefault(p => singleTarget
                    ? GetTargetFramework(assembly, symbols, GetTargetFrameworkPlatform(targetFramework)) is { } framework && MatchesFramework(framework, targetFramework)
                    : Targets(p, targetFramework))) is { } match)
            {
                // named as the list names it, unless the project's name is a framework rather than an alias
                selected.Add((match, GetTargetFrameworkFromName(match) is { } name && IsFrameworkName(name) ? name : targetFramework));
            }
            else
            {
                missing.Add(targetFramework);
            }
        }

        if (missing.Count > 0)
        {
            var message = $"Project '{project.FilePath}' does not target {string.Join(", ", missing.Select(tfm => $"'{tfm}'"))}, so it is not documented for that.";
            // as with `targetFramework`, the projects of a solution commonly target other frameworks
            if (fromSolution)
            {
                Logger.LogInfo(message);
            }
            else
            {
                Logger.LogWarning(message, code: WarningCodes.Metadata.TargetFrameworkNotFound);
            }
        }

        return selected;
    }

    private static List<Project> GetProjectsFromSameFile(Solution solution, Project project)
    {
        return solution.Projects
            .Where(p => FilePathComparer.OSPlatformSensitiveRelativePathComparer.Equals(p.FilePath, project.FilePath))
            .ToList();
    }

    // A project loaded for one of several frameworks.
    private static bool Targets(Project project, string targetFramework)
    {
        return GetFrameworkOfTarget(project, GetTargetFrameworkPlatform(targetFramework)) is { } framework
            && MatchesFramework(framework, targetFramework, GetPlatformVersion(project, framework));
    }

    private static bool IsFrameworkName(string name) => TargetFrameworkSymbolRegex().IsMatch(GetTargetFrameworkSymbol(name));

    /// <summary>
    /// Names the framework of a project as <c>targetFrameworks: "*"</c> lists it: by the name a project loaded for
    /// one of several frameworks has, or the framework an alias of it stands for; for a project that targets a
    /// single framework, by <paramref name="assembly"/>, its compilation, with the spelling of its preprocessor
    /// symbols where both agree, which leave out the platform version the project file most likely leaves out
    /// too: <c>net8.0-windows</c>, not the <c>net8.0-windows7.0</c> the attributes say.
    /// </summary>
    internal static string? GetFrameworkLabel(Project project, IAssemblySymbol? assembly)
    {
        var symbols = project.ParseOptions?.PreprocessorSymbolNames ?? [];
        if (GetTargetFrameworkFromName(project) is { } name)
        {
            return IsFrameworkName(name) ? name : GetTargetFrameworkFromSymbols(symbols) ?? name;
        }

        var spelled = GetTargetFrameworkFromSymbols(symbols);
        var exact = GetTargetFramework(assembly, symbols);
        return spelled is not null && exact is not null && MatchesFramework(exact, spelled) ? spelled : exact ?? spelled;
    }

    /// <summary>
    /// Reads the framework back from the preprocessor symbols the .NET SDK defines for it, with its platform
    /// but not the platform version, which only the version symbols give: <c>net8.0-windows</c> for
    /// <c>net8.0-windows10.0.19041.0</c>. A platform outside the list the SDK ships is not read, as with
    /// <c>targetFramework</c> when it names no platform.
    /// </summary>
    internal static string? GetTargetFrameworkFromSymbols(IEnumerable<string> preprocessorSymbols)
    {
        var symbols = preprocessorSymbols.ToHashSet(StringComparer.Ordinal);
        if (GetTargetFrameworkFromSymbols(symbols) is not { } framework)
        {
            return null;
        }

        return GetPlatformFromSymbols(symbols, GetTargetFrameworkSymbol(framework)).Platform is { } platform
            ? $"{framework}-{platform.ToLowerInvariant()}"
            : framework;
    }

    /// <summary>
    /// Names a framework that leaves out its platform version as one of <paramref name="frameworks"/> that gives
    /// the version the preprocessor symbols of its project have, so that both are one framework: a single-target
    /// project read as <c>net8.0-windows</c> from its symbols is <c>net8.0-windows10.0.19041.0</c> if a
    /// multi-targeted project is named that, and targets the same version.
    /// </summary>
    internal static string WithPlatformVersionOf(string framework, IEnumerable<string> preprocessorSymbols, IEnumerable<string> frameworks)
    {
        if (GetTargetFrameworkPlatform(framework) is null || GetTargetFrameworkPlatformVersion(framework) is not null)
        {
            return framework;
        }

        var symbols = preprocessorSymbols.ToHashSet(StringComparer.Ordinal);
        if (GetPlatformFromSymbols(symbols, GetTargetFrameworkSymbol(framework)).Version is not { } version)
        {
            return framework;
        }

        return frameworks.FirstOrDefault(other => GetTargetFrameworkPlatformVersion(other) == version && MatchesFramework(other, framework)) ?? framework;
    }

    /// <summary>
    /// Returns the project, among those loaded from the same file as <paramref name="project"/>, that
    /// targets <paramref name="targetFramework"/>. Falls back to <paramref name="project"/> when the file
    /// does not target that framework, and reports it, unless <paramref name="report"/> is <see langword="false"/>:
    /// as a warning for a project the entry lists, and as information for one that comes from a solution,
    /// which commonly holds analyzers, tools and test projects that target other frameworks.
    /// </summary>
    /// <returns>The project to document, and whether it is known to target <paramref name="targetFramework"/>.</returns>
    public static async Task<(Project project, bool matched)> SelectProject(
        Solution solution, Project project, string targetFramework, ProjectFrameworks frameworks, bool fromSolution = false, bool report = true)
    {
        var candidates = GetProjectsFromSameFile(solution, project);

        // Why MSBuild cannot say what a framework stands for, reported in the message of the project it leaves
        // unmatched: one project, one warning.
        var failures = new List<string>();

        if (candidates.Count <= 1)
        {
            var (name, failure) = await frameworks.GetName(project);
            if (name is null)
            {
                if (failure is not null)
                {
                    failures.Add(failure);
                }

                Report($"Project '{project.FilePath}' is not known to target '{targetFramework}', documenting the framework it is loaded for.");
                return (project, false);
            }

            if (await Targets(project, name))
            {
                return (project, true);
            }

            Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting '{name}' instead.");
            return (project, false);
        }

        // An exact name wins over one that only writes the platform version differently, or that a
        // framework without a platform version selects among several versions of one platform.
        if (candidates.FirstOrDefault(p => string.Equals(GetTargetFrameworkFromName(p), targetFramework, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return (exact, true);
        }

        foreach (var candidate in candidates)
        {
            if (GetTargetFrameworkFromName(candidate) is { } name && await Targets(candidate, name))
            {
                return (candidate, true);
            }
        }

        var available = string.Join(", ", candidates.Select(GetTargetFrameworkFromName).Where(tfm => tfm is not null).Select(tfm => $"'{tfm}'"));
        Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting '{GetTargetFrameworkFromName(project)}' instead. It targets {available}.");
        return (project, false);

        async Task<bool> Targets(Project candidate, string name)
        {
            if (string.Equals(name, targetFramework, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // An alias does not say which framework it stands for, nor `net8.0-windows` the platform version it
            // does: MSBuild does, when the framework and platform it names are those requested.
            if (IsFrameworkName(name))
            {
                var leavesOutVersion = GetTargetFrameworkPlatform(name) is not null && GetTargetFrameworkPlatformVersion(name) is null
                    && GetTargetFrameworkPlatformVersion(targetFramework) is not null;
                if (!leavesOutVersion || !IsSameFrameworkAndPlatform(name, targetFramework))
                {
                    return MatchesFramework(name, targetFramework);
                }
            }

            // What an alias stands for is a framework, which a value that names none, such as a typo, never matches:
            // only the name itself can, and it does not.
            if (candidate.FilePath is null || !IsFrameworkName(targetFramework))
            {
                return false;
            }

            // A project loaded for its framework alone is evaluated as the workspace loaded it, with the framework
            // as a global property only when its `TargetFrameworks` lists it.
            var evaluation = candidates.Count <= 1
                ? await frameworks.EvaluateAsLoaded(candidate.FilePath, name)
                : await frameworks.Evaluate(candidate.FilePath, name);
            if (evaluation.Failure is { } failure)
            {
                failures.Add(failure);
            }

            return evaluation.Framework is { } framework && MatchesFramework(framework.Moniker, targetFramework);
        }

        void Report(string message)
        {
            if (!report)
            {
                return;
            }

            if (failures.Count > 0)
            {
                message = $"{message} {string.Join(" ", failures.Select(failure => $"{failure.TrimEnd('.')}."))}";
            }

            if (fromSolution)
            {
                Logger.LogInfo(message);
            }
            else
            {
                Logger.LogWarning(message, code: WarningCodes.Metadata.TargetFrameworkNotFound);
            }
        }
    }

    /// <summary>
    /// Says why <paramref name="targetFramework"/> cannot name a single framework: a list such as
    /// <c>net8.0;netstandard2.0</c>, a dash without a platform, as in <c>net8.0-</c>, or a platform version that is
    /// not a version, such as <c>net8.0-windows10.0.19041.O</c>.
    /// </summary>
    /// <returns><see langword="null"/> when it can.</returns>
    internal static string? GetInvalidReason(string targetFramework)
    {
        if (targetFramework.IndexOfAny([';', ',']) >= 0)
        {
            return "a list of frameworks, while it selects a single one";
        }

        // An alias, which may hold a dash and digits as any name may, is selected by itself.
        var dash = targetFramework.IndexOf('-');
        if (dash < 0 || !IsFrameworkName(targetFramework))
        {
            return null;
        }

        var platform = targetFramework[(dash + 1)..];
        var digits = new string(platform.SkipWhile(char.IsLetter).ToArray());
        if (digits.Length == platform.Length)
        {
            return "a framework with a dash but no platform after it: write the platform after the dash, as 'net8.0-windows' does, or leave out the dash";
        }

        return digits.Length == 0 || ParseVersion(digits) is not null
            ? null
            : $"a framework whose platform version '{digits}' is not a version: write it in digits and dots, as 'net8.0-windows10.0.19041.0' does";
    }

    /// <summary>
    /// Reads the framework from the <c>(tfm)</c> suffix Roslyn appends to the name of a project
    /// loaded for one of several target frameworks.
    /// </summary>
    internal static string? GetTargetFrameworkFromName(Project project)
    {
        var name = project.Name;
        if (!name.EndsWith(')'))
        {
            return null;
        }

        var start = name.LastIndexOf('(');
        return start < 0 ? null : name[(start + 1)..^1].Trim();
    }

    // A framework rather than an alias, which `TargetFrameworks` can list too.
    private static bool IsFrameworkName(string name) => TargetFrameworkSymbolRegex().IsMatch(GetTargetFrameworkSymbol(name));

    // .NET 5 and later, the frameworks with platforms.
    internal static bool IsNet5OrLater(string framework)
    {
        return GetTargetFrameworkSymbol(framework) is var symbol && symbol.StartsWith("NET", StringComparison.Ordinal)
            && symbol.IndexOf('_') is > 3 and var underscore && int.TryParse(symbol.AsSpan(3, underscore - 3), out var major) && major >= 5;
    }

    /// <summary>
    /// Whether a project that targets <paramref name="projectFramework"/> targets <paramref name="targetFramework"/>:
    /// the same framework on the same platform, and the same platform version unless <paramref name="targetFramework"/>
    /// leaves it out. <c>net8.0-windows</c> selects <c>net8.0-windows10.0.19041.0</c>, while
    /// <c>net8.0-windows10.0.22621.0</c> does not, and <c>net8.0</c> does not select <c>net8.0-windows</c>, nor the reverse.
    /// </summary>
    internal static bool MatchesFramework(string projectFramework, string targetFramework)
    {
        return IsSameFrameworkAndPlatform(projectFramework, targetFramework)
            && (GetTargetFrameworkPlatformVersion(targetFramework) is not { } version
                || version == GetTargetFrameworkPlatformVersion(projectFramework));
    }

    private static bool IsSameFrameworkAndPlatform(string a, string b)
    {
        return string.Equals(GetTargetFrameworkSymbol(a), GetTargetFrameworkSymbol(b), StringComparison.Ordinal)
            && string.Equals(GetTargetFrameworkPlatform(a), GetTargetFrameworkPlatform(b), StringComparison.Ordinal);
    }

    /// <summary>
    /// Converts a framework name such as <c>.NETCoreApp,Version=v8.0</c> to its moniker, <c>net8.0</c>.
    /// </summary>
    internal static string? ParseFrameworkName(string frameworkName)
    {
        var match = FrameworkNameRegex().Match(frameworkName);
        if (!match.Success)
        {
            return null;
        }

        var (major, minor) = (match.Groups[2].Value, match.Groups[3].Value);
        return match.Groups[1].Value.ToUpperInvariant() switch
        {
            "NETSTANDARD" => $"netstandard{major}.{minor}",
            "NETCOREAPP" => int.Parse(major) >= 5 ? $"net{major}.{minor}" : $"netcoreapp{major}.{minor}",
            _ => $"net{major}{minor}{match.Groups[4].Value}",
        };
    }

    /// <summary>
    /// Gets the platform version of an OS-specific target framework, with the parts it leaves out as zero:
    /// <c>10.0.19041.0</c> for both <c>net8.0-windows10.0.19041.0</c> and <c>net8.0-windows10.0.19041</c>,
    /// <see langword="null"/> for <c>net8.0-windows</c> and <c>net8.0</c>.
    /// </summary>
    internal static Version? GetTargetFrameworkPlatformVersion(string targetFramework)
    {
        var dash = targetFramework.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var digits = new string(targetFramework[(dash + 1)..].SkipWhile(char.IsLetter).ToArray());
        return ParseVersion(digits);
    }

    private static Version? ParseVersion(string digits)
    {
        // Version needs at least two parts, a platform version may give one: `android34`.
        return Version.TryParse(digits.Contains('.') ? digits : $"{digits}.0", out var version)
            ? new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0))
            : null;
    }

    /// <summary>
    /// Gets the preprocessor symbol the .NET SDK defines for a target framework, which is the framework
    /// in upper case with dots replaced by underscores, without its platform: <c>net8.0</c> and
    /// <c>net8.0-windows</c> define <c>NET8_0</c>, <c>netstandard2.0</c> defines <c>NETSTANDARD2_0</c>.
    /// </summary>
    internal static string GetTargetFrameworkSymbol(string targetFramework)
    {
        var dash = targetFramework.IndexOf('-');
        var framework = dash < 0 ? targetFramework : targetFramework[..dash];
        return framework.ToUpperInvariant().Replace('.', '_');
    }

    /// <summary>
    /// Gets the platform of an OS-specific target framework in upper case, without its version:
    /// <c>WINDOWS</c> for <c>net8.0-windows10.0.19041.0</c>, <see langword="null"/> for <c>net8.0</c>.
    /// </summary>
    internal static string? GetTargetFrameworkPlatform(string targetFramework)
    {
        var dash = targetFramework.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var platform = new string(targetFramework[(dash + 1)..].TakeWhile(char.IsLetter).ToArray());
        return platform.Length == 0 ? null : platform.ToUpperInvariant();
    }
}

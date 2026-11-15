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
/// <c>Foo(net8.0)</c>. Roslyn exposes no MSBuild properties, so that name is the only place the
/// framework can be read from. A project that targets a single framework is not given the suffix,
/// and its framework is recognized by the preprocessor symbol the .NET SDK defines for it instead, or else
/// by the <c>TargetFrameworkAttribute</c> of its compilation.
/// </remarks>
internal static partial class TargetFrameworkHelper
{
    // The symbols the .NET SDK defines for the platform of an OS-specific framework such as net8.0-windows.
    private static readonly string[] s_platformSymbols = ["ANDROID", "BROWSER", "IOS", "MACCATALYST", "MACOS", "TIZEN", "TVOS", "WASI", "WINDOWS"];

    // The symbols the .NET SDK defines for every framework of a family; a project without any of them
    // (a non-SDK project, or `DisableImplicitFrameworkDefines`) does not say which framework it targets.
    private static readonly string[] s_frameworkFamilySymbols = ["NET", "NETCOREAPP", "NETFRAMEWORK", "NETSTANDARD"];

    // The exact-version symbols only: `NET8_0_OR_GREATER`, `NET` and `NETFRAMEWORK` name no single framework.
    // Case-sensitive, as C# symbols are: a project's own `net8_0` is not the SDK's.
    [GeneratedRegex(@"^(NETSTANDARD\d+_\d+|NETCOREAPP\d+_\d+|NET\d+_\d+|NET\d{2,3})$")]
    private static partial Regex TargetFrameworkSymbolRegex();

    // `WINDOWS10_0_19041_0_OR_GREATER`: the SDK defines one for every platform version up to the one targeted.
    [GeneratedRegex(@"^([A-Z]+)(\d+(?:_\d+)*)_OR_GREATER$")]
    private static partial Regex PlatformVersionSymbolRegex();

    [GeneratedRegex(@"^\.(NETCoreApp|NETStandard|NETFramework),Version=v(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkNameRegex();

    /// <summary>
    /// Returns the project, among those loaded from the same file as <paramref name="project"/>, that
    /// targets <paramref name="targetFramework"/>. Falls back to <paramref name="project"/> when the file
    /// does not target that framework, and reports it, unless <paramref name="report"/> is <see langword="false"/>:
    /// as a warning for a project the entry lists, and as information for one that comes from a solution,
    /// which commonly holds analyzers, tools and test projects that target other frameworks.
    /// </summary>
    /// <returns>The project to document, and whether it is known to target <paramref name="targetFramework"/>.</returns>
    public static async Task<(Project project, bool matched)> SelectProject(Solution solution, Project project, string targetFramework, bool fromSolution = false, bool report = true)
    {
        var candidates = solution.Projects
            .Where(p => FilePathComparer.OSPlatformSensitiveRelativePathComparer.Equals(p.FilePath, project.FilePath))
            .ToList();

        if (candidates.Count > 1)
        {
            // An exact name wins over one that only writes the platform version differently, or that a
            // framework without a platform version selects among several versions of one platform.
            if (candidates.FirstOrDefault(p => string.Equals(GetTargetFrameworkFromName(p), targetFramework, StringComparison.OrdinalIgnoreCase)) is { } exact)
            {
                return (exact, true);
            }

            if (candidates.FirstOrDefault(p => GetTargetFrameworkFromName(p) is { } tfm && MatchesFramework(tfm, targetFramework, GetPlatformVersion(p, tfm))) is { } match)
            {
                return (match, true);
            }

            var available = string.Join(", ", candidates.Select(GetTargetFrameworkFromName).Where(tfm => tfm is not null).Select(tfm => $"'{tfm}'"));
            Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting '{GetTargetFrameworkFromName(project)}' instead. It targets {available}.");
            return (project, false);
        }

        var targets = TargetsFramework(project.ParseOptions?.PreprocessorSymbolNames ?? [], targetFramework);
        // A project without the symbols, such as one that sets `DisableImplicitFrameworkDefines`, still has
        // the attribute the SDK generates. The compilation is the one documented, so it is not built twice.
        if (targets is null && await project.GetCompilationAsync() is { } compilation &&
            GetTargetFrameworkFromAttribute(compilation.Assembly) is { } attributeFramework)
        {
            targets = TargetFrameworkSymbolRegex().IsMatch(GetTargetFrameworkSymbol(targetFramework)) && MatchesFramework(attributeFramework, targetFramework);
        }

        switch (targets)
        {
            case true:
                return (project, true);
            case false:
                Report($"Project '{project.FilePath}' does not target '{targetFramework}', documenting the target framework it has instead.");
                return (project, false);
            default:
                Report($"Project '{project.FilePath}' says neither in its preprocessor symbols nor in a TargetFrameworkAttribute which framework it targets, so whether it targets '{targetFramework}' is not known. It is documented as it is.");
                return (project, false);
        }

        void Report(string message)
        {
            if (!report)
            {
                return;
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

    /// <summary>
    /// Whether a project that targets <paramref name="projectFramework"/> targets <paramref name="targetFramework"/>:
    /// the same framework on the same platform, and the same platform version unless <paramref name="targetFramework"/>
    /// leaves it out. <c>net8.0-windows</c> selects <c>net8.0-windows10.0.19041.0</c>, while
    /// <c>net8.0-windows10.0.22621.0</c> does not, and <c>net8.0</c> does not select <c>net8.0-windows</c>, nor the reverse.
    /// </summary>
    /// <param name="projectPlatformVersion">
    /// The platform version of a project whose framework leaves it out, as <c>net8.0-windows</c> stands for
    /// <c>net8.0-windows7.0</c>.
    /// </param>
    internal static bool MatchesFramework(string projectFramework, string targetFramework, Version? projectPlatformVersion = null)
    {
        return string.Equals(GetTargetFrameworkSymbol(projectFramework), GetTargetFrameworkSymbol(targetFramework), StringComparison.Ordinal)
            && string.Equals(GetTargetFrameworkPlatform(projectFramework), GetTargetFrameworkPlatform(targetFramework), StringComparison.Ordinal)
            && (GetTargetFrameworkPlatformVersion(targetFramework) is not { } version
                || version == (GetTargetFrameworkPlatformVersion(projectFramework) ?? projectPlatformVersion));
    }

    // The platform version the symbols of a project loaded for `framework` give.
    private static Version? GetPlatformVersion(Project project, string framework)
    {
        var symbols = (project.ParseOptions?.PreprocessorSymbolNames ?? []).ToHashSet(StringComparer.Ordinal);
        return GetPlatformFromSymbols(symbols, GetTargetFrameworkSymbol(framework)).Version;
    }

    /// <summary>
    /// Gets the platform, and its version, of a project from its preprocessor symbols. The SDK defines one such
    /// as <c>WINDOWS</c> for .NET 5 and later only, together with a <c>WINDOWS7_0_OR_GREATER</c> for every version
    /// up to the one targeted, the highest of which is that version: a project's own <c>WINDOWS</c> alone is not it.
    /// </summary>
    private static (string? Platform, Version? Version) GetPlatformFromSymbols(HashSet<string> symbols, string frameworkSymbol)
    {
        var underscore = frameworkSymbol.IndexOf('_');
        if (!frameworkSymbol.StartsWith("NET", StringComparison.Ordinal) || underscore < 0 ||
            !int.TryParse(frameworkSymbol.AsSpan(3, underscore - 3), out var major) || major < 5)
        {
            return (null, null);
        }

        foreach (var platform in s_platformSymbols.Where(symbols.Contains))
        {
            var version = symbols
                .Select(s => PlatformVersionSymbolRegex().Match(s))
                .Where(m => m.Success && m.Groups[1].Value == platform)
                .Select(m => ParseVersion(m.Groups[2].Value.Replace('_', '.')))
                .Max();
            if (version is not null)
            {
                return (platform, version);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// Reads the framework from the <c>TargetFrameworkAttribute</c> of an assembly, and its platform from the
    /// <c>TargetPlatformAttribute</c>, for a project whose preprocessor symbols do not name it.
    /// </summary>
    internal static string? GetTargetFrameworkFromAttribute(IAssemblySymbol assembly)
    {
        string? GetArgument(string attributeName) => assembly.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attributeName)?
            .ConstructorArguments.FirstOrDefault().Value as string;

        if (GetArgument("System.Runtime.Versioning.TargetFrameworkAttribute") is not { } frameworkName || ParseFrameworkName(frameworkName) is not { } framework)
        {
            return null;
        }

        // `Windows10.0.19041.0`
        return GetArgument("System.Runtime.Versioning.TargetPlatformAttribute") is { Length: > 0 } platform
            ? $"{framework}-{platform.ToLowerInvariant()}"
            : framework;
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

    /// <summary>
    /// Tells from the preprocessor symbols of a project that targets a single framework whether that
    /// framework is <paramref name="targetFramework"/>, platform included, the way a multi-targeted project
    /// is matched by name: <c>net8.0</c> does not match a <c>net8.0-windows</c> project, nor the reverse.
    /// </summary>
    /// <returns><see langword="null"/> when the symbols do not say which framework the project targets.</returns>
    internal static bool? TargetsFramework(IEnumerable<string> preprocessorSymbols, string targetFramework)
    {
        // `net` or `netstandard` would otherwise match the family symbol every project of the family defines.
        var frameworkSymbol = GetTargetFrameworkSymbol(targetFramework);
        if (!TargetFrameworkSymbolRegex().IsMatch(frameworkSymbol))
        {
            return false;
        }

        // C# symbols are case-sensitive, and the SDK defines these in upper case: a project's own `Windows`
        // or `Net` symbol is not one of them.
        var symbols = preprocessorSymbols.ToHashSet(StringComparer.Ordinal);
        if (!s_frameworkFamilySymbols.Any(symbols.Contains))
        {
            return null;
        }

        if (!symbols.Contains(frameworkSymbol))
        {
            return false;
        }

        var (projectPlatform, platformVersion) = GetPlatformFromSymbols(symbols, frameworkSymbol);
        if (!string.Equals(projectPlatform, GetTargetFrameworkPlatform(targetFramework), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return GetTargetFrameworkPlatformVersion(targetFramework) is not { } version || version == platformVersion;
    }
}

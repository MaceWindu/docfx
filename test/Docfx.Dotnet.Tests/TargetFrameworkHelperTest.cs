// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Docfx.Dotnet.Tests;

// SelectProject logs through the global logger, which other tests of the collection listen to.
[Collection("docfx STA")]
public class TargetFrameworkHelperTest
{
    [Theory]
    [InlineData("net8.0", "NET8_0")]
    [InlineData("net10.0", "NET10_0")]
    [InlineData("net8.0-windows", "NET8_0")]
    [InlineData("net8.0-windows10.0.19041.0", "NET8_0")]
    [InlineData("netstandard2.0", "NETSTANDARD2_0")]
    [InlineData("netcoreapp3.1", "NETCOREAPP3_1")]
    [InlineData("net462", "NET462")]
    public void GetTargetFrameworkSymbol(string targetFramework, string expected)
    {
        TargetFrameworkHelper.GetTargetFrameworkSymbol(targetFramework).Should().Be(expected);
    }

    [Theory]
    [InlineData("net8.0", null)]
    [InlineData("net8.0-windows", "WINDOWS")]
    [InlineData("net8.0-windows10.0.19041.0", "WINDOWS")]
    [InlineData("net9.0-maccatalyst18.0", "MACCATALYST")]
    [InlineData("netstandard2.0", null)]
    public void GetTargetFrameworkPlatform(string targetFramework, string expected)
    {
        TargetFrameworkHelper.GetTargetFrameworkPlatform(targetFramework).Should().Be(expected);
    }

    [Theory]
    // the symbols the .NET SDK defines for net8.0, net8.0-windows, netstandard2.0 and net462
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER", "net8.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER", "NET8.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER", "net9.0", false)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER", "net8.0-windows", false)]
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS;WINDOWS7_0_OR_GREATER", "net8.0-windows", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS;WINDOWS7_0_OR_GREATER", "net8.0", false)]
    [InlineData("NETSTANDARD;NETSTANDARD2_0", "netstandard2.0", true)]
    [InlineData("NETFRAMEWORK;NET462", "net462", true)]
    [InlineData("NETFRAMEWORK;NET462", "net48", false)]
    // the platform version is compared only when the framework gives one, as for a multi-targeted project
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS;WINDOWS10_0_19041_0_OR_GREATER", "net8.0-windows", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS;WINDOWS7_0_OR_GREATER;WINDOWS10_0_17763_0_OR_GREATER;WINDOWS10_0_19041_0_OR_GREATER", "net8.0-windows10.0.19041.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS;WINDOWS7_0_OR_GREATER;WINDOWS10_0_17763_0_OR_GREATER;WINDOWS10_0_19041_0_OR_GREATER", "net8.0-windows10.0.19041", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS;WINDOWS7_0_OR_GREATER;WINDOWS10_0_17763_0_OR_GREATER;WINDOWS10_0_19041_0_OR_GREATER", "net8.0-windows10.0.17763.0", false)]
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS;WINDOWS7_0_OR_GREATER;WINDOWS10_0_17763_0_OR_GREATER", "net8.0-windows10.0.22621.0", false)]
    // a Windows SDK version ending in `.1` (CsWinRT 3.0) has the symbols of the one ending in `.0`, and `CSWINRT3_0`
    [InlineData("NET;NETCOREAPP;NET10_0;WINDOWS;WINDOWS10_0_26100_0_OR_GREATER;CSWINRT3_0", "net10.0-windows10.0.26100.1", true)]
    [InlineData("NET;NETCOREAPP;NET10_0;WINDOWS;WINDOWS10_0_26100_0_OR_GREATER;CSWINRT3_0", "net10.0-windows10.0.26100.0", false)]
    [InlineData("NET;NETCOREAPP;NET10_0;WINDOWS;WINDOWS10_0_26100_0_OR_GREATER;CSWINRT3_0", "net10.0-windows", true)]
    [InlineData("NET;NETCOREAPP;NET10_0;WINDOWS;WINDOWS10_0_26100_0_OR_GREATER", "net10.0-windows10.0.26100.1", false)]
    [InlineData("NET;NETCOREAPP;NET10_0;WINDOWS;WINDOWS10_0_26100_0_OR_GREATER", "net10.0-windows10.0.26100.0", true)]
    // a platform whose frameworks have no version has no `_OR_GREATER` symbol
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;BROWSER", "net8.0-browser", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;BROWSER", "net8.0", false)]
    [InlineData("NET;NETCOREAPP;NET10_0;NET10_0_OR_GREATER;WASI", "net10.0-wasi", true)]
    // a platform with versions wins over a project's own symbol of a platform without them, for code it shares with it
    [InlineData("NET;NETCOREAPP;NET8_0;BROWSER;WINDOWS;WINDOWS7_0_OR_GREATER;WINDOWS10_0_19041_0_OR_GREATER", "net8.0-windows", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;BROWSER;WINDOWS;WINDOWS7_0_OR_GREATER;WINDOWS10_0_19041_0_OR_GREATER", "net8.0-windows10.0.19041.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;BROWSER;WINDOWS;WINDOWS7_0_OR_GREATER;WINDOWS10_0_19041_0_OR_GREATER", "net8.0-browser", false)]
    // symbols are case-sensitive: a project's own `Windows` is not the SDK's platform symbol
    [InlineData("NET;NETCOREAPP;NET8_0;Windows", "net8.0", true)]
    // nor is its own `WINDOWS`: the SDK's comes with the platform's versions, and for .NET 5 and later only
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS", "net8.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS", "net8.0-windows", false)]
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS", "net8.0-windows10.0.19041.0", false)]
    // the SDK defines the symbol of the exact version too, the highest, also one its list of versions lacks
    [InlineData("NET;NETCOREAPP;NET8_0;ANDROID;ANDROID21_0_OR_GREATER;ANDROID34_0_OR_GREATER;ANDROID35_0", "net8.0-android35.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;ANDROID;ANDROID21_0_OR_GREATER;ANDROID34_0_OR_GREATER;ANDROID35_0", "net8.0-android34.0", false)]
    [InlineData("NET;NETCOREAPP;NET10_0;WINDOWS;WINDOWS10_0_26100_0_OR_GREATER;WINDOWS10_0_26100_0;CSWINRT3_0", "net10.0-windows10.0.26100.1", true)]
    // a platform the SDK does not ship, `net8.0-xyz1.0`, has symbols of the same form as a project's own
    // `SQLITE` and `SQLITE3`, so it counts only when the framework asked for names it
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;XYZ;XYZ1_0", "net8.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;XYZ;XYZ1_0", "net8.0-xyz", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;XYZ;XYZ1_0", "net8.0-xyz1.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;XYZ;XYZ1_0", "net8.0-xyz2.0", false)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;SQLITE;SQLITE3", "net8.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;EF;EF8;DEBUG", "net8.0", true)]
    // without a version, `net8.0-desktop`, only its symbol: a project's own unless the framework asked for names it
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;DESKTOP", "net8.0-desktop", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;DESKTOP", "net8.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;DEBUG;TRACE", "net8.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER", "net8.0-desktop", false)]
    [InlineData("NETFRAMEWORK;NET462;WINDOWS;WINDOWS7_0_OR_GREATER", "net462", true)]
    [InlineData("NETSTANDARD;NETSTANDARD2_0;ANDROID;ANDROID21_0_OR_GREATER", "netstandard2.0", true)]
    [InlineData("net;netcoreapp;net8_0", "net8.0", null)]
    // a family names no framework, though every framework of it defines its symbol
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER", "net", false)]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER", "netcoreapp", false)]
    [InlineData("NETSTANDARD;NETSTANDARD2_0", "netstandard", false)]
    [InlineData("NETFRAMEWORK;NET462", "netframework", false)]
    [InlineData("TRACE", "net", false)]
    // a non-SDK project, or one with DisableImplicitFrameworkDefines, does not say
    [InlineData("TRACE;DEBUG", "net48", null)]
    [InlineData("", "net8.0", null)]
    public void TargetsFramework(string symbols, string targetFramework, bool? expected)
    {
        TargetFrameworkHelper.TargetsFramework(symbols.Split(';', StringSplitOptions.RemoveEmptyEntries), targetFramework).Should().Be(expected);
    }

    [Theory]
    [InlineData("net8.0", "net8.0", true)]
    [InlineData("net8.0", "NET8.0", true)]
    [InlineData("net8.0", "net9.0", false)]
    [InlineData("net8.0", "net8.0-windows", false)]
    [InlineData("net8.0-windows", "net8.0", false)]
    [InlineData("net8.0-windows10.0.19041.0", "net8.0-windows", true)]
    [InlineData("net8.0-windows10.0.19041.0", "net8.0-windows10.0.19041", true)]
    [InlineData("net8.0-windows10.0.17763.0", "net8.0-windows10.0.19041.0", false)]
    [InlineData("net8.0-windows", "net8.0-windows10.0.19041.0", false)]
    [InlineData("net8.0-android34.0", "net8.0-android34", true)]
    [InlineData("net8.0-windows", "net8.0-android", false)]
    [InlineData("net8.0", "net", false)]
    // the version a framework without one stands for, as its symbols give it
    [InlineData("net8.0-windows", "net8.0-windows7.0", true, "7.0")]
    [InlineData("net8.0-windows", "net8.0-windows10.0.19041.0", false, "7.0")]
    [InlineData("net8.0-windows10.0.17763.0", "net8.0-windows10.0.19041.0", false, "10.0.19041.0")]
    public void MatchesFramework(string projectFramework, string targetFramework, bool expected, string projectPlatformVersion = null)
    {
        var version = projectPlatformVersion is null ? null : TargetFrameworkHelper.GetTargetFrameworkPlatformVersion($"net8.0-windows{projectPlatformVersion}");
        TargetFrameworkHelper.MatchesFramework(projectFramework, targetFramework, version).Should().Be(expected);
    }

    [Theory]
    [InlineData("net8.0", null)]
    [InlineData("net8.0-windows", null)]
    [InlineData("net8.0-windows10.0.19041", "10.0.19041.0")]
    [InlineData("net8.0-windows10.0.19041.0", "10.0.19041.0")]
    [InlineData("net8.0-android34", "34.0.0.0")]
    public void GetTargetFrameworkPlatformVersion(string targetFramework, string expected)
    {
        TargetFrameworkHelper.GetTargetFrameworkPlatformVersion(targetFramework).Should().Be(expected is null ? null : Version.Parse(expected));
    }

    [Theory]
    [InlineData("Lib(net8.0)", "net8.0")]
    [InlineData("Lib(netstandard2.0)", "netstandard2.0")]
    [InlineData("My(Lib)(net8.0)", "net8.0")]
    [InlineData("Lib( netstandard2.0 )", "netstandard2.0")]
    [InlineData("Lib", null)]
    public void GetTargetFrameworkFromName(string projectName, string expected)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(projectName, LanguageNames.CSharp);

        TargetFrameworkHelper.GetTargetFrameworkFromName(project).Should().Be(expected);
    }

    [Theory]
    [InlineData("net8.0", "Lib(net8.0)")]
    [InlineData("NET8.0", "Lib(net8.0)")]
    [InlineData("NetStandard2.0", "Lib(netstandard2.0)")]
    [InlineData("net8.0-windows", "Lib(net8.0-windows10.0.17763.0)")]
    [InlineData("net8.0-windows10.0.19041.0", "Lib(net8.0-windows10.0.19041.0)")]
    [InlineData("net8.0-windows10.0.19041", "Lib(net8.0-windows10.0.19041.0)")]
    public async Task SelectProjectOfAMultiTargetedProject(string targetFramework, string expected)
    {
        using var workspace = new AdhocWorkspace();
        var projects = new[] { "Lib(net8.0)", "Lib(netstandard2.0)", "Lib(net8.0-windows10.0.17763.0)", "Lib(net8.0-windows10.0.19041.0)" }
            .Select(name => workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, name, "Lib", LanguageNames.CSharp, filePath: "/src/Lib/Lib.csproj")))
            .ToList();

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(workspace.CurrentSolution, projects[0], targetFramework);

        selected.Name.Should().Be(expected);
        matched.Should().BeTrue();
    }

    [Fact]
    public async Task SelectProjectOfAMultiTargetedProjectPrefersTheExactName()
    {
        // `net8.0-windows` also selects `net8.0-windows10.0.17763.0`, which comes first.
        using var workspace = new AdhocWorkspace();
        var projects = new[] { "Lib(net8.0-windows10.0.17763.0)", "Lib(net8.0-windows)" }
            .Select(name => workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, name, "Lib", LanguageNames.CSharp, filePath: "/src/Lib/Lib.csproj")))
            .ToList();

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(workspace.CurrentSolution, projects[0], "net8.0-windows");

        selected.Name.Should().Be("Lib(net8.0-windows)");
        matched.Should().BeTrue();
    }

    [Theory]
    [InlineData("net8.0-windows7.0", true)]
    [InlineData("net8.0-windows10.0.19041.0", false)]
    // a project's own symbol of a platform without versions does not hide the versions of its platform
    [InlineData("net8.0-windows7.0", true, ";BROWSER")]
    public async Task SelectProjectReadsThePlatformVersionANameLeavesOutFromTheSymbols(string targetFramework, bool expectMatch, string ownSymbols = "")
    {
        using var workspace = new AdhocWorkspace();
        var projects = new[] { ("Lib(net8.0)", "NET;NET8_0"), ("Lib(net8.0-windows)", "NET;NET8_0;WINDOWS;WINDOWS7_0_OR_GREATER" + ownSymbols) }
            .Select(p => workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Default, p.Item1, "Lib", LanguageNames.CSharp, filePath: "/src/Lib/Lib.csproj",
                parseOptions: new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(preprocessorSymbols: p.Item2.Split(';')))))
            .ToList();

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(workspace.CurrentSolution, projects[0], targetFramework);

        matched.Should().Be(expectMatch);
        selected.Should().BeSameAs(expectMatch ? projects[1] : projects[0]);
    }

    [Theory]
    [InlineData("net9.0")]
    [InlineData("net8.0-android")]
    // a platform version the project does not target is not one it does
    [InlineData("net8.0-windows10.0.22621.0")]
    [InlineData("net")]
    public async Task SelectProjectFallsBackToTheProjectGiven(string targetFramework)
    {
        using var workspace = new AdhocWorkspace();
        var projects = new[] { "Lib(net8.0)", "Lib(net8.0-windows10.0.17763.0)", "Lib(net8.0-windows10.0.19041.0)" }
            .Select(name => workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, name, "Lib", LanguageNames.CSharp, filePath: "/src/Lib/Lib.csproj")))
            .ToList();

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(workspace.CurrentSolution, projects[0], targetFramework);

        selected.Should().BeSameAs(projects[0]);
        matched.Should().BeFalse();
    }

    [Fact]
    public async Task SelectProjectOfAProjectWithoutFrameworkSymbolsIsNotAMatch()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Legacy", "Legacy", LanguageNames.CSharp, filePath: "/src/Legacy/Legacy.csproj",
            parseOptions: new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(preprocessorSymbols: ["TRACE"])));

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(workspace.CurrentSolution, project, "net8.0");

        selected.Should().BeSameAs(project);
        matched.Should().BeFalse();
    }

    [Theory]
    [InlineData("", "net8.0", true)]
    [InlineData("", "net9.0", false)]
    [InlineData("", "net8.0-windows", false)]
    [InlineData("", "net", false)]
    [InlineData("Windows10.0.19041.0", "net8.0-windows", true)]
    [InlineData("Windows10.0.19041.0", "net8.0-windows10.0.19041.0", true)]
    [InlineData("Windows10.0.19041.0", "net8.0", false)]
    public async Task SelectProjectOfAProjectWithoutFrameworkSymbolsReadsItsTargetFrameworkAttribute(string platform, string targetFramework, bool expectMatch)
    {
        // what the SDK generates for a project that sets `DisableImplicitFrameworkDefines`
        var source = """[assembly: System.Runtime.Versioning.TargetFramework(".NETCoreApp,Version=v8.0")]"""
            + (platform.Length > 0 ? $"""[assembly: System.Runtime.Versioning.TargetPlatform("{platform}")]""" : "");

        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Lib", "Lib", LanguageNames.CSharp, filePath: "/src/Lib/Lib.csproj",
            compilationOptions: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(preprocessorSymbols: ["TRACE"]),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(System.Runtime.Versioning.TargetFrameworkAttribute).Assembly.Location)]));
        project = workspace.AddDocument(project.Id, "AssemblyInfo.cs", Microsoft.CodeAnalysis.Text.SourceText.From(source)).Project;

        var (_, matched) = await TargetFrameworkHelper.SelectProject(project.Solution, project, targetFramework);

        matched.Should().Be(expectMatch);
    }

    [Theory]
    // the attributes name the framework exactly, so a project's own symbols do not make it another
    [InlineData("Windows10.0.19041.0", "NET;NETCOREAPP;NET8_0;WINDOWS;WINDOWS10_0_17763_0_OR_GREATER", null, "net8.0-windows10.0.19041.0")]
    [InlineData("Windows10.0.26100.1", "NET;NETCOREAPP;NET8_0;WINDOWS;WINDOWS10_0_26100_0_OR_GREATER", null, "net8.0-windows10.0.26100.1")]
    [InlineData("", "NET;NETCOREAPP;NET8_0;SQLITE;SQLITE3", null, "net8.0")]
    [InlineData("", "NET;NETCOREAPP;NET8_0;SQLITE;SQLITE3", "SQLITE", "net8.0-sqlite3.0")]
    // without a TargetPlatformAttribute (`GenerateAssemblyInfo` off), the symbols give the platform
    [InlineData("", "NET;NETCOREAPP;NET8_0;WINDOWS;WINDOWS7_0_OR_GREATER;WINDOWS10_0_19041_0_OR_GREATER", null, "net8.0-windows10.0.19041.0")]
    [InlineData("", "NET;NETCOREAPP;NET8_0;BROWSER", null, "net8.0-browser")]
    // nor does the SDK generate one for a platform without a version
    [InlineData("", "NET;NETCOREAPP;NET8_0;DESKTOP", "DESKTOP", "net8.0-desktop")]
    [InlineData("", "NET;NETCOREAPP;NET8_0;DESKTOP", null, "net8.0")]
    [InlineData("", "", null, "net8.0")]
    public async Task GetTargetFrameworkReadsTheAttributesFirst(string platform, string symbols, string requestedPlatform, string expected)
    {
        var project = CreateProjectWithAttributes(".NETCoreApp,Version=v8.0", platform, symbols);
        var compilation = await project.GetCompilationAsync(TestContext.Current.CancellationToken);

        TargetFrameworkHelper.GetTargetFramework(compilation.Assembly, symbols.Split(';', StringSplitOptions.RemoveEmptyEntries), requestedPlatform)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER", "net8.0")]
    [InlineData("NETSTANDARD;NETSTANDARD2_0", "netstandard2.0")]
    [InlineData("NETFRAMEWORK;NET462", "net462")]
    [InlineData("TRACE", null)]
    public void GetTargetFrameworkWithoutTheAttributesReadsTheSymbols(string symbols, string expected)
    {
        TargetFrameworkHelper.GetTargetFramework(null, symbols.Split(';')).Should().Be(expected);
    }

    [Fact]
    public async Task SelectProjectOfASingleTargetProjectWithOwnSymbolsShapedLikeAPlatform()
    {
        var project = CreateProjectWithAttributes(".NETCoreApp,Version=v8.0", "", "NET;NETCOREAPP;NET8_0;NET8_0_OR_GREATER;SQLITE;SQLITE3");

        var (_, matched) = await TargetFrameworkHelper.SelectProject(project.Solution, project, "net8.0");

        matched.Should().BeTrue();
    }

    [Theory]
    [InlineData("net8.0", "Lib(modern)")]
    [InlineData("netstandard2.0", "Lib(legacy)")]
    // the alias itself names it as well
    [InlineData("legacy", "Lib(legacy)")]
    public async Task SelectProjectOfAMultiTargetedProjectWithAliasesReadsTheirSymbols(string targetFramework, string expected)
    {
        // `<TargetFrameworks>modern;legacy</TargetFrameworks>`, with `TargetFrameworkIdentifier` and version set per alias
        using var workspace = new AdhocWorkspace();
        var projects = new[] { ("Lib(modern)", "NET;NETCOREAPP;NET8_0"), ("Lib(legacy)", "NETSTANDARD;NETSTANDARD2_0") }
            .Select(p => workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(), VersionStamp.Default, p.Item1, "Lib", LanguageNames.CSharp, filePath: "/src/Lib/Lib.csproj",
                parseOptions: new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(preprocessorSymbols: p.Item2.Split(';')))))
            .ToList();

        var (selected, matched) = await TargetFrameworkHelper.SelectProject(workspace.CurrentSolution, projects[0], targetFramework);

        selected.Name.Should().Be(expected);
        matched.Should().BeTrue();
    }

    private static Project CreateProjectWithAttributes(string frameworkName, string platform, string symbols)
    {
        var source = $"""[assembly: System.Runtime.Versioning.TargetFramework("{frameworkName}")]"""
            + (platform.Length > 0 ? $"""[assembly: System.Runtime.Versioning.TargetPlatform("{platform}")]""" : "");

        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(), VersionStamp.Default, "Lib", "Lib", LanguageNames.CSharp, filePath: "/src/Lib/Lib.csproj",
            compilationOptions: new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new Microsoft.CodeAnalysis.CSharp.CSharpParseOptions(preprocessorSymbols: symbols.Split(';', StringSplitOptions.RemoveEmptyEntries)),
            metadataReferences: [MetadataReference.CreateFromFile(typeof(System.Runtime.Versioning.TargetFrameworkAttribute).Assembly.Location)]));
        return workspace.AddDocument(project.Id, "AssemblyInfo.cs", Microsoft.CodeAnalysis.Text.SourceText.From(source)).Project;
    }

    [Theory]
    [InlineData(".NETCoreApp,Version=v8.0", "net8.0")]
    [InlineData(".NETCoreApp,Version=v3.1", "netcoreapp3.1")]
    [InlineData(".NETStandard,Version=v2.0", "netstandard2.0")]
    [InlineData(".NETFramework,Version=v4.6.2", "net462")]
    [InlineData(".NETFramework,Version=v4.8", "net48")]
    [InlineData("Silverlight,Version=v5.0", null)]
    public void ParseFrameworkName(string frameworkName, string expected)
    {
        TargetFrameworkHelper.ParseFrameworkName(frameworkName).Should().Be(expected);
    }
}

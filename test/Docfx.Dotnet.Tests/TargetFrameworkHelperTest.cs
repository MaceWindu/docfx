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
    // symbols are case-sensitive: a project's own `Windows` is not the SDK's platform symbol
    [InlineData("NET;NETCOREAPP;NET8_0;Windows", "net8.0", true)]
    // nor is its own `WINDOWS`: the SDK's comes with the platform's versions, and for .NET 5 and later only
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS", "net8.0", true)]
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS", "net8.0-windows", false)]
    [InlineData("NET;NETCOREAPP;NET8_0;WINDOWS", "net8.0-windows10.0.19041.0", false)]
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
    [InlineData("net8.0", "NET", "NETCOREAPP", "NET8_0", "NET8_0_OR_GREATER", "NET7_0_OR_GREATER")]
    [InlineData("net10.0", "NET10_0_OR_GREATER", "NET10_0")]
    [InlineData("netstandard2.0", "NETSTANDARD", "NETSTANDARD2_0", "NETSTANDARD1_6_OR_GREATER")]
    [InlineData("netcoreapp3.1", "NETCOREAPP", "NETCOREAPP3_1")]
    [InlineData("net462", "NETFRAMEWORK", "NET462", "NET461_OR_GREATER")]
    [InlineData(null, "NET", "NETFRAMEWORK", "DEBUG", "NET8_0_OR_GREATER")]
    // the platform is kept, as a multi-targeted project's name keeps it, but no symbol names its version
    [InlineData("net8.0-windows", "NET", "NET8_0", "WINDOWS", "WINDOWS10_0_19041_0_OR_GREATER")]
    // symbols are case-sensitive: a project's own symbols are not the SDK's
    [InlineData("net8.0", "NET", "NET8_0", "Windows")]
    [InlineData(null, "net8_0")]
    // nor is a project's own `WINDOWS` without the platform's versions
    [InlineData("net8.0", "NET", "NET8_0", "WINDOWS")]
    public void GetTargetFrameworkFromSymbols(string expected, params string[] symbols)
    {
        TargetFrameworkHelper.GetTargetFrameworkFromSymbols(symbols).Should().Be(expected);
    }

    [Fact]
    public void GetTargetFrameworkFromAttribute()
    {
        var compilation = CompilationHelper.CreateCompilationFromCSharpCode(
            """[assembly: System.Runtime.Versioning.TargetFramework(".NETStandard,Version=v2.1")]""",
            new Dictionary<string, string>(),
            "Lib");

        TargetFrameworkHelper.GetTargetFrameworkFromAttribute(compilation.Assembly).Should().Be("netstandard2.1");
    }

    [Theory]
    [InlineData("""{ "targetFrameworks": "*" }""", new[] { "*" })]
    [InlineData("""{ "targetFrameworks": [ "net8.0", "net462" ] }""", new[] { "net8.0", "net462" })]
    public void TargetFrameworksIsReadFromAStringOrAnArray(string json, string[] expected)
    {
        Newtonsoft.Json.JsonConvert.DeserializeObject<MetadataJsonItemConfig>(json).TargetFrameworks.Should().Equal(expected);
        System.Text.Json.JsonSerializer.Deserialize<MetadataJsonItemConfig>(json).TargetFrameworks.Should().Equal(expected);
    }

    [Fact]
    public void AppliesToIsGroupedByValueInTheOrderOfTheAxis()
    {
        var axis = TargetFrameworkAxis.Create(
            [new() { Name = "EF Core", Values = [new() { Id = "ef3", Label = "EF Core 3.1", Files = ["a"] }, new() { Id = "ef9", Files = ["b"] }] }],
            [Directory.GetCurrentDirectory()]);

        var groups = YamlViewModelExtensions.ToAppliesToViewModel(["ef9:net8.0", "ef3:netstandard2.0", "ef3:net462"], axis);

        groups.Select(g => g.Version).Should().Equal("ef3", "ef9");
        groups.Select(g => g.Label).Should().Equal("EF Core 3.1", "ef9");
        groups[0].Frameworks.Should().Equal("netstandard2.0", "net462");
    }

    [Fact]
    public void AppliesToWithoutAnAxisIsOneGroup()
    {
        var groups = YamlViewModelExtensions.ToAppliesToViewModel(["net8.0", "net462"], null);

        groups.Should().ContainSingle().Which.Frameworks.Should().Equal("net8.0", "net462");
        groups[0].Version.Should().BeNull();
    }

    [Theory]
    [InlineData("ef9:net8.0", "ef9", "net8.0")]
    [InlineData("net8.0", null, "net8.0")]
    public void SplitUnit(string unit, string value, string framework)
    {
        TargetFrameworkAxis.SplitUnit(unit).Should().Be((value, framework));
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

    [Theory]
    [InlineData("net8.0-windows7.0", true)]
    [InlineData("net8.0-windows10.0.19041.0", false)]
    public async Task SelectProjectReadsThePlatformVersionANameLeavesOutFromTheSymbols(string targetFramework, bool expectMatch)
    {
        using var workspace = new AdhocWorkspace();
        var projects = new[] { ("Lib(net8.0)", "NET;NET8_0"), ("Lib(net8.0-windows)", "NET;NET8_0;WINDOWS;WINDOWS7_0_OR_GREATER") }
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

    [Theory]
    [InlineData("net10.0", "net462 netstandard2.0 net8.0 net10.0", "net10.0 net8.0 netstandard2.0 net462")]
    [InlineData("net10.0", "net462 netstandard2.0", "netstandard2.0 net462")]
    [InlineData("net48", "netstandard2.0 net8.0 net462", "net462 netstandard2.0 net8.0")]
    [InlineData("net8.0", "netstandard2.0 netstandard2.1 netcoreapp3.1", "netcoreapp3.1 netstandard2.1 netstandard2.0")]
    [InlineData("net472", "netstandard2.1 netstandard2.0 netstandard1.3", "netstandard2.0 netstandard1.3 netstandard2.1")]
    [InlineData("net45", "netstandard1.3 netstandard1.1", "netstandard1.1 netstandard1.3")]
    [InlineData("netcoreapp2.1", "netstandard2.1 netstandard2.0", "netstandard2.0 netstandard2.1")]
    [InlineData("net8.0", "net8.0-windows netstandard2.0", "netstandard2.0 net8.0-windows")]
    [InlineData("net8.0-windows", "net8.0 net8.0-windows", "net8.0-windows net8.0")]
    [InlineData("NET8.0", "net9.0 net8.0", "net8.0 net9.0")]
    [InlineData("unknown", "net8.0 netstandard2.0", "net8.0 netstandard2.0")]
    public void OrderByNearest(string target, string frameworks, string expected)
    {
        TargetFrameworkHelper.OrderByNearest(target, frameworks.Split(' ')).Should().Equal(expected.Split(' '));
    }

    [Theory]
    [InlineData("net10.0", "net8.0", true)]
    [InlineData("net10.0", "netstandard2.0", true)]
    [InlineData("net472", "net462", true)]
    [InlineData("net462", "netstandard2.0", true)]
    [InlineData("NET8.0", "net8.0", true)]
    [InlineData("net8.0-windows", "net8.0", true)]
    [InlineData("net8.0", "net10.0", false)]
    [InlineData("net462", "net8.0", false)]
    [InlineData("net462", "netstandard2.1", false)]
    [InlineData("net8.0", "net8.0-windows", false)]
    [InlineData("unknown", "net8.0", false)]
    public void CanStandIn(string target, string framework, bool expected)
    {
        TargetFrameworkHelper.CanStandIn(target, framework).Should().Be(expected);
    }
}

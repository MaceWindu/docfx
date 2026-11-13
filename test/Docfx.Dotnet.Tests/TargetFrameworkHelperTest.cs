// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Docfx.Dotnet.Tests;

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
    // a non-SDK project, or one with DisableImplicitFrameworkDefines, does not say
    [InlineData("TRACE;DEBUG", "net48", null)]
    [InlineData("", "net8.0", null)]
    public void TargetsFramework(string symbols, string targetFramework, bool? expected)
    {
        TargetFrameworkHelper.TargetsFramework(symbols.Split(';', StringSplitOptions.RemoveEmptyEntries), targetFramework).Should().Be(expected);
    }

    [Theory]
    [InlineData("net8.0", "NET", "NETCOREAPP", "NET8_0", "NET8_0_OR_GREATER", "NET7_0_OR_GREATER")]
    [InlineData("net10.0", "NET10_0_OR_GREATER", "NET10_0")]
    [InlineData("netstandard2.0", "NETSTANDARD", "NETSTANDARD2_0", "NETSTANDARD1_6_OR_GREATER")]
    [InlineData("netcoreapp3.1", "NETCOREAPP", "NETCOREAPP3_1")]
    [InlineData("net462", "NETFRAMEWORK", "NET462", "NET461_OR_GREATER")]
    [InlineData(null, "NET", "NETFRAMEWORK", "DEBUG", "NET8_0_OR_GREATER")]
    public void GetTargetFrameworkFromSymbols(string expected, params string[] symbols)
    {
        TargetFrameworkHelper.GetTargetFrameworkFromSymbols(symbols).Should().Be(expected);
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
    [InlineData("Lib", null)]
    public void GetTargetFrameworkFromName(string projectName, string expected)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(projectName, LanguageNames.CSharp);

        TargetFrameworkHelper.GetTargetFrameworkFromName(project).Should().Be(expected);
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

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
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AwesomeAssertions;
using Docfx.Tests.Common;
using Xunit;

namespace Docfx.Dotnet.Tests;

/// <summary>
/// What <c>dotnet msbuild</c> evaluates real projects to.
/// </summary>
public class ProjectFrameworksTest : TestBase
{
    private readonly string _projectFolder;

    public ProjectFrameworksTest()
    {
        _projectFolder = Path.GetFullPath(GetRandomFolder());

        // independent of the repository's own build settings
        File.WriteAllText(Path.Combine(_projectFolder, "Directory.Build.props"), "<Project></Project>");
        File.WriteAllText(Path.Combine(_projectFolder, "Directory.Build.targets"), "<Project></Project>");
        File.WriteAllText(Path.Combine(_projectFolder, "Directory.Packages.props"), "<Project></Project>");
    }

    [Theory]
    [InlineData("modern", "net8.0")]
    [InlineData("legacy", "netstandard2.0")]
    public async Task AnAliasIsEvaluatedToTheFrameworkItStandsFor(string alias, string expected)
    {
        var project = CreateProject("Lib.csproj", AliasProject);

        var evaluation = await new ProjectFrameworks(new Dictionary<string, string>()).Evaluate(project, alias);

        evaluation.Failure.Should().BeNull();
        evaluation.Framework.Should().Be(new ProjectFramework(alias, expected));
    }

    [Fact]
    public async Task ANonSdkProjectIsEvaluatedToTheFrameworkItTargets()
    {
        var project = CreateProject("Legacy.csproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <AssemblyName>Legacy</AssemblyName>
                <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Widget.cs" />
              </ItemGroup>
              <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
            </Project>
            """);

        var evaluation = await new ProjectFrameworks(new Dictionary<string, string>()).Evaluate(project, null);

        evaluation.Failure.Should().BeNull();
        evaluation.Framework.Should().Be(new ProjectFramework("net48", "net48"));
    }

    [Fact]
    public async Task TheEvaluationHasThePropertiesOfTheMetadataItem()
    {
        // `;` and `,` separate properties on the command line, so they reach MSBuild escaped
        var project = CreateProject("Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework Condition="'$(Choice)' == 'a;b,c'">net8.0</TargetFramework>
                <TargetFramework Condition="'$(Choice)' != 'a;b,c'">netstandard2.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var evaluation = await new ProjectFrameworks(new Dictionary<string, string> { ["Choice"] = "a;b,c" }).Evaluate(project, null);

        evaluation.Failure.Should().BeNull();
        evaluation.Framework?.Name.Should().Be("net8.0");
    }

    [Fact]
    public async Task AnEvaluationThatDoesNotAnswerInTimeIsAFailure()
    {
        var project = CreateProject("Lib.csproj", AliasProject);

        var evaluation = await new ProjectFrameworks(new Dictionary<string, string>(), TimeSpan.FromMilliseconds(1)).Evaluate(project, "modern");

        evaluation.Framework.Should().BeNull();
        evaluation.Failure.Should().StartWith($"MSBuild cannot evaluate project '{project}' for 'modern'").And.Contain("did not answer within 0.001 seconds");
    }

    // `<TargetFrameworks>modern;legacy</TargetFrameworks>`, with the framework of each alias set
    private const string AliasProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFrameworks>modern;legacy</TargetFrameworks>
          </PropertyGroup>
          <PropertyGroup Condition="'$(TargetFramework)' == 'modern'">
            <TargetFrameworkIdentifier>.NETCoreApp</TargetFrameworkIdentifier>
            <TargetFrameworkVersion>v8.0</TargetFrameworkVersion>
          </PropertyGroup>
          <PropertyGroup Condition="'$(TargetFramework)' == 'legacy'">
            <TargetFrameworkIdentifier>.NETStandard</TargetFrameworkIdentifier>
            <TargetFrameworkVersion>v2.0</TargetFrameworkVersion>
          </PropertyGroup>
        </Project>
        """;

    private string CreateProject(string fileName, string content)
    {
        var directory = Path.Combine(_projectFolder, Path.GetFileNameWithoutExtension(fileName));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Widget.cs"), "namespace Lib { public class Widget { } }");
        var project = Path.Combine(directory, fileName);
        File.WriteAllText(project, content);
        return project;
    }
}

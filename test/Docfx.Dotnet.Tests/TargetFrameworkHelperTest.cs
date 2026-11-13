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

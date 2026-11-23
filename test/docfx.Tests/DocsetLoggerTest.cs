// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Docfx.Common;
using Docfx.Dotnet;
using Docfx.Tests.Common;

namespace Docfx.Tests;

[Collection("docfx STA")]
public class DocsetLoggerTest : TestBase
{
    [Fact]
    public async Task BuildKeepsTheListenersOfItsCaller()
    {
        var directory = GetRandomFolder();
        var configPath = CreateFile("docfx.json",
            """
            {
                "build": {
                    "content": [{ "files": [ "*.md" ] }],
                    "dest": "_site"
                }
            }
            """, directory);
        CreateFile("index.md", "# Hello", directory);

        using var listener = new TestListenerScope();
        await Docset.Build(configPath);

        Assert.Contains(listener, Logger.GetAllListeners());
        Logger.LogWarning("After the build");
        Assert.Contains(listener.Items, item => item.Message == "After the build");
        Logger.ResetCount();
    }

    [Fact]
    public async Task MetadataKeepsTheListenersOfItsCaller()
    {
        var directory = GetRandomFolder();
        var configPath = CreateFile("docfx.json", "{}", directory);

        using var listener = new TestListenerScope();
        await DotnetApiCatalog.GenerateManagedReferenceYamlFiles(configPath);

        Assert.Contains(listener, Logger.GetAllListeners());
        Logger.LogWarning("After the metadata");
        Assert.Contains(listener.Items, item => item.Message == "After the metadata");
        Logger.ResetCount();
    }

    [Fact]
    public async Task BuildRemovesTheListenerItRegistered()
    {
        var directory = GetRandomFolder();
        var configPath = CreateFile("docfx.json",
            """
            {
                "build": {
                    "content": [{ "files": [ "*.md" ] }],
                    "dest": "_site"
                }
            }
            """, directory);
        CreateFile("index.md", "# Hello", directory);

        var before = Logger.GetAllListeners().Count();
        await Docset.Build(configPath);

        Assert.Equal(before, Logger.GetAllListeners().Count());
    }
}

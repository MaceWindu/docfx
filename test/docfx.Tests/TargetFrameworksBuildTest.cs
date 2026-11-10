// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Docfx.Dotnet;
using Docfx.Tests.Common;
using Newtonsoft.Json.Linq;

namespace Docfx.Tests;

/// <summary>
/// Builds a site from a multi-targeted project. `appliesTo` can be written to the `.yml` faithfully and
/// still render nothing, so what it does is asserted on the HTML and the other files of the site.
/// </summary>
[Collection("docfx STA")]
public class TargetFrameworksBuildTest : TestBase
{
    private const string Both = ".NET 8.0, .NET Standard 2.0";
    private const string Net8 = ".NET 8.0";

    private readonly string _projectFolder;

    public TargetFrameworksBuildTest()
    {
        _projectFolder = GetRandomFolder();

        File.WriteAllText(Path.Combine(_projectFolder, "Directory.Build.props"), "<Project></Project>");
        File.WriteAllText(Path.Combine(_projectFolder, "Directory.Packages.props"), "<Project></Project>");

        var folder = Path.Combine(_projectFolder, "Lib");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>
                <GenerateDocumentationFile>true</GenerateDocumentationFile>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(folder, "Lib.cs"), """
            namespace Lib
            {
                public class Shared
                {
                    public int Everywhere { get; set; }
            #if NET8_0
                    public int OnlyNet8Member { get; set; }
                    public void Do(int value) { }
            #else
                    public void Do(string value) { }
            #endif
                }
            #if NET8_0
                public class OnlyNet8 { }

                /// <summary>Described for net8.</summary>
                public class Differs
                {
                    public int Value() => 0;
                }
            #else
                /// <summary>Described for older, see <see cref="Shared"/>.</summary>
                public class Differs
                {
                    public long Value() => 0;
                }
            #endif
            }
            """);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"", "<strong>Applies to</strong>: {0}</h6>")]
    [InlineData("\"default\", \"modern\"", "<dt>Applies to</dt><dd>{0}</dd>")]
    public async Task EachTypeStatesTheFrameworksItExistsIn(string template, string expected)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");

        Assert.Contains(string.Format(expected, Both), await Page(site, "Lib.Shared.html"));
        Assert.Contains(string.Format(expected, Net8), await Page(site, "Lib.OnlyNet8.html"));
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task OnlyMembersThatDifferFromTheirTypeAreMarked()
    {
        var site = await Build("\"default\", \"modern\"", targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Shared.html");

        Assert.Contains("data-uid=\"Lib.Shared.Everywhere\" data-applies-to=\"net8.0 netstandard2.0\"", page);
        Assert.Contains("data-uid=\"Lib.Shared.OnlyNet8Member\" data-applies-to=\"net8.0\"", page);
        Assert.Contains($"<span class=\"badge text-bg-secondary applies-to\">{Net8}</span>", page);
        Assert.DoesNotContain($"<span class=\"badge text-bg-secondary applies-to\">{Both}</span>", page);
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task AnOverloadPageAppliesWhereverAnyOverloadDoes()
    {
        var site = await Build("\"default\", \"modern\"", targetFrameworks: "\"*\"", memberLayout: "separatePages");
        var page = await Page(site, "Lib.Shared.Do.html");

        Assert.Contains($"<dt>Applies to</dt><dd>{Both}</dd>", page);
        Assert.Contains($"<span class=\"badge text-bg-secondary applies-to\">{Net8}</span>", page);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("samePage", "Lib.OnlyNet8")]
    [InlineData("separatePages", "Lib.Shared.OnlyNet8Member*")]
    public async Task TheTableOfContentsCarriesTheFrameworks(string memberLayout, string uid)
    {
        var site = await Build("\"default\", \"modern\"", targetFrameworks: "\"*\"", memberLayout: memberLayout);

        var toc = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(site, "toc.json"), TestContext.Current.CancellationToken));
        var node = toc.Descendants().OfType<JObject>().First(n => (string)n["topicUid"] == uid);

        Assert.Equal(["net8.0"], node["appliesTo"][0]["frameworks"].Values<string>());
    }

    [Fact]
    [Trait("Related", "docfx")]
    public async Task ReferencesCarryTheFrameworksOfTheirTarget()
    {
        var site = await Build("\"default\"", targetFrameworks: "\"*\"", extraBuild: "\"exportRawModel\": true,");

        var raw = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(site, "Lib.raw.json"), TestContext.Current.CancellationToken));
        var child = raw["children"].Single(c => (string)c["uid"] == "Lib.OnlyNet8");

        Assert.Equal(["net8.0"], child["appliesTo"][0]["frameworks"].Values<string>());
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task WhatDiffersInAnotherFrameworkIsShownWithItsLabel(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Differs.html");

        Assert.Contains("Described for net8.", page);
        Assert.Contains("class=\"api-variant\" data-applies-to=\"netstandard2.0\"", page);
        Assert.Contains("<h6 class=\"api-variant-label\">.NET Standard 2.0</h6>", page);
        Assert.Contains("Described for older", page);

        // The summary went through the same markup and link resolution as the item's own.
        Assert.Contains("href=\"Lib.Shared.html\"", page);

        // A member shows its own difference, and not the one of its type.
        Assert.Contains("public long Value()", page);
        Assert.Equal(1, Regex.Count(page, "Described for older"));
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task AVariantDoesNotRepeatWhatIsTheSame(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"");
        var page = await Page(site, "Lib.Differs.html");

        var variant = Regex.Match(page, "<div class=\"api-variant\"[\\s\\S]*?Described for older[\\s\\S]*?</div>\\s*</div>").Value;
        Assert.DoesNotContain("Described for net8.", variant);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task NothingIsRenderedWithoutTheOption(string template)
    {
        var site = await Build(template, targetFrameworks: null);
        var page = await Page(site, "Lib.Shared.html");

        Assert.DoesNotContain("Applies to", page);
        Assert.DoesNotContain("data-applies-to", page);
        Assert.DoesNotContain("api-variant", page);
    }

    [Theory]
    [Trait("Related", "docfx")]
    [InlineData("\"default\"")]
    [InlineData("\"default\", \"modern\"")]
    public async Task DisableApiAppliesToRemovesItFromThePage(string template)
    {
        var site = await Build(template, targetFrameworks: "\"*\"", extraBuild: "\"globalMetadata\": { \"_disableApiAppliesTo\": true },");
        var page = await Page(site, "Lib.Shared.html");

        Assert.DoesNotContain("Applies to", page);
        Assert.DoesNotContain("data-applies-to", page);
        Assert.DoesNotContain("applies-to\">", page);

        var differs = await Page(site, "Lib.Differs.html");
        Assert.DoesNotContain("api-variant", differs);
    }

    private static Task<string> Page(string site, string name) => File.ReadAllTextAsync(Path.Combine(site, name));

    /// <returns>The folder of the generated API pages.</returns>
    private async Task<string> Build(string template, string targetFrameworks, string memberLayout = "samePage", string extraBuild = "")
    {
        var option = targetFrameworks is null ? "" : $"\"targetFrameworks\": {targetFrameworks},";
        var configPath = Path.Combine(_projectFolder, "docfx.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "metadata": [
                {
                  "src": [ { "files": [ "Lib/Lib.csproj" ] } ],
                  {{option}}
                  "memberLayout": "{{memberLayout}}",
                  "dest": "api"
                }
              ],
              "build": {
                "content": [ { "files": [ "api/**.yml" ] } ],
                "template": [ {{template}} ],
                {{extraBuild}}
                "dest": "_site"
              }
            }
            """);

        await DotnetApiCatalog.GenerateManagedReferenceYamlFiles(configPath);
        await Docset.Build(configPath);

        return Path.Combine(_projectFolder, "_site", "api");
    }
}

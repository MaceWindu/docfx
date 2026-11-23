// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Docfx.Dotnet;
using Docfx.Tests.Common;
using HtmlAgilityPack;

namespace Docfx.Tests;

[Collection("docfx STA")]
public class InterfaceImplementingTypesTest : TestBase
{
    [Theory]
    [InlineData("default", false)]
    [InlineData("default", true)]
    [InlineData("modern", false)]
    [InlineData("modern", true)]
    public async Task AnInterfaceListsTheTypesThatImplementItWhenEnabled(string template, bool enabled)
    {
        // A namespace per case: the derived classes found in one run of the process are still listed in the next
        // run on a type with the same UID.
        var ns = $"N{template}{enabled}";
        var folder = GetRandomFolder();
        CreateFile("Library.cs", $$"""
            namespace {{ns}}
            {
                public interface IShape { }
                public class Circle : IShape { }
                public struct Square : IShape { }
                public class Base { }
                public class Derived : Base { }
            }
            """, folder);

        var templates = Path.GetFullPath("../../../../../templates");
        // `modern` builds on `default`
        string[] layers = template == "modern" ? ["common", "default", "modern"] : ["common", "default"];
        var config = CreateFile("docfx.json", JsonSerializer.Serialize(new
        {
            metadata = new[]
            {
                new
                {
                    src = new[] { new { files = new[] { "Library.cs" } } },
                    dest = "api",
                    disableGitFeatures = true,
                }
            },
            build = new
            {
                content = new[] { new { files = new[] { "api/**.yml" } } },
                dest = "_site",
                template = layers.Select(layer => Path.Combine(templates, layer)).ToArray(),
                globalMetadata = new Dictionary<string, object> { ["_enableApiImplementingTypes"] = enabled },
            }
        }), folder);

        await DotnetApiCatalog.GenerateManagedReferenceYamlFiles(config);
        await Docset.Build(config);

        var listed = Links(Load("IShape"));
        if (enabled)
        {
            Assert.Equal([$"{ns}.Circle.html", $"{ns}.Square.html"], listed);
        }
        else
        {
            Assert.Empty(listed);
        }

        // a class lists the classes derived from it either way
        Assert.Contains($"{ns}.Derived.html", Load("Base").DocumentNode.Descendants("a").Select(a => a.GetAttributeValue("href", null)));

        string[] Links(HtmlDocument page)
        {
            var list = page.DocumentNode.SelectSingleNode(template == "default" ? "//div[@class='derivedClasses']" : "//dl[@class='typelist derived']");
            return list?.Descendants("a").Select(a => a.GetAttributeValue("href", null)).ToArray() ?? [];
        }

        HtmlDocument Load(string name)
        {
            var document = new HtmlDocument();
            document.Load(Path.Combine(folder, "_site", "api", $"{ns}.{name}.html"));
            return document;
        }
    }
}

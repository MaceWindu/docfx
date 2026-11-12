// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Docfx.Common;
using Docfx.Plugins;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace Docfx.Dotnet;

/// <summary>
/// Provides access to a .NET API definitions and their associated documentation.
/// </summary>
public static partial class DotnetApiCatalog
{
    private static IDeserializer deserializer = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build();

    /// <summary>
    /// Generates metadata reference YAML files using docfx.json config.
    /// </summary>
    /// <param name="configPath">The path to docfx.json config file.</param>
    /// <returns>A task to await for build completion.</returns>
    public static Task GenerateManagedReferenceYamlFiles(string configPath)
    {
        return GenerateManagedReferenceYamlFiles(configPath, new());
    }

    /// <summary>
    /// Generates metadata reference YAML files using docfx.json config.
    /// </summary>
    /// <param name="configPath">The path to docfx.json config file.</param>
    /// <returns>A task to await for build completion.</returns>
    public static async Task GenerateManagedReferenceYamlFiles(string configPath, DotnetApiOptions options)
    {
        var consoleLogListener = new ConsoleLogListener();
        Logger.RegisterListener(consoleLogListener);

        try
        {
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
            var config = JObject.Parse(await File.ReadAllTextAsync(configPath));
            if (config.TryGetValue("metadata", out var value))
            {
                Logger.Rules = config["rules"]?.ToObject<Dictionary<string, LogLevel>>();
                await Exec(value.ToObject<MetadataJsonConfig>(NewtonsoftJsonUtility.DefaultSerializer.Value), options, configDirectory);
            }
        }
        finally
        {
            Logger.Flush();
            Logger.PrintSummary();
            Logger.UnregisterAllListeners();
        }
    }

    internal static async Task Exec(MetadataJsonConfig config, DotnetApiOptions options, string configDirectory, string outputDirectory = null, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            string originalGlobalNamespaceId = VisitorHelper.GlobalNamespaceId;

            EnvironmentContext.SetBaseDirectory(configDirectory);

            foreach (var item in config)
            {
                VisitorHelper.GlobalNamespaceId = item.GlobalNamespaceId;
                EnvironmentContext.SetGitFeaturesDisabled(item.DisableGitFeatures);

                await Build(ConvertConfig(item, configDirectory, outputDirectory), options);
            }

            VisitorHelper.GlobalNamespaceId = originalGlobalNamespaceId;
        }
        finally
        {
            EnvironmentContext.Clean();
        }

        Logger.LogVerbose($".NET API done in {stopwatch.Elapsed}");

        async Task Build(ExtractMetadataConfig config, DotnetApiOptions options)
        {
            var (assemblies, frameworks) = await Compile(config);

            // A single framework has nothing to merge, and its output must not differ from plain selection.
            if (frameworks?.Values.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any() != true)
            {
                frameworks = null;
            }

            if (frameworks is not null && config.OutputFormat is not MetadataOutputFormat.Mref)
            {
                Logger.LogError(
                    $"'targetFrameworks' with more than one framework is only supported by the 'mref' output format, not '{config.OutputFormat}'.",
                    code: ErrorCodes.Metadata.TargetFrameworksNotSupported);
                return;
            }

            if (frameworks is not null && config.DefaultFramework is { } defaultFramework &&
                !frameworks.Values.Any(unit => string.Equals(TargetFrameworkAxis.SplitUnit(unit).Framework, defaultFramework, StringComparison.OrdinalIgnoreCase)))
            {
                Logger.LogWarning(
                    $"No project is documented for the default framework '{defaultFramework}', so the nearest framework each project targets provides the content.",
                    code: WarningCodes.Metadata.TargetFrameworkNotFound);
            }

            switch (config.OutputFormat)
            {
                case MetadataOutputFormat.Markdown:
                    CreatePages(WriteMarkdown, assemblies, config, options);

                    void WriteMarkdown(string outputFolder, string id, Build.ApiPage.ApiPage apiPage)
                    {
                        File.WriteAllText(Path.Combine(outputFolder, $"{id}.md"), Docfx.Build.ApiPage.ApiPageMarkdownTemplate.Render(apiPage));
                    }
                    break;

                case MetadataOutputFormat.ApiPage:
                    CreatePages(WriteYaml, assemblies, config, options);

                    void WriteYaml(string outputFolder, string id, Build.ApiPage.ApiPage apiPage)
                    {
                        var json = JsonSerializer.Serialize(apiPage, Docfx.Build.ApiPage.ApiPage.JsonSerializerOptions);
                        var obj = deserializer.Deserialize(json);
                        YamlUtility.Serialize(Path.Combine(outputFolder, $"{id}.yml"), obj, "YamlMime:ApiPage");
                    }
                    break;

                case MetadataOutputFormat.Mref:
                    CreateManagedReference(assemblies, config, options, frameworks);
                    break;
            }
        }
    }

    private static VariantIgnore ParseVariantIgnore(List<string> values)
    {
        var result = VariantIgnore.None;
        foreach (var value in values ?? [])
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "attributes":
                    result |= VariantIgnore.Attributes;
                    break;
                case "documentation":
                    result |= VariantIgnore.Documentation;
                    break;
                case "inheritdoc":
                    result |= VariantIgnore.InheritedDocumentation;
                    break;
                default:
                    Logger.LogWarning(
                        $"Unknown 'variantIgnore' value '{value}'. The supported values are 'attributes', 'documentation' and 'inheritdoc'.",
                        code: WarningCodes.Metadata.InvalidVariantIgnore);
                    break;
            }
        }

        return result;
    }

    private static ExtractMetadataConfig ConvertConfig(MetadataJsonItemConfig configModel, string configDirectory, string outputDirectory)
    {
        var projects = configModel.Src;
        var references = configModel.References;

        var outputFolder = Path.GetFullPath(Path.Combine(
            string.IsNullOrEmpty(outputDirectory) ? Path.Combine(configDirectory, configModel.Output ?? "") : outputDirectory,
            configModel.Dest ?? ""));

        var expandedFiles = GlobUtility.ExpandFileMapping(EnvironmentContext.BaseDirectory, projects);
        var expandedReferences = GlobUtility.ExpandFileMapping(EnvironmentContext.BaseDirectory, references);

        ExtractMetadataConfig.UseClrTypeNames = configModel?.UseClrTypeNames ?? false;

        // The patterns of an axis are relative to the same folders as the patterns that found the projects.
        var axis = TargetFrameworkAxis.Create(
            configModel?.Axes,
            (projects?.Items ?? []).Select(item => Path.GetFullPath(Path.Combine(EnvironmentContext.BaseDirectory, item.Src ?? ""))).Distinct());

        var targetFrameworks = configModel?.TargetFrameworks?
            .Where(framework => !string.IsNullOrWhiteSpace(framework))
            .Select(framework => framework.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() is { Count: > 0 } frameworks ? frameworks : null;

        return new ExtractMetadataConfig
        {
            ShouldSkipMarkup = configModel?.ShouldSkipMarkup ?? false,
            FilterConfigFile = configModel?.Filter is null ? null : Path.GetFullPath(Path.Combine(EnvironmentContext.BaseDirectory, configModel.Filter)),
            IncludePrivateMembers = configModel?.IncludePrivateMembers ?? false,
            IncludeExplicitInterfaceImplementations = configModel?.IncludeExplicitInterfaceImplementations ?? false,
            GlobalNamespaceId = configModel?.GlobalNamespaceId,
            MSBuildProperties = configModel?.Properties,
            TargetFramework = string.IsNullOrWhiteSpace(configModel?.TargetFramework) ? null : configModel.TargetFramework.Trim(),
            // An axis tells apart projects that would otherwise be documented as one, so every framework
            // of each of them is documented unless the frameworks are given.
            TargetFrameworks = targetFrameworks ?? (axis is null ? null : ["*"]),
            DefaultFramework = string.IsNullOrWhiteSpace(configModel?.DefaultFramework) ? null : configModel.DefaultFramework.Trim(),
            VariantIgnore = ParseVariantIgnore(configModel?.VariantIgnore),
            Axis = axis,
            OutputFormat = configModel?.OutputFormat ?? default,
            OutputFolder = outputFolder,
            CodeSourceBasePath = configModel?.CodeSourceBasePath,
            DisableDefaultFilter = configModel?.DisableDefaultFilter ?? false,
            DisableGitFeatures = configModel?.DisableGitFeatures ?? false,
            SourceLinkFilter = new(configModel?.SourceLinkExclude),
            NoRestore = configModel?.NoRestore ?? false,
            CategoryLayout = configModel?.CategoryLayout ?? default,
            NamespaceLayout = configModel?.NamespaceLayout ?? default,
            MemberLayout = configModel?.MemberLayout ?? default,
            EnumSortOrder = configModel?.EnumSortOrder ?? default,
            AllowCompilationErrors = configModel?.AllowCompilationErrors ?? false,
            Files = expandedFiles.Items.SelectMany(static s => s.Files).ToList(),
            References = expandedReferences?.Items.SelectMany(static s => s.Files).ToList()
        };
    }
}

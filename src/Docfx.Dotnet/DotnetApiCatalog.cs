// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docfx.Common;
using Docfx.Plugins;
using Microsoft.CodeAnalysis;
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

        var originalGlobalNamespaceId = VisitorHelper.GlobalNamespaceId;
        var originalAssemblyUidPrefixes = VisitorHelper.AssemblyUidPrefixes;
        var originalUidPrefix = VisitorHelper.UidPrefix;
        var originalUidPrefixAssemblies = VisitorHelper.UidPrefixAssemblies;

        try
        {
            EnvironmentContext.SetBaseDirectory(configDirectory);

            // A UID prefix is a property of the assembly, not of the metadata item that documents it,
            // so the maps of all metadata items are combined before any of them is processed. This keeps
            // references between metadata items resolvable regardless of the order they are declared in.
            VisitorHelper.AssemblyUidPrefixes = GetAssemblyUidPrefixes(config);

            foreach (var item in config)
            {
                VisitorHelper.GlobalNamespaceId = item.GlobalNamespaceId;
                EnvironmentContext.SetGitFeaturesDisabled(item.DisableGitFeatures);

                await Build(ConvertConfig(item, configDirectory, outputDirectory), options);
            }
        }
        finally
        {
            VisitorHelper.GlobalNamespaceId = originalGlobalNamespaceId;
            VisitorHelper.AssemblyUidPrefixes = originalAssemblyUidPrefixes;
            VisitorHelper.UidPrefix = originalUidPrefix;
            VisitorHelper.UidPrefixAssemblies = originalUidPrefixAssemblies;
            EnvironmentContext.Clean();
        }

        Logger.LogVerbose($".NET API done in {stopwatch.Elapsed}");

        async Task Build(ExtractMetadataConfig config, DotnetApiOptions options)
        {
            var assemblies = await Compile(config);

            // `uidPrefix` applies to the assemblies this metadata item documents, which are only
            // known once they are compiled. It stays constant for the whole item, so the parallel
            // API page generation can read it safely.
            VisitorHelper.UidPrefix = config.UidPrefix;
            VisitorHelper.UidPrefixAssemblies = string.IsNullOrEmpty(config.UidPrefix)
                ? null
                : new HashSet<IAssemblySymbol>(assemblies.Select(a => a.symbol), SymbolEqualityComparer.Default);

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
                    CreateManagedReference(assemblies, config, options);
                    break;
            }
        }
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex UidPrefixRegex();

    private static bool IsValidUidPrefix(string prefix)
    {
        return !string.IsNullOrEmpty(prefix) && UidPrefixRegex().IsMatch(prefix);
    }

    /// <summary>
    /// Combines the <c>assemblyUidPrefixes</c> maps of every metadata item into a single map.
    /// </summary>
    private static Dictionary<string, string> GetAssemblyUidPrefixes(MetadataJsonConfig config)
    {
        Dictionary<string, string> result = null;

        foreach (var item in config)
        {
            if (item.UidPrefix is not null && !IsValidUidPrefix(item.UidPrefix))
            {
                Logger.LogWarning(
                    $"Ignoring invalid UID prefix '{item.UidPrefix}'. A UID prefix must be a dot separated identifier, e.g. 'MyLib' or 'MyLib.V2'.",
                    code: "InvalidUidPrefix");
                item.UidPrefix = null;
            }

            if (item.AssemblyUidPrefixes is null)
            {
                continue;
            }

            foreach (var (assemblyName, prefix) in item.AssemblyUidPrefixes)
            {
                if (string.IsNullOrWhiteSpace(assemblyName))
                {
                    Logger.LogWarning("Ignoring 'assemblyUidPrefixes' entry with an empty assembly name.", code: "InvalidUidPrefix");
                    continue;
                }

                if (!IsValidUidPrefix(prefix))
                {
                    Logger.LogWarning(
                        $"Ignoring invalid UID prefix '{prefix}' for assembly '{assemblyName}'. A UID prefix must be a dot separated identifier, e.g. 'MyLib' or 'MyLib.V2'.",
                        code: "InvalidUidPrefix");
                    continue;
                }

                result ??= new(StringComparer.OrdinalIgnoreCase);

                if (result.TryGetValue(assemblyName, out var existingPrefix))
                {
                    if (existingPrefix != prefix)
                    {
                        Logger.LogWarning(
                            $"Assembly '{assemblyName}' is mapped to both UID prefix '{existingPrefix}' and '{prefix}', '{existingPrefix}' is used.",
                            code: "InvalidUidPrefix");
                    }
                    continue;
                }

                result.Add(assemblyName, prefix);
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

        return new ExtractMetadataConfig
        {
            ShouldSkipMarkup = configModel?.ShouldSkipMarkup ?? false,
            FilterConfigFile = configModel?.Filter is null ? null : Path.GetFullPath(Path.Combine(EnvironmentContext.BaseDirectory, configModel.Filter)),
            IncludePrivateMembers = configModel?.IncludePrivateMembers ?? false,
            IncludeExplicitInterfaceImplementations = configModel?.IncludeExplicitInterfaceImplementations ?? false,
            GlobalNamespaceId = configModel?.GlobalNamespaceId,
            UidPrefix = configModel?.UidPrefix,
            MSBuildProperties = configModel?.Properties,
            OutputFormat = configModel?.OutputFormat ?? default,
            OutputFolder = outputFolder,
            CodeSourceBasePath = configModel?.CodeSourceBasePath,
            DisableDefaultFilter = configModel?.DisableDefaultFilter ?? false,
            DisableGitFeatures = configModel?.DisableGitFeatures ?? false,
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

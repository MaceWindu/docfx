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
                await Exec(
                    value.ToObject<MetadataJsonConfig>(NewtonsoftJsonUtility.DefaultSerializer.Value),
                    options,
                    configDirectory,
                    assemblyUidPrefixes: config["assemblyUidPrefixes"]?.ToObject<Dictionary<string, string>>());
            }
        }
        finally
        {
            Logger.Flush();
            Logger.PrintSummary();
            Logger.UnregisterAllListeners();
        }
    }

    internal static async Task Exec(
        MetadataJsonConfig config,
        DotnetApiOptions options,
        string configDirectory,
        string outputDirectory = null,
        Dictionary<string, string> assemblyUidPrefixes = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        var originalGlobalNamespaceId = VisitorHelper.GlobalNamespaceId;
        var originalAssemblyUidPrefixes = VisitorHelper.AssemblyUidPrefixes;
        var originalUidPrefixOverride = VisitorHelper.UidPrefixOverride;
        var originalUidPrefixOverrideAssemblies = VisitorHelper.UidPrefixOverrideAssemblies;

        try
        {
            EnvironmentContext.SetBaseDirectory(configDirectory);

            // A UID prefix is a property of the assembly, not of the metadata item that documents it,
            // which is why the map is a project level setting: every item has to agree on the prefixes
            // for the references it makes into assemblies documented by another item to resolve.
            VisitorHelper.AssemblyUidPrefixes = ValidateAssemblyUidPrefixes(assemblyUidPrefixes);
            ValidateUidPrefixOverrides(config);

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
            VisitorHelper.UidPrefixOverride = originalUidPrefixOverride;
            VisitorHelper.UidPrefixOverrideAssemblies = originalUidPrefixOverrideAssemblies;
            EnvironmentContext.Clean();
        }

        Logger.LogVerbose($".NET API done in {stopwatch.Elapsed}");

        async Task Build(ExtractMetadataConfig config, DotnetApiOptions options)
        {
            var assemblies = await Compile(config);

            // `uidPrefixOverride` applies to the assemblies this metadata item documents, which are only
            // known once they are compiled. It stays constant for the whole item, so the parallel
            // API page generation can read it safely.
            VisitorHelper.UidPrefixOverride = config.UidPrefixOverride;
            VisitorHelper.UidPrefixOverrideAssemblies = string.IsNullOrEmpty(config.UidPrefixOverride)
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

    // A UID ends up as a file name, an xref key and an HTML anchor, so a prefix is restricted to the
    // characters that are safe in all three: letters, digits, underscores, and dots as separators.
    // Unlike a namespace, a segment may start with a digit, so target framework style prefixes such as
    // `net8.0` work.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)*$")]
    private static partial Regex UidPrefixRegex();

    private static bool IsValidUidPrefix(string prefix)
    {
        return !string.IsNullOrEmpty(prefix) && UidPrefixRegex().IsMatch(prefix);
    }

    /// <summary>
    /// Drops invalid entries from the project level <c>assemblyUidPrefixes</c> map and makes lookups by
    /// assembly name case insensitive, as assembly names are.
    /// </summary>
    private static Dictionary<string, string> ValidateAssemblyUidPrefixes(Dictionary<string, string> assemblyUidPrefixes)
    {
        if (assemblyUidPrefixes is null)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (assemblyName, prefix) in assemblyUidPrefixes)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                Logger.LogWarning("Ignoring 'assemblyUidPrefixes' entry with an empty assembly name.", code: "InvalidUidPrefix");
                continue;
            }

            if (!IsValidUidPrefix(prefix))
            {
                Logger.LogWarning(
                    $"Ignoring invalid UID prefix '{prefix}' for assembly '{assemblyName}'. A UID prefix must start with a letter or underscore and may contain letters, digits, underscores and dots, e.g. 'MyLib', 'MyLib.V2' or 'net8.0'.",
                    code: "InvalidUidPrefix");
                continue;
            }

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

        return result;
    }

    /// <summary>
    /// Drops each metadata item's <c>uidPrefixOverride</c> if it isn't a usable prefix.
    /// </summary>
    private static void ValidateUidPrefixOverrides(MetadataJsonConfig config)
    {
        foreach (var item in config)
        {
            if (item.UidPrefixOverride is not null && !IsValidUidPrefix(item.UidPrefixOverride))
            {
                Logger.LogWarning(
                    $"Ignoring invalid UID prefix '{item.UidPrefixOverride}'. A UID prefix must start with a letter or underscore and may contain letters, digits, underscores and dots, e.g. 'MyLib', 'MyLib.V2' or 'net8.0'.",
                    code: "InvalidUidPrefix");
                item.UidPrefixOverride = null;
            }
        }
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
            UidPrefixOverride = configModel?.UidPrefixOverride,
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

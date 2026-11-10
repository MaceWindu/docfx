// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Docfx.Common;
using Docfx.DataContracts.Common;
using Docfx.DataContracts.ManagedReference;
using Microsoft.CodeAnalysis;

#nullable enable

namespace Docfx.Dotnet;

partial class DotnetApiCatalog
{
    private static void CreateManagedReference(List<(IAssemblySymbol symbol, Compilation compilation)> assemblies, ExtractMetadataConfig config, DotnetApiOptions options, Dictionary<Compilation, string>? frameworks = null)
    {
        var projectMetadataList = new List<MetadataItem>();
        var filter = new SymbolFilter(config, options);

        // Symbols of different compilations cannot be used together, and each framework is a compilation
        // of its own, so extension methods and the documented assemblies are worked out per framework.
        // The first group provides the content of an API that several share, so the default value of an
        // axis goes first; the order is otherwise kept.
        List<List<(IAssemblySymbol symbol, Compilation compilation)>> frameworkGroups = frameworks is null
            ? [assemblies]
            : assemblies
                .GroupBy(a => frameworks[a.compilation], StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => config.Axis is { } axis && TargetFrameworkAxis.SplitUnit(group.Key).Value != axis.Default ? 1 : 0)
                .Select(group => group.ToList())
                .ToList();

        foreach (var group in frameworkGroups)
        {
            var extensionMethods = group.SelectMany(assembly => assembly.symbol.FindExtensionMethods(filter)).ToArray();
            var allAssemblies = new HashSet<IAssemblySymbol>(group.Select(a => a.symbol), SymbolEqualityComparer.Default);

            foreach (var (assembly, compilation) in group)
            {
                Logger.LogInfo($"Processing {assembly.Name}");
                var projectMetadata = assembly.Accept(new SymbolVisitorAdapter(
                    compilation, new(compilation, config.MemberLayout, allAssemblies), config, filter, extensionMethods));

                if (projectMetadata != null)
                {
                    if (frameworks is not null)
                    {
                        TagAppliesTo(projectMetadata, frameworks[compilation]);
                    }

                    projectMetadataList.Add(projectMetadata);
                }
            }
        }

        Logger.LogInfo($"Creating output...");
        var allMembers = new Dictionary<string, MetadataItem>();
        var allReferences = new Dictionary<string, ReferenceItem>();
        if (frameworks is null)
        {
            MergeMembers(allMembers, projectMetadataList);
        }
        else
        {
            MergeTargetFrameworks(allMembers, projectMetadataList, config.VariantIgnore);
        }

        MergeReferences(allReferences, projectMetadataList);

        if (allMembers.Count == 0)
        {
            if (assemblies.Count == 0)
            {
                Logger.LogWarning("No .NET API detected. Check the input files and earlier load or compilation diagnostics.");
            }
            else
            {
                var value = StringExtension.ToDelimitedString(assemblies.Select(a => a.symbol.Name));
                var filterConfig = config.FilterConfigFile is null ? "" : $" in '{config.FilterConfigFile}'";
                Logger.LogWarning($"No .NET API detected for {value}. Check that the input contains APIs to document, and review their visibility and the API filtering rules{filterConfig}.");
            }
            return;
        }

        ResolveAndExportYamlMetadata(allMembers, allReferences);

        return;


        void ResolveAndExportYamlMetadata(
        Dictionary<string, MetadataItem> allMembers, Dictionary<string, ReferenceItem> allReferences)
        {
            var outputFileNames = new Dictionary<string, int>(FilePathComparer.OSPlatformSensitiveStringComparer);
            var model = YamlMetadataResolver.ResolveMetadata(allMembers, allReferences, config.NamespaceLayout);

            // generate toc.yml
            model.TocYamlViewModel.Type = MemberType.Toc;

            var tocViewModel = new TocItemViewModel
            {
                Metadata = new() { ["memberLayout"] = config.MemberLayout },
                Items = model.TocYamlViewModel.ToTocViewModel(axis: config.Axis),
            };
            string tocFilePath = Path.Combine(config.OutputFolder, "toc.yml");

            YamlUtility.Serialize(tocFilePath, tocViewModel, YamlMime.TableOfContent);
            outputFileNames.Add(tocFilePath, 1);

            ApiReferenceViewModel indexer = [];

            // generate each item's yaml
            var members = model.Members;
            foreach (var memberModel in members)
            {
                var fileName = memberModel.Name.Replace('`', '-');
                var outputFileName = GetUniqueFileNameWithSuffix(fileName + Constants.YamlExtension, outputFileNames);
                string itemFilePath = Path.Combine(config.OutputFolder, outputFileName);
                var memberViewModel = memberModel.ToPageViewModel(config);
                memberViewModel.ShouldSkipMarkup = config.ShouldSkipMarkup;
                memberViewModel.MemberLayout = config.MemberLayout;
                YamlUtility.Serialize(itemFilePath, memberViewModel, YamlMime.ManagedReference);
                Logger.Log(LogLevel.Diagnostic, $"Metadata file for {memberModel.Name} is saved to {itemFilePath}.");
                AddMemberToIndexer(memberModel, outputFileName, indexer);
            }

            // generate manifest file
            JsonUtility.Serialize(Path.Combine(config.OutputFolder, ".manifest"), indexer, indented: true);
        }
    }

    private static string GetUniqueFileNameWithSuffix(string fileName, Dictionary<string, int> existingFileNames)
    {
        if (existingFileNames.TryGetValue(fileName, out int suffix))
        {
            existingFileNames[fileName] = suffix + 1;
            var newFileName = $"{fileName}_{suffix}";
            var extensionIndex = fileName.LastIndexOf('.');
            if (extensionIndex > -1)
            {
                newFileName = $"{fileName.Substring(0, extensionIndex)}_{suffix}.{fileName.Substring(extensionIndex + 1)}";
            }
            return GetUniqueFileNameWithSuffix(newFileName, existingFileNames);
        }
        else
        {
            existingFileNames[fileName] = 1;
            return fileName;
        }
    }

    private static void AddMemberToIndexer(MetadataItem memberModel, string outputPath, ApiReferenceViewModel indexer)
    {
        if (memberModel.Type == MemberType.Namespace)
        {
            indexer.Add(memberModel.Name, outputPath);
        }
        else
        {
            TreeIterator.Preorder(memberModel, null, s => s!.Items, (member, parent) =>
            {
                if (indexer.TryGetValue(member!.Name, out var path))
                {
                    Logger.LogWarning($"{member.Name} already exists in {path}, the duplicate one {outputPath} will be ignored.");
                }
                else
                {
                    indexer.Add(member.Name, outputPath);
                }
                return true;
            });
        }
    }

    private static void MergeMembers(Dictionary<string, MetadataItem> result, List<MetadataItem> items)
    {
        foreach (var item in items)
        {
            MergeNode(item);
        }

        bool MergeNode(MetadataItem node)
        {
            if (node.Type is MemberType.Assembly)
            {
                foreach (var item in node.Items ?? [])
                {
                    MergeNode(item);
                }
                return false;
            }

            if (!result.TryGetValue(node.Name, out var existingNode))
            {
                result.Add(node.Name, node);
                foreach (var item in node.Items ?? [])
                {
                    MergeNode(item);
                }
                return true;
            }

            if (node.Type is MemberType.Namespace or MemberType.Class)
            {
                foreach (var item in node.Items ?? [])
                {
                    if (MergeNode(item))
                    {
                        existingNode.Items ??= [];
                        existingNode.Items.Add(item);
                    }
                }
                return false;
            }

            Logger.Log(LogLevel.Warning, $"Ignore duplicated member {node.Type}:{node.Name} from {node.Source?.Path} as it already exists in {existingNode.Source?.Path}.");
            return false;
        }
    }

    internal static void TagAppliesTo(MetadataItem item, string framework)
    {
        item.AppliesTo = [framework];
        foreach (var child in item.Items ?? [])
        {
            TagAppliesTo(child, framework);
        }
    }

    /// <summary>
    /// Merges the same APIs compiled for several frameworks into one item per UID, recording the frameworks
    /// each one exists in. Items must be given primary framework first: the first occurrence of a UID
    /// provides its content.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="MergeMembers"/>, a repeated UID is expected here for every kind of member, and the
    /// children of every kind are unioned, as a struct or interface can gain members in one framework too.
    /// </remarks>
    internal static void MergeTargetFrameworks(Dictionary<string, MetadataItem> result, List<MetadataItem> items, VariantIgnore ignore = VariantIgnore.None)
    {
        foreach (var item in items)
        {
            MergeNode(item);
        }

        bool MergeNode(MetadataItem node)
        {
            if (node.Type is MemberType.Assembly)
            {
                foreach (var item in node.Items ?? [])
                {
                    MergeNode(item);
                }
                return false;
            }

            if (!result.TryGetValue(node.Name, out var existingNode))
            {
                result.Add(node.Name, node);
                foreach (var item in node.Items ?? [])
                {
                    MergeNode(item);
                }
                return true;
            }

            var framework = node.AppliesTo[0];
            var isSameFramework = existingNode.AppliesTo.Contains(framework, StringComparer.OrdinalIgnoreCase);
            if (isSameFramework && node.Type is not (MemberType.Namespace or MemberType.Class))
            {
                Logger.Log(LogLevel.Warning, $"Ignore duplicated member {node.Type}:{node.Name} from {node.Source?.Path} as it already exists in {existingNode.Source?.Path}.");
                return false;
            }

            if (!isSameFramework)
            {
                existingNode.AppliesTo.Add(framework);
                TargetFrameworkVariants.Merge(existingNode, node, framework, ignore);

                if (node.Type != existingNode.Type)
                {
                    Logger.LogWarning(
                        $"{node.Name} is a {node.Type} in '{framework}' but a {existingNode.Type} in '{existingNode.AppliesTo[0]}'. It is documented as a {existingNode.Type}.",
                        code: WarningCodes.Metadata.ApiKindDiffersBetweenFrameworks);
                }
            }

            foreach (var item in node.Items ?? [])
            {
                if (MergeNode(item))
                {
                    existingNode.Items ??= [];
                    existingNode.Items.Add(item);
                }
            }
            return false;
        }
    }

    private static void MergeReferences(Dictionary<string, ReferenceItem> result, List<MetadataItem> items)
    {
        foreach (var project in items)
        {
            if (project.References != null)
            {
                foreach (var pair in project.References)
                {
                    if (result.TryGetValue(pair.Key, out var value))
                    {
                        value.Merge(pair.Value);
                    }
                    else
                    {
                        result[pair.Key] = pair.Value;
                    }
                }
            }
        }
    }
}

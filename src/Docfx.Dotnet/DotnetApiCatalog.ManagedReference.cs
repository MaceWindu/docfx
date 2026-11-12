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
        // axis goes first, then the default framework and those nearest to it; the order is otherwise kept.
        var frameworkOrder = config.DefaultFramework is { } defaultFramework && frameworks is not null
            ? TargetFrameworkHelper.OrderByNearest(defaultFramework, frameworks.Values.Select(unit => TargetFrameworkAxis.SplitUnit(unit).Framework))
            : [];
        List<List<(IAssemblySymbol symbol, Compilation compilation)>> frameworkGroups = frameworks is null
            ? [assemblies]
            : assemblies
                .GroupBy(a => frameworks[a.compilation], StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => config.Axis is { } axis && TargetFrameworkAxis.SplitUnit(group.Key).Value != axis.Default ? 1 : 0)
                .ThenBy(group => frameworkOrder.FindIndex(f => string.Equals(f, TargetFrameworkAxis.SplitUnit(group.Key).Framework, StringComparison.OrdinalIgnoreCase)))
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

        ApplyAssemblyLabel(allMembers, config);

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
            if (config.Axis is { } axis)
            {
                tocViewModel.Metadata["axis"] = axis.ToTocMetadata();
            }

            string tocFilePath = Path.Combine(config.OutputFolder, "toc.yml");

            YamlUtility.Serialize(tocFilePath, tocViewModel, YamlMime.TableOfContent);
            outputFileNames.Add(tocFilePath, 1);

            ApiReferenceViewModel indexer = [];

            // generate each item's yaml
            var members = model.Members;
            foreach (var memberModel in members)
            {
                // Page level UIDs contain neither `#` nor `*`, so this only differs from replacing the
                // backticks by also mapping the `::` of the assembly component, which is not a legal
                // file name character. It has to match what `SymbolUrlResolver` builds hrefs from.
                var fileName = VisitorHelper.PathFriendlyId(memberModel.Name);
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

    /// <summary>
    /// Appends the assembly a namespace was declared in to the names it is displayed under, as
    /// <c>assemblyLabel</c> asks. It runs after the merge rather than in the symbol visitor because
    /// <see cref="AssemblyLabel.Shared"/> has to know which namespaces more than one of this item's
    /// assemblies declares, and a visitor only ever sees one assembly at a time.
    /// </summary>
    internal static void ApplyAssemblyLabel(Dictionary<string, MetadataItem> allMembers, ExtractMetadataConfig config)
    {
        if (config.AssemblyLabel is not (AssemblyLabel.Shared or AssemblyLabel.Suffix))
            return;

        var namespaces = allMembers.Values.Where(x => x.Type is MemberType.Namespace).ToList();

        // `allMembers` is keyed by uid, so two namespaces that trim to the same name are two distinct
        // declarations of it. That also covers a qualified assembly colliding with an unqualified one,
        // whose namespace carries no component and so is left alone below.
        var shared = config.AssemblyLabel is AssemblyLabel.Shared
            ? namespaces
                .GroupBy(x => VisitorHelper.TrimAssemblyUid(x.Name), StringComparer.Ordinal)
                .Where(x => x.Count() > 1)
                .SelectMany(x => x)
                .ToHashSet()
            : null;

        foreach (var @namespace in namespaces)
        {
            if (@namespace.AssemblyUid is not { } assemblyUid)
                continue;

            if (shared is not null && !shared.Contains(@namespace))
                continue;

            Append(@namespace.DisplayNames, assemblyUid);
            Append(@namespace.DisplayNamesWithType, assemblyUid);
            Append(@namespace.DisplayQualifiedNames, assemblyUid);
        }

        static void Append(SortedList<SyntaxLanguage, string> names, string assemblyUid)
        {
            foreach (var language in names.Keys.ToArray())
            {
                names[language] = $"{names[language]} ({assemblyUid})";
            }
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

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Docfx.Dotnet;

internal class ExtractMetadataConfig
{
    public List<string> Files { get; init; }

    public List<string> References { get; init; }

    public string OutputFolder { get; init; }

    public MetadataOutputFormat OutputFormat { get; init; }

    public bool ShouldSkipMarkup { get; init; }

    public string FilterConfigFile { get; init; }

    public bool IncludePrivateMembers { get; init; }

    public bool IncludeExplicitInterfaceImplementations { get; init; }

    public string GlobalNamespaceId { get; init; }

    public string CodeSourceBasePath { get; init; }

    public bool DisableDefaultFilter { get; init; }

    public bool DisableGitFeatures { get; init; }

    public SourceLinkFilter SourceLinkFilter { get; init; } = new(null);

    public bool NoRestore { get; init; }

    public CategoryLayout CategoryLayout { get; init; }

    public NamespaceLayout NamespaceLayout { get; init; }

    public MemberLayout MemberLayout { get; init; }

    public EnumSortOrder EnumSortOrder { get; init; }

    public Dictionary<string, string> MSBuildProperties { get; init; }

    public string TargetFramework { get; init; }

    public List<string> TargetFrameworks { get; init; }

    /// <summary>
    /// Whether <see cref="TargetFrameworks"/> is every framework because <c>axes</c> is set, not because
    /// <c>targetFrameworks</c> is, so that messages name the option the entry sets.
    /// </summary>
    public bool TargetFrameworksFromAxes { get; init; }

    public string DefaultFramework { get; init; }

    public VariantIgnore VariantIgnore { get; init; }

    public TargetFrameworkAxis Axis { get; init; }

    /// <summary>
    /// Whether <c>axes</c> is set but cannot be used, which has been reported: the entry is not documented, as
    /// documenting the projects of every value as one would be neither the axis nor what docfx does without it.
    /// </summary>
    public bool InvalidAxes { get; init; }

    public bool AllowCompilationErrors { get; init; }

    public static bool UseClrTypeNames { get; set; }

}

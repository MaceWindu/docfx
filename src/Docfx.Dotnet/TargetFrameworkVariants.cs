// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json;
using YamlDotNet.Serialization;

namespace Docfx.Dotnet;

/// <summary>
/// Records how the same API differs between the target frameworks it is documented for.
/// </summary>
internal static class TargetFrameworkVariants
{
    // Excluded rather than listed the other way round, so that a field added to the model is compared by
    // default. These identify the API, say where it was declared, or are combined instead of compared.
    private static readonly HashSet<string> NotCompared =
    [
        nameof(MetadataItem.Name),
        nameof(MetadataItem.CommentId),
        nameof(MetadataItem.DisplayNames),
        nameof(MetadataItem.DisplayNamesWithType),
        nameof(MetadataItem.DisplayQualifiedNames),
        nameof(MetadataItem.Parent),
        nameof(MetadataItem.Type),
        nameof(MetadataItem.AssemblyNameList),
        nameof(MetadataItem.NamespaceName),
        nameof(MetadataItem.Source),
        nameof(MetadataItem.Documentation),
        nameof(MetadataItem.Overload),
        nameof(MetadataItem.IsExplicitInterfaceImplementation),
        nameof(MetadataItem.IsExtensionMethod),
        nameof(MetadataItem.DerivedClasses),
        nameof(MetadataItem.Implements),
        nameof(MetadataItem.InheritedMembers),
        nameof(MetadataItem.ExtensionMethods),
        nameof(MetadataItem.Items),
        nameof(MetadataItem.References),
    ];

    private static readonly PropertyInfo[] Compared = typeof(MetadataItem)
        .GetProperties()
        .Where(p => p.GetCustomAttribute<YamlMemberAttribute>() is not null && !NotCompared.Contains(p.Name))
        .ToArray();

    /// <summary>
    /// Adds what <paramref name="other"/> has in <paramref name="framework"/> to <paramref name="primary"/>.
    /// </summary>
    public static void Merge(MetadataItem primary, MetadataItem other, string framework)
    {
        // A member or interface present in one framework belongs on the page whichever framework
        // provides the rest of it. An inheritance chain cannot be combined, so it is compared instead.
        primary.Implements = Union(primary.Implements, other.Implements);
        primary.InheritedMembers = Union(primary.InheritedMembers, other.InheritedMembers);
        primary.ExtensionMethods = Union(primary.ExtensionMethods, other.ExtensionMethods);

        var content = new MetadataItem();
        var differs = false;
        foreach (var property in Compared)
        {
            var value = property.GetValue(other);
            if (!AreEqual(property.GetValue(primary), value))
            {
                property.SetValue(content, value);
                differs = true;
            }
        }

        if (!differs)
        {
            return;
        }

        primary.Variants ??= [];
        if (primary.Variants.FirstOrDefault(variant => HasSameContent(variant.Content, content)) is { } existing)
        {
            existing.Frameworks.Add(framework);
        }
        else
        {
            primary.Variants.Add(new() { Frameworks = [framework], Content = content });
        }
    }

    private static bool HasSameContent(MetadataItem left, MetadataItem right)
    {
        return Compared.All(property => AreEqual(property.GetValue(left), property.GetValue(right)));
    }

    private static bool AreEqual(object left, object right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return left is string
            ? left.Equals(right)
            : JsonSerializer.Serialize(left, left.GetType()) == JsonSerializer.Serialize(right, right.GetType());
    }

    private static List<string> Union(List<string> first, List<string> second)
    {
        if (second is not { Count: > 0 })
        {
            return first;
        }

        return first is null ? second : first.Union(second).ToList();
    }
}

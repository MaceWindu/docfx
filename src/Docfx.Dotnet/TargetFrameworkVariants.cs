// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Docfx.DataContracts.ManagedReference;
using YamlDotNet.Serialization;

namespace Docfx.Dotnet;

/// <summary>
/// The kinds of difference between target frameworks that are not recorded as variants.
/// </summary>
[Flags]
internal enum VariantIgnore
{
    None = 0,

    /// <summary>
    /// Attributes, including the attribute lines of a declaration.
    /// </summary>
    Attributes = 1,

    /// <summary>
    /// Summaries, remarks, examples, descriptions, exceptions and see-also links.
    /// </summary>
    Documentation = 2,

    /// <summary>
    /// <see cref="Documentation"/>, for an API whose documentation is inherited through <c>inheritdoc</c>,
    /// explicitly or as an override or implementation without a comment of its own.
    /// </summary>
    InheritedDocumentation = 4,
}

/// <summary>
/// Records how the same API differs between the target frameworks it is documented for.
/// </summary>
internal static partial class TargetFrameworkVariants
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

    private static readonly (PropertyInfo Property, string Name)[] Compared = typeof(MetadataItem)
        .GetProperties()
        .Where(p => p.GetCustomAttribute<YamlMemberAttribute>() is not null && !NotCompared.Contains(p.Name))
        .Select(p => (p, p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name))
        .ToArray();

    // Text compared regardless of its whitespace, which follows the layout of the source comment.
    private static readonly HashSet<string> DocumentationText = ["summary", "remarks", "example", "description"];

    // Documentation that one framework has and another lacks is not a difference: it is most often inherited
    // from the reference assemblies of a framework, and some ship without documentation.
    private static readonly HashSet<string> Documentation = [.. DocumentationText, "exceptions", "seealso"];

    [GeneratedRegex(@"^[ \t]*(\[[^\]\r\n]*\]|<[^>\r\n]*>)[ \t]*(\r?\n|$)", RegexOptions.Multiline)]
    private static partial Regex AttributeLineRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Adds what <paramref name="other"/> has in <paramref name="framework"/> to <paramref name="primary"/>.
    /// </summary>
    public static void Merge(MetadataItem primary, MetadataItem other, string framework, VariantIgnore ignore = VariantIgnore.None)
    {
        // Inherited text most often comes from the reference assemblies of a framework, which word it for each
        // framework; either side inheriting is enough, as the other then documents the same API.
        if (ignore.HasFlag(VariantIgnore.InheritedDocumentation) && (primary.IsDocumentationInherited || other.IsDocumentationInherited))
        {
            ignore |= VariantIgnore.Documentation;
        }

        // A member or interface present in one framework belongs on the page whichever framework
        // provides the rest of it. An inheritance chain cannot be combined, so it is compared instead.
        primary.Implements = Union(primary.Implements, other.Implements);
        primary.InheritedMembers = Union(primary.InheritedMembers, other.InheritedMembers);
        primary.ExtensionMethods = Union(primary.ExtensionMethods, other.ExtensionMethods);
        // A framework can build a type into an assembly of its own name.
        primary.AssemblyNameList = Union(primary.AssemblyNameList, other.AssemblyNameList);

        // A member inherited in only some frameworks, as its base type differs, is marked with them on the page.
        foreach (var uid in other.InheritedMembers ?? [])
        {
            primary.InheritedMembersAppliesTo ??= [];
            if (primary.InheritedMembersAppliesTo.TryGetValue(uid, out var frameworks))
            {
                frameworks.Add(framework);
            }
            else
            {
                primary.InheritedMembersAppliesTo[uid] = [framework];
            }
        }

        if (string.IsNullOrWhiteSpace(primary.Summary))
        {
            primary.Summary = other.Summary;
        }

        if (string.IsNullOrWhiteSpace(primary.Remarks))
        {
            primary.Remarks = other.Remarks;
        }

        if (primary.Examples is not { Count: > 0 })
        {
            primary.Examples = other.Examples;
        }

        if (primary.Exceptions is not { Count: > 0 })
        {
            primary.Exceptions = other.Exceptions;
        }

        if (primary.SeeAlsos is not { Count: > 0 })
        {
            primary.SeeAlsos = other.SeeAlsos;
        }

        if (primary.Syntax is { } ownSyntax && other.Syntax is { } otherSyntax)
        {
            FillDescriptions(ownSyntax.Parameters, otherSyntax.Parameters);
            FillDescriptions(ownSyntax.TypeParameters, otherSyntax.TypeParameters);
            if (ownSyntax.Return is { } ownReturn && string.IsNullOrWhiteSpace(ownReturn.Description))
            {
                ownReturn.Description = otherSyntax.Return?.Description;
            }
        }

        // The base types and interfaces of a type are listed in the order of each framework's reference assemblies.
        var baseLists = primary.Type is MemberType.Class or MemberType.Struct or MemberType.Interface;

        var content = new MetadataItem();
        var differs = false;
        foreach (var (property, name) in Compared)
        {
            var value = property.GetValue(other);
            if (!AreEquivalent(property.GetValue(primary), value, name, ignore, baseLists))
            {
                property.SetValue(content, value);
                // A variant shows what the other framework has: one that only lacks something would show nothing.
                differs |= !IsEmpty(ToNode(value));
            }
        }

        if (!differs)
        {
            return;
        }

        if (content.Syntax is { } syntax && primary.Syntax is { } own)
        {
            content.Syntax = SyntaxThatDiffers(own, syntax, ignore);
        }

        primary.Variants ??= [];
        if (primary.Variants.FirstOrDefault(variant => HasSameContent(variant.Content, content, ignore, baseLists)) is { } existing)
        {
            existing.Frameworks.Add(framework);
        }
        else
        {
            primary.Variants.Add(new() { Frameworks = [framework], Content = content });
        }
    }

    /// <summary>
    /// The declaration, with the parameters, type parameters and return value only where they differ: a field
    /// whose constant differs would otherwise repeat a return type that is the same everywhere.
    /// </summary>
    private static SyntaxDetail SyntaxThatDiffers(SyntaxDetail own, SyntaxDetail other, VariantIgnore ignore)
    {
        return new()
        {
            Content = other.Content,
            Parameters = AreEquivalent(own.Parameters, other.Parameters, "parameters", ignore, baseLists: false) ? null : other.Parameters,
            TypeParameters = AreEquivalent(own.TypeParameters, other.TypeParameters, "typeParameters", ignore, baseLists: false) ? null : other.TypeParameters,
            Return = AreEquivalent(own.Return, other.Return, "return", ignore, baseLists: false) ? null : other.Return,
        };
    }

    /// <summary>
    /// Whether a framework can share a variant another framework has. Compared strictly: documentation one
    /// variant has and the other lacks is a difference here, or the framework would be shown the other's text.
    /// </summary>
    private static bool HasSameContent(MetadataItem left, MetadataItem right, VariantIgnore ignore, bool baseLists)
    {
        return Compared.All(field => AreEquivalent(field.Property.GetValue(left), field.Property.GetValue(right), field.Name, ignore, baseLists, strict: true));
    }

    /// <summary>
    /// Fills the descriptions <paramref name="own"/> lacks from the parameters of the same name in <paramref name="other"/>,
    /// as the summary and remarks are filled: the framework that has documentation provides it.
    /// </summary>
    private static void FillDescriptions(List<ApiParameter> own, List<ApiParameter> other)
    {
        if (own is null || other is null)
        {
            return;
        }

        foreach (var parameter in own)
        {
            if (string.IsNullOrWhiteSpace(parameter.Description) &&
                other.FirstOrDefault(o => o.Name == parameter.Name) is { Description: { } description } && !string.IsNullOrWhiteSpace(description))
            {
                parameter.Description = description;
            }
        }
    }

    private static bool AreEquivalent(object left, object right, string name, VariantIgnore ignore, bool baseLists, bool strict = false)
    {
        return ReferenceEquals(left, right) || AreEquivalent(ToNode(left), ToNode(right), name, ignore, inDeclaration: false, baseLists, strict);
    }

    private static JsonNode ToNode(object value)
    {
        return value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType());
    }

    private static bool AreEquivalent(JsonNode left, JsonNode right, string name, VariantIgnore ignore, bool inDeclaration, bool baseLists, bool strict)
    {
        if ((ignore.HasFlag(VariantIgnore.Attributes) && name == "attributes") ||
            (ignore.HasFlag(VariantIgnore.Documentation) && Documentation.Contains(name)))
        {
            return true;
        }

        if (IsEmpty(left) || IsEmpty(right))
        {
            return (IsEmpty(left) && IsEmpty(right)) || (!strict && Documentation.Contains(name));
        }

        switch (left)
        {
            case JsonObject leftObject when right is JsonObject rightObject:
                return leftObject.Select(p => p.Key).Union(rightObject.Select(p => p.Key)).All(key =>
                    AreEquivalent(leftObject[key], rightObject[key], key, ignore, inDeclaration: name == "content", baseLists, strict));

            case JsonArray leftArray when right is JsonArray rightArray:
                return leftArray.Count == rightArray.Count
                    && leftArray.Zip(rightArray).All(pair => AreEquivalent(pair.First, pair.Second, name, ignore, inDeclaration, baseLists, strict));

            case JsonValue when right is JsonValue:
                if (left.GetValueKind() is JsonValueKind.String && right.GetValueKind() is JsonValueKind.String)
                {
                    if (inDeclaration)
                    {
                        var leftDeclaration = left.GetValue<string>();
                        var rightDeclaration = right.GetValue<string>();
                        if (ignore.HasFlag(VariantIgnore.Attributes))
                        {
                            leftDeclaration = AttributeLineRegex().Replace(leftDeclaration, "");
                            rightDeclaration = AttributeLineRegex().Replace(rightDeclaration, "");
                        }

                        // The reference assemblies of each framework list the interfaces of a type in their own order.
                        return baseLists
                            ? SortBaseList(leftDeclaration, name) == SortBaseList(rightDeclaration, name)
                            : leftDeclaration == rightDeclaration;
                    }

                    // Line breaks and indentation of documentation follow the layout of the source comment,
                    // which is rewrapped between versions without changing what it says.
                    if (DocumentationText.Contains(name))
                    {
                        var leftText = WhitespaceRegex().Replace(left.GetValue<string>(), "");
                        var rightText = WhitespaceRegex().Replace(right.GetValue<string>(), "");
                        return leftText == rightText || (!strict && (leftText.Length == 0 || rightText.Length == 0));
                    }
                }

                return left.ToJsonString() == right.ToJsonString();

            default:
                return false;
        }
    }

    /// <summary>
    /// Sorts the base types and interfaces a type declaration lists: after <c>:</c> up to a <c>where</c>
    /// clause in C#, after <c>Implements</c>, or <c>Inherits</c> for an interface, in Visual Basic. Other
    /// declarations are returned unchanged.
    /// </summary>
    internal static string SortBaseList(string declaration, string language)
    {
        var isVisualBasic = string.Equals(language, nameof(SyntaxLanguage.VB), StringComparison.OrdinalIgnoreCase);
        // A Visual Basic interface lists the interfaces it extends after Inherits, a class after Implements.
        var separator = !isVisualBasic ? " : "
            : declaration.Contains(" Implements ", StringComparison.Ordinal) ? " Implements " : " Inherits ";
        var depth = 0;
        int start = -1, end = declaration.Length;
        var commas = new List<int>();

        for (var i = 0; i < declaration.Length; i++)
        {
            switch (declaration[i])
            {
                case '<' or '(' or '[' or '{':
                    depth++;
                    continue;
                case '>' or ')' or ']' or '}':
                    depth--;
                    continue;
            }

            if (depth != 0)
            {
                continue;
            }

            if (start < 0)
            {
                if (string.CompareOrdinal(declaration, i, separator, 0, separator.Length) == 0)
                {
                    // A `where` before the first colon makes it a constraint, not a base list.
                    if (!isVisualBasic && declaration.AsSpan(0, i).Contains(" where ", StringComparison.Ordinal))
                    {
                        return declaration;
                    }

                    start = i + separator.Length;
                    i = start - 1;
                }
            }
            else if (declaration[i] == ',')
            {
                commas.Add(i);
            }
            else if (!isVisualBasic && string.CompareOrdinal(declaration, i, " where ", 0, 7) == 0)
            {
                end = i;
                break;
            }
        }

        if (start < 0 || commas.Count == 0)
        {
            return declaration;
        }

        var items = new List<string>();
        var from = start;
        foreach (var comma in commas)
        {
            items.Add(declaration[from..comma].Trim());
            from = comma + 1;
        }

        items.Add(declaration[from..end].Trim());
        items.Sort(StringComparer.Ordinal);
        return string.Concat(declaration.AsSpan(0, start), string.Join(", ", items), declaration.AsSpan(end));
    }

    private static bool IsEmpty(JsonNode node)
    {
        return node switch
        {
            null => true,
            JsonArray array => array.Count == 0,
            JsonObject obj => obj.Count == 0,
            JsonValue value => value.GetValueKind() is JsonValueKind.String && value.GetValue<string>().Length == 0,
            _ => false,
        };
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

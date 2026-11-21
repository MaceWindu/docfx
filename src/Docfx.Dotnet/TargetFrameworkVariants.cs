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
    /// The attribute lines of a declaration.
    /// </summary>
    Attributes = 1,

    /// <summary>
    /// Summaries, remarks, examples, descriptions, exceptions and see-also links.
    /// </summary>
    Documentation = 2,

    /// <summary>
    /// <see cref="Documentation"/>, between two frameworks that both inherit the summary of an API through
    /// <c>inheritdoc</c>, explicitly or as an override or implementation without a comment of its own.
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
        // No template shows them but in the declaration, which is compared: an attribute whose constructor takes an
        // `int` in one framework and a `long` in another is written the same there, and would be a variant that
        // shows nothing.
        nameof(MetadataItem.Attributes),
    ];

    // Templates show the declaration in C#, the first language an item lists.
    private const string ShownLanguage = nameof(SyntaxLanguage.CSharp);

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

    // A line of a declaration that starts with `[` and ends with `]` (`<` and `>` in Visual Basic) is an attribute,
    // whatever its arguments hold, such as `[Obsolete("Use T[] instead")]` or `<Obsolete("a > b")>`.
    [GeneratedRegex(@"^[ \t]*(\[[^\r\n]*\]|<[^\r\n]*>)[ \t]*(\r?\n|$)", RegexOptions.Multiline)]
    private static partial Regex AttributeLineRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Adds what <paramref name="other"/> has in <paramref name="framework"/> to <paramref name="primary"/>.
    /// </summary>
    public static void Merge(MetadataItem primary, MetadataItem other, string framework, VariantIgnore ignore = VariantIgnore.None)
    {
        // Inherited text most often comes from the reference assemblies of a framework, which word it for each
        // framework. Text of its own in either framework is the API's, and is compared.
        var sharedIgnore = ignore;
        if (ignore.HasFlag(VariantIgnore.InheritedDocumentation) && primary.IsDocumentationInherited && other.IsDocumentationInherited)
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
        // The cross-references in the text of the other framework, which fills what this one lacks or a variant
        // shows, are resolved for the page as its own are: only their keys are recorded until then.
        foreach (var key in other.References?.Keys ?? Enumerable.Empty<string>())
        {
            primary.References ??= [];
            primary.References.TryAdd(key, null);
        }

        // A member inherited in only some frameworks, as its base type differs, is marked with them on the page,
        // and so is a type implementing an interface in only some, on the page of the interface.
        primary.InheritedMembersAppliesTo = AddFramework(primary.InheritedMembersAppliesTo, other.InheritedMembers, framework);
        primary.ImplementsAppliesTo = AddFramework(primary.ImplementsAppliesTo, other.Implements, framework);

        if (string.IsNullOrWhiteSpace(primary.Summary))
        {
            // The summary shown is now that framework's: a later framework that inherits its own is compared with
            // it only if it is inherited too, or that framework would be shown text of another framework's own.
            if (!string.IsNullOrWhiteSpace(other.Summary))
            {
                primary.IsDocumentationInherited = other.IsDocumentationInherited;
            }

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
            content.Syntax = SyntaxThatDiffers(own, syntax, ignore, baseLists);
        }

        // Shared with the ignore every framework has, not the one widened for this framework, which a framework
        // recorded with its own text would match: whatever order they are merged in, a framework is never shown
        // another one's text.
        // An override the other framework lacks leaves `Overridden` unset, which reads as "the same".
        var noOverridden = primary.Overridden is not null && other.Overridden is null;
        primary.Variants ??= [];
        if (primary.Variants.FirstOrDefault(variant => variant.NoOverridden == noOverridden && HasSameContent(variant.Content, content, sharedIgnore, baseLists)) is { } existing)
        {
            existing.Frameworks.Add(framework);
        }
        else
        {
            primary.Variants.Add(new() { Frameworks = [framework], Content = content, NoOverridden = noOverridden });
        }
    }

    /// <summary>
    /// The declaration, parameters, type parameters and return value only where they differ: a field whose
    /// constant differs would otherwise repeat a return type that is the same everywhere, and a parameter
    /// described differently a declaration that is the same. As what is the same is left out, parameters or a
    /// return value the API has and the variant lacks are written empty: no parameters, and a return value
    /// without a type, so that a template showing the variant whole does not take the API's.
    /// </summary>
    private static SyntaxDetail SyntaxThatDiffers(SyntaxDetail own, SyntaxDetail other, VariantIgnore ignore, bool baseLists)
    {
        return new()
        {
            Content = AreEquivalent(own.Content, other.Content, "content", ignore, baseLists) ? null : other.Content,
            Parameters = AreEquivalent(own.Parameters, other.Parameters, "parameters", ignore, baseLists: false)
                ? null
                : other.Parameters ?? [],
            TypeParameters = AreEquivalent(own.TypeParameters, other.TypeParameters, "typeParameters", ignore, baseLists: false) ? null : other.TypeParameters,
            Return = AreEquivalent(own.Return, other.Return, "return", ignore, baseLists: false)
                ? null
                : other.Return ?? new ApiParameter(),
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

    // An attribute argument can be `double.NaN` or an infinity, which strict number handling cannot write.
    private static readonly JsonSerializerOptions NodeOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private static JsonNode ToNode(object value)
    {
        return value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), NodeOptions);
    }

    private static bool AreEquivalent(JsonNode left, JsonNode right, string name, VariantIgnore ignore, bool inDeclaration, bool baseLists, bool strict)
    {
        if (ignore.HasFlag(VariantIgnore.Documentation) && Documentation.Contains(name))
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
                return leftObject.Select(p => p.Key).Union(rightObject.Select(p => p.Key))
                    // No page shows the attributes of a parameter or return value, which differ wherever one
                    // framework has an attribute such as `NotNullWhen` and another lacks or hides it: the variant
                    // would repeat a declaration and parameters that are the same.
                    .Where(key => !(key == "attributes" && name is "parameters" or "typeParameters" or "return"))
                    // Pages show the declaration in one language: one that differs only in Visual Basic would be a
                    // variant that repeats the same C# declaration.
                    .Where(key => !(name == "content" && key != ShownLanguage && leftObject.ContainsKey(ShownLanguage) && rightObject.ContainsKey(ShownLanguage)))
                    .All(key => AreEquivalent(leftObject[key], rightObject[key], key, ignore, inDeclaration: name == "content", baseLists, strict));

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

        // The attribute lines come first, and an argument of theirs can hold a `<` or `(` that is no bracket, as
        // `[Range(0, 1 < 2 ? 1 : 2)]` does: the base list is on a line after them.
        var first = 0;
        for (var line = AttributeLineRegex().Match(declaration); line.Success && line.Index == first; line = line.NextMatch())
        {
            first = line.Index + line.Length;
        }

        for (var i = first; i < declaration.Length; i++)
        {
            switch (declaration[i])
            {
                // A string or character in an attribute argument may hold any bracket. In Visual Basic, a
                // character is a string with a `c` after it.
                case '"':
                case '\'' when !isVisualBasic:
                    i = EndOfLiteral(i);
                    continue;
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

        // The index of the quote that closes the literal opened at `open`: C# escapes with a backslash,
        // Visual Basic doubles the quote.
        int EndOfLiteral(int open)
        {
            var quote = declaration[open];
            for (var j = open + 1; j < declaration.Length; j++)
            {
                if (!isVisualBasic && declaration[j] == '\\')
                {
                    j++;
                }
                else if (declaration[j] == quote)
                {
                    if (isVisualBasic && j + 1 < declaration.Length && declaration[j + 1] == quote)
                    {
                        j++;
                        continue;
                    }

                    return j;
                }
            }

            return declaration.Length;
        }
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

    private static Dictionary<string, List<string>> AddFramework(Dictionary<string, List<string>> frameworksOf, List<string> uids, string framework)
    {
        foreach (var uid in uids ?? [])
        {
            frameworksOf ??= [];
            if (frameworksOf.TryGetValue(uid, out var frameworks))
            {
                frameworks.Add(framework);
            }
            else
            {
                frameworksOf[uid] = [framework];
            }
        }

        return frameworksOf;
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

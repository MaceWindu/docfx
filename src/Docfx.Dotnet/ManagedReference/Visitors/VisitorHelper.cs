// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.RegularExpressions;
using Docfx.Common;
using Docfx.Common.Git;
using Docfx.DataContracts.Common;
using Docfx.DataContracts.ManagedReference;
using Docfx.Plugins;
using Microsoft.CodeAnalysis;

namespace Docfx.Dotnet;

internal static partial class VisitorHelper
{
    public static string GlobalNamespaceId { get; set; }

    /// <summary>
    /// Maps an assembly name to the prefix prepended to the UID of every API declared in that assembly.
    /// This is assigned once before metadata generation starts and is only read afterwards,
    /// so it is safe to read from the parallel API page generation.
    /// </summary>
    public static IReadOnlyDictionary<string, string> AssemblyUidPrefixes { get; set; }

    /// <summary>
    /// The prefix prepended to the UID of every API declared in <see cref="UidPrefixAssemblies"/>.
    /// It takes precedence over <see cref="AssemblyUidPrefixes"/>, which lets metadata items that
    /// document assemblies sharing an assembly name give them distinct UIDs.
    /// This is assigned once per metadata item, before its APIs are generated.
    /// </summary>
    public static string UidPrefix { get; set; }

    /// <summary>
    /// The assemblies <see cref="UidPrefix"/> applies to, i.e. the assemblies documented by the
    /// metadata item that is currently being processed.
    /// </summary>
    public static HashSet<IAssemblySymbol> UidPrefixAssemblies { get; set; }

    private static bool IsUidPrefixConfigured => AssemblyUidPrefixes is { Count: > 0 } || !string.IsNullOrEmpty(UidPrefix);

    [GeneratedRegex(@"``\d+$")]
    private static partial Regex GenericMethodPostFix();

    public static string PathFriendlyId(string id)
    {
        return id.Replace('`', '-').Replace('#', '-').Replace("*", "");
    }

    public static string GetId(ISymbol symbol)
    {
        return GetId(symbol, applyUidPrefix: true);
    }

    /// <summary>
    /// Gets the id of a symbol without applying any configured UID prefix.
    /// API filters use this so that filter rules keep matching the actual API surface
    /// regardless of the configured UID prefixes.
    /// </summary>
    public static string GetRawId(ISymbol symbol)
    {
        return GetId(symbol, applyUidPrefix: false);
    }

    private static string GetId(ISymbol symbol, bool applyUidPrefix)
    {
        if (symbol == null)
        {
            return null;
        }

        if (symbol is INamespaceSymbol { IsGlobalNamespace: true })
        {
            return GlobalNamespaceId;
        }

        if (symbol is IAssemblySymbol assemblySymbol)
        {
            return assemblySymbol.MetadataName;
        }

        if (symbol is IDynamicTypeSymbol)
        {
            return "dynamic";
        }

        var id = GetDocumentationCommentId(symbol, applyUidPrefix)?.Substring(2);

        if ((id is null) && (symbol is IFunctionPointerTypeSymbol functionPointerTypeSymbol))
        {
            // Roslyn doesn't currently support doc comments for function pointer type symbols
            // This returns just the stringified symbol to ensure the source and target parts
            // match for reference item merging.

            return functionPointerTypeSymbol.ToString();
        }

        return id;
    }

    private static string GetDocumentationCommentId(ISymbol symbol, bool applyUidPrefix = true)
    {
        string str = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(str))
        {
            return null;
        }

        if (InGlobalNamespace(symbol) && !string.IsNullOrEmpty(GlobalNamespaceId))
        {
            bool isNamespace = symbol is INamespaceSymbol;
            bool isTypeParameter = symbol is ITypeParameterSymbol;
            if (!isNamespace && !isTypeParameter)
            {
                str = str.Insert(2, GlobalNamespaceId + ".");
            }
        }

        if (applyUidPrefix && GetUidPrefix(symbol) is { } uidPrefix)
        {
            str = str.Insert(2, uidPrefix + ".");
        }

        return str;
    }

    /// <summary>
    /// Gets the configured UID prefix of the assembly that declares <paramref name="symbol"/>,
    /// or <see langword="null"/> when the symbol is not prefixed.
    /// </summary>
    public static string GetUidPrefix(ISymbol symbol)
    {
        if (symbol is null || !IsUidPrefixConfigured)
        {
            return null;
        }

        // Type parameters are scoped to their declaring API, prefixing them would break the
        // `` `0 `` / ` ``0 ` references used by the documentation comment id format.
        if (symbol is ITypeParameterSymbol)
        {
            return null;
        }

        // ContainingAssembly is null for symbols that don't belong to a single assembly,
        // e.g. merged namespaces or some symbols reached through cref resolution.
        if (symbol.ContainingAssembly is not { } assembly)
        {
            return null;
        }

        // The prefix of the metadata item being processed wins, so that assemblies sharing an
        // assembly name can still be told apart by the item that documents each of them.
        if (!string.IsNullOrEmpty(UidPrefix) && UidPrefixAssemblies is { } assemblies && assemblies.Contains(assembly))
        {
            return UidPrefix;
        }

        return AssemblyUidPrefixes is { } prefixes && prefixes.TryGetValue(assembly.Name, out var prefix) ? prefix : null;
    }

    /// <summary>
    /// Gets the configured UID prefix of the API a documentation comment id (i.e. a cref) points to,
    /// or <see langword="null"/> when the target is not prefixed or cannot be resolved.
    /// </summary>
    public static string GetUidPrefixForCommentId(string commentId, Compilation compilation)
    {
        if (string.IsNullOrEmpty(commentId) || !IsUidPrefixConfigured)
        {
            return null;
        }

        // `Overload:` is a docfx concept, Roslyn only knows about declaration id kinds.
        const string overloadPrefix = "Overload:";
        if (commentId.StartsWith(overloadPrefix, StringComparison.Ordinal))
        {
            var body = commentId[overloadPrefix.Length..];
            return GetUidPrefix(ResolveDeclarationId($"M:{body}"))
                ?? GetUidPrefix(ResolveDeclarationId($"P:{body}"));
        }

        return GetUidPrefix(ResolveDeclarationId(commentId));

        ISymbol ResolveDeclarationId(string id) => DocumentationCommentId.GetFirstSymbolForDeclarationId(id, compilation);
    }

    public static string GetCommentId(ISymbol symbol)
    {
        if (symbol == null || symbol is IAssemblySymbol)
        {
            return null;
        }

        if (symbol is IDynamicTypeSymbol)
        {
            return "T:" + typeof(object).FullName;
        }

        return GetDocumentationCommentId(symbol);
    }

    public static string GetOverloadId(ISymbol symbol)
    {
        return GetOverloadIdBody(symbol) + "*";
    }

    public static string GetOverloadIdBody(ISymbol symbol)
    {
        var id = GetId(symbol);
        var uidBody = id;
        {
            var index = uidBody.IndexOf('(');
            if (index != -1)
            {
                uidBody = uidBody.Remove(index);
            }
        }
        uidBody = GenericMethodPostFix().Replace(uidBody, string.Empty);
        return uidBody;
    }

    public static ApiParameter GetParameterDescription(ISymbol symbol, MetadataItem item, string id, bool isReturn)
    {
        string comment = isReturn ? item.CommentModel?.Returns : item.CommentModel?.GetParameter(symbol.Name);
        return new ApiParameter
        {
            Name = isReturn ? null : symbol.Name,
            Type = id,
            Description = comment,
        };
    }

    public static ApiParameter GetTypeParameterDescription(ITypeParameterSymbol symbol, MetadataItem item)
    {
        string comment = item.CommentModel?.GetTypeParameter(symbol.Name);
        return new ApiParameter
        {
            Name = symbol.Name,
            Description = comment,
        };
    }

    public static SourceDetail GetSourceDetail(ISymbol symbol, Compilation compilation)
    {
        // For namespace, definition is meaningless
        if (symbol == null || symbol.Kind == SymbolKind.Namespace)
        {
            return null;
        }

        var syntaxRef = symbol.DeclaringSyntaxReferences.LastOrDefault();
        if (symbol.IsExtern || syntaxRef == null)
        {
            if (SymbolUrlResolver.GetPdbSourceLinkUrl(compilation, symbol) is string url)
            {
                return new() { Href = url };
            }

            return null;
        }

        var syntaxNode = syntaxRef.GetSyntax();
        Debug.Assert(syntaxNode != null);
        if (syntaxNode != null)
        {
            var source = new SourceDetail
            {
                StartLine = syntaxNode.SyntaxTree.GetLineSpan(syntaxNode.Span).StartLinePosition.Line,
                Path = syntaxNode.SyntaxTree.FilePath,
                Name = symbol.Name
            };

            source.Remote = GitUtility.TryGetFileDetail(source.Path);
            if (source.Remote != null)
            {
                source.Path = PathUtility.FormatPath(source.Path, UriKind.Relative, EnvironmentContext.BaseDirectory);
            }
            return source;
        }

        return null;
    }

    public static MemberType GetMemberTypeFromTypeKind(TypeKind typeKind)
    {
        switch (typeKind)
        {
            case TypeKind.Module:
            case TypeKind.Class:
                return MemberType.Class;
            case TypeKind.Enum:
                return MemberType.Enum;
            case TypeKind.Interface:
                return MemberType.Interface;
            case TypeKind.Struct:
                return MemberType.Struct;
            case TypeKind.Delegate:
                return MemberType.Delegate;
            case TypeKind.Extension:
                return MemberType.Extension;
            default:
                return MemberType.Default;
        }
    }

    public static bool InGlobalNamespace(ISymbol symbol)
    {
        Debug.Assert(symbol != null);

        return symbol.ContainingNamespace == null || symbol.ContainingNamespace.IsGlobalNamespace;
    }
}

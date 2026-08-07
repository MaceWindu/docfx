// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Docfx.DataContracts.ManagedReference;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Docfx.Dotnet.Tests;

/// <summary>
/// Tests for the <c>uidPrefixes</c> metadata option, which prefixes the UID of every API declared
/// in a given assembly so that assemblies sharing a namespace don't collide.
/// </summary>
[Collection("docfx STA")]
public class UidPrefixUnitTest : IDisposable
{
    private static readonly Dictionary<string, string> EmptyMSBuildProperties = [];

    private const string SharedLibraryCode =
        """
        namespace Shared;

        /// <summary>A widget.</summary>
        public class Widget
        {
            /// <summary>Does something.</summary>
            public void Do() { }

            /// <summary>Does something else.</summary>
            public void Do(int value) { }
        }
        """;

    public void Dispose()
    {
        VisitorHelper.UidPrefixes = null;
        VisitorHelper.GlobalNamespaceId = null;
    }

    private static void UsePrefixes(params (string assembly, string prefix)[] prefixes)
    {
        VisitorHelper.UidPrefixes = prefixes.ToDictionary(x => x.assembly, x => x.prefix, StringComparer.OrdinalIgnoreCase);
    }

    private static MetadataItem Verify(string code, string assemblyName = "test.dll", ExtractMetadataConfig config = null, params MetadataReference[] references)
    {
        var compilation = CompilationHelper.CreateCompilationFromCSharpCode(code, EmptyMSBuildProperties, assemblyName, references);
        Assert.Empty(compilation.GetDeclarationDiagnostics());
        return compilation.Assembly.GenerateMetadataItem(compilation, config);
    }

    /// <summary>
    /// Compiles <paramref name="code"/> into an in-memory assembly that can be referenced by another compilation.
    /// </summary>
    private static MetadataReference CreateReference(string code, string assemblyName)
    {
        var compilation = CompilationHelper.CreateCompilationFromCSharpCode(code, EmptyMSBuildProperties, assemblyName);
        Assert.Empty(compilation.GetDeclarationDiagnostics());
        return compilation.ToMetadataReference();
    }

    [Fact]
    public void UidsAreUnchangedWhenNoPrefixIsConfigured()
    {
        var output = Verify(SharedLibraryCode);

        var @namespace = output.Items[0];
        Assert.Equal("Shared", @namespace.Name);
        Assert.Equal("N:Shared", @namespace.CommentId);
        Assert.Equal("Shared", @namespace.DisplayNames[SyntaxLanguage.CSharp]);
        Assert.Equal("Shared.Widget", @namespace.Items[0].Name);
    }

    [Fact]
    public void NamespaceTypeAndMemberUidsArePrefixed()
    {
        UsePrefixes(("test.dll", "Pkg"));

        var output = Verify(SharedLibraryCode);

        var @namespace = output.Items[0];
        Assert.Equal("Pkg.Shared", @namespace.Name);
        Assert.Equal("N:Pkg.Shared", @namespace.CommentId);

        var type = @namespace.Items[0];
        Assert.Equal("Pkg.Shared.Widget", type.Name);
        Assert.Equal("T:Pkg.Shared.Widget", type.CommentId);
        Assert.Equal("Pkg.Shared", type.NamespaceName);

        var method = type.Items.First(i => i.Name.Contains("Do(System.Int32)"));
        Assert.Equal("Pkg.Shared.Widget.Do(System.Int32)", method.Name);
        Assert.Equal("M:Pkg.Shared.Widget.Do(System.Int32)", method.CommentId);
        Assert.Equal("Pkg.Shared.Widget.Do*", method.Overload);
    }

    [Fact]
    public void NamespaceDisplayNameShowsThePrefix()
    {
        UsePrefixes(("test.dll", "Pkg"));

        var @namespace = Verify(SharedLibraryCode).Items[0];

        // The prefix has to be visible, otherwise the same namespace coming from two assemblies
        // is indistinguishable in the TOC, breadcrumbs and page titles.
        Assert.Equal("Pkg.Shared", @namespace.DisplayNames[SyntaxLanguage.CSharp]);
        Assert.Equal("Pkg.Shared", @namespace.DisplayNamesWithType[SyntaxLanguage.CSharp]);
        Assert.Equal("Pkg.Shared", @namespace.DisplayQualifiedNames[SyntaxLanguage.CSharp]);

        // Type display names are unaffected.
        Assert.Equal("Widget", @namespace.Items[0].DisplayNames[SyntaxLanguage.CSharp]);
    }

    [Fact]
    public void TwoAssembliesSharingANamespaceGetDistinctUids()
    {
        UsePrefixes(("a.dll", "A"), ("b.dll", "B"));

        var a = Verify(SharedLibraryCode, "a.dll");
        var b = Verify(SharedLibraryCode, "b.dll");

        Assert.Equal("A.Shared", a.Items[0].Name);
        Assert.Equal("B.Shared", b.Items[0].Name);
        Assert.Equal("A.Shared.Widget", a.Items[0].Items[0].Name);
        Assert.Equal("B.Shared.Widget", b.Items[0].Items[0].Name);
    }

    [Fact]
    public void CrossAssemblyReferencesUseTheTargetAssemblyPrefix()
    {
        UsePrefixes(("a.dll", "A"), ("b.dll", "B"));

        var reference = CreateReference(SharedLibraryCode, "a.dll");
        var output = Verify(
            """
            namespace Shared;

            /// <summary>A gadget.</summary>
            public class Gadget : Widget { }
            """,
            "b.dll",
            references: reference);

        var type = output.Items[0].Items[0];
        Assert.Equal("B.Shared.Gadget", type.Name);

        // The base type lives in a.dll, so it must carry A's prefix, not B's.
        Assert.Equal(["System.Object", "A.Shared.Widget"], type.Inheritance);
    }

    [Fact]
    public void AssembliesWithoutAPrefixAreLeftUnchanged()
    {
        UsePrefixes(("b.dll", "B"));

        var reference = CreateReference(SharedLibraryCode, "a.dll");
        var output = Verify(
            """
            namespace Other;

            /// <summary>A gadget.</summary>
            public class Gadget : Shared.Widget { }
            """,
            "b.dll",
            references: reference);

        var type = output.Items[0].Items[0];
        Assert.Equal("B.Other.Gadget", type.Name);

        // Neither the unmapped assembly nor the framework is prefixed.
        Assert.Equal(["System.Object", "Shared.Widget"], type.Inheritance);
    }

    [Fact]
    public void CrefsToPrefixedAssembliesAreResolved()
    {
        UsePrefixes(("a.dll", "A"), ("b.dll", "B"));

        var reference = CreateReference(SharedLibraryCode, "a.dll");
        var output = Verify(
            """
            namespace Other;

            /// <summary>Wraps a <see cref="Shared.Widget"/>.</summary>
            /// <seealso cref="Shared.Widget"/>
            /// <seealso cref="System.String"/>
            public class Gadget
            {
                /// <summary>Wraps.</summary>
                /// <exception cref="System.InvalidOperationException">Always.</exception>
                /// <exception cref="Shared.Widget">Never.</exception>
                public void Wrap() { }
            }
            """,
            "b.dll",
            references: reference);

        var type = output.Items[0].Items[0];

        Assert.Contains("<xref href=\"A.Shared.Widget\"", type.Summary);
        Assert.Equal(["A.Shared.Widget", "System.String"], type.SeeAlsos.Select(x => x.LinkId));

        var method = type.Items[0];
        Assert.Equal(["System.InvalidOperationException", "A.Shared.Widget"], method.Exceptions.Select(x => x.Type));
    }

    /// <summary>
    /// `Overload:` crefs only appear in hand authored XML documentation, so this exercises the
    /// comment parser directly instead of going through a C# compilation.
    /// </summary>
    [Fact]
    public void OverloadCrefsArePrefixed()
    {
        UsePrefixes(("a.dll", "A"));

        var compilation = CompilationHelper.CreateCompilationFromCSharpCode(SharedLibraryCode, EmptyMSBuildProperties, "a.dll");
        var context = new XmlCommentParserContext
        {
            ResolveUidPrefix = commentId => VisitorHelper.GetUidPrefixForCommentId(commentId, compilation),
        };

        var comment = XmlComment.Parse(
            """
            <member name="T:Other.Gadget">
              <summary>See <see cref="Overload:Shared.Widget.Do"/>.</summary>
            </member>
            """, context);

        Assert.Contains("<xref href=\"A.Shared.Widget.Do*\"", comment.Summary);
    }

    [Fact]
    public void GenericsAndTypeParametersArePrefixedOnlyOnce()
    {
        UsePrefixes(("test.dll", "Pkg"));

        var output = Verify(
            """
            using System.Collections.Generic;

            namespace Shared;

            /// <summary>A box.</summary>
            public class Box<T>
            {
                /// <summary>Gets many.</summary>
                public List<T> GetMany<TOther>(T value, TOther other) => null;
            }
            """);

        var type = output.Items[0].Items[0];
        Assert.Equal("Pkg.Shared.Box`1", type.Name);

        var method = type.Items[0];
        Assert.Equal("Pkg.Shared.Box`1.GetMany``1(`0,``0)", method.Name);
        Assert.Equal("Pkg.Shared.Box`1.GetMany*", method.Overload);

        // The prefix is applied exactly once, everywhere.
        Assert.DoesNotContain(output.References.Keys, key => key.Contains("Pkg.Pkg"));
    }

    [Fact]
    public void GlobalNamespaceIdComposesWithTheUidPrefix()
    {
        UsePrefixes(("test.dll", "Pkg"));
        VisitorHelper.GlobalNamespaceId = "Global";

        var output = Verify(
            """
            /// <summary>A widget.</summary>
            public class Widget { }
            """);

        Assert.Equal("Pkg.Global.Widget", output.Items[0].Items[0].Name);
    }

    [Fact]
    public void FilterRulesMatchUnprefixedUids()
    {
        UsePrefixes(("test.dll", "Pkg"));

        var output = Verify(
            """
            namespace Shared;

            /// <summary>A widget.</summary>
            public class Widget { }

            /// <summary>A hidden widget.</summary>
            public class HiddenWidget { }
            """,
            config: new() { FilterConfigFile = "TestData/filterconfig.uidprefix.yml" });

        // The filter file matches `Shared.HiddenWidget`, i.e. the API surface, not `Pkg.Shared.HiddenWidget`.
        Assert.Equal(["Pkg.Shared.Widget"], output.Items[0].Items.Select(x => x.Name));
    }
}

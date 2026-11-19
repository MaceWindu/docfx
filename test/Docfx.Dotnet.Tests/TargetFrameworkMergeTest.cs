// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AwesomeAssertions;
using Docfx.Common;
using Docfx.DataContracts.ManagedReference;
using Docfx.Tests.Common;
using Xunit;

namespace Docfx.Dotnet.Tests;

[Collection("docfx STA")]
public class TargetFrameworkMergeTest
{
    private const string First = "net8.0";
    private const string Second = "netstandard2.0";
    private const string Third = "net462";

    [Fact]
    public void EachItemListsTheFrameworksItExistsIn()
    {
        var result = Merge(
            ("namespace N { public class Shared { } public class OnlyFirst { } }", First),
            ("namespace N { public class Shared { } public class OnlyLast { } }", Second));

        result["N"].AppliesTo.Should().Equal(First, Second);
        result["N.Shared"].AppliesTo.Should().Equal(First, Second);
        result["N.OnlyFirst"].AppliesTo.Should().Equal(First);
        result["N.OnlyLast"].AppliesTo.Should().Equal(Second);
        result["N"].Items.Select(i => i.Name).Should().BeEquivalentTo("N.Shared", "N.OnlyFirst", "N.OnlyLast");
    }

    [Theory]
    [InlineData("class")]
    [InlineData("struct")]
    [InlineData("interface")]
    public void MembersAreUnionedForEveryKindOfType(string kind)
    {
        var result = Merge(
            ($"namespace N {{ public {kind} T {{ public int Shared {{ get; }} public int OnlyFirst {{ get; }} }} }}", First),
            ($"namespace N {{ public {kind} T {{ public int Shared {{ get; }} public int OnlyLast {{ get; }} }} }}", Second));

        result["N.T.Shared"].AppliesTo.Should().Equal(First, Second);
        result["N.T.OnlyFirst"].AppliesTo.Should().Equal(First);
        result["N.T.OnlyLast"].AppliesTo.Should().Equal(Second);
        result["N.T"].Items.Select(i => i.Name).Should().BeEquivalentTo("N.T.Shared", "N.T.OnlyFirst", "N.T.OnlyLast");
    }

    [Fact]
    public void FrameworksKeepTheOrderTheyWereMergedIn()
    {
        var result = Merge(
            ("namespace N { public class T { } }", Third),
            ("namespace N { public class Other { } }", First),
            ("namespace N { public class T { } }", Second));

        result["N.T"].AppliesTo.Should().Equal(Third, Second);
    }

    [Fact]
    public void TheFirstFrameworkProvidesTheContent()
    {
        var result = Merge(
            ("namespace N { /// <summary>first</summary>\npublic class T { } }", First),
            ("namespace N { /// <summary>last</summary>\npublic class T { } }", Second));

        result["N.T"].Summary.Should().Contain("first");
    }

    [Fact]
    public void AssembliesOfOneFrameworkDoNotRepeatIt()
    {
        var result = Merge(
            ("namespace N { public class T { public int A { get; } } }", First),
            ("namespace N { public class T { public int B { get; } } }", First));

        result["N"].AppliesTo.Should().Equal(First);
        result["N.T"].AppliesTo.Should().Equal(First);
        result["N.T"].Items.Select(i => i.Name).Should().BeEquivalentTo("N.T.A", "N.T.B");
    }

    [Fact]
    public void ADuplicateWithinOneFrameworkIsNotMerged()
    {
        var result = Merge(
            ("namespace N { public struct S { public int A { get; } } }", First),
            ("namespace N { public struct S { public int B { get; } } }", First));

        result["N.S"].Items.Select(i => i.Name).Should().Equal("N.S.A");
        result.Should().NotContainKey("N.S.B");
    }

    [Fact]
    public void IdenticalContentHasNoVariant()
    {
        var result = Merge(
            ("namespace N { /// <summary>same</summary>\npublic class T { } }", First),
            ("namespace N { /// <summary>same</summary>\npublic class T { } }", Second));

        result["N.T"].Variants.Should().BeNull();
    }

    [Fact]
    public void AVariantHoldsOnlyWhatDiffers()
    {
        var result = Merge(
            ("namespace N { /// <summary>first</summary>\npublic class T { } }", First),
            ("namespace N { /// <summary>last</summary>\npublic class T { } }", Second));

        var variant = result["N.T"].Variants.Should().ContainSingle().Subject;
        variant.Frameworks.Should().Equal(Second);
        variant.Content.Summary.Should().Contain("last");
        variant.Content.Syntax.Should().BeNull();
        variant.Content.Inheritance.Should().BeNull();
    }

    [Fact]
    public void FrameworksWithTheSameDifferenceShareAVariant()
    {
        var result = Merge(
            ("namespace N { /// <summary>one</summary>\npublic class T { } }", First),
            ("namespace N { /// <summary>two</summary>\npublic class T { } }", Second),
            ("namespace N { /// <summary>two</summary>\npublic class T { } }", Third));

        var variant = result["N.T"].Variants.Should().ContainSingle().Subject;
        variant.Frameworks.Should().Equal(Second, Third);
    }

    [Fact]
    public void DifferentDifferencesAreSeparateVariants()
    {
        var result = Merge(
            ("namespace N { /// <summary>one</summary>\npublic class T { } }", First),
            ("namespace N { /// <summary>two</summary>\npublic class T { } }", Second),
            ("namespace N { /// <summary>three</summary>\npublic class T { } }", Third));

        result["N.T"].Variants.Select(v => v.Frameworks.Single()).Should().Equal(Second, Third);
    }

    [Fact]
    public void ADifferentSignatureIsAVariant()
    {
        var result = Merge(
            ("namespace N { public class T { public int M() => 0; } }", First),
            ("namespace N { public class T { public long M() => 0; } }", Second));

        var variant = result["N.T.M"].Variants.Should().ContainSingle().Subject;
        variant.Content.Syntax.Return.Type.Should().Be("System.Int64");
    }

    [Fact]
    public void ADifferentSignatureHoldsOnlyThePartsThatDiffer()
    {
        var result = Merge(
            ("namespace N { public class T { public const string C = \"a\"; public int M<TV>(int x) => 0; } }", First),
            ("namespace N { public class T { public const string C = \"b\"; public long M<TV>(int x) => 0; } }", Second));

        var constant = result["N.T.C"].Variants.Should().ContainSingle().Subject.Content.Syntax;
        constant.Content.Should().ContainValue("public const string C = \"b\"");
        constant.Return.Should().BeNull();

        var method = result["N.T.M``1(System.Int32)"].Variants.Should().ContainSingle().Subject.Content.Syntax;
        method.Return.Type.Should().Be("System.Int64");
        method.Parameters.Should().BeNull();
        method.TypeParameters.Should().BeNull();
    }

    [Fact]
    public void WhatAVariantLacksIsWrittenEmptyRatherThanLeftOut()
    {
        var result = Merge(
            ("namespace N { public delegate void D(int x); public class T { public int M() => 0; } }", First),
            ("namespace N { public delegate void D(); public class T { public void M() { } } }", Second));

        // left out, it would read as the same as the API's
        var method = result["N.T.M"].Variants.Should().ContainSingle().Subject.Content.Syntax;
        method.Return.Should().NotBeNull();
        method.Return.Type.Should().BeNull();
        method.Parameters.Should().BeNull();

        var handler = result["N.D"].Variants.Should().ContainSingle().Subject.Content.Syntax;
        handler.Parameters.Should().BeEmpty();
        handler.Return.Should().BeNull();
    }

    [Theory]
    [InlineData("class")]
    [InlineData("interface")]
    public void InterfacesListedInADifferentOrderAreNotAVariant(string kind)
    {
        var result = Merge(
            ($"namespace N {{ public interface IA {{ }} public interface IB {{ }} public {kind} T : IA, IB {{ }} }}", First),
            ($"namespace N {{ public interface IA {{ }} public interface IB {{ }} public {kind} T : IB, IA {{ }} }}", Second));

        result["N.T"].Variants.Should().BeNull();
    }

    [Fact]
    public void ADifferentSetOfInterfacesIsAVariant()
    {
        var result = Merge(
            ("namespace N { public interface IA { } public interface IB { } public class T : IA, IB { } }", First),
            ("namespace N { public interface IA { } public interface IB { } public class T : IA { } }", Second));

        result["N.T"].Variants.Should().ContainSingle();
    }

    [Theory]
    [InlineData("CSharp", "public class T : B, IB, IA where X : class", "public class T : B, IA, IB where X : class")]
    [InlineData("CSharp", "public class T<K, V> : IB<K, V>, IA", "public class T<K, V> : IA, IB<K, V>")]
    [InlineData("CSharp", "public record R(int A, int B) : IB, IA", "public record R(int A, int B) : IA, IB")]
    [InlineData("CSharp", "public void M<T>(T x) where T : IB, IA", "public void M<T>(T x) where T : IB, IA")]
    [InlineData("CSharp", "public int P { get; set; }", "public int P { get; set; }")]
    [InlineData("VB", "Public Class T Inherits B Implements IB, IA", "Public Class T Inherits B Implements IA, IB")]
    [InlineData("VB", "Public Interface IT Inherits IB, IA", "Public Interface IT Inherits IA, IB")]
    [InlineData("VB", "Public Class T Inherits B", "Public Class T Inherits B")]
    // a bracket in a string or character of an attribute is not one of the declaration
    [InlineData("CSharp", "[Obsolete(\"use a -> b\")]\npublic class T : IB, IA", "[Obsolete(\"use a -> b\")]\npublic class T : IA, IB")]
    [InlineData("CSharp", "[Obsolete(\"say \\\"(\\\" \")]\npublic class T : IB, IA", "[Obsolete(\"say \\\"(\\\" \")]\npublic class T : IA, IB")]
    [InlineData("CSharp", "[Sep(')')]\npublic class T : IB, IA", "[Sep(')')]\npublic class T : IA, IB")]
    [InlineData("VB", "<Obsolete(\"use a -> \"\"b\"\"\")>\nPublic Class T Implements IB, IA", "<Obsolete(\"use a -> \"\"b\"\"\")>\nPublic Class T Implements IA, IB")]
    public void SortBaseList(string language, string declaration, string expected)
    {
        TargetFrameworkVariants.SortBaseList(declaration, language).Should().Be(expected);
    }

    [Fact]
    public void ADifferentInheritanceChainIsAVariantNotAUnion()
    {
        var result = Merge(
            ("namespace N { public class B1 { } public class T : B1 { } }", First),
            ("namespace N { public class B2 { } public class T : B2 { } }", Second));

        result["N.T"].Inheritance.Should().Contain("N.B1").And.NotContain("N.B2");
        result["N.T"].Variants.Should().ContainSingle().Which.Content.Inheritance.Should().Contain("N.B2");
    }

    [Fact]
    public void ImplementedInterfacesAndInheritedMembersAreUnioned()
    {
        var result = Merge(
            ("namespace N { public interface I { } public class B { } public class T : B { } }", First),
            ("namespace N { public interface I { } public class B { public void Extra() { } } public class T : B, I { } }", Second));

        result["N.T"].Implements.Should().Contain("N.I");
        result["N.T"].InheritedMembers.Should().Contain("N.B.Extra");
    }

    [Fact]
    public void AKindThatDiffersWarnsAndTheFirstFrameworkWins()
    {
        using var listener = new TestListenerScope();

        var result = Merge(
            ("namespace N { public class K { } }", First),
            ("namespace N { public struct K { } }", Second));

        result["N.K"].Type.Should().Be(MemberType.Class);
        listener.GetItemsByLogLevel(LogLevel.Warning).Should().ContainSingle(x => x.Code == WarningCodes.Metadata.ApiKindDiffersBetweenFrameworks);
    }

    [Fact]
    public void DocumentationMissingInOneFrameworkIsNotADifference()
    {
        var result = Merge(
            ("namespace N { public class T { /// <summary>sum</summary><param name=\"x\">the x</param><returns>a value</returns>\npublic int M(int x) => x; } }", First),
            ("namespace N { public class T { public int M(int x) => x; } }", Second));

        result["N.T.M(System.Int32)"].Variants.Should().BeNull();
    }

    [Theory]
    [InlineData("<exception cref=\"System.ArgumentException\">bad</exception>")]
    [InlineData("<seealso cref=\"System.String\"/>")]
    public void ExceptionsAndSeeAlsoInOneFrameworkOnlyAreNotADifference(string comment)
    {
        var documented = $"namespace N {{ public class T {{ /// {comment}\npublic void M() {{ }} }} }}";
        var undocumented = "namespace N { public class T { public void M() { } } }";

        Merge((documented, First), (undocumented, Second))["N.T.M"].Variants.Should().BeNull();

        var taken = Merge((undocumented, First), (documented, Second))["N.T.M"];
        taken.Variants.Should().BeNull();
        ((taken.Exceptions?.Count ?? 0) + (taken.SeeAlsos?.Count ?? 0)).Should().Be(1);
    }

    [Fact]
    public void AFrameworkWithoutDocumentationTakesItFromAnother()
    {
        var result = Merge(
            ("namespace N { public class T { } }", First),
            ("namespace N { /// <summary>from the second</summary>\npublic class T { } }", Second));

        result["N.T"].Summary.Should().Contain("from the second");
        result["N.T"].Variants.Should().BeNull();
    }

    [Fact]
    public void AFrameworkWithoutParameterDescriptionsTakesThemFromAnother()
    {
        var result = Merge(
            ("namespace N { public class T { /// <summary>m</summary>\npublic int M<TV>(int x) => x; } }", First),
            ("namespace N { public class T { /// <summary>m</summary><typeparam name=\"TV\">the TV</typeparam><param name=\"x\">the x</param><returns>the result</returns>\npublic int M<TV>(int x) => x; } }", Second));

        var syntax = result["N.T.M``1(System.Int32)"].Syntax;
        syntax.Parameters.Single().Description.Should().Contain("the x");
        syntax.TypeParameters.Single().Description.Should().Contain("the TV");
        syntax.Return.Description.Should().Contain("the result");
        result["N.T.M``1(System.Int32)"].Variants.Should().BeNull();
    }

    [Fact]
    public void AFrameworkSharesAVariantOnlyWithTheSameDocumentation()
    {
        // The third framework differs from the first in its signature only, the second in its signature and
        // summary: sharing the second's variant would show the third the second's summary.
        var result = Merge(
            ("namespace N { public class T { /// <summary>one</summary>\npublic int M() => 0; } }", First),
            ("namespace N { public class T { /// <summary>two</summary>\npublic long M() => 0; } }", Second),
            ("namespace N { public class T { /// <summary>one</summary>\npublic long M() => 0; } }", Third));

        var variants = result["N.T.M"].Variants;
        variants.Select(v => v.Frameworks.Single()).Should().Equal(Second, Third);
        variants[0].Content.Summary.Should().Contain("two");
        variants[1].Content.Summary.Should().BeNull();
    }

    [Fact]
    public void OnlyTheBaseListOfATypeIsComparedInAnyOrder()
    {
        var primary = new MetadataItem { Name = "N.T.C", Type = MemberType.Field, Syntax = new() { Content = new() { [SyntaxLanguage.CSharp] = "public const string C = \"a : b, c\"" } } };
        var other = new MetadataItem { Name = "N.T.C", Type = MemberType.Field, Syntax = new() { Content = new() { [SyntaxLanguage.CSharp] = "public const string C = \"a : c, b\"" } } };

        TargetFrameworkVariants.Merge(primary, other, Second);

        primary.Variants.Should().ContainSingle();
    }

    [Fact]
    public void DocumentationThatDiffersOnlyInWhitespaceIsNotADifference()
    {
        var result = Merge(
            ("namespace N { public class T { /// <summary>one two</summary><param name=\"x\">the x</param>\npublic int M(int x) => x; } }", First),
            ("namespace N { public class T { /// <summary>\n/// one\n///     two\n/// </summary><param name=\"x\">the\n/// x</param>\npublic int M(int x) => x; } }", Second));

        result["N.T.M(System.Int32)"].Variants.Should().BeNull();
    }

    [Fact]
    public void WhitespaceOutsideDocumentationIsADifference()
    {
        var primary = new MetadataItem { Name = "N.T", Syntax = new() { Content = new() { [SyntaxLanguage.CSharp] = "public class T" } } };
        var other = new MetadataItem { Name = "N.T", Syntax = new() { Content = new() { [SyntaxLanguage.CSharp] = "public  class T" } } };

        TargetFrameworkVariants.Merge(primary, other, Second);

        primary.Variants.Should().ContainSingle();
    }

    [Fact]
    public void WhitespaceOnlyDocumentationIsTakenFromAnother()
    {
        var primary = new MetadataItem { Name = "N.T", Summary = " \n " };
        var other = new MetadataItem { Name = "N.T", Summary = "text" };

        TargetFrameworkVariants.Merge(primary, other, Second);

        primary.Summary.Should().Be("text");
        primary.Variants.Should().BeNull();
    }

    [Fact]
    public void EmptyAndMissingAreTheSame()
    {
        var primary = new MetadataItem { Name = "N.T", Examples = [], Exceptions = [] };
        var other = new MetadataItem { Name = "N.T" };

        TargetFrameworkVariants.Merge(primary, other, Second);

        primary.Variants.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(VariantIgnore.None), true)]
    [InlineData(nameof(VariantIgnore.Attributes), false)]
    public void AttributesCanBeIgnored(string ignore, bool expectVariant)
    {
        var result = MergeIgnoring(
            Enum.Parse<VariantIgnore>(ignore),
            ("namespace N { [System.Obsolete] public class T { } }", First),
            ("namespace N { public class T { } }", Second));

        (result["N.T"].Variants is not null).Should().Be(expectVariant);
    }

    [Theory]
    [InlineData("[System.Obsolete(\"Use T[] instead\")]")]
    [InlineData("[System.Obsolete(\"a > b ] c\")]")]
    [InlineData("[A(new[] { 1 })]")]
    public void AttributesWithBracketsInTheirArgumentsCanBeIgnored(string attribute)
    {
        var result = MergeIgnoring(
            VariantIgnore.Attributes,
            ($"namespace N {{ public class A : System.Attribute {{ public A(int[] a) {{ }} }} {attribute} public class T {{ }} }}", First),
            ("namespace N { public class A : System.Attribute { public A(int[] a) { } } public class T { } }", Second));

        result["N.T"].Syntax.Content.Values.Should().Contain(c => c.StartsWith('['));
        result["N.T"].Variants.Should().BeNull();
    }

    [Fact]
    public void AttributeArgumentsThatAreNotFiniteNumbersAreCompared()
    {
        string Code(string max, string parameter) => $$"""
            namespace N
            {
                public class A : System.Attribute { public A(double d) { } }
                public class T
                {
                    [A({{max}})] public double Max { get; set; }
                    public void M([A({{parameter}})] double d) { }
                }
            }
            """;

        var same = Merge((Code("double.PositiveInfinity", "double.NaN"), First), (Code("double.PositiveInfinity", "double.NaN"), Second));
        same["N.T.Max"].Variants.Should().BeNull();
        same["N.T.M(System.Double)"].Variants.Should().BeNull();

        var different = Merge((Code("double.PositiveInfinity", "double.NaN"), First), (Code("double.NegativeInfinity", "1d"), Second));
        different["N.T.Max"].Variants.Should().ContainSingle();
        // the attributes of a parameter are never a difference
        different["N.T.M(System.Double)"].Variants.Should().BeNull();
    }

    [Fact]
    public void AMemberOnlyAnotherFrameworkHasKeepsItsPlaceInTheSource()
    {
        var result = Merge(
            ("namespace N { public class T { public void Read(int b) { } public void Write() { } } }", First),
            ("namespace N { public class T { public void Read(int b) { } public void Read(long b) { } public void Write() { } public void Flush() { } } }", Second));

        result["N.T"].Items.Select(i => i.Name).Should().Equal("N.T.Read(System.Int32)", "N.T.Read(System.Int64)", "N.T.Write", "N.T.Flush");
    }

    [Fact]
    public void ADeclarationThatIsTheSameIsLeftOutOfAVariant()
    {
        var result = Merge(
            ("namespace N { public class T { /// <param name=\"x\">milliseconds</param><returns>one</returns>\npublic int M(int x) => x; } }", First),
            ("namespace N { public class T { /// <param name=\"x\">seconds</param><returns>one</returns>\npublic int M(int x) => x; } }", Second));

        var syntax = result["N.T.M(System.Int32)"].Variants.Should().ContainSingle().Subject.Content.Syntax;
        syntax.Content.Should().BeNull();
        syntax.Parameters.Should().ContainSingle().Which.Description.Should().Contain("seconds");
        syntax.Return.Should().BeNull();
    }

    [Fact]
    public void AttributesOfParametersAndReturnValuesAreNotADifference()
    {
        // as `NotNullWhen`, public in one framework and missing or internal in another; an attribute whose
        // constructor is implicitly declared is never recorded, so `A` declares one
        var result = Merge(
            ("namespace N { public class A : System.Attribute { public A() { } } public class T { [return: A] public string M([A] string s) => s; } }", First),
            ("namespace N { public class T { public string M(string s) => s; } }", Second));

        var item = result["N.T.M(System.String)"];
        item.Syntax.Parameters.Should().ContainSingle().Which.Attributes.Should().NotBeEmpty();
        item.Syntax.Return.Attributes.Should().NotBeEmpty();
        item.Variants.Should().BeNull();
    }

    [Theory]
    [InlineData(nameof(VariantIgnore.None), true)]
    [InlineData(nameof(VariantIgnore.Documentation), false)]
    public void DocumentationCanBeIgnored(string ignore, bool expectVariant)
    {
        var result = MergeIgnoring(
            Enum.Parse<VariantIgnore>(ignore),
            ("namespace N { /// <summary>one</summary>\npublic class T { } }", First),
            ("namespace N { /// <summary>two</summary>\npublic class T { } }", Second));

        (result["N.T"].Variants is not null).Should().Be(expectVariant);
    }

    [Theory]
    [InlineData(nameof(VariantIgnore.None), "", true)]
    [InlineData(nameof(VariantIgnore.InheritedDocumentation), "", false)]
    [InlineData(nameof(VariantIgnore.None), "/// <inheritdoc/>\n", true)]
    [InlineData(nameof(VariantIgnore.InheritedDocumentation), "/// <inheritdoc/>\n", false)]
    public void InheritedDocumentationCanBeIgnored(string ignore, string comment, bool expectVariant)
    {
        string Code(string text) => $$"""
            namespace N
            {
                public class B { /// <summary>{{text}}</summary>
                public virtual void M() { } }
                public class T : B { {{comment}}public override void M() { } }
            }
            """;

        var result = MergeIgnoring(Enum.Parse<VariantIgnore>(ignore), (Code("one"), First), (Code("two"), Second));

        (result["N.T.M"].Variants is not null).Should().Be(expectVariant);
    }

    [Fact]
    public void IgnoringInheritedDocumentationKeepsDocumentationOfItsOwn()
    {
        var result = MergeIgnoring(
            VariantIgnore.InheritedDocumentation,
            ("namespace N { /// <summary>one</summary>\npublic class T { } }", First),
            ("namespace N { /// <summary>two</summary>\npublic class T { } }", Second));

        result["N.T"].Variants.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IgnoringInheritedDocumentationNeverShowsAFrameworkTheTextOfAnother(bool inheritingFrameworkFirst)
    {
        // `Second` and `Third` have the same declaration, which differs from `First`'s; `Second` has a summary of its own
        const string Base = "public class B { /// <summary>base</summary>\npublic void M(int a) { } }";
        var second = ($"namespace N {{ {Base} public class T {{ /// <summary>own</summary>\npublic void M(int b) {{ }} }} }}", Second);
        var third = ($"namespace N {{ {Base} public class T {{ /// <inheritdoc cref=\"B.M\"/>\npublic void M(int b) {{ }} }} }}", Third);
        var first = ($"namespace N {{ {Base} public class T {{ /// <inheritdoc cref=\"B.M\"/>\npublic void M(int a) {{ }} }} }}", First);

        var result = MergeIgnoring(VariantIgnore.InheritedDocumentation, inheritingFrameworkFirst ? new[] { first, third, second } : new[] { first, second, third });

        var variants = result["N.T.M(System.Int32)"].Variants;
        variants.Should().HaveCount(2);
        variants.Single(v => v.Frameworks.Contains(Second)).Content.Summary.Should().Contain("own");
        var inheriting = variants.Single(v => v.Frameworks.Contains(Third));
        inheriting.Frameworks.Should().Equal(Third);
        (inheriting.Content.Summary ?? "").Should().NotContain("own");
    }

    [Fact]
    public void IgnoringInheritedDocumentationComparesWithTheSummaryShown()
    {
        // `First` inherits an empty summary, which `Second`'s own fills; `Third` inherits a summary of its base
        string Code(string baseSummary, string summary) => $$"""
            namespace N
            {
                public class B { {{baseSummary}}
                public virtual void M() { } }
                public class T : B { {{summary}}
                public override void M() { } }
            }
            """;

        var result = MergeIgnoring(
            VariantIgnore.InheritedDocumentation,
            (Code("", ""), First),
            (Code("", "/// <summary>own</summary>"), Second),
            (Code("/// <summary>inherited</summary>", ""), Third));

        var item = result["N.T.M"];
        item.Summary.Should().Contain("own");
        item.Variants.Should().ContainSingle().Which.Frameworks.Should().Equal(Third);
        item.Variants[0].Content.Summary.Should().Contain("inherited");
    }

    [Theory]
    [InlineData(nameof(VariantIgnore.None), false)]
    [InlineData(nameof(VariantIgnore.Documentation), false)]
    [InlineData(nameof(VariantIgnore.InheritedDocumentation), true)]
    public void WhetherDocumentationIsInheritedIsReadOnlyToIgnoreIt(string ignore, bool expected)
    {
        var compilation = CompilationHelper.CreateCompilationFromCSharpCode(
            "namespace N { public class B { /// <summary>base</summary>\npublic virtual void M() { } } public class T : B { /// <inheritdoc/>\npublic override void M() { } } }",
            new Dictionary<string, string>(), "Lib");

        var item = compilation.Assembly.GenerateMetadataItem(compilation, new() { VariantIgnore = Enum.Parse<VariantIgnore>(ignore) })!;

        item.Items.Single().Items.Single(i => i.Name == "N.T").Items.Single().IsDocumentationInherited.Should().Be(expected);
    }

    [Fact]
    public void AnInheritdocInTheRemarksLeavesTheSummaryOfItsOwn()
    {
        string Code(string summary) => $$"""
            namespace N
            {
                public class B { /// <summary>base</summary>
                /// <remarks>base remarks</remarks>
                public virtual void M() { } }
                public class T : B { /// <summary>{{summary}}</summary>
                /// <remarks><inheritdoc/></remarks>
                public override void M() { } }
            }
            """;

        var result = MergeIgnoring(VariantIgnore.InheritedDocumentation, (Code("one"), First), (Code("two"), Second));

        result["N.T.M"].Variants.Should().ContainSingle().Which.Content.Summary.Should().Contain("two");
    }

    [Fact]
    public void AnInheritdocOfTheWholeCommentLeavesASummaryOfItsOwnBeforeIt()
    {
        // The `inheritdoc` expands in its place, and the first summary is the one shown.
        string Code(string summary, bool summaryFirst)
        {
            var own = $"/// <summary>{summary}</summary>";
            var comment = summaryFirst ? $"{own}\n/// <inheritdoc/>" : $"/// <inheritdoc/>\n{own}";
            return $$"""
                namespace N
                {
                    public class B { /// <summary>base</summary>
                    public virtual void M() { } }
                    public class T : B {
                    {{comment}}
                    public override void M() { } }
                }
                """;
        }

        var own = MergeIgnoring(VariantIgnore.InheritedDocumentation, (Code("Fast path", true), First), (Code("Slow path", true), Second));
        own["N.T.M"].Summary.Should().Contain("Fast path");
        own["N.T.M"].Variants.Should().ContainSingle().Which.Content.Summary.Should().Contain("Slow path");

        var inherited = MergeIgnoring(VariantIgnore.InheritedDocumentation, (Code("Fast path", false), First), (Code("Slow path", false), Second));
        inherited["N.T.M"].Summary.Should().Contain("base");
        inherited["N.T.M"].Variants.Should().BeNullOrEmpty();
    }

    [Fact]
    public void ATypeImplementingAnInterfaceTwiceIsListedOnceAsDerived()
    {
        // `class C : IConverter<int>, IConverter<string>`, resolved twice, as two runs in one process do
        MetadataModel Model() => new()
        {
            Members =
            [
                new() { Name = "N.IConverter`1", Type = MemberType.Interface },
                new() { Name = "N.C", Type = MemberType.Class, Implements = ["N.IConverter{System.Int32}", "N.IConverter{System.String}"] },
            ],
        };
        var context = new ResolverContext
        {
            References = new()
            {
                ["N.IConverter{System.Int32}"] = new() { Definition = "N.IConverter`1" },
                ["N.IConverter{System.String}"] = new() { Definition = "N.IConverter`1" },
            },
        };
        var pipeline = new SetDerivedClass();

        pipeline.Run(Model(), context);
        var second = Model();
        pipeline.Run(second, context);

        second.Members[0].DerivedClasses.Should().Equal("N.C");
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("<member name=\"M:N.T.M\"><inheritdoc/></member>", true)]
    [InlineData("<member name=\"M:N.T.M\"><inheritdoc cref=\"B.M\"/><remarks>own</remarks></member>", true)]
    [InlineData("<member name=\"M:N.T.M\"><summary> <inheritdoc/> </summary></member>", true)]
    [InlineData("<member name=\"M:N.T.M\"><summary>own <inheritdoc/></summary></member>", false)]
    [InlineData("<member name=\"M:N.T.M\"><summary>own</summary><remarks><inheritdoc/></remarks></member>", false)]
    [InlineData("<member name=\"M:N.T.M\"><summary>own</summary><param name=\"a\"><inheritdoc/></param></member>", false)]
    [InlineData("<summary><inheritdoc/></summary>", true)]
    [InlineData("<summary>own</summary>", false)]
    // an inheritdoc of the whole comment expands in its place and the first summary is shown: its own before it stays its own
    [InlineData("<member name=\"M:N.T.M\"><inheritdoc/><summary>Fast path</summary></member>", true)]
    [InlineData("<member name=\"M:N.T.M\"><summary>Fast path</summary><inheritdoc/></member>", false)]
    [InlineData("<member name=\"M:N.T.M\"><inheritdoc/><summary><inheritdoc/></summary></member>", true)]
    public void DocumentationIsInheritedWhenItsSummaryIsWhollyInherited(string comment, bool expected)
    {
        SymbolVisitorAdapter.IsDocumentationInherited(comment).Should().Be(expected);
    }

    [Fact]
    public void ANamespaceThatOnlyHoldsOthersListsItsFrameworksInTheOrderOfTheMerge()
    {
        // No namespace orders First relative to Second, so only the order of the merge does.
        var units = new[]
        {
            ("namespace A.X { public class C { } }", Second),
            ("namespace A.Y { public class D { } }", First),
            ("namespace A.X { public class C { } } namespace A.Y { public class D { } }", Third),
        };
        var items = units.Select(unit =>
        {
            var compilation = CompilationHelper.CreateCompilationFromCSharpCode(unit.Item1, new Dictionary<string, string>(), "Lib");
            var item = compilation.Assembly.GenerateMetadataItem(compilation, new())!;
            DotnetApiCatalog.TagAppliesTo(item, unit.Item2);
            return item;
        }).ToList();
        var result = new Dictionary<string, MetadataItem>();
        DotnetApiCatalog.MergeTargetFrameworks(result, items);
        var references = new Dictionary<string, ReferenceItem>();
        foreach (var reference in items.SelectMany(i => i.References ?? []))
        {
            references.TryAdd(reference.Key, reference.Value);
        }

        var model = YamlMetadataResolver.ResolveMetadata(result, references, NamespaceLayout.Nested, [Second, First, Third]);

        var placeholder = model.TocYamlViewModel.Items.Single(i => i.Name == "A");
        placeholder.AppliesTo.Should().Equal(Second, First, Third);
    }

    private static Dictionary<string, MetadataItem> Merge(params (string Code, string Framework)[] units)
    {
        return MergeIgnoring(VariantIgnore.None, units);
    }

    private static Dictionary<string, MetadataItem> MergeIgnoring(VariantIgnore ignore, params (string Code, string Framework)[] units)
    {
        var items = units.Select(unit =>
        {
            var compilation = CompilationHelper.CreateCompilationFromCSharpCode(unit.Code, new Dictionary<string, string>(), "Lib");
            var item = compilation.Assembly.GenerateMetadataItem(compilation, new() { VariantIgnore = ignore })!;
            DotnetApiCatalog.TagAppliesTo(item, unit.Framework);
            return item;
        }).ToList();

        var result = new Dictionary<string, MetadataItem>();
        DotnetApiCatalog.MergeTargetFrameworks(result, items, ignore);
        return result;
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AwesomeAssertions;
using Docfx.Common;
using Docfx.DataContracts.ManagedReference;
using Docfx.Tests.Common;
using Xunit;

namespace Docfx.Dotnet.Tests;

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

    private static Dictionary<string, MetadataItem> Merge(params (string Code, string Framework)[] units)
    {
        var items = units.Select(unit =>
        {
            var compilation = CompilationHelper.CreateCompilationFromCSharpCode(unit.Code, new Dictionary<string, string>(), "Lib");
            var item = compilation.Assembly.GenerateMetadataItem(compilation)!;
            DotnetApiCatalog.TagAppliesTo(item, unit.Framework);
            return item;
        }).ToList();

        var result = new Dictionary<string, MetadataItem>();
        DotnetApiCatalog.MergeTargetFrameworks(result, items);
        return result;
    }
}

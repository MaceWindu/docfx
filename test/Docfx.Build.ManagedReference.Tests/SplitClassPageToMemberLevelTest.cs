// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AwesomeAssertions;
using Docfx.DataContracts.ManagedReference;
using Xunit;

namespace Docfx.Build.ManagedReference.Tests;

public class SplitClassPageToMemberLevelTest
{
    [Theory]
    // overloads that frameworks build into differently named assemblies
    [InlineData(true, new[] { "Lib", "Lib.Legacy" })]
    // without frameworks, the first overload's, as before
    [InlineData(false, new[] { "Lib" })]
    public void AnOverloadPageListsTheAssembliesOfEveryOverloadOnlyWithFrameworks(bool frameworks, string[] expected)
    {
        var appliesTo = frameworks ? new List<AppliesToViewModel> { new() { Frameworks = ["net8.0", "netstandard2.0"] } } : null;
        var type = new ItemViewModel { Uid = "N.T", AssemblyNameList = ["Lib", "Lib.Legacy"], AppliesTo = appliesTo };
        var page = new PageViewModel
        {
            Items =
            [
                type,
                new() { Uid = "N.T.M(System.Int32)", Overload = "N.T.M*", Type = MemberType.Method, AssemblyNameList = ["Lib"] },
                new() { Uid = "N.T.M(System.String)", Overload = "N.T.M*", Type = MemberType.Method, AssemblyNameList = ["Lib.Legacy"] },
            ],
        };

        var overload = page.Items.Skip(1).GroupBy(item => item.Overload).Single();

        SplitClassPageToMemberLevel.GenerateOverloadPage(page, overload).AssemblyNameList.Should().Equal(expected);
    }
}

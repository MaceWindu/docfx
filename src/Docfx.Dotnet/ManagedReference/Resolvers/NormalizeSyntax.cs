// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Docfx.Common;
using Docfx.DataContracts.ManagedReference;

namespace Docfx.Dotnet;

internal class NormalizeSyntax : IResolverPipeline
{
    public void Run(MetadataModel yaml, ResolverContext context)
    {
        TreeIterator.Preorder(
            yaml.TocYamlViewModel,
            null,
            s => s.IsInvalid ? null : s.Items,
            (member, parent) =>
            {
                Normalize(member.Syntax);
                foreach (var variant in member.Variants ?? [])
                {
                    Normalize(variant.Content.Syntax);
                }

                return true;
            });
    }

    private static void Normalize(SyntaxDetail syntax)
    {
        // get all the possible places where link is possible
        if (syntax is { Content: not null })
        {
            SyntaxLanguage[] keys = new SyntaxLanguage[syntax.Content.Count];
            syntax.Content.Keys.CopyTo(keys, 0);
            foreach (var key in keys)
            {
                syntax.Content[key] = NormalizeLines(syntax.Content[key]);
            }
        }
    }

    private static string NormalizeLines(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }
        return content.Replace("\r\n", "\n");
    }
}

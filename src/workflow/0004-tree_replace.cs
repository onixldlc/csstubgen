using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsStubGen;

class TreeReplace
{
    // takes one module's already-decompiled stub string,
    // walks it with Roslyn, and removes methods / properties / fields / events
    // whose names are NOT in the source-member dictionary (externalMethodDict).
    public static string Execute(MethodsDictionary externalMethodDict, string moduleStub)
    {
        // parse the decompiled stub into a syntax tree
        var stubSyntaxTree = CSharpSyntaxTree.ParseText(moduleStub);
        var currentRoot = (CompilationUnitSyntax)stubSyntaxTree.GetRoot();

        // walk every bucket -> module -> type and prune unwanted members.
        // Map returns must match the dict's nested types: Bucket -> Module -> MethodType.
        externalMethodDict
        .Map((bucketKey, bucket) => bucket
            .Map((moduleName, moduleEntry) => moduleEntry
                .Map((typeName, typeEntry) => {
                    // strip <T> off generic typeName so we match the class as written
                    var baseTypeName = typeName;
                    if (typeName.Contains('<')) {
                        baseTypeName = typeName.Substring(0, typeName.IndexOf('<'));
                    }

                    // MethodsDictionary keys are full reflection names ("UnityEngine.GameObject"),
                    // but Roslyn's TypeDeclarationSyntax.Identifier.Text is just the short name
                    // ("GameObject"). Split and take the last segment to compare.
                    var shortTypeName = baseTypeName;
                    if (baseTypeName.Contains('.')) {
                        shortTypeName = baseTypeName.Substring(baseTypeName.LastIndexOf('.') + 1);
                    }

                    // 1. find type-decl (class / struct / interface / record) matching shortTypeName.
                    var cls = currentRoot.DescendantNodes()
                        .OfType<TypeDeclarationSyntax>()
                        .FirstOrDefault(c => c.Identifier.Text == shortTypeName);
                    if (cls == null) return typeEntry; // type not in this stub, skip

                    // 2. build whitelist of member names we WANT to keep
                    var wantedNames = typeEntry.method
                        .Select(m => m.method)
                        .ToHashSet();

                    // 3. find members NOT in whitelist
                    var unwanted = cls.Members
                        .OfType<MethodDeclarationSyntax>()
                        .Where(m => !wantedNames.Contains(m.Identifier.Text))
                        .Cast<SyntaxNode>()
                        .ToList();

                    // 4. remove non-whitelisted members, reassign outer var
                    currentRoot = currentRoot.RemoveNodes(unwanted, SyntaxRemoveOptions.KeepNoTrivia);

                    return typeEntry;
                })
            )
        );

        return currentRoot.ToFullString();
    }
}

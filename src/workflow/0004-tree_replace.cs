using System;
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
    public static string Execute(MethodsDictionary externalMethodDict, string moduleName, string moduleStub)
    {
        // parse the decompiled stub into a syntax tree
        var stubSyntaxTree = CSharpSyntaxTree.ParseText(moduleStub);
        var currentRoot = (CompilationUnitSyntax)stubSyntaxTree.GetRoot();

        // walk every bucket -> module -> type and prune unwanted members.
        // Map returns must match the dict's nested types: Bucket -> Module -> MethodType.

        var externalBucket = externalMethodDict.Get("external");
        var moduleEntry = externalBucket.Get(moduleName);
        moduleEntry.Map((typeName, typeEntry) => {
            var simpleTypeName = typeName.Contains('.') ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;
            var stubTypeNode = currentRoot.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => t.Identifier.Text == simpleTypeName);
            var stubType = stubTypeNode?.ToFullString() ?? "";
            var b64StubType = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(stubType));
            Console.WriteLine($"Processing external::{moduleName}::{typeName} with stub (base64):\n{b64StubType}");
            return typeEntry;
        });

        // externalMethodDict
        // .Map((bucketKey, bucket) => bucket
        //     .Map((moduleName, moduleEntry) => moduleEntry
        //         .Map((typeName, typeEntry) => {
        //             var stubTypeNode = currentRoot.DescendantNodes()
        //                 .OfType<TypeDeclarationSyntax>()
        //                 .FirstOrDefault(t => t.Identifier.Text == typeName);
        //             var stubType = stubTypeNode?.ToFullString() ?? "";
        //             var b64StubType = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(stubType));
        //             Console.WriteLine($"Processing {bucketKey}::{moduleName}::{typeName} with stub (base64):\n{b64StubType}");
        //             return typeEntry;
        //         })
        //     )
        // );

        return currentRoot.ToFullString();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
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
    public static string Execute(MethodsDictionary externalMethodDict, string moduleName, string moduleStub, string outDir)
    {

        var stubSyntaxTree = CSharpSyntaxTree.ParseText(moduleStub);
        var currentRoot = (CompilationUnitSyntax)stubSyntaxTree.GetRoot();

        // walk every bucket -> module -> type and prune unwanted members.
        // Map returns must match the dict's nested types: Bucket -> Module -> MethodType.

        var methodStubList= new List<string>();
        var stubTypeNodes = new List<TypeDeclarationSyntax>();
        var externalBucket = externalMethodDict.Get("external");
        var moduleEntry = externalBucket.Get(moduleName);
        moduleEntry.Map((typeName, typeEntry) => {
            var simpleTypeName = typeName.Contains('.') ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;
            var stubTypeNode = currentRoot.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => t.Identifier.Text == simpleTypeName);
            stubTypeNodes.Add(stubTypeNode);
            foreach (var m in typeEntry.method)
                methodStubList.Add(m.stub.Trim());
            var stubType = stubTypeNode?.ToFullString() ?? "";
            var b64StubType = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(stubType));
            Console.WriteLine($"Processing external::{moduleName}::{typeName} with stub (base64):\n{b64StubType}");
            return typeEntry;
        });

        var matchExactMode = StringComparer.Ordinal;
        var whiteList = new HashSet<string>(methodStubList, matchExactMode);
        var nodesToRemove = new List<SyntaxNode>();
        foreach (var stubTypeNode in stubTypeNodes)
        {
            if (stubTypeNode == null) continue;
            var stub = stubTypeNode.ToFullString();
            foreach (var child in stubTypeNode.Members)
            {
                var code = child.ToString();
                Console.WriteLine($"Child node:\n{code}");
                if(whiteList.Contains(code)) {
                    Console.WriteLine("Keep this member.");
                } else {
                    Console.WriteLine("Remove this member.");
                    nodesToRemove.Add(child);
                }
            }
        }
        currentRoot = currentRoot.RemoveNodes(nodesToRemove, SyntaxRemoveOptions.KeepNoTrivia);

        moduleEntry.Map((typeName, typeEntry) => {
            var simpleTypeName = typeName.Contains('.') ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;
            var stubTypeNode = currentRoot.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => t.Identifier.Text == simpleTypeName);
            var stubType = stubTypeNode?.ToFullString() ?? "";
            var b64StubType = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(stubType));
            Console.WriteLine($"Final external::{moduleName}::{typeName} with stub (base64):\n{b64StubType}");
            return typeEntry;
        });

        var stubDir = Path.Combine(outDir, "0015-stubs");
        Directory.CreateDirectory(stubDir);
        var stubPath = Path.Combine(stubDir, $"{moduleName}.cs");
        File.WriteAllText(stubPath, currentRoot.ToFullString());

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

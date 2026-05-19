using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsStubGen;

class DllMapper
{
    // takes one module's already-decompiled stub string,
    // walks it with Roslyn, and removes methods / properties / fields / events
    // whose names are NOT in the source-member dictionary (externalMethodDict).
    public static TreeNode Execute(MethodsDictionary externalMethodDict, string moduleName, string moduleStub, string outDir)
    {

        var stubSyntaxTree = CSharpSyntaxTree.ParseText(moduleStub);
        var currentRoot = (CompilationUnitSyntax)stubSyntaxTree.GetRoot();
        var decompileIds = new List<EntityHandle>();
        var typeTree = new TreeNode("tree");
        var tree = recurse(currentRoot);
        tree.Name = moduleName;
        Console.WriteLine(tree.dumpJson());

        return typeTree;
    }

    public static TreeNode recurse(SyntaxNode currentRoot)
    {
        var name = currentRoot is TypeDeclarationSyntax tds ? tds.Identifier.Text : "root";
        var node = new TreeNode(name);

        var childNodes = currentRoot.ChildNodes()
            .OfType<TypeDeclarationSyntax>();
            
        foreach (var child in childNodes){
            var childNode = recurse(child);
            node.AddChild(childNode);
        }
        return node;
    }
}


class TreeNode
{
    public string Name { get; set; }
    public List<TreeNode> Children { get; set; } = new List<TreeNode>();

    public void find(string name, List<string> path)
    {
        path.Add(Name);
        if (Name == name)
        {
            Console.WriteLine($"Found node: {Name}");
            Console.WriteLine($"Path: {string.Join(" -> ", path)}");
            return;
        }
        foreach (var child in Children)
        {
            child.find(name, path);
        }
        path.RemoveAt(path.Count - 1);
    }
    public List<string> GetPathToNode(string name)
    {
        var path = new List<string>();
        find(name, path);
        return path;
    }
    public void AddChild(TreeNode child)
    {
        Children.Add(child);
    }
    public TreeNode(string name)
    {
        Name = name;
    }
    public string dumpJson(int indent = 0)
    {
        var pad = new string(' ', indent * 2);
        var inner = new string(' ', (indent + 1) * 2);
        if (Children.Count == 0)
            return $"{pad}\"{Name}\": []";
        var childrenJson = string.Join(",\n", Children.Select(c => c.dumpJson(indent + 1)));
        return $"{pad}\"{Name}\": {{\n{childrenJson}\n{pad}}}";
    }
}
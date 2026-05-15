using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsStubGen;

public class StubPruner : CSharpSyntaxRewriter
{
    readonly Dictionary<string, HashSet<string>> _usedMembers;
    readonly HashSet<string> _knownTypes;
    readonly bool _verbose;
    readonly bool _debug;

    public StubPruner(Dictionary<string, HashSet<string>> usedMembers, HashSet<string> knownTypes, bool verbose = false, bool debug = false)
    {
        _usedMembers = usedMembers;
        _knownTypes = knownTypes;
        _verbose = verbose;
        _debug = debug;
    }

    public override SyntaxNode VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var fullName = GetFullTypeName(node);
        var keep = _knownTypes.Contains(fullName);
        if (_verbose)
            Console.WriteLine($"  [pruner] enum '{fullName}' → {(keep ? "KEEP (in knownTypes)" : "DELETE (not in knownTypes)")}");
        return keep ? node : null;
    }

    public override SyntaxNode VisitStructDeclaration(StructDeclarationSyntax node)
        => VisitTypeDecl(node);

    public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
        => VisitTypeDecl(node);

    public override SyntaxNode VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        => VisitTypeDecl(node);

    SyntaxNode VisitTypeDecl(TypeDeclarationSyntax node)
    {
        var fullName = GetFullTypeName(node);

        if (!_knownTypes.Contains(fullName))
        {
            if (_verbose)
                Console.WriteLine($"  [pruner] type '{fullName}' → DELETE (not in knownTypes)");
            return null;
        }

        var hasMembers = _usedMembers.TryGetValue(fullName, out var members);

        if (_verbose)
        {
            if (hasMembers)
                Console.WriteLine($"  [pruner] type '{fullName}' → KEEP (in knownTypes + usedMembers: {{{string.Join(", ", members)}}})");
            else
                Console.WriteLine($"  [pruner] type '{fullName}' → KEEP shell (in knownTypes, NOT in usedMembers → empty body)");
        }

        var newMembers = new List<MemberDeclarationSyntax>();
        foreach (var member in node.Members)
        {
            switch (member)
            {
                case EnumDeclarationSyntax:
                case ClassDeclarationSyntax:
                case StructDeclarationSyntax:
                case InterfaceDeclarationSyntax:
                    var visited = (MemberDeclarationSyntax)Visit(member);
                    if (visited != null) newMembers.Add(visited);
                    break;

                case FieldDeclarationSyntax field:
                    if (HasModifier(field.Modifiers, SyntaxKind.ConstKeyword))
                    {
                        newMembers.Add(field);
                    }
                    else if (members != null)
                    {
                        var varName = field.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
                        if (varName != null && members.Contains(varName))
                            newMembers.Add(field);
                        else if (_verbose)
                            Console.WriteLine($"    [pruner]   field '{varName}' → SKIP (not in usedMembers)");
                    }
                    break;

                case PropertyDeclarationSyntax prop:
                    if (members != null && members.Contains(prop.Identifier.Text))
                        newMembers.Add(prop);
                    else if (_verbose)
                        Console.WriteLine($"    [pruner]   prop '{prop.Identifier.Text}' → SKIP");
                    break;

                case MethodDeclarationSyntax method:
                    if (members != null && members.Contains(method.Identifier.Text))
                        newMembers.Add(method);
                    else if (_verbose)
                        Console.WriteLine($"    [pruner]   method '{method.Identifier.Text}' → SKIP");
                    break;

                case ConstructorDeclarationSyntax:
                    if (members != null && members.Count > 0)
                        newMembers.Add(member);
                    else if (_verbose)
                        Console.WriteLine($"    [pruner]   ctor → SKIP (no used members)");
                    break;

                case EventFieldDeclarationSyntax evtField:
                    var evtName = evtField.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
                    if (members != null && evtName != null && members.Contains(evtName))
                        newMembers.Add(member);
                    else if (_verbose)
                        Console.WriteLine($"    [pruner]   event '{evtName}' → SKIP");
                    break;

                case EventDeclarationSyntax evtDecl:
                    if (members != null && members.Contains(evtDecl.Identifier.Text))
                        newMembers.Add(member);
                    else if (_verbose)
                        Console.WriteLine($"    [pruner]   event '{evtDecl.Identifier.Text}' → SKIP");
                    break;

                case DelegateDeclarationSyntax del:
                    newMembers.Add(member);
                    if (_verbose)
                        Console.WriteLine($"    [pruner]   delegate '{del.Identifier.Text}' → KEEP (always)");
                    break;
            }
        }

        if (_verbose)
            Console.WriteLine($"  [pruner] type '{fullName}' → {newMembers.Count}/{node.Members.Count} members kept");

        return node.WithMembers(SyntaxFactory.List(newMembers));
    }

    static string GetFullTypeName(BaseTypeDeclarationSyntax node)
    {
        var parts = new List<string>();
        parts.Add(node.Identifier.Text);

        var parent = node.Parent;
        while (parent != null)
        {
            if (parent is TypeDeclarationSyntax parentType)
                parts.Add(parentType.Identifier.Text);
            else if (parent is NamespaceDeclarationSyntax ns)
            {
                parts.Add(ns.Name.ToString());
                break;
            }
            else if (parent is FileScopedNamespaceDeclarationSyntax fns)
            {
                parts.Add(fns.Name.ToString());
                break;
            }
            parent = parent.Parent;
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    static bool HasModifier(SyntaxTokenList modifiers, SyntaxKind kind)
        => modifiers.Any(m => m.IsKind(kind));
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsStubGen;

public class SourceAnalyzer : CSharpSyntaxWalker
{
    public HashSet<string> TypeNames { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> TypeMembers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> UnresolvedMembers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Namespaces { get; } = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _varTypes = new(StringComparer.Ordinal);

    // types declared in the source itself (these are NOT external)
    private readonly HashSet<string> _localTypes = new(StringComparer.Ordinal);

    public static SourceAnalyzer Analyze(IEnumerable<string> sourceFiles)
    {
        var analyzer = new SourceAnalyzer();
        foreach (var file in sourceFiles)
        {
            var code = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(code);
            // first pass: collect local type declarations
            foreach (var typeDecl in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                analyzer._localTypes.Add(typeDecl.Identifier.Text);
            // second pass: collect references
            analyzer.Visit(tree.GetRoot());
        }
        // remove locally declared types from external references
        foreach (var local in analyzer._localTypes)
            analyzer.TypeNames.Remove(local);
        return analyzer;
    }

    void AddType(string name)
    {
        if (!string.IsNullOrEmpty(name) && !IsBuiltinType(name))
            TypeNames.Add(name);
    }

    void AddTypeMember(string typeName, string memberName)
    {
        if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(memberName)) return;
        if (!TypeMembers.TryGetValue(typeName, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            TypeMembers[typeName] = set;
        }
        set.Add(memberName);
    }

    static bool IsBuiltinType(string name)
    {
        return name switch
        {
            "void" or "bool" or "byte" or "sbyte" or "char" or "short" or "ushort"
            or "int" or "uint" or "long" or "ulong" or "float" or "double" or "decimal"
            or "string" or "object" or "var" or "dynamic" or "nint" or "nuint" => true,
            _ => false
        };
    }

    string ExtractTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax gen => gen.Identifier.Text,
            QualifiedNameSyntax qual => qual.Right.Identifier.Text,
            NullableTypeSyntax nullable => ExtractTypeName(nullable.ElementType),
            ArrayTypeSyntax array => ExtractTypeName(array.ElementType),
            PredefinedTypeSyntax _ => null,
            _ => null
        };
    }

    void AddTypeFromSyntax(TypeSyntax type)
    {
        if (type == null) return;

        switch (type)
        {
            case IdentifierNameSyntax id:
                AddType(id.Identifier.Text);
                break;
            case GenericNameSyntax gen:
                AddType(gen.Identifier.Text);
                foreach (var arg in gen.TypeArgumentList.Arguments)
                    AddTypeFromSyntax(arg);
                break;
            case QualifiedNameSyntax qual:
                AddType(qual.Right.Identifier.Text);
                // left part might be a namespace
                if (qual.Left is IdentifierNameSyntax ns)
                    Namespaces.Add(ns.Identifier.Text);
                break;
            case NullableTypeSyntax nullable:
                AddTypeFromSyntax(nullable.ElementType);
                break;
            case ArrayTypeSyntax array:
                AddTypeFromSyntax(array.ElementType);
                break;
        }
    }

    string ResolveExpressionType(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case IdentifierNameSyntax id:
                if (_varTypes.TryGetValue(id.Identifier.Text, out var varType))
                    return varType;
                return id.Identifier.Text;

            case GenericNameSyntax gen:
                foreach (var arg in gen.TypeArgumentList.Arguments)
                    AddTypeFromSyntax(arg);
                return gen.Identifier.Text;

            case ParenthesizedExpressionSyntax paren:
                return ResolveExpressionType(paren.Expression);

            case CastExpressionSyntax cast:
                AddTypeFromSyntax(cast.Type);
                return ExtractTypeName(cast.Type);

            default:
                return null;
        }
    }

    // --- Visitors ---

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        if (node.Name != null)
            Namespaces.Add(node.Name.ToString());
        base.VisitUsingDirective(node);
    }

    public override void VisitParameter(ParameterSyntax node)
    {
        if (node.Type != null)
        {
            AddTypeFromSyntax(node.Type);
            var typeName = ExtractTypeName(node.Type);
            if (typeName != null && node.Identifier.Text.Length > 0)
                _varTypes[node.Identifier.Text] = typeName;
        }
        base.VisitParameter(node);
    }

    public override void VisitVariableDeclaration(VariableDeclarationSyntax node)
    {
        if (node.Type != null && !node.Type.IsVar)
        {
            AddTypeFromSyntax(node.Type);
            var typeName = ExtractTypeName(node.Type);
            if (typeName != null)
            {
                foreach (var v in node.Variables)
                    _varTypes[v.Identifier.Text] = typeName;
            }
        }
        base.VisitVariableDeclaration(node);
    }

    public override void VisitBaseList(BaseListSyntax node)
    {
        foreach (var baseType in node.Types)
            AddTypeFromSyntax(baseType.Type);
        base.VisitBaseList(node);
    }

    public override void VisitTypeOfExpression(TypeOfExpressionSyntax node)
    {
        AddTypeFromSyntax(node.Type);
        base.VisitTypeOfExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (node.Type != null)
            AddTypeFromSyntax(node.Type);
        base.VisitObjectCreationExpression(node);
    }

    public override void VisitCastExpression(CastExpressionSyntax node)
    {
        AddTypeFromSyntax(node.Type);
        base.VisitCastExpression(node);
    }

    public override void VisitGenericName(GenericNameSyntax node)
    {
        AddType(node.Identifier.Text);
        foreach (var arg in node.TypeArgumentList.Arguments)
            AddTypeFromSyntax(arg);
        base.VisitGenericName(node);
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // handle nameof(Type.Member)
        if (node.Expression is IdentifierNameSyntax id && id.Identifier.Text == "nameof"
            && node.ArgumentList.Arguments.Count == 1)
        {
            var arg = node.ArgumentList.Arguments[0].Expression;
            if (arg is MemberAccessExpressionSyntax ma)
            {
                var typeName = ResolveExpressionType(ma.Expression);
                if (typeName != null)
                {
                    AddType(typeName);
                    AddTypeMember(typeName, ma.Name.Identifier.Text);
                }
            }
        }
        base.VisitInvocationExpression(node);
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var memberName = node.Name.Identifier.Text;
        var typeName = ResolveExpressionType(node.Expression);

        if (typeName != null)
            AddTypeMember(typeName, memberName);
        else
            UnresolvedMembers.Add(memberName);

        base.VisitMemberAccessExpression(node);
    }

    public override void VisitMemberBindingExpression(MemberBindingExpressionSyntax node)
    {
        // handles ?.member — can't resolve type from syntax alone
        UnresolvedMembers.Add(node.Name.Identifier.Text);
        base.VisitMemberBindingExpression(node);
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        AddTypeFromSyntax(node.Declaration.Type);
        base.VisitFieldDeclaration(node);
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        AddTypeFromSyntax(node.Type);
        base.VisitPropertyDeclaration(node);
    }

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        AddTypeFromSyntax(node.ReturnType);
        base.VisitMethodDeclaration(node);
    }
}

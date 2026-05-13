using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsStubGen;

public class AnalysisResult
{
    public Dictionary<string, HashSet<string>> TypeMembers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Namespaces { get; } = new(StringComparer.Ordinal);
    public HashSet<string> BaseTypeNames { get; } = new(StringComparer.Ordinal);

    public void AddMember(string typeFullName, string memberName)
    {
        if (string.IsNullOrEmpty(typeFullName) || string.IsNullOrEmpty(memberName)) return;
        if (!TypeMembers.TryGetValue(typeFullName, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            TypeMembers[typeFullName] = set;
        }
        set.Add(memberName);
    }

    public void EnsureType(string typeFullName)
    {
        if (!string.IsNullOrEmpty(typeFullName) && !TypeMembers.ContainsKey(typeFullName))
            TypeMembers[typeFullName] = new HashSet<string>(StringComparer.Ordinal);
    }
}

public static class SourceAnalyzer
{
    public static AnalysisResult Analyze(IEnumerable<string> sourceFiles, IEnumerable<string> allDlls)
    {
        var result = new AnalysisResult();

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = sourceFiles
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOptions, path: f))
            .ToList();

        var refs = allDlls
            .Select(dll => (MetadataReference)MetadataReference.CreateFromFile(dll))
            .ToList();

        var compilation = CSharpCompilation.Create("StubAnalysis",
            syntaxTrees: trees,
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true));

        var localTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var td in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(td);
                if (symbol != null)
                    localTypes.Add(GetFullName(symbol));
            }
        }

        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (u.Name != null)
                    result.Namespaces.Add(u.Name.ToString());
            }

            foreach (var bl in root.DescendantNodes().OfType<BaseListSyntax>())
            {
                foreach (var bt in bl.Types)
                {
                    var sym = model.GetTypeInfo(bt.Type).Type;
                    if (sym != null && IsExternal(sym, localTypes))
                    {
                        var fn = GetFullName(sym);
                        result.BaseTypeNames.Add(fn);
                        result.EnsureType(fn);
                    }
                }
            }

            foreach (var node in root.DescendantNodes())
            {
                SymbolInfo? info = node switch
                {
                    MemberAccessExpressionSyntax => model.GetSymbolInfo(node),
                    MemberBindingExpressionSyntax => model.GetSymbolInfo(node),
                    _ => null
                };

                if (info is { } si)
                {
                    var symbol = si.Symbol ?? si.CandidateSymbols.FirstOrDefault();
                    if (symbol?.ContainingType != null && IsExternal(symbol.ContainingType, localTypes))
                        result.AddMember(GetFullName(symbol.ContainingType), symbol.Name);
                }
            }

            foreach (var typeSyntax in root.DescendantNodes().OfType<TypeSyntax>())
            {
                if (typeSyntax is PredefinedTypeSyntax) continue;
                if (typeSyntax is IdentifierNameSyntax idn && idn.Identifier.Text == "var") continue;

                var type = model.GetTypeInfo(typeSyntax).Type;
                if (type != null && IsExternal(type, localTypes))
                    result.EnsureType(GetFullName(type));
            }
        }

        var errors = compilation.GetDiagnostics()
            .Count(d => d.Severity == DiagnosticSeverity.Error);
        if (errors > 0)
            Console.WriteLine($"[csstubgen] Note: compilation had {errors} errors (missing references? use --lib for non-stub DLLs)");

        return result;
    }

    static bool IsExternal(ITypeSymbol type, HashSet<string> localTypes)
    {
        if (type.TypeKind == TypeKind.Error) return false;
        if (type.SpecialType != SpecialType.None) return false;
        if (localTypes.Contains(GetFullName(type))) return false;
        return true;
    }

    static string GetFullName(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named && named.IsGenericType)
            type = named.OriginalDefinition;

        var parts = new List<string>();
        parts.Add(type.Name);

        var container = type.ContainingType;
        while (container != null)
        {
            parts.Add(container.Name);
            container = container.ContainingType;
        }

        var ns = type.ContainingNamespace;
        if (ns != null && !ns.IsGlobalNamespace)
            parts.Add(ns.ToDisplayString());

        parts.Reverse();
        return string.Join(".", parts);
    }
}

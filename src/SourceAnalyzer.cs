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
    // Preserved for future ReferenceResolver compatibility (not populated yet)
    public Dictionary<string, HashSet<string>> TypeMembers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Namespaces { get; } = new(StringComparer.Ordinal);
    public HashSet<string> BaseTypeNames { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> NameofHints { get; } = new(StringComparer.Ordinal);

    // All unique method/function calls found in source — tag: "bcl" | "external" | "unresolved"
    public SortedDictionary<string, string> CalledMethods { get; } = new(StringComparer.Ordinal);

    // Approximate stub signature for resolved methods
    public SortedDictionary<string, string> MethodSignatures { get; } = new(StringComparer.Ordinal);

    // All types referenced in external method signatures, grouped by assembly
    public SortedDictionary<string, SortedSet<string>> ReferencedTypes { get; } = new(StringComparer.Ordinal);
}

public static class SourceAnalyzer
{
    public static AnalysisResult Analyze(IEnumerable<string> sourceFiles, IEnumerable<string> allDlls)
    {
        var result = new AnalysisResult();
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var dllList = allDlls.ToList();

        var trees = sourceFiles
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parseOptions, path: f))
            .ToList();

        var refs = dllList
            .Select(dll => (MetadataReference)MetadataReference.CreateFromFile(dll))
            .ToList();

        var compilation = CSharpCompilation.Create("StubAnalysis",
            syntaxTrees: trees,
            references: refs,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithAllowUnsafe(true)
                .WithMetadataImportOptions(MetadataImportOptions.All));

        foreach (var tree in trees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string entry = null;
                string receiver = null;

                switch (invocation.Expression)
                {
                    case MemberAccessExpressionSyntax ma:
                    {
                        var methodName = ma.Name.Identifier.Text;
                        receiver = ma.Expression switch
                        {
                            IdentifierNameSyntax id                 => id.Identifier.Text,
                            MemberAccessExpressionSyntax nested     => nested.Name.Identifier.Text,
                            PredefinedTypeSyntax pre                => pre.Keyword.Text,
                            BaseExpressionSyntax                    => "base",
                            ThisExpressionSyntax                    => "this",
                            _                                       => null
                        };
                        entry = receiver != null ? $"{receiver}.{methodName}" : methodName;
                        break;
                    }
                    case MemberBindingExpressionSyntax mb:
                        entry = mb.Name.Identifier.Text;
                        break;
                    case IdentifierNameSyntax id:
                        entry = id.Identifier.Text;
                        break;
                    case GenericNameSyntax gn:
                        entry = gn.Identifier.Text;
                        break;
                }

                if (entry == null || result.CalledMethods.ContainsKey(entry)) continue;

                var si = model.GetSymbolInfo(invocation);
                var symbol = si.Symbol ?? si.CandidateSymbols.FirstOrDefault();

                string tag;
                if (symbol == null)
                {
                    tag = "unresolved";
                }
                else
                {
                    ITypeSymbol receiverType = null;
                    if (invocation.Expression is MemberAccessExpressionSyntax ma2)
                        receiverType = model.GetTypeInfo(ma2.Expression).Type;

                    var resolvedAsm = receiverType?.ContainingAssembly ?? symbol.ContainingAssembly;
                    var asmName = resolvedAsm?.Name ?? "";
                    var typeName = receiverType?.ToDisplayString() ?? symbol.ContainingType?.ToDisplayString() ?? "?";

                    var bucket = string.IsNullOrEmpty(asmName) ? "unresolved"
                        : asmName == "StubAnalysis" ? "self"
                        : IsFrameworkAssembly(asmName) ? "bcl" : "external";

                    tag = $"{bucket} | {typeName} ({asmName})";
                }

                result.CalledMethods.Add(entry, tag);
                if (symbol is IMethodSymbol ms)
                {
                    result.MethodSignatures[entry] = BuildStub(ms);
                    if (tag.StartsWith("external"))
                    {
                        CollectTypes(ms.ReturnType, result.ReferencedTypes);
                        foreach (var p in ms.Parameters)
                            CollectTypes(p.Type, result.ReferencedTypes);
                    }
                }
            }
        }

        return result;
    }

    static string BuildStub(IMethodSymbol m)
    {
        var acc = m.DeclaredAccessibility switch
        {
            Accessibility.Public    => "public",
            Accessibility.Protected => "protected",
            Accessibility.Internal  => "internal",
            _                       => "public"
        };
        var stat = m.IsStatic ? " static" : "";
        var ret  = m.ReturnType.ToDisplayString();
        var parms = string.Join(", ", m.Parameters.Select(p =>
        {
            var prefix = p.RefKind switch
            {
                RefKind.Ref  => "ref ",
                RefKind.Out  => "out ",
                RefKind.In   => "in ",
                _            => ""
            };
            return $"{prefix}{p.Type.ToDisplayString()} {p.Name}";
        }));
        return $"{acc}{stat} {ret} {m.Name}({parms}) {{ throw new System.NotImplementedException(); }}";
    }

    static void CollectTypes(ITypeSymbol type, SortedDictionary<string, SortedSet<string>> dest)
    {
        if (type == null || type.SpecialType != SpecialType.None) return;
        switch (type)
        {
            case IArrayTypeSymbol arr:
                CollectTypes(arr.ElementType, dest);
                return;
            case INamedTypeSymbol named:
            {
                var asm = named.ContainingAssembly?.Name;
                if (asm == null) return;
                if (!dest.TryGetValue(asm, out var set))
                    dest[asm] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(named.ToDisplayString());
                foreach (var ta in named.TypeArguments)
                    CollectTypes(ta, dest);
                break;
            }
        }
    }

    static bool IsFrameworkAssembly(string name) =>
        name == "mscorlib" || name == "netstandard" || name == "System.Private.CoreLib"
        || name == "System" || name.StartsWith("System.") || name.StartsWith("Microsoft.CSharp");
}

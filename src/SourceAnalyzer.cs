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
    // Used members per type: "Rewired.Controller" → { "GetButtonDown", "GetButton" }
    public Dictionary<string, HashSet<string>> UsedMembers { get; } = new(StringComparer.Ordinal);

    // Which assembly each type belongs to: "Rewired.Controller" → "Rewired_Core"
    public Dictionary<string, string> TypeAssembly { get; } = new(StringComparer.Ordinal);

    // All types referenced in external method signatures, grouped by assembly
    public SortedDictionary<string, SortedSet<string>> ReferencedTypes { get; } = new(StringComparer.Ordinal);

    // Raw called methods for verbose logging
    public List<CalledMethodEntry> CalledMethods { get; } = new();
}

public class CalledMethodEntry
{
    public string Method { get; set; }
    public string Group { get; set; }
    public string Type { get; set; }
    public string Source { get; set; }
    public string Details { get; set; }
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

                if (entry == null || result.CalledMethods.Any(m => m.Method == entry)) continue;

                var si = model.GetSymbolInfo(invocation);
                var symbol = si.Symbol ?? si.CandidateSymbols.FirstOrDefault();

                string group;
                string typeName = "?";
                string asmName = "";

                if (symbol == null)
                {
                    group = "unresolved";
                }
                else
                {
                    ITypeSymbol receiverType = null;
                    if (invocation.Expression is MemberAccessExpressionSyntax ma2)
                        receiverType = model.GetTypeInfo(ma2.Expression).Type;

                    var resolvedAsm = receiverType?.ContainingAssembly ?? symbol.ContainingAssembly;
                    asmName = resolvedAsm?.Name ?? "";
                    typeName = receiverType?.ToDisplayString() ?? symbol.ContainingType?.ToDisplayString() ?? "?";

                    group = string.IsNullOrEmpty(asmName) ? "unresolved"
                        : asmName == "StubAnalysis" ? "self"
                        : IsFrameworkAssembly(asmName) ? "bcl" : "external";

                    if (group == "external" && symbol is IMethodSymbol ms)
                    {
                        var methodName = ms.Name;

                        if (!result.UsedMembers.TryGetValue(typeName, out var members))
                            result.UsedMembers[typeName] = members = new HashSet<string>(StringComparer.Ordinal);
                        members.Add(methodName);

                        if (!result.TypeAssembly.ContainsKey(typeName))
                            result.TypeAssembly[typeName] = asmName;

                        CollectTypes(ms.ReturnType, result.ReferencedTypes);
                        foreach (var p in ms.Parameters)
                            CollectTypes(p.Type, result.ReferencedTypes);
                    }
                }

                result.CalledMethods.Add(new CalledMethodEntry
                {
                    Method = entry,
                    Group = group,
                    Type = typeName,
                    Source = asmName,
                    Details = $"{typeName} ({asmName})"
                });
            }

            // Also collect member access on fields/properties/events.
            // These also need to flow into CalledMethods so MethodsParser picks them up
            // (otherwise the stub whitelist has no fields/props and TreeReplace nukes them).
            foreach (var memberAccess in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var si = model.GetSymbolInfo(memberAccess);
                var symbol = si.Symbol ?? si.CandidateSymbols.FirstOrDefault();
                if (symbol == null) continue;

                var kind = symbol.Kind;
                bool isProperty = kind == SymbolKind.Property;
                bool isField    = kind == SymbolKind.Field;
                bool isEvent    = kind == SymbolKind.Event;
                if (!isProperty && !isField && !isEvent) continue;

                var containingType = symbol.ContainingType;
                var asm = containingType?.ContainingAssembly;
                if (asm == null || IsFrameworkAssembly(asm.Name) || asm.Name == "StubAnalysis") continue;

                var typeName = containingType.ToDisplayString();
                var memberName = symbol.Name;
                var asmName = asm.Name;

                // existing UsedMembers / TypeAssembly bookkeeping
                if (!result.UsedMembers.TryGetValue(typeName, out var members))
                    result.UsedMembers[typeName] = members = new HashSet<string>(StringComparer.Ordinal);
                members.Add(memberName);

                if (!result.TypeAssembly.ContainsKey(typeName))
                    result.TypeAssembly[typeName] = asmName;

                // also push it into CalledMethods so MethodsParser puts it in MethodsDictionary.
                // dedup by name like the invocation loop above.
                bool alreadyTracked = result.CalledMethods.Any(m => m.Method == memberName);
                if (alreadyTracked) continue;

                result.CalledMethods.Add(new CalledMethodEntry
                {
                    Method = memberName,
                    Group = "external",
                    Type = typeName,
                    Source = asmName,
                    Details = $"{typeName} ({asmName})"
                });
            }
        }

        return result;
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

    public static bool IsFrameworkAssembly(string name) =>
        name == "mscorlib" || name == "netstandard" || name == "System.Private.CoreLib"
        || name == "System" || name.StartsWith("System.") || name.StartsWith("Microsoft.CSharp");
}

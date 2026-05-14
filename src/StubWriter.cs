using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CsStubGen;

public static class StubWriter
{
    public static void Write(AnalysisResult analysis, IEnumerable<string> refDlls, string outDir, bool verbose = false, bool debug = false)
    {
        Directory.CreateDirectory(outDir);

        // Map assembly name → DLL path
        var dllByAssembly = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dll in refDlls)
        {
            try
            {
                var decompiler = new CSharpDecompiler(dll, new DecompilerSettings());
                var asmName = decompiler.TypeSystem.MainModule.AssemblyName;
                dllByAssembly[asmName] = dll;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[warn] Could not read assembly name from {dll}: {ex.Message}");
            }
        }

        // Figure out which types to decompile per assembly
        var typesPerAssembly = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // Types with used members
        foreach (var (typeName, asmName) in analysis.TypeAssembly)
        {
            if (!typesPerAssembly.TryGetValue(asmName, out var set))
                typesPerAssembly[asmName] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(typeName);
        }

        // Types referenced in signatures (parameters, return types)
        foreach (var (asmName, typeNames) in analysis.ReferencedTypes)
        {
            if (SourceAnalyzer.IsFrameworkAssembly(asmName)) continue;
            if (!typesPerAssembly.TryGetValue(asmName, out var set))
                typesPerAssembly[asmName] = set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in typeNames)
                set.Add(t);
        }

        // Build known types set: all types mod uses or references
        var knownTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeName in analysis.UsedMembers.Keys)
            knownTypes.Add(typeName);
        foreach (var (asmName2, typeSet) in analysis.ReferencedTypes)
        {
            if (SourceAnalyzer.IsFrameworkAssembly(asmName2)) continue;
            foreach (var t in typeSet)
                knownTypes.Add(t);
        }

        var generatedAssemblies = new List<string>();

        foreach (var (asmName, typeNames) in typesPerAssembly.OrderBy(kv => kv.Key))
        {
            if (!dllByAssembly.TryGetValue(asmName, out var dllPath))
            {
                Console.Error.WriteLine($"[warn] No DLL found for assembly '{asmName}', skipping");
                continue;
            }

            var asmDir = Path.Combine(outDir, asmName);
            Directory.CreateDirectory(asmDir);

            var stubSource = DecompileAndPrune(dllPath, typeNames, analysis.UsedMembers, knownTypes, verbose, debug);
            if (string.IsNullOrWhiteSpace(stubSource)) continue;

            var header = "// =============================================================================\n"
                       + "// AUTO-GENERATED STUB by csstubgen — DO NOT EDIT\n"
                       + "// =============================================================================\n\n";

            File.WriteAllText(Path.Combine(asmDir, "Stubs.cs"), header + stubSource);
            WriteCsproj(asmName, asmDir);
            generatedAssemblies.Add(asmName);
            Console.WriteLine($"[csstubgen] Generated stubs for {asmName} ({typeNames.Count} types)");

            if (debug)
            {
                Console.Write("[debug] Press Enter to continue to next assembly (q to quit)... ");
                var input = Console.ReadLine();
                if (input?.Trim().ToLowerInvariant() == "q") return;
            }
        }

        if (generatedAssemblies.Count > 0)
            WriteStubsProps(generatedAssemblies, outDir);
    }

        static string DecompileAndPrune(string dllPath, HashSet<string> typeNames,
        Dictionary<string, HashSet<string>> usedMembers, HashSet<string> knownTypes, bool verbose, bool debug = false)
    {
        var settings = new DecompilerSettings
        {
            ThrowOnAssemblyResolveErrors = false,
            AlwaysUseBraces = true,
            ShowXmlDocumentation = false,
            DecompileMemberBodies = false,
        };

        var decompiler = new CSharpDecompiler(dllPath, settings);

        var sb = new StringBuilder();
        var decompiled = new HashSet<string>(StringComparer.Ordinal);

        // Build lookup of types available in this assembly
        var availableTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var td in decompiler.TypeSystem.MainModule.TypeDefinitions)
            availableTypes.Add(td.ReflectionName);


        var asmName = Path.GetFileNameWithoutExtension(dllPath);
        var debugDir = debug ? Path.Combine("debug", asmName) : null;
        if (debugDir != null) Directory.CreateDirectory(debugDir);

        foreach (var typeName in typeNames.OrderBy(x => x))
        {
            var cleanName = typeName;
            var angleBracket = cleanName.IndexOf('<');
            if (angleBracket >= 0)
                cleanName = cleanName.Substring(0, angleBracket);

            if (!decompiled.Add(cleanName)) continue;

            var reflectionName = cleanName;
            if (!availableTypes.Contains(reflectionName))
            {
                Console.Error.WriteLine($"[warn] Type '{cleanName}' not found in {Path.GetFileName(dllPath)}, skipping");
                continue;
            }

            string source;
            try
            {
                var fullTypeName = new FullTypeName(reflectionName);
                source = decompiler.DecompileTypeAsString(fullTypeName);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[warn] Could not decompile type '{cleanName}': {ex.Message}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(source)) continue;

            var safeName = cleanName.Replace(".", "_");

            if (debug)
            {
                File.WriteAllText(Path.Combine(debugDir, $"{safeName}.raw.cs"), source);
                Console.WriteLine($"  [debug] wrote {safeName}.raw.cs");
            }

            // Parse and prune each type individually
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetRoot();

            if (verbose)
            {
                var diags = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                if (diags.Count > 0)
                    Console.WriteLine($"  [debug] parse errors for {cleanName}: {diags.Count} (first: {diags[0]})");
            }

            var pruner = new StubPruner(usedMembers, knownTypes, verbose, debug);
            var pruned = pruner.Visit(root);
            var prunedText = pruned.NormalizeWhitespace().ToFullString();

            if (debug)
            {
                File.WriteAllText(Path.Combine(debugDir, $"{safeName}.pruned.cs"), prunedText);
                Console.WriteLine($"  [debug] wrote {safeName}.pruned.cs");
                Console.Write($"  [debug] Press Enter to continue (q to quit)... ");
                var input = Console.ReadLine();
                if (input?.Trim().ToLowerInvariant() == "q")
                    Environment.Exit(0);
            }

            sb.AppendLine(prunedText);
        }

        var combined = sb.ToString();
        if (string.IsNullOrWhiteSpace(combined)) return combined;

        return combined;
    }

    static void WriteCsproj(string assemblyName, string dir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net472</TargetFramework>");
        sb.AppendLine($"    <AssemblyName>{assemblyName}</AssemblyName>");
        sb.AppendLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
        sb.AppendLine("    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>");
        sb.AppendLine("    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
        sb.AppendLine("    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>");
        sb.AppendLine("    <NoWarn>CS0649;CS0169;CS0414;CS0626</NoWarn>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("</Project>");

        File.WriteAllText(Path.Combine(dir, $"{assemblyName}.csproj"), sb.ToString());
    }

    static void WriteStubsProps(IEnumerable<string> assemblyNames, string outDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project>");
        sb.AppendLine("  <!-- Game-specific stubs (minimal signatures, no proprietary code) -->");
        sb.AppendLine("  <ItemGroup>");
        foreach (var assemblyName in assemblyNames)
        {
            sb.AppendLine($"    <ProjectReference Include=\"$(MSBuildThisFileDirectory){assemblyName}\\{assemblyName}.csproj\">");
            sb.AppendLine("      <Private>false</Private>");
            sb.AppendLine("    </ProjectReference>");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");

        File.WriteAllText(Path.Combine(outDir, "Stubs.props"), sb.ToString());
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CsStubGen;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var sourcePaths = new List<string>();
        var refPaths = new List<string>();
        var libPaths = new List<string>();
        string outDir = "./stubs";
        bool verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-s" or "--source":
                    if (++i < args.Length) sourcePaths.Add(args[i]);
                    break;
                case "-r" or "--ref":
                    if (++i < args.Length) refPaths.Add(args[i]);
                    break;
                case "-l" or "--lib":
                    if (++i < args.Length) libPaths.Add(args[i]);
                    break;
                case "-o" or "--out":
                    if (++i < args.Length) outDir = args[i];
                    break;
                case "--unity-version":
                    if (++i < args.Length) { /* ignored */ }
                    break;
                case "-v" or "--verbose":
                    verbose = true;
                    break;
            }
        }

        if (sourcePaths.Count == 0)
        {
            Console.Error.WriteLine("Error: no source files specified (-s)");
            return 1;
        }
        if (refPaths.Count == 0)
        {
            Console.Error.WriteLine("Error: no reference DLLs specified (-r)");
            return 1;
        }

        var sourceFiles = sourcePaths.SelectMany(p => ResolveFiles(p, "*.cs")).ToList();
        var refDlls = refPaths.SelectMany(p => ResolveFiles(p, "*.dll")).ToList();
        var libDlls = libPaths.SelectMany(p => ResolveFiles(p, "*.dll")).ToList();

        if (sourceFiles.Count == 0)
        {
            Console.Error.WriteLine("Error: no .cs files found in specified source paths");
            return 1;
        }

        Console.WriteLine($"[csstubgen] Source files: {sourceFiles.Count}");
        foreach (var f in sourceFiles)
            Console.WriteLine($"  {f}");

        var analysis = SourceAnalyzer.Analyze(sourceFiles, refDlls.Concat(libDlls));

        Console.WriteLine($"\n[csstubgen] Found {analysis.CalledMethods.Count} unique method calls:\n");
        var buckets = verbose
            ? new[] { "bcl", "external", "self", "unresolved" }
            : new string[] { };
        foreach (var bucket in buckets)
        {
            var methods = analysis.CalledMethods.Where(kv => kv.Value.StartsWith(bucket)).ToList();
            if (methods.Count == 0) continue;

            Console.WriteLine($"-- [{bucket}] ({methods.Count}) --");

            foreach (var typeGroup in methods.GroupBy(kv => kv.Value).OrderBy(g => g.Key))
            {
                Console.WriteLine($"");
                foreach (var (method, tag) in typeGroup.OrderBy(kv => kv.Key))
                    Console.WriteLine($"    {method,-55} {tag}");
            }
            Console.WriteLine();
        }

        // Print approximate stub signatures for each external method
        var externalMethods = analysis.CalledMethods
            .Where(kv => kv.Value.StartsWith("external"))
            .ToList();

        Console.WriteLine($"-- [stubs] ({externalMethods.Count}) --\n");
        if (verbose)
        {
            foreach (var (method, _) in externalMethods)
            {
                if (analysis.MethodSignatures.TryGetValue(method, out var stub))
                {
                    Console.WriteLine($"    {method}:");
                    Console.WriteLine($"    {stub}");
                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine($"    {method}:");
                    Console.WriteLine($"    // (unresolved signature)");
                    Console.WriteLine();
                }
            }
        }
        else
        {
            Console.WriteLine($"    stub to build: {string.Join(", ", externalMethods.Select(kv => kv.Key))}");
        }
        Console.WriteLine();

        // Print all types referenced in external method signatures, grouped by assembly
        var totalTypes = analysis.ReferencedTypes.Values.Sum(s => s.Count);
        Console.WriteLine($"-- [referenced types] ({totalTypes}) --\n");
        if (verbose)
        {
            foreach (var (asm, types) in analysis.ReferencedTypes)
            {
                Console.WriteLine($"  [{asm}]");
                foreach (var t in types)
                    Console.WriteLine($"    {t}");
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine($"    {string.Join(", ", analysis.ReferencedTypes.Keys)}");
        }
        Console.WriteLine();

        // Print class shells with stub methods grouped by declaring type
        var classGroups = new SortedDictionary<string, (string typeName, string asm, List<string> stubs)>(StringComparer.Ordinal);
        foreach (var (method, tag) in externalMethods)
        {
            if (!analysis.MethodSignatures.TryGetValue(method, out var stub)) continue;
            var afterPipe = tag.Substring(tag.IndexOf('|') + 2).Trim();
            var parenIdx = afterPipe.LastIndexOf('(');
            var typeName = afterPipe.Substring(0, parenIdx).Trim();
            var asmName = afterPipe.Substring(parenIdx + 1, afterPipe.Length - parenIdx - 2);
            var key = $"{asmName}::{typeName}";
            if (!classGroups.ContainsKey(key))
                classGroups[key] = (typeName, asmName, new List<string>());
            classGroups[key].stubs.Add(stub);
        }

        Console.WriteLine($"-- [class stubs] ({classGroups.Count}) --\n");
        if (verbose)
        {
            string lastAsm = null;
            foreach (var kv in classGroups)
            {
                var (typeName, asmName, stubs) = kv.Value;
                if (asmName != lastAsm)
                {
                    if (lastAsm != null) Console.WriteLine();
                    Console.WriteLine($"  // [{asmName}]");
                    lastAsm = asmName;
                }
                var simpleName = typeName.Contains('.') ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;
                var nameComment = typeName != simpleName ? $" // {typeName}" : "";
                Console.WriteLine($"  public class {simpleName}{nameComment}");
                Console.WriteLine($"  {{");
                foreach (var stub in stubs)
                    Console.WriteLine($"      {stub}");
                Console.WriteLine($"  }}");
            }
        }
        else
        {
            Console.WriteLine($"building ...");
            Console.WriteLine($"done!");
        }
        Console.WriteLine();

        // Print the .cs file content that would be written per assembly
        var fileGroups = classGroups
            .GroupBy(kv => kv.Value.asm)
            .OrderBy(g => g.Key);

        Console.WriteLine($"-- [generated .cs files] --\n");
        if (verbose)
        {
            foreach (var asmGroup in fileGroups)
            {
                var asmName = asmGroup.Key;
                Console.WriteLine($"  // ===== {asmName}.cs =====");
                Console.WriteLine();

                var allStubs = asmGroup.SelectMany(kv => kv.Value.stubs).ToList();
                var usings = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var stub in allStubs)
                {
                    foreach (var ns in ExtractNamespaces(stub))
                        usings.Add(ns);
                }
                foreach (var u in usings)
                    Console.WriteLine($"  using {u};");
                if (usings.Count > 0) Console.WriteLine();

                Console.WriteLine($"  namespace {asmName}");
                Console.WriteLine($"  {{");
                foreach (var kv in asmGroup)
                {
                    var (typeName, _, stubs) = kv.Value;
                    var simpleName = typeName.Contains('.') ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;
                    var nameComment = typeName != simpleName ? $" // {typeName}" : "";
                    Console.WriteLine($"      public class {simpleName}{nameComment}");
                    Console.WriteLine($"      {{");
                    foreach (var stub in stubs)
                        Console.WriteLine($"          {stub}");
                    Console.WriteLine($"      }}");
                    Console.WriteLine();
                }
                Console.WriteLine($"  }}");
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine($"building ...");
            Console.WriteLine($"done!");
        }
        Console.WriteLine();

        StubWriter.Write(classGroups, outDir);

        return 0;
    }

    static IEnumerable<string> ExtractNamespaces(string stub)
        => StubWriter.ExtractNamespaces(stub);

    static IEnumerable<string> ResolveFiles(string path, string pattern)
    {
        if (Directory.Exists(path))
            return Directory.GetFiles(path, pattern, SearchOption.AllDirectories);
        if (File.Exists(path))
            return new[] { path };
        return Enumerable.Empty<string>();
    }

    static void PrintUsage()
    {
        Console.WriteLine("csstubgen - Generate minimal C# stubs from source + reference DLLs");
        Console.WriteLine();
        Console.WriteLine("Usage: csstubgen -s <source> -r <reference> [-l <library>] [-o <output>]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -s, --source <path>       Source .cs files or directory (required, repeatable)");
        Console.WriteLine("  -r, --ref <path>          Reference DLL or directory to generate stubs for (required, repeatable)");
        Console.WriteLine("  -l, --lib <path>          Library DLL or directory for analysis only, no stubs (repeatable)");
        Console.WriteLine("  -o, --out <path>          Output directory (default: ./stubs)");
        Console.WriteLine("  -h, --help                Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  csstubgen -s ./MyMod.cs -r ./Assembly-CSharp.dll -l ./Managed/ -o ./stubs/");
        Console.WriteLine("  csstubgen -s ./src/ -r ./Assembly-CSharp.dll -r ./Mirage.dll -l ./BepInEx.dll -o ./stubs/");
    }
}

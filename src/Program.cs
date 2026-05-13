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
            : new[] { "external" };
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

        return 0;
    }

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

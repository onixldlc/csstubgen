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
        string outDir = "./stubs";
        string unityVersion = "2022.3.9";

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
                case "-o" or "--out":
                    if (++i < args.Length) outDir = args[i];
                    break;
                case "--unity-version":
                    if (++i < args.Length) unityVersion = args[i];
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

        if (sourceFiles.Count == 0)
        {
            Console.Error.WriteLine("Error: no .cs files found in specified source paths");
            return 1;
        }
        if (refDlls.Count == 0)
        {
            Console.Error.WriteLine("Error: no .dll files found in specified reference paths");
            return 1;
        }

        Console.WriteLine($"[csstubgen] Source files: {sourceFiles.Count}");
        foreach (var f in sourceFiles)
            Console.WriteLine($"  {f}");
        Console.WriteLine($"[csstubgen] Reference DLLs: {refDlls.Count}");
        foreach (var f in refDlls)
            Console.WriteLine($"  {f}");

        var analysis = SourceAnalyzer.Analyze(sourceFiles);
        Console.WriteLine($"[csstubgen] Referenced types: {string.Join(", ", analysis.TypeNames.OrderBy(x => x))}");
        Console.WriteLine($"[csstubgen] Resolved member accesses: {analysis.TypeMembers.Sum(kv => kv.Value.Count)}");
        Console.WriteLine($"[csstubgen] Unresolved members: {string.Join(", ", analysis.UnresolvedMembers.OrderBy(x => x))}");

        Console.WriteLine("[csstubgen] Resolving members:");
        var result = ReferenceResolver.Resolve(analysis, refDlls);
        Console.WriteLine($"[csstubgen] Stub types: {result.TypesByAssembly.Sum(kv => kv.Value.Count)}");
        foreach (var (asm, types) in result.TypesByAssembly.OrderBy(kv => kv.Key))
        {
            Console.WriteLine($"  {asm}: {string.Join(", ", types.Select(t => t.Definition.Name).OrderBy(x => x))}");
        }

        StubWriter.Write(result, outDir, unityVersion);
        Console.WriteLine($"[csstubgen] Stubs written to: {outDir}");

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
        Console.WriteLine("Usage: csstubgen -s <source> -r <reference> [-o <output>]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -s, --source <path>       Source .cs files or directory (required, repeatable)");
        Console.WriteLine("  -r, --ref <path>          Reference DLL or directory (required, repeatable)");
        Console.WriteLine("  -o, --out <path>          Output directory (default: ./stubs)");
        Console.WriteLine("  --unity-version <ver>     UnityEngine.Modules NuGet version (default: 2022.3.9)");
        Console.WriteLine("  -h, --help                Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  csstubgen -s ./MyMod.cs -r ./stripped/ -o ./stubs/");
        Console.WriteLine("  csstubgen -s ./src/ -r ./Assembly-CSharp.dll -r ./Mirage.dll -o ./ci/stubs/");
    }
}

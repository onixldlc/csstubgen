using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CsStubGen;

class Logic
{
    public static int Run(List<string> sourcePaths, List<string> refPaths, List<string> libPaths, string outDir, bool verbose, bool debug)
    {
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

        
        var methods_dictionary = FilterMethods.Execute(sourceFiles, refDlls, libDlls, outDir, debug);

        // Console.WriteLine($"\n[csstubgen] Done. Output: {non_unity_core}");

        // convert any source with . to the actual folder structure. 
        /*
        var exploaded = external_methods
        for(const [bucket] of exploaded){
            for(const [source] of exploaded.bucket){
                if(source.includes('.'){

                } 
            }
        }

        */

        // var exploaded_json = System.Text.Json.JsonSerializer.Serialize(exploaded, serialize_options);
        // File.WriteAllText(Path.Combine(outDir, "0006-analysis_exploaded.json"), exploaded_json);








        































        // Console.WriteLine($"[csstubgen] Source files: {sourceFiles.Count}");
        // foreach (var f in sourceFiles)
        //     Console.WriteLine($"  {f}");

        // var analysis = SourceAnalyzer.Analyze(sourceFiles, refDlls.Concat(libDlls));

        // Console.WriteLine($"\n[csstubgen] Found {analysis.CalledMethods.Count} unique method calls");

        // if (verbose)
        // {
        //     foreach (var bucket in new[] { "bcl", "external", "self", "unresolved" })
        //     {
        //         var methods = analysis.CalledMethods.Where(kv => kv.Value.StartsWith(bucket)).ToList();
        //         if (methods.Count == 0) continue;
        //         Console.WriteLine($"\n-- [{bucket}] ({methods.Count}) --");
        //         foreach (var (method, tag) in methods.OrderBy(kv => kv.Key))
        //             Console.WriteLine($"    {method,-55} {tag}");
        //     }
        // }

        // Console.WriteLine($"\n[csstubgen] Used types: {analysis.UsedMembers.Count}");
        // if (verbose)
        // {
        //     foreach (var (typeName, members) in analysis.UsedMembers.OrderBy(kv => kv.Key))
        //     {
        //         var asm = analysis.TypeAssembly.GetValueOrDefault(typeName, "?");
        //         Console.WriteLine($"  {typeName} ({asm})");
        //         foreach (var m in members.OrderBy(x => x))
        //             Console.WriteLine($"    .{m}");
        //     }
        // }

        // Console.WriteLine($"\n[csstubgen] Referenced types in signatures:");
        // foreach (var (asm, types) in analysis.ReferencedTypes)
        // {
        //     if (SourceAnalyzer.IsFrameworkAssembly(asm)) continue;
        //     Console.WriteLine($"  [{asm}] {types.Count} types");
        //     if (verbose)
        //         foreach (var t in types)
        //             Console.WriteLine($"    {t}");
        // }

        // Console.WriteLine($"\n[csstubgen] Generating stubs...");
        // StubWriter.Write(analysis, refDlls, outDir, verbose, debug);

        // Console.WriteLine($"\n[csstubgen] Done. Output: {outDir}");
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
}

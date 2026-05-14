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

        
        // dump the analysis result to ./analysis.json for debugging
        var analysis_result = SourceAnalyzer.Analyze(sourceFiles, refDlls.Concat(libDlls));
        var serialize_options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = System.Text.Json.JsonSerializer.Serialize(analysis_result.CalledMethods, serialize_options);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "0001-analysis.json"), json);

        // for each called method, group by bucket (bcl, external, self, unresolved) as json for debugging
        var bucket_grouped = analysis_result.CalledMethods
            .GroupBy(e => e.Group)
            .ToDictionary(g => g.Key, g => g.Select(e => new { e.Method, e.Type, e.Source, e.Details }).ToList());
        var bucket_grouped_json = System.Text.Json.JsonSerializer.Serialize(bucket_grouped, serialize_options);
        File.WriteAllText(Path.Combine(outDir, "0002-analysis_grouped.json"), bucket_grouped_json);

        var bucket_source_grouped = analysis_result.CalledMethods
            .GroupBy(e => e.Group)
            .ToDictionary(g => g.Key, g => g.GroupBy(e => e.Source).ToDictionary(sg => sg.Key, sg => sg.Select(e => new { e.Method, e.Type, e.Details }).ToList()));
        var bucket_source_grouped_json = System.Text.Json.JsonSerializer.Serialize(bucket_source_grouped, serialize_options);
        File.WriteAllText(Path.Combine(outDir, "0003-analysis_grouped_by_source.json"), bucket_source_grouped_json);

        // if method has variable (known by . in the name) then remove the variable name and keep just the method name
        var method_grouped = bucket_source_grouped_json.SelectMany(kv => kv);
        Console.WriteLine($"\n[csstubgen] Found {method_grouped} unique method calls");


























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

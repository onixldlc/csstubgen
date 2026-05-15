using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using System.Reflection.Metadata;

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

        // 1. structure the analysis result 
        var methods_dictionary = MethodsParser.Execute(sourceFiles, refDlls, libDlls, outDir, debug);

        // 2. filter the resulting analysis
        var external_methods = methods_dictionary.Filter((key, bucket) => key == "external");
        if(debug){
            var external_methods_json = external_methods.Json();
            File.WriteAllText(Path.Combine(outDir, "0008-external_methods.json"), external_methods_json);
        }

        var no_unity_core = external_methods
            .Map((_, bucket) => bucket.Filter((key, _) => key != "UnityEngine.CoreModule"));
        if(debug){
            var no_unity_core_json = no_unity_core.Json();
            File.WriteAllText(Path.Combine(outDir, "0009-no_unity_core.json"), no_unity_core_json);
        }

        // 3. go trough all the entries and decompiles per dll required
        var type_list = StubStructurizer.Execute(no_unity_core, refDlls, libDlls, outDir, debug);


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

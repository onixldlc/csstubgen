using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace CsStubGen;

class StubBuilder
{
    public static Dictionary<string, string> FullStubModule { get; set; } = new Dictionary<string, string>();
    public static Dictionary<string, string> GetFullStubModule(){return FullStubModule;}

    public static void Execute(Dictionary<string, EntityHandle[]> idModules, IEnumerable<string> refDlls, IEnumerable<string> libDlls, string outDir, bool debug)
    {
        // signature-only output: keeps file small and reduces missing-type errors
        var settings = DecompilerOptions.Build();

        // rebuild assembly-name -> dll path lookup (same as stub_method_generator)
        var dllByAsm = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dll in refDlls.Concat(libDlls))
        {
            try {
                var probe = new CSharpDecompiler(dll, new DecompilerSettings());
                dllByAsm[probe.TypeSystem.MainModule.AssemblyName] = dll;
            } catch { }
        }

        var stubDir = Path.Combine(outDir, "0013-stubs");
        Directory.CreateDirectory(stubDir);

        foreach (var kv in idModules)
        {
            var moduleName = kv.Key;
            var typeHandles = kv.Value;

            // need a dll for this module to be able to decompile
            var hasDll = dllByAsm.TryGetValue(moduleName, out var dll);
            if (!hasDll) {
                Console.Error.WriteLine($"[warn] no dll for {moduleName}");
                continue;
            }

            // fresh decompiler per module so the settings stick
            var dec = new CSharpDecompiler(dll, settings);

            try {
                // decompile ALL the matched types in one go -> get one wrapped .cs
                // (namespace + class scaffolding included, signatures only)
                var src = dec.DecompileAsString(typeHandles);
                FullStubModule[moduleName] = src;
                var outPath = Path.Combine(stubDir, $"{moduleName}.cs");
                File.WriteAllText(outPath, src);
            } catch (Exception ex) {
                Console.Error.WriteLine($"[warn] {moduleName}: {ex.Message}");
            }
        }
    }
}

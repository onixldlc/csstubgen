using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace CsStubGen;

class StubStructurizer
{
    public static MethodsDictionary Execute(MethodsDictionary srcMethodDict, IEnumerable<string> refDlls, IEnumerable<string> libDlls, string outDir, bool debug)
    {
        var settings = new DecompilerSettings {
            ThrowOnAssemblyResolveErrors = false,
            AlwaysUseBraces = true,
            ShowXmlDocumentation = false,
            DecompileMemberBodies = false,
        };

        var dllByAsm = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dll in refDlls.Concat(libDlls))
        {
            try {
                var probe = new CSharpDecompiler(dll, new DecompilerSettings());
                dllByAsm[probe.TypeSystem.MainModule.AssemblyName] = dll;
            } catch { }
        }

        var decByAsm = new Dictionary<string, CSharpDecompiler>(StringComparer.Ordinal);
        var stubDir = Path.Combine(outDir, "0010-stubs");
        Directory.CreateDirectory(stubDir);

        var stubDict = srcMethodDict
        .Map((_, bucket) => bucket
            .Map((moduleName, moduleEntry) => moduleEntry
                .Map((typeName, typeEntry) => {

                    // load decompiler if exist, create one if doesn't exist
                    var hasCachedDecompiler = decByAsm.TryGetValue(moduleName, out var dec);
                    if (!hasCachedDecompiler)
                    {
                        var hasDll = dllByAsm.TryGetValue(moduleName, out var dll);
                        if (!hasDll) {
                            Console.Error.WriteLine($"[warn] no dll for {moduleName}");
                            return typeEntry;
                        }
                        dec = new CSharpDecompiler(dll, settings);
                        decByAsm[moduleName] = dec;
                    }

                    // check if typeName is a "List<int>"
                    var baseTypeName = typeName;
                    var isGeneric = typeName.Contains('<');
                    if (isGeneric)
                    {
                        // get base type by stripping <T> (subtypes)
                        baseTypeName = typeName.Substring(0, typeName.IndexOf('<'));
                    }

                    // list all Types that are in the DLL, if the type is not found, skip
                    var allTypesInDll = dec.TypeSystem.MainModule.TypeDefinitions;
                    var typeDef = allTypesInDll
                        .FirstOrDefault(t => t.ReflectionName == baseTypeName);
                    var typeExists = (typeDef != null);
                    if (!typeExists) {
                        Console.Error.WriteLine($"[warn] type {baseTypeName} not found in {moduleName}");
                        return typeEntry;
                    }

                    // build a list of all the method in hash for quick lookup
                    var matchExactMode = StringComparer.Ordinal;
                    var srcMethodList = typeEntry.method.Select(m => m);
                    var methodNameList = typeEntry.method.Select(m => m.method);
                    var methodHashList = new HashSet<string>(methodNameList, matchExactMode);

                    // loop over every method in the DLL, match it against the source code list 
                    var allDllMethods = typeDef.Methods;
                    var matchedMethodIds = new List<EntityHandle>();
                    var srcMethodNameDict = new Dictionary<string, EntityHandle>();
                    
                    
                    var stubbedMethods = new Dictionary<string, string>();
                    
                    foreach (var m in allDllMethods)
                    {
                        var methodMatched = methodHashList.Contains(m.Name);
                        if (methodMatched)
                        {
                            // if matched returns the method's id in inside the dll
                            var dllsMethodId = m.MetadataToken;
                            srcMethodNameDict[m.Name] = dllsMethodId;
                            matchedMethodIds.Add(dllsMethodId);
                        }
                    }

                    // if no method matched, skip
                    if (matchedMethodIds.Count == 0)return typeEntry;

                    foreach(var method in srcMethodList){
                        var methodName = method.method;
                        if (!srcMethodNameDict.TryGetValue(methodName, out var dllsMethodId)) continue;
                        try {
                            var src = dec.DecompileAsString(dllsMethodId);
                            method.stub = src;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[warn] {baseTypeName}: {ex.Message}");
                        }
                    }

                    // -- DECOMPILE ONLY THE MATCHED METHODS --
                    // decompile the matched method to src
                    try {
                        var src = dec.DecompileAsString(matchedMethodIds);
                        
                        // sanitize the file name and save to: outDir/0010-stubs/AssemblyName__TypeName.cs
                        var sanitizedFileName = baseTypeName
                            .Replace(".", "_")
                            .Replace("/", "_")
                            .Replace("+", "_");
                        var outputPath = Path.Combine(stubDir, $"{moduleName}__{sanitizedFileName}.cs");
                        File.WriteAllText(outputPath, src);
                    } catch (Exception ex) {
                        Console.Error.WriteLine($"[warn] {baseTypeName}: {ex.Message}");
                    }
                    return typeEntry;
                })
            )
        );
        var stubDictJson = stubDict.Json();
        File.WriteAllText(Path.Combine(outDir, "0010-stub_structure.json"), stubDictJson);
        return stubDict;
    }
}

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
    public static Dictionary<string, EntityHandle[]> IdModules { get; set; } = new Dictionary<string, EntityHandle[]>();
    public static Dictionary<string, string> HalfStubbedModules { get; set; } = new Dictionary<string, string>();
    public static Dictionary<string, string> StubbedTypes { get; set; } = new Dictionary<string, string>();
    public static Dictionary<string, string> GetHalfStubbedModules(){return HalfStubbedModules;}
    public static Dictionary<string, string> GetStubbedTypes(){return StubbedTypes;}
    public static Dictionary<string, EntityHandle[]> GetIdModules(){return IdModules; }
    public static string JsonHalfStubbedModules(){return Jsonify(HalfStubbedModules);}
    public static string JsonStubbedTypes(){return Jsonify(StubbedTypes);}
    private static string Jsonify(object obj)
    {
        return System.Text.Json.JsonSerializer.Serialize(obj, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }


    public static MethodsDictionary Execute(MethodsDictionary srcMethodDict, IEnumerable<string> refDlls, IEnumerable<string> libDlls, string outDir, bool debug)
    {
        var settings = DecompilerOptions.Build();

        var dllByAsm = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dll in refDlls.Concat(libDlls))
        {
            try {
                var probe = new CSharpDecompiler(dll, new DecompilerSettings());
                dllByAsm[probe.TypeSystem.MainModule.AssemblyName] = dll;
            } catch { }
        }

        var decByAsm = new Dictionary<string, CSharpDecompiler>(StringComparer.Ordinal);
        var stubDir = Path.Combine(outDir, "0011-stubs");
        Directory.CreateDirectory(stubDir);

        var stubDict = srcMethodDict
        .Map((_, bucket) => bucket
            .Map((moduleName, moduleEntry) => {
                var matchedMethodIds = new List<EntityHandle>();
                var matchedTypeIds = new List<EntityHandle>();
                var result = moduleEntry
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
                    matchedTypeIds.Add(typeDef.MetadataToken);

                    // build a list of all the method in hash for quick lookup
                    var matchExactMode = StringComparer.Ordinal;
                    var srcMethodList = typeEntry.method.Select(m => m);
                    var methodNameList = typeEntry.method.Select(m => m.method);
                    var methodHashList = new HashSet<string>(methodNameList, matchExactMode);

                    // loop over every method in the DLL, match it against the source code list 
                    var allDllMethods = typeDef.Methods;
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
                    if (srcMethodNameDict.Count == 0)return typeEntry;

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

                    // decompile all matched methods of THIS type to one combined string
                    // and store it in StubbedTypes. no file dump, no early return.
                    // any post-processing / file output is the job of stage 4.
                    var typeMethodIds = srcMethodNameDict.Values.ToList();
                    if (typeMethodIds.Count > 0)
                    {
                        try {
                            var typeStub = dec.DecompileAsString(typeMethodIds);
                            var typeKey = $"{moduleName}__{baseTypeName}";
                            StubbedTypes[typeKey] = typeStub;
                        } catch (Exception ex) {
                            Console.Error.WriteLine($"[warn] {baseTypeName}: {ex.Message}");
                        }
                    }

                    return typeEntry;
                });

                // export the matched type handles for the stub builder step
                if (matchedTypeIds.Count > 0) {
                    IdModules[moduleName] = matchedTypeIds.ToArray();
                }

                // decompile all matched methods of this module to one file
                if (matchedMethodIds.Count > 0 && decByAsm.TryGetValue(moduleName, out var moduleDec)) {
                    try {
                        var src = moduleDec.DecompileAsString(matchedMethodIds);
                        HalfStubbedModules[moduleName] = src;
                        if(debug){
                            var outputPath = Path.Combine(stubDir, $"{moduleName}.cs");
                            File.WriteAllText(outputPath, src);
                        }
                    } catch (Exception ex) {
                        Console.Error.WriteLine($"[warn] {moduleName}: {ex.Message}");
                    }
                }
                return result;
            })
        );
        if(debug){
            var stubDictJson = stubDict.Json();
            File.WriteAllText(Path.Combine(outDir, "0010-stub_structure.json"), stubDictJson);
        }
        return stubDict;
    }
}

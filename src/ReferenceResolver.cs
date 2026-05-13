using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace CsStubGen;

public class ResolvedType
{
    public TypeDefinition Definition;
    public HashSet<string> NeededMembers = new(StringComparer.Ordinal);
    public string AssemblyName;
}

public class ResolverResult
{
    public Dictionary<string, List<ResolvedType>> TypesByAssembly = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> AssemblyDependencies = new(StringComparer.Ordinal);

    public void AddType(ResolvedType rt)
    {
        if (!TypesByAssembly.TryGetValue(rt.AssemblyName, out var list))
        {
            list = new List<ResolvedType>();
            TypesByAssembly[rt.AssemblyName] = list;
        }
        list.Add(rt);
    }

    public void AddDependency(string from, string to)
    {
        if (from == to) return;
        if (!AssemblyDependencies.TryGetValue(from, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            AssemblyDependencies[from] = set;
        }
        set.Add(to);
    }
}

public static class ReferenceResolver
{
    public static ResolverResult Resolve(AnalysisResult analysis, IEnumerable<string> refDlls)
    {
        var result = new ResolverResult();

        var typeIndex = new Dictionary<string, List<(TypeDefinition Type, string Assembly)>>(StringComparer.Ordinal);
        var assemblies = new List<AssemblyDefinition>();
        var knownAssemblies = new HashSet<string>(StringComparer.Ordinal);
        var dllPaths = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var dll in refDlls)
        {
            var asm = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { ReadWrite = false });
            assemblies.Add(asm);
            var asmName = asm.Name.Name;
            knownAssemblies.Add(asmName);
            dllPaths[asmName] = dll;

            foreach (var type in asm.MainModule.Types)
            {
                if (type.Name == "<Module>") continue;
                AddToIndex(typeIndex, GetCecilKey(type), type, asmName);

                foreach (var nested in type.NestedTypes)
                    AddToIndex(typeIndex, GetCecilKey(nested), nested, asmName);
            }
        }

        var processed = new HashSet<string>(StringComparer.Ordinal);
        var shellQueue = new Queue<string>();

        // Layer 1: types + members directly used by source
        foreach (var (typeFullName, members) in analysis.TypeMembers)
        {
            if (processed.Contains(typeFullName)) continue;
            processed.Add(typeFullName);

            var entry = FindInIndex(typeIndex, typeFullName);
            if (entry == null) continue;
            var (typeDef, asmName) = entry.Value;

            if (IsUnityAssembly(asmName) || IsFrameworkAssembly(asmName)) continue;

            var rt = new ResolvedType
            {
                Definition = typeDef,
                AssemblyName = asmName
            };

            var dllLabel = dllPaths.TryGetValue(asmName, out var dp) ? dp : asmName + ".dll";

            foreach (var m in members)
            {
                if (HasMember(typeDef, m))
                {
                    rt.NeededMembers.Add(m);
                    Console.WriteLine($"  {dllLabel} -> {typeDef.Name}.{m} ({GetMemberKind(typeDef, m)})");
                }
            }

            result.AddType(rt);
            EnqueueShellDeps(typeDef, rt, shellQueue, knownAssemblies, result, asmName);
        }

        // Layer 2: empty shell types (exist for stub compilation, no members)
        ProcessShellQueue(shellQueue, processed, typeIndex, knownAssemblies, result);

        // Layer 3: abstract overrides for stub→stub inheritance
        AddAbstractOverrides(result);

        // Layer 4: signature types for methods added by abstract overrides
        var shellQueue2 = new Queue<string>();
        foreach (var typeList in result.TypesByAssembly.Values)
            foreach (var rt in typeList)
                EnqueueShellDeps(rt.Definition, rt, shellQueue2, knownAssemblies, result, rt.AssemblyName);
        ProcessShellQueue(shellQueue2, processed, typeIndex, knownAssemblies, result);

        return result;
    }

    static void ProcessShellQueue(Queue<string> shellQueue, HashSet<string> processed,
        Dictionary<string, List<(TypeDefinition Type, string Assembly)>> typeIndex,
        HashSet<string> knownAssemblies, ResolverResult result)
    {
        while (shellQueue.Count > 0)
        {
            var name = shellQueue.Dequeue();
            if (processed.Contains(name)) continue;
            processed.Add(name);

            var entry = FindInIndex(typeIndex, name);
            if (entry == null) continue;
            var (typeDef, asmName) = entry.Value;

            if (IsUnityAssembly(asmName) || IsFrameworkAssembly(asmName)) continue;

            var rt = new ResolvedType
            {
                Definition = typeDef,
                AssemblyName = asmName
            };

            result.AddType(rt);

            if (typeDef.BaseType != null && !IsTrivialBase(typeDef.BaseType))
            {
                shellQueue.Enqueue(GetCecilKey(typeDef.BaseType));

                var baseScope = GetAssemblyScope(typeDef.BaseType);
                if (baseScope != null && knownAssemblies.Contains(baseScope) && baseScope != asmName)
                    result.AddDependency(asmName, baseScope);
            }
        }
    }

    static void EnqueueShellDeps(TypeDefinition typeDef, ResolvedType rt,
        Queue<string> shellQueue, HashSet<string> knownAssemblies,
        ResolverResult result, string asmName)
    {
        if (typeDef.BaseType != null && !IsTrivialBase(typeDef.BaseType))
        {
            shellQueue.Enqueue(GetCecilKey(typeDef.BaseType));

            var baseScope = GetAssemblyScope(typeDef.BaseType);
            if (baseScope != null && knownAssemblies.Contains(baseScope) && baseScope != asmName)
                result.AddDependency(asmName, baseScope);
        }

        foreach (var field in typeDef.Fields)
        {
            if (!field.IsPublic || !rt.NeededMembers.Contains(field.Name)) continue;
            EnqueueSignatureType(field.FieldType, shellQueue, knownAssemblies, result, asmName);
        }

        foreach (var method in typeDef.Methods)
        {
            if (!method.IsPublic || method.IsConstructor || !rt.NeededMembers.Contains(method.Name)) continue;
            EnqueueSignatureType(method.ReturnType, shellQueue, knownAssemblies, result, asmName);
            foreach (var p in method.Parameters)
                EnqueueSignatureType(p.ParameterType, shellQueue, knownAssemblies, result, asmName);
        }

        foreach (var prop in typeDef.Properties)
        {
            if (!rt.NeededMembers.Contains(prop.Name)) continue;
            EnqueueSignatureType(prop.PropertyType, shellQueue, knownAssemblies, result, asmName);
        }
    }

    static void EnqueueSignatureType(TypeReference typeRef, Queue<string> shellQueue,
        HashSet<string> knownAssemblies, ResolverResult result, string currentAssembly)
    {
        if (typeRef == null || typeRef is GenericParameter) return;

        if (typeRef is GenericInstanceType gen)
        {
            EnqueueSignatureType(gen.ElementType, shellQueue, knownAssemblies, result, currentAssembly);
            foreach (var arg in gen.GenericArguments)
                EnqueueSignatureType(arg, shellQueue, knownAssemblies, result, currentAssembly);
            return;
        }

        if (typeRef is ArrayType arr)
        {
            EnqueueSignatureType(arr.ElementType, shellQueue, knownAssemblies, result, currentAssembly);
            return;
        }

        if (typeRef is ByReferenceType byRef)
        {
            EnqueueSignatureType(byRef.ElementType, shellQueue, knownAssemblies, result, currentAssembly);
            return;
        }

        var ns = typeRef.Namespace ?? "";
        if (ns == "System" || ns.StartsWith("System.")) return;

        var scope = GetAssemblyScope(typeRef);
        if (scope != null && knownAssemblies.Contains(scope) && scope != currentAssembly)
            result.AddDependency(currentAssembly, scope);

        shellQueue.Enqueue(GetCecilKey(typeRef));
    }

    static void AddAbstractOverrides(ResolverResult result)
    {
        var allStubTypes = result.TypesByAssembly.Values.SelectMany(x => x).ToList();
        var stubByKey = new Dictionary<string, ResolvedType>(StringComparer.Ordinal);
        foreach (var rt in allStubTypes)
            stubByKey.TryAdd(GetCecilKey(rt.Definition), rt);

        foreach (var rt in allStubTypes)
        {
            var baseRef = rt.Definition.BaseType;
            while (baseRef != null)
            {
                var baseName = GetCecilKey(baseRef);
                if (!stubByKey.TryGetValue(baseName, out var baseStub)) break;

                if (baseStub.Definition.IsAbstract)
                {
                    foreach (var method in baseStub.Definition.Methods)
                    {
                        if (!method.IsAbstract) continue;

                        baseStub.NeededMembers.Add(method.Name);

                        var overrideMethod = rt.Definition.Methods
                            .FirstOrDefault(m => m.Name == method.Name
                                && m.IsVirtual && !m.IsNewSlot);
                        if (overrideMethod != null)
                            rt.NeededMembers.Add(overrideMethod.Name);
                    }
                }

                try { baseRef = baseStub.Definition.BaseType; }
                catch { break; }
            }
        }
    }

    static void AddToIndex(Dictionary<string, List<(TypeDefinition, string)>> index,
        string key, TypeDefinition type, string assembly)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = new List<(TypeDefinition, string)>();
            index[key] = list;
        }
        list.Add((type, assembly));
    }

    static (TypeDefinition Type, string Assembly)? FindInIndex(
        Dictionary<string, List<(TypeDefinition Type, string Assembly)>> index,
        string key)
    {
        if (!index.TryGetValue(key, out var candidates) || candidates.Count == 0)
            return null;

        if (candidates.Count == 1)
            return candidates[0];

        foreach (var c in candidates)
        {
            var ns = c.Type.Namespace ?? "";
            if (!ns.StartsWith("System") && !ns.StartsWith("Mono."))
                return c;
        }

        return candidates[0];
    }

    static string GetCecilKey(TypeReference type)
    {
        if (type is GenericInstanceType gen)
            type = gen.ElementType;
        if (type is ByReferenceType byRef)
            type = byRef.ElementType;
        if (type is ArrayType arr)
            type = arr.ElementType;

        var name = StripGenericArity(type.Name);

        if (type.IsNested && type.DeclaringType != null)
        {
            var parent = StripGenericArity(type.DeclaringType.Name);
            var ns = type.DeclaringType.Namespace;
            if (string.IsNullOrEmpty(ns))
                return parent + "." + name;
            return ns + "." + parent + "." + name;
        }

        if (string.IsNullOrEmpty(type.Namespace))
            return name;
        return type.Namespace + "." + name;
    }

    static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick >= 0 ? name.Substring(0, tick) : name;
    }

    static string GetAssemblyScope(TypeReference type)
    {
        if (type is GenericInstanceType gen)
            return GetAssemblyScope(gen.ElementType);
        return (type.Scope as AssemblyNameReference)?.Name
            ?? (type.Scope as ModuleDefinition)?.Assembly?.Name?.Name;
    }

    static bool HasMember(TypeDefinition type, string name)
    {
        return type.Fields.Any(f => f.IsPublic && f.Name == name)
            || type.Methods.Any(m => m.IsPublic && m.Name == name)
            || type.Properties.Any(p => p.Name == name);
    }

    static string GetMemberKind(TypeDefinition type, string name)
    {
        if (type.Fields.Any(f => f.IsPublic && f.Name == name)) return "field";
        if (type.Properties.Any(p => p.Name == name)) return "property";
        if (type.Methods.Any(m => m.IsPublic && m.Name == name)) return "method";
        return "member";
    }

    static bool IsTrivialBase(TypeReference baseType)
    {
        var fn = baseType.FullName;
        return fn == "System.Object" || fn == "System.ValueType" || fn == "System.Enum";
    }

    static bool IsUnityAssembly(string assemblyName)
    {
        return assemblyName == "UnityEngine" || assemblyName.StartsWith("UnityEngine.");
    }

    static bool IsFrameworkAssembly(string assemblyName)
    {
        return assemblyName == "mscorlib"
            || assemblyName == "netstandard"
            || assemblyName == "System"
            || assemblyName.StartsWith("System.");
    }
}

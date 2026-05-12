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
    public static ResolverResult Resolve(SourceAnalyzer analysis, IEnumerable<string> refDlls)
    {
        var result = new ResolverResult();

        // global type index: typeName -> all candidates (handles name collisions)
        var typeIndex = new Dictionary<string, List<(TypeDefinition Type, string Assembly)>>(StringComparer.Ordinal);
        var assemblies = new List<AssemblyDefinition>();
        var knownAssemblies = new HashSet<string>(StringComparer.Ordinal);
        var dllPaths = new Dictionary<string, string>(StringComparer.Ordinal); // asmName -> dll path

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
                var cleanName = StripGenericArity(type.Name);
                AddToTypeIndex(typeIndex, cleanName, type, asmName);

                foreach (var nested in type.NestedTypes)
                {
                    var nestedClean = StripGenericArity(nested.Name);
                    AddToTypeIndex(typeIndex, nestedClean, nested, asmName);
                }
            }
        }

        // track which types we've already processed
        var processed = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        // seed queue with directly referenced types
        foreach (var typeName in analysis.TypeNames)
        {
            if (ResolveFromIndex(typeIndex, typeName, analysis.Namespaces) != null)
                queue.Enqueue(typeName);
        }

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (processed.Contains(name)) continue;
            processed.Add(name);

            var resolved = ResolveFromIndex(typeIndex, name, analysis.Namespaces);
            if (resolved == null) continue;
            var (typeDef, asmName) = resolved.Value;

            var rt = new ResolvedType
            {
                Definition = typeDef,
                AssemblyName = asmName
            };

            var dllLabel = dllPaths.TryGetValue(asmName, out var dp) ? dp : asmName + ".dll";

            // add explicitly mapped members
            if (analysis.TypeMembers.TryGetValue(name, out var explicitMembers))
            {
                foreach (var m in explicitMembers)
                {
                    rt.NeededMembers.Add(m);
                    Console.WriteLine($"  {dllLabel} -> {typeDef.Name}.{m} ({GetMemberKind(typeDef, m)}) [explicit]");
                }
            }

            // add unresolved members that match any member on this type
            foreach (var memberName in analysis.UnresolvedMembers)
            {
                if (HasMember(typeDef, memberName))
                {
                    rt.NeededMembers.Add(memberName);
                    Console.WriteLine($"  {dllLabel} -> {typeDef.Name}.{memberName} ({GetMemberKind(typeDef, memberName)}) [unresolved match]");
                }
            }

            // if abstract, include all abstract methods (required for subclass compilation)
            if (typeDef.IsAbstract)
            {
                foreach (var method in typeDef.Methods)
                {
                    if (method.IsAbstract)
                    {
                        rt.NeededMembers.Add(method.Name);
                        Console.WriteLine($"  {dllLabel} -> {typeDef.Name}.{method.Name} (method) [abstract]");
                    }
                }
            }

            result.AddType(rt);

            // walk dependencies

            // base type
            EnqueueBaseType(typeDef, queue, knownAssemblies, result, asmName);

            // for needed fields: enqueue field types
            foreach (var field in typeDef.Fields)
            {
                if (!field.IsPublic) continue;
                if (!rt.NeededMembers.Contains(field.Name)) continue;
                EnqueueTypeRef(field.FieldType, queue, knownAssemblies, result, asmName);
            }

            // for needed methods: enqueue parameter and return types
            foreach (var method in typeDef.Methods)
            {
                if (!method.IsPublic) continue;
                if (method.IsConstructor) continue;
                if (!rt.NeededMembers.Contains(method.Name)) continue;

                EnqueueTypeRef(method.ReturnType, queue, knownAssemblies, result, asmName);
                foreach (var param in method.Parameters)
                    EnqueueTypeRef(param.ParameterType, queue, knownAssemblies, result, asmName);
            }

            // for needed properties
            foreach (var prop in typeDef.Properties)
            {
                if (!rt.NeededMembers.Contains(prop.Name)) continue;
                EnqueueTypeRef(prop.PropertyType, queue, knownAssemblies, result, asmName);
            }

            // interfaces: only if directly referenced in source
            foreach (var iface in typeDef.Interfaces)
            {
                var ifaceName = GetSimpleTypeName(iface.InterfaceType);
                if (analysis.TypeNames.Contains(ifaceName))
                    queue.Enqueue(ifaceName);
            }
        }

        // second pass: for types extending abstract base types in our stubs,
        // ensure abstract method overrides are included
        AddAbstractOverrides(result, typeIndex, analysis.Namespaces);

        return result;
    }

    static void AddAbstractOverrides(ResolverResult result,
        Dictionary<string, List<(TypeDefinition Type, string Assembly)>> typeIndex,
        HashSet<string> sourceNamespaces)
    {
        var allStubTypes = result.TypesByAssembly.Values.SelectMany(x => x).ToList();
        var stubTypeNames = new HashSet<string>(allStubTypes.Select(t => StripGenericArity(t.Definition.Name)));

        foreach (var rt in allStubTypes)
        {
            var baseType = rt.Definition.BaseType;
            while (baseType != null)
            {
                var baseName = GetSimpleTypeName(baseType);
                if (!stubTypeNames.Contains(baseName)) break;

                var baseResolved = ResolveFromIndex(typeIndex, baseName, sourceNamespaces);
                if (baseResolved != null && baseResolved.Value.Type.IsAbstract)
                {
                    foreach (var method in baseResolved.Value.Type.Methods)
                    {
                        if (method.IsAbstract)
                        {
                            // find the override in current type
                            var overrideMethod = rt.Definition.Methods
                                .FirstOrDefault(m => m.Name == method.Name
                                    && m.IsVirtual && !m.IsNewSlot);
                            if (overrideMethod != null)
                                rt.NeededMembers.Add(overrideMethod.Name);
                        }
                    }
                }

                // walk up
                var walkResolved = ResolveFromIndex(typeIndex, baseName, sourceNamespaces);
                if (walkResolved != null)
                    baseType = walkResolved.Value.Type.BaseType;
                else
                    break;
            }
        }
    }

    static void AddToTypeIndex(Dictionary<string, List<(TypeDefinition Type, string Assembly)>> index,
        string name, TypeDefinition type, string assembly)
    {
        if (!index.TryGetValue(name, out var candidates))
        {
            candidates = new List<(TypeDefinition, string)>();
            index[name] = candidates;
        }
        candidates.Add((type, assembly));
    }

    static (TypeDefinition Type, string Assembly)? ResolveFromIndex(
        Dictionary<string, List<(TypeDefinition Type, string Assembly)>> index,
        string name, HashSet<string> sourceNamespaces)
    {
        if (!index.TryGetValue(name, out var candidates) || candidates.Count == 0)
            return null;

        if (candidates.Count == 1)
            return candidates[0];

        // prefer type whose namespace matches a using directive from source
        foreach (var c in candidates)
        {
            if (sourceNamespaces.Contains(c.Type.Namespace ?? ""))
                return c;
        }

        // prefer non-System/non-Mono types (likely game assemblies)
        foreach (var c in candidates)
        {
            var ns = c.Type.Namespace ?? "";
            if (!ns.StartsWith("System") && !ns.StartsWith("Mono."))
                return c;
        }

        return candidates[0];
    }

    static void EnqueueBaseType(TypeDefinition type, Queue<string> queue,
        HashSet<string> knownAssemblies, ResolverResult result, string currentAssembly)
    {
        if (type.BaseType == null) return;
        var baseName = GetSimpleTypeName(type.BaseType);
        if (baseName == "Object" || baseName == "ValueType" || baseName == "Enum") return;

        var baseScope = GetAssemblyScope(type.BaseType);
        if (baseScope != null && knownAssemblies.Contains(baseScope) && baseScope != currentAssembly)
            result.AddDependency(currentAssembly, baseScope);

        queue.Enqueue(baseName);
    }

    static void EnqueueTypeRef(TypeReference typeRef, Queue<string> queue,
        HashSet<string> knownAssemblies, ResolverResult result, string currentAssembly)
    {
        if (typeRef == null) return;

        var name = GetSimpleTypeName(typeRef);
        if (IsFrameworkType(name)) return;

        var scope = GetAssemblyScope(typeRef);
        if (scope != null && knownAssemblies.Contains(scope) && scope != currentAssembly)
            result.AddDependency(currentAssembly, scope);

        queue.Enqueue(name);

        // recurse into generic arguments
        if (typeRef is GenericInstanceType gen)
        {
            foreach (var arg in gen.GenericArguments)
                EnqueueTypeRef(arg, queue, knownAssemblies, result, currentAssembly);
        }
        else if (typeRef is ArrayType arr)
        {
            EnqueueTypeRef(arr.ElementType, queue, knownAssemblies, result, currentAssembly);
        }
        else if (typeRef is ByReferenceType byRef)
        {
            EnqueueTypeRef(byRef.ElementType, queue, knownAssemblies, result, currentAssembly);
        }
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

    static string GetSimpleTypeName(TypeReference type)
    {
        if (type is GenericInstanceType gen)
            return StripGenericArity(gen.ElementType.Name);
        return StripGenericArity(type.Name);
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

    static bool IsFrameworkType(string name)
    {
        return name switch
        {
            "Object" or "String" or "Boolean" or "Int32" or "Int64" or "Single"
            or "Double" or "Byte" or "Void" or "ValueType" or "Enum" or "Type"
            or "Action" or "Func" or "Task" or "Nullable" => true,
            _ => false
        };
    }
}

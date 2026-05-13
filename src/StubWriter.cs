using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

namespace CsStubGen;

public static class StubWriter
{
    public static void Write(ResolverResult result, string outDir, string unityVersion)
    {
        Directory.CreateDirectory(outDir);

        foreach (var (assemblyName, types) in result.TypesByAssembly)
        {
            var asmDir = Path.Combine(outDir, assemblyName);
            Directory.CreateDirectory(asmDir);

            WriteStubCs(types, Path.Combine(asmDir, "Stubs.cs"));
            WriteCsproj(assemblyName, result, asmDir, unityVersion);
        }
    }

    static void WriteStubCs(List<ResolvedType> types, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// =============================================================================");
        sb.AppendLine("// AUTO-GENERATED STUB by csstubgen — DO NOT EDIT");
        sb.AppendLine("// =============================================================================");
        sb.AppendLine();

        var usings = CollectUsings(types);
        foreach (var ns in usings.OrderBy(x => x))
            sb.AppendLine($"using {ns};");
        if (usings.Count > 0) sb.AppendLine();

        var byNamespace = types.GroupBy(t => t.Definition.Namespace ?? "").OrderBy(g => g.Key);

        foreach (var group in byNamespace)
        {
            bool hasNs = !string.IsNullOrEmpty(group.Key);
            if (hasNs)
            {
                sb.AppendLine($"namespace {group.Key}");
                sb.AppendLine("{");
            }

            string indent = hasNs ? "    " : "";

            // sort: interfaces first, then abstract classes, then classes
            var sorted = group.OrderBy(t =>
                t.Definition.IsInterface ? 0 :
                t.Definition.IsAbstract ? 1 : 2)
                .ThenBy(t => GetInheritanceDepth(t.Definition));

            foreach (var rt in sorted)
            {
                WriteType(sb, rt, indent);
                sb.AppendLine();
            }

            if (hasNs)
                sb.AppendLine("}");
        }

        File.WriteAllText(path, sb.ToString());
    }

    static void WriteType(StringBuilder sb, ResolvedType rt, string indent)
    {
        var type = rt.Definition;

        // modifiers
        var mods = new List<string>();
        if (type.IsPublic || type.IsNestedPublic) mods.Add("public");
        else mods.Add("internal");
        if (type.IsAbstract && type.IsSealed) mods.Add("static");
        else if (type.IsAbstract && !type.IsInterface) mods.Add("abstract");

        var keyword = type.IsInterface ? "interface" :
                      type.IsEnum ? "enum" :
                      type.IsValueType && !type.IsEnum ? "struct" : "class";

        // generic parameters
        var genericParams = FormatGenericParams(type);
        var constraints = FormatGenericConstraints(type, indent);

        // base type and interfaces
        var baseParts = new List<string>();
        if (type.BaseType != null
            && type.BaseType.FullName != "System.Object"
            && type.BaseType.FullName != "System.ValueType"
            && type.BaseType.FullName != "System.Enum")
        {
            baseParts.Add(FormatTypeRef(type.BaseType));
        }

        // only include interfaces that are in our stub set
        // (we don't track this perfectly, so include all — they'll compile
        // as long as the interface type exists in NuGet or stubs)
        foreach (var iface in type.Interfaces)
        {
            var ifaceName = StripGenericArity(iface.InterfaceType.Name);
            // only include if we have a stub for it (otherwise it's from NuGet/framework)
            // skip for now — interfaces from the same assembly will be in stubs,
            // others are assumed external
        }

        var baseClause = baseParts.Count > 0 ? " : " + string.Join(", ", baseParts) : "";

        sb.AppendLine($"{indent}{string.Join(" ", mods)} {keyword} {StripGenericArity(type.Name)}{genericParams}{baseClause}{constraints}");
        sb.AppendLine($"{indent}{{");

        var memberIndent = indent + "    ";

        if (type.IsEnum)
        {
            // enum members use declaration syntax, not field syntax
            var enumFields = type.Fields
                .Where(f => f.IsPublic && f.IsStatic && f.IsLiteral && rt.NeededMembers.Contains(f.Name))
                .OrderBy(f => f.Name)
                .ToList();
            for (int fi = 0; fi < enumFields.Count; fi++)
            {
                var sep = fi < enumFields.Count - 1 ? "," : "";
                sb.AppendLine($"{memberIndent}{enumFields[fi].Name}{sep}");
            }
        }
        else
        {
            // fields
            foreach (var field in type.Fields.Where(f => f.IsPublic).OrderBy(f => f.Name))
            {
                if (!rt.NeededMembers.Contains(field.Name)) continue;
                if (field.IsSpecialName) continue;

                var fMods = field.IsStatic ? "public static" : "public";
                sb.AppendLine($"{memberIndent}{fMods} {FormatTypeRef(field.FieldType)} {field.Name};");
            }

            // properties
            foreach (var prop in type.Properties.OrderBy(p => p.Name))
            {
                if (!rt.NeededMembers.Contains(prop.Name)) continue;
                var getter = prop.GetMethod;
                var setter = prop.SetMethod;
                if (getter == null && setter == null) continue;
                if (getter != null && !getter.IsPublic && (setter == null || !setter.IsPublic)) continue;

                var pMods = new List<string> { "public" };
                if ((getter?.IsStatic ?? false) || (setter?.IsStatic ?? false))
                    pMods.Add("static");

                var accessors = new List<string>();
                if (getter != null) accessors.Add("get;");
                if (setter != null)
                {
                    if (setter.IsPublic) accessors.Add("set;");
                    else accessors.Add("private set;");
                }

                sb.AppendLine($"{memberIndent}{string.Join(" ", pMods)} {FormatTypeRef(prop.PropertyType)} {prop.Name} {{ {string.Join(" ", accessors)} }}");
            }

            // methods
            foreach (var method in type.Methods.OrderBy(m => m.Name))
            {
                if (!method.IsPublic) continue;
                if (method.IsConstructor) continue;
                if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")) continue;
                if (method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) continue;
                if (!rt.NeededMembers.Contains(method.Name)) continue;
                if (method.Name.Contains("<") || method.Name.Contains(">")) continue;

                WriteMethod(sb, method, memberIndent);
            }
        }

        sb.AppendLine($"{indent}}}");
    }

    static void WriteMethod(StringBuilder sb, MethodDefinition method, string indent)
    {
        var mods = new List<string>();
        if (method.IsPublic) mods.Add("public");

        if (method.IsStatic)
            mods.Add("static");
        else if (method.IsAbstract)
            mods.Add("abstract");
        else if (method.IsVirtual && !method.IsNewSlot)
            mods.Add("override");
        else if (method.IsVirtual && method.IsNewSlot)
            mods.Add("virtual");

        var returnType = FormatTypeRef(method.ReturnType);
        var parameters = FormatParameters(method);
        var genericP = FormatMethodGenericParams(method);

        var signature = $"{string.Join(" ", mods)} {returnType} {method.Name}{genericP}({parameters})";

        if (method.IsAbstract)
        {
            sb.AppendLine($"{indent}{signature};");
        }
        else if (method.ReturnType.FullName == "System.Void")
        {
            sb.AppendLine($"{indent}{signature} {{ }}");
        }
        else
        {
            sb.AppendLine($"{indent}{signature}");
            sb.AppendLine($"{indent}    => throw new System.NotImplementedException(\"Stub\");");
        }
    }

    static string FormatParameters(MethodDefinition method)
    {
        return string.Join(", ", method.Parameters.Select(p =>
        {
            var prefix = "";
            if (p.IsOut) prefix = "out ";
            else if (p.ParameterType is ByReferenceType) prefix = "ref ";

            var type = p.ParameterType;
            if (type is ByReferenceType byRef)
                type = byRef.ElementType;

            return $"{prefix}{FormatTypeRef(type)} {EscapeKeyword(p.Name)}";
        }));
    }

    static string FormatGenericParams(TypeDefinition type)
    {
        if (!type.HasGenericParameters) return "";
        return "<" + string.Join(", ", type.GenericParameters.Select(p => p.Name)) + ">";
    }

    static string FormatGenericConstraints(TypeDefinition type, string indent)
    {
        if (!type.HasGenericParameters) return "";

        var parts = new List<string>();
        foreach (var gp in type.GenericParameters)
        {
            var constraints = new List<string>();

            if (gp.HasReferenceTypeConstraint)
                constraints.Add("class");
            if (gp.HasNotNullableValueTypeConstraint)
                constraints.Add("struct");

            foreach (var c in gp.Constraints)
            {
                var cName = FormatTypeRef(c.ConstraintType);
                if (cName != "ValueType")
                    constraints.Add(cName);
            }

            if (gp.HasDefaultConstructorConstraint && !gp.HasNotNullableValueTypeConstraint)
                constraints.Add("new()");

            if (constraints.Count > 0)
                parts.Add($"where {gp.Name} : {string.Join(", ", constraints)}");
        }

        if (parts.Count == 0) return "";
        return "\n" + indent + "    " + string.Join("\n" + indent + "    ", parts);
    }

    static string FormatMethodGenericParams(MethodDefinition method)
    {
        if (!method.HasGenericParameters) return "";
        return "<" + string.Join(", ", method.GenericParameters.Select(p => p.Name)) + ">";
    }

    static string FormatTypeRef(TypeReference type)
    {
        if (type == null) return "void";

        switch (type.FullName)
        {
            case "System.Void": return "void";
            case "System.Boolean": return "bool";
            case "System.Byte": return "byte";
            case "System.SByte": return "sbyte";
            case "System.Char": return "char";
            case "System.Int16": return "short";
            case "System.UInt16": return "ushort";
            case "System.Int32": return "int";
            case "System.UInt32": return "uint";
            case "System.Int64": return "long";
            case "System.UInt64": return "ulong";
            case "System.Single": return "float";
            case "System.Double": return "double";
            case "System.Decimal": return "decimal";
            case "System.String": return "string";
            case "System.Object": return "object";
        }

        if (type is GenericInstanceType genType)
        {
            if (genType.ElementType.FullName == "System.Nullable`1")
                return FormatTypeRef(genType.GenericArguments[0]) + "?";

            if (genType.ElementType.FullName.StartsWith("System.ValueTuple`"))
            {
                var tupleArgs = string.Join(", ", genType.GenericArguments.Select(FormatTypeRef));
                return $"({tupleArgs})";
            }

            var baseName = StripGenericArity(genType.ElementType.Name);
            var args = string.Join(", ", genType.GenericArguments.Select(FormatTypeRef));
            return $"{baseName}<{args}>";
        }

        if (type is GenericParameter genParam)
            return genParam.Name;

        if (type is ArrayType arrType)
            return $"{FormatTypeRef(arrType.ElementType)}[]";

        if (type is ByReferenceType refType)
            return FormatTypeRef(refType.ElementType);

        if (type is PointerType ptrType)
            return $"{FormatTypeRef(ptrType.ElementType)}*";

        return StripGenericArity(type.Name);
    }

    static HashSet<string> CollectUsings(List<ResolvedType> types)
    {
        var usings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rt in types)
        {
            CheckNamespace(rt.Definition.BaseType, usings);
            foreach (var field in rt.Definition.Fields.Where(f => f.IsPublic))
            {
                if (!rt.NeededMembers.Contains(field.Name)) continue;
                CheckNamespace(field.FieldType, usings);
            }
            foreach (var method in rt.Definition.Methods.Where(m => m.IsPublic))
            {
                if (method.IsConstructor || !rt.NeededMembers.Contains(method.Name)) continue;
                CheckNamespace(method.ReturnType, usings);
                foreach (var p in method.Parameters)
                    CheckNamespace(p.ParameterType, usings);
            }
            foreach (var prop in rt.Definition.Properties)
            {
                if (!rt.NeededMembers.Contains(prop.Name)) continue;
                CheckNamespace(prop.PropertyType, usings);
            }
        }

        // remove namespaces that are defined in our stubs
        var stubNamespaces = types.Select(t => t.Definition.Namespace).Where(n => !string.IsNullOrEmpty(n)).ToHashSet();
        foreach (var ns in stubNamespaces)
            usings.Remove(ns);

        // remove System (always available)
        usings.Remove("System");
        usings.Remove("");

        return usings;
    }

    static void CheckNamespace(TypeReference type, HashSet<string> usings)
    {
        if (type == null) return;

        if (type is GenericInstanceType gen)
        {
            CheckNamespace(gen.ElementType, usings);
            foreach (var arg in gen.GenericArguments)
                CheckNamespace(arg, usings);
            return;
        }

        if (type is ArrayType arr)
        {
            CheckNamespace(arr.ElementType, usings);
            return;
        }

        if (type is ByReferenceType byRef)
        {
            CheckNamespace(byRef.ElementType, usings);
            return;
        }

        var ns = type.Namespace;
        if (!string.IsNullOrEmpty(ns) && ns != "System")
            usings.Add(ns);
    }

    static void WriteCsproj(string assemblyName, ResolverResult result, string dir, string unityVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net472</TargetFramework>");
        sb.AppendLine($"    <AssemblyName>{assemblyName}</AssemblyName>");
        sb.AppendLine("    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>");
        sb.AppendLine("    <GenerateTargetFrameworkAttribute>false</GenerateTargetFrameworkAttribute>");
        sb.AppendLine("    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
        sb.AppendLine("    <EnableDefaultCompileItems>true</EnableDefaultCompileItems>");
        sb.AppendLine("    <NoWarn>CS0649;CS0169;CS0414;CS0626</NoWarn>");
        sb.AppendLine("  </PropertyGroup>");

        // check if any type references UnityEngine
        bool needsUnity = result.TypesByAssembly[assemblyName]
            .Any(t => ReferencesUnity(t));

        if (needsUnity)
        {
            sb.AppendLine("  <ItemGroup>");
            sb.AppendLine($"    <PackageReference Include=\"UnityEngine.Modules\" Version=\"{unityVersion}\" />");
            sb.AppendLine("  </ItemGroup>");
        }

        // cross-assembly dependencies
        if (result.AssemblyDependencies.TryGetValue(assemblyName, out var deps) && deps.Count > 0)
        {
            sb.AppendLine("  <ItemGroup>");
            foreach (var dep in deps.OrderBy(x => x))
            {
                if (result.TypesByAssembly.ContainsKey(dep))
                    sb.AppendLine($"    <ProjectReference Include=\"..\\{dep}\\{dep}.csproj\" />");
            }
            sb.AppendLine("  </ItemGroup>");
        }

        sb.AppendLine("</Project>");

        File.WriteAllText(Path.Combine(dir, $"{assemblyName}.csproj"), sb.ToString());
    }

    static bool ReferencesUnity(ResolvedType rt)
    {
        var type = rt.Definition;
        if (type.BaseType?.Namespace?.StartsWith("UnityEngine") == true) return true;
        if (type.Fields.Any(f => f.IsPublic && rt.NeededMembers.Contains(f.Name) && f.FieldType.Namespace?.StartsWith("UnityEngine") == true)) return true;
        if (type.Methods.Any(m => m.IsPublic && rt.NeededMembers.Contains(m.Name) && m.ReturnType.Namespace?.StartsWith("UnityEngine") == true)) return true;
        if (type.Properties.Any(p => rt.NeededMembers.Contains(p.Name) && p.PropertyType.Namespace?.StartsWith("UnityEngine") == true)) return true;
        return false;
    }

    static int GetInheritanceDepth(TypeDefinition type)
    {
        int depth = 0;
        var current = type.BaseType;
        while (current != null)
        {
            depth++;
            try { current = current.Resolve()?.BaseType; }
            catch { break; }
        }
        return depth;
    }

    static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick >= 0 ? name.Substring(0, tick) : name;
    }

    static string EscapeKeyword(string name)
    {
        var keywords = new HashSet<string> {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
            "char", "checked", "class", "const", "continue", "decimal", "default",
            "delegate", "do", "double", "else", "enum", "event", "explicit",
            "extern", "false", "finally", "fixed", "float", "for", "foreach",
            "goto", "if", "implicit", "in", "int", "interface", "internal",
            "is", "lock", "long", "namespace", "new", "null", "object",
            "operator", "out", "override", "params", "private", "protected",
            "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch",
            "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unsafe", "ushort", "using", "virtual", "void",
            "volatile", "while"
        };
        return keywords.Contains(name) ? $"@{name}" : name;
    }
}

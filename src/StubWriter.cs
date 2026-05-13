using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

namespace CsStubGen;

public static class StubWriter
{
    public static void Write(ResolverResult result, string outDir)
    {
        Directory.CreateDirectory(outDir);

        foreach (var (assemblyName, types) in result.TypesByAssembly)
        {
            var asmDir = Path.Combine(outDir, assemblyName);
            Directory.CreateDirectory(asmDir);

            WriteStubCs(types, Path.Combine(asmDir, "Stubs.cs"));
            WriteCsproj(assemblyName, result, asmDir);
        }

        WriteStubsProps(result, outDir);
    }

    static void WriteStubsProps(ResolverResult result, string outDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project>");
        sb.AppendLine("  <!-- Game-specific stubs (minimal signatures, no proprietary code) -->");
        sb.AppendLine("  <ItemGroup>");
        foreach (var assemblyName in result.TypesByAssembly.Keys.OrderBy(x => x))
        {
            sb.AppendLine($"    <ProjectReference Include=\"$(MSBuildThisFileDirectory){assemblyName}\\{assemblyName}.csproj\">");
            sb.AppendLine("      <Private>false</Private>");
            sb.AppendLine("    </ProjectReference>");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");

        File.WriteAllText(Path.Combine(outDir, "Stubs.props"), sb.ToString());
    }

    static void WriteStubCs(List<ResolvedType> types, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// =============================================================================");
        sb.AppendLine("// AUTO-GENERATED STUB by csstubgen — DO NOT EDIT");
        sb.AppendLine("// =============================================================================");
        sb.AppendLine();

        var usings = CollectUsings(types);
        sb.AppendLine("using System;");
        foreach (var ns in usings.OrderBy(x => x))
            sb.AppendLine($"using {ns};");
        sb.AppendLine();

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

        // delegates cannot be expressed as classes — emit delegate keyword form
        var baseFullName = type.BaseType?.FullName;
        if (baseFullName == "System.MulticastDelegate" || baseFullName == "System.Delegate")
        {
            var invoke = type.Methods.FirstOrDefault(m => m.Name == "Invoke");
            var ret = invoke != null ? FormatTypeRef(invoke.ReturnType) : "void";
            var prms = invoke != null ? FormatParameters(invoke) : "";
            var acc = (type.IsPublic || type.IsNestedPublic) ? "public" : "internal";
            var gp = FormatGenericParams(type);
            sb.AppendLine($"{indent}{acc} delegate {ret} {StripGenericArity(type.Name)}{gp}({prms});");
            return;
        }

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
            baseParts.Add(FormatTypeRef(iface.InterfaceType));
        }

        var baseClause = baseParts.Count > 0 ? " : " + string.Join(", ", baseParts) : "";

        sb.AppendLine($"{indent}{string.Join(" ", mods)} {keyword} {StripGenericArity(type.Name)}{genericParams}{baseClause}{constraints}");
        sb.AppendLine($"{indent}{{");

        var memberIndent = indent + "    ";

        if (type.IsEnum)
        {
            // enum members use declaration syntax, not field syntax
            var enumFields = type.Fields
                .Where(f => f.IsPublic && f.IsStatic && f.IsLiteral)
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
            foreach (var field in type.Fields.Where(f => f.IsPublic || f.IsFamily).OrderBy(f => f.Name))
            {
                if (field.IsSpecialName) continue;

                var fMods = field.IsStatic ? "public static" : (field.IsFamily ? "protected" : "public");
                sb.AppendLine($"{memberIndent}{fMods} {FormatTypeRef(field.FieldType)} {field.Name};");
            }

            // properties
            foreach (var prop in type.Properties.OrderBy(p => p.Name))
            {
                var getter = prop.GetMethod;
                var setter = prop.SetMethod;
                if (getter == null && setter == null) continue;
                if (getter != null && !getter.IsPublic && !getter.IsFamily && (setter == null || (!setter.IsPublic && !setter.IsFamily))) continue;

                var pMods = new List<string>();
                if (getter?.IsPublic == true || setter?.IsPublic == true) pMods.Add("public");
                else pMods.Add("protected");
                if ((getter?.IsStatic ?? false) || (setter?.IsStatic ?? false))
                    pMods.Add("static");

                var propType = FormatTypeRef(prop.PropertyType);
                if (getter != null && setter == null)
                {
                    // expression-bodied avoids auto-property backing field (prevents struct self-ref cycle)
                    var staticKw = (getter.IsStatic ? " static" : "");
                    sb.AppendLine($"{memberIndent}public{staticKw} {propType} {prop.Name}");
                    sb.AppendLine($"{memberIndent}    => throw new System.NotImplementedException(\"Stub\");");
                }
                else
                {
                    var accessors = new List<string>();
                    if (getter != null) accessors.Add("get;");
                    if (setter != null)
                    {
                        if (setter.IsPublic) accessors.Add("set;");
                        else accessors.Add("private set;");
                    }
                    sb.AppendLine($"{memberIndent}{string.Join(" ", pMods)} {propType} {prop.Name} {{ {string.Join(" ", accessors)} }}");
                }
            }

            // events
            foreach (var evt in type.Events.OrderBy(e => e.Name))
            {
                var addMethod = evt.AddMethod;
                if (addMethod == null || (!addMethod.IsPublic && !addMethod.IsFamily)) continue;
                var eMods = addMethod.IsPublic ? "public" : "protected";
                if (addMethod.IsStatic) eMods += " static";
                sb.AppendLine($"{memberIndent}{eMods} event {FormatTypeRef(evt.EventType)} {evt.Name};");
            }

            // constructors
            foreach (var ctor in type.Methods
                .Where(m => m.IsConstructor && (m.IsPublic || m.IsFamily) && !m.IsStatic)
                .OrderBy(m => m.Parameters.Count))
            {
                var cMods = ctor.IsPublic ? "public" : "protected";
                var parameters = FormatParameters(ctor);
                sb.AppendLine($"{memberIndent}{cMods} {StripGenericArity(type.Name)}({parameters}) {{ }}");
            }

            // methods (only used ones)
            foreach (var method in type.Methods.OrderBy(m => m.Name))
            {
                if (!method.IsPublic && !method.IsFamily) continue;
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
        else if (method.IsFamily) mods.Add("protected");

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
            var ownerNs = rt.Definition.Namespace ?? "";
            CheckNamespace(rt.Definition.BaseType, usings, ownerNs);
            foreach (var field in rt.Definition.Fields.Where(f => f.IsPublic || f.IsFamily))
            {
                CheckNamespace(field.FieldType, usings, ownerNs);
            }
            foreach (var method in rt.Definition.Methods.Where(m => m.IsPublic || m.IsFamily))
            {
                if (!method.IsConstructor && !rt.NeededMembers.Contains(method.Name)) continue;
                CheckNamespace(method.ReturnType, usings, ownerNs);
                foreach (var p in method.Parameters)
                    CheckNamespace(p.ParameterType, usings, ownerNs);
            }
            foreach (var prop in rt.Definition.Properties)
            {
                CheckNamespace(prop.PropertyType, usings, ownerNs);
            }
            foreach (var evt in rt.Definition.Events)
            {
                CheckNamespace(evt.EventType, usings, ownerNs);
            }
        }

        // remove System (always available)
        usings.Remove("System");
        usings.Remove("");

        return usings;
    }

    static void CheckNamespace(TypeReference type, HashSet<string> usings, string ownerNs = "")
    {
        if (type == null) return;

        if (type is GenericInstanceType gen)
        {
            CheckNamespace(gen.ElementType, usings, ownerNs);
            foreach (var arg in gen.GenericArguments)
                CheckNamespace(arg, usings, ownerNs);
            return;
        }

        if (type is ArrayType arr)
        {
            CheckNamespace(arr.ElementType, usings, ownerNs);
            return;
        }

        if (type is ByReferenceType byRef)
        {
            CheckNamespace(byRef.ElementType, usings, ownerNs);
            return;
        }

        var ns = type.Namespace;
        if (!string.IsNullOrEmpty(ns) && ns != "System" && ns != ownerNs)
            usings.Add(ns);
    }

    static void WriteCsproj(string assemblyName, ResolverResult result, string dir)
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

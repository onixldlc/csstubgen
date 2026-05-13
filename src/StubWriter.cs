using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CsStubGen;

public static class StubWriter
{
    public static void Write(
        SortedDictionary<string, (string typeName, string asm, List<string> stubs)> classGroups,
        string outDir)
    {
        Directory.CreateDirectory(outDir);

        var asmGroups = classGroups
            .GroupBy(kv => kv.Value.asm)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var asmGroup in asmGroups)
        {
            var asmName = asmGroup.Key;
            var asmDir = Path.Combine(outDir, asmName);
            Directory.CreateDirectory(asmDir);

            WriteStubCs(asmName, asmGroup, Path.Combine(asmDir, "Stubs.cs"));
            WriteCsproj(asmName, asmDir);
        }

        WriteStubsProps(asmGroups.Select(g => g.Key), outDir);
    }

    static void WriteStubCs(
        string asmName,
        IEnumerable<KeyValuePair<string, (string typeName, string asm, List<string> stubs)>> types,
        string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// =============================================================================");
        sb.AppendLine("// AUTO-GENERATED STUB by csstubgen — DO NOT EDIT");
        sb.AppendLine("// =============================================================================");
        sb.AppendLine();

        var usings = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var kv in types)
            foreach (var stub in kv.Value.stubs)
                foreach (var ns in ExtractNamespaces(stub))
                    usings.Add(ns);

        sb.AppendLine("using System;");
        foreach (var u in usings)
            sb.AppendLine($"using {u};");
        sb.AppendLine();

        sb.AppendLine($"namespace {asmName}");
        sb.AppendLine("{");
        foreach (var kv in types)
        {
            var (typeName, _, stubs) = kv.Value;
            var simpleName = typeName.Contains('.') ? typeName.Substring(typeName.LastIndexOf('.') + 1) : typeName;
            var nameComment = typeName != simpleName ? $" // {typeName}" : "";
            sb.AppendLine($"    public class {simpleName}{nameComment}");
            sb.AppendLine("    {");
            foreach (var stub in stubs)
                sb.AppendLine($"        {stub}");
            sb.AppendLine("    }");
            sb.AppendLine();
        }
        sb.AppendLine("}");

        File.WriteAllText(path, sb.ToString());
    }

    static void WriteCsproj(string assemblyName, string dir)
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
        sb.AppendLine("</Project>");

        File.WriteAllText(Path.Combine(dir, $"{assemblyName}.csproj"), sb.ToString());
    }

    static void WriteStubsProps(IEnumerable<string> assemblyNames, string outDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project>");
        sb.AppendLine("  <!-- Game-specific stubs (minimal signatures, no proprietary code) -->");
        sb.AppendLine("  <ItemGroup>");
        foreach (var assemblyName in assemblyNames)
        {
            sb.AppendLine($"    <ProjectReference Include=\"$(MSBuildThisFileDirectory){assemblyName}\\{assemblyName}.csproj\">");
            sb.AppendLine("      <Private>false</Private>");
            sb.AppendLine("    </ProjectReference>");
        }
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");

        File.WriteAllText(Path.Combine(outDir, "Stubs.props"), sb.ToString());
    }

    public static IEnumerable<string> ExtractNamespaces(string stub)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        while (i < stub.Length)
        {
            if (char.IsUpper(stub[i]) && (i == 0 || !char.IsLetterOrDigit(stub[i - 1]) && stub[i - 1] != '.'))
            {
                var start = i;
                while (i < stub.Length && (char.IsLetterOrDigit(stub[i]) || stub[i] == '.'))
                    i++;
                var name = stub.Substring(start, i - start);
                var lastDot = name.LastIndexOf('.');
                if (lastDot > 0)
                {
                    var ns = name.Substring(0, lastDot);
                    if (seen.Add(ns)) yield return ns;
                }
                continue;
            }
            i++;
        }
    }
}

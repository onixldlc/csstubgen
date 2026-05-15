using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CsStubGen;

class FilterMethods
{
    public static Dictionary<string, Dictionary<string, List<object>>> Execute(List<string> sourceFiles, List<string> refDlls, List<string> libDlls, string outDir, bool debug)
    {
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
        var method_grouped = bucket_source_grouped.ToDictionary(
            bucket => bucket.Key,
            bucket => bucket.Value.ToDictionary(
                source => source.Key,
                source => source.Value.Select(m => (object)new {
                    Method = m.Method.Contains('.') ? m.Method.Split('.').Last() : m.Method,
                    m.Type,
                    m.Details
                }).ToList()
            )
        );
        var method_grouped_json = System.Text.Json.JsonSerializer.Serialize(method_grouped, serialize_options);
        File.WriteAllText(Path.Combine(outDir, "0004-analysis_grouped_by_source_method.json"), method_grouped_json);
        var methods_dictionary = method_grouped;



        // filter out if its not "external"
        var external_methods = method_grouped.Where(kv => kv.Key == "external").ToDictionary(kv => kv.Key, kv => kv.Value);
        var external_methods_json = System.Text.Json.JsonSerializer.Serialize(external_methods, serialize_options);
        File.WriteAllText(Path.Combine(outDir, "0005-analysis_external_methods.json"), external_methods_json);



        // filter out unity core module
        var filtered_unity_core = external_methods.ToDictionary(
            kv => kv.Key, 
            kv => kv.Value.Where(m => m.Key != "UnityEngine.CoreModule").ToDictionary(kv2 => kv2.Key, kv2 => kv2.Value));
        var filtered_unity_core_json = System.Text.Json.JsonSerializer.Serialize(filtered_unity_core, serialize_options);
        File.WriteAllText(Path.Combine(outDir, "0006-analysis_external_methods_filtered_unity_core.json"), filtered_unity_core_json);
        var methods_dictionary = filtered_unity_core;
        return methods_dictionary;
    }
}

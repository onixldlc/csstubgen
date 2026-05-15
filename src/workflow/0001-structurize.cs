using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CsStubGen;

class MethodsParser
{
    public static MethodsDictionary Execute(List<string> sourceFiles, List<string> refDlls, List<string> libDlls, string outDir, bool debug)
    {
        // dump the analysis result to ./analysis.json for debugging
        var analysis_result = SourceAnalyzer.Analyze(sourceFiles, refDlls.Concat(libDlls));
        var methods_dictionary = new MethodsDictionary();
        foreach(var calledMethod in analysis_result.CalledMethods){
            var group = calledMethod.Group;
            var method = calledMethod.Method;
            var type = calledMethod.Type;
            var source = calledMethod.Source;
            var details = calledMethod.Details;

            if(!methods_dictionary.bucket.ContainsKey(group)){
                methods_dictionary.bucket[group] = new Bucket();
            }
            var currentBucket = methods_dictionary.bucket[group];

            if(!currentBucket.module.ContainsKey(source)){
                currentBucket.module[source] = new Module();
            }
            var currentModule = currentBucket.module[source];

            if(!currentModule.type.ContainsKey(type)){
                currentModule.type[type] = new MethodType();
            }

            var method_entry = new Method{
                method = method,
                details = details
            };
            currentModule.type[type].method.Add(method_entry);
        }

        // dumps methods_dictionary to json for debugging
        if(debug){
            File.WriteAllText(Path.Combine(outDir, "analysis_formarted.json"), methods_dictionary.Json());
        }
        return methods_dictionary;
    }
}


class MethodsDictionary
{
    public Dictionary<string, Bucket> bucket { get; set; } = new();
    public Bucket this[string key] => bucket[key];
    public Bucket Get(string bucketName){
        if(bucket.ContainsKey(bucketName)){
            return bucket[bucketName];
        }
        return null;
    }
    public MethodsDictionary Filter(Func<string, Bucket, bool> predicate){
        var filtered = new MethodsDictionary();
        foreach(var kv in bucket){
            if(predicate(kv.Key, kv.Value)){
                filtered.bucket[kv.Key] = kv.Value;
            }
        }
        return filtered;
    }
    public MethodsDictionary Map(Func<string, Bucket, Bucket> transform){
        var result = new MethodsDictionary();
        foreach(var kv in bucket) result.bucket[kv.Key] = transform(kv.Key, kv.Value);
        return result;
    }
    public string Json() => MethodsDictionaryJson.Serialize(this);
}
class Bucket
{
    public Dictionary<string, Module> module { get; set; } = new();
    public Module Get(string moduleName){
        if(module.ContainsKey(moduleName)){
            return module[moduleName];
        }
        return null;
    }
    public Bucket Filter(Func<string, Module, bool> predicate){
        var filtered = new Bucket();
        foreach(var kv in module){
            if(predicate(kv.Key, kv.Value)){
                filtered.module[kv.Key] = kv.Value;
            }
        }
        return filtered;
    }
    public Bucket Map(Func<string, Module, Module> transform){
        var result = new Bucket();
        foreach(var kv in module) result.module[kv.Key] = transform(kv.Key, kv.Value);
        return result;
    }
    public string Json() => MethodsDictionaryJson.Serialize(this);
}
class Module
{
    public Dictionary<string, MethodType> type { get; set; } = new();
    public MethodType Get(string typeName){
        if(type.ContainsKey(typeName)){
            return type[typeName];
        }
        return null;
    }
    public Module Filter(Func<string, MethodType, bool> predicate){
        var filtered = new Module();
        foreach(var kv in type){
            if(predicate(kv.Key, kv.Value)){
                filtered.type[kv.Key] = kv.Value;
            }
        }
        return filtered;
    }
    public Module Map(Func<string, MethodType, MethodType> transform){
        var result = new Module();
        foreach(var kv in type) result.type[kv.Key] = transform(kv.Key, kv.Value);
        return result;
    }
    public string Json() => MethodsDictionaryJson.Serialize(this);
}
class MethodType
{
    public List<Method> method { get; set; } = new();
    public Method Get(string methodName){
        return method.FirstOrDefault(m => m.method == methodName);
    }
    public MethodType Filter(Func<Method, bool> predicate){
        var filtered = new MethodType();
        filtered.method = method.Where(predicate).ToList();
        return filtered;
    }
    public string Json() => MethodsDictionaryJson.Serialize(this);
}
class Method
{
    public string method { get; set; }
    public string details { get; set; }
}

static class MethodsDictionaryJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = {
            new MethodsDictionaryConverter(),
            new BucketConverter(),
            new ModuleConverter(),
            new MethodTypeConverter()
        }
    };
    public static string Serialize(object value) => System.Text.Json.JsonSerializer.Serialize(value, Options);
}

// --- transparent serialization: each wrapper class serializes as its inner dict ---

abstract class DictWrapperConverter<TWrapper, TInner> : System.Text.Json.Serialization.JsonConverter<TWrapper>
{
    protected abstract Dictionary<string, TInner> GetDict(TWrapper wrapper);
    protected abstract TWrapper FromDict(Dictionary<string, TInner> dict);
    public override TWrapper Read(ref System.Text.Json.Utf8JsonReader reader, System.Type t, System.Text.Json.JsonSerializerOptions o)
        => FromDict(System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, TInner>>(ref reader, o)!);
    public override void Write(System.Text.Json.Utf8JsonWriter writer, TWrapper value, System.Text.Json.JsonSerializerOptions o)
        => System.Text.Json.JsonSerializer.Serialize(writer, GetDict(value), o);
}

class MethodsDictionaryConverter : DictWrapperConverter<MethodsDictionary, Bucket>
{
    protected override Dictionary<string, Bucket> GetDict(MethodsDictionary w) => w.bucket;
    protected override MethodsDictionary FromDict(Dictionary<string, Bucket> d) => new() { bucket = d };
}
class BucketConverter : DictWrapperConverter<Bucket, Module>
{
    protected override Dictionary<string, Module> GetDict(Bucket w) => w.module;
    protected override Bucket FromDict(Dictionary<string, Module> d) => new() { module = d };
}
class ModuleConverter : DictWrapperConverter<Module, MethodType>
{
    protected override Dictionary<string, MethodType> GetDict(Module w) => w.type;
    protected override Module FromDict(Dictionary<string, MethodType> d) => new() { type = d };
}
class MethodTypeConverter : DictWrapperConverter<MethodType, List<Method>>
{
    protected override Dictionary<string, List<Method>> GetDict(MethodType w) => throw new NotImplementedException();
    protected override MethodType FromDict(Dictionary<string, List<Method>> d) => throw new NotImplementedException();
    public override void Write(System.Text.Json.Utf8JsonWriter writer, MethodType value, System.Text.Json.JsonSerializerOptions o)
        => System.Text.Json.JsonSerializer.Serialize(writer, value.method, o);
    public override MethodType Read(ref System.Text.Json.Utf8JsonReader reader, System.Type t, System.Text.Json.JsonSerializerOptions o)
        => new() { method = System.Text.Json.JsonSerializer.Deserialize<List<Method>>(ref reader, o)! };
}
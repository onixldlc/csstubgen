using System;
using System.Collections.Generic;
using System.Linq;

namespace CsStubGen;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var sourcePaths = new List<string>();
        var refPaths = new List<string>();
        var libPaths = new List<string>();
        string outDir = "./stubs";
        bool verbose = false;
        bool debug = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-s" or "--source":
                    if (++i < args.Length) sourcePaths.Add(args[i]);
                    break;
                case "-r" or "--ref":
                    if (++i < args.Length) refPaths.Add(args[i]);
                    break;
                case "-l" or "--lib":
                    if (++i < args.Length) libPaths.Add(args[i]);
                    break;
                case "-o" or "--out":
                    if (++i < args.Length) outDir = args[i];
                    break;
                case "--unity-version":
                    if (++i < args.Length) { /* ignored — unity types are stubbed directly */ }
                    break;
                case "-v" or "--verbose":
                    verbose = true;
                    break;
                case "--debug":
                    debug = true;
                    verbose = true;
                    break;
            }
        }

        return Logic.Run(sourcePaths, refPaths, libPaths, outDir, verbose, debug);
    }
    static void PrintUsage()
    {
        Console.WriteLine("csstubgen - Generate minimal C# stubs from source + reference DLLs");
        Console.WriteLine();
        Console.WriteLine("Usage: csstubgen -s <source> -r <reference> [-l <library>] [-o <output>]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -s, --source <path>       Source .cs files or directory (required, repeatable)");
        Console.WriteLine("  -r, --ref <path>          Reference DLL or directory to generate stubs for (required, repeatable)");
        Console.WriteLine("  -l, --lib <path>          Library DLL or directory for analysis only, no stubs (repeatable)");
        Console.WriteLine("  -o, --out <path>          Output directory (default: ./stubs)");
        Console.WriteLine("  -v, --verbose             Show detailed analysis output");
        Console.WriteLine("  -h, --help                Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  csstubgen -s ./MyMod.cs -r ./Assembly-CSharp.dll -l ./Managed/ -o ./stubs/");
        Console.WriteLine("  csstubgen -s ./src/ -r ./Assembly-CSharp.dll -r ./Mirage.dll -l ./BepInEx.dll -o ./stubs/");
    }
}

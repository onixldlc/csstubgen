using ICSharpCode.Decompiler;

namespace CsStubGen;

// central place for every DecompilerSettings flag in ICSharpCode.Decompiler v9.1.
// each flag has its own variable + comment so you can toggle and see effect.
// call DecompilerOptions.Build() to get a configured DecompilerSettings.
//
// goal: find an option (or combination) that reduces the verbosity of 0013-stubs
// (StubBuilder output) and stops ILSpy from emitting members you don't want.
//
// summary of relevant knobs for "filter what gets decompiled":
//   - DecompileMemberBodies  : false = keep signatures only (no method bodies)
//   - ShowXmlDocumentation   : false = drop /// xml docs
//   - ExpandMemberDefinitions: false = compact one-liner members
//   - ExpandUsingDeclarations: false = compact using directives
//   - FoldBraces             : false = no fold markers
//   - UsingDeclarations      : false = drop usings entirely (breaks compileable output!)
//   - the C#-version transform flags (RecordClasses, NullableReferenceTypes, etc.)
//     can be set false to force older / simpler output
//
// NOTE: there is NO member-level filter setting. To skip selected members you must
// either pass member EntityHandles to DecompileAsString, or post-process the
// SyntaxTree from dec.Decompile(...).
class DecompilerOptions
{
    // ---------- output verbosity (most useful for stub generation) ----------

    // emit only member signatures, no method/property bodies.
    // false = signatures only -> what we want for stubs.
    public static bool DecompileMemberBodies = false;

    // include /// xml doc comments above members.
    // false = no docs -> cleaner stubs.
    public static bool ShowXmlDocumentation = false;

    // expand each member body block with explicit braces and indentation.
    // false (default) = compact member layout.
    public static bool ExpandMemberDefinitions = false;

    // expand using directives into a wider block format.
    // false (default) = single line per using.
    public static bool ExpandUsingDeclarations = false;

    // emit fold markers (#region-like) around brace blocks.
    // false (default) = no fold markers.
    public static bool FoldBraces = false;

    // emit C# `using` directives at all.
    // false = strip every using -> NOT compileable, but minimal output.
    public static bool UsingDeclarations = true;

    // always wrap single-statement blocks in braces.
    // true = consistent `{ ... }` style.
    public static bool AlwaysUseBraces = true;


    // ---------- assembly resolution / loading ----------

    // throw when a referenced assembly cannot be resolved.
    // false = swallow resolve errors and keep going (better for incomplete ref sets).
    public static bool ThrowOnAssemblyResolveErrors = false;

    // load the whole PE file into memory before decompiling.
    // false (default) = stream from disk.
    public static bool LoadInMemory = false;

    // also resolve assemblies that the input depends on.
    // true (default) = follow references automatically.
    public static bool AutoLoadAssemblyReferences = true;

    // apply WinRT name projections (e.g. IIterable<T> -> IEnumerable<T>).
    // true (default) = standard projections.
    public static bool ApplyWindowsRuntimeProjections = true;

    // include PDB-based debug info in the output.
    // false (default) = no debug info.
    public static bool ShowDebugInfo = false;

    // use PDB symbol names for locals/parameters when available.
    // true (default) = nicer names.
    public static bool UseDebugSymbols = true;


    // ---------- formatting / style ----------

    // emit type members already cast at explicit-interface-impl call sites.
    // false (default) = let inference do it.
    public static bool AlwaysCastTargetsOfExplicitInterfaceImplementationCalls = false;

    // always fully qualify member references (e.g. `this.Foo`).
    // false (default) = bare names where unambiguous.
    public static bool AlwaysQualifyMemberReferences = false;

    // always print explicit value for enum members.
    // false (default) = only print when needed.
    public static bool AlwaysShowEnumMemberValues = false;

    // declare each local variable on its own line.
    // false (default) = combined where possible.
    public static bool SeparateLocalVariableDeclarations = false;

    // sort custom attributes by name for stable output.
    // false (default) = source order.
    public static bool SortCustomAttributes = false;

    // prefix every global-scope reference with `global::`.
    // false (default) = only when ambiguous.
    public static bool AlwaysUseGlobal = false;

    // emit SDK-style .csproj when producing a project.
    // true (default) = modern format.
    public static bool UseSdkStyleProjectFormat = true;

    // mirror the namespace hierarchy into output folders.
    // false (default) = flat folder.
    public static bool UseNestedDirectoriesForNamespaces = false;


    // ---------- C# language feature transforms ----------
    // each one, when true, makes the output use that C# feature.
    // setting false forces an older / simpler equivalent (more verbose IL-like form).

    public static bool NativeIntegers = true;                       // `nint` / `nuint`
    public static bool NumericIntPtr = true;                        // C# 11 IntPtr-as-nint
    public static bool CovariantReturns = true;                     // C# 9
    public static bool InitAccessors = true;                        // `init` setters
    public static bool RecordClasses = true;                        // `record` classes
    public static bool RecordStructs = true;                        // `record struct`
    public static bool WithExpressions = true;                      // `x with { ... }`
    public static bool UsePrimaryConstructorSyntax = true;          // record primary ctor
    public static bool UsePrimaryConstructorSyntaxForNonRecordTypes = true; // C# 12 ctor
    public static bool FunctionPointers = true;                     // `delegate*<...>`
    public static bool ScopedRef = true;                            // `scoped ref`
    public static bool LifetimeAnnotations = true;                  // `[UnscopedRef]`
    public static bool RequiredMembers = true;                      // `required` keyword
    public static bool SwitchExpressions = true;                    // `x switch { ... }`
    public static bool FileScopedNamespaces = true;                 // `namespace X;`
    public static bool AnonymousMethods = true;                     // `delegate(...){}`
    public static bool AnonymousTypes = true;                       // `new { X = 1 }`
    public static bool UseLambdaSyntax = true;                      // `x => ...` over delegate
    public static bool ExpressionTrees = true;                      // `Expression<...>`
    public static bool YieldReturn = true;                          // iterators
    public static bool Dynamic = true;                              // `dynamic`
    public static bool AsyncAwait = true;                           // `async`/`await`
    public static bool AwaitInCatchFinally = true;                  // C# 6
    public static bool AsyncEnumerator = true;                      // `IAsyncEnumerable`
    public static bool DecimalConstants = true;                     // `1.0m` literals
    public static bool FixedBuffers = true;                         // `fixed` arrays
    public static bool StringConcat = true;                         // collapse + chains
    public static bool LiftNullables = true;                        // T? lifting
    public static bool NullPropagation = true;                      // `?.`
    public static bool AutomaticProperties = true;                  // `{ get; set; }`
    public static bool GetterOnlyAutomaticProperties = true;        // C# 6 read-only autoprop
    public static bool AutomaticEvents = true;                      // field-like events
    public static bool UsingStatement = true;                       // `using (x) { }`
    public static bool UseEnhancedUsing = true;                     // C# 8 `using var`
    public static bool ForEachStatement = true;                     // `foreach`
    public static bool ForEachWithGetEnumeratorExtension = true;    // ext-method foreach
    public static bool ForStatement = true;                         // `for (...)`
    public static bool DoWhileStatement = true;                     // `do { } while`
    public static bool LockStatement = true;                        // `lock (...)`
    public static bool SwitchStatementOnString = true;              // string switch
    public static bool SparseIntegerSwitch = true;                  // sparse jump tables
    public static bool ExtensionMethods = true;                     // `this T` params
    public static bool QueryExpressions = true;                     // LINQ query syntax
    public static bool UseImplicitMethodGroupConversion = true;     // group conversion
    public static bool ArrayInitializers = true;                    // `new int[] { ... }`
    public static bool ObjectOrCollectionInitializers = true;       // `new X { ... }`
    public static bool DictionaryInitializers = true;               // `new D { [k] = v }`
    public static bool ExtensionMethodsInCollectionInitializers = true;
    public static bool UseRefLocalsForAccurateOrderOfEvaluation = true;
    public static bool RefExtensionMethods = true;                  // `this ref T`
    public static bool StringInterpolation = true;                  // `$"..."`
    public static bool Utf8StringLiterals = true;                   // `"..."u8`
    public static bool SwitchOnReadOnlySpanChar = true;             // C# 11
    public static bool UnsignedRightShift = true;                   // `>>>`
    public static bool CheckedOperators = true;                     // `checked operator`
    public static bool OutVariables = true;                         // `out var x`
    public static bool Discards = true;                             // `_` discards
    public static bool IntroduceRefModifiersOnStructs = true;       // `ref struct`
    public static bool IntroduceReadonlyAndInModifiers = true;      // `in` / `readonly`
    public static bool IntroducePrivateProtectedAccessibility = true;
    public static bool ReadOnlyMethods = true;                      // `readonly` member
    public static bool AsyncUsingAndForEachStatement = true;        // `await using`
    public static bool IntroduceUnmanagedConstraint = true;         // `where T : unmanaged`
    public static bool StackAllocInitializers = true;               // `stackalloc[] { }`
    public static bool PatternBasedFixedStatement = true;           // C# 7.3
    public static bool TupleTypes = true;                           // `(int, int)`
    public static bool ThrowExpressions = true;                     // `throw new ...` expr
    public static bool TupleConversions = true;
    public static bool TupleComparisons = true;
    public static bool NamedArguments = true;                       // `foo(name: x)`
    public static bool NonTrailingNamedArguments = true;
    public static bool OptionalArguments = true;                    // default params
    public static bool LocalFunctions = true;                       // nested methods
    public static bool Deconstruction = true;                       // `var (a, b) = x`
    public static bool PatternMatching = true;                      // `is X x`
    public static bool RecursivePatternMatching = true;             // `is { Y: 1 }`
    public static bool PatternCombinators = true;                   // `is X and Y`
    public static bool RelationalPatterns = true;                   // `is > 0`
    public static bool StaticLocalFunctions = true;                 // `static void Local()`
    public static bool Ranges = true;                               // `..`
    public static bool NullableReferenceTypes = true;               // `T?` on refs
    public static bool RefReadOnlyParameters = true;                // C# 12
    public static bool UseExpressionBodyForCalculatedGetterOnlyProperties = true;


    // ---------- IL-level optimization toggles ----------

    public static bool AssumeArrayLengthFitsIntoInt32 = true;       // skip long bounds
    public static bool IntroduceIncrementAndDecrement = true;       // `x++` / `--x`
    public static bool MakeAssignmentExpressions = true;            // `x = y = 1`
    public static bool RemoveDeadCode = true;                      // strip dead branches
    public static bool RemoveDeadStores = false;                    // strip unused stores
    public static bool AggressiveScalarReplacementOfAggregates = false;
    public static bool AggressiveInlining = false;


    // assembles the DecompilerSettings instance used by 0002 / 0003 / stub builder.
    public static DecompilerSettings Build()
    {
        var settings = new DecompilerSettings();

        // output verbosity
        settings.DecompileMemberBodies = DecompileMemberBodies;
        settings.ShowXmlDocumentation = ShowXmlDocumentation;
        settings.ExpandMemberDefinitions = ExpandMemberDefinitions;
        settings.ExpandUsingDeclarations = ExpandUsingDeclarations;
        settings.FoldBraces = FoldBraces;
        settings.UsingDeclarations = UsingDeclarations;
        settings.AlwaysUseBraces = AlwaysUseBraces;

        // assembly resolution
        settings.ThrowOnAssemblyResolveErrors = ThrowOnAssemblyResolveErrors;
        settings.LoadInMemory = LoadInMemory;
        settings.AutoLoadAssemblyReferences = AutoLoadAssemblyReferences;
        settings.ApplyWindowsRuntimeProjections = ApplyWindowsRuntimeProjections;
        settings.ShowDebugInfo = ShowDebugInfo;
        settings.UseDebugSymbols = UseDebugSymbols;

        // formatting / style
        settings.AlwaysCastTargetsOfExplicitInterfaceImplementationCalls = AlwaysCastTargetsOfExplicitInterfaceImplementationCalls;
        settings.AlwaysQualifyMemberReferences = AlwaysQualifyMemberReferences;
        settings.AlwaysShowEnumMemberValues = AlwaysShowEnumMemberValues;
        settings.SeparateLocalVariableDeclarations = SeparateLocalVariableDeclarations;
        settings.SortCustomAttributes = SortCustomAttributes;
        settings.AlwaysUseGlobal = AlwaysUseGlobal;
        settings.UseSdkStyleProjectFormat = UseSdkStyleProjectFormat;
        settings.UseNestedDirectoriesForNamespaces = UseNestedDirectoriesForNamespaces;

        // C# language transforms
        settings.NativeIntegers = NativeIntegers;
        settings.NumericIntPtr = NumericIntPtr;
        settings.CovariantReturns = CovariantReturns;
        settings.InitAccessors = InitAccessors;
        settings.RecordClasses = RecordClasses;
        settings.RecordStructs = RecordStructs;
        settings.WithExpressions = WithExpressions;
        settings.UsePrimaryConstructorSyntax = UsePrimaryConstructorSyntax;
        settings.UsePrimaryConstructorSyntaxForNonRecordTypes = UsePrimaryConstructorSyntaxForNonRecordTypes;
        settings.FunctionPointers = FunctionPointers;
        settings.ScopedRef = ScopedRef;
        settings.LifetimeAnnotations = LifetimeAnnotations;
        settings.RequiredMembers = RequiredMembers;
        settings.SwitchExpressions = SwitchExpressions;
        settings.FileScopedNamespaces = FileScopedNamespaces;
        settings.AnonymousMethods = AnonymousMethods;
        settings.AnonymousTypes = AnonymousTypes;
        settings.UseLambdaSyntax = UseLambdaSyntax;
        settings.ExpressionTrees = ExpressionTrees;
        settings.YieldReturn = YieldReturn;
        settings.Dynamic = Dynamic;
        settings.AsyncAwait = AsyncAwait;
        settings.AwaitInCatchFinally = AwaitInCatchFinally;
        settings.AsyncEnumerator = AsyncEnumerator;
        settings.DecimalConstants = DecimalConstants;
        settings.FixedBuffers = FixedBuffers;
        settings.StringConcat = StringConcat;
        settings.LiftNullables = LiftNullables;
        settings.NullPropagation = NullPropagation;
        settings.AutomaticProperties = AutomaticProperties;
        settings.GetterOnlyAutomaticProperties = GetterOnlyAutomaticProperties;
        settings.AutomaticEvents = AutomaticEvents;
        settings.UsingStatement = UsingStatement;
        settings.UseEnhancedUsing = UseEnhancedUsing;
        settings.ForEachStatement = ForEachStatement;
        settings.ForEachWithGetEnumeratorExtension = ForEachWithGetEnumeratorExtension;
        settings.ForStatement = ForStatement;
        settings.DoWhileStatement = DoWhileStatement;
        settings.LockStatement = LockStatement;
        settings.SwitchStatementOnString = SwitchStatementOnString;
        settings.SparseIntegerSwitch = SparseIntegerSwitch;
        settings.ExtensionMethods = ExtensionMethods;
        settings.QueryExpressions = QueryExpressions;
        settings.UseImplicitMethodGroupConversion = UseImplicitMethodGroupConversion;
        settings.ArrayInitializers = ArrayInitializers;
        settings.ObjectOrCollectionInitializers = ObjectOrCollectionInitializers;
        settings.DictionaryInitializers = DictionaryInitializers;
        settings.ExtensionMethodsInCollectionInitializers = ExtensionMethodsInCollectionInitializers;
        settings.UseRefLocalsForAccurateOrderOfEvaluation = UseRefLocalsForAccurateOrderOfEvaluation;
        settings.RefExtensionMethods = RefExtensionMethods;
        settings.StringInterpolation = StringInterpolation;
        settings.Utf8StringLiterals = Utf8StringLiterals;
        settings.SwitchOnReadOnlySpanChar = SwitchOnReadOnlySpanChar;
        settings.UnsignedRightShift = UnsignedRightShift;
        settings.CheckedOperators = CheckedOperators;
        settings.OutVariables = OutVariables;
        settings.Discards = Discards;
        settings.IntroduceRefModifiersOnStructs = IntroduceRefModifiersOnStructs;
        settings.IntroduceReadonlyAndInModifiers = IntroduceReadonlyAndInModifiers;
        settings.IntroducePrivateProtectedAccessibility = IntroducePrivateProtectedAccessibility;
        settings.ReadOnlyMethods = ReadOnlyMethods;
        settings.AsyncUsingAndForEachStatement = AsyncUsingAndForEachStatement;
        settings.IntroduceUnmanagedConstraint = IntroduceUnmanagedConstraint;
        settings.StackAllocInitializers = StackAllocInitializers;
        settings.PatternBasedFixedStatement = PatternBasedFixedStatement;
        settings.TupleTypes = TupleTypes;
        settings.ThrowExpressions = ThrowExpressions;
        settings.TupleConversions = TupleConversions;
        settings.TupleComparisons = TupleComparisons;
        settings.NamedArguments = NamedArguments;
        settings.NonTrailingNamedArguments = NonTrailingNamedArguments;
        settings.OptionalArguments = OptionalArguments;
        settings.LocalFunctions = LocalFunctions;
        settings.Deconstruction = Deconstruction;
        settings.PatternMatching = PatternMatching;
        settings.RecursivePatternMatching = RecursivePatternMatching;
        settings.PatternCombinators = PatternCombinators;
        settings.RelationalPatterns = RelationalPatterns;
        settings.StaticLocalFunctions = StaticLocalFunctions;
        settings.Ranges = Ranges;
        settings.NullableReferenceTypes = NullableReferenceTypes;
        settings.RefReadOnlyParameters = RefReadOnlyParameters;
        settings.UseExpressionBodyForCalculatedGetterOnlyProperties = UseExpressionBodyForCalculatedGetterOnlyProperties;

        // IL optimization
        settings.AssumeArrayLengthFitsIntoInt32 = AssumeArrayLengthFitsIntoInt32;
        settings.IntroduceIncrementAndDecrement = IntroduceIncrementAndDecrement;
        settings.MakeAssignmentExpressions = MakeAssignmentExpressions;
        settings.RemoveDeadCode = RemoveDeadCode;
        settings.RemoveDeadStores = RemoveDeadStores;
        settings.AggressiveScalarReplacementOfAggregates = AggressiveScalarReplacementOfAggregates;
        settings.AggressiveInlining = AggressiveInlining;

        return settings;
    }
}

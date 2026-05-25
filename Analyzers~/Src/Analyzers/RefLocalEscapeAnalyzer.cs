using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0012 — A 'ref' local bound to a StaticEcs ref-returning member (per FFSECS0010 allow-list)
    /// must continue to be passed with the 'ref' keyword. Passing it as a value argument silently copies
    /// the underlying component at the call boundary, defeating the ref binding.
    ///
    /// Example caught:
    ///   ref var pos = ref entity.Ref&lt;Position&gt;();   // ok — ref to storage
    ///   ApplyGravity(pos);                            // FFSECS0012 — pos copied here; mutations lost
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RefLocalEscapeAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.RefLocalPassedByValue);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) {
                    return;
                }
                if (symbols.RefReturningTargets.IsEmpty) {
                    return;
                }

                start.RegisterOperationBlockAction(ctx => AnalyzeBlock(ctx, symbols));
            });
        }

        private static void AnalyzeBlock(OperationBlockAnalysisContext context, StaticEcsSymbols symbols) {
            // First pass: collect ref-locals whose initializer is one of our allow-listed ref-returning members.
            HashSet<ILocalSymbol> tracked = null;
            foreach (var block in context.OperationBlocks) {
                foreach (var operation in block.DescendantsAndSelf()) {
                    if (operation is not IVariableDeclaratorOperation declarator) continue;
                    if (!declarator.Symbol.IsRef) continue;
                    // Skip atomically-valued ref-locals: passing them by value can't lose state
                    // (no internal structure that could be mutated through a copy). Covers the common
                    // `ref var x = ref ent.Add<C>().Value; M(x);` pattern where .Value is a primitive/enum,
                    // and any `ref T x` where T is a reference type (local holds a pointer, copies of which
                    // still hit the same heap object).
                    if (IsAtomicallyValuedType(declarator.Symbol.Type)) continue;
                    var initializerValue = declarator.Initializer?.Value;
                    if (!IsAllowListedRefReturn(initializerValue, symbols)) continue;
                    tracked ??= new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
                    tracked.Add(declarator.Symbol);
                }
            }
            if (tracked is null) return;

            // Second pass: flag any value-argument whose value resolves to a tracked local.
            foreach (var block in context.OperationBlocks) {
                foreach (var operation in block.DescendantsAndSelf()) {
                    if (operation is not IArgumentOperation argument) continue;
                    if (argument.Parameter is null) continue;
                    if (argument.Parameter.RefKind != RefKind.None) continue;
                    var local = ResolveLocalReference(argument.Value);
                    if (local is null || !tracked.Contains(local)) continue;

                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.RefLocalPassedByValue,
                        argument.Value.Syntax.GetLocation(),
                        local.Name));
                }
            }
        }

        /// <summary>True if the initializer's resolved target is one of the StaticEcs ref-returning OR
        /// ref-readonly-returning members. Walks the receiver chain so that
        /// <c>ref var p = ref entity.Ref&lt;T&gt;().Field</c> is also tracked: the ref binding propagates
        /// through field access into the underlying storage. Non-ref properties in the chain break the
        /// ref binding (the snippet wouldn't compile), and reference-typed outer values aren't subject
        /// to the copy concern — both are suppressed by <see cref="OperationHelpers.TryResolveRefReturningChain"/>.</summary>
        private static bool IsAllowListedRefReturn(IOperation value, StaticEcsSymbols symbols) {
            var match = OperationHelpers.TryResolveRefReturningChain(value, symbols, out _, out _);
            return match == RefChainMatch.Write || match == RefChainMatch.Read;
        }

        /// <summary>
        /// True if <paramref name="type"/> has no internal state that could be lost via a by-value copy:
        /// reference types (local stores a pointer; copies of the pointer reach the same heap object),
        /// CLR primitives (bool/char/numerics/IntPtr/UIntPtr), and enums. For these, FFSECS0012's
        /// "passing breaks the ref binding" concern doesn't apply — there's nothing the called method
        /// could mutate through ref that can't be re-expressed via the caller's later write-through.
        /// Notably <c>Entity</c> is NOT atomic here (it's a multi-field struct).
        /// </summary>
        private static bool IsAtomicallyValuedType(ITypeSymbol type) {
            if (type is null) return false;
            if (type.IsReferenceType) return true;
            if (type.TypeKind == TypeKind.Enum) return true;
            switch (type.SpecialType) {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Char:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_IntPtr:
                case SpecialType.System_UIntPtr:
                    return true;
                default:
                    return false;
            }
        }

        private static ILocalSymbol ResolveLocalReference(IOperation value) {
            value = UnwrapImplicitConversions(value);
            return value is ILocalReferenceOperation localRef ? localRef.Local : null;
        }

        private static IOperation UnwrapImplicitConversions(IOperation value) => OperationHelpers.UnwrapImplicitConversions(value);
    }
}

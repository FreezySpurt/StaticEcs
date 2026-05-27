using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0012 — A 'ref' local bound to a StaticEcs ref-returning member (per FFSECS0010 allow-list)
    /// must continue to be passed with the 'ref' keyword. Passing it as a value argument silently copies
    /// the underlying component at the call boundary, defeating the ref binding.
    ///
    /// Universal across method/lambda/local-function bodies: the analyzer walks the top-level CFG and
    /// every nested CFG via <see cref="OperationHelpers.WalkCfgRecursive"/>, so a ref-local declared
    /// inside a lambda passed to <c>WorldQuery.For(...)</c> is analysed exactly like one in a regular
    /// method body.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RefLocalEscapeAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.RefLocalPassedByValue);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.RefReturningTargets.IsEmpty) return;
                start.RegisterOperationBlockAction(ctx => AnalyzeBlocks(ctx, symbols));
            });
        }

        private static void AnalyzeBlocks(OperationBlockAnalysisContext context, StaticEcsSymbols symbols) {
            var owner = context.OwningSymbol as IMethodSymbol;
            foreach (var block in context.OperationBlocks) {
                OperationHelpers.WalkCfgRecursive(block, owner, (cfg, _) => AnalyzeCfg(cfg, symbols, context.ReportDiagnostic));
            }
        }

        private static void AnalyzeCfg(ControlFlowGraph cfg, StaticEcsSymbols symbols, Action<Diagnostic> report) {
            HashSet<ILocalSymbol> tracked = null;
            foreach (var block in cfg.Blocks) {
                foreach (var op in block.Operations) CollectTracked(op, symbols, ref tracked);
                if (block.BranchValue != null) CollectTracked(block.BranchValue, symbols, ref tracked);
            }
            if (tracked is null) return;

            foreach (var block in cfg.Blocks) {
                foreach (var op in block.Operations) ReportEscapes(op, tracked, report);
                if (block.BranchValue != null) ReportEscapes(block.BranchValue, tracked, report);
            }
        }

        private static void CollectTracked(IOperation root, StaticEcsSymbols symbols, ref HashSet<ILocalSymbol> tracked) {
            foreach (var d in root.DescendantsAndSelf()) {
                if (d is not ISimpleAssignmentOperation a || !a.IsRef) continue;
                if (a.Target is not ILocalReferenceOperation localRef) continue;
                if (IsAtomicallyValuedType(localRef.Local.Type)) continue;
                if (!IsAllowListedRefReturn(a.Value, symbols)) continue;
                tracked ??= new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
                tracked.Add(localRef.Local);
            }
        }

        private static void ReportEscapes(IOperation root, HashSet<ILocalSymbol> tracked, Action<Diagnostic> report) {
            foreach (var d in root.DescendantsAndSelf()) {
                if (d is not IArgumentOperation arg) continue;
                if (arg.Parameter?.RefKind != RefKind.None) continue;
                var local = ResolveLocalReference(arg.Value);
                if (local is null || !tracked.Contains(local)) continue;
                report(Diagnostic.Create(Diagnostics.RefLocalPassedByValue, arg.Value.Syntax.GetLocation(), local.Name));
            }
        }

        private static bool IsAllowListedRefReturn(IOperation value, StaticEcsSymbols symbols) {
            var match = OperationHelpers.TryResolveRefReturningChain(value, symbols, out _, out _);
            return match == RefChainMatch.Write || match == RefChainMatch.Read;
        }

        // Atomic value types pass losslessly by value: reference types (local stores a pointer that
        // remains alive through copies), primitives, and enums. Everything else has structure that
        // could be mutated through ref and lost in a copy. Entity is intentionally NOT atomic — it's a
        // multi-field struct.
        private static bool IsAtomicallyValuedType(ITypeSymbol type) {
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
            value = OperationHelpers.UnwrapImplicitConversions(value);
            return value is ILocalReferenceOperation localRef ? localRef.Local : null;
        }
    }
}

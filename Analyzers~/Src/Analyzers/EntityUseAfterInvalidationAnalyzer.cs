using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0040 — Use of an entity alias after a call that invalidated the underlying storage.
    ///
    /// Three entry points share a common CFG-based reachability analysis:
    ///   • Pattern A: lambda passed to WorldQuery&lt;TFilter&gt;.For(...). Aliases = ref/in lambda parameters.
    ///   • Pattern B: struct/class implementing any IQuery callback interface. Aliases = ref/in parameters of Invoke.
    ///   • Pattern C: ref-local within any method body, bound to entity.Ref/Mut/Read/Add(). Aliases = those locals.
    ///
    /// For each invalidator call (Destroy/MoveTo/Unload — full; Delete&lt;T&gt; — per-type) on the entity source,
    /// every forward-reachable access to an affected alias produces a diagnostic.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class EntityUseAfterInvalidationAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.EntityUseAfterInvalidation);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.EntityType is null) return;
                if (symbols.EntityFullInvalidators.IsEmpty && symbols.EntityComponentInvalidators.IsEmpty) return;

                if (symbols.WorldQuery is not null) {
                    start.RegisterOperationAction(ctx => AnalyzeForInvocation(ctx, symbols), OperationKind.Invocation);
                }

                // One block-action handles both Pattern B (Invoke method of IQuery-implementing type)
                // and Pattern C (ref-locals tracking inside any body).
                start.RegisterOperationBlockAction(ctx => AnalyzeBlock(ctx, symbols));
            });
        }

        // ============================================================================
        // Pattern A: Lambda in WorldQuery<TFilter>.For(...)
        // ============================================================================
        private static void AnalyzeForInvocation(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var invocation = (IInvocationOperation)context.Operation;
            if (invocation.TargetMethod is null || invocation.TargetMethod.Name != "For") return;
            if (!symbols.IsWithinWorldQuery(invocation.TargetMethod.ContainingType)) return;

            foreach (var argument in invocation.Arguments) {
                var lambda = ExtractLambda(argument.Value);
                if (lambda is null) continue;
                // Lambdas don't expose a usable CFG via ControlFlowGraph.Create (parent != null), so we
                // pass null and let AnalyzeParameterAliases fall back to syntax-span ordering.
                AnalyzeParameterAliases(context.ReportDiagnostic, lambda.Symbol, lambda.Body, null, symbols);
            }
        }

        private static IAnonymousFunctionOperation ExtractLambda(IOperation value) => OperationHelpers.ExtractLambda(value);

        // ============================================================================
        // OperationBlock entry: routes to Pattern B (IQuery.Invoke) and Pattern C (ref-locals).
        // ============================================================================
        private static void AnalyzeBlock(OperationBlockAnalysisContext context, StaticEcsSymbols symbols) {
            // Pattern B — gate by structure only; AnalyzeParameterAliases scans for the Entity parameter
            // at any position (no longer assumes index 0).
            if (!symbols.QueryCallbackInterfaces.IsEmpty
                && context.OwningSymbol is IMethodSymbol method
                && method.Name == "Invoke"
                && method.ContainingType is { } owner
                && (owner.TypeKind == TypeKind.Struct || owner.TypeKind == TypeKind.Class)
                && owner.AllInterfaces.Any(i => symbols.QueryCallbackInterfaces.Contains(i.OriginalDefinition))
                && method.Parameters.Length >= 2) {
                foreach (var block in context.OperationBlocks) {
                    AnalyzeParameterAliases(context.ReportDiagnostic, method, block, TryCreateCfg(block), symbols);
                }
            }

            // Pattern C
            if (symbols.RefReturningTargets.IsEmpty && symbols.RefReadonlyReadTargets.IsEmpty) return;
            AnalyzeBlockForLocalAliases(context, symbols);
        }

        // ============================================================================
        // Pattern C: Ref-locals in any method body backed by entity ref-returning members
        // ============================================================================
        private static void AnalyzeBlockForLocalAliases(OperationBlockAnalysisContext context, StaticEcsSymbols symbols) {
            // alias-local → entity-source-local (same-local policy: only direct ILocalReferenceOperation receivers)
            Dictionary<ILocalSymbol, ILocalSymbol> aliasToSource = null;
            foreach (var block in context.OperationBlocks) {
                foreach (var operation in block.DescendantsAndSelf()) {
                    if (operation is not IVariableDeclaratorOperation declarator) continue;
                    if (!declarator.Symbol.IsRef) continue;
                    var init = declarator.Initializer?.Value;
                    if (init is null) continue;
                    var unwrapped = UnwrapImplicitConversions(init);
                    if (unwrapped is not IInvocationOperation invocation) continue;
                    var target = invocation.TargetMethod?.OriginalDefinition;
                    if (target is null) continue;
                    if (!symbols.RefReturningTargets.Contains(target) && !symbols.RefReadonlyReadTargets.Contains(target)) continue;
                    var receiver = invocation.Instance is null ? null : UnwrapImplicitConversions(invocation.Instance);
                    if (receiver is not ILocalReferenceOperation receiverLocal) continue;
                    if (!IsEntityType(receiverLocal.Local.Type, symbols)) continue;
                    aliasToSource ??= new Dictionary<ILocalSymbol, ILocalSymbol>(SymbolEqualityComparer.Default);
                    aliasToSource[declarator.Symbol] = receiverLocal.Local;
                }
            }
            if (aliasToSource is null) return;

            foreach (var block in context.OperationBlocks) {
                var cfg = TryCreateCfg(block);
                if (cfg is null) continue;
                AnalyzeCfgForLocalAliases(context.ReportDiagnostic, block, cfg, aliasToSource, symbols);
            }
        }

        // ============================================================================
        // Core helper used by Pattern A and B: parameter-alias analysis
        // ============================================================================
        private static void AnalyzeParameterAliases(Action<Diagnostic> report, IMethodSymbol methodSymbol, IOperation body, ControlFlowGraph cfg, StaticEcsSymbols symbols) {
            if (body is null) return;
            if (methodSymbol.Parameters.Length < 2) return;

            // Scan all parameters: the first Entity-typed parameter (at ANY position) is the receiver
            // for invalidator calls; ref/in component parameters (everything else with non-None RefKind)
            // are the aliases.
            IParameterSymbol entityParam = null;
            var aliases = new List<IParameterSymbol>();
            foreach (var parameter in methodSymbol.Parameters) {
                if (entityParam is null && IsEntityType(parameter.Type, symbols)) {
                    entityParam = parameter;
                    continue;
                }
                if (parameter.RefKind != RefKind.None) aliases.Add(parameter);
            }
            if (entityParam is null || aliases.Count == 0) return;

            if (cfg is not null) {
                AnalyzeCfgForParameterAliases(report, body, cfg, entityParam, aliases, symbols);
            } else {
                AnalyzeSyntaxOrderForParameterAliases(report, body, entityParam, aliases, symbols);
            }
        }

        /// <summary>
        /// Fallback for contexts where ControlFlowGraph.Create is unavailable (lambdas). Uses a
        /// flow-insensitive syntactic order check: if an alias reference appears AFTER an invalidator
        /// in source order, flag it. Misses some patterns (e.g. branches), but covers the common
        /// "destroy → use in same block" case.
        /// </summary>
        private static void AnalyzeSyntaxOrderForParameterAliases(Action<Diagnostic> report, IOperation body, IParameterSymbol entityParam, List<IParameterSymbol> aliases, StaticEcsSymbols symbols) {
            foreach (var invalidator in EnumerateInvalidatorsOnParameter(body, entityParam, symbols)) {
                var affected = invalidator.fullKill
                    ? aliases
                    : FilterAliasesByType(aliases, invalidator.targetType);
                if (affected.Count == 0) continue;

                var invalidatorEnd = invalidator.invocation.Syntax.Span.End;
                foreach (var descendant in body.DescendantsAndSelf()) {
                    if (descendant is not IParameterReferenceOperation paramRef) continue;
                    if (descendant.Syntax.SpanStart < invalidatorEnd) continue;
                    if (!ContainsParameter(affected, paramRef.Parameter)) continue;
                    report(Diagnostic.Create(
                        Diagnostics.EntityUseAfterInvalidation,
                        paramRef.Syntax.GetLocation(),
                        paramRef.Parameter.Name,
                        invalidator.invocation.TargetMethod?.Name ?? "<invalidator>"));
                }
            }
        }

        private static void AnalyzeCfgForParameterAliases(Action<Diagnostic> report, IOperation body, ControlFlowGraph cfg, IParameterSymbol entityParam, List<IParameterSymbol> aliases, StaticEcsSymbols symbols) {
            foreach (var invalidator in EnumerateInvalidatorsOnParameter(body, entityParam, symbols)) {
                var affected = invalidator.fullKill
                    ? aliases
                    : FilterAliasesByType(aliases, invalidator.targetType);
                if (affected.Count == 0) continue;

                foreach (var reachable in EnumerateForwardReachable(cfg, invalidator.invocation)) {
                    if (reachable is not IParameterReferenceOperation paramRef) continue;
                    if (!ContainsParameter(affected, paramRef.Parameter)) continue;
                    report(Diagnostic.Create(
                        Diagnostics.EntityUseAfterInvalidation,
                        paramRef.Syntax.GetLocation(),
                        paramRef.Parameter.Name,
                        invalidator.invocation.TargetMethod?.Name ?? "<invalidator>"));
                }
            }
        }

        /// <summary>
        /// Per-alias gen/kill dataflow over the CFG. Mirrors the structure of FFSECS0041
        /// (<see cref="EntityHandleUseAfterInvalidationAnalyzer"/>):
        ///   • gen: an invalidator call whose receiver is the alias's source local and whose kill kind
        ///     (full or per-T) affects this alias's component type.
        ///   • kill: the alias's own <see cref="IVariableDeclaratorOperation"/> re-executing — for ref-locals,
        ///     this is the rebinding that clears any prior taint (in a foreach body the declarator runs
        ///     once per iteration, killing taint from the previous iteration's invalidator).
        ///   • merge: union (taint at block-entry if any predecessor exited tainted).
        /// One pass per alias keeps state simple; multi-alias cases are rare in practice.
        /// </summary>
        private static void AnalyzeCfgForLocalAliases(Action<Diagnostic> report, IOperation body, ControlFlowGraph cfg, Dictionary<ILocalSymbol, ILocalSymbol> aliasToSource, StaticEcsSymbols symbols) {
            foreach (var pair in aliasToSource) {
                RunDataflowForAlias(report, cfg, pair.Key, pair.Value, symbols);
            }
        }

        private readonly struct AliasTaintState {
            public readonly bool Tainted;
            public readonly string InvalidatorName;
            public AliasTaintState(bool tainted, string invalidatorName) { Tainted = tainted; InvalidatorName = invalidatorName; }
            public static readonly AliasTaintState Clean = new AliasTaintState(false, null);
        }

        private static void RunDataflowForAlias(Action<Diagnostic> report, ControlFlowGraph cfg, ILocalSymbol alias, ILocalSymbol source, StaticEcsSymbols symbols) {
            var aliasComponentType = alias.Type?.OriginalDefinition;
            var entry = new AliasTaintState[cfg.Blocks.Length];
            var visited = new bool[cfg.Blocks.Length];
            var inWorklist = new bool[cfg.Blocks.Length];
            var worklist = new Queue<BasicBlock>();
            worklist.Enqueue(cfg.Blocks[0]);
            inWorklist[0] = true;
            visited[0] = true;
            var reported = new HashSet<Location>();

            while (worklist.Count > 0) {
                var block = worklist.Dequeue();
                inWorklist[block.Ordinal] = false;
                var exitState = ProcessAliasBlock(block, alias, source, aliasComponentType, entry[block.Ordinal], symbols, report, reported);
                EnqueueAliasSuccessor(block.FallThroughSuccessor?.Destination, exitState, entry, visited, inWorklist, worklist);
                EnqueueAliasSuccessor(block.ConditionalSuccessor?.Destination, exitState, entry, visited, inWorklist, worklist);
            }
        }

        private static void EnqueueAliasSuccessor(BasicBlock successor, AliasTaintState predExit, AliasTaintState[] entry, bool[] visited, bool[] inWorklist, Queue<BasicBlock> worklist) {
            if (successor is null) return;
            var ordinal = successor.Ordinal;
            var stateChanged = predExit.Tainted && !entry[ordinal].Tainted;
            if (stateChanged) entry[ordinal] = predExit;
            if (!visited[ordinal]) {
                visited[ordinal] = true;
            } else if (!stateChanged) {
                return;
            }
            if (!inWorklist[ordinal]) {
                inWorklist[ordinal] = true;
                worklist.Enqueue(successor);
            }
        }

        private static AliasTaintState ProcessAliasBlock(BasicBlock block, ILocalSymbol alias, ILocalSymbol source, ITypeSymbol aliasComponentType, AliasTaintState state, StaticEcsSymbols symbols, Action<Diagnostic> report, HashSet<Location> reported) {
            foreach (var topOp in block.Operations) {
                state = ProcessAliasTopOperation(topOp, alias, source, aliasComponentType, state, symbols, report, reported);
            }
            if (block.BranchValue is not null) {
                state = ProcessAliasTopOperation(block.BranchValue, alias, source, aliasComponentType, state, symbols, report, reported);
            }
            return state;
        }

        private static AliasTaintState ProcessAliasTopOperation(IOperation topOp, ILocalSymbol alias, ILocalSymbol source, ITypeSymbol aliasComponentType, AliasTaintState stateAtStart, StaticEcsSymbols symbols, Action<Diagnostic> report, HashSet<Location> reported) {
            var hasInvalidator = false;
            string invalidatorName = null;
            var hasKill = false;

            foreach (var descendant in topOp.DescendantsAndSelf()) {
                if (descendant is IInvocationOperation inv
                    && TryClassifyInvalidator(inv, symbols, out var fullKill, out var targetType)
                    && IsInvocationOnLocal(inv, source)
                    && AffectsAlias(fullKill, targetType, aliasComponentType)) {
                    hasInvalidator = true;
                    invalidatorName ??= inv.TargetMethod?.Name;
                }
                // Roslyn's CFG lowers `ref var x = ref expr` into `ISimpleAssignmentOperation(IsRef=true,
                // Target=ILocalReferenceOperation(x), Value=expr)`. The source-form IVariableDeclaratorOperation
                // is not present in CFG blocks, so we match the lowered shape directly. IsRef=true distinguishes
                // a re-binding (kill) from a write-through-the-ref (`x = value`), which is a USE of the alias.
                if (descendant is ISimpleAssignmentOperation assignment
                    && assignment.IsRef
                    && assignment.Target is ILocalReferenceOperation targetRef
                    && SymbolEqualityComparer.Default.Equals(targetRef.Local, alias)) {
                    hasKill = true;
                }
            }

            if (stateAtStart.Tainted) {
                foreach (var descendant in topOp.DescendantsAndSelf()) {
                    if (descendant is not ILocalReferenceOperation localRef) continue;
                    if (!SymbolEqualityComparer.Default.Equals(localRef.Local, alias)) continue;
                    // Don't flag the LHS of the kill-assignment itself — that reference IS the rebind,
                    // not a use of the stale alias. (Mirrors FFSECS0041's IsKillAssignmentLhs.)
                    if (descendant.Parent is ISimpleAssignmentOperation parentAssignment
                        && parentAssignment.IsRef
                        && ReferenceEquals(parentAssignment.Target, descendant)) continue;
                    var location = descendant.Syntax.GetLocation();
                    if (!reported.Add(location)) continue;
                    report(Diagnostic.Create(
                        Diagnostics.EntityUseAfterInvalidation,
                        location,
                        alias.Name,
                        stateAtStart.InvalidatorName ?? "<invalidator>"));
                }
            }

            // Kill wins over gen within the same top-op (matches FFSECS0041 semantics).
            if (hasKill) return AliasTaintState.Clean;
            if (hasInvalidator) return new AliasTaintState(true, invalidatorName);
            return stateAtStart;
        }

        private static bool IsInvocationOnLocal(IInvocationOperation invocation, ILocalSymbol source) {
            if (invocation.Instance is null) return false;
            var receiver = UnwrapImplicitConversions(invocation.Instance);
            return receiver is ILocalReferenceOperation localRef
                   && SymbolEqualityComparer.Default.Equals(localRef.Local, source);
        }

        private static bool AffectsAlias(bool fullKill, ITypeSymbol targetType, ITypeSymbol aliasComponentType) {
            if (fullKill) return true;
            if (targetType is null || aliasComponentType is null) return false;
            return SymbolEqualityComparer.Default.Equals(targetType.OriginalDefinition, aliasComponentType);
        }

        // ============================================================================
        // CFG construction — handles every IOperation kind that has a Create overload.
        // ============================================================================
        private static ControlFlowGraph TryCreateCfg(IOperation body) => OperationHelpers.TryCreateCfg(body);

        // ============================================================================
        // Invalidator discovery
        // ============================================================================
        private readonly struct InvalidatorOnParameter {
            public readonly IInvocationOperation invocation;
            public readonly bool fullKill;
            public readonly ITypeSymbol targetType;
            public InvalidatorOnParameter(IInvocationOperation invocation, bool fullKill, ITypeSymbol targetType) {
                this.invocation = invocation; this.fullKill = fullKill; this.targetType = targetType;
            }
        }

        // Walks the ORIGINAL operation tree (not CFG-lowered) so receivers stay as IParameterReferenceOperation/
        // ILocalReferenceOperation. CFG lowers some local references into IFlowCaptureReferenceOperation, which
        // would otherwise prevent us from matching the receiver.
        private static IEnumerable<InvalidatorOnParameter> EnumerateInvalidatorsOnParameter(IOperation body, IParameterSymbol entityParam, StaticEcsSymbols symbols) {
            if (body is null) yield break;
            foreach (var descendant in body.DescendantsAndSelf()) {
                if (descendant is not IInvocationOperation invocation) continue;
                if (!TryClassifyInvalidator(invocation, symbols, out var fullKill, out var targetType)) continue;
                var receiver = invocation.Instance is null ? null : UnwrapImplicitConversions(invocation.Instance);
                if (receiver is not IParameterReferenceOperation paramRef) continue;
                if (!SymbolEqualityComparer.Default.Equals(paramRef.Parameter, entityParam)) continue;
                yield return new InvalidatorOnParameter(invocation, fullKill, targetType);
            }
        }

        private static bool TryClassifyInvalidator(IInvocationOperation invocation, StaticEcsSymbols symbols, out bool fullKill, out ITypeSymbol targetType) {
            fullKill = false;
            targetType = null;
            var target = invocation.TargetMethod?.OriginalDefinition;
            if (target is null) return false;
            if (symbols.EntityFullInvalidators.Contains(target)) {
                fullKill = true;
                return true;
            }
            if (symbols.EntityComponentInvalidators.Contains(target)) {
                if (invocation.TargetMethod.TypeArguments.Length >= 1) {
                    targetType = invocation.TargetMethod.TypeArguments[0];
                    return true;
                }
            }
            return false;
        }

        // ============================================================================
        // Forward-reachability over the CFG starting from an invalidator invocation
        // ============================================================================
        private static IEnumerable<IOperation> EnumerateForwardReachable(ControlFlowGraph cfg, IInvocationOperation invalidator) {
            BasicBlock startBlock = null;
            var startOperationIndex = -1;

            foreach (var block in cfg.Blocks) {
                for (var opIdx = 0; opIdx < block.Operations.Length; opIdx++) {
                    if (block.Operations[opIdx].DescendantsAndSelf().Any(d => ReferenceEquals(d, invalidator) || d.Syntax == invalidator.Syntax)) {
                        startBlock = block;
                        startOperationIndex = opIdx;
                        break;
                    }
                }
                if (startBlock is not null) break;
            }
            if (startBlock is null) yield break;

            for (var i = startOperationIndex + 1; i < startBlock.Operations.Length; i++) {
                foreach (var descendant in startBlock.Operations[i].DescendantsAndSelf()) {
                    yield return descendant;
                }
            }
            if (startBlock.BranchValue is not null) {
                foreach (var descendant in startBlock.BranchValue.DescendantsAndSelf()) {
                    yield return descendant;
                }
            }

            var visited = new HashSet<BasicBlock> { startBlock };
            var queue = new Queue<BasicBlock>();
            EnqueueSuccessors(startBlock, queue, visited);

            while (queue.Count > 0) {
                var block = queue.Dequeue();
                foreach (var op in block.Operations) {
                    foreach (var descendant in op.DescendantsAndSelf()) {
                        yield return descendant;
                    }
                }
                if (block.BranchValue is not null) {
                    foreach (var descendant in block.BranchValue.DescendantsAndSelf()) {
                        yield return descendant;
                    }
                }
                EnqueueSuccessors(block, queue, visited);
            }
        }

        private static void EnqueueSuccessors(BasicBlock block, Queue<BasicBlock> queue, HashSet<BasicBlock> visited) {
            EnqueueIfNew(block.FallThroughSuccessor?.Destination, queue, visited);
            EnqueueIfNew(block.ConditionalSuccessor?.Destination, queue, visited);
        }

        private static void EnqueueIfNew(BasicBlock block, Queue<BasicBlock> queue, HashSet<BasicBlock> visited) {
            if (block is null) return;
            if (visited.Add(block)) queue.Enqueue(block);
        }

        // ============================================================================
        // Utilities
        // ============================================================================
        private static IOperation UnwrapImplicitConversions(IOperation value) => OperationHelpers.UnwrapImplicitConversions(value);

        private static bool IsEntityType(ITypeSymbol type, StaticEcsSymbols symbols) {
            if (type is null || symbols.EntityType is null) return false;
            return SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, symbols.EntityType);
        }

        private static List<IParameterSymbol> FilterAliasesByType(List<IParameterSymbol> aliases, ITypeSymbol targetType) {
            var result = new List<IParameterSymbol>();
            foreach (var alias in aliases) {
                if (SymbolEqualityComparer.Default.Equals(alias.Type.OriginalDefinition, targetType?.OriginalDefinition)) {
                    result.Add(alias);
                }
            }
            return result;
        }

        private static bool ContainsParameter(List<IParameterSymbol> aliases, IParameterSymbol parameter) {
            foreach (var alias in aliases) {
                if (SymbolEqualityComparer.Default.Equals(alias, parameter)) return true;
            }
            return false;
        }
    }
}

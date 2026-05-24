using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0041 — Use of an Entity variable (local or parameter) after the handle has been
    /// invalidated by Destroy/MoveTo/Unload on the same variable. The only allowed operation on
    /// the variable while invalidated is reassignment (<c>entity = ...</c>), which clears the taint.
    ///
    /// This is the entity-handle counterpart to FFSECS0040, which tracks ref/in aliases into
    /// component storage. FFSECS0040 covers the case <c>ref var hp = ref entity.Ref&lt;Health&gt;(); entity.Destroy(); hp = ...</c>;
    /// FFSECS0041 covers <c>entity.Destroy(); entity.Add&lt;Health&gt;();</c> on the entity itself.
    ///
    /// Detection pipeline:
    ///  1. For each body, find every full-invalidator call (<see cref="StaticEcsSymbols.EntityFullInvalidators"/>)
    ///     whose receiver is a local or parameter of <see cref="StaticEcsSymbols.EntityType"/>.
    ///  2. Per entity variable: run a forward gen/kill dataflow over the CFG.
    ///     • gen: an invalidator call on the variable taints subsequent ops in the same block.
    ///     • kill: an assignment with the variable as LHS (<c>entity = ...</c>) clears the taint.
    ///     • merge: union (any predecessor block tainted ⇒ tainted on entry).
    ///  3. Any reference to the variable encountered while tainted-on-entry-of-op (other than the
    ///     LHS of a kill-assignment, the receiver of the invalidator itself, or one of the listed
    ///     metadata-only accesses) is reported.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class EntityHandleUseAfterInvalidationAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.EntityHandleUseAfterInvalidation);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.EntityType is null) return;
                if (symbols.EntityFullInvalidators.IsEmpty) return;

                start.RegisterOperationBlockAction(ctx => AnalyzeBlock(ctx, symbols));
            });
        }

        private static void AnalyzeBlock(OperationBlockAnalysisContext context, StaticEcsSymbols symbols) {
            foreach (var block in context.OperationBlocks) {
                var cfg = OperationHelpers.TryCreateCfg(block);
                if (cfg is null) continue;
                AnalyzeCfg(context.ReportDiagnostic, block, cfg, symbols);
            }
        }

        private static void AnalyzeCfg(System.Action<Diagnostic> report, IOperation body, ControlFlowGraph cfg, StaticEcsSymbols symbols) {
            // Find entity locals/params that the body invalidates at least once. Skipping bodies with
            // zero invalidator calls avoids running dataflow over the entire CFG for every body.
            var candidates = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var descendant in body.DescendantsAndSelf()) {
                if (descendant is not IInvocationOperation invocation) continue;
                if (!IsFullInvalidator(invocation, symbols)) continue;
                var receiverSymbol = TryGetReceiverSymbol(invocation, symbols);
                if (receiverSymbol is not null) candidates.Add(receiverSymbol);
            }
            if (candidates.Count == 0) return;

            foreach (var candidate in candidates) {
                RunDataflowForVariable(report, cfg, candidate, symbols);
            }
        }

        /// <summary>
        /// Gen/kill dataflow over the CFG for a single tracked entity variable.
        /// </summary>
        /// <summary>Per-block dataflow state: tainted flag + name of the most recent invalidator
        /// responsible (for richer diagnostic messages across block boundaries).</summary>
        private readonly struct TaintState {
            public readonly bool Tainted;
            public readonly string InvalidatorName;
            public TaintState(bool tainted, string invalidatorName) { Tainted = tainted; InvalidatorName = invalidatorName; }
            public static readonly TaintState Clean = new TaintState(false, null);
        }

        private static void RunDataflowForVariable(System.Action<Diagnostic> report, ControlFlowGraph cfg, ISymbol variable, StaticEcsSymbols symbols) {
            var entry = new TaintState[cfg.Blocks.Length];
            var visited = new bool[cfg.Blocks.Length];
            var inWorklist = new bool[cfg.Blocks.Length];
            var worklist = new Queue<BasicBlock>();

            worklist.Enqueue(cfg.Blocks[0]);
            inWorklist[0] = true;
            visited[0] = true;
            // De-dup reports: a single source-location may be visited multiple times during fixpoint.
            var reported = new HashSet<Microsoft.CodeAnalysis.Location>();

            while (worklist.Count > 0) {
                var block = worklist.Dequeue();
                inWorklist[block.Ordinal] = false;

                var exitState = ProcessBlock(block, variable, entry[block.Ordinal], symbols, report, reported);

                EnqueueSuccessor(block.FallThroughSuccessor?.Destination, exitState, entry, visited, inWorklist, worklist);
                EnqueueSuccessor(block.ConditionalSuccessor?.Destination, exitState, entry, visited, inWorklist, worklist);
            }
        }

        /// <summary>
        /// Enqueue a successor block. Two reasons to enqueue:
        ///  (1) first visit ever (we have to look inside even with clean entry state to find invalidators);
        ///  (2) entry state newly escalated to tainted (need to re-process with new state).
        /// </summary>
        private static void EnqueueSuccessor(BasicBlock successor, TaintState predExit, TaintState[] entry, bool[] visited, bool[] inWorklist, Queue<BasicBlock> worklist) {
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

        /// <summary>
        /// Walks operations in <paramref name="block"/> in source order, updating taint state and
        /// reporting any tainted use of <paramref name="variable"/>. Returns the exit taint state.
        /// </summary>
        private static TaintState ProcessBlock(BasicBlock block, ISymbol variable, TaintState state, StaticEcsSymbols symbols, System.Action<Diagnostic> report, HashSet<Microsoft.CodeAnalysis.Location> reported) {
            foreach (var topOp in block.Operations) {
                state = ProcessTopOperation(topOp, variable, state, symbols, report, reported);
            }
            if (block.BranchValue is not null) {
                state = ProcessTopOperation(block.BranchValue, variable, state, symbols, report, reported);
            }
            return state;
        }

        /// <summary>
        /// Process a single top-level operation. Identifies the invalidator (gen) / reassignment (kill)
        /// inside this top-op, reports tainted uses, then applies gen/kill atomically at the end of the op.
        /// Order within a single top-op is approximate but precise enough for the patterns this rule targets.
        /// </summary>
        private static TaintState ProcessTopOperation(IOperation topOp, ISymbol variable, TaintState stateAtStart, StaticEcsSymbols symbols, System.Action<Diagnostic> report, HashSet<Microsoft.CodeAnalysis.Location> reported) {
            var hasInvalidator = false;
            string invalidatorName = null;
            var hasKill = false;

            foreach (var descendant in topOp.DescendantsAndSelf()) {
                if (descendant is IInvocationOperation inv
                    && IsFullInvalidator(inv, symbols)
                    && SymbolEqualityComparer.Default.Equals(TryGetReceiverSymbol(inv, symbols), variable)) {
                    hasInvalidator = true;
                    if (inv.TargetMethod?.Name is { } name) invalidatorName = name;
                }
                if (IsKillAssignment(descendant, variable) || IsOutArgumentWrite(descendant, variable)) {
                    hasKill = true;
                }
            }

            if (stateAtStart.Tainted) {
                foreach (var descendant in topOp.DescendantsAndSelf()) {
                    if (!IsVariableReference(descendant, variable)) continue;
                    if (IsReceiverOfInvalidator(descendant, symbols, variable)) continue;
                    if (IsKillAssignmentLhs(descendant, variable)) continue;
                    if (IsOutArgumentBinding(descendant)) continue;
                    var location = descendant.Syntax.GetLocation();
                    if (!reported.Add(location)) continue;
                    report(Diagnostic.Create(
                        Diagnostics.EntityHandleUseAfterInvalidation,
                        location,
                        variable.Name,
                        stateAtStart.InvalidatorName ?? "<invalidator>"));
                }
            }

            // Kill wins over gen within the same top-op.
            if (hasKill) return TaintState.Clean;
            if (hasInvalidator) return new TaintState(true, invalidatorName);
            return stateAtStart;
        }

        private static bool IsVariableReference(IOperation op, ISymbol variable) {
            switch (op) {
                case ILocalReferenceOperation localRef:
                    return SymbolEqualityComparer.Default.Equals(localRef.Local, variable);
                case IParameterReferenceOperation paramRef:
                    return SymbolEqualityComparer.Default.Equals(paramRef.Parameter, variable);
                default:
                    return false;
            }
        }

        /// <summary>
        /// True if <paramref name="op"/> is the receiver of an invocation that is a full invalidator on <paramref name="variable"/>.
        /// The invalidator call itself reads the variable BEFORE the call's side effect, so flagging
        /// the receiver as a use would be wrong (the gen happens at the end of the call).
        /// </summary>
        private static bool IsReceiverOfInvalidator(IOperation op, StaticEcsSymbols symbols, ISymbol variable) {
            if (op.Parent is not IInvocationOperation invocation) return false;
            if (!ReferenceEquals(OperationHelpers.UnwrapImplicitConversions(invocation.Instance), op)) return false;
            if (!IsFullInvalidator(invocation, symbols)) return false;
            return SymbolEqualityComparer.Default.Equals(TryGetReceiverSymbol(invocation, symbols), variable);
        }

        /// <summary>True if <paramref name="op"/> is itself the LHS of an <c>X = ...</c> kill assignment for <paramref name="variable"/>.</summary>
        private static bool IsKillAssignmentLhs(IOperation op, ISymbol variable) {
            if (op.Parent is not ISimpleAssignmentOperation assignment) return false;
            if (!ReferenceEquals(assignment.Target, op)) return false;
            return IsVariableReference(op, variable);
        }

        /// <summary>True if anywhere inside <paramref name="op"/> we see <c>variable = ...</c>.</summary>
        private static bool IsKillAssignment(IOperation op, ISymbol variable) {
            return op is ISimpleAssignmentOperation assignment
                   && IsVariableReference(assignment.Target, variable);
        }

        /// <summary>
        /// True if <paramref name="op"/> is an <c>out</c> argument that writes to <paramref name="variable"/>.
        /// Two shapes: <c>Method(out var X)</c> (declaration) and <c>Method(out X)</c> (existing local/param).
        /// Both count as a kill on X: the method is contractually required to write to it before returning.
        /// </summary>
        private static bool IsOutArgumentWrite(IOperation op, ISymbol variable) {
            if (op is not IArgumentOperation argument) return false;
            if (argument.Parameter?.RefKind != RefKind.Out) return false;
            var value = argument.Value;
            if (value is IDeclarationExpressionOperation declExpr) {
                if (declExpr.Expression is ILocalReferenceOperation localDecl) {
                    return SymbolEqualityComparer.Default.Equals(localDecl.Local, variable);
                }
                return false;
            }
            return IsVariableReference(value, variable);
        }

        /// <summary>
        /// True if <paramref name="op"/> is itself the variable reference appearing inside an <c>out</c> argument.
        /// We don't want to flag the receiver of <c>TryUnpack(out var entity)</c> as a tainted use — the out-write
        /// IS the kill, not a read.
        /// </summary>
        private static bool IsOutArgumentBinding(IOperation op) {
            // Walk up: ref/parameter ref → IDeclarationExpression → IArgumentOperation(Out).
            // Or: ref/parameter ref → IArgumentOperation(Out).
            var parent = op.Parent;
            if (parent is IDeclarationExpressionOperation) parent = parent.Parent;
            return parent is IArgumentOperation arg && arg.Parameter?.RefKind == RefKind.Out;
        }

        private static bool IsFullInvalidator(IInvocationOperation invocation, StaticEcsSymbols symbols) {
            var target = invocation.TargetMethod?.OriginalDefinition;
            return target is not null && symbols.EntityFullInvalidators.Contains(target);
        }

        /// <summary>
        /// Extracts the receiver symbol of <paramref name="invocation"/> if the receiver is a direct
        /// local or parameter reference to a value of Entity type. Returns null for indirect receivers
        /// (e.g. field access, computed expressions) which this rule deliberately does not track.
        /// </summary>
        private static ISymbol TryGetReceiverSymbol(IInvocationOperation invocation, StaticEcsSymbols symbols) {
            if (invocation.Instance is null) return null;
            var receiver = OperationHelpers.UnwrapImplicitConversions(invocation.Instance);
            ITypeSymbol receiverType;
            ISymbol receiverSymbol;
            switch (receiver) {
                case ILocalReferenceOperation localRef:
                    receiverType = localRef.Local.Type;
                    receiverSymbol = localRef.Local;
                    break;
                case IParameterReferenceOperation paramRef:
                    receiverType = paramRef.Parameter.Type;
                    receiverSymbol = paramRef.Parameter;
                    break;
                default:
                    return null;
            }
            if (receiverType is null || symbols.EntityType is null) return null;
            if (!SymbolEqualityComparer.Default.Equals(receiverType.OriginalDefinition, symbols.EntityType)) return null;
            return receiverSymbol;
        }
    }
}

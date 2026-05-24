using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers {
    internal enum RefChainMatch {
        /// <summary>No ref-returning member in the chain, or the chain is irrelevant to the rule.</summary>
        None,
        /// <summary>Chain resolves to a member from <see cref="StaticEcsSymbols.RefReturningTargets"/>.</summary>
        Write,
        /// <summary>Chain resolves to a member from <see cref="StaticEcsSymbols.RefReadonlyReadTargets"/>.</summary>
        Read,
        /// <summary>
        /// Ref-returning member is present in the chain, but the rule should NOT fire:
        /// either the final value is a reference type (copy concern doesn't apply), or a non-ref
        /// property breaks the ref chain between the local and the ref-returning call (binding by
        /// ref would not compile, so there's nothing to suggest).
        /// </summary>
        SuppressedByChain,
    }

    /// <summary>
    /// Tiny pure helpers shared across analyzers — extracted to keep individual analyzer files focused.
    /// All methods are pure: they only inspect their inputs and never report diagnostics.
    /// </summary>
    internal static class OperationHelpers {
        /// <summary>
        /// Unwraps implicit (compiler-inserted) conversion operations to expose the underlying value.
        /// Explicit conversions (user-written casts) are deliberately left in place — they may carry
        /// intent (escape-hatch markers etc.).
        /// </summary>
        public static IOperation UnwrapImplicitConversions(IOperation value) {
            while (value is IConversionOperation conv && conv.IsImplicit) {
                value = conv.Operand;
            }
            return value;
        }

        /// <summary>
        /// Unwraps an argument value down to its <see cref="IAnonymousFunctionOperation"/> if it is one.
        /// Returns null for method-group references or non-lambda values.
        /// </summary>
        public static IAnonymousFunctionOperation ExtractLambda(IOperation value) {
            while (value is IDelegateCreationOperation delegateCreation) {
                value = delegateCreation.Target;
            }
            return value as IAnonymousFunctionOperation;
        }

        /// <summary>
        /// Unwraps an argument value down to its <see cref="IMethodReferenceOperation"/> if it is a
        /// method-group reference (e.g. <c>.For(SomeMethod)</c>). Returns null for lambdas or other values.
        /// </summary>
        public static IMethodReferenceOperation ExtractMethodReference(IOperation value) {
            while (value is IDelegateCreationOperation delegateCreation) {
                value = delegateCreation.Target;
            }
            return value as IMethodReferenceOperation;
        }

        /// <summary>
        /// Tries to build a <see cref="ControlFlowGraph"/> for the given body operation.
        /// <c>ControlFlowGraph.Create</c> requires a root operation whose Parent is null; this helper
        /// walks up the tree to find such a root and dispatches to the correct Create overload.
        /// Returns null for unsupported shapes (e.g. lambdas — Roslyn cannot create a CFG for them
        /// directly) or if construction throws.
        /// </summary>
        /// <summary>
        /// Walks the receiver chain of <paramref name="value"/> from outer to inner through field and
        /// property accesses, looking for a StaticEcs ref-returning member (FFSECS0010/0012 allow-list)
        /// or ref-readonly Read&lt;T&gt; member (FFSECS0011). Returns the classification and, when matched,
        /// the resolved <paramref name="target"/> symbol plus the <paramref name="matchedOperation"/>
        /// (the invocation/property reference node itself, used by analyzers for diagnostic location).
        ///
        /// <para>Suppression rules — when ref-returning member IS present but the diagnostic should not fire:</para>
        /// <list type="bullet">
        ///   <item>The outermost <paramref name="value"/> resolves to a reference type — the local would
        ///         hold a heap reference; mutations through it reach the same object, no copy concern.</item>
        ///   <item>Any step on the way from the outer value to the ref-returning member is an
        ///         <see cref="IPropertyReferenceOperation"/> whose property does NOT return by ref —
        ///         the ref chain is already broken at that property, so 'ref' binding cannot compile,
        ///         and the user is explicitly working with a copy.</item>
        /// </list>
        /// <para>Also returns <see cref="RefChainMatch.SuppressedByChain"/> when the ref-returning member's
        /// T payload is itself a reference type (e.g. <c>Resource&lt;MyClass&gt;.Value</c>) — same
        /// reasoning as the outer reference-type check, but applies when the outer type cannot be
        /// resolved (e.g. <c>var</c> on a member whose type is unknown).</para>
        /// </summary>
        public static RefChainMatch TryResolveRefReturningChain(
            IOperation value,
            StaticEcsSymbols symbols,
            out ISymbol target,
            out IOperation matchedOperation) {
            target = null;
            matchedOperation = null;
            if (value is null || symbols is null) return RefChainMatch.None;

            var outerIsReferenceType = value.Type?.IsReferenceType == true;
            var current = UnwrapImplicitConversions(value);
            var sawNonRefProperty = false;

            while (current is not null) {
                ISymbol candidate = current switch {
                    IInvocationOperation invocation => invocation.TargetMethod?.OriginalDefinition,
                    IPropertyReferenceOperation propertyRef => propertyRef.Property?.OriginalDefinition,
                    _ => null,
                };
                if (candidate is not null) {
                    var matchesWrite = symbols.RefReturningTargets.Contains(candidate);
                    var matchesRead = symbols.RefReadonlyReadTargets.Contains(candidate);
                    if (matchesWrite || matchesRead) {
                        if (outerIsReferenceType || sawNonRefProperty || !RefReturningPayloadIsValueType(current)) {
                            return RefChainMatch.SuppressedByChain;
                        }
                        target = candidate;
                        matchedOperation = current;
                        return matchesWrite ? RefChainMatch.Write : RefChainMatch.Read;
                    }
                }

                switch (current) {
                    case IFieldReferenceOperation fieldRef when fieldRef.Instance is not null:
                        current = UnwrapImplicitConversions(fieldRef.Instance);
                        continue;
                    case IPropertyReferenceOperation propRef when propRef.Instance is not null:
                        // A property breaks the ref chain unless it returns by ref / ref readonly.
                        // Once broken, any deeper ref-returning call cannot be reached by 'ref' binding
                        // from the outer expression — the user already accepted a copy at this boundary.
                        if (!propRef.Property.ReturnsByRef && !propRef.Property.ReturnsByRefReadonly) {
                            sawNonRefProperty = true;
                        }
                        current = UnwrapImplicitConversions(propRef.Instance);
                        continue;
                    default:
                        return RefChainMatch.None;
                }
            }
            return RefChainMatch.None;
        }

        /// <summary>
        /// True unless we can prove the ref-returned payload (the T of Ref&lt;T&gt;()/Read&lt;T&gt;()/etc.)
        /// is a reference type. Defaults to <c>true</c> when the T cannot be resolved (preserves
        /// "flag" behavior when unsure).
        /// </summary>
        private static bool RefReturningPayloadIsValueType(IOperation operation) {
            ITypeSymbol payload = operation switch {
                IInvocationOperation invocation when invocation.TargetMethod is { } method =>
                    method.TypeArguments.Length > 0
                        ? method.TypeArguments[0]
                        : (method.ContainingType?.TypeArguments.Length > 0 ? method.ContainingType.TypeArguments[0] : null),
                IPropertyReferenceOperation propRef when propRef.Property?.ContainingType?.TypeArguments.Length > 0 =>
                    propRef.Property.ContainingType.TypeArguments[0],
                _ => null,
            };
            if (payload is null) return true;
            return payload.IsValueType;
        }

        public static ControlFlowGraph TryCreateCfg(IOperation body) {
            if (body is null) return null;
            var root = body;
            while (root.Parent is not null) {
                root = root.Parent;
            }
            try {
                switch (root) {
                    case IBlockOperation b: return ControlFlowGraph.Create(b);
                    case IMethodBodyOperation m: return ControlFlowGraph.Create(m);
                    case IConstructorBodyOperation c: return ControlFlowGraph.Create(c);
                    case IFieldInitializerOperation f: return ControlFlowGraph.Create(f);
                    case IPropertyInitializerOperation p: return ControlFlowGraph.Create(p);
                    case IParameterInitializerOperation pi: return ControlFlowGraph.Create(pi);
                    default: return null;
                }
            } catch {
                return null;
            }
        }
    }
}

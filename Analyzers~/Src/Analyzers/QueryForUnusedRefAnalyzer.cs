using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0030 — WorldQuery&lt;TFilter&gt;.For(...) lambda parameter declared 'ref T' but
    /// never mutated inside the lambda body. Suggest the 'in T' overload.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class QueryForUnusedRefAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.QueryForRefParameterNotMutated);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.QueryBuilderForMethods.IsEmpty) return;

                start.RegisterOperationAction(ctx => AnalyzeInvocation(ctx, symbols), OperationKind.Invocation);
            });
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var invocation = (IInvocationOperation)context.Operation;
            if (!symbols.QueryBuilderForMethods.Contains(invocation.TargetMethod.OriginalDefinition)) return;

            foreach (var argument in invocation.Arguments) {
                var lambda = OperationHelpers.ExtractLambda(argument.Value);
                if (lambda is null) continue;

                foreach (var parameter in lambda.Symbol.Parameters) {
                    if (parameter.RefKind != RefKind.Ref) continue;
                    if (HasMutation(lambda.Body, parameter)) continue;

                    var location = parameter.Locations.Length > 0 ? parameter.Locations[0] : Location.None;
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.QueryForRefParameterNotMutated,
                        location,
                        parameter.Name,
                        parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }
            }
        }

        private static bool HasMutation(IOperation body, IParameterSymbol parameter) {
            if (body is null) return true; // unknown body → assume safe.
            foreach (var op in body.DescendantsAndSelf()) {
                switch (op) {
                    case IAssignmentOperation assignment when ReferencesParameter(assignment.Target, parameter):
                        return true;
                    case IIncrementOrDecrementOperation incdec when ReferencesParameter(incdec.Target, parameter):
                        return true;
                    case IArgumentOperation arg when arg.Parameter?.RefKind is RefKind.Ref or RefKind.Out
                                                  && ReferencesParameter(arg.Value, parameter):
                        return true;
                }
            }
            return false;
        }

        private static bool ReferencesParameter(IOperation operation, IParameterSymbol parameter) {
            while (operation is not null) {
                switch (operation) {
                    case IParameterReferenceOperation paramRef:
                        return SymbolEqualityComparer.Default.Equals(paramRef.Parameter, parameter);
                    case IFieldReferenceOperation fieldRef:
                        operation = fieldRef.Instance;
                        break;
                    case IPropertyReferenceOperation propertyRef:
                        operation = propertyRef.Instance;
                        break;
                    case IArrayElementReferenceOperation arrayRef:
                        operation = arrayRef.ArrayReference;
                        break;
                    case IConversionOperation conv:
                        operation = conv.Operand;
                        break;
                    default:
                        return false;
                }
            }
            return false;
        }
    }
}

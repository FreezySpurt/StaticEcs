; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
FFSECS0010 | FFS.StaticEcs.Correctness | Error | Ref-returning StaticEcs member result must be bound by 'ref'. CodeFix.
FFSECS0011 | FFS.StaticEcs.Correctness | Info | Read<T>() result bound to a copy (IDE suggestion; silence via .editorconfig). CodeFix.
FFSECS0012 | FFS.StaticEcs.Correctness | Info | 'ref'/'ref readonly' local backed by StaticEcs storage passed as value argument (IDE suggestion; atomically-valued types excluded; silence via .editorconfig). CodeFix.
FFSECS0020 | FFS.StaticEcs.Correctness | Error | ECS marker interface (incl. IMultiComponent) implemented by class. CodeFix.
FFSECS0021 | FFS.StaticEcs.Correctness | Warning | Non-unmanaged IMultiComponent without Write/Read override.
FFSECS0030 | FFS.StaticEcs.Correctness | Info | Query.For lambda 'ref' parameter never mutated; use 'in T'. CodeFix.
FFSECS0031 | FFS.StaticEcs.Performance | Error | Lambda in Query.For captures outer state — allocates a closure each call.
FFSECS0032 | FFS.StaticEcs.Usage | Info | IsMatch<TFilter>() can be replaced with a direct Entity Has*/Is* method (suggestion). CodeFix.
FFSECS0040 | FFS.StaticEcs.Correctness | Error | 'ref'/'in' reference to a component used after Destroy/MoveTo/Unload/Delete on the same entity.
FFSECS0041 | FFS.StaticEcs.Correctness | Error | Entity variable used after Destroy/MoveTo/Unload without reassignment.
FFSECS0050 | FFS.StaticEcs.Correctness | Error | Redundant component in query filter (duplicate within filter or overlap with lambda/IQuery param).
FFSECS0051 | FFS.StaticEcs.Correctness | Error | Contradictory All+None in query filter — matches no entity.

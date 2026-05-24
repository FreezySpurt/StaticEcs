---
title: Roslyn analyzer
parent: EN
nav_order: 6
---

# Roslyn analyzer

StaticEcs ships with a Roslyn analyzer + code-fix suite that catches common misuses of the framework at compile time. It is bundled automatically with the `FFS.StaticEcs` NuGet package — no extra reference, no manual installation. Inside the package the binaries live at:

- `analyzers/dotnet/cs/FFS.StaticEcs.Analyzers.dll` — diagnostic rules
- `analyzers/dotnet/cs/FFS.StaticEcs.Analyzers.CodeFixes.dll` — automatic code fixes

The analyzer auto-disables itself when the compilation does not reference `FFS.StaticEcs`, so it is safe to keep in any solution.

Diagnostic categories:

- **`FFS.StaticEcs.Correctness`** — code that compiles but is semantically wrong (silent copies of ref-returns, use-after-free of entities, contradictory query filters, …).
- **`FFS.StaticEcs.Performance`** — patterns that allocate or block runtime optimizations (closure-capturing lambdas in `Query.For`).
- **`FFS.StaticEcs.Usage`** — style/clarity suggestions (more direct API available).

___

## Rule index

| ID | Category | Severity | Title | CodeFix |
|---|---|---|---|---|
| [FFSECS0010](#ffsecs0010) | Correctness | Error | Ref-returning member result must be bound by 'ref' | yes |
| [FFSECS0011](#ffsecs0011) | Correctness | Info | `Read<T>()` result is bound to a copy | yes |
| [FFSECS0012](#ffsecs0012) | Correctness | Info | Ref-local backed by StaticEcs storage passed by value (atomically-valued types skipped) | yes |
| [FFSECS0020](#ffsecs0020) | Correctness | Error | StaticEcs marker interface must be implemented by a struct | yes |
| [FFSECS0021](#ffsecs0021) | Correctness | Error | `IMultiComponent` must be implemented by a struct | yes |
| [FFSECS0022](#ffsecs0022) | Correctness | Warning | Non-unmanaged `IMultiComponent` must override `Write`/`Read` | — |
| [FFSECS0030](#ffsecs0030) | Correctness | Info | Query.For lambda parameter declared `ref` but never mutated | yes |
| [FFSECS0031](#ffsecs0031) | Performance | Error | Lambda in `Query.For` captures outer state | — |
| [FFSECS0032](#ffsecs0032) | Usage | Info | `IsMatch<TFilter>()` can be replaced with a direct Entity method | yes |
| [FFSECS0040](#ffsecs0040) | Correctness | Error | `ref`/`in` reference to a component used after invalidation | — |
| [FFSECS0041](#ffsecs0041) | Correctness | Error | Entity used after invalidation | — |
| [FFSECS0050](#ffsecs0050) | Correctness | Error | Redundant component in query filter | — |
| [FFSECS0051](#ffsecs0051) | Correctness | Error | Contradictory `All` + `None` in query filter | — |

___

## Rules

### FFSECS0010
**Category:** Correctness · **Severity:** Error · **CodeFix:** yes

`Entity.Ref/Mut/Add`, `Components<T>.Ref/Mut/Add`, `Resource<T>.Value`, `NamedResource<T>.Value`, `Multi<T>.First/Last/[i]`, `MultiComponentsIterator<T>.Current` all return by reference. Binding the result to a non-ref local silently copies the component — any mutation goes to the copy, not the storage. Detected in variable declarations, value-arguments, simple assignments and non-ref return statements.

Reference-typed payloads (e.g. `Resource<MyClass>.Value`) are suppressed: copying a reference is cheap and idiomatic.

#### Pattern (will fire)
```csharp
var pos = entity.Ref<Position>();           // FFSECS0010 — silent copy
Consume(entity.Ref<Position>());            // FFSECS0010 — copied at call boundary
return entity.Ref<Position>();              // FFSECS0010 — copied at return
```

#### Fix (no diagnostic)
```csharp
ref var pos = ref entity.Ref<Position>();   // ok — ref-bound
entity.Ref<Position>().Value = 5;           // ok — direct write through ref
Consume(ref entity.Ref<Position>());        // ok — passed by ref
```

#### Explicit opt-in to a copy: `*RO` siblings
When you genuinely want a snapshot (a copy) from `Resource<T>` / `NamedResource<T>` / `Multi<T>` / `MultiComponentsIterator<T>`, use the dedicated `*RO` members instead of binding the mutable ref-return to a plain local. These return `ref readonly T` and are intentionally **not** in the analyzer's allow-list — the `RO` suffix communicates the intent in the source.

| Mutable (flagged) | Read-only sibling |
|---|---|
| `Resource<T>.Value` | `Resource<T>.ValueRO` |
| `NamedResource<T>.Value` | `NamedResource<T>.ValueRO` |
| `Multi<T>.First()` | `Multi<T>.GetFirst()` |
| `Multi<T>.Last()` | `Multi<T>.GetLast()` |
| `Multi<T>[idx]` | `Multi<T>.Get(idx)` |
| `MultiComponentsIterator<T>.Current` | `MultiComponentsIterator<T>.CurrentRO` |

```csharp
var snapshot = timer.ValueRO;               // ok — explicit RO opt-in, no diagnostic
ref readonly var refSnap = ref multi.GetFirst();
```

For `Entity` and `Components<T>` the snapshot path is `Read<T>()` / `Read(Entity)` — paired with FFSECS0011 (Info hint).

The codefix offers a one-click «Switch to '`*RO`' (intentional copy)» action.

___

### FFSECS0011
**Category:** Correctness · **Severity:** Info · **CodeFix:** yes

`Entity.Read<T>()` and `Components<T>.Read(Entity)` return `ref readonly T`. Binding to a non-ref-readonly local copies the value — undesirable for large components. Surfaced as an IDE hint; silence per-project with `dotnet_diagnostic.FFSECS0011.severity = none`.

#### Pattern (will fire)
```csharp
var snapshot = entity.Read<Position>();     // FFSECS0011 — copied
```

#### Fix (no diagnostic)
```csharp
ref readonly var snap = ref entity.Read<Position>();
Consume(in entity.Read<Position>());        // ok — passed by 'in'
```

___

### FFSECS0012
**Category:** Correctness · **Severity:** Info · **CodeFix:** yes

A `ref` / `ref readonly` local bound to a StaticEcs storage source was passed by value. This copies the component at the call boundary — the callee mutates the copy. The hint is heuristic: the analyzer can't tell an accidental loss of ref semantics from an intentional pass-the-current-value, so it surfaces as Info; to silence globally use `dotnet_diagnostic.FFSECS0012.severity = none` in `.editorconfig`.

Atomically-valued types are excluded automatically — they have no internal state that could be lost via a copy: CLR primitives (`bool`/`int`/`float`/...), enums, and reference types (the local holds a pointer; copying the pointer still hits the same heap object).

#### Pattern (will fire)
```csharp
ref var hp = ref entity.Ref<Health>();      // Health — multi-field struct
Consume(hp);                                // FFSECS0012 — copied at the call
```

#### Fix
```csharp
Consume(ref hp);                            // ok
Consume(in hp);                             // ok — 'in' accepts a ref local
ref var id = ref entity.Add<PlayerId>().Value;  // .Value — ushort, atomic
SetBehaviour(id);                           // ok — primitive isn't tracked
ref var st = ref entity.Ref<C>().Status;    // Status — enum
M(st);                                      // ok — enum isn't tracked
```

___

### FFSECS0020
**Category:** Correctness · **Severity:** Error · **CodeFix:** yes

A class implementing any StaticEcs marker interface (`IComponent`, `ITag`, `IEvent`, `ILinkType`, `ILinksType`, `IEntityType`, `IWorldType`) breaks generic dispatch — every public API in StaticEcs has a `where T : struct` constraint, and reflection-based `RegisterAll` would skip class types.

#### Pattern (will fire)
```csharp
public class Health : IComponent { public int Value; }   // FFSECS0020
```

#### Fix
```csharp
public struct Health : IComponent { public int Value; }  // ok
```

___

### FFSECS0021
**Category:** Correctness · **Severity:** Error · **CodeFix:** yes

Same as FFSECS0020 but specifically for `IMultiComponent`.

___

### FFSECS0022
**Category:** Correctness · **Severity:** Warning · **CodeFix:** —

A struct implementing `IMultiComponent` that is **not** `unmanaged` (contains managed fields like `string`, arrays, delegates, …) must override both `Write(ref BinaryPackWriter)` and `Read(ref BinaryPackReader)`. The interface's default implementations are no-ops — without overrides, snapshots silently produce empty data for the managed payload.

#### Pattern (will fire)
```csharp
public struct Inventory : IMultiComponent { public string Owner; }  // FFSECS0022 — no overrides
```

#### Fix
```csharp
public struct Inventory : IMultiComponent {
    public string Owner;
    public void Write(ref BinaryPackWriter w) { w.WriteString(Owner); }
    public void Read(ref BinaryPackReader r) { Owner = r.ReadString(); }
}
```

Unmanaged structs (`int`/`float`/`Nullable<int>` etc.) are bulk-copied by the storage and don't need overrides.

___

### FFSECS0030
**Category:** Correctness · **Severity:** Info · **CodeFix:** yes

A `ref T` parameter of a `Query.For` lambda that is never written marks the component as **changed** at runtime whenever change-tracking is enabled — even though the body only reads it. Use the `in T` overload to signal read-only intent and skip the change mark.

#### Pattern (will fire)
```csharp
W.Query().For((ref Health h) => { Console.WriteLine(h.Value); }); // FFSECS0030
```

#### Fix
```csharp
W.Query().For((in Health h) => { Console.WriteLine(h.Value); });
```

___

### FFSECS0031
**Category:** Performance · **Severity:** Error · **CodeFix:** —

A lambda passed to `Query.For` (or any fluent builder's `.For(...)`) that captures outer state (`this`, a method-local variable, an instance field/property/method) allocates a closure every time the query runs. Use one of the alternatives:

- `static` lambda + the `userData` overload (`For<TData>(userData, static (ref TData d, …) => …)`).
- A `struct` implementing `W.IQuery.Write<…>` / `W.IQuery.Read<…>`.
- `foreach (var entity in W.Query<…>().Entities())` + `ref var` locals.

#### Pattern (will fire)
```csharp
var multiplier = 2;
W.Query().For((ref Health h) => { h.Value *= multiplier; });    // FFSECS0031
```

#### Fix
```csharp
var multiplier = 2;
W.Query().For(multiplier, static (ref int m, ref Health h) => { h.Value *= m; });
```

Method-group references to non-static instance methods are also flagged (they capture `this`).

___

### FFSECS0032
**Category:** Usage · **Severity:** Info · **CodeFix:** yes

`Entity.IsMatch<TFilter>()` works for any `IQueryFilter`, but for simple shapes (`All<…>`, `Any<…>`, `None<…>`, their `*WithDisabled`/`*OnlyDisabled` siblings, `EntityIs<…>`, `EntityIsAny<…>`, `EntityIsNot<…>`) Entity has a shorter, intent-revealing direct method:

| Filter | Equivalent |
|---|---|
| `All<T..>` (arity 1-3) | `HasEnabled<T..>()` |
| `AllWithDisabled<T..>` | `Has<T..>()` |
| `AllOnlyDisabled<T..>` | `HasDisabled<T..>()` |
| `Any<T..>` (arity 2-3) | `HasEnabledAny<T..>()` |
| `AnyWithDisabled<T..>` | `HasAny<T..>()` |
| `AnyOnlyDisabled<T..>` | `HasDisabledAny<T..>()` |
| `None<T..>` | `!HasEnabled<…>` / `!HasEnabledAny<…>` |
| `NoneWithDisabled<T..>` | `!Has<…>` / `!HasAny<…>` |
| `EntityIs<T>` | `Is<T>()` |
| `EntityIsAny<T..>` | `IsAny<T..>()` |
| `EntityIsNot<T..>` | `IsNot<T..>()` |

#### Pattern (will fire)
```csharp
if (entity.IsMatch<All<Health, Mana>>())  { … }   // FFSECS0032
if (entity.IsMatch<None<Stunned>>())      { … }   // FFSECS0032
```

#### Fix
```csharp
if (entity.HasEnabled<Health, Mana>()) { … }
if (!entity.HasEnabled<Stunned>())     { … }
```

Constraint check: `HasEnabled`/`HasEnabledAny`/`HasDisabled`/`HasDisabledAny` all require `T : struct, IComponent, IDisableable`. For `All<…>`, `Any<…>`, `None<…>` (which accept `IComponentOrTag` — tags allowed) the diagnostic only fires when every type argument is both `IComponent` and `IDisableable`; otherwise the naive replacement would not compile and is silently skipped. `*OnlyDisabled` filters already carry the same constraints, so the check is automatic for them.

Composite filters (`And<…>`, `Or<…>`, `Nothing`) and arity > 3 are not suggested — `IsMatch` is the only practical entry point there.

___

### FFSECS0040
**Category:** Correctness · **Severity:** Error · **CodeFix:** —

`ref`/`in` references to a component become stale after the underlying entity is invalidated. Three patterns are tracked:

- **Lambda in `WorldQuery.For`** — the references are the lambda's `ref`/`in` component parameters.
- **`struct` implementing `IQuery.*`** — the references are the `ref`/`in` parameters of the `Invoke` method.
- **`ref`-locals from `entity.Ref/Mut/Read/Add(...)`**.

Invalidators: `Destroy`, `MoveTo`, `Unload` (full kill), `Delete<T>` (only references to a component of type `T`).

#### Pattern (will fire)
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    e.Destroy();
    hp.Value = 0;                       // FFSECS0040 — hp points into freed storage
});
```

#### Fix
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    var snap = hp.Value;                // copy first
    e.Destroy();
    Use(snap);                          // ok
});
```

___

### FFSECS0041
**Category:** Correctness · **Severity:** Error · **CodeFix:** —

Counterpart to FFSECS0040 but tracks the **entity variable itself**, not the `ref`/`in` references to its components. After `Destroy`/`MoveTo`/`Unload` on a local or parameter, any further operation on that variable (`Has`, `Add`, `IsActual`, …) is flagged. The only allowed operations on the tainted variable are:

- Direct reassignment (`entity = W.NewEntity<…>();`).
- Out-parameter rebind (`Method(out entity);` or `Method(out var entity)` inside a loop).

#### Pattern (will fire)
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
_ = e.Has<Health>();                    // FFSECS0041
```

#### Fix
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
e = W.NewEntity<Default>();             // reassignment kills the taint
_ = e.Has<Health>();                    // ok
```

The merge across conditional branches is conservative — if any predecessor path leaves the variable invalidated, the merge point is tainted.

___

### FFSECS0050
**Category:** Correctness · **Severity:** Error · **CodeFix:** —

A component is referenced more than once inside the same query — either as a duplicate inside same-kind filters (`All`+`All`, `None`+`None`, `Any`+`Any`, including their `*WithDisabled`/`*OnlyDisabled` variants), or as an overlap between the filter chain and a lambda `ref`/`in` parameter, or with an `IQuery` struct's component generic.

#### Pattern (will fire)
```csharp
foreach (var _ in W.Query<All<Health>, All<Health>>().Entities()) { }                       // FFSECS0050
W.Query<All<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0050 — filter ↔ lambda
W.Query<All<Health>>().Write<Health>().For<MyWriteFn>();                                    // FFSECS0050 — filter ↔ IQuery generic
foreach (var _ in W.Query<All<Health>, AllOnlyDisabled<Health>>().Entities()) { }           // FFSECS0050 — base + disabled variant
```

___

### FFSECS0051
**Category:** Correctness · **Severity:** Error · **CodeFix:** —

The query has the same component in both an `All<…>` and a `None<…>` — the resulting set is always empty. The implicit `All` contribution of lambda parameters and `IQuery` struct generics also counts.

#### Pattern (will fire)
```csharp
foreach (var _ in W.Query<All<Health>, None<Health>>().Entities()) { }                       // FFSECS0051
W.Query<None<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0051 — lambda implies All
```

___

## Suppressing diagnostics

Per-line / per-block:
```csharp
#pragma warning disable FFSECS0011
var snap = entity.Read<Health>();
#pragma warning restore FFSECS0011
```

Per-project (`.editorconfig`):
```ini
[*.cs]
dotnet_diagnostic.FFSECS0011.severity = none
```

Per-build (`csproj`):
```xml
<NoWarn>FFSECS0011</NoWarn>
```

___

## Source code

All analyzers live under `StaticEcs/Analyzers~/Src/Analyzers/*.cs`; code fixes under `StaticEcs/Analyzers~/CodeFixes/`. Rule IDs are centralised in `StaticEcs/Analyzers~/Shared/FFSECSIds.cs`.

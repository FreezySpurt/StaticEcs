---
title: Common Pitfalls
parent: EN
nav_order: 4
---

# Common Pitfalls

A list of frequent mistakes when using StaticEcs. Useful for both developers and AI coding assistants.

___

## Lifecycle Errors

### Forgetting type registration
ALL component, tag, event, link, and multi-component types MUST be registered between `W.Create()` and `W.Initialize()`. Using an unregistered type causes a runtime error.
```csharp
// WRONG: component not registered
W.Create(WorldConfig.Default());
W.Initialize();
var e = W.NewEntity<Position>(0); // RuntimeError!

// CORRECT — manual registration
W.Create(WorldConfig.Default());
W.Types().Component<Position>();
W.Initialize();
var e = W.NewEntity<Position>(0); // OK

// CORRECT — auto-registration of all types from the assembly
W.Create(WorldConfig.Default());
W.Types().RegisterAll();
W.Initialize();
```

### `RegisterAll()` and multi-assembly projects / Unity IL2CPP / WebGL / NativeAOT

`W.Types().RegisterAll()` without arguments scans **exactly one assembly** — the one that declares your `IWorldType` marker (`typeof(TWorld).Assembly`). It does **not** walk the stack and does **not** enumerate loaded assemblies, which means:

- **It is safe on all runtimes**, including Unity IL2CPP, Unity WebGL and NativeAOT, where stack walking (`Assembly.GetCallingAssembly`) returns unreliable results.
- **It will miss ECS types defined in other assemblies.** A common mistake is to keep your `TWorld` marker struct in a "core" / "shared" assembly and your components in a gameplay assembly — the parameterless call then registers nothing.

```csharp
// WRONG — MyWorld lives in Game.Core.dll, components live in Game.Gameplay.dll.
// Only Game.Core.dll is scanned, so no components get registered.
W.Types().RegisterAll();

// CORRECT — list every assembly that contains ECS types.
W.Types().RegisterAll(
    typeof(MyWorld).Assembly,
    typeof(Position).Assembly,
    typeof(AiPlugin).Assembly
);
```

If in doubt, place your `TWorld` marker in the same assembly as your components and use the parameterless form.

### Entity operations before Initialize
`NewEntity`, queries, and all entity operations only work after `W.Initialize()`. Calling them during the `Created` phase (between `Create` and `Initialize`) will fail.

### Calling Create twice
Calling `W.Create()` without `W.Destroy()` first is an error. The world must be destroyed before re-creating.

___

## Entity Handle Errors

### Using Entity after Destroy
`Entity` is a 4-byte uint slot handle with no generation counter. After `Destroy()`, the slot is immediately available for reuse. The old handle now silently points to a completely different entity — or to garbage.
```csharp
var entity = W.NewEntity<Position>(0);
entity.Destroy();
// entity is now INVALID — any use is undefined behavior
entity.Ref<Position>(); // DANGER: may access a different entity's data
```

### Storing Entity across frames
Since Entity has no generation counter, it cannot detect staleness. Never store `Entity` in fields, lists, or other persistent structures. Use `EntityGID` instead.
```csharp
// WRONG
class MySystem { Entity targetEntity; } // Stale after target is destroyed

// CORRECT
class MySystem { EntityGID targetGid; } // Safe — version check detects staleness
// Usage:
if (targetGid.TryUnpack<WT>(out var entity)) {
    // entity is valid and alive
}
```

### Comparing Entity for identity
`Entity` equality is by IdWithOffset (uint) only. Two entities created at different times in the same slot have the same Entity value. Use `EntityGID` for identity comparison.

___

## Component Errors

### Add vs Set semantics
`Add<T>()` without a value is **idempotent** — if the component already exists, it returns a ref to the existing data with NO hooks called. This is NOT an overwrite.

`Set(value)` **always overwrites** — calls OnDelete on old value, overwrites data, calls OnAdd on new value.

```csharp
entity.Set(new Position { Value = Vector3.Zero }); // Sets position
entity.Add<Position>(); // Does NOTHING — returns ref to existing {0,0,0}
entity.Set(new Position { Value = Vector3.One }); // Overwrites: OnDelete(old) → set → OnAdd(new)
```

### Implementing empty hook methods
`ComponentTypeInfo<T>` uses reflection at startup to detect which hooks are implemented. If any hook has a non-empty body, hook dispatch is enabled for ALL instances of that component type. Don't override hooks with empty bodies.
```csharp
// WRONG: empty hook body still causes hook dispatch overhead
public struct Foo : IComponent {
    public void OnAdd<TW>(World<TW>.Entity self) where TW : struct, IWorldType { }
}

// CORRECT: don't implement hooks you don't need (default interface methods are already empty)
public struct Foo : IComponent { }
```

### HasOnDelete vs DataLifecycle
OnDelete hook and DataLifecycle (reset to `DefaultValue`) are mutually exclusive cleanup paths. If a component has an OnDelete hook, the hook handles cleanup — the data is NOT reset. DataLifecycle reset only applies to components without OnDelete. When `noDataLifecycle: true` in config, no initialization or cleanup is performed by the framework.

### Disable/Enable on a non-`IDisableable` component
`Entity.Disable<T>()` / `Enable<T>()` / `HasDisabled<T>()` / `HasEnabled<T>()` and the `*Disabled` query filters all constrain `T : struct, IComponent, IDisableable`. Calling them on a component without the marker is a **compile-time error**, not a runtime assert. If your code used to compile in 2.1.x and now fails — add `IDisableable` to the affected component's declaration. See [migration to 2.2.0](migrationguide.md).

___

## Query Errors

### Iteration snapshot vs other entities
Strict / Flexible restrictions apply only to **other entities that belong to the iteration snapshot** — the bitmask of entities matching the filter at the moment iteration starts. Entities outside the snapshot are **not blocked**: they can be freely created, configured, mutated, or destroyed inside the loop. This includes:
- entities created during iteration (always outside the snapshot, since the snapshot was fixed before they existed),
- entities that did not pass the filter (different components, wrong entity type, etc.).

```csharp
// OK in Strict — fresh entity is not in the snapshot
foreach (var e in W.Query<All<Position>>().Entities()) {
    var fresh = W.NewEntity<Default>();
    fresh.Add<Position>();
    fresh.Set(new Velocity { ... });
}

// OK in Strict — `unrelated` does not match `All<Position>`
foreach (var e in W.Query<All<Position>>().Entities()) {
    unrelated.Add<Tag>(); // no Position, not in snapshot
}
```

### Removing the snapshot match from a non-current entity
Strict's assert (and Flexible's — Flexible does NOT lift this restriction) is precise. It fires only on operations that could remove the cached snapshot match from a non-current entity. Per filter type `T`:

| Filter               | Blocked on non-current snapshot entity |
|----------------------|----------------------------------------|
| `All<T>`             | `Delete<T>`, `Disable<T>`              |
| `AllOnlyDisabled<T>` | `Delete<T>`, `Enable<T>`               |
| `AllWithDisabled<T>` | `Delete<T>`                            |
| `None<T>`            | `Add<T>`, `Set<T>`, `Enable<T>`        |

Operations on **types not in the filter** are not blocked. Operations on entities **outside the snapshot** (created mid-iteration, or whose bit was 0 in the cached mask) are not blocked. The current entity may be mutated freely.

```csharp
// WRONG — Position is in the filter, otherEntity is in the snapshot:
W.Query<All<Position>>().For((W.Entity e) => {
    otherEntity.Delete<Position>(); // asserts in DEBUG
});
W.Query<All<Position>>().For((W.Entity e) => {
    otherEntity.Delete<Position>(); // also asserts in Flexible
}, queryMode: QueryMode.Flexible);

// CORRECT — mark with a tag during the loop, batch-delete in a single pass after:
W.Query<All<Position>>().For((W.Entity e) => {
    if (ShouldStrip(otherEntity)) otherEntity.Set<Marked>(); // Marked isn't in the filter — never blocked
});
W.Query<All<Position, Marked>>().BatchDelete<Position, Marked>();

// CORRECT — mutating a type that isn't in the filter is allowed:
W.Query<All<Position>>().For((W.Entity e) => {
    otherEntity.Delete<Velocity>(); // OK: Velocity not in the filter, no blocker
});

// CORRECT — mutating an entity outside the snapshot (newly created, or non-matching) is allowed:
W.Query<All<Position>>().For((W.Entity e) => {
    var fresh = W.NewEntity<Default>();   // outside the snapshot by definition
    fresh.Set(new Position { ... });      // OK
});
```

### Entity-level operations on other snapshot entities — Flexible only
Destroying, disabling, or enabling **another snapshot** entity during iteration is forbidden in Strict (asserts in DEBUG) but allowed in Flexible, where the cached bitmask is updated so the affected entity is skipped for the rest of the loop.
```csharp
// WRONG in Strict:
foreach (var e in W.Query<All<Position>>().Entities()) {
    otherEntity.Destroy(); // asserts in DEBUG (otherEntity is in the snapshot)
}

// CORRECT in Flexible:
foreach (var e in W.Query<All<Position>>().EntitiesFlexible()) {
    otherEntity.Destroy();  // OK — excluded from the remaining iteration
    otherEntity.Disable();  // OK
    otherEntity.Enable();   // OK
}
```

### Parallel iteration constraints
During `ForParallel`, only modify the CURRENT entity's data. Do not create/destroy entities, modify other entities, or perform structural changes.

### Unnecessary Flexible mode
Flexible mode re-reads the cached bitmask on every step, making it slower than Strict. Use Flexible only when you actually need to `Destroy` / `Disable` / `Enable` other snapshot entities during iteration — that is the only extra freedom it provides. Creating new entities inside the loop and configuring them does NOT require Flexible: new entities are not part of the snapshot in either mode.

### Duplicating delegate components in the `Query<>` filter
The `For<T0, ...>` overloads on `WorldQuery<TFilter>` automatically add the components from the delegate signature (`ref T0`, `in T0`) to the iteration filter. Listing them again inside `All<>` is wrong — it duplicates the type and is a clear sign you are fighting the API:
```csharp
// WRONG — Position and Velocity repeated in All<>
W.Query<All<Position, Velocity>>().For(static (ref Position p, ref Velocity v) => { ... });

// CORRECT — components in the delegate form the filter on their own
W.Query().For(static (ref Position p, ref Velocity v) => { ... });

// CORRECT — Query<> only carries extra filters (tags, None, EntityIs, etc.)
W.Query<None<Stunned>>().For(static (W.Entity e, ref Position p, ref Velocity v) => { ... });

// CORRECT — entity-only delegate: no component in the signature,
// so the filter must live in Query<All<...>>
W.Query<All<Position>>().For(static (W.Entity e) => { ... });
```
___

## Registration Errors

### MultiComponent without Multi wrapper
`IMultiComponent` types must be registered via `W.Types().Multi<Item>()`, not as regular components. They are stored internally as `Multi<Item>` which is the actual component.

### Missing serialization setup
Serialization requires:
1. FFS.StaticPack dependency
2. All types get auto-computed GUIDs. Override via `new ComponentTypeConfig<T>(guid: ...)` for stability across type renames
3. Non-unmanaged components need `Write`/`Read` hook implementations
4. Serialization strategy is auto-detected (`UnmanagedPackArrayStrategy<T>` for unmanaged types, `StructPackArrayStrategy<T>` otherwise)

___

## Resource Errors

### NamedResource caching issue
`NamedResource<T>` caches its internal box reference on first access. If stored as `readonly` or passed by value after first use, the cache copy becomes stale.
```csharp
// WRONG
readonly NamedResource<Config> config = new("main"); // readonly breaks cache

// CORRECT
NamedResource<Config> config = new("main"); // mutable — cache works
```

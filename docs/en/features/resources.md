---
title: Resources
parent: Features
nav_order: 11
---

## Resources
Resources are an alternative to DI — a simple mechanism for storing and passing user data and services to systems and other methods
- Resources are world-level singletons: shared state that doesn't belong to any specific entity
- Ideal for configuration, time/delta-time, input state, asset caches, service references
- Two variants: **singleton** (one per type) and **named** (multiple per type, distinguished by string key)
- Available in both `Created` and `Initialized` world phases
- Every resource type **must** implement the marker interface `IResource`

___

## Singleton Resources

A singleton resource stores exactly one instance of a given type per world.
Internally uses static generic storage — access is O(1) with zero dictionary overhead.

#### Setting a resource:
```csharp
// User classes and services — must implement IResource
public class GameConfig : IResource { public float Gravity; }
public class InputState : IResource { public Vector2 MousePos; }

// Set a resource in the world
// By default clearOnDestroy = true — the resource will be automatically cleared on World.Destroy()
W.SetResource(new GameConfig { Gravity = 9.81f });
W.SetResource(new InputState(), clearOnDestroy: false); // persists across world re-creation

// If SetResource is called again for the same type, the value is replaced without error
W.SetResource(new GameConfig { Gravity = 4.0f }); // overwrites the previous value
```

{: .important }
The `clearOnDestroy` parameter is only applied on the first registration. Replacing an existing resource preserves the original `clearOnDestroy` setting.

#### Basic operations:
```csharp
// Check if a resource of the given type is registered
bool has = W.HasResource<GameConfig>();

// Get a mutable ref to the resource value — modifications are written directly to storage
ref var config = ref W.GetResource<GameConfig>();
config.Gravity = 11.0f; // modified in-place, no setter call needed

// Remove the resource from the world
W.RemoveResource<GameConfig>();

// Resource<T> — zero-cost readonly struct handle for frequent access (no initialization needed)
W.Resource<GameConfig> configHandle;
bool registered = configHandle.IsRegistered;
ref var cfg = ref configHandle.Value;
configHandle.Set(new GameConfig { Gravity = 9.81f }); // register / replace via the handle
configHandle.Remove();                                 // remove via the handle
```

___

## Named Resources

Named resources allow multiple instances of the same type, distinguished by string keys.
Internally stored in a `Dictionary<string, object>` with type-safe `Box<T>` wrappers.

#### Setting a named resource:
```csharp
// Set named resources of the same type under different keys
W.SetResource("player_config", new GameConfig { Gravity = 9.81f });
W.SetResource("moon_config", new GameConfig { Gravity = 1.62f });

// If SetResource is called again for an existing key, the value is replaced without error
W.SetResource("player_config", new GameConfig { Gravity = 10.0f }); // overwrites
```

#### Basic operations:
```csharp
// Check if a named resource with the given key exists
bool has = W.HasResource<GameConfig>("player_config");

// Get a mutable ref to the named resource value
ref var config = ref W.GetResource<GameConfig>("player_config");
config.Gravity = 5.0f;

// Remove a named resource by key
W.RemoveResource("player_config");

// NamedResource<T> — struct handle that caches the internal reference after the first access
// Create a handle bound to a key (does not register the resource)
var moonConfig = new W.NamedResource<GameConfig>("moon_config");
bool registered = moonConfig.IsRegistered;  // always performs dictionary lookup, not cached
ref var cfg = ref moonConfig.Value;          // first call resolves from dictionary and caches; subsequent calls are O(1)
moonConfig.Set(new GameConfig { Gravity = 1.62f }); // register / replace under the bound key
moonConfig.Remove();                                // remove the bound key (also drops the cache)
// The cache is automatically invalidated when the resource is removed or the world is destroyed
```

{: .warning }
`NamedResource<T>` is a mutable struct that caches an internal reference on first `Value` access.
Do **not** store it in a `readonly` field or pass by value after first use — the C# compiler
will create a defensive copy, discarding the cache and causing a dictionary lookup on every access.
Store it in a non-readonly field or local variable.

___

## Lifecycle

```csharp
W.Create(WorldConfig.Default());

// Resources can be set after Create (no need to wait for Initialize)
W.SetResource(new GameConfig { Gravity = 9.81f });
W.SetResource("debug_flags", new DebugFlags(), clearOnDestroy: false);

W.Initialize();

// Resources remain available during the Initialized phase
ref var config = ref W.GetResource<GameConfig>();

// On Destroy: resources with clearOnDestroy=true are cleared automatically
// Resources with clearOnDestroy=false persist and remain available after the next Create+Initialize cycle
W.Destroy();
```

___

## Systems-scoped resources

Both `Resource<T>` and `NamedResource<T>` also exist at the systems-pipeline level. Each `World<TWorld>.Systems<TSystemsType>` has its own independent resource storage, isolated from the world's resources and from other systems groups. The lifecycle of these resources is bound to the systems pipeline: they are cleared on `Systems<TSystemsType>.Destroy()`, not on `World<TWorld>.Destroy()`.

Use them when a piece of state belongs to a specific system group (e.g., a fixed-step accumulator for `FixedSys`, a render-only frame buffer for `RenderSys`) and should not leak into world-level resources or into other pipelines.

#### Public API

The same method set as on the world is mirrored on `Systems<TSystemsType>` — only the storage scope differs:

```csharp
public struct FixedSystems : ISystemsType { }
public abstract class FixedSys : W.Systems<FixedSystems> { }

public struct FixedTime : IResource { public float Accumulator; public float Step; }

// Singleton resource scoped to FixedSys
FixedSys.SetResource(new FixedTime { Step = 1f / 60f });
ref var time = ref FixedSys.GetResource<FixedTime>();
bool has = FixedSys.HasResource<FixedTime>();
FixedSys.RemoveResource<FixedTime>();

// Keyed resource scoped to FixedSys
FixedSys.SetResource("solver_a", new SolverState());
ref var solver = ref FixedSys.GetResource<SolverState>("solver_a");
FixedSys.RemoveResource("solver_a");
```

#### Handle structs

`World<TWorld>.Systems<TSystemsType>.Resource<T>` and `World<TWorld>.Systems<TSystemsType>.NamedResource<T>` mirror the world-level handles and access the systems-scoped storage directly:

```csharp
public struct PhysicsSystem : ISystem {
    private FixedSys.Resource<FixedTime> _time;
    private FixedSys.NamedResource<SolverState> _solver = new("solver_a");

    public void Update() {
        ref var time = ref _time.Value;          // zero-cost handle, no lookup
        ref var solver = ref _solver.Value;      // dictionary lookup on first access, then cached
        // ...
    }
}
```

Both handle types also expose `Set(value, clearOnDestroy)` and `Remove()` — the same registration/removal API as on the world or systems pipeline, but invoked directly on the handle (the resource type / key are taken from the handle itself).

The same `NamedResource<T>` caching warning applies: do not store these handles in `readonly` fields or pass them by value after the first `Value` access.

#### Lifecycle

```csharp
FixedSys.Create();

// Resources can be set immediately after Create
FixedSys.SetResource(new FixedTime { Step = 1f / 60f });

FixedSys.Add(new PhysicsSystem());
FixedSys.Initialize();

// ... game loop ...

// On Destroy: all FixedSys resources with clearOnDestroy=true are cleared,
// independently of W.Destroy()
FixedSys.Destroy();
```

Different `ISystemsType` types (e.g., `FixedSys` vs `RenderSys`) keep their resource stores fully independent; the same goes for world-level resources vs any systems-scoped pipeline.

___

## Snapshot serialization

`IResource` has four optional default-implemented methods. Override `Guid()` to opt the resource into automatic snapshot serialization:

```csharp
public interface IResource {
    public Guid? Guid()                                              => null;  // null → not serialized
    public byte  Version()                                            => 0;
    public void  Write(ref BinaryPackWriter writer)                   {}
    public void  Read(ref BinaryPackReader reader, byte version)      {}
}
```

- **Unmanaged struct (no references)**: `Write`/`Read` are not required — the framework uses raw memory copy via `Unsafe`.
- **Non-unmanaged** type: both `Write` and `Read` are required — `SetResource` throws `StaticEcsException` otherwise.
- Same rules apply to both world-scope and `Systems<TSystemsType>`-scope resources, singleton and named alike.

Full serialization details (format selection, version migration, standalone `CreateResourcesSnapshot` / `LoadResourcesSnapshot` API): see [Serialization → Resources serialization](./serialization.md).


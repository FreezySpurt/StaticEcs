---
title: Serialization
parent: Features
nav_order: 15
---

## Serialization
Serialization is a mechanism for creating binary snapshots of the entire world or individual entities, clusters, and chunks.
Binary serialization uses [StaticPack](https://github.com/Felid-Force-Studios/StaticPack).

___

## Configuring components

To support component serialization:
1. Specify a `Guid` during registration (stable type identifier)
2. Implement `Write` and `Read` hooks on the component

{: .important }
`Write` and `Read` hooks are **required** for `EntitiesSnapshot` serialization (for all component types, including unmanaged). For world/cluster/chunk snapshots, non-unmanaged types also always use these hooks.

#### Unmanaged component:
```csharp
public struct Position : IComponent, IComponentConfig<Position> {
    public float X, Y, Z;

    public ComponentTypeConfig<Position> Config() => new(
        guid: new Guid("b121594c-456e-4712-9b64-b75dbb37e611")
    );

    public void Write<TWorld>(ref BinaryPackWriter writer, World<TWorld>.Entity self)
        where TWorld : struct, IWorldType {
        writer.WriteFloat(X);
        writer.WriteFloat(Y);
        writer.WriteFloat(Z);
    }

    public void Read<TWorld>(ref BinaryPackReader reader, World<TWorld>.Entity self, byte version, bool disabled)
        where TWorld : struct, IWorldType {
        X = reader.ReadFloat();
        Y = reader.ReadFloat();
        Z = reader.ReadFloat();
    }
}

W.Types().Component<Position>();
```

#### Non-unmanaged component (contains reference fields):
```csharp
public struct Name : IComponent, IComponentConfig<Name> {
    public string Value;

    public ComponentTypeConfig<Name> Config() => new(
        guid: new Guid("531dc870-fdf5-4a8d-a4c6-b4911b1ea1c3")
    );

    public void Write<TWorld>(ref BinaryPackWriter writer, World<TWorld>.Entity self)
        where TWorld : struct, IWorldType {
        writer.WriteString16(Value);
    }

    public void Read<TWorld>(ref BinaryPackReader reader, World<TWorld>.Entity self, byte version, bool disabled)
        where TWorld : struct, IWorldType {
        Value = reader.ReadString16();
    }
}

W.Types().Component<Name>();
```

#### Bulk memory copying for unmanaged types:

For world/cluster/chunk snapshots, unmanaged components are automatically serialized as a memory block instead of per-component `Write`/`Read` calls.

{: .note }
`UnmanagedPackArrayStrategy<T>` performs direct memory copying — significantly faster than per-component serialization. Works only for unmanaged types. On version mismatch (data migration), the system automatically falls back to `Read` hooks. The default strategy is auto-detected: `UnmanagedPackArrayStrategy<T>` for unmanaged types, `StructPackArrayStrategy<T>` otherwise.

#### Bulk segment serialization for Multi and Links:

Multi-components and Links store their values in shared segment storage. Bulk segment serialization strategies are applied automatically for unmanaged value types. To override the GUID or other config, implement the corresponding config interface on the type:

```csharp
// Multi-component with custom config
public struct Item : IMultiComponent, IMultiComponentConfig<Item> {
    public int Id;

    public ComponentTypeConfig<W.Multi<Item>> Config<TWorld>()
        where TWorld : struct, IWorldType => new(
        guid: new Guid("...")
    );

    public IPackArrayStrategy<Item> ElementPackStrategy()
        => new UnmanagedPackArrayStrategy<Item>();
}

W.Types().Multi<Item>();

// Links with custom config
public struct MyLinkType : ILinksType, ILinksConfig<MyLinkType> {
    public ComponentTypeConfig<W.Links<MyLinkType>> Config<TWorld>()
        where TWorld : struct, IWorldType => new(
        guid: new Guid("...")
    );
}

W.Types().Links<MyLinkType>();
```

#### Full configuration:
```csharp
public struct Position : IComponent, IComponentConfig<Position> {
    public float X, Y, Z;

    public ComponentTypeConfig<Position> Config() => new(
        guid: new Guid("b121594c-456e-4712-9b64-b75dbb37e611"),
        version: 1,                  // data schema version for migration (default — 0)
        noDataLifecycle: true        // disable framework data management (default — false)
        // serialization strategy is auto-detected: UnmanagedPackArrayStrategy<T> for unmanaged, StructPackArrayStrategy<T> otherwise
    );

    // ... Write/Read hooks ...
}

W.Types().Component<Position>();
```

___

## Configuring tags

Tags are configured by implementing `ITagConfig<T>`:

```csharp
public struct IsPlayer : ITag, ITagConfig<IsPlayer> {
    public TagTypeConfig<IsPlayer> Config() => new(
        guid: new Guid("3a6fe6a2-9427-43ae-9b4a-f8582e3a5f90")
    );
}

public struct IsDead : ITag, ITagConfig<IsDead> {
    public TagTypeConfig<IsDead> Config() => new(
        guid: new Guid("d25b7a08-cbe6-4c77-bd8e-29ce7f748c30")
    );
}

W.Types()
    .Tag<IsPlayer>()
    .Tag<IsDead>();
```

#### Full configuration:
```csharp
public struct Poisoned : ITag, ITagConfig<Poisoned>,
                         ITrackableAdded, ITrackableDeleted {
    public TagTypeConfig<Poisoned> Config() => new(
        guid: new Guid("A1B2C3D4-...") // stable identifier for serialization (default — auto-computed from type name)
    );
}

W.Types().Tag<Poisoned>();
```

Change tracking is enabled by implementing marker interfaces (`ITrackableAdded`, `ITrackableDeleted`) on the type itself — see [Change Tracking](tracking).

{: .note }
All types automatically get a stable GUID computed from the type name. To override, implement `ITagConfig<T>` on the tag struct with a custom guid.

___

## Configuring events

Events are configured by implementing `IEventConfig<T>` — similar to components:

```csharp
public struct OnDamage : IEvent, IEventConfig<OnDamage> {
    public float Amount;

    public EventTypeConfig<OnDamage> Config() => new(
        guid: new Guid("a1b2c3d4-e5f6-7890-abcd-ef1234567890")
    );

    public void Write(ref BinaryPackWriter writer) {
        writer.WriteFloat(Amount);
    }

    public void Read(ref BinaryPackReader reader, byte version) {
        Amount = reader.ReadFloat();
    }
}

W.Types().Event<OnDamage>();
```

___

## Excluding types from serialization

Implementing the marker interface `INonSerializable` on a type excludes its data from **every** snapshot the world produces (`WorldSnapshot`, `ChunkSnapshot`, `ClusterSnapshot`, `EntitiesSnapshot`). Applicable to any type registered as a component, tag, event, link, multi-link or multi-component.

```csharp
// Component excluded from snapshots
public struct CachedTransform : IComponent, INonSerializable {
    public Matrix4x4 Value;
}

// Tag excluded from snapshots
public struct IsHovered : ITag, INonSerializable { }

// Event excluded from snapshots
public struct OnTick : IEvent, INonSerializable { }

// Link/Links/Multi: marker is placed on the user's link/value type,
// not on Link<T> / Links<T> / Multi<T>
public struct VolatileTarget : ILinkType, INonSerializable {
    public void OnAdd<TW>(World<TW>.Entity self, EntityGID value) where TW : struct, IWorldType { }
    public void OnDelete<TW>(World<TW>.Entity self, EntityGID value, HookReason reason) where TW : struct, IWorldType { }
    public void CopyTo<TW>(World<TW>.Entity self, World<TW>.Entity other, EntityGID value) where TW : struct, IWorldType { }
}
```

The marker affects **only serialization**. All runtime behavior — `Add`/`Set`/`Has`/`Query`, lifecycle hooks (`OnAdd`/`OnDelete`/`CopyTo`), enable/disable, `Destroy`/`Unload`, change tracking, type-erased handles — works identically to a regular type. The type still has a `Guid`, `DynamicId`, a pool, bitmasks, etc.

What the marker does on the **write** path:
- The type's pool is skipped when writing chunks (`WorldSnapshot` / `ChunkSnapshot` / `ClusterSnapshot`) — neither the per-pool header nor the per-entity components belonging to that type are emitted.
- The type's events are skipped when writing the event ring buffer.
- The per-entity component bitmask written into `EntitiesSnapshot` excludes bits for non-serializable pools.
- Resulting snapshot size shrinks by exactly the bytes that would otherwise belong to the excluded type.

What the marker does on the **read** path: nothing. If a snapshot already contains data for a type that is now marked `INonSerializable`, the reader still restores that data via the standard GUID lookup. This keeps older snapshots loadable. Use a delete migrator (see *Migration of removed types*) only if the type is **also** unregistered.

The flag is also visible at runtime on the type-erased handles — `ComponentsHandle.NonSerializable` and `EventsHandle.NonSerializable` mirror `INonSerializable` on the registered type, which is useful for custom tooling, debug dumps and custom serializers.

___

## World Snapshot

Saves the full world state: all entities, components, tags, events, and change tracking state.

{: .important }
**World snapshots persist the tick and the entire tracking history.** The saved stream includes `CurrentTick`, `CurrentLastTick`, and all `TrackingBufferSize + 1` history slots — for `AllAdded<T>` / `AllChanged<T>` / `AllDeleted<T>` filters, per-entity `HasAdded/HasChanged/HasDeleted` methods, and world-level `HasCreated` tracking. After loading, tick-based tracking queries (including those using `fromTick`) return the same results as before saving.

{: .important }
**Configuration must match when loading.** The `TrackingBufferSize` and `TrackCreated` values of the target world must equal those saved in the snapshot. Any mismatch throws `StaticEcsException`. This is a property of the `WorldConfig` passed during world creation — changing it between save and load is not supported.

{: .note }
Each snapshot begins with a 2-byte format version header (`FormatVersion = 2`) and an 8-byte snapshot size. Loading a snapshot produced by an incompatible version throws `StaticEcsException` with a clear message.

#### Saving and loading after initialization:
```csharp
byte[] worldSnapshot = W.Serializer.CreateWorldSnapshot();
W.Destroy();

CreateWorld();
W.Initialize();
// All existing entities and events are removed before loading
W.Serializer.LoadWorldSnapshot(worldSnapshot);
```

#### Additional parameters:
```csharp
// Save to file
W.Serializer.CreateWorldSnapshot("path/to/world.bin");

// With GZIP compression
byte[] compressed = W.Serializer.CreateWorldSnapshot(gzip: true);

// Filter by clusters
W.Serializer.CreateWorldSnapshot(clusters: new ushort[] { 0, 1 });

// Chunk writing strategy
W.Serializer.CreateWorldSnapshot(strategy: ChunkWritingStrategy.SelfOwner);

// Without events
W.Serializer.CreateWorldSnapshot(writeEvents: false);

// Without custom data
W.Serializer.CreateWorldSnapshot(withCustomSnapshotData: false);

// Load from file (gzip is autodetected)
W.Serializer.LoadWorldSnapshot("path/to/world.bin");

// Load compressed data (gzip is autodetected)
W.Serializer.LoadWorldSnapshot(compressed);
```

{: .important }
All components and tags are automatically assigned a stable `Guid` computed from the type name. You can override the `Guid` via config to ensure stability across type renames.

___

## Entities Snapshot

Allows saving and loading individual entities with granular control.

#### Saving entities:
```csharp
// Create an entity writer
using var writer = W.Serializer.CreateEntitiesSnapshotWriter();

// Write specific entities
foreach (var entity in W.Query().Entities()) {
    writer.Write(entity);
}

// Or write all entities at once
// writer.WriteAllEntities();

// Create the snapshot
byte[] snapshot = writer.CreateSnapshot();

// Or save to file
// writer.CreateSnapshot("path/to/entities.bin");
```

#### Writing with simultaneous unloading:
```csharp
using var writer = W.Serializer.CreateEntitiesSnapshotWriter();

// Write and unload — saves memory during streaming
foreach (var entity in W.Query().Entities()) {
    writer.WriteAndUnload(entity);
}

// Or all entities at once
// writer.WriteAndUnloadAllEntities();

byte[] snapshot = writer.CreateSnapshot();
```

___

#### Loading entities (entitiesAsNew):

The `entitiesAsNew` parameter determines how entities are loaded:

- **`entitiesAsNew: false`** (default) — entities are restored to **the same slots** (same EntityGID). If a slot is already occupied — error in DEBUG.
- **`entitiesAsNew: true`** — entities are loaded into **new slots** with new EntityGIDs. Links between entities (Link, Links) may point to incorrect entities.

```csharp
// Load into original slots
W.Serializer.LoadEntitiesSnapshot(snapshot, entitiesAsNew: false);

// Load as new entities
W.Serializer.LoadEntitiesSnapshot(snapshot, entitiesAsNew: true);

// With a callback for each loaded entity
W.Serializer.LoadEntitiesSnapshot(snapshot, entitiesAsNew: true, onLoad: entity => {
    Console.WriteLine($"Loaded: {entity.PrettyString}");
});
```

___

#### Preserving links between entities (GID Store):

To correctly load entities with `entitiesAsNew: false`, save the global identifier store:

```csharp
// 1. Save entities and GID Store
using var writer = W.Serializer.CreateEntitiesSnapshotWriter();
writer.WriteAllEntities();
byte[] entitiesSnapshot = writer.CreateSnapshot();
byte[] gidSnapshot = W.Serializer.CreateGIDStoreSnapshot();
W.Destroy();

// 2. Restore world with GID Store
CreateWorld();
W.Initialize();
W.Serializer.RestoreFromGIDStoreSnapshot(gidSnapshot);

// New entities won't occupy saved entity slots
var newEntity = W.NewEntity<Default>();
newEntity.Set(new Position { X = 1 });

// 3. Load entities into original slots — all links are correct
W.Serializer.LoadEntitiesSnapshot(entitiesSnapshot, entitiesAsNew: false);
```

{: .note }
The GID Store contains information about all issued identifiers. This guarantees that new entities won't occupy slots of unloaded entities, and all links (Link, Links, EntityGID in data) remain correct.

___

## GID Store

```csharp
// Save GID Store
byte[] gidSnapshot = W.Serializer.CreateGIDStoreSnapshot();

// With GZIP compression
byte[] gidCompressed = W.Serializer.CreateGIDStoreSnapshot(gzip: true);

// To file
W.Serializer.CreateGIDStoreSnapshot("path/to/gid.bin");

// With chunk writing strategy
W.Serializer.CreateGIDStoreSnapshot(strategy: ChunkWritingStrategy.SelfOwner);

// Filter by clusters
W.Serializer.CreateGIDStoreSnapshot(clusters: new ushort[] { 0, 1 });

// Restore GID Store in an already initialized world
// All entities are deleted, state is reset
CreateWorld();
W.Initialize();
W.Serializer.RestoreFromGIDStoreSnapshot(gidSnapshot);
```

___

## Cluster and chunk snapshots

#### Cluster:
```csharp
// Save a cluster
byte[] clusterSnapshot = W.Serializer.CreateClusterSnapshot(clusterId: 1);

// With data for loading as new entities
byte[] clusterWithEntities = W.Serializer.CreateClusterSnapshot(
    clusterId: 1,
    withEntitiesData: true  // required for entitiesAsNew during loading
);

// Unload the cluster from memory
ReadOnlySpan<ushort> clusters = stackalloc ushort[] { 1 };
W.Query().BatchUnload(EntityStatusType.Any, clusters: clusters);

// Load the cluster from a snapshot
W.Serializer.LoadClusterSnapshot(clusterSnapshot);

// Load as new entities into a different cluster
W.Serializer.LoadClusterSnapshot(clusterWithEntities,
    new EntitiesAsNewParams(entitiesAsNew: true, clusterId: 2)
);
```

#### Chunk:
```csharp
// Save a chunk
byte[] chunkSnapshot = W.Serializer.CreateChunkSnapshot(chunkIdx: 0);

// Unload the chunk from memory
ReadOnlySpan<uint> unloadChunks = stackalloc uint[] { 0 };
W.Query().BatchUnload(EntityStatusType.Any, unloadChunks);

// Load the chunk from a snapshot
W.Serializer.LoadChunkSnapshot(chunkSnapshot);
```

{: .important }
By default, cluster and chunk snapshots **do not store** entity identifier data (only component data). If you need to load them as new entities (`entitiesAsNew: true`), specify `withEntitiesData: true` when creating the snapshot.

{: .important }
**Cluster and chunk snapshots do not save change tracking data.** Unlike world snapshots, these partial snapshots are designed for streaming and migration scenarios where the target world has its own independent tick and tracking state. Loading a cluster or chunk snapshot leaves the target world's `CurrentTick`, `CurrentLastTick`, and tracking history untouched; only entities, components, and tags are restored. If consistent tracking across partial snapshots is required, use a world snapshot instead.

___

#### Comprehensive streaming example:
```csharp
void PrintCounts(string label) {
    Console.WriteLine($"{label} — Total: {W.CalculateEntitiesCount()} | Loaded: {W.CalculateLoadedEntitiesCount()}");
}

// Save individual entities
using var writer = W.Serializer.CreateEntitiesSnapshotWriter();
foreach (var entity in W.Query().Entities()) {
    writer.WriteAndUnload(entity);
}
byte[] entitiesSnapshot = writer.CreateSnapshot();
PrintCounts("After unloading entities"); // Total: 2 | Loaded: 0

// Create a cluster and populate it
const ushort ZONE_CLUSTER = 1;
W.RegisterCluster(ZONE_CLUSTER);
struct ZoneEntityType : IEntityType { }
W.NewEntities<ZoneEntityType>(count: 2000, clusterId: ZONE_CLUSTER);
PrintCounts("After creating cluster"); // Total: 2002 | Loaded: 2000

// Save and unload cluster
byte[] clusterSnapshot = W.Serializer.CreateClusterSnapshot(ZONE_CLUSTER);
ReadOnlySpan<ushort> zoneClusters = stackalloc ushort[] { ZONE_CLUSTER };
W.Query().BatchUnload(EntityStatusType.Any, clusters: zoneClusters);
PrintCounts("After unloading cluster"); // Total: 2002 | Loaded: 0

// Create a chunk and populate it
var chunkIdx = W.FindNextSelfFreeChunk().ChunkIdx;
W.RegisterChunk(chunkIdx, clusterId: 0);
for (int i = 0; i < 100; i++) {
    W.NewEntityInChunk<ZoneEntityType>(chunkIdx: chunkIdx);
}
PrintCounts("After creating chunk"); // Total: 2102 | Loaded: 100

// Save and unload chunk
byte[] chunkSnapshot = W.Serializer.CreateChunkSnapshot(chunkIdx);
ReadOnlySpan<uint> unloadChunks = stackalloc uint[] { chunkIdx };
W.Query().BatchUnload(EntityStatusType.Any, unloadChunks);
PrintCounts("After unloading chunk"); // Total: 2102 | Loaded: 0

// Save GID Store and recreate world
byte[] gidSnapshot = W.Serializer.CreateGIDStoreSnapshot();
W.Destroy();

CreateWorld();
W.Initialize();
W.Serializer.RestoreFromGIDStoreSnapshot(gidSnapshot);

// Load in any order
W.Serializer.LoadClusterSnapshot(clusterSnapshot);
PrintCounts("After loading cluster"); // Total: 2102 | Loaded: 2000

W.Serializer.LoadEntitiesSnapshot(entitiesSnapshot);
PrintCounts("After loading entities"); // Total: 2102 | Loaded: 2002

W.Serializer.LoadChunkSnapshot(chunkSnapshot);
PrintCounts("After loading chunk"); // Total: 2102 | Loaded: 2102
```

___

## Data migration

#### Component versioning:

The `version` parameter in the `Read` hook enables data migration between schema versions:

```csharp
public struct Position : IComponent, IComponentConfig<Position> {
    public float X, Y, Z;

    public ComponentTypeConfig<Position> Config() => new(
        guid: new Guid("b121594c-456e-4712-9b64-b75dbb37e611"),
        version: 1  // was version 0, now 1
    );

    public void Write<TWorld>(ref BinaryPackWriter writer, World<TWorld>.Entity self)
        where TWorld : struct, IWorldType {
        writer.WriteFloat(X);
        writer.WriteFloat(Y);
        writer.WriteFloat(Z);
    }

    public void Read<TWorld>(ref BinaryPackReader reader, World<TWorld>.Entity self, byte version, bool disabled)
        where TWorld : struct, IWorldType {
        X = reader.ReadFloat();
        Y = reader.ReadFloat();
        // Version 0 didn't have Z — use default value
        Z = version >= 1 ? reader.ReadFloat() : 0f;
    }
}

// Registration
W.Types().Component<Position>();
```

___

#### Migration of removed types:

If a component, tag, or event has been removed from the code, data is skipped automatically by default. For custom handling:

```csharp
// Migration for a removed component
W.Serializer.SetComponentDeleteMigrator(
    new Guid("guid-of-removed-component"),
    (ref BinaryPackReader reader, W.Entity entity, byte version, bool disabled) => {
        // Read ALL data and perform custom logic
    }
);

// Migration for a removed tag
W.Serializer.SetMigrator(
    new Guid("guid-of-removed-tag"),
    (W.Entity entity) => {
        // Custom logic
    }
);

// Migration for a removed event
W.Serializer.SetEventDeleteMigrator(
    new Guid("guid-of-removed-event"),
    (ref BinaryPackReader reader, byte version) => {
        // Read ALL data and perform custom logic
    }
);
```

{: .note }
When new types are added, old snapshots load correctly — new components are simply absent on loaded entities.

___

## Callbacks

#### Global callbacks:
```csharp
// Called for all snapshot types (World, Cluster, Chunk, Entities)

// Before creating a snapshot
W.Serializer.RegisterPreCreateSnapshotCallback(param => {
    Console.WriteLine($"Creating snapshot of type: {param.Type}");
});

// After creating a snapshot
W.Serializer.RegisterPostCreateSnapshotCallback(param => {
    Console.WriteLine($"Snapshot created: {param.Type}");
});

// Before loading a snapshot
W.Serializer.RegisterPreLoadSnapshotCallback(param => {
    Console.WriteLine($"Loading snapshot: {param.Type}, AsNew: {param.EntitiesAsNew}");
});

// After loading a snapshot
W.Serializer.RegisterPostLoadSnapshotCallback(param => {
    Console.WriteLine($"Snapshot loaded: {param.Type}");
});
```

#### Filtering by snapshot type:
```csharp
W.Serializer.RegisterPreCreateSnapshotCallback(param => {
    if (param.Type == SnapshotType.World) {
        Console.WriteLine("Saving world");
    }
});
```

#### Per-entity callbacks:
```csharp
// After saving each entity
W.Serializer.RegisterPostCreateSnapshotEachEntityCallback((entity, param) => {
    Console.WriteLine($"Saved: {entity.PrettyString}");
});

// After loading each entity
W.Serializer.RegisterPostLoadSnapshotEachEntityCallback((entity, param) => {
    Console.WriteLine($"Loaded: {entity.PrettyString}");
});
```

___

## Custom data in snapshots

#### Global custom data:
```csharp
// Add arbitrary data to a snapshot (e.g., system or service data)
W.Serializer.SetSnapshotHandler(
    new Guid("57c15483-988a-47e7-919c-51b9a7b957b5"), // unique data type guid
    version: 0,
    writer: (ref BinaryPackWriter writer, SnapshotWriteParams param) => {
        writer.WriteDateTime(DateTime.Now);
    },
    reader: (ref BinaryPackReader reader, ushort version, SnapshotReadParams param) => {
        var savedTime = reader.ReadDateTime();
        Console.WriteLine($"Save time: {savedTime}");
    }
);
```

#### Per-entity custom data:
```csharp
W.Serializer.SetSnapshotHandlerEachEntity(
    new Guid("68d26594-1a9b-48f8-b2de-71c0a8b068c6"),
    version: 0,
    writer: (ref BinaryPackWriter writer, W.Entity entity, SnapshotWriteParams param) => {
        // Write additional data for the entity
    },
    reader: (ref BinaryPackReader reader, W.Entity entity, ushort version, SnapshotReadParams param) => {
        // Read additional data for the entity
    }
);
```

___

## Event serialization

```csharp
// Save events
byte[] eventsSnapshot = W.Serializer.CreateEventsSnapshot();

// With GZIP compression
byte[] eventsCompressed = W.Serializer.CreateEventsSnapshot(gzip: true);

// To file
W.Serializer.CreateEventsSnapshot("path/to/events.bin");

// Load events
W.Serializer.LoadEventsSnapshot(eventsSnapshot);

// From file
W.Serializer.LoadEventsSnapshot("path/to/events.bin");
```

{: .note }
When using `CreateWorldSnapshot`, events are saved automatically (unless `writeEvents: false` is specified). Separate event serialization is needed when using `EntitiesSnapshot`.

___

## Resources serialization

`IResource` ships with four optional default-implemented methods. Override `Guid()` to opt the resource into automatic snapshot serialization; the others are required only when the type is not unmanaged.

```csharp
public interface IResource {
    public Guid? Guid()                                              => null;
    public byte  Version()                                            => 0;
    public void  Write(ref BinaryPackWriter writer)                   {}
    public void  Read(ref BinaryPackReader reader, byte version)      {}
}
```

#### Validation rules (checked at first `SetResource`)

- Resources without `Guid` (default `null`) are silently excluded from snapshots.
- If `Guid` is non-empty and the type is not unmanaged (a reference type, or a struct containing references) and either `Write` or `Read` is missing — `StaticEcsException` is thrown.
- Duplicate `Guid` between two singleton resources of different types is asserted in DEBUG.

#### Format selection

- **Unmanaged struct without `Write`/`Read`** — framework writes/reads `Unsafe.SizeOf<T>()` raw bytes directly from `Resources<TWorld, T>.Value` / the named-resource box.
- **Non-unmanaged type, or any version mismatch on an unmanaged type** — `Read(ref reader, savedVersion)` is invoked for migration; `Write(ref writer)` is used on save.

#### Examples

```csharp
// Unmanaged singleton resource — Write/Read not required
public struct GameSettings : IResource {
    public float MasterVolume;
    public bool  Vsync;
    public Guid? Guid() => new("11111111-2222-3333-4444-555555555555");
}

// Non-unmanaged resource — Write/Read required
public class AssetCache : IResource {
    public Dictionary<string, byte[]> Items = new();

    public Guid? Guid() => new("22222222-3333-4444-5555-666666666666");
    public byte  Version() => 1;

    public void Write(ref BinaryPackWriter writer) {
        writer.WriteInt(Items.Count);
        foreach (var kvp in Items) {
            writer.WriteString16(kvp.Key);
            writer.WriteByteArray(kvp.Value);
        }
    }

    public void Read(ref BinaryPackReader reader, byte version) {
        Items.Clear();
        var count = reader.ReadInt();
        for (var i = 0; i < count; i++) {
            var key = reader.ReadString16();
            Items[key] = reader.ReadByteArray();
        }
    }
}
```

#### What enters the snapshot

- **Singleton resources** (`SetResource<T>(value, …)`) — keyed by `Guid` of `T`.
- **Named resources** (`SetResource<T>(key, value, …)`) — keyed by `Guid` of `T` together with the string key.

`WorldSnapshot` includes both groups automatically (between events and custom-snapshot data). To save or load only resources, use the standalone API mirroring events:

```csharp
// Save
byte[] snapshot = W.Serializer.CreateResourcesSnapshot();
W.Serializer.CreateResourcesSnapshot("resources.bin", gzip: true);

// Load
W.Serializer.LoadResourcesSnapshot(snapshot);
W.Serializer.LoadResourcesSnapshot("resources.bin");
```

On load, entries whose `Guid` is not currently registered are silently skipped (same as removed components or events) — adding or removing a resource type between save and load is forward-compatible.

___

## Systems serialization

`ISystem` carries the same four optional methods as `IResource`. Override `Guid()` to opt a system into snapshot serialization.

```csharp
public interface ISystem {
    public void Init()             { }
    public void Update()           { }
    public bool UpdateIsActive()   => true;
    public void Destroy()          { }

    public Guid? Guid()                                              => null;
    public byte  Version()                                            => 0;
    public void  Write(ref BinaryPackWriter writer)                   {}
    public void  Read(ref BinaryPackReader reader, byte version)      {}
}
```

#### Validation rules (checked at `Add<TSystem>`)

- Systems without `Guid` are silently excluded from snapshots.
- Any system declaring `Guid` **must** override both `Write` and `Read` — regardless of layout. The unmanaged fast-path does not apply: system instances live boxed inside `SystemData.System`, so framework always invokes the hooks. Missing them throws `StaticEcsException` from `Add`.
- Duplicate `Guid` within the same `Systems<TSystemsType>` group is asserted in DEBUG.

#### Example

```csharp
public class SpawnerSystem : ISystem {
    private int _nextId;
    private float _accumulator;

    public Guid? Guid() => new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    public byte  Version() => 1;

    public void Update() { /* spawn logic */ }

    public void Write(ref BinaryPackWriter writer) {
        writer.WriteInt(_nextId);
        writer.WriteFloat(_accumulator);
    }

    public void Read(ref BinaryPackReader reader, byte version) {
        _nextId = reader.ReadInt();
        _accumulator = reader.ReadFloat();
    }
}
```

#### `Systems<TSystemsType>.Create` accepts an explicit pipeline `Guid`

```csharp
GameSys.Create(baseSize: 64);                                                // Guid = typeof(GameSystems).GuidFromAQN()
GameSys.Create(baseSize: 64, snapshotGuid: new("…stable-pipeline-guid…"));   // explicit, survives namespace renames
```

The pipeline registers itself in the world's snapshot registry on `Create` and unregisters on `Destroy`. `WorldSnapshot` walks all registered pipelines and writes a section per pipeline; on load, sections whose pipeline `Guid` is not currently registered are silently skipped.

#### Standalone API

Mirrors `Create/LoadEventsSnapshot`. Walks all registered `Systems<TSystemsType>` pipelines (and their scoped resources):

```csharp
// Save
byte[] snapshot = W.Serializer.CreateSystemsSnapshot();
W.Serializer.CreateSystemsSnapshot("systems.bin", gzip: true);

// Load
W.Serializer.LoadSystemsSnapshot(snapshot);
W.Serializer.LoadSystemsSnapshot("systems.bin");
```

Each pipeline section contains its scoped resources (singleton + named) followed by every system within it that declares a `Guid`.

___

## Custom GUID for stability

All types automatically get a stable `Guid` computed from the type name (`assembly-qualified name`). If you rename or move a type, the auto-generated GUID changes — breaking compatibility with existing snapshots. To prevent this, specify a fixed GUID:

```csharp
// Example: save all entities
using var writer = W.Serializer.CreateEntitiesSnapshotWriter();
writer.WriteAllEntities();
byte[] snapshot = writer.CreateSnapshot();
byte[] gidSnapshot = W.Serializer.CreateGIDStoreSnapshot();
byte[] eventsSnapshot = W.Serializer.CreateEventsSnapshot();
```

___

## Compression (GZIP)

All snapshot creation methods support GZIP compression via `gzip: true`. **All** loading methods (`LoadWorldSnapshot`, `LoadClusterSnapshot`, `LoadChunkSnapshot`, `LoadEventsSnapshot`, `LoadResourcesSnapshot`, `LoadSystemsSnapshot`, `RestoreFromGIDStoreSnapshot`) **autodetect** gzip from the byte stream — pass the bytes/path directly without any flag.

```csharp
// World — gzip autodetected on load
byte[] snapshot = W.Serializer.CreateWorldSnapshot(gzip: true);
W.Serializer.LoadWorldSnapshot(snapshot);

// Cluster — gzip autodetected on load
byte[] cluster = W.Serializer.CreateClusterSnapshot(1, gzip: true);
W.Serializer.LoadClusterSnapshot(cluster);

// Chunk — gzip autodetected on load
byte[] chunk = W.Serializer.CreateChunkSnapshot(0, gzip: true);
W.Serializer.LoadChunkSnapshot(chunk);

// GID Store
byte[] gid = W.Serializer.CreateGIDStoreSnapshot(gzip: true);

// Events — gzip autodetected on load
byte[] events = W.Serializer.CreateEventsSnapshot(gzip: true);
W.Serializer.LoadEventsSnapshot(events);

// Resources — gzip autodetected on load
byte[] resources = W.Serializer.CreateResourcesSnapshot(gzip: true);
W.Serializer.LoadResourcesSnapshot(resources);

// Systems — gzip autodetected on load
byte[] systems = W.Serializer.CreateSystemsSnapshot(gzip: true);
W.Serializer.LoadSystemsSnapshot(systems);

// Files — gzip autodetected on load for world/cluster/chunk/events/resources/systems
W.Serializer.CreateWorldSnapshot("world.bin", gzip: true);
W.Serializer.LoadWorldSnapshot("world.bin");
```

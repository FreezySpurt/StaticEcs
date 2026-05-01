---
title: 序列化
parent: 功能
nav_order: 15
---

## 序列化
序列化是创建整个世界或单个实体、集群、块的二进制快照的机制。
二进制序列化使用 [StaticPack](https://github.com/Felid-Force-Studios/StaticPack)。

___

## 配置组件

要支持组件序列化：
1. 注册时指定 `Guid`（稳定的类型标识符）
2. 在组件上实现 `Write` 和 `Read` 钩子

{: .importantzh }
`Write` 和 `Read` 钩子对于 `EntitiesSnapshot` 序列化是**必需的**（适用于所有组件类型，包括 unmanaged）。对于世界/集群/块快照，non-unmanaged 类型也始终使用这些钩子。

#### Unmanaged 组件：
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

#### Non-unmanaged 组件（包含引用字段）：
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

#### Unmanaged 类型的块内存复制：

对于世界/集群/块快照，unmanaged 组件自动作为内存块序列化，而不是逐个调用 `Write`/`Read`。

{: .notezh }
`UnmanagedPackArrayStrategy<T>` 执行直接内存复制 — 比逐个组件序列化快得多。仅适用于 unmanaged 类型。版本不匹配时（数据迁移），系统自动回退到 `Read` 钩子。默认策略自动检测：unmanaged 类型使用 `UnmanagedPackArrayStrategy<T>`，其他类型使用 `StructPackArrayStrategy<T>`。

#### Multi 和 Links 的批量段序列化：

多组件和 Links 将值存储在共享段存储中。批量段序列化策略会自动应用于 unmanaged 值类型。要覆盖 GUID 或其他配置，请在类型上实现相应的配置接口：

```csharp
// 带自定义配置的多组件
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

// 带自定义配置的 Links
public struct MyLinkType : ILinksType, ILinksConfig<MyLinkType> {
    public ComponentTypeConfig<W.Links<MyLinkType>> Config<TWorld>()
        where TWorld : struct, IWorldType => new(
        guid: new Guid("...")
    );
}

W.Types().Links<MyLinkType>();
```

#### 完整配置：
```csharp
public struct Position : IComponent, IComponentConfig<Position> {
    public float X, Y, Z;

    public ComponentTypeConfig<Position> Config() => new(
        guid: new Guid("b121594c-456e-4712-9b64-b75dbb37e611"),
        version: 1,                  // 用于迁移的数据模式版本（默认 — 0）
        noDataLifecycle: true        // 禁用框架数据管理（默认 — false）
        // 序列化策略自动检测：unmanaged 类型使用 UnmanagedPackArrayStrategy<T>，其他类型使用 StructPackArrayStrategy<T>
    );

    // ... Write/Read 钩子 ...
}

W.Types().Component<Position>();
```

___

## 配置标签

标签通过实现 `ITagConfig<T>` 配置：

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

#### 完整配置:
```csharp
public struct Poisoned : ITag, ITagConfig<Poisoned>,
                         ITrackableAdded, ITrackableDeleted {
    public TagTypeConfig<Poisoned> Config() => new(
        guid: new Guid("A1B2C3D4-...") // 序列化的稳定标识符（默认 — 从类型名称自动计算）
    );
}

W.Types().Tag<Poisoned>();
```

变更追踪通过在类型本身上实现标记接口（`ITrackableAdded`、`ITrackableDeleted`）启用 — 参见[变更追踪](tracking)。

{: .notezh }
所有类型自动获得由类型名称计算的稳定 GUID。要覆盖，请在标签结构体上实现 `ITagConfig<T>` 并提供自定义 guid。

___

## 配置事件

事件通过实现 `IEventConfig<T>` 配置 — 类似于组件：

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

## 世界快照（World Snapshot）

保存完整的世界状态：所有实体、组件、标签、事件以及变更追踪状态。

{: .important }
**世界快照会持久化当前 tick 和完整的追踪历史。** 保存的数据流包括 `CurrentTick`、`CurrentLastTick` 以及全部 `TrackingBufferSize + 1` 个历史槽位——用于 `AllAdded<T>` / `AllChanged<T>` / `AllDeleted<T>` 过滤器、实体的 `HasAdded/HasChanged/HasDeleted` 方法，以及世界级的创建追踪（`HasCreated`）。加载之后，基于 tick 的追踪查询（包括使用 `fromTick` 的查询）返回的结果与保存前一致。

{: .important }
**加载时配置必须匹配。** 目标世界的 `TrackingBufferSize` 和 `TrackCreated` 必须与快照中保存的值相等。任何不一致都会抛出 `StaticEcsException`。这些值在创建世界时通过 `WorldConfig` 指定——保存和加载之间不支持更改这些值。

{: .note }
每个快照以 2 字节的格式版本头（`FormatVersion = 2`）和 8 字节的快照大小开头。加载由不兼容版本生成的快照会抛出带有明确说明的 `StaticEcsException`。

#### 在初始化后保存和加载：
```csharp
byte[] worldSnapshot = W.Serializer.CreateWorldSnapshot();
W.Destroy();

CreateWorld();
W.Initialize();
// 加载前所有现有实体和事件将被删除
W.Serializer.LoadWorldSnapshot(worldSnapshot);
```

#### 附加参数：
```csharp
// 保存到文件
W.Serializer.CreateWorldSnapshot("path/to/world.bin");

// 使用 GZIP 压缩
byte[] compressed = W.Serializer.CreateWorldSnapshot(gzip: true);

// 按集群过滤
W.Serializer.CreateWorldSnapshot(clusters: new ushort[] { 0, 1 });

// 块写入策略
W.Serializer.CreateWorldSnapshot(strategy: ChunkWritingStrategy.SelfOwner);

// 不包含事件
W.Serializer.CreateWorldSnapshot(writeEvents: false);

// 不包含自定义数据
W.Serializer.CreateWorldSnapshot(withCustomSnapshotData: false);

// 从文件加载
W.Serializer.LoadWorldSnapshot("path/to/world.bin");

// 加载压缩数据
W.Serializer.LoadWorldSnapshot(compressed, gzip: true);
```

{: .importantzh }
所有组件和标签自动获得由类型名称计算的稳定 `Guid`。您可以通过配置覆盖 `Guid`，以确保在重命名类型时保持稳定。

___

## 实体快照（Entities Snapshot）

允许以精细控制保存和加载单个实体。

#### 保存实体：
```csharp
// 创建实体写入器
using var writer = W.Serializer.CreateEntitiesSnapshotWriter();

// 写入特定实体
foreach (var entity in W.Query().Entities()) {
    writer.Write(entity);
}

// 或一次写入所有实体
// writer.WriteAllEntities();

// 创建快照
byte[] snapshot = writer.CreateSnapshot();

// 或保存到文件
// writer.CreateSnapshot("path/to/entities.bin");
```

#### 写入并同时卸载：
```csharp
using var writer = W.Serializer.CreateEntitiesSnapshotWriter();

// 写入并卸载 — 在流式加载时节省内存
foreach (var entity in W.Query().Entities()) {
    writer.WriteAndUnload(entity);
}

// 或一次处理所有实体
// writer.WriteAndUnloadAllEntities();

byte[] snapshot = writer.CreateSnapshot();
```

___

#### 加载实体（entitiesAsNew）：

`entitiesAsNew` 参数决定实体的加载方式：

- **`entitiesAsNew: false`**（默认）— 实体恢复到**相同槽位**（相同 EntityGID）。如果槽位已被占用 — DEBUG 模式下报错。
- **`entitiesAsNew: true`** — 实体加载到**新槽位**，获得新的 EntityGID。实体间的链接（Link、Links）可能指向错误的实体。

```csharp
// 加载到原始槽位
W.Serializer.LoadEntitiesSnapshot(snapshot, entitiesAsNew: false);

// 作为新实体加载
W.Serializer.LoadEntitiesSnapshot(snapshot, entitiesAsNew: true);

// 为每个加载的实体添加回调
W.Serializer.LoadEntitiesSnapshot(snapshot, entitiesAsNew: true, onLoad: entity => {
    Console.WriteLine($"已加载: {entity.PrettyString}");
});
```

___

#### 保持实体间链接（GID Store）：

要正确使用 `entitiesAsNew: false` 加载实体，需要保存全局标识符存储：

```csharp
// 1. 保存实体和 GID Store
using var writer = W.Serializer.CreateEntitiesSnapshotWriter();
writer.WriteAllEntities();
byte[] entitiesSnapshot = writer.CreateSnapshot();
byte[] gidSnapshot = W.Serializer.CreateGIDStoreSnapshot();
W.Destroy();

// 2. 使用 GID Store 恢复世界
CreateWorld();
W.Initialize();
W.Serializer.RestoreFromGIDStoreSnapshot(gidSnapshot);

// 新实体不会占用已保存实体的槽位
var newEntity = W.NewEntity<Default>();
newEntity.Set(new Position { X = 1 });

// 3. 将实体加载到原始槽位 — 所有链接正确
W.Serializer.LoadEntitiesSnapshot(entitiesSnapshot, entitiesAsNew: false);
```

{: .notezh }
GID Store 包含所有已发放标识符的信息。这保证了新实体不会占用已卸载实体的槽位，所有链接（Link、Links、数据中的 EntityGID）保持正确。

___

## GID Store

```csharp
// 保存 GID Store
byte[] gidSnapshot = W.Serializer.CreateGIDStoreSnapshot();

// 使用 GZIP 压缩
byte[] gidCompressed = W.Serializer.CreateGIDStoreSnapshot(gzip: true);

// 保存到文件
W.Serializer.CreateGIDStoreSnapshot("path/to/gid.bin");

// 使用块写入策略
W.Serializer.CreateGIDStoreSnapshot(strategy: ChunkWritingStrategy.SelfOwner);

// 按集群过滤
W.Serializer.CreateGIDStoreSnapshot(clusters: new ushort[] { 0, 1 });

// 在已初始化的世界中恢复 GID Store
// 所有实体将被删除，状态将重置
CreateWorld();
W.Initialize();
W.Serializer.RestoreFromGIDStoreSnapshot(gidSnapshot);
```

___

## 集群和块快照

#### 集群：
```csharp
// 保存集群
byte[] clusterSnapshot = W.Serializer.CreateClusterSnapshot(clusterId: 1);

// 包含实体数据以便作为新实体加载
byte[] clusterWithEntities = W.Serializer.CreateClusterSnapshot(
    clusterId: 1,
    withEntitiesData: true  // 加载时使用 entitiesAsNew 所需
);

// 从内存卸载集群
ReadOnlySpan<ushort> clusters = stackalloc ushort[] { 1 };
W.Query().BatchUnload(EntityStatusType.Any, clusters: clusters);

// 从快照加载集群
W.Serializer.LoadClusterSnapshot(clusterSnapshot);

// 作为新实体加载到不同集群
W.Serializer.LoadClusterSnapshot(clusterWithEntities,
    new EntitiesAsNewParams(entitiesAsNew: true, clusterId: 2)
);
```

#### 块：
```csharp
// 保存块
byte[] chunkSnapshot = W.Serializer.CreateChunkSnapshot(chunkIdx: 0);

// 从内存卸载块
ReadOnlySpan<uint> unloadChunks = stackalloc uint[] { 0 };
W.Query().BatchUnload(EntityStatusType.Any, unloadChunks);

// 从快照加载块
W.Serializer.LoadChunkSnapshot(chunkSnapshot);
```

{: .importantzh }
默认情况下，集群和块快照**不存储**实体标识符数据（仅存储组件数据）。如果需要作为新实体加载（`entitiesAsNew: true`），创建快照时请指定 `withEntitiesData: true`。

{: .important }
**集群和块快照不保存变更追踪数据。** 与世界快照不同，这些部分快照用于流式加载和迁移场景，目标世界拥有自己独立的 tick 和追踪状态。加载集群或块快照不会改变目标世界的 `CurrentTick`、`CurrentLastTick` 和追踪历史——只恢复实体、组件和标签。如果需要跨部分快照保持一致的追踪历史，请使用世界快照。

___

#### 综合流式加载示例：
```csharp
void PrintCounts(string label) {
    Console.WriteLine($"{label} — 总计: {W.CalculateEntitiesCount()} | 已加载: {W.CalculateLoadedEntitiesCount()}");
}

// 保存单个实体
using var writer = W.Serializer.CreateEntitiesSnapshotWriter();
foreach (var entity in W.Query().Entities()) {
    writer.WriteAndUnload(entity);
}
byte[] entitiesSnapshot = writer.CreateSnapshot();
PrintCounts("卸载实体后"); // 总计: 2 | 已加载: 0

// 创建集群并填充
const ushort ZONE_CLUSTER = 1;
W.RegisterCluster(ZONE_CLUSTER);
struct ZoneEntityType : IEntityType { }
W.NewEntities<ZoneEntityType>(count: 2000, clusterId: ZONE_CLUSTER);
PrintCounts("创建集群后"); // 总计: 2002 | 已加载: 2000

// 保存并卸载集群
byte[] clusterSnapshot = W.Serializer.CreateClusterSnapshot(ZONE_CLUSTER);
ReadOnlySpan<ushort> zoneClusters = stackalloc ushort[] { ZONE_CLUSTER };
W.Query().BatchUnload(EntityStatusType.Any, clusters: zoneClusters);
PrintCounts("卸载集群后"); // 总计: 2002 | 已加载: 0

// 创建块并填充
var chunkIdx = W.FindNextSelfFreeChunk().ChunkIdx;
W.RegisterChunk(chunkIdx, clusterId: 0);
for (int i = 0; i < 100; i++) {
    W.NewEntityInChunk<ZoneEntityType>(chunkIdx: chunkIdx);
}
PrintCounts("创建块后"); // 总计: 2102 | 已加载: 100

// 保存并卸载块
byte[] chunkSnapshot = W.Serializer.CreateChunkSnapshot(chunkIdx);
ReadOnlySpan<uint> unloadChunks = stackalloc uint[] { chunkIdx };
W.Query().BatchUnload(EntityStatusType.Any, unloadChunks);
PrintCounts("卸载块后"); // 总计: 2102 | 已加载: 0

// 保存 GID Store 并重建世界
byte[] gidSnapshot = W.Serializer.CreateGIDStoreSnapshot();
W.Destroy();

CreateWorld();
W.Initialize();
W.Serializer.RestoreFromGIDStoreSnapshot(gidSnapshot);

// 以任意顺序加载
W.Serializer.LoadClusterSnapshot(clusterSnapshot);
PrintCounts("加载集群后"); // 总计: 2102 | 已加载: 2000

W.Serializer.LoadEntitiesSnapshot(entitiesSnapshot);
PrintCounts("加载实体后"); // 总计: 2102 | 已加载: 2002

W.Serializer.LoadChunkSnapshot(chunkSnapshot);
PrintCounts("加载块后"); // 总计: 2102 | 已加载: 2102
```

___

## 数据迁移

#### 组件版本控制：

`Read` 钩子中的 `version` 参数支持在模式版本之间迁移数据：

```csharp
public struct Position : IComponent, IComponentConfig<Position> {
    public float X, Y, Z;

    public ComponentTypeConfig<Position> Config() => new(
        guid: new Guid("b121594c-456e-4712-9b64-b75dbb37e611"),
        version: 1  // 之前是版本 0，现在是 1
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
        // 版本 0 没有 Z — 使用默认值
        Z = version >= 1 ? reader.ReadFloat() : 0f;
    }
}

// 注册
W.Types().Component<Position>();
```

___

#### 已删除类型的迁移：

如果组件、标签或事件已从代码中删除，数据默认会自动跳过。对于自定义处理：

```csharp
// 已删除组件的迁移
W.Serializer.SetComponentDeleteMigrator(
    new Guid("已删除组件的guid"),
    (ref BinaryPackReader reader, W.Entity entity, byte version, bool disabled) => {
        // 读取所有数据并执行自定义逻辑
    }
);

// 已删除标签的迁移
W.Serializer.SetMigrator(
    new Guid("已删除标签的guid"),
    (W.Entity entity) => {
        // 自定义逻辑
    }
);

// 已删除事件的迁移
W.Serializer.SetEventDeleteMigrator(
    new Guid("已删除事件的guid"),
    (ref BinaryPackReader reader, byte version) => {
        // 读取所有数据并执行自定义逻辑
    }
);
```

{: .notezh }
添加新类型时，旧快照可以正确加载 — 新组件只是在加载的实体上不存在。

___

## 回调

#### 全局回调：
```csharp
// 对所有快照类型调用（World、Cluster、Chunk、Entities）

// 创建快照前
W.Serializer.RegisterPreCreateSnapshotCallback(param => {
    Console.WriteLine($"正在创建快照类型: {param.Type}");
});

// 创建快照后
W.Serializer.RegisterPostCreateSnapshotCallback(param => {
    Console.WriteLine($"快照已创建: {param.Type}");
});

// 加载快照前
W.Serializer.RegisterPreLoadSnapshotCallback(param => {
    Console.WriteLine($"正在加载快照: {param.Type}, AsNew: {param.EntitiesAsNew}");
});

// 加载快照后
W.Serializer.RegisterPostLoadSnapshotCallback(param => {
    Console.WriteLine($"快照已加载: {param.Type}");
});
```

#### 按快照类型过滤：
```csharp
W.Serializer.RegisterPreCreateSnapshotCallback(param => {
    if (param.Type == SnapshotType.World) {
        Console.WriteLine("正在保存世界");
    }
});
```

#### 每个实体的回调：
```csharp
// 保存每个实体后
W.Serializer.RegisterPostCreateSnapshotEachEntityCallback((entity, param) => {
    Console.WriteLine($"已保存: {entity.PrettyString}");
});

// 加载每个实体后
W.Serializer.RegisterPostLoadSnapshotEachEntityCallback((entity, param) => {
    Console.WriteLine($"已加载: {entity.PrettyString}");
});
```

___

## 快照中的自定义数据

#### 全局自定义数据：
```csharp
// 向快照添加任意数据（例如系统或服务数据）
W.Serializer.SetSnapshotHandler(
    new Guid("57c15483-988a-47e7-919c-51b9a7b957b5"), // 唯一的数据类型 guid
    version: 0,
    writer: (ref BinaryPackWriter writer, SnapshotWriteParams param) => {
        writer.WriteDateTime(DateTime.Now);
    },
    reader: (ref BinaryPackReader reader, ushort version, SnapshotReadParams param) => {
        var savedTime = reader.ReadDateTime();
        Console.WriteLine($"保存时间: {savedTime}");
    }
);
```

#### 每个实体的自定义数据：
```csharp
W.Serializer.SetSnapshotHandlerEachEntity(
    new Guid("68d26594-1a9b-48f8-b2de-71c0a8b068c6"),
    version: 0,
    writer: (ref BinaryPackWriter writer, W.Entity entity, SnapshotWriteParams param) => {
        // 为实体写入附加数据
    },
    reader: (ref BinaryPackReader reader, W.Entity entity, ushort version, SnapshotReadParams param) => {
        // 读取实体的附加数据
    }
);
```

___

## 事件序列化

```csharp
// 保存事件
byte[] eventsSnapshot = W.Serializer.CreateEventsSnapshot();

// 使用 GZIP 压缩
byte[] eventsCompressed = W.Serializer.CreateEventsSnapshot(gzip: true);

// 保存到文件
W.Serializer.CreateEventsSnapshot("path/to/events.bin");

// 加载事件
W.Serializer.LoadEventsSnapshot(eventsSnapshot);

// 从文件加载
W.Serializer.LoadEventsSnapshot("path/to/events.bin");
```

{: .notezh }
使用 `CreateWorldSnapshot` 时，事件会自动保存（除非指定 `writeEvents: false`）。使用 `EntitiesSnapshot` 时需要单独序列化事件。

___

## 资源序列化

`IResource` 提供四个可选的默认实现方法。重写 `Guid()` 以使资源自动加入快照序列化；其他方法仅在类型不是 unmanaged 时才需要。

```csharp
public interface IResource {
    public Guid? Guid()                                              => null;
    public byte  Version()                                            => 0;
    public void  Write(ref BinaryPackWriter writer)                   {}
    public void  Read(ref BinaryPackReader reader, byte version)      {}
}
```

#### 验证规则（在第一次 `SetResource` 时检查）

- 没有 `Guid`（默认 `null`）的资源会被静默排除在快照之外。
- 如果 `Guid` 非空、类型不是 unmanaged（引用类型，或包含引用的 struct）、且缺少 `Write` 或 `Read` — 抛出 `StaticEcsException`。
- 不同类型的两个 singleton 资源出现重复 `Guid` 时在 DEBUG 模式下断言。

#### 格式选择

- **没有 `Write`/`Read` 的 unmanaged struct** — 框架直接从 `Resources<TWorld, T>.Value` 或命名资源 box 写入/读取 `Unsafe.SizeOf<T>()` 个原始字节。
- **非 unmanaged 类型，或 unmanaged 类型遇到版本不匹配** — 调用 `Read(ref reader, savedVersion)` 进行迁移；保存时调用 `Write(ref writer)`。

#### 示例

```csharp
// Unmanaged singleton 资源 — 不需要 Write/Read
public struct GameSettings : IResource {
    public float MasterVolume;
    public bool  Vsync;
    public Guid? Guid() => new("11111111-2222-3333-4444-555555555555");
}

// 非 unmanaged 资源 — 需要 Write/Read
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

#### 进入快照的内容

- **Singleton 资源**（`SetResource<T>(value, …)`）— 以类型 `T` 的 `Guid` 为键。
- **命名资源**（`SetResource<T>(key, value, …)`）— 以类型 `T` 的 `Guid` 加字符串键为键。

`WorldSnapshot` 自动包含两组（位于事件块和用户自定义快照数据之间）。如果只想保存或加载资源，可使用与事件镜像的独立 API：

```csharp
// 保存
byte[] snapshot = W.Serializer.CreateResourcesSnapshot();
W.Serializer.CreateResourcesSnapshot("resources.bin", gzip: true);

// 加载
W.Serializer.LoadResourcesSnapshot(snapshot);
W.Serializer.LoadResourcesSnapshot("resources.bin", gzip: true);
```

加载时，当前未注册的 `Guid` 条目会被静默跳过（与已删除的组件或事件相同）— 在保存和加载之间添加或删除资源类型是向前兼容的。

___

## 系统序列化

`ISystem` 拥有与 `IResource` 相同的四个可选方法。重写 `Guid()` 以使系统加入快照序列化。

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

#### 验证规则（在 `Add<TSystem>` 时检查）

- 没有 `Guid` 的系统会被静默排除在快照之外。
- 任何声明 `Guid` 的系统**必须**重写 `Write` 和 `Read` — 与布局无关。unmanaged 快速路径不适用：系统实例以装箱形式存储在 `SystemData.System` 中，框架始终调用钩子。缺少它们会从 `Add` 抛出 `StaticEcsException`。
- 同一 `Systems<TSystemsType>` 组内的重复 `Guid` 在 DEBUG 模式下断言。

#### 示例

```csharp
public class SpawnerSystem : ISystem {
    private int _nextId;
    private float _accumulator;

    public Guid? Guid() => new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    public byte  Version() => 1;

    public void Update() { /* spawn 逻辑 */ }

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

#### `Systems<TSystemsType>.Create` 接受显式的组 `Guid`

```csharp
GameSys.Create(baseSize: 64);                                                // Guid = typeof(GameSystems).GuidFromAQN()
GameSys.Create(baseSize: 64, snapshotGuid: new("…stable-pipeline-guid…"));   // 显式，可在命名空间重命名后保持
```

组在 `Create` 时将自身注册到世界的快照注册表中，在 `Destroy` 时取消注册。`WorldSnapshot` 遍历所有已注册的组，每个组写一个段；加载时未注册的组 `Guid` 段会被静默跳过。

#### 独立 API

镜像 `Create/LoadEventsSnapshot`。遍历所有已注册的 `Systems<TSystemsType>` 组（连同其作用域内的资源）：

```csharp
// 保存
byte[] snapshot = W.Serializer.CreateSystemsSnapshot();
W.Serializer.CreateSystemsSnapshot("systems.bin", gzip: true);

// 加载
W.Serializer.LoadSystemsSnapshot(snapshot);
W.Serializer.LoadSystemsSnapshot("systems.bin", gzip: true);
```

每个组段包含其作用域内的资源（singleton + named），然后是组内每个声明 `Guid` 的系统。

___

## 自定义 GUID 以确保稳定性

所有类型自动获得由类型名称（`assembly-qualified name`）计算的稳定 `Guid`。如果重命名或移动类型，自动生成的 GUID 会改变 — 这将破坏与现有快照的兼容性。为防止此问题，请指定固定的 GUID：

```csharp
// 示例：保存所有实体
using var writer = W.Serializer.CreateEntitiesSnapshotWriter();
writer.WriteAllEntities();
byte[] snapshot = writer.CreateSnapshot();
byte[] gidSnapshot = W.Serializer.CreateGIDStoreSnapshot();
byte[] eventsSnapshot = W.Serializer.CreateEventsSnapshot();
```

___

## 压缩（GZIP）

所有快照创建和加载方法都支持 GZIP 压缩：

```csharp
// 世界
byte[] snapshot = W.Serializer.CreateWorldSnapshot(gzip: true);
W.Serializer.LoadWorldSnapshot(snapshot, gzip: true);

// 集群
byte[] cluster = W.Serializer.CreateClusterSnapshot(1, gzip: true);
W.Serializer.LoadClusterSnapshot(cluster, gzip: true);

// 块
byte[] chunk = W.Serializer.CreateChunkSnapshot(0, gzip: true);
W.Serializer.LoadChunkSnapshot(chunk, gzip: true);

// GID Store
byte[] gid = W.Serializer.CreateGIDStoreSnapshot(gzip: true);

// 事件
byte[] events = W.Serializer.CreateEventsSnapshot(gzip: true);
W.Serializer.LoadEventsSnapshot(events, gzip: true);

// 文件
W.Serializer.CreateWorldSnapshot("world.bin", gzip: true);
W.Serializer.LoadWorldSnapshot("world.bin", gzip: true);
```

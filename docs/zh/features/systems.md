---
title: 系统
parent: 功能
nav_order: 10
---

## Systems
系统通过定义的生命周期管理世界逻辑
- 嵌套类 `World<TWorld>.Systems<SysType>` — 每个 `ISystemsType` 类型在世界中创建一个隔离的系统组
- 统一的 `ISystem` 接口，包含四个方法（全部可选）
- 系统按 `order` 参数定义的顺序执行
- 未实现的方法不会被调用，不产生开销
- 系统可以是结构体或类

___

## ISystemsType

用于隔离系统组的标记接口。每种类型获得独立的静态存储：

```csharp
public struct GameSystems : ISystemsType { }
public struct FixedSystems : ISystemsType { }
public struct LateSystems : ISystemsType { }

// 便捷访问的别名
public abstract class GameSys : W.Systems<GameSystems> { }
public abstract class FixedSys : W.Systems<FixedSystems> { }
public abstract class LateSys : W.Systems<LateSystems> { }
```

___

## ISystem

所有系统的统一接口。只实现需要的方法 — 其余不会被调用：

```csharp
public interface ISystem {
    // 在 Systems.Initialize() 时调用一次
    void Init() { }

    // 在 Systems.Update() 时每帧调用
    void Update() { }

    // 在每次 Update() 前调用 — false 跳过更新
    bool UpdateIsActive() => true;

    // 在 Systems.Destroy() 时调用一次
    void Destroy() { }

    // 快照序列化钩子 — 重写 Guid() 以使该系统加入快照
    Guid? Guid()                                              => null;
    byte  Version()                                            => 0;
    void  Write(ref BinaryPackWriter writer)                   {}
    void  Read(ref BinaryPackReader reader, byte version)      {}
}
```

{: .importantzh }
不要留下空的方法实现。如果不需要某个方法 — 不要实现它。未实现的方法通过反射检测，不会被调用。

#### 系统示例：
```csharp
// 仅 Update 系统
public struct MoveSystem : ISystem {
    public void Update() {
        W.Query().For(static (ref Position pos, in Velocity vel) => {
            pos.Value += vel.Value;
        });
    }
}

// 带初始化和销毁的系统
public struct AudioSystem : ISystem {
    public void Init() {
        // 加载音频资源
    }

    public void Update() {
        // 处理声音
    }

    public void Destroy() {
        // 释放资源
    }
}

// 带条件执行的系统
public struct PausableSystem : ISystem {
    public void Update() {
        // 游戏逻辑
    }

    public bool UpdateIsActive() {
        return !W.GetResource<GameState>().IsPaused;
    }
}
```

___

## 生命周期

```
Create() → Add() → Initialize() → Update() 循环 → Destroy()
```

```csharp
// 1. 创建系统组（baseSize — 初始数组容量，snapshotGuid — 快照中的组标识）
GameSys.Create(baseSize: 64);
// 或在重命名后保持快照稳定性时使用显式组 Guid：
// GameSys.Create(baseSize: 64, snapshotGuid: new("…stable-pipeline-guid…"));

// 2. 注册系统（order 决定执行顺序）
GameSys.Add(new InputSystem(), order: -10)
    .Add(new MoveSystem(), order: 0)
    .Add(new RenderSystem(), order: 10);

// 3. 初始化 — 按 order 排序，调用所有系统的 Init()
GameSys.Initialize();

// 4. 游戏循环 — 每帧调用 Update()
while (gameIsRunning) {
    GameSys.Update();
}

// 5. 销毁 — 调用所有系统的 Destroy()，重置状态
GameSys.Destroy();
```

___

## 注册

所有系统通过单一 `Add<T>()` 方法注册：

```csharp
// 基本注册（order 默认为 0）
GameSys.Add(new MoveSystem());

// 指定顺序（越小越早）
GameSys.Add(new InputSystem(), order: -10)    // 最先执行
    .Add(new PhysicsSystem(), order: 0)       // 然后物理
    .Add(new RenderSystem(), order: 10);      // 渲染最后

// 相同 order 的系统按注册顺序执行
GameSys.Add(new SystemA(), order: 0)  // order=0 中的第一个
    .Add(new SystemB(), order: 0);    // order=0 中的第二个
```

___

## 条件执行

`UpdateIsActive()` 方法允许在当前帧跳过系统更新：

```csharp
public struct GameplaySystem : ISystem {
    public void Update() {
        // 仅在游戏未暂停时运行的逻辑
    }

    public bool UpdateIsActive() {
        return !W.GetResource<GameState>().IsPaused;
    }
}

public struct TutorialSystem : ISystem {
    public void Update() {
        // 教程逻辑
    }

    public bool UpdateIsActive() {
        return W.GetResource<PlayerProgress>().IsFirstPlay;
    }
}
```

___

## 多个系统组

不同的 `ISystemsType` 类型创建具有独立生命周期的独立组：

```csharp
public struct GameSystems : ISystemsType { }
public struct FixedSystems : ISystemsType { }
public abstract class GameSys : W.Systems<GameSystems> { }
public abstract class FixedSys : W.Systems<FixedSystems> { }

// 设置
GameSys.Create();
GameSys.Add(new InputSystem())
    .Add(new RenderSystem());
GameSys.Initialize();

FixedSys.Create();
FixedSys.Add(new PhysicsSystem())
    .Add(new CollisionSystem());
FixedSys.Initialize();

// 游戏循环
while (gameIsRunning) {
    GameSys.Update();           // 每帧

    while (fixedTimeAccumulated) {
        FixedSys.Update();      // 固定时间步长
    }
}

GameSys.Destroy();
FixedSys.Destroy();
```

___

## 完整示例

```csharp
// 系统类型
public struct GameSystems : ISystemsType { }

// 系统
public struct InputSystem : ISystem {
    public void Update() {
        // 读取输入
    }
}

public struct MoveSystem : ISystem {
    public void Update() {
        W.Query().For(static (ref Position pos, in Velocity vel) => {
            pos.Value += vel.Value;
        });
    }
}

public struct DamageSystem : ISystem {
    private EventReceiver<WT, OnDamage> _receiver;

    public void Init() {
        _receiver = W.RegisterEventReceiver<OnDamage>();
    }

    public void Update() {
        foreach (var e in _receiver) {
            if (e.Value.Target.TryUnpack<WT>(out var target)) {
                ref var health = ref target.Ref<Health>();
                health.Current -= e.Value.Amount;
            }
        }
    }

    public void Destroy() {
        W.DeleteEventReceiver(ref _receiver);
    }
}

// 启动
W.Create(WorldConfig.Default());
// ... 注册类型 ...
W.Initialize();

GameSys.Create();
GameSys.Add(new InputSystem(), order: -10)
    .Add(new MoveSystem(), order: 0)
    .Add(new DamageSystem(), order: 5);
GameSys.Initialize();

while (gameIsRunning) {
    GameSys.Update();
}

GameSys.Destroy();
W.Destroy();
```

___

## 快照序列化

`ISystem` 提供四个可选的默认实现方法（`Guid?`、`Version`、`Write`、`Read`）— 与 `IResource` 形式相同。重写 `Guid()` 以使系统实例加入快照序列化：

```csharp
public class SpawnerSystem : ISystem {
    private int _nextId;

    public Guid? Guid() => new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    public byte  Version() => 1;

    public void Update() { /* ... */ }

    public void Write(ref BinaryPackWriter writer)              => writer.WriteInt(_nextId);
    public void Read(ref BinaryPackReader reader, byte version) => _nextId = reader.ReadInt();
}
```

验证在 `Add<TSystem>` 时执行：

- 没有 `Guid` 的系统会被静默排除在快照之外。
- 任何声明 `Guid` 的系统必须同时重写 `Write` 和 `Read`，与布局无关（系统实例以装箱形式存储在 `SystemData` 中，因此 unmanaged 快速路径不适用）。缺少它们会抛出 `StaticEcsException`。
- 同一 `Systems<TSystemsType>` 组内的重复 `Guid` 在 DEBUG 模式下断言。

每个 `Systems<TSystemsType>.Create` 将其组注册到世界的快照注册表中；组 `Guid` 默认为 `typeof(TSystemsType).GuidFromAQN()`，可通过可选的 `snapshotGuid` 参数覆盖。`WorldSnapshot` 自动为每个组写一个段（其作用域内的资源 + 每个声明 `Guid` 的系统）；加载时未注册的组 `Guid` 段会被静默跳过。

独立 API 镜像 `Create/LoadEventsSnapshot`：

```csharp
// 保存
byte[] snapshot = W.Serializer.CreateSystemsSnapshot();
W.Serializer.CreateSystemsSnapshot("systems.bin", gzip: true);

// 加载
W.Serializer.LoadSystemsSnapshot(snapshot);
W.Serializer.LoadSystemsSnapshot("systems.bin", gzip: true);
```

完整的格式和迁移细节：参见 [序列化 → 系统序列化](./serialization.md)。


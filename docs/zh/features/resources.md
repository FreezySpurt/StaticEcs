---
title: 资源
parent: 功能
nav_order: 11
---

## Resources
资源是 DI 的替代方案 — 一种简单的机制，用于存储和传递用户数据和服务到系统和其他方法中
- 资源是世界级单例：不属于任何特定实体的共享状态
- 适用于配置、时间/增量时间、输入状态、资产缓存、服务引用
- 两种变体：**单例**（每种类型一个）和 **命名的**（每种类型多个，通过字符串键区分）
- 在 `Created` 和 `Initialized` 世界阶段均可使用
- 每种资源类型**必须**实现标记接口 `IResource`

___

## 单例资源

单例资源在每个世界中存储给定类型的唯一一个实例。
内部使用静态泛型存储 — 访问复杂度 O(1)，无字典开销。

#### 设置资源：
```csharp
// 用户类和服务 — 必须实现 IResource
public class GameConfig : IResource { public float Gravity; }
public class InputState : IResource { public Vector2 MousePos; }

// 在世界中设置资源
// 默认 clearOnDestroy = true — 资源将在 World.Destroy() 时自动清除
W.SetResource(new GameConfig { Gravity = 9.81f });
W.SetResource(new InputState(), clearOnDestroy: false); // 在世界重建后保留

// 对同一类型再次调用 SetResource 时，值会被覆盖而不会报错
W.SetResource(new GameConfig { Gravity = 4.0f }); // 覆盖之前的值
```

{: .importantzh }
`clearOnDestroy` 参数仅在首次注册时生效。替换已有资源时保留原始的 `clearOnDestroy` 设置。

#### 基本操作：
```csharp
// 检查给定类型的资源是否已注册
bool has = W.HasResource<GameConfig>();

// 获取资源值的可变 ref 引用 — 修改直接写入存储
ref var config = ref W.GetResource<GameConfig>();
config.Gravity = 11.0f; // 就地修改，无需调用 setter

// 从世界中移除资源
W.RemoveResource<GameConfig>();

// Resource<T> — 零开销的 readonly 结构体句柄，用于频繁访问（无需初始化）
W.Resource<GameConfig> configHandle;
bool registered = configHandle.IsRegistered;
ref var cfg = ref configHandle.Value;
configHandle.Set(new GameConfig { Gravity = 9.81f }); // 通过句柄注册 / 替换
configHandle.Remove();                                 // 通过句柄移除
```

___

## 命名资源

命名资源允许存储同一类型的多个实例，通过字符串键区分。
内部存储在 `Dictionary<string, object>` 中，使用类型安全的 `Box<T>` 包装器。

#### 设置命名资源：
```csharp
// 使用不同的键设置相同类型的命名资源
W.SetResource("player_config", new GameConfig { Gravity = 9.81f });
W.SetResource("moon_config", new GameConfig { Gravity = 1.62f });

// 对已有键再次调用 SetResource 时，值会被覆盖而不会报错
W.SetResource("player_config", new GameConfig { Gravity = 10.0f }); // 覆盖
```

#### 基本操作：
```csharp
// 检查给定键的命名资源是否存在
bool has = W.HasResource<GameConfig>("player_config");

// 获取命名资源值的可变 ref 引用
ref var config = ref W.GetResource<GameConfig>("player_config");
config.Gravity = 5.0f;

// 通过键移除命名资源
W.RemoveResource("player_config");

// NamedResource<T> — 结构体句柄，在首次访问后缓存内部引用
// 创建绑定到键的句柄（不会注册资源）
var moonConfig = new W.NamedResource<GameConfig>("moon_config");
bool registered = moonConfig.IsRegistered;  // 始终执行字典查找，不使用缓存
ref var cfg = ref moonConfig.Value;          // 首次调用从字典解析并缓存；后续调用为 O(1)
moonConfig.Set(new GameConfig { Gravity = 1.62f }); // 通过绑定的键注册 / 替换
moonConfig.Remove();                                // 通过绑定的键移除（同时丢弃缓存）
// 当资源被移除或世界被销毁时，缓存会自动失效
```

{: .warningzh }
`NamedResource<T>` 是一个可变结构体，在首次访问 `Value` 时缓存内部引用。
**不要**将其存储在 `readonly` 字段中，也不要在首次使用后按值传递 — C# 编译器
会创建防御性副本，丢弃缓存，导致每次访问都执行字典查找。
请存储在非 readonly 字段或局部变量中。

___

## 生命周期

```csharp
W.Create(WorldConfig.Default());

// Create 之后即可设置资源（无需等待 Initialize）
W.SetResource(new GameConfig { Gravity = 9.81f });
W.SetResource("debug_flags", new DebugFlags(), clearOnDestroy: false);

W.Initialize();

// 在 Initialized 阶段资源仍然可用
ref var config = ref W.GetResource<GameConfig>();

// Destroy 时：clearOnDestroy=true 的资源自动清除
// clearOnDestroy=false 的资源保留，在下一个 Create+Initialize 周期后仍可使用
W.Destroy();
```

___

## 系统组作用域的资源

`Resource<T>` 和 `NamedResource<T>` 也存在于系统流水线层级。每个 `World<TWorld>.Systems<TSystemsType>` 拥有独立的资源存储，与世界级资源以及其他系统组互相隔离。这些资源的生命周期绑定到系统流水线：它们在 `Systems<TSystemsType>.Destroy()` 时被清理，而不是在 `World<TWorld>.Destroy()` 时。

当某状态逻辑上属于某个具体的系统组（例如 `FixedSys` 的固定步长累加器、`RenderSys` 的仅渲染帧缓冲）且不应泄漏到世界级资源或其他流水线时，请使用它们。

#### 公共 API

`Systems<TSystemsType>` 镜像了与世界相同的方法集 — 仅存储作用域不同：

```csharp
public struct FixedSystems : ISystemsType { }
public abstract class FixedSys : W.Systems<FixedSystems> { }

public struct FixedTime : IResource { public float Accumulator; public float Step; }

// FixedSys 作用域的单例资源
FixedSys.SetResource(new FixedTime { Step = 1f / 60f });
ref var time = ref FixedSys.GetResource<FixedTime>();
bool has = FixedSys.HasResource<FixedTime>();
FixedSys.RemoveResource<FixedTime>();

// FixedSys 作用域的命名资源
FixedSys.SetResource("solver_a", new SolverState());
ref var solver = ref FixedSys.GetResource<SolverState>("solver_a");
FixedSys.RemoveResource("solver_a");
```

#### 句柄结构体

`World<TWorld>.Systems<TSystemsType>.Resource<T>` 与 `World<TWorld>.Systems<TSystemsType>.NamedResource<T>` 镜像世界级句柄，直接访问系统组作用域的存储：

```csharp
public struct PhysicsSystem : ISystem {
    private FixedSys.Resource<FixedTime> _time;
    private FixedSys.NamedResource<SolverState> _solver = new("solver_a");

    public void Update() {
        ref var time = ref _time.Value;          // 零成本句柄，无需查找
        ref var solver = ref _solver.Value;      // 首次访问执行字典查找，之后使用缓存
        // ...
    }
}
```

两种句柄还提供 `Set(value, clearOnDestroy)` 和 `Remove()` 方法 — 与世界或系统流水线相同的注册/移除 API，只是直接在句柄上调用（资源类型 / 键直接取自句柄本身）。

`NamedResource<T>` 的缓存警告同样适用：不要将这些句柄存储在 `readonly` 字段中，也不要在首次访问 `Value` 之后按值传递。

#### 生命周期

```csharp
FixedSys.Create();

// Create 之后即可设置资源
FixedSys.SetResource(new FixedTime { Step = 1f / 60f });

FixedSys.Add(new PhysicsSystem());
FixedSys.Initialize();

// ... 游戏循环 ...

// Destroy 时：所有 clearOnDestroy=true 的 FixedSys 资源都被清除
// 与 W.Destroy() 互不影响
FixedSys.Destroy();
```

不同的 `ISystemsType` 类型（如 `FixedSys` 与 `RenderSys`）的资源存储完全独立；世界级资源与任何系统流水线之间也是如此。

___

## 快照序列化

`IResource` 提供四个可选的默认实现方法。重写 `Guid()` 以使资源自动加入快照序列化：

```csharp
public interface IResource {
    public Guid? Guid()                                              => null;  // null → 不序列化
    public byte  Version()                                            => 0;
    public void  Write(ref BinaryPackWriter writer)                   {}
    public void  Read(ref BinaryPackReader reader, byte version)      {}
}
```

- **Unmanaged struct（无引用）**：不需要 `Write`/`Read` — 框架通过 `Unsafe` 复制原始内存。
- **非 unmanaged** 类型：必须同时提供 `Write` 和 `Read` — 否则 `SetResource` 抛出 `StaticEcsException`。
- 同样的规则适用于世界级资源和 `Systems<TSystemsType>` 级资源、singleton 和 named。

完整的序列化细节（格式选择、版本迁移、独立的 `CreateResourcesSnapshot` / `LoadResourcesSnapshot` API）：参见 [序列化 → 资源序列化](./serialization.md)。


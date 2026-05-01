---
title: 事件
parent: 功能
nav_order: 13
---

## Events
事件是系统之间或用户服务之间交换信息的机制
- 以带有 `IEvent` 标记接口的用户自定义数据结构体表示
- "发送者 → 多个接收者"模型，具有自动生命周期管理
- 每个接收者拥有独立的读取游标
- 当所有接收者都读取了事件或事件被抑制时，事件会自动删除

#### 示例:
```csharp
public struct WeatherChanged : IEvent {
    public WeatherType WeatherType;
}

public struct OnDamage : IEvent {
    public float Amount;
    public EntityGID Target;
}
```

___

{: .importantzh }
事件类型注册在 `Created` 和 `Initialized` 阶段均可用

```csharp
W.Create(WorldConfig.Default());
//...
// 简单注册
W.Types()
    .Event<WeatherChanged>()
    .Event<OnDamage>();

// 配置通过在事件结构体上实现 IEventConfig<T> 提供
// （参见下方示例）
//...
W.Initialize();
```

{: .notezh }
要提供配置，请在事件结构体上实现 `IEventConfig<T>` 接口。手动注册和 `RegisterAll()` 都会自动使用它：

```csharp
public struct WeatherChanged : IEvent, IEventConfig<WeatherChanged> {
    public WeatherType WeatherType;
    public EventTypeConfig<WeatherChanged> Config() => new(
        guid: new Guid("..."),   // 序列化的稳定标识符（默认 — 从类型名称自动计算）
        version: 1               // 数据模式版本，用于迁移（默认 — 0）
    );
}
```

___

#### 发送事件:
```csharp
// 发送带数据的事件
// 如果事件已添加到缓冲区则返回 true，如果没有注册的监听者则返回 false
bool sent = W.SendEvent(new WeatherChanged { WeatherType = WeatherType.Sunny });

// 发送默认值的事件
bool sent = W.SendEvent<OnDamage>();
```

{: .warningzh }
如果没有注册的监听者，`SendEvent` 返回 `false` 且事件**不会被存储**。请在发送事件前注册监听者。

___

#### 接收事件:
```csharp
// 创建监听者 — 每个监听者拥有独立的读取游标
var weatherReceiver = W.RegisterEventReceiver<WeatherChanged>();

// 发送事件
W.SendEvent(new WeatherChanged { WeatherType = WeatherType.Sunny });
W.SendEvent(new WeatherChanged { WeatherType = WeatherType.Rainy });

// 通过 foreach 读取事件
// 迭代后，事件对此监听者标记为已读
foreach (var e in weatherReceiver) {
    ref var data = ref e.Value; // ref 访问事件数据
    Console.WriteLine(data.WeatherType);
}

// 迭代时的附加事件信息
foreach (var e in weatherReceiver) {
    // 如果此监听者是最后一个读取此事件的，则为 true
    //（事件将在读取后被删除）
    bool last = e.IsLastReading();

    // 尚未读取此事件的监听者数量（不包括当前监听者）
    int remaining = e.UnreadCount();

    // 抑制事件 — 立即为所有剩余监听者删除该事件
    e.Suppress();
}
```

___

#### 监听者管理:
```csharp
// 通过委托读取所有事件；返回委托被调用的次数
int handled = weatherReceiver.ReadAll(static (Event<WeatherChanged> e) => {
    Console.WriteLine(e.Value.WeatherType);
});

// 抑制此监听者的所有未读事件
// 事件被删除，其他监听者将无法再读取它们
// 返回此次调用实际抑制的事件数量
int suppressed = weatherReceiver.SuppressAll();

// 将所有事件标记为已读但不处理
// 事件不会被删除 — 其他监听者仍然可以读取它们
// 返回此次调用实际标记为已读的事件数量
int marked = weatherReceiver.MarkAsReadAll();

// 删除监听者
W.DeleteEventReceiver(ref weatherReceiver);
```

`ReadAll`、`MarkAsReadAll` 和 `SuppressAll` 返回当前监听者在此次调用中实际处理的事件数量。
已被其他监听者抑制（或先前已被所有其他监听者完全消费）的事件会被静默跳过 —— 从当前监听者
的角度来看，它们「不存在」，因此**不会**计入返回值。具体而言：

- `ReadAll(action)` —— `action` 被调用的次数。
- `MarkAsReadAll()` —— 未读计数实际被递减的事件数量。
- `SuppressAll()` —— 未读计数原本非零、并在此次调用中被清零的事件数量。

___

#### Peek —— 不消费的检查:

`Peek()` 返回一个迭代器，遍历该监听者所有未读事件**而不推进游标，也不递减 `UnreadReceiversCount`**。
foreach 退出后，监听者状态不变 —— 重复 `foreach (var e in receiver.Peek())` 会返回相同的事件。

适用于多遍处理、诊断、不带副作用的「演练」处理。

```csharp
foreach (var e in weatherReceiver.Peek()) {
    Console.WriteLine(e.Value.WeatherType);
    // 数据可通过 e.Value 访问，但事件不会被标记为已读
}
// 之后的常规 foreach 仍然能看到相同的事件并正常消费：
foreach (var e in weatherReceiver) { ... }
```

___

#### LastOnly —— 仅当本监听者是最后读取者时消费:

`LastOnly()` 返回一个迭代器，从监听者游标开始向前遍历所有未读事件，**仅 yield 那些本监听者
作为最后一个未读取读者的事件**（等价于 `IsLastReading() == true`）。被 yield 的事件会自动
标记为已消费（递减 + 清除 mask 位），如同常规 foreach。其他监听者尚未读取的事件
（`UnreadCount > 1`）会**跳过且不修改其状态** —— 它们在后续遍历中仍然可达。监听者游标只在
连续的"已完成"事件前缀上推进；一旦遍历越过未处理的事件，游标停止前进，但迭代器继续向前扫描，
寻找本监听者已经是最后读者的更靠后位置。

这是「在所有其他监听者都做出反应后恰好执行一次动作」模式的自然表达，与单帧内的系统执行
顺序无关。

{: .warningzh }
每种事件类型只能有**一个**监听者使用 `LastOnly()` —— 两个会互相等待，事件将永远挂起。
这是用户的责任，框架不会验证。

示例：实体死亡后销毁。当实体死亡时，发送携带其 `EntityGID` 的 `DeadEvent`，多个系统需要在
实体被物理销毁**之前**读取它（生成战利品、给予经验、播放死亡音效）。立即销毁会导致反应系统
无法读取其组件；通过手动 `IsLastReading()` 销毁则要求清理系统保证最后运行 —— 通常无法保证。

使用 `LastOnly()`，清理器只需等待合适的帧：

```csharp
public struct DeadEvent : IEvent { public EntityGID Gid; }

// 反应器以常规方式注册：
EventReceiver<GameWT, DeadEvent> lootReactor = W.RegisterEventReceiver<DeadEvent>();
EventReceiver<GameWT, DeadEvent> xpReactor   = W.RegisterEventReceiver<DeadEvent>();
// 清理器 —— 常规注册，但将使用 LastOnly()：
EventReceiver<GameWT, DeadEvent> deadCleaner = W.RegisterEventReceiver<DeadEvent>();

// 死亡时 —— 发送事件，实体暂时保持存活：
W.SendEvent(new DeadEvent { Gid = entity.Gid() });

// 反应系统（任何顺序，常规 foreach）：
foreach (var e in lootReactor) {
    if (e.Value.Gid.TryUnpack(out var entity)) SpawnLoot(entity.Read<Position>());
}
foreach (var e in xpReactor) {
    if (e.Value.Gid.TryUnpack(out var entity)) GiveXp(entity.Read<XpReward>().Amount);
}

// 清理系统（管线中任何位置）：
foreach (var e in deadCleaner.LastOnly()) {
    if (e.Value.Gid.TryUnpack(out var entity)) {
        entity.Destroy();   // 安全：所有其他监听者已消费
    }
    // 无需 MarkAsRead —— 迭代器自动标记为已消费
}
```

第 N 帧：反应器和清理器以任意顺序运行。仍有反应器未处理的事件其 `UnreadCount > 1` ——
`LastOnly()` 跳过这些事件且不修改其状态（mask 保留，游标不越过）。所有反应器都已处理的事件
被 yield 出来，实体在此销毁。第 N+1 帧：剩余反应器读取其待处理事件，清理器下次遍历会处理
所有此时已就绪的事件。

___

#### 多线程:

{: .warningzh }
发送事件（`SendEvent`）在以下条件下是线程安全的：
- 多个线程可以同时为**同一**事件类型调用 `SendEvent`
- **同时读取和发送**同一事件类型是**禁止的** — 只有在没有同时读取同一类型时，发送才是线程安全的
- 读取同一类型的事件（`foreach`、`ReadAll`）必须在**单个线程**中进行
- 不同的事件类型可以从**不同线程同时**读取，因为每种类型独立存储
- 同一事件类型可以在不同线程中**在不同时间**读取（非同时）

监听者操作（`foreach`、`ReadAll`、`MarkAsReadAll`、`SuppressAll`、创建和删除监听者）**不支持**多线程模式，只能在主线程中执行。

___

#### 事件生命周期:

{: .importantzh }
事件在以下两种情况下自动删除：
1. 所有注册的监听者都已读取该事件
2. 事件被抑制（`Suppress` 或 `SuppressAll`）

所有注册的监听者都必须读取其事件（或调用 `MarkAsReadAll`/`SuppressAll`），否则事件会在内存中累积。

```csharp
// 两个监听者的生命周期示例
var receiverA = W.RegisterEventReceiver<WeatherChanged>();
var receiverB = W.RegisterEventReceiver<WeatherChanged>();

W.SendEvent(new WeatherChanged { WeatherType = WeatherType.Sunny });
// 事件的 UnreadCount = 2

foreach (var e in receiverA) {
    // receiverA 已读取，UnreadCount = 1
}

foreach (var e in receiverB) {
    // receiverB 已读取，UnreadCount = 0 → 事件自动删除
}
```

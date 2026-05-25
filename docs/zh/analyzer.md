---
title: Roslyn 分析器
parent: ZH
nav_order: 6
---

# Roslyn 分析器

StaticEcs 自带一套 Roslyn 分析器与代码修复 (code-fix)，在编译期捕获框架的常见误用。它随 `FFS.StaticEcs` NuGet 包自动附带 —— 无需额外引用、无需手动安装。包内二进制位置：

- `analyzers/dotnet/cs/FFS.StaticEcs.Analyzers.dll` —— 诊断规则
- `analyzers/dotnet/cs/FFS.StaticEcs.Analyzers.CodeFixes.dll` —— 自动代码修复

如果当前编译没有引用 `FFS.StaticEcs`，分析器会自行禁用，因此在任何 solution 中保留它都是安全的。

诊断类别：

- **`FFS.StaticEcs.Correctness`** —— 代码可以编译但语义错误（ref 返回的隐式拷贝、对实体的 use-after-free、查询过滤器矛盾等）。
- **`FFS.StaticEcs.Performance`** —— 触发分配或阻碍运行时优化的模式（`Query.For` 中带闭包的 lambda）。
- **`FFS.StaticEcs.Usage`** —— 风格 / 可读性建议（存在更直接的 API）。

___

## 规则索引

| ID | 类别 | 严重程度 | 标题 | CodeFix |
|---|---|---|---|---|
| [FFSECS0010](#ffsecs0010) | Correctness | Error | ref 返回结果必须以 'ref' 绑定 | 有 |
| [FFSECS0011](#ffsecs0011) | Correctness | Info | `Read<T>()` 结果被绑定为副本 | 有 |
| [FFSECS0012](#ffsecs0012) | Correctness | Info | 来自 StaticEcs 存储的 ref-local 按值传递（原子值类型自动跳过） | 有 |
| [FFSECS0020](#ffsecs0020) | Correctness | Error | StaticEcs 标记接口必须由 struct 实现 | 有 |
| [FFSECS0021](#ffsecs0021) | Correctness | Error | `IMultiComponent` 必须由 struct 实现 | 有 |
| [FFSECS0022](#ffsecs0022) | Correctness | Warning | 非 unmanaged 的 `IMultiComponent` 必须重写 `Write`/`Read` | — |
| [FFSECS0030](#ffsecs0030) | Correctness | Info | `Query.For` lambda 的 `ref` 参数从未被写入 | 有 |
| [FFSECS0031](#ffsecs0031) | Performance | Error | `Query.For` 的 lambda 捕获外部状态 | — |
| [FFSECS0032](#ffsecs0032) | Usage | Info | `IsMatch<TFilter>()` 可替换为 Entity 上的直接方法 | 有 |
| [FFSECS0040](#ffsecs0040) | Correctness | Error | 失效后仍使用对组件的 `ref`/`in` 引用 | — |
| [FFSECS0041](#ffsecs0041) | Correctness | Error | 失效后仍使用 Entity | — |
| [FFSECS0050](#ffsecs0050) | Correctness | Error | 查询过滤器中存在冗余组件 | — |
| [FFSECS0051](#ffsecs0051) | Correctness | Error | 查询过滤器中 `All` 与 `None` 相互矛盾 | — |

___

## 规则详解

### FFSECS0010
**类别：** Correctness · **严重程度：** Error · **CodeFix：** 有

`Entity.Ref/Mut/Add`、`Components<T>.Ref/Mut/Add`、`Resource<T>.Value`、`NamedResource<T>.Value`、`Multi<T>.First/Last/[i]`、`MultiComponentsIterator<T>.Current` 全部按引用返回。把结果绑定到普通 local 会悄无声息地复制组件 —— 后续的修改写入副本而非存储。会在变量声明、值参数、简单赋值、非 ref 返回中触发。

引用类型负载（如 `Resource<MyClass>.Value`）会被静默放行：复制一个引用本身没问题。

#### 触发
```csharp
var pos = entity.Ref<Position>();           // FFSECS0010 —— 隐式拷贝
Consume(entity.Ref<Position>());            // FFSECS0010 —— 在调用边界处拷贝
return entity.Ref<Position>();              // FFSECS0010 —— 返回时拷贝
```

#### 修复
```csharp
ref var pos = ref entity.Ref<Position>();   // ok —— ref 绑定
entity.Ref<Position>().Value = 5;           // ok —— 直接通过 ref 写入
Consume(ref entity.Ref<Position>());        // ok —— 按 ref 传递
```

#### 显式选择拷贝：`*RO` 对等成员
当你确实希望从 `Resource<T>` / `NamedResource<T>` / `Multi<T>` / `MultiComponentsIterator<T>` 得到一个快照（拷贝）时，请使用专门的 `*RO` 成员，而不是把可变 ref 返回绑定到普通局部变量。它们返回 `ref readonly T`，并被**故意**排除在分析器的允许列表之外 —— `RO` 后缀在源代码中传达了意图。

| 可变（会被诊断） | 只读对等成员 |
|---|---|
| `Resource<T>.Value` | `Resource<T>.ValueRO` |
| `NamedResource<T>.Value` | `NamedResource<T>.ValueRO` |
| `Multi<T>.First()` | `Multi<T>.GetFirst()` |
| `Multi<T>.Last()` | `Multi<T>.GetLast()` |
| `Multi<T>[idx]` | `Multi<T>.Get(idx)` |
| `MultiComponentsIterator<T>.Current` | `MultiComponentsIterator<T>.CurrentRO` |

```csharp
var snapshot = timer.ValueRO;               // ok —— 显式 RO 选择，无诊断
ref readonly var refSnap = ref multi.GetFirst();
```

对于 `Entity` 与 `Components<T>`，快照路径是 `Read<T>()` / `Read(Entity)`（FFSECS0011 Info 已经提示这条路径）。

CodeFix 提供一键操作「Switch to '`*RO`' (intentional copy)」。

___

### FFSECS0011
**类别：** Correctness · **严重程度：** Info · **CodeFix：** 有

`Entity.Read<T>()` 与 `Components<T>.Read(Entity)` 返回 `ref readonly T`。绑定到非 ref-readonly local 会复制 —— 对大型组件不可取。Severity 为 Info，仅是 IDE 提示，不会破坏构建。可通过 `.editorconfig` 中的 `dotnet_diagnostic.FFSECS0011.severity = none` 关闭。

#### 触发
```csharp
var snapshot = entity.Read<Position>();     // FFSECS0011 —— 复制
```

#### 修复
```csharp
ref readonly var snap = ref entity.Read<Position>();
Consume(in entity.Read<Position>());        // ok —— 按 'in' 传递
```

___

### FFSECS0012
**类别：** Correctness · **严重程度：** Info · **CodeFix：** 有

绑定到 StaticEcs 存储源的 `ref` / `ref readonly` local 以值方式传递。这会在调用边界处拷贝组件 —— 被调用者修改的是副本。该提示属于启发式：分析器无法区分意外丢失 ref 语义与有意传递当前值，因此以 Info 呈现；如需全局静默，在 `.editorconfig` 中设置 `dotnet_diagnostic.FFSECS0012.severity = none`。

原子值类型会自动排除 —— 它们没有可通过拷贝丢失的内部状态：CLR 原生类型 (`bool`/`int`/`float`/...)、`enum`、以及引用类型 (local 保存指针；拷贝指针仍命中同一个堆对象)。

#### 触发
```csharp
ref var hp = ref entity.Ref<Health>();      // Health — 多字段 struct
Consume(hp);                                // FFSECS0012 —— 拷贝
```

#### 修复
```csharp
Consume(ref hp);                            // ok
Consume(in hp);                             // ok —— 'in' 接受 ref-local
ref var id = ref entity.Add<PlayerId>().Value;  // .Value 是 ushort，原子值
SetBehaviour(id);                           // ok —— 原生类型不会被跟踪
ref var st = ref entity.Ref<C>().Status;    // Status 是 enum
M(st);                                      // ok —— enum 不会被跟踪
```

___

### FFSECS0020
**类别：** Correctness · **严重程度：** Error · **CodeFix：** 有

`class` 实现了任意 StaticEcs 标记接口（`IComponent`、`ITag`、`IEvent`、`ILinkType`、`ILinksType`、`IEntityType`、`IWorldType`）会破坏泛型派发：StaticEcs 的公共 API 都带 `where T : struct` 约束，并且基于反射的 `RegisterAll` 会跳过 class 实现。

#### 触发
```csharp
public class Health : IComponent { public int Value; }   // FFSECS0020
```

#### 修复
```csharp
public struct Health : IComponent { public int Value; }  // ok
```

___

### FFSECS0021
**类别：** Correctness · **严重程度：** Error · **CodeFix：** 有

与 FFSECS0020 相同，但专门针对 `IMultiComponent`。

___

### FFSECS0022
**类别：** Correctness · **严重程度：** Warning · **CodeFix：** —

实现 `IMultiComponent` 的 struct 如果**不是** `unmanaged`（包含 `string`、数组、委托等托管字段），必须重写 `Write(ref BinaryPackWriter)` 与 `Read(ref BinaryPackReader)`。接口默认实现是空的 —— 不重写则快照会对托管载荷静默写入空数据。

#### 触发
```csharp
public struct Inventory : IMultiComponent { public string Owner; }  // FFSECS0022
```

#### 修复
```csharp
public struct Inventory : IMultiComponent {
    public string Owner;
    public void Write(ref BinaryPackWriter w) { w.WriteString(Owner); }
    public void Read(ref BinaryPackReader r) { Owner = r.ReadString(); }
}
```

Unmanaged struct（`int`/`float`/`Nullable<int>` 等）由存储以 bulk-copy 处理，无需重写。

___

### FFSECS0030
**类别：** Correctness · **严重程度：** Info · **CodeFix：** 有

`Query.For` lambda 的 `ref T` 参数从未被写入。在开启变更跟踪的运行时，对 `ref` 参数的任何访问都会把组件标记为已修改 —— 即便函数体只读不写。换用 `in T` 重载以明确只读意图并跳过变更标记。

#### 触发
```csharp
W.Query().For((ref Health h) => { Console.WriteLine(h.Value); }); // FFSECS0030
```

#### 修复
```csharp
W.Query().For((in Health h) => { Console.WriteLine(h.Value); });
```

___

### FFSECS0031
**类别：** Performance · **严重程度：** Error · **CodeFix：** —

传给 `Query.For`（或任何 fluent `.For(...)`）的 lambda 如果捕获了外部状态（`this`、方法局部变量、实例字段/属性/方法），每次执行查询都会分配闭包。替代方案：

- `static` lambda + `userData` 重载（`For<TData>(userData, static (ref TData d, …) => …)`）。
- 实现 `W.IQuery.Write<…>` / `W.IQuery.Read<…>` 的 `struct`。
- `foreach (var entity in W.Query<…>().Entities())` + `ref var` 本地变量。

#### 触发
```csharp
var multiplier = 2;
W.Query().For((ref Health h) => { h.Value *= multiplier; });    // FFSECS0031
```

#### 修复
```csharp
var multiplier = 2;
W.Query().For(multiplier, static (ref int m, ref Health h) => { h.Value *= m; });
```

method-group 指向非静态实例方法的引用也会被检测到（它们会捕获 `this`）。

___

### FFSECS0032
**类别：** Usage · **严重程度：** Info · **CodeFix：** 有

`Entity.IsMatch<TFilter>()` 对任意 `IQueryFilter` 都能用，但对于简单形态（`All<…>`、`Any<…>`、`None<…>` 以及它们的 `*WithDisabled`/`*OnlyDisabled` 变体、`EntityIs<…>`、`EntityIsAny<…>`、`EntityIsNot<…>`），Entity 上有更简短、更能表达意图的直接方法：

| 过滤器 | 等价方法 |
|---|---|
| `All<T..>`（arity 1-3） | `HasEnabled<T..>()` |
| `AllWithDisabled<T..>` | `Has<T..>()` |
| `AllOnlyDisabled<T..>` | `HasDisabled<T..>()` |
| `Any<T..>`（arity 2-3） | `HasEnabledAny<T..>()` |
| `AnyWithDisabled<T..>` | `HasAny<T..>()` |
| `AnyOnlyDisabled<T..>` | `HasDisabledAny<T..>()` |
| `None<T..>` | `!HasEnabled<…>` / `!HasEnabledAny<…>` |
| `NoneWithDisabled<T..>` | `!Has<…>` / `!HasAny<…>` |
| `EntityIs<T>` | `Is<T>()` |
| `EntityIsAny<T..>` | `IsAny<T..>()` |
| `EntityIsNot<T..>` | `IsNot<T..>()` |

#### 触发
```csharp
if (entity.IsMatch<All<Health, Mana>>())  { … }   // FFSECS0032
if (entity.IsMatch<None<Stunned>>())      { … }   // FFSECS0032
```

#### 修复
```csharp
if (entity.HasEnabled<Health, Mana>()) { … }
if (!entity.HasEnabled<Stunned>())     { … }
```

约束检查：`HasEnabled`/`HasEnabledAny`/`HasDisabled`/`HasDisabledAny` 都要求 `T : struct, IComponent, IDisableable`。对于 `All<…>`、`Any<…>`、`None<…>`（它们接受 `IComponentOrTag` —— 允许 tag）只有当每个类型参数同时实现 `IComponent` 和 `IDisableable` 时诊断才触发；否则朴素替换无法编译，规则会静默跳过。`*OnlyDisabled` 过滤器本身已带相同的约束，自动满足检查。

复合过滤器（`And<…>`、`Or<…>`、`Nothing`）以及 arity > 3 不会被建议替换 —— 那里 `IsMatch` 仍是唯一实用入口。

___

### FFSECS0040
**类别：** Correctness · **严重程度：** Error · **CodeFix：** —

对组件的 `ref`/`in` 引用在底层实体失效后变成悬挂引用。跟踪三种模式：

- **`WorldQuery.For` 的 lambda** —— 引用即 lambda 的 `ref`/`in` 组件参数。
- **实现 `IQuery.*` 的 `struct`** —— 引用即 `Invoke` 的 `ref`/`in` 参数。
- **来自 `entity.Ref/Mut/Read/Add(...)` 的 `ref`-locals**。

失效操作：`Destroy`、`MoveTo`、`Unload`（整体 kill）、`Delete<T>`（仅指向 `T` 类型组件的引用）。

#### 触发
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    e.Destroy();
    hp.Value = 0;                       // FFSECS0040 —— hp 指向已释放的存储
});
```

#### 修复
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    var snap = hp.Value;                // 先快照
    e.Destroy();
    Use(snap);                          // ok
});
```

___

### FFSECS0041
**类别：** Correctness · **严重程度：** Error · **CodeFix：** —

与 FFSECS0040 对称，但追踪的是**实体变量本身**，而非对组件的 `ref`/`in` 引用。对某个 local 或参数执行 `Destroy`/`MoveTo`/`Unload` 后，对该变量的任何后续操作（`Has`、`Add`、`IsActual`、…）都会被标出。只允许：

- 直接重新赋值（`entity = W.NewEntity<…>();`）。
- out 参数重绑（`Method(out entity);` 或循环中的 `Method(out var entity)`）。

#### 触发
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
_ = e.Has<Health>();                    // FFSECS0041
```

#### 修复
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
e = W.NewEntity<Default>();             // 重新赋值清除 taint
_ = e.Has<Health>();                    // ok
```

跨条件分支的合并是保守的 —— 只要任一前驱路径让变量失效，汇合点就视为有 taint。

___

### FFSECS0050
**类别：** Correctness · **严重程度：** Error · **CodeFix：** —

某个组件在查询中被引用多次 —— 可能是同种过滤器中的重复（`All`+`All`、`None`+`None`、`Any`+`Any`，含其 `*WithDisabled`/`*OnlyDisabled` 变体），或是过滤器链与 lambda 的 `ref`/`in` 参数之间的重叠，或与 `IQuery` 结构的组件泛型之间的重叠。

#### 触发
```csharp
foreach (var _ in W.Query<All<Health>, All<Health>>().Entities()) { }                       // FFSECS0050
W.Query<All<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0050 —— 过滤器 ↔ lambda
W.Query<All<Health>>().Write<Health>().For<MyWriteFn>();                                    // FFSECS0050 —— 过滤器 ↔ IQuery generic
foreach (var _ in W.Query<All<Health>, AllOnlyDisabled<Health>>().Entities()) { }           // FFSECS0050 —— 基础 + disabled 变体
```

___

### FFSECS0051
**类别：** Correctness · **严重程度：** Error · **CodeFix：** —

同一组件同时出现在 `All<…>` 和 `None<…>` 中 —— 查询结果永远为空。lambda 参数与 `IQuery` 结构泛型隐含的 `All` 贡献也会计入。

#### 触发
```csharp
foreach (var _ in W.Query<All<Health>, None<Health>>().Entities()) { }                       // FFSECS0051
W.Query<None<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0051 —— lambda 隐含 All
```

___

## 关闭诊断

针对单行 / 单块：
```csharp
#pragma warning disable FFSECS0011
var snap = entity.Read<Health>();
#pragma warning restore FFSECS0011
```

针对整个项目（`.editorconfig`）：
```ini
[*.cs]
dotnet_diagnostic.FFSECS0011.severity = none
```

针对构建（`csproj`）：
```xml
<NoWarn>FFSECS0011</NoWarn>
```

___

## 源代码

所有分析器位于 `StaticEcs/Analyzers~/Src/Analyzers/*.cs`；code-fix 位于 `StaticEcs/Analyzers~/CodeFixes/`。规则 ID 集中在 `StaticEcs/Analyzers~/Shared/FFSECSIds.cs`。

---
title: Roslyn-анализатор
parent: RU
nav_order: 6
---

# Roslyn-анализатор

StaticEcs поставляется с набором Roslyn-анализаторов и code-fix'ов, которые ловят типовые ошибки использования фреймворка во время компиляции. Анализатор автоматически прикладывается NuGet-пакетом `FFS.StaticEcs` — никаких дополнительных пакетов и ручной установки не требуется. В пакете бинарники лежат по путям:

- `analyzers/dotnet/cs/FFS.StaticEcs.Analyzers.dll` — диагностические правила
- `analyzers/dotnet/cs/FFS.StaticEcs.Analyzers.CodeFixes.dll` — автоматические исправления

Анализатор сам отключается, если проект не ссылается на `FFS.StaticEcs`, поэтому его безопасно держать в любом solution.

Категории диагностик:

- **`FFS.StaticEcs.Correctness`** — код компилируется, но семантически неверен (молчаливые копии ref-возвратов, обращения к удалённым сущностям, противоречивые фильтры запросов и т. п.).
- **`FFS.StaticEcs.Performance`** — паттерны, которые аллоцируют память или блокируют рантайм-оптимизации (захват замыканием в `Query.For`).
- **`FFS.StaticEcs.Usage`** — стилистические предложения (есть более прямой API).

___

## Список правил

| ID | Категория | Severity | Заголовок | CodeFix |
|---|---|---|---|---|
| [FFSECS0010](#ffsecs0010) | Correctness | Error | Результат ref-возврата должен биндиться через `ref` | да |
| [FFSECS0011](#ffsecs0011) | Correctness | Info | Результат `Read<T>()` биндится в копию | да |
| [FFSECS0012](#ffsecs0012) | Correctness | Info | ref-local над storage передан по значению (атомарно-значимые типы пропускаются) | да |
| [FFSECS0020](#ffsecs0020) | Correctness | Error | Marker-интерфейс StaticEcs должен реализовываться `struct` | да |
| [FFSECS0021](#ffsecs0021) | Correctness | Error | `IMultiComponent` должен реализовываться `struct` | да |
| [FFSECS0022](#ffsecs0022) | Correctness | Warning | Non-unmanaged `IMultiComponent` должен переопределить `Write`/`Read` | — |
| [FFSECS0030](#ffsecs0030) | Correctness | Info | Параметр `ref` лямбды `Query.For` ни разу не записывается | да |
| [FFSECS0031](#ffsecs0031) | Performance | Error | Лямбда в `Query.For` захватывает внешнее состояние | — |
| [FFSECS0032](#ffsecs0032) | Usage | Info | `IsMatch<TFilter>()` можно заменить прямым методом Entity | да |
| [FFSECS0040](#ffsecs0040) | Correctness | Error | Используется `ref`/`in` ссылка на компонент после инвалидации | — |
| [FFSECS0041](#ffsecs0041) | Correctness | Error | Используется сущность после инвалидации | — |
| [FFSECS0050](#ffsecs0050) | Correctness | Error | Дубликат компонента в фильтре запроса | — |
| [FFSECS0051](#ffsecs0051) | Correctness | Error | Противоречие `All` + `None` в фильтре | — |

___

## Правила

### FFSECS0010
**Категория:** Correctness · **Severity:** Error · **CodeFix:** да

`Entity.Ref/Mut/Add`, `Components<T>.Ref/Mut/Add`, `Resource<T>.Value`, `NamedResource<T>.Value`, `Multi<T>.First/Last/[i]`, `MultiComponentsIterator<T>.Current` возвращают по ссылке. Биндинг результата в обычный local молча копирует компонент — последующая мутация пишется в копию, а не в storage. Ловится в объявлении переменной, аргументе по значению, простом присваивании и обычном `return`.

Reference-типы (например `Resource<MyClass>.Value`) исключены: копирование ссылки дёшево и идиоматично.

#### Срабатывает
```csharp
var pos = entity.Ref<Position>();           // FFSECS0010 — молчаливая копия
Consume(entity.Ref<Position>());            // FFSECS0010 — копия на границе вызова
return entity.Ref<Position>();              // FFSECS0010 — копия на возврате
```

#### Без диагностики
```csharp
ref var pos = ref entity.Ref<Position>();   // ok — ref-binding
entity.Ref<Position>().Value = 5;           // ok — прямая запись
Consume(ref entity.Ref<Position>());        // ok — передача по ref
```

#### Явный opt-in на копию: `*RO`-сиблинги
Когда копия (snapshot) — это намеренный выбор для `Resource<T>` / `NamedResource<T>` / `Multi<T>` / `MultiComponentsIterator<T>`, используйте специальные `*RO`-члены вместо биндинга mutable ref-возврата в обычный local. Они возвращают `ref readonly T` и намеренно **не** включены в allow-list анализатора — суффикс `RO` выражает намерение в самом коде.

| Mutable (флагается) | Read-only сиблинг |
|---|---|
| `Resource<T>.Value` | `Resource<T>.ValueRO` |
| `NamedResource<T>.Value` | `NamedResource<T>.ValueRO` |
| `Multi<T>.First()` | `Multi<T>.GetFirst()` |
| `Multi<T>.Last()` | `Multi<T>.GetLast()` |
| `Multi<T>[idx]` | `Multi<T>.Get(idx)` |
| `MultiComponentsIterator<T>.Current` | `MultiComponentsIterator<T>.CurrentRO` |

```csharp
var snapshot = timer.ValueRO;               // ok — explicit RO opt-in, без диагностики
ref readonly var refSnap = ref multi.GetFirst();
```

Для `Entity` и `Components<T>` snapshot-путь — это `Read<T>()` / `Read(Entity)`

Codefix предлагает действие «Switch to '`*RO`' (intentional copy)» в один клик.

___

### FFSECS0011
**Категория:** Correctness · **Severity:** Info · **CodeFix:** да

`Entity.Read<T>()` и `Components<T>.Read(Entity)` возвращают `ref readonly T`. Биндинг в обычный local — копия, нежелательная для больших компонентов. Severity Info: подсказка в IDE, не ломает сборку. Подавить можно через `dotnet_diagnostic.FFSECS0011.severity = none` в `.editorconfig`.

#### Срабатывает
```csharp
var snapshot = entity.Read<Position>();     // FFSECS0011 — копия
```

#### Без диагностики
```csharp
ref readonly var snap = ref entity.Read<Position>();
Consume(in entity.Read<Position>());        // ok — передача по in
```

___

### FFSECS0012
**Категория:** Correctness · **Severity:** Info · **CodeFix:** да

`ref` / `ref readonly` local, привязанный к источнику ref-возврата StaticEcs, передан по значению. Это копирует компонент на границе вызова — callee изменит копию. Подсказка эвристическая: правило не отличает «случайную» потерю ref-семантики от «явной» передачи текущего значения, поэтому surfaces как Info; чтобы скрыть глобально — `dotnet_diagnostic.FFSECS0012.severity = none` в `.editorconfig`.

Атомарно-значимые типы автоматически исключаются — у них нет внутреннего state-а, который мог бы быть «потерян» при копии: примитивы (`bool`/`int`/`float`/...), `enum`, ссылочные типы (локал хранит ссылку — копия указателя бьёт по тому же heap-объекту).

#### Срабатывает
```csharp
ref var hp = ref entity.Ref<Health>();      // Health — multi-field struct
Consume(hp);                                // FFSECS0012 — копия
```

#### Без диагностики
```csharp
Consume(ref hp);                            // ok
Consume(in hp);                             // ok — 'in' принимает ref-local
ref var id = ref entity.Add<PlayerId>().Value;  // .Value — ushort, атомарно
SetBehaviour(id);                           // ok — primitive не трекается
ref var st = ref entity.Ref<C>().Status;    // Status — enum
M(st);                                      // ok — enum не трекается
```

___

### FFSECS0020
**Категория:** Correctness · **Severity:** Error · **CodeFix:** да

`class`, реализующий любой marker-интерфейс StaticEcs (`IComponent`, `ITag`, `IEvent`, `ILinkType`, `ILinksType`, `IEntityType`, `IWorldType`), ломает generic-диспетчеризацию: весь публичный API StaticEcs объявлен с `where T : struct`, а reflection-based `RegisterAll` пропустит class.

#### Срабатывает
```csharp
public class Health : IComponent { public int Value; }   // FFSECS0020
```

#### Без диагностики
```csharp
public struct Health : IComponent { public int Value; }  // ok
```

___

### FFSECS0021
**Категория:** Correctness · **Severity:** Error · **CodeFix:** да

То же, что FFSECS0020, но конкретно для `IMultiComponent`.

___

### FFSECS0022
**Категория:** Correctness · **Severity:** Warning · **CodeFix:** —

`struct`-реализация `IMultiComponent`, которая **не** является `unmanaged` (содержит managed-поля: `string`, массивы, делегаты, …), обязана переопределить оба метода `Write(ref BinaryPackWriter)` и `Read(ref BinaryPackReader)`. Дефолтные реализации интерфейса пустые — без переопределения снапшоты молча сохранят пустые данные для managed-полей.

#### Срабатывает
```csharp
public struct Inventory : IMultiComponent { public string Owner; }  // FFSECS0022
```

#### Без диагностики
```csharp
public struct Inventory : IMultiComponent {
    public string Owner;
    public void Write(ref BinaryPackWriter w) { w.WriteString(Owner); }
    public void Read(ref BinaryPackReader r) { Owner = r.ReadString(); }
}
```

Unmanaged-структуры (`int`/`float`/`Nullable<int>` …) сериализуются bulk-copy и в переопределениях не нуждаются.

___

### FFSECS0030
**Категория:** Correctness · **Severity:** Info · **CodeFix:** да

`ref T`-параметр лямбды `Query.For`, который ни разу не записывается, при включённом change-tracking всё равно помечает компонент изменённым, потому что обращение идёт через `ref`. Замените на `in T` — это явно сигнализирует read-only намерение и пропускает отметку изменения.

#### Срабатывает
```csharp
W.Query().For((ref Health h) => { Console.WriteLine(h.Value); }); // FFSECS0030
```

#### Без диагностики
```csharp
W.Query().For((in Health h) => { Console.WriteLine(h.Value); });
```

___

### FFSECS0031
**Категория:** Performance · **Severity:** Error · **CodeFix:** —

Лямбда, переданная в `Query.For` (или в любой fluent-`.For(...)`) и захватывающая внешнее состояние (`this`, локальную переменную, поле/свойство/метод инстанса), аллоцирует замыкание при каждом вызове запроса. Альтернативы:

- `static`-лямбда + перегрузка с `userData` (`For<TData>(userData, static (ref TData d, …) => …)`).
- `struct`, реализующий `W.IQuery.Write<…>` / `W.IQuery.Read<…>`.
- `foreach (var entity in W.Query<…>().Entities())` + `ref var`-локалы.

#### Срабатывает
```csharp
var multiplier = 2;
W.Query().For((ref Health h) => { h.Value *= multiplier; });    // FFSECS0031
```

#### Без диагностики
```csharp
var multiplier = 2;
W.Query().For(multiplier, static (ref int m, ref Health h) => { h.Value *= m; });
```

Method-group ссылки на нестатический instance-метод тоже ловятся (они захватывают `this`).

___

### FFSECS0032
**Категория:** Usage · **Severity:** Info · **CodeFix:** да

`Entity.IsMatch<TFilter>()` работает для любого `IQueryFilter`, но для простых фильтров (`All<…>`, `Any<…>`, `None<…>`, их `*WithDisabled`/`*OnlyDisabled`-варианты, `EntityIs<…>`, `EntityIsAny<…>`, `EntityIsNot<…>`) у `Entity` есть короткий и понятный прямой метод:

| Фильтр | Эквивалент |
|---|---|
| `All<T..>` (арность 1-3) | `HasEnabled<T..>()` |
| `AllWithDisabled<T..>` | `Has<T..>()` |
| `AllOnlyDisabled<T..>` | `HasDisabled<T..>()` |
| `Any<T..>` (арность 2-3) | `HasEnabledAny<T..>()` |
| `AnyWithDisabled<T..>` | `HasAny<T..>()` |
| `AnyOnlyDisabled<T..>` | `HasDisabledAny<T..>()` |
| `None<T..>` | `!HasEnabled<…>` / `!HasEnabledAny<…>` |
| `NoneWithDisabled<T..>` | `!Has<…>` / `!HasAny<…>` |
| `EntityIs<T>` | `Is<T>()` |
| `EntityIsAny<T..>` | `IsAny<T..>()` |
| `EntityIsNot<T..>` | `IsNot<T..>()` |

#### Срабатывает
```csharp
if (entity.IsMatch<All<Health, Mana>>())  { … }   // FFSECS0032
if (entity.IsMatch<None<Stunned>>())      { … }   // FFSECS0032
```

#### Без диагностики
```csharp
if (entity.HasEnabled<Health, Mana>()) { … }
if (!entity.HasEnabled<Stunned>())     { … }
```

Проверка констрейнтов: `HasEnabled`/`HasEnabledAny`/`HasDisabled`/`HasDisabledAny` требуют `T : struct, IComponent, IDisableable`. Для `All<…>`, `Any<…>`, `None<…>` (которые принимают `IComponentOrTag` — теги разрешены) диагностика срабатывает только когда каждый аргумент типа одновременно `IComponent` и `IDisableable`; иначе наивная замена не скомпилируется, поэтому правило молчит. У `*OnlyDisabled` фильтров такие же констрейнты уже зашиты — проверка для них автоматическая.

Композитные фильтры (`And<…>`, `Or<…>`, `Nothing`) и арность > 3 не предлагаются — там `IsMatch` остаётся единственным практичным API.

___

### FFSECS0040
**Категория:** Correctness · **Severity:** Error · **CodeFix:** —

`ref`/`in` ссылки на компонент становятся невалидными после инвалидации соответствующей сущности. Отслеживаются три паттерна:

- **Лямбда в `WorldQuery.For`** — ссылки это `ref`/`in`-параметры лямбды.
- **`struct`, реализующий `IQuery.*`** — ссылки это `ref`/`in`-параметры метода `Invoke`.
- **`ref`-локалы из `entity.Ref/Mut/Read/Add(...)`**.

Инвалидаторы: `Destroy`, `MoveTo`, `Unload` (полный kill), `Delete<T>` (только ссылки на компонент типа `T`).

#### Срабатывает
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    e.Destroy();
    hp.Value = 0;                       // FFSECS0040 — hp указывает в освобождённое место
});
```

#### Без диагностики
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    var snap = hp.Value;                // снимок до Destroy
    e.Destroy();
    Use(snap);                          // ok
});
```

___

### FFSECS0041
**Категория:** Correctness · **Severity:** Error · **CodeFix:** —

Двойник FFSECS0040, но отслеживает не ссылки на компоненты, а **саму переменную-сущность**. После `Destroy`/`MoveTo`/`Unload` по локалу или параметру любая дальнейшая операция на этой переменной (`Has`, `Add`, `IsActual`, …) флагается. Разрешены только:

- Прямое переприсваивание (`entity = W.NewEntity<…>();`).
- Out-параметр (`Method(out entity);` или `Method(out var entity)` внутри цикла).

#### Срабатывает
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
_ = e.Has<Health>();                    // FFSECS0041
```

#### Без диагностики
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
e = W.NewEntity<Default>();             // reassignment снимает taint
_ = e.Has<Health>();                    // ok
```

Объединение в условных ветках консервативное: если хоть один путь оставляет переменную невалидной, в точке слияния taint остаётся.

___

### FFSECS0050
**Категория:** Correctness · **Severity:** Error · **CodeFix:** —

Компонент упомянут в запросе более одного раза — либо дубликат внутри фильтров одного типа (`All`+`All`, `None`+`None`, `Any`+`Any`, в т.ч. `*WithDisabled`/`*OnlyDisabled`-варианты), либо пересечение цепочки фильтров с `ref`/`in`-параметром лямбды или с component-generic-параметром `IQuery`-структуры.

#### Срабатывает
```csharp
foreach (var _ in W.Query<All<Health>, All<Health>>().Entities()) { }                       // FFSECS0050
W.Query<All<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0050 — фильтр ↔ лямбда
W.Query<All<Health>>().Write<Health>().For<MyWriteFn>();                                    // FFSECS0050 — фильтр ↔ IQuery generic
foreach (var _ in W.Query<All<Health>, AllOnlyDisabled<Health>>().Entities()) { }           // FFSECS0050 — базовый + disabled-вариант
```

___

### FFSECS0051
**Категория:** Correctness · **Severity:** Error · **CodeFix:** —

В запросе один и тот же компонент находится одновременно в `All<…>` и `None<…>` — результат запроса всегда пуст. Неявный вклад `All` от параметров лямбды и component-generic `IQuery`-структуры тоже учитывается.

#### Срабатывает
```csharp
foreach (var _ in W.Query<All<Health>, None<Health>>().Entities()) { }                       // FFSECS0051
W.Query<None<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0051 — лямбда подразумевает All
```

___

## Подавление диагностик

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

## Исходники

Все анализаторы лежат в `StaticEcs/Analyzers~/Src/Analyzers/*.cs`; code-fix'ы в `StaticEcs/Analyzers~/CodeFixes/`. Идентификаторы правил централизованы в `StaticEcs/Analyzers~/Shared/FFSECSIds.cs`.

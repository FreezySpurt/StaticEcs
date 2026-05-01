---
title: Частые ошибки
parent: RU
nav_order: 4
---

# Частые ошибки

Список частых ошибок при использовании StaticEcs. Полезен как разработчикам, так и AI-ассистентам.

___

## Ошибки жизненного цикла

### Забыли зарегистрировать типы
ВСЕ типы компонентов, тегов, событий, связей и мультикомпонентов ДОЛЖНЫ быть зарегистрированы между `W.Create()` и `W.Initialize()`. Использование незарегистрированного типа вызывает ошибку.
```csharp
// НЕПРАВИЛЬНО: компонент не зарегистрирован
W.Create(WorldConfig.Default());
W.Initialize();
var e = W.NewEntity<Position>(0); // RuntimeError!

// ПРАВИЛЬНО — ручная регистрация
W.Create(WorldConfig.Default());
W.Types().Component<Position>();
W.Initialize();
var e = W.NewEntity<Position>(0); // OK

// ПРАВИЛЬНО — авторегистрация всех типов из сборки
W.Create(WorldConfig.Default());
W.Types().RegisterAll();
W.Initialize();
```

### `RegisterAll()` в мульти-сборочных проектах / Unity IL2CPP / WebGL / NativeAOT

`W.Types().RegisterAll()` без аргументов сканирует **ровно одну сборку** — ту, в которой объявлен ваш `IWorldType`-маркер (`typeof(TWorld).Assembly`). Метод **не** использует stack walking и **не** перебирает все загруженные сборки. Это значит:

- **Метод безопасен на всех рантаймах**, включая Unity IL2CPP, Unity WebGL и NativeAOT, где `Assembly.GetCallingAssembly` возвращает ненадёжный результат.
- **Он пропустит ECS-типы из других сборок.** Типичная ошибка — держать маркер `TWorld` в «core»/«shared»-сборке, а компоненты — в игровой сборке: беспараметрный вызов тогда не зарегистрирует ничего.

```csharp
// НЕПРАВИЛЬНО — MyWorld лежит в Game.Core.dll, компоненты — в Game.Gameplay.dll.
// Сканируется только Game.Core.dll, поэтому компоненты не регистрируются.
W.Types().RegisterAll();

// ПРАВИЛЬНО — перечислите все сборки с ECS-типами.
W.Types().RegisterAll(
    typeof(MyWorld).Assembly,
    typeof(Position).Assembly,
    typeof(AiPlugin).Assembly
);
```

Если сомневаетесь — держите маркер `TWorld` в той же сборке, что и компоненты, и пользуйтесь беспараметрной формой.

### Операции с сущностями до Initialize
`NewEntity`, запросы и все операции с сущностями работают только после `W.Initialize()`. Вызов их во время фазы `Created` (между Create и Initialize) приведёт к ошибке.

### Повторный вызов Create
Вызов `W.Create()` без предварительного `W.Destroy()` — ошибка. Мир должен быть уничтожен перед повторным созданием.

___

## Ошибки работы с Entity

### Использование Entity после Destroy
`Entity` — 4-байтовый uint-хендл без счётчика поколений. После `Destroy()` слот немедленно доступен для переиспользования. Старый хендл теперь молча указывает на совершенно другую сущность.
```csharp
var entity = W.NewEntity<Position>(0);
entity.Destroy();
// entity теперь НЕВАЛИДЕН — любое использование — неопределённое поведение
entity.Ref<Position>(); // ОПАСНО: может обратиться к данным другой сущности
```

### Хранение Entity между кадрами
Поскольку Entity не имеет счётчика поколений, он не может обнаружить устаревание. Никогда не храните `Entity` в полях, списках или других постоянных структурах. Используйте `EntityGID`.
```csharp
// НЕПРАВИЛЬНО
class MySystem { Entity targetEntity; } // Устареет после уничтожения цели

// ПРАВИЛЬНО
class MySystem { EntityGID targetGid; } // Безопасно — проверка версии обнаружит устаревание
// Использование:
if (targetGid.TryUnpack<WT>(out var entity)) {
    // entity валиден и жив
}
```

### Сравнение Entity для идентификации
Равенство `Entity` — только по IdWithOffset (uint). Две сущности, созданные в разное время в одном слоте, имеют одинаковое значение Entity. Используйте `EntityGID` для сравнения идентичности.

___

## Ошибки работы с компонентами

### Семантика Add и Set
`Add<T>()` без значения — **идемпотентен**: если компонент уже существует, возвращает ref на существующие данные, хуки НЕ вызываются. Это НЕ перезапись.

`Set(value)` **всегда перезаписывает**: вызывает OnDelete на старом значении, перезаписывает данные, вызывает OnAdd на новом.

```csharp
entity.Set(new Position { Value = Vector3.Zero }); // Устанавливает позицию
entity.Add<Position>(); // Ничего НЕ делает — возвращает ref на существующий {0,0,0}
entity.Set(new Position { Value = Vector3.One }); // Перезаписывает: OnDelete(old) → set → OnAdd(new)
```

### Реализация пустых хуков
`ComponentTypeInfo<T>` использует рефлексию при старте для обнаружения реализованных хуков. Если хоть один хук имеет непустое тело, диспетчеризация хуков включается для ВСЕХ экземпляров данного типа компонента.
```csharp
// НЕПРАВИЛЬНО: пустое тело хука всё равно вызывает оверхед диспетчеризации
public struct Foo : IComponent {
    public void OnAdd<TW>(World<TW>.Entity self) where TW : struct, IWorldType { }
}

// ПРАВИЛЬНО: не реализуйте хуки, которые вам не нужны
public struct Foo : IComponent { }
```

### HasOnDelete vs DataLifecycle
Хук OnDelete и DataLifecycle (сброс к `DefaultValue`) — взаимоисключающие пути очистки. Если у компонента есть хук OnDelete, хук отвечает за очистку — данные НЕ сбрасываются. Сброс DataLifecycle применяется только к компонентам без OnDelete. При `noDataLifecycle: true` в конфиге фреймворк не выполняет ни инициализацию, ни очистку.

### Disable/Enable на компоненте без `IDisableable`
Методы `Entity.Disable<T>()` / `Enable<T>()` / `HasDisabled<T>()` / `HasEnabled<T>()` и фильтры `*Disabled` имеют констрейнт `T : struct, IComponent, IDisableable`. Вызов на типе без маркера — **ошибка компиляции**, не runtime-ассерт. Если код собирался в 2.1.x и теперь падает на компиляции — добавьте `IDisableable` к декларации затронутого компонента. См. [миграцию на 2.2.0](migrationguide.md).

___

## Ошибки запросов

### Снимок итерации vs другие сущности
Ограничения Strict / Flexible применяются только к **другим сущностям, входящим в снимок итерации** — битмаску сущностей, прошедших фильтр на момент старта обхода. Сущности вне снимка **не блокируются**: их можно свободно создавать, настраивать, изменять и уничтожать внутри тела цикла. Сюда входят:
- сущности, созданные во время итерации (всегда вне снимка — снимок зафиксирован до их появления);
- сущности, не прошедшие фильтр (другие компоненты, неподходящий тип сущности и т. п.).

```csharp
// OK в Strict — новая сущность не входит в снимок
foreach (var e in W.Query<All<Position>>().Entities()) {
    var fresh = W.NewEntity<Default>();
    fresh.Add<Position>();
    fresh.Set(new Velocity { ... });
}

// OK в Strict — `unrelated` не подходит под `All<Position>`
foreach (var e in W.Query<All<Position>>().Entities()) {
    unrelated.Add<Tag>(); // нет Position, в снимок не входит
}
```

### Снятие матча с не-текущей сущности из снимка
Ассерт Strict (и Flexible — он это ограничение НЕ снимает) — точечный. Он срабатывает только на операциях, способных снять матч с не-текущей сущности из снимка. Конкретно по типу `T` из фильтра:

| Фильтр              | Блокируется на не-текущей сущности из снимка |
|---------------------|----------------------------------------------|
| `All<T>`             | `Delete<T>`, `Disable<T>`                    |
| `AllOnlyDisabled<T>` | `Delete<T>`, `Enable<T>`                     |
| `AllWithDisabled<T>` | `Delete<T>`                                  |
| `None<T>`            | `Add<T>`, `Set<T>`, `Enable<T>`              |

Операции над **типами вне фильтра** не блокируются. Операции над сущностями **вне снимка** (созданными внутри итерации или с битом 0 в кэшированной маске) не блокируются. Текущую сущность мутировать можно как угодно.

```csharp
// НЕПРАВИЛЬНО — Position в фильтре, otherEntity в снимке:
W.Query<All<Position>>().For((W.Entity e) => {
    otherEntity.Delete<Position>(); // ассерт в DEBUG
});
W.Query<All<Position>>().For((W.Entity e) => {
    otherEntity.Delete<Position>(); // ассерт в DEBUG и в Flexible
}, queryMode: QueryMode.Flexible);

// ПРАВИЛЬНО — пометьте тегом в цикле, удалите одним batch-проходом после:
W.Query<All<Position>>().For((W.Entity e) => {
    if (ShouldStrip(otherEntity)) otherEntity.Set<Marked>(); // Marked не в фильтре — никогда не блокируется
});
W.Query<All<Position, Marked>>().BatchDelete<Position, Marked>();

// ПРАВИЛЬНО — мутация типа, не входящего в фильтр, разрешена:
W.Query<All<Position>>().For((W.Entity e) => {
    otherEntity.Delete<Velocity>(); // OK: Velocity в фильтре нет, никаких блокеров
});

// ПРАВИЛЬНО — мутация на сущности вне снимка (новая, либо не прошла фильтр) разрешена:
W.Query<All<Position>>().For((W.Entity e) => {
    var fresh = W.NewEntity<Default>();   // вне снимка по определению
    fresh.Set(new Position { ... });      // OK
});
```

### Entity-уровневые операции на других сущностях из снимка — только Flexible
Уничтожение, отключение или включение **другой сущности из снимка** во время итерации запрещено в Strict (ассерт в DEBUG), но разрешено в Flexible: кэшированная битмаска обновляется, и такая сущность исключается из оставшейся итерации.
```csharp
// НЕПРАВИЛЬНО в Strict:
foreach (var e in W.Query<All<Position>>().Entities()) {
    otherEntity.Destroy(); // ассерт в DEBUG (otherEntity в снимке)
}

// ПРАВИЛЬНО в Flexible:
foreach (var e in W.Query<All<Position>>().EntitiesFlexible()) {
    otherEntity.Destroy();  // OK — исключена из оставшейся итерации
    otherEntity.Disable();  // OK
    otherEntity.Enable();   // OK
}
```

### Ограничения параллельной итерации
Во время `ForParallel` модифицируйте только данные ТЕКУЩЕЙ сущности. Не создавайте/уничтожайте сущности, не модифицируйте другие сущности.

### Ненужный Flexible режим
Flexible перечитывает кэшированную битмаску на каждом шаге, что медленнее Strict. Используйте Flexible только когда действительно нужно `Destroy` / `Disable` / `Enable` других сущностей из снимка во время итерации — это единственная дополнительная свобода, которую он даёт. Создание новых сущностей и их настройка внутри тела цикла Flexible НЕ требуют: новые сущности не входят в снимок ни в одном из режимов.

### Дублирование компонентов делегата в `Query<>`-фильтре
Перегрузки `For<T0, ...>` на `WorldQuery<TFilter>` сами добавляют компоненты из сигнатуры делегата (`ref T0`, `in T0`) в фильтр итерации. Дополнительно перечислять их в `All<>` неверно — это лишнее дублирование, и явный признак, что вы боретесь с API:
```csharp
// НЕПРАВИЛЬНО — Position и Velocity повторены в All<>
W.Query<All<Position, Velocity>>().For(static (ref Position p, ref Velocity v) => { ... });

// ПРАВИЛЬНО — компоненты из делегата формируют фильтр сами
W.Query().For(static (ref Position p, ref Velocity v) => { ... });

// ПРАВИЛЬНО — в Query<> идут только дополнительные фильтры (теги, None, EntityIs и т.п.)
W.Query<None<Stunned>>().For(static (W.Entity e, ref Position p, ref Velocity v) => { ... });

// ПРАВИЛЬНО — entity-only делегат: компонента в сигнатуре нет, поэтому
// фильтр обязан быть в Query<All<...>>
W.Query<All<Position>>().For(static (W.Entity e) => { ... });
```
___

## Ошибки регистрации

### MultiComponent без обёртки Multi
Типы `IMultiComponent` должны регистрироваться через `W.Types().Multi<Item>()`, а не как обычные компоненты.

### Отсутствие настройки сериализации
Сериализация требует:
1. Зависимость FFS.StaticPack
2. Все типы автоматически получают GUID. Переопределите через `new ComponentTypeConfig<T>(guid: ...)` для стабильности при переименовании типов
3. Для не-unmanaged компонентов нужны реализации хуков `Write`/`Read`
4. Стратегия сериализации определяется автоматически (`UnmanagedPackArrayStrategy<T>` для unmanaged типов, `StructPackArrayStrategy<T>` в остальных случаях)

___

## Ошибки ресурсов

### Проблема кэширования NamedResource
`NamedResource<T>` кэширует внутреннюю box-ссылку при первом доступе. Если хранится как `readonly` или передаётся по значению после первого использования, копия кэша становится устаревшей.
```csharp
// НЕПРАВИЛЬНО
readonly NamedResource<Config> config = new("main"); // readonly ломает кэш

// ПРАВИЛЬНО
NamedResource<Config> config = new("main"); // мутабельный — кэш работает
```

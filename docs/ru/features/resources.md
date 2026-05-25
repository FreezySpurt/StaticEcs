---
title: Ресурсы
parent: Возможности
nav_order: 11
---

## Resources
Ресурсы — альтернатива DI, простой механизм хранения и передачи пользовательских данных и сервисов в системы и другие методы
- Ресурсы — это синглтоны уровня мира: общее состояние, не привязанное к конкретной сущности
- Идеальны для конфигурации, времени/дельта-времени, состояния ввода, кэшей ассетов, ссылок на сервисы
- Два варианта: **singleton** (один на тип) и **именованный** (несколько на тип, различаются строковым ключом)
- Доступны как в фазе `Created`, так и в `Initialized`
- Каждый тип ресурса **обязан** реализовывать маркерный интерфейс `IResource`

___

## Singleton-ресурсы

Singleton-ресурс хранит ровно один экземпляр данного типа на мир.
Внутри использует статическое generic-хранилище — доступ O(1) без словарных накладных расходов.

#### Установка ресурса:
```csharp
// Пользовательские классы и сервисы — обязаны реализовывать IResource
public class GameConfig : IResource { public float Gravity; }
public class InputState : IResource { public Vector2 MousePos; }

// Установить ресурс в мире
// По умолчанию clearOnDestroy = true — ресурс будет автоматически очищен при World.Destroy()
W.SetResource(new GameConfig { Gravity = 9.81f });
W.SetResource(new InputState(), clearOnDestroy: false); // переживёт пересоздание мира

// При повторном вызове SetResource для того же типа значение перезаписывается без ошибки
W.SetResource(new GameConfig { Gravity = 4.0f }); // перезаписывает предыдущее значение
```

{: .importantru }
Параметр `clearOnDestroy` учитывается только при первой регистрации. При замене существующего ресурса исходная настройка `clearOnDestroy` сохраняется.

#### Основные операции:
```csharp
// Проверить, зарегистрирован ли ресурс данного типа
bool has = W.HasResource<GameConfig>();

// Получить мутабельную ref-ссылку на значение ресурса — изменения записываются прямо в хранилище
ref var config = ref W.GetResource<GameConfig>();
config.Gravity = 11.0f; // изменено на месте, вызов сеттера не нужен

// Удалить ресурс из мира
W.RemoveResource<GameConfig>();

// Resource<T> — readonly-структура нулевой стоимости, хэндл для частого доступа (инициализация не нужна)
W.Resource<GameConfig> configHandle;
bool registered = configHandle.IsRegistered;
ref var cfg = ref configHandle.Value;
ref readonly var cfgRO = ref configHandle.ValueRO;     // `ref readonly` аксессор — без копии, без мутаций
configHandle.Set(new GameConfig { Gravity = 9.81f }); // регистрация / замена через хэндл
configHandle.Remove();                                 // удаление через хэндл
```

___

## Именованные ресурсы

Именованные ресурсы позволяют хранить несколько экземпляров одного типа, различаемых строковыми ключами.
Внутри хранятся в `Dictionary<string, object>` с типобезопасными `Box<T>`-обёртками.

#### Установка именованного ресурса:
```csharp
// Установить именованные ресурсы одного типа под разными ключами
W.SetResource("player_config", new GameConfig { Gravity = 9.81f });
W.SetResource("moon_config", new GameConfig { Gravity = 1.62f });

// При повторном вызове SetResource для существующего ключа значение перезаписывается без ошибки
W.SetResource("player_config", new GameConfig { Gravity = 10.0f }); // перезаписывает
```

#### Основные операции:
```csharp
// Проверить, существует ли именованный ресурс с данным ключом
bool has = W.HasResource<GameConfig>("player_config");

// Получить мутабельную ref-ссылку на значение именованного ресурса
ref var config = ref W.GetResource<GameConfig>("player_config");
config.Gravity = 5.0f;

// Удалить именованный ресурс по ключу
W.RemoveResource("player_config");

// NamedResource<T> — структура-хэндл, кэширующая внутреннюю ссылку после первого обращения
// Создать хэндл, привязанный к ключу (не регистрирует ресурс)
var moonConfig = new W.NamedResource<GameConfig>("moon_config");
bool registered = moonConfig.IsRegistered;  // всегда выполняет поиск в словаре, не кэшируется
ref var cfg = ref moonConfig.Value;          // первый вызов ищет в словаре и кэширует; последующие — O(1)
ref readonly var cfgRO = ref moonConfig.ValueRO; // `ref readonly` аксессор, поведение кэша как у Value
moonConfig.Set(new GameConfig { Gravity = 1.62f }); // регистрация / замена по привязанному ключу
moonConfig.Remove();                                // удаление по привязанному ключу (сбрасывает кэш)
// Кэш автоматически инвалидируется при удалении ресурса или уничтожении мира
```

{: .warningru }
`NamedResource<T>` — мутабельная структура, кэширующая внутреннюю ссылку при первом обращении к `Value`.
**Не** храните её в `readonly`-поле и не передавайте по значению после первого использования — компилятор C#
создаст защитную копию, сбросит кэш, и каждый доступ будет выполнять поиск в словаре.
Храните в обычном (не readonly) поле или локальной переменной.

___

## Жизненный цикл

```csharp
W.Create(WorldConfig.Default());

// Ресурсы можно устанавливать после Create (не нужно ждать Initialize)
W.SetResource(new GameConfig { Gravity = 9.81f });
W.SetResource("debug_flags", new DebugFlags(), clearOnDestroy: false);

W.Initialize();

// Ресурсы остаются доступными в фазе Initialized
ref var config = ref W.GetResource<GameConfig>();

// При Destroy: ресурсы с clearOnDestroy=true очищаются автоматически
// Ресурсы с clearOnDestroy=false сохраняются и остаются доступными после следующего цикла Create+Initialize
W.Destroy();
```

___

## Ресурсы группы систем

`Resource<T>` и `NamedResource<T>` существуют также на уровне группы систем. Каждый `World<TWorld>.Systems<TSystemsType>` имеет собственное независимое хранилище ресурсов, изолированное от ресурсов мира и от других групп систем. Жизненный цикл таких ресурсов привязан к пайплайну систем: они очищаются на `Systems<TSystemsType>.Destroy()`, а не на `World<TWorld>.Destroy()`.

Используйте их, когда состояние логически принадлежит конкретной группе систем (например, аккумулятор фиксированного шага для `FixedSys`, кадровый буфер только для рендера в `RenderSys`) и не должно протекать ни в ресурсы мира, ни в другие пайплайны.

#### Публичный API

На `Systems<TSystemsType>` зеркалируется тот же набор методов, что и у мира — отличается только область хранения:

```csharp
public struct FixedSystems : ISystemsType { }
public abstract class FixedSys : W.Systems<FixedSystems> { }

public struct FixedTime : IResource { public float Accumulator; public float Step; }

// Singleton-ресурс в области FixedSys
FixedSys.SetResource(new FixedTime { Step = 1f / 60f });
ref var time = ref FixedSys.GetResource<FixedTime>();
bool has = FixedSys.HasResource<FixedTime>();
FixedSys.RemoveResource<FixedTime>();

// Именованный ресурс в области FixedSys
FixedSys.SetResource("solver_a", new SolverState());
ref var solver = ref FixedSys.GetResource<SolverState>("solver_a");
FixedSys.RemoveResource("solver_a");
```

#### Структуры-хэндлы

`World<TWorld>.Systems<TSystemsType>.Resource<T>` и `World<TWorld>.Systems<TSystemsType>.NamedResource<T>` зеркалируют мировые хэндлы и обращаются напрямую к хранилищу группы систем:

```csharp
public struct PhysicsSystem : ISystem {
    private FixedSys.Resource<FixedTime> _time;
    private FixedSys.NamedResource<SolverState> _solver = new("solver_a");

    public void Update() {
        ref var time = ref _time.Value;          // хэндл нулевой стоимости, без поиска
        ref var solver = ref _solver.Value;      // словарный поиск при первом доступе, далее — кэш
        // ...
    }
}
```

Оба хэндла также имеют методы `Set(value, clearOnDestroy)` и `Remove()` — это тот же API регистрации/удаления, что и у мира или группы систем, но вызывается прямо на хэндле (тип ресурса / ключ берутся из самого хэндла).

Те же предупреждения о кэшировании `NamedResource<T>` остаются в силе: не храните такие хэндлы в `readonly`-полях и не передавайте их по значению после первого обращения к `Value`.

#### Жизненный цикл

```csharp
FixedSys.Create();

// Ресурсы можно устанавливать сразу после Create
FixedSys.SetResource(new FixedTime { Step = 1f / 60f });

FixedSys.Add(new PhysicsSystem());
FixedSys.Initialize();

// ... игровой цикл ...

// При Destroy: все ресурсы FixedSys с clearOnDestroy=true очищаются
// независимо от W.Destroy()
FixedSys.Destroy();
```

Разные типы `ISystemsType` (например, `FixedSys` и `RenderSys`) ведут полностью независимые хранилища ресурсов; то же касается ресурсов мира и любого пайплайна систем.

___

## Сериализация в снапшот

`IResource` имеет четыре опциональных метода с дефолтной реализацией. Переопределите `Guid()`, чтобы подключить ресурс к автоматической сериализации:

```csharp
public interface IResource {
    public Guid? Guid()                                              => null;  // null → не сериализуется
    public byte  Version()                                            => 0;
    public void  Write(ref BinaryPackWriter writer)                   {}
    public void  Read(ref BinaryPackReader reader, byte version)      {}
}
```

- **Unmanaged struct (без ссылок)**: `Write`/`Read` не требуются — фреймворк копирует сырую память через `Unsafe`.
- **Не-unmanaged** тип: и `Write`, и `Read` обязательны — иначе `SetResource` кидает `StaticEcsException`.
- Те же правила работают для ресурсов уровня мира и `Systems<TSystemsType>`, для singleton- и named-вариантов.

Полные детали сериализации (выбор формата, миграция версий, отдельный API `CreateResourcesSnapshot` / `LoadResourcesSnapshot`): см. [Сериализация → Сериализация ресурсов](./serialization.md).


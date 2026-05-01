---
title: Events
parent: Features
nav_order: 13
---

## Events
Event is a mechanism for exchanging information between systems or user services
- Represented as a user struct with data and the `IEvent` marker interface
- "Sender → multiple receivers" model with automatic lifecycle management
- Each receiver has an independent read cursor
- An event is automatically deleted when all receivers have read it or it is suppressed

#### Example:
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

{: .important }
Event type registration is available in both `Created` and `Initialized` phases

```csharp
W.Create(WorldConfig.Default());
//...
// Simple registration
W.Types()
    .Event<WeatherChanged>()
    .Event<OnDamage>();

// Configuration is provided by implementing IEventConfig<T> on the event struct
// (see example below)
//...
W.Initialize();
```

{: .note }
To provide configuration, implement the `IEventConfig<T>` interface on the event struct. Both manual registration and `RegisterAll()` will use it automatically:

```csharp
public struct WeatherChanged : IEvent, IEventConfig<WeatherChanged> {
    public WeatherType WeatherType;
    public EventTypeConfig<WeatherChanged> Config() => new(
        guid: new Guid("..."),   // stable identifier for serialization (default — auto-computed from type name)
        version: 1               // data schema version for migration (default — 0)
    );
}
```

___

#### Sending events:
```csharp
// Send an event with data
// Returns true if the event was added to the buffer, false if no registered receivers
bool sent = W.SendEvent(new WeatherChanged { WeatherType = WeatherType.Sunny });

// Send an event with default value
bool sent = W.SendEvent<OnDamage>();
```

{: .warning }
If there are no registered receivers, `SendEvent` returns `false` and the event is **not stored**. Register receivers before sending events.

___

#### Receiving events:
```csharp
// Create a receiver — each receiver has an independent read cursor
var weatherReceiver = W.RegisterEventReceiver<WeatherChanged>();

// Send events
W.SendEvent(new WeatherChanged { WeatherType = WeatherType.Sunny });
W.SendEvent(new WeatherChanged { WeatherType = WeatherType.Rainy });

// Read events via foreach
// After iteration, events are marked as read for this receiver
foreach (var e in weatherReceiver) {
    ref var data = ref e.Value; // ref access to event data
    Console.WriteLine(data.WeatherType);
}

// Additional event information during iteration
foreach (var e in weatherReceiver) {
    // true if this receiver is the last one to read this event
    // (the event will be deleted after reading)
    bool last = e.IsLastReading();

    // Number of receivers that haven't read this event yet (excluding current)
    int remaining = e.UnreadCount();

    // Suppress the event — immediately deletes it for all remaining receivers
    e.Suppress();
}
```

___

#### Receiver management:
```csharp
// Read all events via delegate; returns the number of times the delegate was invoked
int handled = weatherReceiver.ReadAll(static (Event<WeatherChanged> e) => {
    Console.WriteLine(e.Value.WeatherType);
});

// Suppress all unread events for this receiver
// Events are deleted and other receivers can no longer read them
// Returns the number of events actually suppressed by this call
int suppressed = weatherReceiver.SuppressAll();

// Mark all events as read without processing
// Events are not deleted — other receivers can still read them
// Returns the number of events actually marked as read
int marked = weatherReceiver.MarkAsReadAll();

// Delete the receiver
W.DeleteEventReceiver(ref weatherReceiver);
```

`ReadAll`, `MarkAsReadAll` and `SuppressAll` return the number of events for which the receiver
performed real work in this call. Events that were already suppressed by another receiver (or
already fully consumed by all other receivers) are silently skipped — from the calling receiver's
point of view those events did not exist, and they are **not** included in the returned count.
Concretely:

- `ReadAll(action)` — number of times `action` was invoked.
- `MarkAsReadAll()` — number of events whose unread counter was actually decremented.
- `SuppressAll()` — number of events whose unread counter was non-zero and was zeroed by this call.

___

#### Peek — inspection without consumption:

`Peek()` returns an iterator that walks all unread events of this receiver **without advancing
the cursor and without decrementing `UnreadReceiversCount`**. After the foreach exits, the
receiver state is unchanged — running `foreach (var e in receiver.Peek())` again yields the same
events.

Useful for multi-pass handling, dry-run/diagnostics, or reading queued state without committing
to consumption.

```csharp
foreach (var e in weatherReceiver.Peek()) {
    Console.WriteLine(e.Value.WeatherType);
    // data is accessible via e.Value, but the event is NOT marked as read
}
// A subsequent regular foreach still sees the same events and consumes them normally:
foreach (var e in weatherReceiver) { ... }
```

___

#### LastOnly — consume only when this receiver is the last reader:

`LastOnly()` returns an iterator that walks all unread events from the receiver's cursor
onward and yields **only those for which this receiver is the last unread reader** (equivalent
to `IsLastReading() == true`). Yielded events are automatically marked as consumed (decrement
+ mask bit cleared), just like in a regular foreach. Events still pending other receivers
(`UnreadCount > 1`) are **skipped without modification** — they remain reachable on later
passes. The receiver's cursor advances only through the contiguous prefix of done events; once
the walk passes an unprocessed event the cursor stops there, but the iterator keeps scanning
forward to find later positions where this receiver is already last.

This is the natural expression of «do something exactly once after every other receiver has
reacted», independent of system order within a frame.

{: .warning }
Only **one** receiver per event type should use `LastOnly()` — two would wait on each other
forever and events would hang indefinitely. This is the user's responsibility; the framework
does not validate it.

Example: destroying an entity after death. When an entity dies, a `DeadEvent` carrying its
`EntityGID` is dispatched, and several systems must read the entity (spawn loot, grant XP, play
death sound) **before** the entity is physically destroyed. Destroying it on death prevents
reactors from reading components; destroying via manual `IsLastReading()` requires a guarantee
that the cleanup system runs last — which usually doesn't hold.

With `LastOnly()`, the cleaner simply waits for the right frame:

```csharp
public struct DeadEvent : IEvent { public EntityGID Gid; }

// Reactors are registered the regular way:
EventReceiver<GameWT, DeadEvent> lootReactor = W.RegisterEventReceiver<DeadEvent>();
EventReceiver<GameWT, DeadEvent> xpReactor   = W.RegisterEventReceiver<DeadEvent>();
// Cleaner — registered the regular way, but will use LastOnly():
EventReceiver<GameWT, DeadEvent> deadCleaner = W.RegisterEventReceiver<DeadEvent>();

// On death — send the event; the entity stays alive for now:
W.SendEvent(new DeadEvent { Gid = entity.Gid() });

// Reactor systems (any order, regular foreach):
foreach (var e in lootReactor) {
    if (e.Value.Gid.TryUnpack(out var entity)) SpawnLoot(entity.Read<Position>());
}
foreach (var e in xpReactor) {
    if (e.Value.Gid.TryUnpack(out var entity)) GiveXp(entity.Read<XpReward>().Amount);
}

// Cleanup system (anywhere in the pipeline):
foreach (var e in deadCleaner.LastOnly()) {
    if (e.Value.Gid.TryUnpack(out var entity)) {
        entity.Destroy();   // safe: all other receivers have already consumed
    }
    // no MarkAsRead needed — the iterator marks events as consumed automatically
}
```

Frame N: reactors and cleaner run in arbitrary order. Each event still pending some reactor
has `UnreadCount > 1` — `LastOnly()` skips it without modification (mask stays, cursor doesn't
move past it). Events that every reactor has already consumed get yielded here and the entity
is destroyed inline. Frame N+1: remaining reactors read their pending events, and the next
cleaner pass picks up everything that has become ready since.

___

#### Multithreading:

{: .warning }
Sending events (`SendEvent`) is thread-safe under the following conditions:
- Multiple threads can simultaneously call `SendEvent` for the **same** event type
- **Simultaneous reading and sending** of the same event type from different threads is **forbidden** — sending is thread-safe only when there is no concurrent reading of the same type
- Reading events of one type (`foreach`, `ReadAll`) must be done in a **single thread**
- Different event types can be read from **different threads simultaneously**, as each type is stored independently
- The same event type can be read from different threads **at different times** (not concurrently)

Receiver operations (`foreach`, `ReadAll`, `MarkAsReadAll`, `SuppressAll`, creating and deleting receivers) are **not supported** in multithreaded mode and must only be performed on the main thread.

___

#### Event lifecycle:

{: .important }
An event is automatically deleted in two cases:
1. All registered receivers have read the event
2. The event was suppressed (`Suppress` or `SuppressAll`)

It is important that all registered receivers read their events (or call `MarkAsReadAll`/`SuppressAll`), otherwise events will accumulate in memory.

```csharp
// Lifecycle example with two receivers
var receiverA = W.RegisterEventReceiver<WeatherChanged>();
var receiverB = W.RegisterEventReceiver<WeatherChanged>();

W.SendEvent(new WeatherChanged { WeatherType = WeatherType.Sunny });
// Event has UnreadCount = 2

foreach (var e in receiverA) {
    // receiverA read it, UnreadCount = 1
}

foreach (var e in receiverB) {
    // receiverB read it, UnreadCount = 0 → event is automatically deleted
}
```

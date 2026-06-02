# Code Layout

## Decision

`Sim.Core` is organized into **feature folders**, one per system or layer. Each
folder is a namespace (`Sim.Core.Engine`, `Sim.Core.World`, `Sim.Core.Movement`,
etc.). A feature's events, intents, and pure systems live together in its folder
rather than being scattered across by-kind directories.

Current folders:

```
src/Sim.Core/
  Engine/        Simulation, EventQueue, ScheduledEvent, Rng — the deterministic spine
  World/         TileCoord, TileGrid, Unit, GameWorld, Resource, Structure — game state types
  Intents/       Intent (base), IntentEvent — intent abstraction + binding into the event stream
  Movement/      Pathfinding, MoveIntent, MoveArrivalEvent
  Logistics/     Regen, HarvestTickEvent, HaulIntent, BuildCompleteEvent  (M1)
  Persistence/   Snapshot
  GlobalUsings.cs   — exports each sub-namespace so internal code reads cleanly
```

Future milestones add sibling folders: `Combat/`, `Fog/`, `Roads/`, `Trade/`,
`Diplomacy/`. Each owns its own events, intents, and pure functions.

## Why feature folders over by-kind layering

Two layouts were considered:

- **A (chosen):** feature folders. A feature's pieces are grouped.
- **B:** by-kind folders (`Events/`, `Intents/`, `Model/`, `Systems/`). Familiar
  from app-development conventions.

A was chosen because:

- The design doc itself is organized by feature (§7 Logistics, §8 Roads, §9
  Combat...). Matching the layout to the design makes "where does X live?" answerable
  with the same vocabulary used to discuss X.
- A feature is a concrete bundle (its state additions, its events, its intents,
  its pure systems), not an abstract layer. B would split every feature across
  4–5 folders; adding a feature would mean editing many directories.
- "Where does road decay live?" → `Roads/`, not "split across Events/, Systems/,
  Model/." The folder name predicts the contents.

The trade A accepts: the `Engine/` folder ends up depending on multiple feature
namespaces (e.g. `Simulation.SubmitIntent` references `Intent`). In strict
layered codebases the kernel sits alone at the bottom. Here the engine is the
integration point and that's fine — namespaces are organization, not assembly
boundaries.

## Conventions

- **Folder = namespace.** A file at `Sim.Core/Movement/Pathfinding.cs` lives in
  `namespace Sim.Core.Movement`. No exceptions.
- **GlobalUsings.cs at the project root** exports every sub-namespace so files
  inside `Sim.Core` don't need to repeat `using` statements for sibling folders.
  Consumers (`Sim.Host`, `Sim.Tests`) write explicit `using` statements per file.
- **No type-namespace collisions.** The class formerly known as `World` was
  renamed to `GameWorld` so that `using Sim.Core.World;` does not shadow a type
  of the same name. New types follow the same rule — if a type name matches a
  namespace name, rename the type.
- **Append-only enums.** Any enum whose value is serialized into snapshots or
  intent payloads (`Resource`, `StructureKind`) is append-only and never
  renumbered. Existing values keep their byte forever.
- **`internal` for cross-file feature glue.** A method shared between e.g.
  `MoveIntent.ScheduleNextStep` and `MoveArrivalEvent.Apply` is `internal`,
  not public — it's plumbing inside a feature, not a public API.

## When to add a new folder

When a feature has *more than one* file's worth of content (its own events,
intents, and/or pure systems), give it a folder. A single file that fits in
an existing folder doesn't need one.

When adding a folder: add it to `GlobalUsings.cs` so internal callers don't
need per-file `using` statements.

## How this expands

- **New gameplay milestones** (combat, fog, roads, trade, diplomacy) each get
  their own folder with the same shape — state additions in `World/` when they
  modify shared types, events + intents + pure systems in the feature folder.
- **Server/client split.** When networking lands, `Sim.Host` either grows
  layered folders (`Server/`, `Net/`, `Wire/`) or splits into multiple
  projects. `Sim.Core` stays untouched — it doesn't know about clients.
- **Assembly split.** If `Sim.Core` ever grows large enough that compile time
  hurts, feature folders are the natural seams to split along — each becomes
  its own assembly. The folder structure pre-stages that split.

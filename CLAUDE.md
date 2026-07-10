# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OptiPather is a single ExileCore2 plugin (Path of Exile 2 atlas overlay). It reads the in-game Atlas
panel, builds a persistent graph of map nodes/edges as the player pans over them, and finds the shortest
route from completed/unlocked nodes to maps matching saved searches (by name and/or content tags like
"Ritual", "Corrupted Nexus"). Matches are drawn on the atlas as a highlight ring, path line, and
off-screen arrow, plus an overview minimap. See the parent `Plugins/Source/CLAUDE.md` for the general
ExileCore2 plugin framework (build commands, lifecycle hooks, shared DLLs, csproj conventions) — this
file only covers what's specific to OptiPather.

Nearly all logic lives in one file, `OptiPather.cs` (~4200 lines), organized with `#region` markers:
Map Finder (graph build + search), Persistence, Rendering, Minimap, Click to pan, Panel (ImGui UI),
Helpers. `OptiPatherSettings.cs` holds the settings nodes plus the `SearchPreset`/`MandatoryEntry`/
`OptionalEntry` POCOs that back saved searches.

## Architecture

**Graph accumulation.** `AtlasPanel.Descriptions`/`Points` only expose nodes near the current camera
viewport (they're live UI elements), so a single scan sees just what's on screen. `cachedNodes` /
`cachedAdjacency` accumulate every scan into a long-lived graph covering everywhere the camera has ever
panned. `GraphNode` instances are immutable — a re-seen node is *replaced*, never mutated, so a
published `FinderResult` holding old node references never tears mid-read.

**Threading model.** `Tick()` runs on the main thread and only touches game state (`AtlasPanel`,
identity resolution) — it never blocks. Search recomputation is dispatched via `DispatchRecompute()`
onto a background `Task` (`RecomputeMapFinder`), gated by `finderBusy` so only one runs at a time.
The worker scans the graph, runs a multi-source BFS from completed/unlocked nodes, builds a distance
field per optional, scores each active search's candidates, and swaps in a new `mapFinderResults` list
atomically. The render thread only ever reads that published list plus live `AtlasPanel` positions for
on-screen placement — it never touches `cachedNodes`/`cachedAdjacency` directly. Fields the worker owns
exclusively are commented `// Worker thread only` / `// Worker-only`; treat that boundary as load-bearing
when editing.

**Identity resolution and persistence.** The (league, character) pair is resolved on the main thread in
`UpdateIdentity()` and debounced (`IdentityStableTicks`) so a transient bad read during a loading screen
or character switch can't flip the active save file. The debounced `stableIdentityKey` is the only
identity the worker ever sees, passed in as a parameter to `RecomputeMapFinder` — the worker never reads
game state itself. Graphs are saved per-identity as JSON under `ConfigDirectory/atlas/<key>.json`
(`GraphSchemaVersion` gates format compatibility; bump it on any `NodeDto`/`EdgeDto`/`GraphSnapshot`
change). Saves are debounced (`SaveDebounceSeconds`) except at natural checkpoints (atlas closed, zone
change), which set `persistFlushRequested` to force an immediate write.

**Search model.** A `SearchPreset` has one or more `Mandatories` (must-reach maps/content — a single
mandatory finds the closest instance, several plan one route visiting all of them via Held-Karp/greedy
TSP in `SolveHeldKarp`/`SolveGreedyRoute`) and any number of `Optionals` (maps/content whose proximity to
the route gives a bonus that can nudge the destination choice, capped by `MaxExtraHops` and smoothed by
`OptionalHysteresis` to stop the pick flickering between near-ties as the atlas reveals more graph).

**Rendering.** `AtlasTransform` maps graph coordinates to screen space, fit from currently-visible
`AtlasNodeDescription`s (`FitAtlasTransform`) or held/panned between refits (`PanFollow`) so routes don't
jump every frame. `DrawAtlasOverlays` draws the per-search route/ring/arrow; `DrawMinimap` draws the
overview map fed by the same worker scans. Click-to-pan (`AdvancePan`) drives the game's own mouse-drag
pan by holding a synthetic mouse button down in short strides — `AbortPan` must run on every teardown
path (`OnClose`, `OnUnload`, `OnPluginDestroyForHotReload`) or the game is left dragging.

**PluginBridge: `OptiPather.GetNextMapStep`.** Lets another plugin ask "what's the next map to click to
get toward map X" and get back data usable to actually click the map device. `GetNextMapStepBridge`
(called on the consumer's thread) registers interest by dropping `(mapName, contentFilter)` into
`nextStepRequests` (a `ConcurrentDictionary`, safe from any thread) and returns whatever's already
published for that key. Answering happens in two hops because of the same worker/main-thread split as
everywhere else in this file:
1. `RecomputeMapFinder` (worker) runs `ComputeNextStepGraphInfo` alongside its normal scan — nearest
   unvisited match + the first not-yet-Visited node on the path to it (`ReconstructFinderPath`,
   `path.Find(n => !n.Visited)`) — and publishes graph-only data (`Vector2i` coordinate, names, hop
   count) into `nextStepGraphInfo`. The worker never touches `AtlasPanelNode`/`Element` (see
   `GraphNode`'s "never touches live game objects" invariant), so it cannot produce a screen position
   here. The multi-source BFS this rides on (`RecomputeMapFinder`'s `Seed()` calls) starts from both
   Visited and Unlocked-but-not-yet-run nodes, so the seed itself is sometimes the right answer (an
   Unlocked seed is already selectable on the map device) and sometimes not (a Visited seed - you're
   just standing in a map you already ran - must be skipped past). Always returning the seed (tried
   2026-07-07) broke every consumer that treats an already-visited node as a stale answer; always
   returning `path[1]` instead (tried 2026-07-08) overshot past an Unlocked seed to a node that isn't
   selectable yet (no Traverse slot - it's still locked behind the seed). Only "first not-yet-Visited
   node" is correct in both cases.
2. `ResolveNextStepScreenPositions` (main thread, called from `Render()` every frame) looks up the next
   hop's live `AtlasNodeDescription.Element.GetClientRect().Center` in this frame's `AtlasPanel
   .Descriptions`, and publishes the merged answer (graph info + `OnScreen`/`ScreenPosition`) into
   `nextStepResults` — the dictionary the bridge getter actually reads.

A node only has a valid click point while it's rendered in the current camera viewport (same limitation
`cachedNodes`/`cachedAdjacency` accumulation exists to work around) — `OnScreen == false` means the
caller must pan the atlas there first before a screen coordinate exists. Tick's `active` flag includes
`!nextStepRequests.IsEmpty` so a bridge consumer alone keeps the scan loop running even with the panel
closed and no active preset.

## Notes for changes

- When adding a field the worker writes and the render/main thread reads (or vice versa), mark it
  `volatile` and comment which thread owns writes, matching the existing pattern.
- Bump `GraphSchemaVersion` when changing `NodeDto`/`EdgeDto`/`GraphSnapshot` — old files are discarded
  wholesale on mismatch, not migrated.
- `Settings.Presets` is intentionally seeded empty (see comment in `OptiPatherSettings.cs`) — the
  settings serializer appends to existing lists rather than replacing them, so a non-empty default would
  duplicate on every launch.

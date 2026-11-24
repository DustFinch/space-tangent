# Simulation and engine boundaries

SpaceTangent keeps numerical evaluation, application state and engine presentation in separate assemblies. The simulator pins a tested core revision through its submodule.

```mermaid
flowchart LR
    Godot[Godot presentation] --> Application
    Application --> Core
    Application --> Contracts
    Core --> Contracts
    Native[Win64 C ABI] --> Core
    Native --> Contracts
    Unity[Unity binding] --> Native
    Unreal[Unreal example] --> Native
```

`SpaceTangent.Contracts` defines shared values such as exact epochs, identities and Cartesian states. `SpaceTangent.Core` evaluates models, reference frames and imported data without an engine dependency. `SpaceTangent.Application` combines those values into a world and observer workflows. The Godot project owns nodes, controls, rendering resources and window capture.

The native ABI currently exposes ephemeris queries. Its engine examples validate embedding; each engine owns its presentation. See [the core's ABI contract](../deps/space-tangent-core/docs/NATIVE_ABI.md) for the supported layout, ownership and validation scope.

## State and identity

`SimulationWorldAssembly` builds the installed catalogue and evaluation graphs; `SimulationWorld` keeps their public query facade. `SimulationSession` owns live observers and delegates linked/independent time changes to `ObserverClock`, complete scenario validation to `ScenarioRestoration`, and camera entry requirements to `CameraModeRules`. UI and scripts keep the same session operations. The adapter reads that state to update controls and rendering.

A view index describes its current place in the layout. A lifetime identity keeps callbacks attached to the same live observer when another view closes. A persistent place-history key identifies saved observing locations. These identities have different lifetimes: splitting forks place identity, restoring a scenario replaces live callback identities, and ordinary add-on reload preserves the session and its views.

Observers retain exact TDB epochs. Linked views share their clock owner's epoch, play state and rate; independent views retain their own clocks. The selected delta-T model affects derived calculations such as Earth orientation without replacing the saved TDB epoch.

`ObserverBookmarkStore` owns bookmark collection changes and publishes its visible list after successful persistence. The existing file codec and scene-link privacy rules remain shared by UI and imports.

`ObserverPlaceAutosave` owns one session's frozen preferences path, revision tracking, debounced writes and retry delay. The adapter decides whether startup policy enables it and displays failures; scenarios, replay and batch startup keep their existing privacy rules.

`ObserverWorkspaceReader` owns archived-input classification streams; the adapter retains the order of replay shutdown and scenario parsing. `DeltaTPreferenceFile` keeps the existing named-model preference format and atomic replacement independently of engine path discovery.

Scenario restoration validates the complete input before changing live state. Saved scenarios and recordings preserve place history, while shared links and bookmarks retain the active cameras without publishing unrelated private locations. See [observer views](OBSERVER_VIEWS.md).

## Data preparation and publication

`AddonLoader` resolves engine-specific source locations and schedules preparation with explicit `AddonPreparationInputs`. `AddonPreparation` in Application reads catalogues, tracks dependencies, decodes meshes and terrain, and prepares observation samples without an engine reference. Its diagnostic callbacks run on the calling worker. The Godot adapter publishes Texture/Mesh resources on the main thread and preserves unchanged decoded/resource identities across reloads.

`AddonLoadCoordinator` owns the loading generation and cancellation source. It cancels superseded work and invalidates pending results when its owner closes. The scene additionally checks its native lifetime and tree membership before publication. A completed request may publish only while it remains current and its scene owner is alive. Cancelled work must release candidate-owned data without disposing an index or asset still shared by the active generation. `GpuAssetPublication` gives newly created GPU resources a per-asset scope. A failed scope or discarded candidate releases its own resources; committed generations retain explicitly reused asset groups and their texture dependencies. ResourceLoader shaders remain borrowed. Grouping by asset keeps alternating partial updates from retaining chains of entire generations. All views must finish switching before a superseded shared resource is released. Native scene nodes retain their own reference-counted resources until removal. Final view removal closes per-view optical/trail and imported drawing resources; temporary layout detachment only rebinds viewport-size subscriptions.

Reload preserves the last successful world on rejected input and keeps the observers' epochs, cameras, selection and delta-T whenever the replacement supports them. Requested source preferences are saved for the successfully applied world. A failed synchronous preference commit restores the previous world, observer records and unfinished flights before the adapter switches its live generation. The previous distant-star index is retired after all live views have switched. SharedResourceRetirement holds it until captured reloads and observer calculations finish; weak retirement records do not retain disposed catalogues. Content hashes and recorded failed lookups determine cache validity. See [add-on cache contracts](ADDON_CACHE.md).

## Frame evaluation

`SolarSystemScene` connects application workflows, controls and views. `ObserverViewHost` owns control creation, active-index remapping, layout dividers and their callbacks, and suppression of programmatic field updates. It receives typed observer actions and explicit renderer creation/resource-binding callbacks, preserving the original node-ready order. `ObserverNavigationInput` owns held movement keys, pointer gestures and inertia, translating engine input into the same session operations through narrow presentation callbacks. Pre-ready release state and focus-loss cleanup retain their original lifetimes.

`ObserverTimePanel` owns clock and calendar controls and their display cache. Typed commands keep time changes in the session; the panel reads the active observer without reaching into scene fields.

`ObserverCalculationSlot` owns supersession and shutdown of each background calculation channel. Cancellation sources remain alive until their worker scopes complete; the event/night shared channel keeps its original behavior. `ObserverScriptPlayer` owns foreground Lua and its view media, with separate ports for rendering, view operations, capture and status. Plugins retain their own quotas and lifecycle.

`SimulationViewport` coordinates a view's drawing and picking. `MissionTrailRendering` owns its rolling samples, publication keys and trail mesh while consuming the already evaluated frame and explicit projection/settings. `MissionCameraRendering` owns mission/timeline/probe nodes and sensor projection with explicit camera, anchors and marker/material factories. `OpticalRendering` owns the eyepiece, display cache and resize redraw; `ObserverRenderFrame` carries captured observer/snapshot/ΔT inputs without evaluating the world again. `StellarRendering` owns spatial star fields, resolved disks and picking caches; `RecordedStarRendering` owns the corresponding replay meshes and saved shader inputs. The viewport keeps coordinate preparation and the ordering between stellar, deep-sky, exoplanet and demo-world stages. `ReferenceMarkRendering` owns axes/grid/terminator meshes and receives precomputed geometry, centre and scale; mission annotations and compact-lens effects keep their specialized overlay/render owners.

Rendering consumes a world snapshot evaluated at the observer's epoch and delta-T model. Reuse is valid only for the same relevant world and time dependencies. Stateful imported Lua models retain their evaluation order; background observation calculations use the world's graph that excludes scripted models and their unsafe dependencies.

Mission trails and selected-body paths retain their published data while preparing replacements. Changing their world, target, centre, style or time dependencies invalidates the appropriate work. At capture boundaries, screenshots, recording and replay require the complete current paths. Incremental budgets and synchronous freshness fallbacks are described in [runtime profiling](PERFORMANCE.md).

## Verification boundaries

Scientific values, persistence, rendered images and performance have separate checks. C# line coverage does not establish shader accuracy, frame latency or compatibility with every external data set. Public CI exercises pinned public inputs; the complete local-data matrix additionally includes real add-ons and isolated rendered workflows.

Performance comparisons use the same workload, inputs, runtime, hardware and resolution without coverage instrumentation. Raw profiles include allocation and GC context, and frame readbacks are reported as wall time. Test scenes and diagnostic workload hosts are excluded from exported releases. See [coverage and acceptance](TEST_COVERAGE.md) for the evidence and failure policies.

Godot probes keep their scenario entry scenes and markers. ProbeHost shares engine-local assertions, frame waits, explicit scene cleanup and coverage-aware exit; scenarios retain their own timeouts and negative expectations. A matrix run can declare a narrow measured assembly scope, while final acceptance still requires all four production modules independently at 95%.

## Responsibility crosswalk

This maps production ownership, not a list of renamed files. Existing entry points remain available.

| Previous owner | Responsibility | Current owner |
| --- | --- | --- |
| SimulationSession.cs | Installed model graph construction | SimulationWorldAssembly |
| SimulationSession.cs | Linked and independent clocks | ObserverClock through SimulationSession |
| SimulationSession.cs | Whole-scenario validation and restoration | ScenarioRestoration |
| SimulationSession.cs | Camera entry constraints | CameraModeRules |
| SimulationSession.cs | Observer-compatible world replacement and rollback | WorldReplacementPlan and the session commit boundary |
| Godot AddonLoader | Worker-side I/O, dependency capture and decoded assets | Application AddonPreparation with explicit inputs |
| SolarSystemScene | Latest-request cancellation | AddonLoadCoordinator; native lifetime stays in adapter |
| SolarSystemScene | View controls, layout and callback remapping | ObserverViewHost |
| SolarSystemScene | Gesture state and navigation input | ObserverNavigationInput |
| SolarSystemScene | Time/calendar and layer widgets | ObserverTimePanel and ObserverLayersPanel |
| SolarSystemScene | Foreground scripting and media | ObserverScriptPlayer with narrow operation ports |
| SolarSystemScene | Place autosave, bookmarks and workspace streams | ObserverPlaceAutosave, ObserverBookmarkStore, ObserverWorkspaceReader |
| SolarSystemScene | Deferred catalogue retirement | SharedResourceRetirement and captured calculation scopes |
| Implicit resource wrappers | Shared GPU candidate publication and release | GpuAssetPublication per-asset groups and PublishedRenderAssets |
| SimulationViewport | Trail cache, scheduler and mesh | MissionTrailRendering |
| SimulationViewport | Eyepiece and resize redraw | OpticalRendering |
| SimulationViewport | Mission camera, probes and sensors | MissionCameraRendering |
| SimulationViewport | Survey/sky canvases and environment | SkyPresentation |
| SimulationViewport | Schematic planet markers and paths | PlanetOverviewRendering |
| SimulationViewport | Live and recorded stars | StellarRendering and RecordedStarRendering |
| SimulationViewport | Axes, grids and terminator meshes | ReferenceMarkRendering |
| SampledMissionCatalog | JSON document graph and traversal lifetime | MissionDocumentSet |
| SampledMissionCatalog | Ordered pool and runtime object binding | MissionRuntimeBinding |
| SscBodyCatalogue | Ordered sources, symbols and resource resolution | SscInstallationInputs and narrow SscResourceContext |
| SscBodyCatalogue | Parent traversal, cycles and rejected references | SscRuntimeBinding |
| Six probe entry facades | Frame waits, assertions and completion protocol | ProbeHost; assertions and markers remain scenario-specific |

SolarSystemScene still composes the engine tree and coordinates each frame. SimulationViewport still prepares coordinates, preserves stage ordering and handles picking. Existing surface, atmosphere, ring, annotation and compact-lens owners remain in place. There is no new global event bus, dependency-injection framework or universal importer.

The world-replacement preparation and synchronous preference rollback boundaries also fix two independently reproduced failure cases: a later observer evaluation could previously leave an earlier observer changed, and failed preference persistence could leave a newly applied world active. Their regression tests are separate from the mechanical extractions.

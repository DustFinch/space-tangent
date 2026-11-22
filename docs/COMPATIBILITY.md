# Compatibility scope and evidence

This matrix describes the accepted application scope, supported input subsets and remaining evidence gaps. It does not claim complete feature, API, catalogue or visual equivalence with another application. Passing an automated workflow also does not establish that its controls are convenient; comparative UI acceptance remains a separate pending activity.

The comparison families are drawn from [Celestia](https://github.com/CelestiaProject/Celestia), [SPICE-enhanced Cosmographia](https://naif.jpl.nasa.gov/naif/cosmographia.html) and [Stellarium](https://stellarium.org/). Their documentation identifies comparison targets; the evidence below concerns this application and its pinned core, not a fresh interactive examination of those programs.

## How to read the matrix

- **Available:** implemented, with the named automated contracts. Optional-data branches require their documented inputs.
- **Partial:** a useful implemented subset, with material limits stated in the same row.
- **Evidence gap:** implementation exists, but a specific historical installation or recording cannot currently be reproduced.
- **Deferred:** deliberately outside the accepted migration scope.

Test-source links identify assertions rather than successful execution by themselves. For a particular checkout, keep its fresh TRX, accepted GPU logs, profile/source hashes and coverage summary together. Data-free CI does not execute private-data branches. A skipped case, a parsed catalogue and a rendered screenshot are three different levels of evidence.

## Space flight and catalogue workflows

| Workflow | Status and supported scope | Evidence and limits |
| --- | --- | --- |
| 3D flight, selection and tracking | Available: target-relative flight, inertial tracking, body rotation, independent cameras and two layouts. | [Navigation contracts](../tests/SpaceTangent.Application.Tests/NavigationOperationBoundaryTests.cs), [rendered workspace input smoke](../src/SpaceTangent.Godot/tests/WorkspaceInputSmoke.cs). Matching another program's camera controls remains manual acceptance. |
| Solar system | Available within installed coverage: eight planets, selected moons and small bodies, optional maps, rings and body orientations. | [Planet and moon tests](../tests/SpaceTangent.Application.Tests/PlanetMoonModelTests.cs), [planetary data and center corrections](PLANET_DATA.md). Missing or expired kernels do not gain coverage from the UI date picker. |
| Clock, split views and reload | Available: independent or synchronized epochs, camera/selection preservation, saved ΔT and atomic reload. | [Clock persistence](../tests/SpaceTangent.Application.Tests/ClockPersistenceTests.cs), [shared clocks](../tests/SpaceTangent.Application.Tests/SharedClockTests.cs), [workspace input smoke](../src/SpaceTangent.Godot/tests/WorkspaceInputSmoke.cs). |
| Last observing places | Available: separate history per persistent view and body, split/restart/reload, explicit-scene precedence and bounded atomic preference saves. | [Place-memory contracts](../tests/SpaceTangent.Application.Tests/ObserverPlaceMemoryTests.cs) and the rendered place-memory smoke. Neutral defaults use Greenwich/UTC; explicit imported user places are retained. |
| SSC body catalogues | Partial: ordered Add/Modify/Replace, aliases and the documented orbit, attitude and local-resource subsets. | [SSC adapters](SSC_BODY_CATALOGUES.md). Original Cassini/Huygens and selected moon/body catalogues exercise different adapters; none establishes compatibility with every catalogue field or API. |
| Meshes and conversion | Partial: supported OBJ, 3DS, binary/text CMOD, CMS, glTF/GLB and local textures. | [Model format contracts](../tests/SpaceTangent.Application.Tests/ModelFormatContractTests.cs), [CLI/export limits](SSC_BODY_CATALOGUES.md). Unsupported primitives/extensions are reported. Exact historical shuttle and model-owner bundles remain evidence gaps. |
| Stars, multiple systems and exoplanets | Available for installed catalogues: spatial stars, STC modifications, binary systems, stellar hosts and local deep-sky entries. | [Spatial data](SPATIAL_SKY.md), core `StellarCatalogueTests` and `StcCatalogueTests`. Regenerated HYG astrometry is distinct from the original stellar archive; its display-radius proxy is not a physical-radius reference. |
| Scripted orbits, tours and scene links | Partial: the documented Lua subset and local trusted factories, tours, native scene links and supported imported bookmark frames. | [Lua scope and quotas](LUA_SCRIPTS.md), [tours](TOURS.md), [bookmark limits](OBSERVER_VIEWS.md). Arbitrary third-party script APIs, VM modules and unsupported frames are not inferred from these checks. |
| Recordings and media | Available for the tested current and independently authored wire examples; historical acceptance is partial. | [Recording tests](../tests/SpaceTangent.Application.Tests/RecordingTests.cs). The old 900-frame full-Moon file, discrete-star, lens and orbit recordings are separate missing witnesses. A newly recorded movie does not restore them. |

## Mission visualization

| Workflow | Status and supported scope | Evidence and limits |
| --- | --- | --- |
| Local mission JSON and sampled arcs | Partial: version 1.0 adapters, documented XYZ/XYZV and attitude/frame models, coverage gaps and local resources. | [Sampled missions](SAMPLED_MISSIONS.md), [sampled mission contracts](../deps/space-tangent-core/tests/SpaceTangent.Core.Tests/SampledMissionCatalogTests.cs). The full original MSL/Dawn source and packed installations remain unavailable. |
| SPICE states and attitudes | Partial: SPK types 1, 2, 3, 8, 9, 12, 13, 17 and 21; CK types 2 and 3; documented frame, clock and text-kernel subsets. | [Core SPICE scope](../deps/space-tangent-core/docs/SSC_SPICE.md), `AdditionalSpkTests`, `PolynomialKernelContractTests`, `SpiceCkOracleTests` and original Cassini reference tests. This is not a replacement for every toolkit kernel type, frame or aberration service. |
| Sensors, footprints and observation annotations | Available for supported field shapes and tested local missions; geometric/format scope is partial. | [Cassini geometry](CASSINI_MISSION.md), [mission geometry and observation probes](../src/SpaceTangent.Godot/tests/EarthOrbitProbe.cs). Surface intercepts use the supported reference shape, not arbitrary DSK terrain. |
| Particles, swarms and timed meshes | Available for the documented local formats, bounded emitters and tested epoch/reload behavior. | [Mission limits](SAMPLED_MISSIONS.md), [compute probe](../src/SpaceTangent.Godot/tests/MissionSwarmComputeProbe.cs). Original archival versions and specific timed-resource bundles require separate evidence. |
| Packed installations | Partial: supported readers, current writer round trips, relocation and ordered owners. | Core packed-catalogue tests and ordinary application reload. Missing historical pack versions are not reconstructed merely by changing a version number in a current manifest. |

## Observing and planetarium workflows

| Workflow | Status and supported scope | Evidence and limits |
| --- | --- | --- |
| Ground sky and optical aids | Available: Earth observer, local horizon, atmosphere, sky layers, finder/eyepiece controls and equatorial mount. | [Observer guide](OBSERVERS.md), [calculation tests](../tests/SpaceTangent.Application.Tests/ObserverCalculationTests.cs), rendered observing profile. |
| Atmospheres and full-Moon clouds | Available for the tested Earth profiles and optional atmosphere tables; approximate rendering model. | [Moon-lighting acceptance](MOON_LIGHTING.md), `EarthAccuracyProbe` and `MoonLightingProbe`. The analytic Earth profile uses single scattering; a successful C# line hit alone says nothing about image quality. |
| Surface observers on other bodies | Available for supported body orientations/ellipsoids; selected Moon/Mars relief modes. | [Surface tests](../tests/SpaceTangent.Application.Tests/SurfaceObserverTests.cs). This does not provide a general DEM, Earth atmosphere on every body or a local solar-day calendar for every planet. |
| Grids, constellations and landscapes | Available for selected installed datasets and the documented layer controls. | [Sky data](SKY_DATA.md), rendered grid/culture/landscape profiles, [artwork recovery](TEST_COVERAGE.md#recover-available-culture-artwork). The recovered subset has 299 available images; six references are absent from the pinned upstream source. Selected-culture acceptance does not establish every possible catalogue. |
| HiPS/DSS and local photographs | Partial: local survey metadata, overview/detail cache, local photographs and supported retrieval behavior. | [Survey tests](../tests/SpaceTangent.Application.Tests/LocalSkySurveyTests.cs), [repeatable DSS capture](TEST_COVERAGE.md#restore-the-upstream-survey-sample), [complete upstream photograph recovery](TEST_COVERAGE.md#restore-all-original-sky-photographs). The 674-image test runs on unchanged pinned upstream files; a three-file DSS sample is not the whole survey. Historical local-cache hashes remain unavailable. |
| Calendars and civil time | Available for the implemented calendar labels, historical Julian/Gregorian transition, selected zones and ΔT models. | [Calendar anchors](../tests/SpaceTangent.Application.Tests/CivilCalendarDisplayTests.cs), [time conventions](../deps/space-tangent-core/docs/TIME.md). Neither a historical date label nor a ΔT formula establishes ephemeris accuracy for that date. |
| Events and AstroCalc | Available: implemented phases, seasons, conjunction/opposition, selected eclipses/passes/flares/shadows, charts and exports. | [Nearby almanac](../tests/SpaceTangent.Application.Tests/NearbyAlmanacTests.cs), [event contracts](../tests/SpaceTangent.Application.Tests/ObserverEventContractTests.cs). Event algorithms, tolerances and installed coverage remain specific to each calculation. |
| Visible tonight | Available: magnitude-qualified lists and observing-window controls. | [Visibility tests](../tests/SpaceTangent.Application.Tests/VisibleTonightTests.cs). Ten-minute sampling supports planning, not exact contact timing. |

## Accepted exclusions and remaining acceptance

HTTP API, network MultiWMS, an out-of-process Lua runtime, complete Lua VM/bytecode/C-module compatibility, Stellarium scripting, new dome/mirror projections, 3D landscapes, telescope control, every SPICE/DSK format and unbounded streaming remain deferred. Large architectural refactoring is a separate project.

The Windows application is the present interactive acceptance target. Standalone C ABI and editor probes do not establish compiled Unreal C++ integration, packaged Unity/Unreal players or a fully accepted desktop release on every operating system.

Before a public release, carry out the [comparative UI acceptance](COMPARATIVE_ACCEPTANCE.md). Record missing behavior and awkward controls separately from intentionally different presentation. No release is authorized by this matrix.

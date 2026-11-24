# Source-line coverage

Coverage measures executable lines in the authored C# modules. Unit tests, generated sources and third-party libraries are excluded. Shader execution and image correctness require separate GPU acceptance. A high combined percentage must not hide an omitted Godot module or an untested Native/CLI consumer.

## Run a fresh acceptance matrix

`scripts/testing/coverage-matrix.ps1` runs a local manifest sequentially and enforces 95% separately for Core, Contracts, Application and Godot. It accepts no previous coverage reports. It snapshots authored sources, test scenes, shader includes, runtime data, embedded resources and engine/build configuration before and after execution, preserves raw evidence and input/report hashes, and fails if those sources change. A failed threshold leaves `summary.json` available for diagnosis; only a `passed` matrix status means the whole matrix met its gates. Native, CLI and engine bindings still need their separate measurements. The inventory includes tracked and nonignored untracked files under `src`, `tests`, `tools`, `bindings`, `scripts`, `.config`, plus root configuration. Generated `bin`, `obj`, `.godot` and Python caches are excluded. Cross-checkout Core comparison normalizes LF/CRLF for known UTF-8 text formats and hashes binary resources byte for byte; each checkout's before/after snapshot always hashes exact bytes.

Keep the manifest, profile mapping and optional data outside Git. The profile mapping is a JSON object whose keys are manifest profile identifiers and whose values are paths to verified data-profile JSON files. Absolute paths are recommended. Relative parameters and map values resolve once against the caller's initial PowerShell location, before changing directories; verification, hashing and execution use that same resolved path. `-Godot` also accepts an executable name from `PATH` (for example, `-Godot godot`); names resolve once to an application path, while explicit absolute or relative paths take precedence over `PATH`. Each run has a unique lowercase `name`, a `kind` (`core`, `application` or `probe`) and a `profile` identifier. Unit runs optionally accept a VSTest `filter`. Probe runs require an explicit test `scene` and `successMarker`; optional fields are `options`, `render`, `userArguments`, `expectedExitCode` and `requiredModules`, with the same contracts as the individual probe runner. Controls and application arguments can use `{output}` for that run's new evidence directory. Do not point captures or saved scenarios at earlier evidence.

For example, a probe entry is:

```json
{
  "version": 1,
  "runs": [
    {
      "name": "observing-places",
      "kind": "probe",
      "profile": "workspace",
      "scene": "res://tests/WorkspaceInputSmoke.tscn",
      "successMarker": "SpaceTangent observing place memory input smoke passed",
      "render": true,
      "options": {"SPACETANGENT_PLACE_MEMORY_SMOKE_ONLY": "1"}
    }
  ]
}
```

This one-entry example describes the schema; it is not enough for the four-module coverage target. Include both full unit suites and the scientific/rendered profiles needed by the measured sources. Every rendered entry uses an inactive Windows desktop automatically, without switching the user's desktop or injecting system input. Headless entries do not need a display. The first rendered or headless probe builds/imports the Godot project; later probes reuse that unchanged build under the individual runner's instrumentation lock.

```powershell
./scripts/testing/coverage-matrix.ps1 -Manifest C:/TestData/matrix.json -Profiles C:/TestData/profiles.json -OutputDirectory C:/TestResults/fresh-matrix -Godot C:/Tools/Godot/Godot_console.exe
```

An incomplete prefix is retained for diagnosis, with failed status and source-stability information. It is not a successful matrix. Collect in a fresh directory after correcting a failure; never relabel a stale or failed report as fresh acceptance.

## Restore the upstream survey sample

The original DSS metadata/tile test can run with a small pinned capture of the same upstream survey. `fetch_survey_sample.py` downloads the properties file, order-3 overview and order-5 Orion tile from the NASA IRSA mirror listed in the [CDS registry](https://alasky.cds.unistra.fr/MocServer/query?ID=CDS%2FP%2FDSS2%2Fcolor&get=record). It checks exact sizes and SHA-256 values before writing each resource, keeps the survey's original metadata and records provenance separately. A changed upstream resource fails; it is not silently accepted by recapturing a data profile.

```powershell
python scripts/testing/fetch_survey_sample.py C:/TestData/dss-sample
python deps/space-tangent-core/scripts/testing/data_profile.py capture --output C:/TestData/dss-profile.json --bind SPACETANGENT_SKY_SURVEYS=C:/TestData/dss-sample/surveys
python deps/space-tangent-core/scripts/testing/data_profile.py run --profile C:/TestData/dss-profile.json -- dotnet test tests/SpaceTangent.Application.Tests --filter FullyQualifiedName~OriginalDssMetadataAndCachedTilesResolveTheOrionField
```

This executes the unchanged test against published upstream data. Historical local-cache hashes are unavailable, so the capture does not establish byte identity with that lost cache. It does not restore the full survey, the separate photographic catalogue or network map compatibility. The original survey credits and distribution terms apply; no images are added to this repository.

## Restore the astrometry fixture

`scripts/sky/prepare_spatial.py` adapts the pinned HYG 4.1 CSV already identified in [the spatial catalogue notes](SPATIAL_SKY.md). Obtain `hyg/CURRENT/hygdata_v41.csv` from revision `c7f7f883fe678cc7680169a50ccd7dcc49b060ce` of the upstream repository; the preparer checks SHA-256 `d9f69fd86bbf90a4e4d52b4c5c53eacfa6dfc0bfdef85bfd94f095e0bebe4ebd` before creating output. It uses the upstream convention for usable parallaxes, retains the original Cartesian position/velocity columns and produces 109,400 rows without truncating to a desired population.

```powershell
python scripts/sky/prepare_spatial.py C:/TestData/hyg-v41.csv C:/TestData/hyg-adapted
python deps/space-tangent-core/scripts/testing/data_profile.py capture --output C:/TestData/hyg-profile.json --bind SPACETANGENT_SPATIAL_STARS=C:/TestData/hyg-adapted/hyg-astrometry.tsv
python deps/space-tangent-core/scripts/testing/data_profile.py run --profile C:/TestData/hyg-profile.json -- dotnet test deps/space-tangent-core/tests/SpaceTangent.Core.Tests --filter FullyQualifiedName~PinnedCatalogueContainsNearbyStarsAtPublishedScales
```

This is a regenerated astrometry fixture, not a byte-identical historical adapter output. Its redundant scalar distance is derived from the supplied Cartesian coordinates. Missing colours use an explicitly recorded display default. The required radius column uses a visual-luminosity display proxy; it is not a measured physical radius and must not be used as a physical-size or rendered-disk reference. Provenance and CC BY-SA 4.0 attribution accompany the output.

For the supplementary STC regressions, use a separate source-only STC/DSC directory **without** `stars.dat`; bind the unchanged binary table separately through `SPACETANGENT_STC_ASTROMETRY`. Those tests first assert that records are missing, then add the binary table and verify recovery against retained native reference coordinates. Placing `stars.dat` in the baseline source directory defeats the missing-input setup. Preserve the original installation and construct a separate pinned snapshot instead of removing files from it. Supply the original stellar archive and exoplanet catalogue too; the full dependency list is declared by the test attributes.

## Restore all original sky photographs

`scripts/sky/fetch_photographs.py` downloads the complete 674-image set and unmodified `textures.json` from the official upstream revision pinned in `photographs-source.json`. The manifest records every filename, byte size and Git blob identity. Captures also record SHA-256 values. Credits, image URLs, coordinates and event dates remain in the original metadata. A partial or changed capture fails and retains incomplete provenance; it never becomes a completed source set.

```powershell
python scripts/sky/fetch_photographs.py C:/TestData/sky-photographs
python scripts/sky/fetch_photographs.py C:/TestData/sky-photographs/photographs --verify
python deps/space-tangent-core/scripts/testing/data_profile.py capture --output C:/TestData/photographs-profile.json --bind SPACETANGENT_SKY_PHOTOGRAPHS=C:/TestData/sky-photographs/photographs
python deps/space-tangent-core/scripts/testing/data_profile.py run --profile C:/TestData/photographs-profile.json -- dotnet test tests/SpaceTangent.Application.Tests --filter FullyQualifiedName~ReadsAllOriginalFilesAndPreservesCoordinatesCreditsAndVisibility
```

The sample requires approximately 130 MB of image downloads, kept outside Git. `--verify` checks an existing source set without downloading or changing it. This restores original upstream files with known identity and executes the retained full-population assertions; it does not prove the hash of an unavailable historical local installation. Keep all original per-image credits and applicable distribution terms with any copy.

For rendered acceptance, run the `EarthOrbitProbe` group `sky-photographs` with `SPACETANGENT_SKY_PHOTOGRAPHS_PROBE=1` and photograph, DSS and historical planetary-series inputs in its pinned profile. The Crab's recorded birth precedes the local DE440 subset; `SPACETANGENT_VSOP87` is required so loading and reloading those epochs is valid. This group requires all 674 images, then uses the original checks for the Crab before/after its recorded birth, independent-view reload, Orion above the dark ground horizon and survey precedence in the other view. Use an inactive desktop as with the other rendered groups; a unit-only metadata check does not establish this rendered behavior.

## Recover available culture artwork

`scripts/sky/restore_culture_art.py` makes a separate copy of the 33 matching culture directories and restores their pinned upstream artwork. `culture-artwork-source.json` records semantic hashes for every definition, all 299 available image blobs and the six constellation-art references absent from that upstream tree. Whitespace-only metadata differences are accepted; semantic differences fail before copying. Existing image bytes must match the pin and are preserved. The original input directory is never updated.

```powershell
python scripts/sky/restore_culture_art.py C:/TestData/skycultures-source C:/TestData/skycultures-recovered
```

The known partial upstream corpus returns **exit code 2**, with explicit `unavailableUpstream` entries in `provenance.json`; download/integrity failures return 1. A successful recovery of all available source files does not turn six unresolved references into available art. The retained partial source has 128 images; this recipe restores 171 more, yielding 299 available images out of 305 references. Keep the upstream notice and each culture's description/credits with the files.

The original archive regression deliberately retains its 128-image fixture expectation. Use that original snapshot for its `SPACETANGENT_SKY_CULTURES` unit-test binding. The expanded snapshot is a separate input, with its own source hashes and acceptance; changing a fixture's expected population to fit a different installation would erase that distinction. Rendered profiles may bind the recovered `cultures` directory to inspect the additional art.

Use the core repository's `scripts/testing/data_profile.py` to pin optional local inputs before running. See its `docs/TEST_DATA_AND_COVERAGE.md` for integrity checks and independent reference provenance. Keep local paths, profiles and optional assets outside the source repositories. Capturing a profile is not proof of its scientific provenance or permission to redistribute it.

```powershell
./scripts/testing/coverage.ps1 -OutputDirectory C:/TestResults/coverage -CoreDataProfile C:/TestData/core-profile.json -ApplicationDataProfile C:/TestData/application-profile.json -Godot C:/Tools/Godot/Godot_console.exe
```

The runner restores pinned dependencies and Coverlet 6.0.4, builds each unit suite and instruments a separate copy of its binaries. It imports and runs the Godot bootstrap headlessly, using isolated settings files. Do not rebuild an instrumented Godot project concurrently. Raw Cobertura XML, unit TRX and the merged source-line JSON remain under the chosen output directory. `--single-hit` records whether a location executed without counting repeated hits; this reduces overhead in numerical loops.

Godot coverage needs two explicit steps: include the main application assembly with `--include-test-assembly`, and flush the injected trackers while the managed assemblies are still loaded. `tests/CoverageCapture.cs` does that only when `SPACETANGENT_COVERAGE_CAPTURE=1`. It verifies all four required trackers and turns a missing tracker or failed flush into a failing test exit. The hook is excluded from ExportRelease with the other test sources and does nothing in ordinary runs.

For rendered input-smoke coverage, use `scripts/testing/godot-probe-coverage.ps1` with `-Scene res://tests/WorkspaceInputSmoke.tscn -Render` and real science inputs. On a shared Windows workstation, also pass `-IsolatedDesktop`; an unattended dedicated display requires `-DedicatedDisplay`. The tests push events into their own Godot viewport and do not synthesize system mouse input. The headless result alone is a limited baseline, not full rendering acceptance.

For the observing-place regression, put `{"SPACETANGENT_PLACE_MEMORY_SMOKE_ONLY":"1"}` in a probe-options file and pass it with `-ProbeOptions`. Use the workspace inputs plus planetary orientation data. The test clicks the body selector and location fields, checks independent Earth/Moon histories, restarts the saved two-view workspace, reloads real add-ons, opens an explicit scene and closes a view. It also blocks an automatic save, verifies recovery after the path becomes writable, and checks corrupt preferences. Acceptance requires `SpaceTangent observing place memory input smoke passed`. Preferences use the isolated settings path; `SPACETANGENT_OBSERVER_PLACES_PATH` can select a separate file for other integrations.

Coverage evidence belongs to its source revision. Preserve source hashes alongside raw reports. Reuse earlier file hits only when the exact source hash and executable-line set match the current build; collect changed files again. Never merge old line numbers into an edited file merely because its path is unchanged. Distinguish a fresh-run percentage from a cumulative result that includes verified unchanged files, and keep both denominators available.

When `-CoreCheckout` selects a standalone core, the matrix verifies the shared Core/Contracts production inputs and root build settings against the application's dependency before collecting evidence. Cross-checkout comparison normalizes Git's LF/CRLF conversion and records the shared-input fingerprint; before/after snapshots retain each tree's exact bytes, dependency locks, collector version and script sources. Independently measured Native/CLI consumers do not participate in that shared-assembly comparison. A source change during report aggregation also rejects the final matrix status.

`summary.json` lists exactly which modules were measured and the missing source lines. Its combiner rejects empty reports, absent required modules and required modules without recorded hits. Combine additional headless/GPU runs with the core's `coverage_report.py`, requiring Core, Contracts, Application and Godot. Report Native, CLI and engine bindings separately until their own runs are included. Do not label a four-module percentage as coverage of all repository code.

The local matrix enforces 95% for each of the four agreed production modules. Missing test data must not silently improve the percentage; the original optional cases remain in the test suites and their outcomes are reported separately.

The fresh local matrix on 2026-11-22 accepted 93 unit, ExternalData, headless and isolated rendered runs, including explicitly checked refusal contracts. Source snapshots matched before and after the complete run. Only these fresh accepted reports contribute to the following Debug executable-line coverage; earlier pre-fix hits are excluded. These results require the pinned optional corpus and are not the data-free public CI percentages.

| Module | Covered / executable lines | Coverage |
|---|---:|---:|
| Core | 15,668 / 16,472 | 95.119% |
| Contracts | 101 / 101 | 100% |
| Application | 9,580 / 10,065 | 95.181% |
| Godot | 14,033 / 14,768 | 95.023% |

The available unit suites pass 1,039 Core and 732 Application cases per configuration, with 23 and 11 historical-input skips respectively. Five of the 39 original cases at the start of this recovery package now execute; the remaining 34 are listed in the [optional-input inventory](OPTIONAL_TEST_INPUTS.md). New authored wire examples, recent public orbital-element captures and newly generated independent numerical references are identified separately; they do not recreate a missing original archive. Debug/Release and repeated profiles do not multiply these counts.

Native managed IL, the Unity binding and CLI consumers have separate measured denominators. NativeAOT ABI and engine batch acceptance do not measure optimized native machine-code lines. Public CI keeps its own data-free/public-data artifacts and thresholds; the local four-module result does not create a private-data dependency in those jobs.

| Separately measured Debug consumer | Covered / executable lines | Coverage |
|---|---:|---:|
| Pack CLI | 176 / 185 | 95.135% |
| Model CLI | 51 / 53 | 96.226% |
| Native managed wrapper | 56 / 58 | 96.552% |
| Unity managed binding | 14 / 14 | 100% |

The Native follow-up uses a freshly published AOT DLL and 14 passing ABI/PInvoke contracts in each configuration, plus the standalone C client. Its sequential state is now a plain wire struct matching the C header and Unity binding; removing unused record-generated behavior removes one executable source line. A new directory-open failure contract covers the access-denied status path. Debug reaches 56/58 lines; Release independently reaches 48/50 (96%) with the Unity binding at 13/13. These configuration denominators are not merged. The two remaining Native lines are unexpected-exception safety catches. Other editor scripts, packaged engine consumers and performance tooling are not included in the four-module threshold. Rendering profiles keep their image/geometry assertions; C# line coverage does not measure shader execution or visual equivalence.

## Coverage in GitHub Actions

Existing jobs execute the tests on Windows and Linux and publish Cobertura XML, JSON and TRX artifacts. The report tool also appends per-module tables to the GitHub job summary. Core public-data jobs measure Core/Contracts; the Native job measures its managed wrapper and Unity binding and requires 95% for each. Application jobs measure the data-free managed suite. Godot jobs additionally run [the headless public-data collector](../scripts/ci/godot-coverage.ps1) with the pinned DE440 input and publish all four loaded modules.

These CI tables report their actual measured subsets. A public-data headless startup is not the full optional-data/GPU matrix above and does not claim its 95% result. Reproducing the full result in Actions requires an explicitly configured runner with the same permitted datasets and suitable rendering hardware; a separate GPU runner is not provisioned by these workflows.

## Running individual probes

For an individual existing probe, use `scripts/testing/godot-probe-coverage.ps1` with a pinned `-DataProfile`, fresh `-OutputDirectory`, explicit `-Scene`, `-SuccessMarker`, and Godot console path. `-ProbeOptions` accepts a JSON object of string controls/output paths; it cannot override a pinned input. Capture uses isolated settings and an exclusive lock on the Godot instrumentation directory. Build/import happens before instrumentation unless `-SkipBuild` is explicitly chosen for the same unchanged build.

On a shared Windows workstation, pass `-Render -IsolatedDesktop`. The launcher creates an inactive desktop, inherits the runner's verified test-data settings, waits for the child with a bounded timeout and never switches the user's desktop or injects system input. `-DedicatedDisplay` is an explicit alternative for an unattended CI display; rendering without either policy is rejected. Ordinary headless runs need neither option. Test runner refusals with `python -m unittest discover -s scripts/testing -p 'test_*.py'`.

EarthOrbitProbe, EarthAccuracyProbe, MoonLightingProbe, MissionSwarmComputeProbe and RuntimeProfileProbe now flush opt-in coverage too. Each declares the modules it must load; all loaded known production trackers are flushed, and the final combined report still requires all four production modules. A diagnostic probe that does not initialize the application need not load it merely to produce a formal hit. Use `-RequiredModules` to match that probe's explicit contract. Numerical mesh-building waits have a bounded instrumentation-only allowance; ordinary acceptance keeps its original time limits and result assertions.

Accept a profile only after its declared expected exit code (zero by default), its requested success marker, required tracker-flush markers and nonempty required-module hits. A negative contract may explicitly require exit code 1 together with its specific refusal message; an unexpected error is never accepted this way. Keep failed logs/XML for diagnosis and exclude them from accepted merged reports. Capture metadata in `run.json` records the profile checksum and controls; profile capture does not establish asset provenance. Record which optional branches actually ran, including blocked branches, rather than counting source methods as tests.

EarthOrbitProbe supports three focused groups through `SPACETANGENT_COVERAGE_PROFILE`: `planets`, `demo-worlds`, and `recording`. The latter two require their existing explicit controls (`SPACETANGENT_DEMO_WORLDS_PROBE=1` plus `SPACETANGENT_PROCEDURAL_TERRAIN_PROBE=1`, or `SPACETANGENT_RECORDING_PROBE=1`). Each group keeps the original assertions and emits `SpaceTangent Earth coverage profile passed: NAME` only after completing them. Unknown groups and missing controls fail. Planet acceptance needs the documented planetary maps, ring profiles, PCK, center kernels and Mars relief dataset; an incomplete installation is not a successful empty probe. A new movie capture is separate evidence from compatibility with an original archived recording.

Additional focused groups use those same acceptance methods and fail when their required inputs or controls are missing:

| Group | Required controls and inputs | Assertions |
|---|---|---|
| `compact-lens` | `SPACETANGENT_COMPACT_LENS_PROBE=1` | Captured shadow, physical/cinematic disks, opaque foreground/background and two views after reload |
| `observing` | `SPACETANGENT_OPTICAL_FINDERS_PROBE=1`, `SPACETANGENT_EARTH_PLACES` | Ground sky, eyepiece, finders, apparent positions, equatorial mount and night mode |
| `mission-geometry` | `SPACETANGENT_MISSION_GEOMETRY_PROBE=1`, `SPACETANGENT_MISSION_SENSOR_PROBE=1`, `SPACETANGENT_SAMPLED_MISSIONS_ROOT` | Original timed meshes, gaps and recovery; Jason-1 and SORCE native sensor geometry |
| `swarm-particles` | `SPACETANGENT_MISSION_SWARM_GPU=1`, `SPACETANGENT_SWARM_INSTALL`, `SPACETANGENT_PARTICLE_GPU_ROOT` | Independent swarm epochs, archived/source formats, textured particle births and deterministic reload |
| `stellar` | `SPACETANGENT_STELLAR_ARCHIVE`, local planetary/DSC inputs | Near and far stellar cameras, resolved disks, deep-sky catalogue/kind/label filters and reload |
| `ssc-bodies` | `SPACETANGENT_MINOR_MOONS_GPU=1`, `SPACETANGENT_MINOR_MOONS`, `SPACETANGENT_SMALL_RINGS`, `SPACETANGENT_PROBE_SCRIPTED_SSC` | Original minor moon geometry, ring bodies, explicit Lua permission, factory fallbacks and independent attitudes |
| `archive-wire` | Planetary, lunar, Earth and ring maps | Independently authored 30-frame recording, textured bodies, Earth limb, rings, orbits/labels, body points, comet, both stellar fields, resource clearing and corrupt-input rollback |
| `landscapes` | `SPACETANGENT_LANDSCAPES` | Four original panoramas, fog visibility, separate observers and reload |
| `sky-cultures` | `SPACETANGENT_SKY_CULTURE_GPU=1`, `SPACETANGENT_SKY_CULTURES` | Independent names, artwork, five cultures and image differences |
| `sky-grids` | Optional `SPACETANGENT_CONSTELLATION_BOUNDARIES` | Equatorial/ecliptic/galactic layers and optional original boundary segments |
| `sky-assets` | No additional external sky corpus | Authored local detail and overview tiles, cache, dated seam-crossing photograph, clearing and live-camera preservation |
| `surface-tiles` | `SPACETANGENT_SURFACE_TILES`, `SPACETANGENT_TILE_FIXTURE` | Original Mars color/normal pages compared against native region samples, including parent fallback |

The equatorial-mount check uses the terrestrial pole transformed into the world frame at the selected epoch, including precession/nutation, and waits for renderer publication before inspecting uniforms. It retains its angular tolerance rather than comparing the date-dependent pole with a fixed J2000 axis.

The archive-wire probe shares the unit suite's independent wire builder only in test configurations. It supplies binary fields and a bitwise CRC without calling the runtime recorder. ExportRelease excludes that source and all probes. Its short controlled example supplements historical recording acceptance; it does not restore the missing original full-Moon recording. The SSC group records the exact catalogue selection and native resources used: a selected ring-body corpus is not evidence that an unrelated full catalogue with conflicting built-in names imports successfully.

Application CI uploads a separate data-free Cobertura/TRX report on Windows and Linux and runs launch-refusal and model CLI contracts. Set `ST_MODEL_TOOL_DLL` to a built model tool to run its Python acceptance locally. Optional `ST_CLI_COVERLET` and a fresh `ST_CLI_COVERAGE_DIRECTORY` collect each CLI invocation separately using the core's pinned tool; actual child statuses and stderr are preserved in `result.json`. CLI/Native reports have their own denominators. Private GPU data is not supplied to public CI, and no 95% application or Godot gate is claimed from that limited profile.

`StartupCommandProbe.tscn` launches the ordinary main scene and flushes trackers as the scene tree exits. Pass application arguments to the probe runner as `-UserArguments @('--startup-exit', ...)`; they follow Godot's `--` separator and are recorded in `run.json`. The runner validates their count, length and characters before creating evidence. The application keeps its own normal exit code. Use `-ExpectedExitCode 1` only for a deliberate rejection test with an exact command-specific diagnostic marker; both the exit code and marker must match, and all required tracker and source-hit checks still apply. Merely observing the generic probe exit message does not verify a saved file: validate generated scenarios, PNG dimensions, movie frames and recording contents separately.

## Startup preference isolation regression

`res://tests/ObserverPlaceStartupProbe.tscn` creates a preference sentinel and scenario/replay fixtures under a fresh isolated directory, starts the real main scene, and checks the debounce and exit-time save policy. The committed headless suite runs six modes: normal, defaults, scenario, replay, smoke, and scenario save with `--startup-exit`. Godot CI runs this suite on Windows and Linux using only the pinned public DE440 kernel and Python 3.12.

```powershell
python scripts/testing/startup_place_suite.py --godot C:/Tools/Godot/Godot_console.exe --project src/SpaceTangent.Godot --de440 C:/TestData/de440.440 --output C:/TestResults/startup-places
```

Build and import the Godot project first. The suite never captures input or opens a rendered window. Each mode has separate settings, bookmarks, add-ons and place files. After native process exit, the outer runner requires exit 0, the exact success marker, no engine/script/shader errors, a successful probe result, and the correct sentinel bytes. Normal startup must read its saved place, edit it and persist that edit; the other five modes must preserve the sentinel. Save-and-exit additionally needs a scenario accepted by the real `ScenarioFile` loader. Raw process/engine logs and `status.json` remain in the output, including on failure. A late shutdown failure cannot pass merely because the host returned 0.

The same probe remains usable with the instrumented GPU runner and an inactive desktop. Set `SPACETANGENT_EARTH_PROBE_OUTPUT` through its probe options and require the normal or batch policy marker and coverage tracker checks. The probe propagates coverage-flush failures into its result instead of ignoring `CoverageCapture.Complete`.

Application regressions separately verify that large current-format recordings replay, shared links retain active cameras without private place histories or persistent view keys, scene files preserve that history, oversized bookmarks fail before replacement, and only old temporary files belonging to the exact preferences destination are cleaned.

## Resource ownership and publication regressions

Run the six committed resource probes together with a hash-pinned workspace profile containing DE440, Jason-1/SORCE science inputs and the original Cassini default mission. The mission also needs the default installation files described in [Cassini installation](CASSINI_MISSION.md); a profile alone does not copy those files into a clean checkout. Missing real inputs fail the probes. On a shared Windows computer:

```powershell
./scripts/testing/resource-probes.ps1 -DataProfile C:/TestData/workspace.json -OutputDirectory C:/TestResults/resources-01 -Godot C:/Tools/Godot/Godot_console.exe -IsolatedDesktop
```

Use a new output directory each time. The suite builds/imports once, runs each probe with coverage in a separate process, and accepts only exit 0, its exact marker, required tracker hits and no engine/script/shader errors. It records progress and failures in `status.json`, raw logs and per-probe XML. It never switches desktops or synthesizes system mouse input. Linux rendered acceptance requires an explicitly dedicated display. These six reports supplement the full source-checked coverage matrix; they do not establish the four-module 95% threshold on their own.

The matrix, individual-probe and resource-suite runners resolve relative input, options and output paths from the caller's PowerShell location, before changing directories. `-Godot` accepts an executable name from `PATH`, an absolute path or an explicit relative path; explicit paths take precedence over a same-named executable on `PATH`. This also works after interactive `Set-Location`, when the process current directory differs from `$PWD`.

| Probe | Required evidence |
|---|---|
| GpuAssetOwnershipProbe | Abandoned/committed/shared dependencies and 32 alternating updates; native ObjectDB identities independently of disposed C# wrappers; a node keeps a mesh alive until its last native reference is released |
| AddonLifecycleProbe | Overlapping real loads, supersession, stable observers and close during preparation |
| AddonPreferenceFailureProbe | Preference-save rejection rolls back the world and observer state |
| AddonBindingFailureProbe | Faults after preferences commit, before/among/after two view bindings; bounded retention, repair, subsequent source toggles, close and late callbacks |
| ClosedViewportProbe | Temporary detach/reattach preserves drawing; final close destroys owned resources and rejects late draw/replace/query work without resurrection |
| ResourceLifecycleProbe | 24 split/no-op cycles plus 24 actual Cassini publications; deletion of previously drawn native mission meshes, unchanged shared Jason-1 mesh, both cameras/clocks/ΔT; final node/resource counts return to a warmed baseline |

The soak writes `memory.json`; native node counts must match the warmed baseline, and resource counts may exceed it by at most two cache entries. Explicit GC/finalizer drains make managed bindings settle and are outside performance measurements. Private/managed byte samples remain diagnostics: allocator reservations and driver VRAM are not bounded by this test. Native ObjectDB deletion establishes release of engine objects, not the timing of driver memory reclamation.

Public Windows/Linux Godot CI runs GpuAssetOwnershipProbe headlessly alongside BootstrapSmoke in the public-data coverage job. Their separate raw reports are uploaded and combined into that limited headless table. The remaining five probes use real optional add-ons and rendered workflows, so they belong to the local resource suite and full GPU matrix rather than the data-free CI profile.

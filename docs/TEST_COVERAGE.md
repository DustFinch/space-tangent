# Source-line coverage

Coverage measures executable lines in the authored C# modules. Unit tests, generated sources and third-party libraries are excluded. Shader execution and image correctness require separate GPU acceptance. A high combined percentage must not hide an omitted Godot module or an untested Native/CLI consumer.

Use the core repository's `scripts/testing/data_profile.py` to pin optional local inputs before running. See its `docs/TEST_DATA_AND_COVERAGE.md` for integrity checks and independent reference provenance. Keep local paths, profiles and optional assets outside the source repositories. Capturing a profile is not proof of its scientific provenance or permission to redistribute it.

```powershell
./scripts/testing/coverage.ps1 -OutputDirectory C:/TestResults/coverage -CoreDataProfile C:/TestData/core-profile.json -ApplicationDataProfile C:/TestData/application-profile.json -Godot C:/Tools/Godot/Godot_console.exe
```

The runner restores pinned dependencies and Coverlet 6.0.4, builds each unit suite and instruments a separate copy of its binaries. It imports and runs the Godot bootstrap headlessly, using isolated settings files. Do not rebuild an instrumented Godot project concurrently. Raw Cobertura XML, unit TRX and the merged source-line JSON remain under the chosen output directory. `--single-hit` records whether a location executed without counting repeated hits; this reduces overhead in numerical loops.

Godot coverage needs two explicit steps: include the main application assembly with `--include-test-assembly`, and flush the injected trackers while the managed assemblies are still loaded. `tests/CoverageCapture.cs` does that only when `SPACETANGENT_COVERAGE_CAPTURE=1`. It verifies all four required trackers and turns a missing tracker or failed flush into a failing test exit. The hook is excluded from ExportRelease with the other test sources and does nothing in ordinary runs.

Rendered input-smoke coverage uses `-Scene res://tests/WorkspaceInputSmoke.tscn -Render` on a dedicated CI desktop with real science inputs. The tests push events into their own Godot viewport and do not synthesize system mouse input. The runner itself does not isolate the Windows desktop: when using a shared workstation, launch the rendered process on an inactive test desktop or use the headless default. The headless result alone is a limited baseline, not full rendering acceptance.

`summary.json` lists exactly which modules were measured and the missing source lines. Its combiner rejects empty reports, absent required modules and required modules without recorded hits. Combine additional headless/GPU runs with the core's `coverage_report.py`, requiring Core, Contracts, Application and Godot. Report Native, CLI and engine bindings separately until their own runs are included. Do not label a four-module percentage as coverage of all repository code.

The initial goal is a stable baseline with no regression. A 95% gate should be enabled only after it is actually reached for the agreed modules; missing test data must not silently improve the percentage.

For an individual existing probe, use `scripts/testing/godot-probe-coverage.ps1` with a pinned `-DataProfile`, fresh `-OutputDirectory`, explicit `-Scene`, `-SuccessMarker`, and Godot console path. `-ProbeOptions` accepts a JSON object of string controls/output paths; it cannot override a pinned input. Capture uses isolated settings and an exclusive lock on the Godot instrumentation directory. Build/import happens before instrumentation unless `-SkipBuild` is explicitly chosen for the same unchanged build.

On a shared Windows workstation, pass `-Render -IsolatedDesktop`. The launcher creates an inactive desktop, inherits the runner's verified test-data settings, waits for the child with a bounded timeout and never switches the user's desktop or injects system input. `-DedicatedDisplay` is an explicit alternative for an unattended CI display; rendering without either policy is rejected. Ordinary headless runs need neither option. Test runner refusals with `python -m unittest discover -s scripts/testing -p 'test_*.py'`.

EarthOrbitProbe, EarthAccuracyProbe, MoonLightingProbe, MissionSwarmComputeProbe and RuntimeProfileProbe now flush opt-in coverage too. Each declares the modules it must load; all loaded known production trackers are flushed, and the final combined report still requires all four production modules. A diagnostic probe that does not initialize the application need not load it merely to produce a formal hit. Use `-RequiredModules` to match that probe's explicit contract. Numerical mesh-building waits have a bounded instrumentation-only allowance; ordinary acceptance keeps its original time limits and result assertions.

Accept a profile only after a zero exit code, its requested success marker, required tracker-flush markers and nonempty required-module hits. Keep failed logs/XML for diagnosis and exclude them from accepted merged reports. Capture metadata in `run.json` records the profile checksum and controls; profile capture does not establish asset provenance. Record which optional branches actually ran, including blocked branches, rather than counting source methods as tests.

EarthOrbitProbe supports three focused groups through `SPACETANGENT_COVERAGE_PROFILE`: `planets`, `demo-worlds`, and `recording`. The latter two require their existing explicit controls (`SPACETANGENT_DEMO_WORLDS_PROBE=1` plus `SPACETANGENT_PROCEDURAL_TERRAIN_PROBE=1`, or `SPACETANGENT_RECORDING_PROBE=1`). Each group keeps the original assertions and emits `SpaceTangent Earth coverage profile passed: NAME` only after completing them. Unknown groups and missing controls fail. Planet acceptance needs the documented planetary maps, ring profiles, PCK, center kernels and Mars relief dataset; an incomplete installation is not a successful empty probe. A new movie capture is separate evidence from compatibility with an original archived recording.

Additional focused groups use those same acceptance methods and fail when their required inputs or controls are missing:

| Group | Required controls and inputs | Assertions |
|---|---|---|
| `compact-lens` | `SPACETANGENT_COMPACT_LENS_PROBE=1` | Captured shadow, physical/cinematic disks, opaque foreground/background and two views after reload |
| `observing` | `SPACETANGENT_OPTICAL_FINDERS_PROBE=1`, `SPACETANGENT_EARTH_PLACES` | Ground sky, eyepiece, finders, apparent positions, equatorial mount and night mode |
| `mission-geometry` | `SPACETANGENT_MISSION_GEOMETRY_PROBE=1`, `SPACETANGENT_MISSION_SENSOR_PROBE=1`, `SPACETANGENT_SAMPLED_MISSIONS_ROOT` | Original timed meshes, gaps and recovery; Jason-1 and SORCE native sensor geometry |
| `swarm-particles` | `SPACETANGENT_MISSION_SWARM_GPU=1`, `SPACETANGENT_SWARM_INSTALL`, `SPACETANGENT_PARTICLE_GPU_ROOT` | Independent swarm epochs, archived/source formats, textured particle births and deterministic reload |

The equatorial-mount check uses the terrestrial pole transformed into the world frame at the selected epoch, including precession/nutation, and waits for renderer publication before inspecting uniforms. It retains its angular tolerance rather than comparing the date-dependent pole with a fixed J2000 axis.

Application CI uploads a separate data-free Cobertura/TRX report on Windows and Linux and runs launch-refusal and model CLI contracts. Set `ST_MODEL_TOOL_DLL` to a built model tool to run its Python acceptance locally. Optional `ST_CLI_COVERLET` and a fresh `ST_CLI_COVERAGE_DIRECTORY` collect each CLI invocation separately using the core's pinned tool; actual child statuses and stderr are preserved in `result.json`. CLI/Native reports have their own denominators. Private GPU data is not supplied to public CI, and no 95% application or Godot gate is claimed from that limited profile.

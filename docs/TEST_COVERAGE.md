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

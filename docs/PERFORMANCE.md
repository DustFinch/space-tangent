# Runtime profiling

Performance probes use fixed inputs and report time and allocations separately from numerical or image accuracy. Ordinary runs do not collect timing samples.

## Catalogue search

This standalone benchmark builds 100,000 synthetic stars and compares complete and bounded queries, without Godot or external data:

```sh
dotnet run --project tools/SpaceTangent.Performance -c Release -- 100000
```

The JSON output includes the runtime, count, query, median/p95/maximum milliseconds and allocated bytes. A bounded query retains at most the requested number of entries per rank while preserving the total count and filters. Substring search still scans the catalogue; this change primarily removes large intermediate result allocations. Timing thresholds are not CI assertions.

## Startup, tracks and rendering

With source-built Godot, set `SPACETANGENT_PROFILE=1` and `SPACETANGENT_PROFILE_OUTPUT` to an output directory, install the local data described in [Windows build](WINDOWS_BUILD.md), and run:

```sh
godot --path src/SpaceTangent.Godot res://tests/RuntimeProfileProbe.tscn
```

Set `SPACETANGENT_STARTUP_WORKSPACE` to a paused Jason-1/full-Moon scenario to include that two-view workload. The probe writes timing JSON and images for physical and cinematic lens views at full and half 3D resolution. Frame samples include a framebuffer readback and measure wall time, not GPU timestamps. They should be compared on the same hardware, driver, inputs and resolution. Test scenes are excluded from exported releases.

Ground-map resizing and mipmaps, planetary decoding, and Earth/observer map decoding run on workers. GPU publication stays on the main thread and yields between batches, with cancellation and scene-owner checks. A single texture upload cannot be preempted; the batch allowance is not a hard maximum for an individual upload. Images prepared for the ground array are checked against the former readback/resize path.

Mission trails and selected-body paths keep their complete published arrays while preparing replacements in main-thread slices. Ordinary slices have a soft three-millisecond allowance and at most eight samples per frame. Identical jobs can be shared within one world generation; changes to the world, target, centre, style or Delta T discard incompatible results.

A mission trail starts its replacement after half of the previous `span / 256` age allowance. If the published epoch would exceed that allowance, the remaining samples finish synchronously before drawing. A pending job whose own epoch has already expired is replaced before spending another slice on it. The worst ordinary update is the previous full 65-point computation, rather than an indefinitely missing trail at high time warp. Initial display may take several frames, but a changing epoch cannot starve it indefinitely.

The selected-body path is keyed by calendar day. Once that day is published, the next day in the observed time direction is prepared incrementally. At a day boundary any unfinished required work completes synchronously; jumps or direction changes fall back to the requested day. An already published path therefore always belongs to the current day. Its samples outside the supported epoch range remain gaps, and prefetch does not request an invalid day.

Movie recording, queued user or script screenshots, and replay require complete paths before rendering the captured frame, including a paused scene or a capture requested after an ordinary slice in the same host frame. Stateful add-on Lua retains synchronous evaluation order. These correctness fallbacks can take a full 65-point mission or 121-point body-path calculation in a frame; the slice budget is not a hard latency guarantee. Each sample still evaluates the complete graph, so total numerical work has not been eliminated.

`FrameSampleChecks` runs without external data in `BootstrapSmoke`. It covers 3,000 fixed frames per rate, forward and reverse time up to 1,000,000x, age/day invariants, key invalidation, sample-count limits and same-frame synchronous completion. The real-data `EarthOrbitProbe` mission-display and selected-body-path cases additionally check every rendered frame at high warp. The mission case verifies endpoint states against full snapshots, first-frame user/script PNG trail pixels, recording and replay; the body-path case checks current-day publication and independent views.

## Optional lens resolution

Set `SPACETANGENT_LENS_RENDER_SCALE=0.5` before launch to reduce the 3D buffer in views with compact-object lensing. Accepted values are 0.5–1.0; full resolution is the default. Ordinary views restore full resolution. The physical integrator, viewport coordinates, object picking and 2D interface are unchanged; the rendered 3D image loses spatial resolution. A lower scale is not guaranteed to improve every scene.

This option uses Godot's viewport 3D scaling; it does not reduce Kerr integration steps or assume that disabling cinematic shading disables the Kerr branch. Acceptance checks compare the physical image, preserve picking coordinates, and measure both disk modes. Results on a discrete GPU do not establish performance on an integrated GPU or software renderer.

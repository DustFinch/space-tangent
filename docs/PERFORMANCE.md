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

Mission trails and selected-body paths sample in main-thread slices with a soft three-millisecond allowance and at most eight samples per view update. Completed arrays are shared only for identical requests in the same world generation. No partial or obsolete array is published. Large time jumps can replace pending work; the current path completes once the request remains stable. Stateful add-on Lua and recorded replay retain their synchronous evaluation order. Each sample still evaluates the complete graph, so the total numerical work has not been eliminated.

## Optional lens resolution

Set `SPACETANGENT_LENS_RENDER_SCALE=0.5` before launch to reduce the 3D buffer in views with compact-object lensing. Accepted values are 0.5–1.0; full resolution is the default. Ordinary views restore full resolution. The physical integrator, viewport coordinates, object picking and 2D interface are unchanged; the rendered 3D image loses spatial resolution. A lower scale is not guaranteed to improve every scene.

This option uses Godot's viewport 3D scaling; it does not reduce Kerr integration steps or assume that disabling cinematic shading disables the Kerr branch. Acceptance checks compare the physical image, preserve picking coordinates, and measure both disk modes. Results on a discrete GPU do not establish performance on an integrated GPU or software renderer.

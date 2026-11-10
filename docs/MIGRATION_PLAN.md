# SpaceTangent migration plan

The migration plan is approved. Each milestone is accepted against its verification criteria.

Use the legacy checkout at `E:/Projects/karyonika/gamedev/space_tangent_legacy` as a reference for behavior, source provenance and tests. The refactor inputs are `docs/review/13-local-fix-progress.md` and `godot-dotnet/docs/LOCAL_FIX_LIMITS.md` in that checkout. Each step below has a way to verify its result.

## 1. Engine-independent math, time and identity

- Agree on units, reference frames and error contracts before adding engine adapters. Use one set of constants and conversions, one set of vector and rotation primitives, distinct object and frame IDs, and an explicit policy for rigid rotations and native-format quaternions.
- Port exact epoch and position arithmetic with the existing precision. Keep time-scale conversion and the selected ΔT model explicit. Cover limits, rounding, leap-second handling and round trips using independent expected values.
- Verify every public core contract from a headless consumer. Keep types that have a single implementation private until a caller needs them.

Review coverage: 01#9, 01#10, 01#25–28, 02#4–6, 02#9, 04#27 and 05#20.

## 2. First complete simulation

- Load the minimum verified ephemeris data for Sun, Earth and Moon. Confirm the legacy DE406 and VSOP87 data actually needed for this slice, check its source and redistribution terms, and put production data with its owner and checksum.
- Produce position and velocity at a requested epoch in a named frame through the core. Display these three bodies in a minimal Godot scene with an independently controlled clock.
- Compare positions and velocities to independent references at representative dates and data boundaries. Run headless core tests and a Godot smoke test from the pinned core commit.

Review coverage: 02#4–6, 09#12 and 09#22. Carry over generator inputs only when an active build or data pipeline consumes them.

## 3. Orbits, frames and add-on import

- Establish one representation of orbital elements and units. Consolidate Kepler and Hermite implementations where numerical and compatibility tests agree. Keep explicit policies for interpolation boundaries, out-of-range epochs and legacy solver behavior. Add analytic satellite derivatives only with a reliable independent velocity reference and an active consumer.
- Evaluate translation and orientation through a shared dependency graph with cycle diagnostics. Make snapshots, dynamic frames and sampled paths query the same model at the requested epoch. Compare body-fixed, two-vector and mean-equator frames at fixed epochs and test nested dependencies.
- Add format adapters one at a time. Keep external syntax at the boundary, use one SPICE name registry and one structured diagnostic contract, and review fixture and asset licenses. Start with real Jason-1, then use SORCE and Cassini to cover distinct import paths as they land. Exercise local terrain assets with a representative real pack when terrain support is added.
- Add exact cache dependencies as imports gain cross-pack lookups. Record failed lookups as well as successful ones. Test content changes with preserved size and modification time, and make reload publish an immutable world generation atomically.

Review coverage: 02#7, 02#13, 02#16, 03#22, 03#26, 03#28–29, 04#11, 04#26, 04#28, 04#31, 04#33, 04#39, 05#21 and 06#32.

## 4. Application state, rendering and interaction

- Build the application layer around immutable world snapshots and per-view observer, clock, camera and selection. Add a shared-clock mode. Keep render contracts data-only and map them to Godot objects in the renderer. Keep panels and actions focused on their own state and use typed test entry points.
- Verify two Jason-1 views with independent epochs, synchronized time, camera mode, selection and ΔT across add-on reload. Move expensive CPU preparation off the main thread with cancellation tied to the world generation. Publish Godot resources on its main thread.
- Add rendering in complete slices. Start with bodies and textures, then atmosphere, terrain and lunar lighting of clouds. Share shader and mesh logic where the rendered model is equivalent. Check the same full-Moon scene, camera and epoch used in the legacy project.
- Add controls, formatting, saved scenarios and Lua features when the corresponding workflow needs them. Keep compatibility adapters at file and script boundaries. Use a single event path and test script quotas when Lua lands. Keep legacy milestone scaffolding, unused helpers and generated reports out of the new production tree.

Review coverage: 01#8, 01#27–28, 05#6, 05#22, 05#25, 06#27, 07#9, 07#21, 07#24–25, 08#26, 08#28 and 09#21.

## 5. Headless embedding and release

- After the simulation API has a real consumer, validate one Win64 NativeAOT export with a versioned C ABI. Specify layout, ownership, precision and errors before exposing more functions. Test loading and querying the same epoch in Unity and Unreal. Each engine owns its presentation.
- Build both repositories from clean checkouts using the committed lock files and submodule revision. Run unit, scientific reference, real add-on and Godot smoke checks. Record supported input formats, data versions and outstanding compatibility limits in the release notes.
- Keep HTTP API, network MultiWMS and an out-of-process Lua runtime in the deferred scope.

## Commit and verification rules

- Use the expected 50–200 commits per repository as a planning range. Let cohesive, usable changes determine the actual count.
- Keep a normal commit to at most roughly 1,000–2,000 changed lines of authored code. A larger cohesive feature may exceed the range when splitting it would leave incomplete functionality.
- Each commit builds and passes the relevant tests from its own checkout. Application commits that change the core pin refer to a tested core commit. Run the relevant real-data case for changes to supported external inputs.
- Before porting a feature, identify its live callers, inputs and test oracles. Preserve required names, license notices and attributions through reviewed literal exceptions. Copy or rewrite only the pieces the new feature uses.
- Compare scientific behavior against independent oracles when possible. Record intentional differences, reference epochs and tolerances. Close review entries based on verified behavior.

# Windows release acceptance

The simulator and its engine-independent core live in separate repositories. The simulator pins its core revision as a Git submodule. Build and export with Godot 4.7.2 .NET and the SDK selected by `global.json`. The Windows package uses the Compatibility renderer and local data beside the executable. [Windows build instructions](WINDOWS_BUILD.md)

## Verified inputs and workflows

| Area | Verified scope |
| --- | --- |
| Solar ephemeris | JPL DE440, geometric J2000 position and velocity for the Sun, Earth and Moon. The original classic file and the prepared 1900–2100 subset have explicit coverage and SHA-256 checks. |
| Earth satellites | Original Jason-1 and SORCE, including TLEs, two-vector orientation and local OBJ, MTL and PNG assets. |
| SSC and SPICE | Original Cassini SSC and version 1.0 SPICE mission JSON, SPK types 1–3 and CK type 3 pointing, local metakernels, camera frusta and Saturn footprints. The sampled SSC includes the fifteen Cassini phases and the three Huygens phases. Ordered SSC overrides work for these adapters. |
| Scientific frames | Body-fixed and two-vector frames, mean equators with an optional frozen epoch, and velocity transport through the world graph. The core checks the original Phobos and Deimos conics in the moving Mars mean equator. |
| Local lunar terrain | NASA LRO LOLA LDEM_16 prepared as a signed 4096 × 2048 grid, with the Moon orientation from NAIF `pck00011.tpc`. The globe and regional views save their own coordinates, scale and time. |
| Earth surface | The original full-Moon observer preset, local cloud and lunar textures, solar and lunar cloud lighting, and single-scattering Rayleigh and aerosol profiles. |
| Application state | Independent or synchronized clocks, selections, camera modes, ΔT, saved scenarios, atomic reload and typed import diagnostics. File content changes with preserved size and timestamps and previously missing inputs are exercised. |
| Lua | In-process observer scripting, including the original pause script and time and instruction quotas. |
| Embedding | Win64 NativeAOT C ABI 1.0, a standalone C executable, Unity 6000.3.18f1 editor and Unreal 5.8.4 Python commandlet. Each editor queries all six components of the same geocentric lunar state at J2000. |

External catalogues, textures, meshes and kernels retain their own distribution terms. The source repositories and the Godot resource pack contain code and committed reference fixtures. Local asset setup and provenance are documented separately. [Science add-ons](SCIENCE_ADDONS.md) · [Cassini and Huygens](CASSINI_MISSION.md) · [Lunar terrain](LUNAR_TERRAIN.md) · [Full-Moon observation](MOON_LIGHTING.md)

## Acceptance checks

- Restored both clean checkouts in locked mode with the simulator's pinned core. Ran 144 core tests and 2 application tests. The scientific fixtures include independent CSPICE, conic, timing and SGP4 references.
- Ran the simulator's bootstrap suite with real SSC and JSON inputs. Ran the render checks on an RTX 3090, including the shared Jason-1 and full-Moon scenario, two-view reload and the Earth atmosphere comparison against independent numerical quadrature.
- Exported the Windows Desktop preset from the clean simulator checkout. Launched the exported package with local data beside the executable.
- Published the Win64 native library from the clean core checkout. Ran the C ABI smoke and loaded the same DLL in both editors. The versioned ABI header and editor smoke entry points live in the core repository.

To save the restored two-view scenario during the GPU bootstrap test, set `SPACETANGENT_SCENARIO_CAPTURE` to an absolute PNG path. The headless test runs without that variable.

## Compatibility limits

The import surface is the set of formats and workflows above. Arbitrary SSC body models, Lua orbit and rotation factories, and general mission JSON are outside the verified adapters. The Earth atmosphere uses single scattering. The Unreal check runs inside the editor through its Python Script Plugin. A compiled Unreal C++ module and packaged Unity and Unreal builds require separate validation.

HTTP API, network MultiWMS and an out-of-process Lua runtime are deferred under the migration plan.

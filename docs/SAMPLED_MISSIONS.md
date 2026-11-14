# Sampled mission catalogues

Place local version 1.0 JSON catalogues in `data/sampled-missions` or set `SPACETANGENT_SAMPLED_MISSIONS_ROOT` to the directory. Keep each trajectory and mesh at the relative path declared in its JSON. Use **Reload add-ons** after changing a file. The current spacecraft search includes the imported missions, and **Go to** and **Track** work during their available arcs.

The reader supports XYZ and XYZV curves, fixed points, changing centers, Ecliptic J2000 and equatorial inertial frames, body-fixed frames and two-vector frames built from relative position and velocity. Arc end times are exclusive. A mission remains available in covered arcs when data for a different arc are unavailable. MSL's landed arc requires a Mars orientation source, and Dawn's Vesta arc requires the local Vesta ephemeris.

A missing optional mesh leaves a marker available and is reported in the log. An unreadable required trajectory rejects the replacement generation and preserves the last loaded world and cameras. The original MSL and Dawn trajectories have been checked against the archived simulator. The tested archive lacks their spacecraft meshes. Dawn's separate particle system is reported as unsupported by this reader.

## Compound tracks and timed models

The same loader accepts fixed spherical positions, Keplerian ellipses, CHEBPOLY files, weighted combinations, and composite trajectories with explicit boundaries. The available analytic moon theories provide the equatorial built-in fallback for supported satellites. Fixed and uniform rotations can be composed with each arc's declared body frame.

Mesh phases can switch at explicit TDB epochs, include nested switches and leave a view with no geometry while the trajectory continues. Optional resources remain relative to the file that defines them. The timed geometry probe uses the original verification catalogue with the original Jason-1 and SORCE OBJ meshes.

The loader visits local `require` entries first and opens each catalogue once. It rejects a dependency cycle. When an entry's file is absent, it reports the missing file and resolves its references against the installed world, as in the archived source loader. Removing a required object rejects the generation when a remaining definition still refers to it.

## Local sensor geometry

A `Sensor` uses a resolved target, a range in kilometres, rectangular or elliptical fields, full horizontal and vertical angles in degrees, and an optional local WXYZ quaternion. The field follows its item's declared body frame. Timed geometry may switch a sensor on and off. In the spatial view the visible grid clips each ray against the target ellipsoid and the instrument range.

The archived sensor fixture attaches authored fields to the original Jason-1 and SORCE spacecraft. The checks use both the source OBJ catalogue and the archived GLB installation, preserving the fixed spacecraft attitude in either case.

An `Observations` item names a local sensor and the target it is centered on. Positive `obsRate` gives individual footprints at that interval in seconds. Zero gives a continuous swath with configurable `alongTrackDivisions`. A footprint is kept when every boundary ray reaches the surface within range. Each observer sees the footprints collected by its own epoch. `showResWithColor` colours them by their mean range to the surface.

## Local SPICE tracks

Catalogues in one sampled mission directory can declare an ordered `spiceKernels` list. A companion catalogue may contain only kernels. The reader resolves geometric SPK positions and velocities in J2000 across kernel centers. It rotates states in built-in inertial frames, text PCK body-fixed frames, and fixed FK chains, including the velocity contribution from rotating axes. It accepts CK types 2 and 3 with local clocks and inertial, fixed, text PCK or nested CK references. Available angular velocities accompany the orientation and contribute to state velocities in rotating axes. Rectangular instrument fields follow the parent attitude. The original Cassini mesh and both ISS cameras were tested through this ordinary mission entry point.

### Particle systems

The mission reader accepts Point, Box, Strip, Disc and ArcStrip emitters in a local source catalogue. It evaluates particle ages, positions and colours from each view's time. PNG and JPEG textures use the declared local resource. A missing texture falls back to a generated spot with an import diagnostic.

For the archived native particle format, place the original `pack.json` and its `textures` directory together under the sampled mission root. The reader accepts stationary packed emitters with the original source axes, including moving body-fixed placements and two-vector orientation. The renderer uses additive blending with depth testing and a budget of 200,000 particles per view.

## Archived mission packs

A local mission root can also hold an existing `pack.json` alongside its `models`, `trajectories`, and optional `textures` directories. Preserve the archive paths. The checked version 11 pack supplies fixed and Keplerian states, sampled XYZ and XYZV, moving frames, Q attitudes, oblate globes and embedded GLB textures. A damaged Q table and its dependent objects are reported and omitted. A missing optional globe map leaves the shape available. Body and frame axes use the original solid arrow profiles and physical scale in both source and packed catalogues.

To validate packs that share kernels across manifests, run PackTool from the simulator checkout. List the manifests relative to their common installation root in the order used by the installation.

```sh
dotnet run --project deps/space-tangent-core/tools/SpaceTangent.PackTool -- check-missions "/path/to/installation" 00-orbits/pack.json 99-frames/pack.json --strict
```

The checker resolves the built-in planets and moons. For a body supplied by another catalogue, pass `--body <name>`. The archive probe uses the packed version of the original Jason-1 model at the same two attitude epochs as the source probe.

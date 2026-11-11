# Cassini at Saturn

Place the original mission JSON or SSC catalogue as `cassini.json` or `cassini.ssc` in `src/SpaceTangent.Godot/.local-data/mission` for source builds or `data/mission` next to the exported executable. Keep the 3DS model and kernel paths relative to the catalogue. `SPACETANGENT_MISSION_CATALOG` can point directly to a catalogue. If both default filenames are present, the JSON catalogue takes precedence.

Choose **Cassini at Saturn** in either view. If the current epoch lacks trajectory or pointing data, the clock seeks to the verified orbit-insertion preview. The view uses the Saturn barycenter as its origin and exaggerates the spacecraft model for visibility. Each observer reads the SPK position and CK orientation at its own epoch. During a pointing gap, the model is hidden and the epoch label reports the gap.

The original local model has SHA-256 `342B3E2DCB789C0E2C638081B0BE1BAACF5C9DCC75A9C9456DD20B1B331198D1`. Its 15,750 faces are grouped into ten diffuse material surfaces. The decoder handles static untextured triangle geometry. The catalogue, model and kernels stay external and retain their own distribution terms.

The JSON catalogue displays both camera frusta. To display Saturn's ellipsoid and trace the camera footprints, place `sat480.bsp` and `pck00011.tpc` next to the catalogue. The nearest hit within each camera's range shortens its ray. Boundary samples that hit the surface form the footprint outlines. Between a hit and a miss the outline stops at the nearest sampled hit. With either optional kernel absent, the view uses the Saturn barycenter marker and draws the full-range frusta.

[NAIF Saturn ephemeris](https://naif.jpl.nasa.gov/pub/naif/generic_kernels/spk/satellites/sat480.bsp) · [NAIF IAU body orientation and radii](https://naif.jpl.nasa.gov/pub/naif/generic_kernels/pck/pck00011.tpc)

The **Cassini timeline** camera loads the original 15-phase sampled trajectory alongside the SPICE mission. Put the source world layout under `src/SpaceTangent.Godot/.local-data/timeline` for source builds or `data/timeline` beside the executable. It needs `ssc/cassini.ssc`, `models/cassini.3ds` and the 15 original Cassini XYZV files under `models/trajectories`. Alternatively, set `SPACETANGENT_TIMELINE_ROOT` to the existing world root.

Use the **JD TDB** field and press Enter to seek. The first center change is at JD 2453167.5. Both views select their phases independently with independent clocks. The view adapts its scale to the spacecraft's distance from the active center. Saving a scenario retains the timeline camera and exact epoch.

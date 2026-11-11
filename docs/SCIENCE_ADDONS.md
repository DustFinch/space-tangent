# Viewing Jason-1 and SORCE

Point `SPACETANGENT_SCIENCE_ROOT` at a local science add-on root. For a local checkout the default is `src/SpaceTangent.Godot/.local-data/science`. The expected layout is described in the [core catalogue notes](../deps/space-tangent-core/docs/SCIENCE_CATALOG.md).

Open **Earth satellites** to see the original Jason-1 and SORCE meshes with their diffuse maps. The view uses the catalogue's Earth-centred compatibility frame. The mesh sizes are symbolic and the orbital distances use a linear scale. If the current clock is outside the first satellite's interval, opening the view moves it to that satellite's TLE epoch.

Run the headless smoke scene with both `SPACETANGENT_DE440` and `SPACETANGENT_SCIENCE_ROOT` set. It checks both models' triangle, material and texture counts, opacity and movement when the clock advances.

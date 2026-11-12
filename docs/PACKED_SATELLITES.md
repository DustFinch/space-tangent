# Packed science satellites

An existing Science folder can contain either the original `catalog/sciencesat.json` and OBJ resources or a legacy `pack.json` with local spacecraft models. The packed manifest takes precedence when both are present. **Reload add-ons** switches between them while preserving the spacecraft IDs and the existing view state.

The supported packed profile has a single Earth-centered TLE per spacecraft and a position/velocity two-vector attitude. The local GLB decoder reads the pack tool's flattened triangle meshes with embedded PNG materials. This profile preserves the original mesh axes, diffuse materials, dimensions and orbit. Other entries in a mixed pack are reported in the log.

The original Jason-1 and SORCE packs passed source comparisons, round-trip reload and GPU checks with 28 and 20 rendered surfaces respectively.

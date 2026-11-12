# Spatial stars and deep sky

The optional `data/sky/spatial-stars.tsv` sidecar provides positions for stars with catalogued distances. In a source checkout use `src/SpaceTangent.Godot/.local-data/sky/spatial-stars.tsv` or set `SPACETANGENT_SPATIAL_STARS` to the file. The first line declares the columns:

```text
id  name  dist_pc  x_pc  y_pc  z_pc  vx_pc_y  vy_pc_y  vz_pc_y  v_mag  b_v  r_km
```

Use tabs as delimiters. Positions are heliocentric ICRS coordinates at J2000 in parsecs. Velocities are parsecs per Julian year, including tangential and measured radial terms where available. The visual radius is an estimate when the source lacks a direct measurement. Direction-only stars remain available through the bundled bright catalogue. The Earth observer retains its original stars, labels and figures.

The verified sidecar is adapted from [HYG 4.1](https://github.com/astronexus/HYG-Database/tree/c7f7f883fe678cc7680169a50ccd7dcc49b060ce). Credit David Nash / Astronexus and distribute the adapted data under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/).

Search can fly to Proxima Centauri and other stars with a spatial entry. At interstellar camera positions, the star field uses the camera's position rather than fixed Earth directions. The **Deep sky** filter and the **Deep-sky objects** layer expose M31, M42 and M45. Their distances and extents are approximate, and their renderings represent the three object classes schematically.

## Diffuse galactic light

The existing deep-sky layer also displays the Milky Way. It uses the observer's position to integrate the stellar disk, arms, bar and dust, with independent maps for separated viewpoints. The same light is dimmed by the atmosphere and sky glow from the ground. The first map is calculated in the background and retained for nearby cameras.

Set `SPACETANGENT_DUST_GRID` or place a compatible local grid at `data/sky/dust-vergely2022.bin` to use the scientific extinction density beyond the nearby cloud model. The tested archive uses an 80 pc resampling of the published grid. [Vergely, Lallement and Cox (2022)](https://doi.org/10.1051/0004-6361/202243319) · [CDS catalogue](https://cdsarc.cds.unistra.fr/viz-bin/cat/J/A%2BA/664/A174)

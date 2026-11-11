# Spatial stars and deep sky

The optional `data/sky/spatial-stars.tsv` sidecar provides positions for stars with catalogued distances. In a source checkout use `src/SpaceTangent.Godot/.local-data/sky/spatial-stars.tsv` or set `SPACETANGENT_SPATIAL_STARS` to the file. The first line declares the columns:

```text
id  name  dist_pc  x_pc  y_pc  z_pc  vx_pc_y  vy_pc_y  vz_pc_y  v_mag  b_v  r_km
```

Use tabs as delimiters. Positions are heliocentric ICRS coordinates at J2000 in parsecs. Velocities are parsecs per Julian year, including tangential and measured radial terms where available. The visual radius is an estimate when the source lacks a direct measurement. Direction-only stars remain available through the bundled bright catalogue. The Earth observer retains its original stars, labels and figures.

The verified sidecar is adapted from [HYG 4.1](https://github.com/astronexus/HYG-Database/tree/c7f7f883fe678cc7680169a50ccd7dcc49b060ce). Credit David Nash / Astronexus and distribute the adapted data under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/).

Search can fly to Proxima Centauri and other stars with a spatial entry. At interstellar camera positions, the star field uses the camera's position rather than fixed Earth directions. The **Deep sky** filter and the **Deep-sky objects** layer expose M31, M42 and M45. Their distances and extents are approximate, and their renderings represent the three object classes schematically.

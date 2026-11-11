# Local lunar terrain

Place the original LRO LOLA `terrain-moon.bin` and NAIF `pck00011.tpc` together under `src/SpaceTangent.Godot/.local-data/terrain` for a source build or `data/terrain` beside the exported executable. `SPACETANGENT_TERRAIN_ROOT` can select a different local directory. The original 4096 × 2048 terrain pack has SHA-256 `05F006DB0116E97E022C3608C26C14FE4891F5EAD16C7922AF1D4A3053BE750C`.

Select **Lunar terrain** to see the globe, or **Copernicus** to see the crater and surrounding relief. The coordinates field accepts east longitude, north latitude and view width in kilometers, separated by commas. **Globe** restores the initial field. The views save their terrain coordinates and width in scenarios.

The globe uses a coarse surface grid. Fields up to 1600 km use a denser regional grid with elevations in physical units. The Sun direction comes from DE440 at the view's TDB epoch and rotates into the IAU Moon frame. The renderer shades the terrain slope. The existing **Full Moon** action still opens the Earth surface scene.

[NASA PDS LOLA archive and data units](https://pds-geosciences.wustl.edu/missions/lro/lola_faq.htm) · [NAIF text body-orientation kernel](https://naif.jpl.nasa.gov/pub/naif/generic_kernels/pck/pck00011.tpc)

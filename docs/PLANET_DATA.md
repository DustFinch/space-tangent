# Planetary maps

For the orbital Earth view, set `SPACETANGENT_PLANET_TEXTURES` to an absolute directory, use `src/SpaceTangent.Godot/.local-data/planets` in a source checkout, or put the maps in `data/planets` beside the exported application. The application uses `earth.jpg` for the surface and optionally `earth-night.jpg` for city lights. The cloud map is shared with the [surface observer](MOON_LIGHTING.md).

| File | Source | SHA-256 |
| --- | --- | --- |
| `earth.jpg` | [NASA Earth Observatory, Blue Marble Next Generation, July 2004, 5400 × 2700](https://science.nasa.gov/earth/earth-observatory/blue-marble-next-generation/base-map/) | `f55226d46d27e05511f2118dc6aa24f5dbf9b6b2cddc87cc6e0e7dd067c00b11` |
| `earth-night.jpg` | [NASA Earth Observatory, Black Marble 2016 grayscale, 3600 × 1800](https://science.nasa.gov/earth/earth-observatory/earth-at-night/maps/) | `4d2158f59123dadf0696a1cf8909c45018a1de8d0daab40da04122a5aa7f27c6` |

Blue Marble imagery credit goes to NASA Earth Observatory, Reto Stöckli and Robert Simmon. Black Marble uses NASA and NOAA Suomi NPP VIIRS observations. The maps are optional local assets.

The orbital renderer and the surface observer share the single-scattering profiles. The texture orientation uses the Earth observer's selected ΔT model and is available inside its 1900–2099 range. At more distant epochs, the orbital renderer uses the generic Earth material. The global maps show broad features from orbit. Regional detail requires a separate surface dataset.

# Planetary maps

For the orbital Earth view, set `SPACETANGENT_PLANET_TEXTURES` to an absolute directory, use `src/SpaceTangent.Godot/.local-data/planets` in a source checkout, or put the maps in `data/planets` beside the exported application. The application uses `earth.jpg` for the surface and optionally `earth-night.jpg` for city lights. The cloud map is shared with the [surface observer](MOON_LIGHTING.md).

| File | Source | SHA-256 |
| --- | --- | --- |
| `earth.jpg` | [NASA Earth Observatory, Blue Marble Next Generation, July 2004, 5400 × 2700](https://science.nasa.gov/earth/earth-observatory/blue-marble-next-generation/base-map/) | `f55226d46d27e05511f2118dc6aa24f5dbf9b6b2cddc87cc6e0e7dd067c00b11` |
| `earth-night.jpg` | [NASA Earth Observatory, Black Marble 2016 grayscale, 3600 × 1800](https://science.nasa.gov/earth/earth-observatory/earth-at-night/maps/) | `4d2158f59123dadf0696a1cf8909c45018a1de8d0daab40da04122a5aa7f27c6` |

Blue Marble imagery credit goes to NASA Earth Observatory, Reto Stöckli and Robert Simmon. Black Marble uses NASA and NOAA Suomi NPP VIIRS observations. The maps are optional local assets.

The orbital renderer and the surface observer share the single-scattering profiles. The texture orientation uses the Earth observer's selected ΔT model and is available inside its 1900–2099 range. At more distant epochs, the orbital renderer uses the generic Earth material. The global maps show broad features from orbit. Regional detail requires a separate surface dataset.

## Other planets

Place equirectangular images in the same directory with the filenames `mercury.jpg`, `venus-clouds.jpg`, `mars.jpg`, `jupiter.jpg`, `saturn.jpg`, `uranus.jpg` and `neptune.jpg`. Place the Saturn ring profile in `saturn-rings.png` as a horizontal strip with alpha, sampled from the inner edge at the left to the outer edge at the right. The renderer also supports solid-color planets when the optional image files are absent.

For body-fixed orientation, use the [NAIF text PCK](https://naif.jpl.nasa.gov/pub/naif/generic_kernels/pck/pck00011.tpc) as `pck00011.tpc`. The existing timeline pack's PCK can also supply these frames. Without a PCK, the globes use an inertial orientation and the ring plane is unavailable.

| System | Center correction | TDB coverage of the locally verified input |
| --- | --- | --- |
| Mars | `center-4.bsp` · MAR099s | 1995-01-01 through 2050-01-01 |
| Jupiter | `center-5.bsp` · JUP365 | 1990-01-01 through 2050-01-01 |
| Saturn | `center-6.bsp` · SAT480 | 1950-01-01 through 2050-01-01 |
| Uranus | `center-7.bsp` · URA111 | 1990-01-01 through 2050-01-01 |
| Neptune | `center-8.bsp` · NEP097 | 1990-01-01 through 2050-01-01 |

The [NAIF satellite kernel archive](https://naif.jpl.nasa.gov/pub/naif/generic_kernels/spk/satellites/) supplies these offsets in J2000 axes, with each planet relative to its own system barycenter. Outside the supplied kernel coverage the planet camera falls back to its DE440 system barycenter. The overview always draws the heliocentric DE440 system paths. Mercury, Venus and Earth already have physical center states in DE440. The default inner and outer views cover different groups. Use **Layers → Planet orbits** to toggle the guide paths.

The local Olympus Mons preset reads `terrain-mars.bin`, an elevation grid in the same signed-height format as the lunar terrain pack. The verified grid comes from [Mars Global Surveyor MOLA](https://pds-geosciences.wustl.edu/missions/mgs/mola.html), is sampled at 4096 × 2048, and has a 3396 km spherical reference radius.

The planetary textures are local sidecar assets. Record the source, author credits and licenses for each map before redistribution.

## Moons and small bodies

The Moon is taken from DE440. The Mars and Jupiter kernel files can also supply Phobos, Deimos, Io, Europa, Ganymede and Callisto. These states are relative to the system barycenter. The graph derives parent-relative motion by subtracting the physical planet center. The optional `moons-6.bsp` uses SAT441 records for Titan. The optional `center-9.bsp` uses PLU058 records for Pluto with the DE440 Pluto system barycenter as its fallback. In the verified pack, these two files cover 1990–2050 TDB.

The optional `ceres.xyzv`, `vesta.xyzv` and `67p.xyzv` files contain geometric heliocentric ICRS vectors from [JPL Horizons](https://ssd.jpl.nasa.gov/horizons/). Use Julian date TDB, kilometers, and kilometers per second. The verified input covers 2010–2050 at three-day intervals. Outside that span the application reports that the target has no usable state. The comet's visible tail is an anti-solar illustration with a nominal scale. Its length and brightness are rendering choices.

Optional maps have the same names as their objects in lowercase, such as `titan.jpg` and `pluto.jpg`. The verified Phobos, Deimos and Vesta renderers use ellipsoid approximations.

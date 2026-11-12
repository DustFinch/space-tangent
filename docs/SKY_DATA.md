# Bright sky data

Open **Layers** to turn on bright stars, star labels, the equatorial grid and constellation figures for the active view. Search by a star's name or Hipparcos ID, or click its mark in the sky. **Point at** uses the selected Earth's observer site and epoch. Stars below its local horizon are hidden. Brightness in the Earth view fades in daylight. The chart uses geometric ICRS directions and linear proper motion, with no stellar aberration, refraction or cloud attenuation.

Version 4 scenarios store layer settings per view. Earlier scenarios open with the layers disabled.

## Catalogue and attribution

- `SkyData/bright-stars.tsv` is an adaptation of [HYG v4.1 by David Nash and Astronexus](https://github.com/astronexus/HYG-Database/tree/c7f7f883fe678cc7680169a50ccd7dcc49b060ce/hyg/CURRENT), under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). It keeps 5,041 entries with Hipparcos IDs and visual magnitude at most 6.0. The subset keeps J2000 ICRS right ascension and declination in radians, tangential proper motions in radians per Julian year, name, and visual magnitude.
- `SkyData/constellation-lines.json` comes from [Olaf Frohn's d3-celestial](https://github.com/ofrohn/d3-celestial/blob/7e720a3de062059d4c5400a379146a601d9010e0/data/constellations.lines.json), under BSD 3-Clause. These illustrative figures are drawn on the J2000 sphere. The original notices for both datasets are included alongside the data and in the resource pack.
- The regression compares Sirius, Betelgeuse, Vega and Polaris with the [original Hipparcos catalogue distributed by NASA HEASARC](https://heasarc.gsfc.nasa.gov/w3browse/star-catalog/hipparcos.html). The angular tolerance is one arcsecond at J1991.25 and J2050, allowing for catalogue rounding and first-order proper-motion propagation.

## Regenerate the subset

Download `hyg/CURRENT/hygdata_v41.csv` and `LICENSE` from the pinned HYG revision as `hyg-v41.csv` and `HYG-LICENSE.txt`. Download `data/constellations.lines.json` and `LICENSE` from the pinned figure revision as `constellation-lines.json` and `LINES-LICENSE.txt`. Place these in a local input directory.

```powershell
python scripts/sky/prepare.py C:/data/sky-input src/SpaceTangent.Godot/SkyData
```

The script checks both original data hashes before writing. SHA-256 of the derived catalogue is `4d1e54f6467594a6003e4971437266674bd4aa04039fbbee37b74a2ae78bbeb0`.

## Local deep-sky catalogues

Place `.dsc` files in `data/sky/dsc` or set `SPACETANGENT_DSC_ROOT` to their directory. Reload add-ons after editing the files. The existing **Deep sky** search filter includes galaxies, nebulae, globular clusters, and open clusters. Search by a name or catalogue alias, then use **Go to** or select the object in a perspective view. Use the **Deep-sky objects** layer to show untargeted objects.

The verified original files contain 10,937 galaxies, 196 globular clusters, and 6,476 open clusters. Messier IDs stay stable when a source updates an object that has a built-in entry. This renderer uses illustrative profiles and the catalogue's positions and extents.

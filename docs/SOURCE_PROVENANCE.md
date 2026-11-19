# Source and verification inventory

This inventory distinguishes numerical implementations, compatibility syntax, verification inputs and optional assets. It records identified origins without assigning authorship merely because another program uses the same format.

| Area | Origin and retained evidence |
| --- | --- |
| Earth orientation | The pinned core identifies the ERFA routines and NumFOCUS copyright next to `EarthOrientation`; its full BSD notice is in `LICENSES/ERFA.txt` and is copied into distributions. |
| Orbit and satellite calculations | The core identifies Vallado's SGP4 reference and the upstream sources of analytical coefficients. See its [ephemeris and source notes](https://github.com/DustFinch/space-tangent-core/blob/main/docs/DE440.md). Preserve these notices when adapting formulas or coefficients. |
| Independent reference data | Generators, pinned tools and native-oracle provenance are described in the core's [reference inventory](https://github.com/DustFinch/space-tangent-core/blob/main/docs/REFERENCE_PROVENANCE.md). Numeric fixture generation does not call the production C# implementation. |
| SSC, mission catalogues, scripts and links | Compatibility adapters retain required external tags at parsing boundaries. Their supported behavior and source references are documented in the format guides. Format compatibility alone is not evidence that an upstream source file was copied. The C# files retain the project's SPDX license headers. |
| Recorded frames | Archived frames are external verification inputs and an explicitly supported replay/import format. Do not remove replay support when separating verification infrastructure from the release. Private frame captures are not required by the public startup smoke. |
| Fonts and bundled sky data | Keep the bundled OFL and sky-data license/attribution files with those assets. See the files alongside `UI/Fonts` and `SkyData`. |
| Optional maps, spacecraft models and kernels | Original add-ons and scientific datasets retain their own distribution terms. The application package and source export do not automatically grant redistribution rights to installed data; consult the linked dataset guides. |

The review checked these attribution locations and the current format boundaries. It did not add speculative copyright claims to independent readers or replace upstream notices with a blanket project attribution. Any future direct source port must retain its actual upstream copyright and license notice at the implementation and distribution boundary.

# Local satellite groups

Place archived or updated `.tle` and `.txt` catalogues in `data/satellites` or set `SPACETANGENT_TLE_ROOT` to their directory. Use **Reload add-ons** after updating a file. Search for a spacecraft by its name, NORAD identifier or group filename. Use **Go to** and **Track** during the propagation window. A selected satellite without an attached model has a marker.

A catalogue can use pairs of element lines or add a name before each pair. Repeated catalogue numbers keep the newest valid epoch and group aliases. Where a modeled scientific spacecraft has an additional local element set, the newer covered state takes precedence and the original modeled state remains available in its own coverage.

The regression uses all four archived groups. It checks 243 distinct catalogue numbers, a flight to GPS BIIR-5 and rollback of a corrupt replacement with the original file size and timestamp. Outside the propagation window the local marker is hidden.

Use **Add-ons > Update satellite groups** to fetch stations, visual, science and GPS Operational from CelesTrak. SpaceTangent stores validated replies in its user profile and merges them with the installed groups. Each group is requested at most once every two hours. If a reply fails, that group's cached elements stay in use. The status tooltip lists any failures.

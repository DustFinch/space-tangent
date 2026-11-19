# Earth frames and rendering

The simulation evaluates Earth-dependent graph nodes with the session's selected Delta T model. A snapshot
retains that model, including nested frame evaluations. View and script caches distinguish models at the same
epoch; replacing add-ons does not reset the model. The pole-of-date overlay uses the core precession model.

Earth maps use the full terrestrial-to-world basis. A scalar meridian angle cannot represent the historical
pole. Ground observer positions and the astronomical sky use geodetic latitude and the reference ellipsoid.

The ground atmosphere remains a local spherical approximation. Its virtual eye is on the geodetic vertical
at the actual height above a tangent reference sphere. The physical ellipsoidal eye is retained for sky
directions. Cloud coordinates use the Earth basis, and eclipse receivers include the translation back to
physical coordinates. This avoids putting an ellipsoidal observer below a sphere of equatorial radius.
The approximation is intended for a local ground view, not precise global atmospheric ray tracing.

`res://tests/EarthAccuracyProbe.tscn` requires a real GPU renderer, `SPACETANGENT_DE440`,
`SPACETANGENT_SURFACE_TEXTURES` and an output directory in `SPACETANGENT_ACCURACY_OUTPUT`.
It compares atmosphere/cloud controls at the equator, Moscow latitude and 75 degrees, and checks a latitude
marker under an Earth-fixed camera at modern and historical epochs. It writes PNGs and measured pixel
differences. The test contains no system input and can run on an unattended or isolated Windows desktop.

`BootstrapSmoke` and `WorkspaceInputSmoke` cover actual scene loading, two views, navigation and reload.
The latter sends events to its own Godot viewport rather than moving the system cursor.

Keep the core's `licenses/ERFA.txt` with published assemblies and Windows exports. The mathematical
precession model does not establish uniform ephemeris or Delta T accuracy on arbitrary distant dates.

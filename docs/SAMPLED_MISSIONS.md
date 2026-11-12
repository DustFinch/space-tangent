# Sampled mission catalogues

Place local version 1.0 JSON catalogues in `data/sampled-missions` or set `SPACETANGENT_SAMPLED_MISSIONS_ROOT` to the directory. Keep each trajectory and mesh at the relative path declared in its JSON. Use **Reload add-ons** after changing a file. The current spacecraft search includes the imported missions, and **Go to** and **Track** work during their available arcs.

The reader supports XYZ and XYZV curves, fixed points, changing centers, Ecliptic J2000 and equatorial inertial frames, body-fixed frames and two-vector frames built from relative position and velocity. Arc end times are exclusive. A mission remains available in covered arcs when data for a different arc are unavailable. MSL's landed arc requires a Mars orientation source, and Dawn's Vesta arc requires the local Vesta ephemeris.

A missing optional mesh leaves a marker available and is reported in the log. An unreadable required trajectory rejects the replacement generation and preserves the last loaded world and cameras. The original MSL and Dawn trajectories have been checked against the archived simulator. The tested archive lacks their spacecraft meshes. Dawn's separate particle system is reported as unsupported by this reader.

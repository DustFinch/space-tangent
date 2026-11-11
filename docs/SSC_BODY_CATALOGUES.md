# Local SSC body catalogues

Choose **Add-ons → SSC body catalogue** and select the folder containing the catalogues and their resources. The optional default source is `data/ssc-bodies` beside the executable. Keep the model and trajectory files under the selected directory.

The importer reads top-level and `data` SSC files in filename order. It supports Add, Modify and Replace within a source, aliases, elliptic and cubic XYZV trajectories, and equatorial or ecliptic J2000 orbit axes. Supported attitudes are fixed and two-vector. A model can use binary CMOD with diffuse PNG textures or the existing 3DS subset. Unsupported orbital or attitude models report a source error.

The selected source is applied as a unit. A bad model, missing texture or unresolved reference leaves the previous world and camera in place. The original station model and a sampled asteroid arc were verified with the regular add-on controls.

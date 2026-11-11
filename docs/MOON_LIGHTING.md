# Full-Moon observation

Place the local `earth-clouds.png` and `moon.jpg` textures in `src/SpaceTangent.Godot/.local-data/surface` or set `SPACETANGENT_SURFACE_TEXTURES` to their directory. The previous project's preserved test assets have SHA-256 values `bf7eb4efd942224e7cc028388b96ae250eb8f29a679c0e4103123b0ea8136dff` and `6f684f9125ed90f9f10fc0c4d769b3cc75d7fd68c639e9094a24cee1a0e02374` respectively. They remain external to the repository.

**Full Moon** opens the Moscow surface view at JD 2460600.5008 TDB. It uses the previous regression's heading of 237°, pitch of 28°, 10° vertical field and 4× apparent lunar size. The shader samples a spherical cloud shell, applies sunlight during the day and adds reflected lunar light on the night side when the Moon is above the local horizon. Lunar flux follows phase, distance and a bounded Earth-shadow estimate. The cloud exposure is a display setting. The surface view uses a mean sidereal Earth approximation and a simple sky background.

To reproduce the GPU check, set `SPACETANGENT_MOON_OUTPUT` to an absolute output directory and launch `res://tests/MoonLightingProbe.tscn` with rendering enabled. The probe captures six images. It measures the cloud brightness change at night and checks that the lunar cloud toggle has no effect on daylight or on a scene with clouds disabled.

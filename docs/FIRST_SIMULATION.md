# Running the first simulation

Get the official classic DE440 file listed in the [core data notes](../deps/space-tangent-core/docs/DE440.md). Set `SPACETANGENT_DE440` to its absolute path or place the file at `src/SpaceTangent.Godot/.local-data/de440.440` for a local checkout. A packaged app also looks for `de440.440` in its user-data directory. The pinned 1900–2100 subset works with the same filename.

Build and run the Godot scene using the commands in the README. The clock starts at J2000, paused. Use the day-step buttons or Play to advance simulated time. The solar overview shows Earth relative to the Sun. The Earth–Moon view shows the Moon relative to Earth. Marker sizes are symbolic and each view uses a linear distance scale.

The headless smoke scene tests the data-backed simulation when `SPACETANGENT_DE440` is set. It verifies the initial epoch, the movement of Earth after a one-day step, and the Earth-centred Moon view. It prints `SpaceTangent DE440 scene smoke passed` on success.

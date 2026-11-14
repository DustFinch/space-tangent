# Running the first simulation

Get the official classic DE440 file listed in the [core data notes](../deps/space-tangent-core/docs/DE440.md). Set `SPACETANGENT_DE440` to its absolute path or place the file at `src/SpaceTangent.Godot/.local-data/de440.440` for a local checkout. A packaged app also looks for `de440.440` in its user-data directory. The pinned 1900–2100 subset works with the same filename.

Build and run the Godot scene using the commands in the README. On a fresh start the clock is paused at J2000, with a perspective camera near Earth. Search for a body and click **Go to** to fly to it. Use the hour-step buttons or **Play** to advance simulated time. **Inner planets** shows the inner orbits around the Sun. **Earth–Moon view** shows the Moon relative to Earth. These overview modes use symbolic markers and a linear distance scale.

The headless smoke scene tests the data-backed simulation when `SPACETANGENT_DE440` is set. It verifies the initial epoch, the movement of Earth after a one-day step, and the Earth-centred Moon view. It prints `SpaceTangent DE440 scene smoke passed` on success.

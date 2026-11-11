# Windows build

Install the Godot .NET export templates matching the project version. Restore the Windows export dependencies in locked mode and export the Windows Desktop preset from the application checkout.

```powershell
dotnet restore src/SpaceTangent.Godot/SpaceTangent.Godot.csproj -p:Configuration=ExportRelease -r win-x64 --locked-mode
godot --headless --path src/SpaceTangent.Godot --editor --import --quit
godot --headless --path src/SpaceTangent.Godot --export-release "Windows Desktop" "C:/build/SpaceTangent/SpaceTangent.exe"
```

Keep the exported executable, resource pack and managed assembly directory together. Place local datasets next to the executable in `data` using the layout below. Environment-variable overrides from the simulation and add-on notes also work.

- `data/de440.440` contains the verified DE440 input.
- `data/science` uses the layout in [the science add-on notes](SCIENCE_ADDONS.md).
- `data/surface` contains the two files in [the full-Moon observation notes](MOON_LIGHTING.md).
- `data/mission` optionally contains the Cassini mission catalogue, original model and its local kernels. See [the mission setup](CASSINI_MISSION.md).

The source export excludes test scenes and local data. Distribute external data according to its own terms.

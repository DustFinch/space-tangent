# Windows build

CI compiles the Godot project on Windows and Linux and runs a headless simulator startup with pinned public DE440. Run `pwsh ./scripts/ci/godot-smoke.ps1` for the same download, hash verification, import and startup procedure. This verifies a real simulation session; GPU and original-add-on acceptance are separate checks. Keep generated `.uid` files for scripts and shaders under version control, and verify that a clean editor import leaves the checkout unchanged.

Install the Godot .NET export templates matching the project version. Restore the Windows export dependencies in locked mode and export the Windows Desktop preset from the application checkout.

The lock files pin the public nuget.org packages. Godot restores into its ignored project-local `.godot/mono/nuget` cache by default: packages bundled with the editor can contain the same assemblies but have different archive hashes in the shared user cache. An explicit `NUGET_PACKAGES` or `RestorePackagesPath` overrides this default; use a cache populated from nuget.org for locked restore.

```powershell
dotnet restore src/SpaceTangent.Godot/SpaceTangent.Godot.csproj -p:Configuration=ExportRelease -r win-x64 --locked-mode
godot --headless --path src/SpaceTangent.Godot --editor --import --quit
godot --headless --path src/SpaceTangent.Godot --export-release "Windows Desktop" "C:/build/SpaceTangent/SpaceTangent.exe" --quit
```

Keep the exported executable, resource pack and managed assembly directory together. Place local datasets next to the executable in `data` using the layout below. Environment-variable overrides from the simulation and add-on notes also work.

For unattended Windows exports, invoke the main Godot executable, wait for its exit code and retain both output streams. A completed resource-pack log alone does not establish a successful exporter exit.

- `data/de440.440` contains the verified DE440 input.
- `data/science` uses the layout in [the science add-on notes](SCIENCE_ADDONS.md).
- `data/surface` contains the two files in [the full-Moon observation notes](MOON_LIGHTING.md).
- `data/mission` optionally contains the Cassini mission catalogue, original model and its local kernels. See [the mission setup](CASSINI_MISSION.md).
- `data/timeline` optionally contains the sampled Cassini and Huygens world data. Its SSC layout and optional kernels are in [the mission setup](CASSINI_MISSION.md).
- `data/terrain` optionally contains the LOLA elevation map and its Moon orientation kernel. See [the lunar terrain setup](LUNAR_TERRAIN.md).

The source export excludes test scenes and local data. Distribute external data according to its own terms.

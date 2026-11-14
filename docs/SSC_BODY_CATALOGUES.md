# Local SSC body catalogues

Choose **Add-ons → SSC body catalogue** and select the folder containing the catalogues and their resources. The optional default source is `data/ssc-bodies` beside the executable. Keep the model and trajectory files under the selected directory.

The importer reads top-level and `data` SSC files in filename order. It supports Add, Modify and Replace within a source, aliases, elliptic and cubic XYZV trajectories, and equatorial or ecliptic J2000 orbit axes. Supported attitudes include fixed, uniform, synchronous, sampled and two-vector. The archived minor moon catalogue uses constant two-vector orbital frames and local PCK for the known Amalthea and Thebe spin models. SemiAxes defines physical mesh dimensions and a local Texture reference supplies the surface map. Models can use binary or text CMOD, displaced-sphere CMS, OBJ, the supported 3DS subset, or packed GLB. Text CMOD uses the same triangle and local texture validation as binary CMOD. Unsupported orbital or attitude models report a source error.

Legacy `SampledOrbit` accepts cubic XYZ and XYZV files in body definitions and `Timeline` phases. It uses single-precision sample values and holds the nearest position outside coverage. If the file cannot be initialized, the importer reports the failure and tries an explicitly declared elliptical orbit or fixed position. When both sampled forms are present, `SampledTrajectory` takes precedence over `SampledOrbit`.

The selected source is applied as a unit. A bad model, missing texture or unresolved reference leaves the previous world and camera in place. The original station model and a sampled asteroid arc were verified with the regular add-on controls.

## Build a pack

Run the commands from the simulator checkout with its core submodule initialized.

```sh
dotnet run --project deps/space-tangent-core/tools/SpaceTangent.PackTool -- import "/path/to/source" --pack "/path/to/new-pack"
dotnet run --project deps/space-tangent-core/tools/SpaceTangent.PackTool -- check "/path/to/new-pack/pack.json" --strict
```

Select the output folder as an SSC body catalogue. Move the entire folder to relocate the pack. The command preserves the relative paths of the model, texture and trajectory files.

## Export a static GLB model

```sh
dotnet run --project tools/SpaceTangent.ModelTool -- model "/path/to/addon/models/body.cmod" "/path/to/body.glb" --root "/path/to/addon"
```

The tool accepts OBJ, CMOD, CMS, 3DS, glTF and GLB. It embeds the images resolved by the input decoder. DDS images require support for `MSFT_texture_dds` in the receiving viewer. CMOD point sprites are exported as glTF points with the legacy `_POINTSIZE` attribute. Local DSC nebulae can use these sprites from CMOD, glTF or GLB. The tool reports CMOD lines as unsupported. Append `--native-body` after the optional `--root` argument for native planetary meshes such as Phobos. This uses the native-body UV convention and preserves the CMOD normal map.

To check a legacy installation that selects built-in orbit or attitude models, supply the local DE440, VSOP87 and planetary PCK data. List the pack directories in installation order.

```sh
dotnet run --project deps/space-tangent-core/tools/SpaceTangent.PackTool -- check-ssc "/path/de440.440" "/path/vsop87.bin" "/path/pck00011.tpc" --strict "/path/00-ssc" "/path/99-override"
```

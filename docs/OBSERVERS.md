# Observer views and reloading

Use **Split view** to open two views. With **Synchronize view time** cleared, the hour-step and playback controls affect only their view. With synchronization enabled, both views use the epoch of the most recently used view. Each view retains its own camera mode and satellite selection.

**Reload add-ons** decodes source geometry on a worker and publishes meshes and the replacement world on the main thread. The current epochs, camera modes, available selections and ΔT preference carry forward. A failed reload leaves the previous generation visible. The ΔT option is saved in the Godot user profile. The Earth surface view uses the selected model for sidereal rotation.

The Godot headless smoke test exercises the workflow with the original Jason-1 and SORCE files. It checks independent and synchronized clocks, a successful reload, a failed reload and the saved ΔT value.

Each new load cancels and supersedes the previous load. The headless test verifies that only the latest request publishes and that the independent clock advances while its models are decoding. PNG decoding and GPU resource creation occur during main-thread publication.

Use **Scenario → Save scenario** to save the current one- or two-view scene to a `.stscene` file. **Open scenario** restores exact TDB epochs, playback, time synchronization, camera modes, selections, the active view and the scene's ΔT model. Choose a model directly in the toolbar to change the default saved in your profile.

Scenario files use versioned JSON and refer to loaded objects by their catalog IDs. Keep the required add-ons available when opening a scenario. An invalid file or missing object leaves the current views intact.

## Object navigation

Open **Search** or press **Enter**. Search by name or catalogue ID, then select an object. The card shows its type and distance from Earth at the active view's epoch. **Go to** flies to the object, and **Esc** or **Stop flight** stops at the current camera position. Drag with the right mouse button to orbit the camera pivot, and use the wheel to zoom. **Track** centers the selected object and follows its position as the view's clock advances.

Selecting an object leaves the epoch unchanged. If an Earth satellite has no state at that epoch, starting a flight or tracking may move the clock to the satellite's catalogue epoch. An independent second view keeps its own epoch and camera.

Saving a scenario writes both camera poses and tracking settings. Open the scenario again after restarting to restore those views. Version 1 scenarios open with their original preset cameras.

## Headless workspace input test

After building the Godot project, set `SPACETANGENT_DE440`, `SPACETANGENT_SCIENCE_ROOT` and `SPACETANGENT_SURFACE_TEXTURES` to the local DE440 file, original science add-ons and Earth surface textures. From the repository root, run:

```powershell
godot --headless --path src/SpaceTangent.Godot res://tests/WorkspaceInputSmoke.tscn
```

The test sends mouse and keyboard events to the Godot viewport. It searches for Jason-1, flies to it and tracks it while the second view shows the full Moon. It verifies independent and synchronized clocks, both cameras and ΔT after reloading add-ons, and a scenario save and reopen through the file dialogs. The test uses a temporary directory for the scenario and ΔT settings.

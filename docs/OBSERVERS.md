# Observer views and reloading

Use **Split view** to open two views. With **Synchronize view time** cleared, the hour-step and playback controls affect only their view. With synchronization enabled, both views use the epoch of the most recently used view. Each view retains its own camera mode and satellite selection.

**Reload add-ons** decodes source geometry on a worker and publishes meshes and the replacement world on the main thread. The current epochs, camera modes, available selections and ΔT preference carry forward. A failed reload leaves the previous generation visible. The ΔT option is saved in the Godot user profile. The Earth surface view uses the selected model for sidereal rotation.

The Godot headless smoke test exercises the workflow with the original Jason-1 and SORCE files. It checks independent and synchronized clocks, a successful reload, a failed reload and the saved ΔT value.

Each new load cancels and supersedes the previous load. The headless test verifies that only the latest request publishes and that the independent clock advances while its models are decoding. PNG decoding and GPU resource creation occur during main-thread publication.

Use **Scenario → Save scenario** to save the current one- or two-view scene to a `.stscene` file. **Open scenario** restores exact TDB epochs, playback, time synchronization, camera modes, selections, the active view and the scene's ΔT model. Choose a model directly in the toolbar to change the default saved in your profile.

Scenario files use versioned JSON and refer to loaded objects by their catalog IDs. Keep the required add-ons available when opening a scenario. An invalid file or missing object leaves the current views intact.

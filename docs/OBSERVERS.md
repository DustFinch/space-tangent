# Observer views and reloading

Use **Split view** to open two views. With **Synchronize view time** cleared, the hour-step and playback controls affect only their view. With synchronization enabled, both views use the epoch of the most recently used view. Each view retains its own camera mode and satellite selection.

**Reload add-ons** prepares the replacement world and meshes before replacing the displayed generation. The current epochs, camera modes, available selections and ΔT preference carry forward. A failed reload leaves the previous generation visible. The ΔT option is saved in the Godot user profile. The Earth rotation renderer will consume this option when it is added.

The Godot headless smoke test exercises the workflow with the original Jason-1 and SORCE files. It checks independent and synchronized clocks, a successful reload, a failed reload and the saved ΔT value.

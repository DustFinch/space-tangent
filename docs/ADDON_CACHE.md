# Add-on reload

Reloading validates four independent sources: the science catalogue, the SPICE mission, the sampled timeline and the lunar terrain pack. Each source records the contents it read, the resource paths it checked, and the file listings that selected its SSC overrides. A previously missing resource can change the selected input on the next reload.

File sizes are a quick rejection check. Matching sizes still require matching SHA-256 content, even when modification times agree. Unrelated files do not invalidate a source. The cache is in memory and lasts for the current simulator session.

The worker validates inputs and builds the new simulation world. Godot prepares changed rendering resources on the main thread and keeps unchanged resources. A failed load leaves the previous generation available. If reloads overlap, only the latest request can publish.

The status area reports the source, import stage and failing filename, with the line when the parser supplies one. The Godot log also includes a stable issue code and the full resource path. Embedding clients can call `AddonDiagnostics.Get(exception)` to obtain the same diagnostic fields while preserving their existing exception handling.

## Local sources

Open **Packs and add-ons** to choose a supported source. Select the science, sampled timeline or lunar terrain folder, or open the mission SSC or JSON catalogue with its files beside it. The panel shows the path and load status. The first local science source replaces the default science source. Enable additional science sources when they supply different catalogue IDs. The mission, timeline and terrain each use one active source.

Sources with the same display name keep separate switches and paths. Successful changes are stored in the user's add-on settings and are reapplied at startup. Paths inside the Windows package's `data` folder are relative to that folder, so the package and data can move together. Other paths are absolute.

**Reload add-ons** checks the current files again. If validation or import fails, the last successful world and source settings stay active. The status shows the error and the path to the affected file. Restore the file or disable its source, then reload again.

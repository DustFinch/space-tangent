# Add-on reload

Reloading validates four independent sources: the science catalogue, the SPICE mission, the sampled timeline and the lunar terrain pack. Each source records the contents it read, the resource paths it checked, and the file listings that selected its SSC overrides. A previously missing resource can change the selected input on the next reload.

File sizes are a quick rejection check. Matching sizes still require matching SHA-256 content, even when modification times agree. Unrelated files do not invalidate a source. The cache is in memory and lasts for the current simulator session.

The worker validates inputs and builds the new simulation world. Godot prepares changed rendering resources on the main thread and keeps unchanged resources. A failed load leaves the previous generation available. If reloads overlap, only the latest request can publish.

The status area reports the source, import stage and failing filename, with the line when the parser supplies one. The Godot log also includes a stable issue code and the full resource path. Embedding clients can call `AddonDiagnostics.Get(exception)` to obtain the same diagnostic fields while preserving their existing exception handling.

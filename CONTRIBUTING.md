# Contributing

Clone recursively, or run `git submodule update --init --recursive` after cloning. Use the .NET SDK selected by `global.json`, the .NET 8 runtime for tests, and Godot 4.7.2 .NET for the simulator. See [Windows build](docs/WINDOWS_BUILD.md) for data and export setup.

```sh
dotnet restore tests/SpaceTangent.Application.Tests --locked-mode
dotnet test tests/SpaceTangent.Application.Tests -c Release --no-restore
dotnet restore src/SpaceTangent.Godot/SpaceTangent.Godot.csproj --locked-mode
dotnet build src/SpaceTangent.Godot/SpaceTangent.Godot.csproj --no-restore
```

The CI workflows run tests, Godot import and startup on Windows and Linux with a pinned public DE440 file. Optional dataset tests name the environment variables they need and report skips when unset. A skipped case is not a successful check. Small synthetic fixtures and independent public references are preferred for new regressions; keep checks against real add-ons as an additional acceptance layer. Do not publish private recordings, designs, or optional assets without checking their source inventory and distribution terms.

Use focused commits with a clear subject and a short explanation of the behavior and validation. Include the applicable contributor and co-author trailers. Avoid unrelated history rewrites and generated build output. Godot `.uid` files belong in Git; a clean import should leave the checkout unchanged.

Git does not copy local hook configuration when cloning. The repository's audit hooks are an optional local setup for contributors and require a nonempty policy file outside the checkout. Maintainers should supply that setup; do not copy its private contents into a commit. If enabling the hooks, use `git config core.hooksPath .githooks` and set `SPACE_TANGENT_STOPWORDS` to the absolute external policy path. A missing policy produces an explicit error; do not silently bypass an enabled audit. Public build and test commands above do not depend on that file.

For performance changes, keep before/after inputs and hardware fixed, report allocations and frame stalls separately from averages, and preserve mathematical and image regressions. [Profiling instructions](docs/PERFORMANCE.md)

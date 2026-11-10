# SpaceTangent

Godot .NET desktop application. The Git submodule in `deps/space-tangent-core` pins the simulation libraries to a specific commit. A recursive clone initializes it when both repositories are hosted under the same owner.

For a local clone, keep the source repositories as siblings and allow file transport for the submodule initialization command:

```sh
git -c protocol.file.allow=always submodule update --init
```

Use Godot 4.7.2 .NET and the .NET SDK version set in `global.json`.

```sh
dotnet restore src/SpaceTangent.Godot/SpaceTangent.Godot.csproj --locked-mode
dotnet build src/SpaceTangent.Godot/SpaceTangent.Godot.csproj --no-restore
godot --headless --path src/SpaceTangent.Godot --editor --import --quit
godot --headless --path src/SpaceTangent.Godot res://tests/BootstrapSmoke.tscn
```

Open `src/SpaceTangent.Godot/project.godot` in the editor for interactive development. The starter uses the Compatibility renderer and its own user-data directory.

See [the migration plan](docs/MIGRATION_PLAN.md) for feature order and verification.

License: GPL-2.0-or-later. See `COPYING`.

## Local Git checks

Install Python 3 and run `git config --local core.hooksPath .githooks` in each clone. Set the intended public Git author name and GitHub noreply email in the clone.

Place the shared UTF-8 `.stopwords` file next to both repositories, or set `SPACE_TANGENT_STOPWORDS` to its absolute path outside the checkout. It accepts one phrase per line, comments starting with `#`, and optional `[en]` and `[ru]` section headers. All hooks require the file.

The checks cover staged paths and supported text, commit identities and messages, and objects introduced by a push, including annotated tags. For a required literal in text, add a reviewed exception to `scripts/audit/allowlist.json` with `path` (repository-relative), `sha256` (SHA-256 of the exact UTF-8 line without its newline) and `reason`. Changing the line invalidates the exception. Paths, commit metadata and the word list itself are outside the exception mechanism.

Text inspection supports UTF-8 and UTF-16 with a BOM. Binary data and dependency repository histories require their own checks. Each dependency repository has its own hooks and publishes its referenced commit before an application push.

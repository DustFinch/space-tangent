# Observer Lua scripts

Use **Script → Run Lua** to open a local `.lua` or `.celx` file for the active view. **Stop script** cancels it. The script's commands use the same session as the toolbar. A synchronized clock follows the normal shared-time rules. Opening a saved scenario stops the current script.

- `st.pause(true)` pauses and `st.pause(false)` resumes. Omit the argument to toggle playback.
- `st.settime(julianDate)` seeks to a TDB Julian date. `st.gettime()` returns the current date.
- `st.synchronizetime(enabled)` changes the clock mode. `st.mode(name)` selects `solar`, `lunar`, `satellites`, `surface` or `mission`. `st.select(id)` selects a loaded Earth satellite by catalogue ID.
- `wait(seconds)` and `st.wait(seconds)` resume on a later frame. `st.log(text)` writes to the Godot log.

The compatibility adapter supports the clock and logging methods used by the original [pause.celx](https://github.com/CelestiaProject/Celestia/blob/master/test/scripts/pause.celx). The test loads the unchanged local fixture with SHA-256 `20F0CCC2781C62D6C216D395049BBC64DCF701C41128079DB93C6909E0402C0B`. Broader script APIs, observer pose controls and add-on script factories remain pending.

The host limits input to 32 KiB, resumes up to 1,024 VM steps per frame and stops after 131,072 steps, 16 MiB of measured execution allocations, 128 log messages or six hours of requested waits. Callbacks that evaluate the world or search the catalogue count as 256 steps. Only the exposed commands and selected standard-library functions are available. Native `string.rep`, string-replacement `string.gsub` and `string.format` calls check conservative output bounds of 4 Mi UTF-16 characters before allocating; format widths and precisions have at most two digits, and all conversions count towards the combined result. Function/table substitution runs as interruptible Lua code under the execution allocation quota. Ordinary ASCII parsing patterns of at most 256 characters use an ordered NFA matcher that preserves Lua matching and capture priority without backtracking. An operation can execute at most 4,000,000 matching steps; all searches of one gmatch iterator or gsub call share that budget, including function/table replacements. Anchored searches start only at the requested position, and repeated calls reuse the last subject ASCII check through a weak reference. This covers line/word splitting, whitespace substitution, numeric fields, captures and disjoint adjacent repetitions. Ambiguous repetitions such as `a*a*b`, balanced/frontier patterns, backreferences, longer patterns and non-ASCII subjects retain the conservative native guard; these may be refused even when a particular input would match quickly. Native repetition also refuses subjects longer than 256 Ki characters. Each interpreter caches at most 64 translated patterns.

A plugin is disabled when its estimated reachable interpreter memory exceeds 64 MiB. The bounded heap walk runs after at least 16 MiB of measured execution allocations, or after two seconds of real monotonic time when there have been allocations. Completed ticks do not force a walk. Importer calls, including failed calls, still force a check. Paused or reversed simulation time does not affect either trigger. A quota violation skips the plugin's cleanup callback. This is cooperative protection on the application's main thread, with bounded per-invocation overshoot and a heap estimate, rather than a process memory cap or full VM isolation.

[MoonSharp coroutine behavior](https://www.moonsharp.org/coroutines.html) · [MoonSharp module selection](https://www.moonsharp.org/sandbox.html)

## Scripted catalogue models

An explicitly trusted local SSC add-on can use `ScriptedOrbit` and `ScriptedRotation` factories. Start with `-- --allow-addon-lua` or set `SPACETANGENT_ALLOW_ADDON_LUA=1` for that process. Each add-on gets its own Lua environment. Modules stay inside its source folder and may use dotted local names during initialization. Factories receive typed SSC arguments and the actual `AddonPath`.

Callbacks use separate instruction and allocation quotas and have no observer or operating-system API. An unaccepted factory uses an available declared fallback. An unavailable body and its descendants are omitted, and the status of the other add-ons is preserved.
An installed pack can contain embedded SSC with a resource table. The source catalogue keeps its scope inside the installed pack, including remapped GLB meshes and available fallback files.

## Folder plugins

Open a local `plugin.json` with **Script → Run Lua…**. An enabled plugin adds its manifest scripts and registered commands to the existing **Script** menu. Use the menu's **Disable** entry to remove the plugin. A plugin remains attached to the observer that opened it. Closing that observer or replacing the scenario stops its jobs.

The archived version 1 manifest can declare `main` and `scripts`. The interpreter accepts local Lua modules through `require` and files through `plugin.read`. Use `plugin.command`, `plugin.on`, `plugin.run` and `plugin.importer` for commands, selection or tick handlers, background Lua invocations and conversion extensions. Commands can wait and a failed job reports its own error. An importer must return valid Lua within the source quota.

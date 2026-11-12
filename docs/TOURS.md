# Convert and run a tour

In **Observer scripts**, choose **Script → Convert tour…**, select the source, and save the resulting Lua file. Run that file with **Script → Run Lua…**. **Esc** or **Stop script** stops the tour and its current camera flight.

The CEL converter recognizes the command blocks used by the verified demo, including target selection, timed flight, follow, time rates, waits, and some sky controls, orbital sweeps and continuous exponential distance changes. The CELX converter accepts the verified recursive Solar System tour. The generated Lua runs under the regular instruction, allocation, log and wait quotas. A tour can skip an unavailable target at the current epoch.

Each converted file records unapplied commands and flags in comments. Long scripts compress the original timing to fit the wait quota. The original source remains available for comparison.

The classic converter carries explicit TDB Julian dates, framed up vectors, longitude and latitude approaches, relative and encoded absolute camera poses, and turns about arbitrary axes. Unsupported commands are listed in the conversion report.

The existing script runner can open the original distance, absolute magnitude and year-stepping Lua scripts. Position and magnitude are resolved against the selected observer's epoch and local catalogue. Their flash messages are sent to the script log.

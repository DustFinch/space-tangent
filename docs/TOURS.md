# Convert and run a tour

In **Observer scripts**, choose **Script → Convert tour…**, select the source, and save the resulting Lua file. Run that file with **Script → Run Lua…**. **Esc** or **Stop script** stops the tour and its current camera flight.

The CEL converter recognizes the command blocks used by the verified demo, including target selection, timed flight, follow, time rates, waits, and some sky controls and orbital sweeps. The CELX converter accepts the verified recursive Solar System tour. The generated Lua runs under the regular instruction, allocation, log and wait quotas. A tour can skip an unavailable target at the current epoch.

Each converted file records unapplied commands and flags in comments. Long scripts compress the original timing to fit the wait quota. The original source remains available for comparison.

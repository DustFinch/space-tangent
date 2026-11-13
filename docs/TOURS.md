# Convert and run a tour

In **Observer scripts**, choose **Script → Convert tour…**, select the source, and save the resulting Lua file. Run that file with **Script → Run Lua…**. **Esc** or **Stop script** stops the tour and its current camera flight.

The CEL converter recognizes the command blocks used by the verified demo, including target selection, timed flight, follow, time rates, waits, and some sky controls, orbital sweeps and continuous exponential distance changes. The converter adapts the verified recursive Solar System tour to the local catalogue. For other CELX files it validates Lua syntax and saves the original program. At runtime the script can use the compatibility functions implemented by the simulator. The generated Lua runs under the regular instruction, allocation, log and wait quotas. A tour can skip an unavailable target at the current epoch.

Each converted file records unapplied commands and flags in comments. Classic command notes include the source line. Cloud, atmosphere, orbit, comet-tail and sky-grid flags apply to the observer running the script. Planet, moon and star label commands share the general labels switch. Long scripts compress the original timing to fit the wait quota. The original source remains available for comparison.

The classic converter carries explicit TDB Julian dates, framed up vectors, longitude and latitude approaches, relative and encoded absolute camera poses, and turns about arbitrary axes. Unsupported commands are listed in the conversion report.

The existing script runner can open the original distance, absolute magnitude and year-stepping Lua scripts. Position and magnitude are resolved against the selected observer's epoch and local catalogue. Their flash messages are sent to the script log.

Guide files use successive blocks with Target, Name and Description. Distances can be in km, AU or light-years. A distance without a unit is read as kilometres. Stops whose targets are unavailable in the active catalogue are skipped with a log message.

Classic overlay and play commands use image and audio files relative to the script folder. Converting a classic script copies its referenced files beside the exported Lua file. Compatible Lua scripts can call the same functions directly. Images appear in the originating view. Escape clears the overlay and stops the audio. Supported images are PNG, JPEG and WebP. Supported sounds are WAV, MP3 and Ogg.

Plugins can present the same media from files under their manifest directory. The image belongs to the plugin's observer. Disabling the plugin or closing that observer clears its images and audio channels.

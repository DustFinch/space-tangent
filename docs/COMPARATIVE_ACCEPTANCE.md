# Comparative UI acceptance

Status: pending owner review. Automated scientific, persistence and rendered-input checks supplement this exercise; they do not establish usability or identical interactive behavior.

Use the installed versions of Celestia, Cosmographia and Stellarium as applicable to each workflow. Record their versions, operating system and installed data first. Start from the same physical target, observer site and epoch. Distinguish UTC labels from TDB/TT and geometric positions from apparent ones. If a program lacks the required data coverage, record that limitation before comparing a picture.

For each task, record the initial state, the desired result, how you found the control, the steps used and whether you could repeat it after reopening. Screenshots should include comparable camera/sky settings. Pixel equality is not the acceptance criterion; geometry, timing, persistence and discoverability need separate judgments.

| Task | What to compare | Evidence to retain |
| --- | --- | --- |
| Find and fly to Earth, Saturn and a moon | Search, catalogue/tree context menu, flight distance, cancel/retarget and return to the prior view. | Controls used, unexpected selections and whether the next action was discoverable. |
| Follow Jason-1 | Select the real add-on target, fly, track, follow body rotation and return to free flight. | Target/camera modes, epoch/data validity and any motion or selection discontinuity. |
| Compare two views | Split, change the active view, move each camera and use independent epochs; enable synchronization and advance time. | Both epochs/rates before and after synchronization, cameras and active-view indication. |
| Reload and reopen a workspace | Reload real add-ons with different cameras and epochs, then save/reopen/restart. | Selection, camera pose/mode, layout, synchronization, ΔT and explicit error behavior for a missing source. |
| Full-Moon ground observation | Use the same Greenwich site/date, show the Moon, change field of view, atmosphere/clouds and optical aids. | Site height, UTC/TT labels, azimuth/altitude conventions, visibility and readability of controls. |
| Change observing places | Change sites separately in two views; visit the Moon and return to Earth; close/reopen and restart. | Independent remembered places, per-body history and explicit-scene precedence. |
| Inspect a planetary mission | Open supported original mission catalogues; inspect timed arcs, orientation, fields of view, footprint and annotations. | Exact catalogue/kernel set, epochs and coverage gaps. Do not infer support for unrelated fields from one successful import. |
| Plan an observing session | Search events, inspect daily/annual charts and Visible tonight, change civil date/site and export results. | Date/time conventions, unsupported intervals, result clarity and export contents. |
| Use sky layers and catalogues | Toggle grids, cultures, labels, landscapes and available local surveys; select an object from the sky. | Dataset versions, unavailable images, layer state per view and ease of recovering a clean sky. |
| Save, share and automate a view | Bookmark/scenario, supported imported link, tour/script and recording. | Preserved fields, explicit unsupported parameters and the actions required to reproduce the result. |

Use a short result record per task:

```text
Application/version and data:
Starting target, site and epoch/time scale:
Desired result:
Actions used:
Observed result and persistence:
Classification: matches accepted scope / missing behavior / awkward control / data gap / deferred
Evidence and suggested change:
```

Collect the observations into a prioritized UI plan after the comparison. First address incorrect state and inaccessible common actions; then unnecessary steps and unclear labels. Keep preferences about layout separate from functional regressions. This document proposes acceptance tasks and does not mark any cross-application session as completed.

# Observer views and bookmarks

The workspace can display two views side by side or stacked. **Settings** contains the layout, free-flight and body-rotation commands. An object camera and the surface observer each have a vertical field-of-view control. Existing scenes use the previous lens settings when they omit the field.

In free flight, **WASD** moves in the camera plane, **R/F** changes height, and right-drag turns the camera. **Track** follows the selected target in inertial axes. **Follow body rotation** preserves the local pose on targets with an available body orientation.

**Scenarios** can save a named bookmark or create a portable `spacetangent://scene/` link. Both preserve the view count and layout, camera placement and field, exact TDB epochs, the active view, time synchronization and ΔT. Bookmarks are stored in the user's profile. Set `SPACETANGENT_BOOKMARKS_PATH` to use a different profile file.

The link box imports `cel://` versions 3 and 4 in the `Freeflight` and `Follow` frames. Position, attitude, selection, TDB epoch, field of view and pause are restored. Unapplied parameters such as rendering flags are reported. A link whose frame or object is unavailable leaves the current scene in place.

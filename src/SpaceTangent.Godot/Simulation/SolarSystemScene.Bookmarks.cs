// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private ObserverBookmarkStore _bookmarkStore = null!;
    private IReadOnlyList<ObserverBookmark> _bookmarks => _bookmarkStore.Items;
    private LineEdit _bookmarkName = null!, _sceneLink = null!;
    private OptionButton _bookmarkList = null!;
    private Button _openBookmark = null!, _deleteBookmark = null!;
    private MenuButton _missionViewpoints = null!;
    private IReadOnlyList<MissionViewpoint>? _viewpointItems;

    private void AddObserverBookmarks(VBoxContainer panel)
    {
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_BOOKMARKS_PATH");
        _bookmarkStore = new(string.IsNullOrWhiteSpace(configured)
            ? ProjectSettings.GlobalizePath("user://bookmarks.json") : configured);
        try
        {
            _bookmarkStore.Load();
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _workspace.Status.Text = $"Could not read bookmarks. {error.Message}";
        }
        panel.AddChild(new HSeparator());
        panel.AddChild(UI.SimulatorTheme.Section("Bookmarks"));
        _bookmarkName = new LineEdit { Name = "BookmarkName", PlaceholderText = "Bookmark name", MaxLength = 100 };
        panel.AddChild(_bookmarkName);
        AddButton(panel, "Save bookmark", StoreBookmark).Name = "StoreBookmark";
        _bookmarkList = new OptionButton { Name = "SavedBookmarks" };
        panel.AddChild(_bookmarkList);
        var buttons = new HBoxContainer();
        panel.AddChild(buttons);
        _openBookmark = AddButton(buttons, "Open", () => OpenSceneLink(_bookmarks[_bookmarkList.Selected].Link));
        _openBookmark.Name = "RestoreBookmark";
        _deleteBookmark = AddButton(buttons, "Delete", DeleteBookmark);
        RefreshBookmarks();
        _missionViewpoints = new MenuButton { Name = "MissionViewpoints", Text = "Mission viewpoints", Visible = false };
        panel.AddChild(_missionViewpoints);
        _missionViewpoints.GetPopup().IdPressed += OpenMissionViewpoint;

        panel.AddChild(new HSeparator());
        panel.AddChild(UI.SimulatorTheme.Section("Scene link"));
        _sceneLink = new LineEdit { Name = "SceneLinkInput", PlaceholderText = "Paste a scene link", MaxLength = SceneLink.MaximumLength };
        panel.AddChild(_sceneLink);
        var links = new HBoxContainer();
        panel.AddChild(links);
        AddButton(links, "Create link", () => CreateSceneLink()).Name = "CreateSceneLink";
        AddButton(links, "Open link", () => OpenSceneLink(_sceneLink.Text)).Name = "OpenSceneLink";
    }

    private void RefreshMissionViewpoints()
    {
        IReadOnlyList<MissionViewpoint> items = _addOns?.Decoded.SampledMissions.Viewpoints ?? [];
        if (ReferenceEquals(items, _viewpointItems))
            return;
        _viewpointItems = items;
        var popup = _missionViewpoints.GetPopup();
        popup.Clear();
        for (int index = 0; index < items.Count; index++)
            popup.AddItem(items[index].Name, index);
        _missionViewpoints.Visible = items.Count != 0;
    }

    private void OpenMissionViewpoint(long id)
    {
        if (Session is not { } session || _viewpointItems is not { } items || id < 0 || id >= items.Count)
            return;
        var viewpoint = items[(int)id];
        _status.Text = session.OpenMissionViewpoint(_active, viewpoint)
            ? $"Viewpoint opened. {viewpoint.Name}"
            : "The viewpoint is unavailable at this epoch.";
        Refresh();
    }

    private bool ImportFavorites(string path)
    {
        try
        {
            var (selected, added, skipped) = _bookmarkStore.ImportFavorites(path, Session!.World, Session.DeltaT, _bookmarkList.Selected);
            RefreshBookmarks(selected);
            _status.Text = $"Imported {added} favorites ({skipped} skipped).";
            return true;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not import favorites. {error.Message}";
            return false;
        }
    }

    private void StoreBookmark()
    {
        try
        {
            int selected = _bookmarkStore.Store(_bookmarkName.Text, Session!.CaptureScenario(_active));
            RefreshBookmarks(selected);
            _status.Text = "Bookmark saved.";
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not save bookmark. {error.Message}";
        }
    }

    private void DeleteBookmark()
    {
        int selected = _bookmarkList.Selected;
        if (selected < 0)
            return;
        try
        {
            RefreshBookmarks(_bookmarkStore.Delete(selected));
            _status.Text = "Bookmark deleted.";
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not delete bookmark. {error.Message}";
        }
    }

    private void RefreshBookmarks(int selected = 0)
    {
        _bookmarkList.Clear();
        foreach (ObserverBookmark bookmark in _bookmarks)
            _bookmarkList.AddItem(bookmark.Name);
        bool empty = _bookmarks.Count == 0;
        if (!empty)
            _bookmarkList.Select(selected);
        _bookmarkList.Disabled = _openBookmark.Disabled = _deleteBookmark.Disabled = empty;
    }

    private void CreateSceneLink(bool legacy = false)
    {
        try
        {
            var session = Session!;
            _sceneLink.Text = legacy
                ? SceneLink.WriteLegacy(session[_active], session.World, session.DeltaT)
                : SceneLink.Write(session.CaptureScenario(_active));
            _sceneLink.GrabFocus();
            _sceneLink.SelectAll();
            _status.Text = "Scene link ready to copy.";
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not create scene link. {error.Message}";
        }
    }

    private void OpenSceneLink(string text)
    {
        try
        {
            var (scenario, warning) = SceneLink.Read(text, Session!.World, Session.DeltaT);
            ApplyScenario(scenario);
            _status.Text = warning is null ? "Scene link opened." : $"Scene link opened. {warning}";
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not open scene link. {error.Message}";
        }
    }
}

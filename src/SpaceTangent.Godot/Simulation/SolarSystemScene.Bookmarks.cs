// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private ObserverBookmarkFile _bookmarkFile = null!;
    private IReadOnlyList<ObserverBookmark> _bookmarks = [];
    private LineEdit _bookmarkName = null!, _sceneLink = null!;
    private OptionButton _bookmarkList = null!;
    private Button _openBookmark = null!, _deleteBookmark = null!;

    private void AddObserverBookmarks(VBoxContainer panel)
    {
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_BOOKMARKS_PATH");
        _bookmarkFile = new(string.IsNullOrWhiteSpace(configured)
            ? ProjectSettings.GlobalizePath("user://bookmarks.json") : configured);
        try
        {
            _bookmarks = _bookmarkFile.Load();
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

        panel.AddChild(new HSeparator());
        panel.AddChild(UI.SimulatorTheme.Section("Scene link"));
        _sceneLink = new LineEdit { Name = "SceneLinkInput", PlaceholderText = "Paste a scene link", MaxLength = SceneLink.MaximumLength };
        panel.AddChild(_sceneLink);
        var links = new HBoxContainer();
        panel.AddChild(links);
        AddButton(links, "Create link", () => CreateSceneLink()).Name = "CreateSceneLink";
        AddButton(links, "Open link", () => OpenSceneLink(_sceneLink.Text)).Name = "OpenSceneLink";
    }

    private void ImportFavorites(string path)
    {
        try
        {
            var (next, added, skipped) = _bookmarkFile.ImportFavorites(path, _bookmarks, Session!.World, Session.DeltaT);
            int selected = added == 0 ? Math.Max(0, _bookmarkList.Selected) : _bookmarks.Count;
            _bookmarks = next;
            RefreshBookmarks(selected);
            _status.Text = $"Imported {added} favorites ({skipped} skipped).";
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = $"Could not import favorites. {error.Message}";
        }
    }

    private void StoreBookmark()
    {
        try
        {
            string name = _bookmarkName.Text.Trim();
            string link = SceneLink.Write(Session!.CaptureScenario(_active));
            var next = _bookmarks.Where(bookmark => !bookmark.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            next.Add(new(name, link));
            _bookmarkFile.Save(next);
            _bookmarks = next.AsReadOnly();
            RefreshBookmarks(next.Count - 1);
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
            var next = _bookmarks.Where((_, index) => index != selected).ToArray();
            _bookmarkFile.Save(next);
            _bookmarks = Array.AsReadOnly(next);
            RefreshBookmarks(Math.Min(selected, next.Length - 1));
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

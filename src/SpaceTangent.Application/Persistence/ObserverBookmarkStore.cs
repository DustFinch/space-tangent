// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

/// <summary>Keeps the visible bookmark collection in step with successfully persisted changes.</summary>
public sealed class ObserverBookmarkStore(string path)
{
    private readonly ObserverBookmarkFile _file = new(path);
    public IReadOnlyList<ObserverBookmark> Items { get; private set; } = [];

    public void Load() => Items = _file.Load();

    public int Store(string name, SimulationScenario scenario)
    {
        name = name.Trim();
        string link = SceneLink.Write(scenario);
        var next = Items.Where(bookmark => !bookmark.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        next.Add(new(name, link));
        _file.Save(next);
        Items = next.AsReadOnly();
        return next.Count - 1;
    }

    public int Delete(int selected)
    {
        var next = Items.Where((_, index) => index != selected).ToArray();
        _file.Save(next);
        Items = Array.AsReadOnly(next);
        return Math.Min(selected, next.Length - 1);
    }

    public (int Selected, int Added, int Skipped) ImportFavorites(string source,
        SimulationWorld world, DeltaTModel model, int selected)
    {
        var (next, added, skipped) = _file.ImportFavorites(source, Items, world, model);
        int selection = added == 0 ? Math.Max(0, selected) : Items.Count;
        Items = next;
        return (selection, added, skipped);
    }
}

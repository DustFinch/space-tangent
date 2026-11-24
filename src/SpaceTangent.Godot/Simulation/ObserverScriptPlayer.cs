// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

// Owns a foreground script and its per-view media. Plugins retain their separate
// quota/lifetime owner; both continue using the same session operations.
internal sealed class ObserverScriptPlayer
{
    private readonly SimulationSession _session;
    private readonly Func<int, SimulationViewport> _renderer;
    private readonly Func<ScriptViewRequest, int> _views;
    private readonly Action<string> _capture, _status;
    private readonly Action<bool> _stopEnabled;
    private readonly List<(SimulationViewport Renderer, ScriptMediaPlayer Player)> _media = [];
    private LuaSessionScript? _script;
    internal LuaSessionScript? Current => _script;
    internal bool Active => _script is { Finished: false } || _media.Any(entry => entry.Player.Active);

    internal ObserverScriptPlayer(SimulationSession session, Func<int, SimulationViewport> renderer,
        Func<ScriptViewRequest, int> views, Action<string> capture, Action<string> status, Action<bool> stopEnabled)
    {
        _session = session; _renderer = renderer; _views = views;
        _capture = capture; _status = status; _stopEnabled = stopEnabled;
    }

    internal bool Step(double delta)
    {
        bool changed = false;
        if (_script is { } script)
        {
            changed |= script.Step(delta);
            if (script.Finished)
            {
                _script = null;
                _stopEnabled(false);
                if (script.Error is not null)
                    ClearMedia();
                _status(script.Error ?? "Lua script finished.");
            }
        }
        if (_media.Count > 0)
        {
            _media.RemoveAll(entry => !entry.Player.Attached);
            if (_script is null)
                _stopEnabled(_media.Any(entry => entry.Player.Active));
        }
        return changed;
    }

    internal LuaSessionScript Run(string path, int active, bool surfaces)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        LuaSessionScript? next = null;
        next = LuaSessionScript.Load(path, _session, active, surfaces,
            line => GD.Print(line), request => Present(next!.View, directory, request),
            views: _views);
        ClearMedia();
        if (_script is { } previous)
        {
            previous.Stop();
            if (previous.View >= 0)
                _session.ClearScriptNotice(previous.View);
        }
        _script = next;
        _stopEnabled(true);
        _status("Lua script running.");
        return next;
    }

    internal void Close()
    {
        _script?.Stop();
        ClearMedia();
    }

    internal void Stop()
    {
        if (_script is { } running)
        {
            running.Stop();
            if (running.View >= 0)
            {
                _session.ClearScriptNotice(running.View);
                _session.CancelFlight(running.View);
                _renderer(running.View).SetScriptNotice(null);
            }
        }
        _script = null;
        _stopEnabled(false);
        ClearMedia();
        _status("Lua script stopped.");
    }

    internal void RemoveView(SimulationViewport renderer)
    {
        int media = _media.FindIndex(entry => entry.Renderer == renderer);
        if (media >= 0)
        {
            _media[media].Player.Clear();
            _media.RemoveAt(media);
        }
    }

    private void Present(int view, string directory, ScriptMediaRequest request)
    {
        if (request is ScriptMediaRequest.Capture capture)
        {
            _capture(capture.File);
            return;
        }
        var renderer = _renderer(view);
        var entry = _media.Find(item => item.Renderer == renderer);
        if (entry.Player is null)
        {
            entry = (renderer, new ScriptMediaPlayer(renderer));
            _media.Add(entry);
        }
        entry.Player.Present(directory, request);
    }

    internal void ClearMedia()
    {
        foreach (var entry in _media)
            entry.Player.Clear();
        _media.Clear();
    }

}

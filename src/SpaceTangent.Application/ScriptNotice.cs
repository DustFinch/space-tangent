// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Application;

public readonly record struct ScriptNotice(string Text, int HorizontalOrigin, int VerticalOrigin, int Column, int Row)
{
    /// <summary>Baseline position measured from the lower left of the observer's viewport.</summary>
    public (double X, double Y) Cursor(int width, int height, int emWidth, int lineHeight)
    {
        double x = (HorizontalOrigin < 0 ? 0 : HorizontalOrigin == 0 ? width / 2 : width) + (double)Column * emWidth;
        double y = (VerticalOrigin < 0 ? -lineHeight : VerticalOrigin == 0 ? height / 2 : height) - (double)Row * lineHeight;
        return (x, y);
    }
}

public sealed partial class SimulationSession
{
    private readonly Dictionary<int, (ScriptNotice Message, double TimeLeft)> _scriptNotices = new();

    public ScriptNotice? Notice(int view) => _scriptNotices.TryGetValue(_viewIds[view], out var state) ? state.Message : null;

    public void ClearScriptNotice(int view) => _scriptNotices.Remove(_viewIds[view]);

    internal void ShowScriptNotice(int view, ScriptNotice notice, double duration)
    {
        if (notice.Text.Length == 0 || duration <= 0)
            ClearScriptNotice(view);
        else
            _scriptNotices[_viewIds[view]] = (notice, duration);
    }

    private bool ExpireScriptNotices(double seconds)
    {
        if (_scriptNotices.Count == 0 || !(seconds > 0))
            return false;
        bool expired = false;
        foreach (int id in _viewIds)
        {
            if (!_scriptNotices.TryGetValue(id, out var state))
                continue;
            if (state.TimeLeft > seconds)
                _scriptNotices[id] = (state.Message, state.TimeLeft - seconds);
            else
                expired |= _scriptNotices.Remove(id);
        }
        return expired;
    }
}

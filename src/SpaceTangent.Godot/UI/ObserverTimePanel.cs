// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using Godot;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Time;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.UI;

internal sealed record ObserverTimePanelActions(Action<int> TogglePlaying, Action<bool, int> Synchronize,
    Action<string, string> SeekUtc, Action<int, string> SeekTimeline, Action<int, double> Advance,
    Action<DeltaTModel> SetDeltaT, Action UpdateActiveFrame);

// Owns the clock controls and display cache, with observer operations supplied
// separately from construction. It never reads private scene state.
internal sealed class ObserverTimePanel
{
    private readonly SimulatorWorkspace _workspace;
    private readonly ObserverViewHost _host;
    private readonly ObserverTimePanelActions _actions;
    private readonly Func<JulianInstant, DeltaTModel, string> _civilLabel;
    private readonly Label _timeHeading, _calendarTime, _otherCalendars;
    private readonly Button _playFromPanel;
    private readonly CheckButton _sync;
    private readonly OptionButton _deltaT;
    private long? _otherCalendarDay;
    internal OptionButton Model => _deltaT;

    internal ObserverTimePanel(SimulatorWorkspace workspace, ObserverViewHost host,
        ObserverTimePanelActions actions, Func<JulianInstant, DeltaTModel, string> civilLabel)
    {
        _workspace = workspace; _host = host; _actions = actions; _civilLabel = civilLabel;
        var toolbar = workspace.Toolbar;
        var time = _workspace.AddPage("Time", WorkspaceText.Choose("Time", "Время"), "Location");
        _timeHeading = new Label { ThemeTypeVariation = "WorkspaceTitle" };
        time.AddChild(_timeHeading);
        _calendarTime = SimulatorTheme.Detail("");
        time.AddChild(_calendarTime);
        _playFromPanel = SimulatorButtons.Add(time, ViewControls.PlaybackButton(false), () => _actions.TogglePlaying(_host.Active));
        _sync = new CheckButton { Name = "SyncViewTime", ButtonPressed = true };
        _sync.Toggled += enabled => _actions.Synchronize(enabled, _host.Active);
        _sync.TooltipText = WorkspaceText.Choose("Synchronize the clocks of all views",
            "Синхронизировать часы всех видов");
        toolbar.AddChild(_sync);
        time.AddChild(SimulatorTheme.Section(WorkspaceText.Choose("Go to date", "Перейти к дате")));
        var dateFields = new HBoxContainer();
        time.AddChild(dateFields);
        var utcDate = new LineEdit { Name = "UtcDate", PlaceholderText = "YYYY-MM-DD", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var utcTime = new LineEdit { Name = "UtcTime", PlaceholderText = "HH:MM:SS", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        dateFields.AddChild(utcDate);
        dateFields.AddChild(utcTime);
        utcDate.TextSubmitted += _ => _actions.SeekUtc(utcDate.Text, utcTime.Text);
        utcTime.TextSubmitted += _ => _actions.SeekUtc(utcDate.Text, utcTime.Text);
        var dateActions = new HBoxContainer();
        time.AddChild(dateActions);
        SimulatorButtons.Add(dateActions, "Now", () =>
        {
            DateTime utc = DateTime.UtcNow;
            _actions.SeekUtc(utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        });
        SimulatorButtons.Add(dateActions, "Go to UTC", () => _actions.SeekUtc(utcDate.Text, utcTime.Text));
        var epochInput = new LineEdit { PlaceholderText = "Julian date (TDB)", Name = "TimePanelEpoch" };
        epochInput.TextSubmitted += text => _actions.SeekTimeline(_host.Active, text);
        time.AddChild(epochInput);
        time.AddChild(SimulatorTheme.Section(WorkspaceText.Choose("Step", "Шаг времени")));
        var steps = new HFlowContainer();
        time.AddChild(steps);
        foreach (var (label, seconds) in new[]
                 { ("−1 y", -31_557_600), ("−1 d", -86400), ("−1 h", -3600), ("+1 h", 3600), ("+1 d", 86400), ("+1 y", 31_557_600) })
            SimulatorButtons.Add(steps, label, () => _actions.Advance(_host.Active, seconds));
        time.AddChild(SimulatorTheme.Section(WorkspaceText.Choose("ΔT model", "Модель ΔT")));
        _deltaT = new OptionButton { Name = "DeltaTModel" };
        _deltaT.AddItem("ΔT · Espenak–Meeus", (int)DeltaTModel.EspenakMeeus2006);
        _deltaT.AddItem("ΔT · Morrison–Stephenson", (int)DeltaTModel.MorrisonStephenson2004);
        _deltaT.AddItem("ΔT · Fixed offset", (int)DeltaTModel.FixedOffset);
        _deltaT.ItemSelected += index => _actions.SetDeltaT((DeltaTModel)_deltaT.GetItemId((int)index));
        time.AddChild(_deltaT);

        var calendars = _workspace.AddPage("Calendars", WorkspaceText.Choose("Calendars", "Календари"), "Location");
        calendars.AddChild(SimulatorTheme.Detail(WorkspaceText.Choose(
            "Civil day from UTC, or the selected modeled UT1 outside UTC coverage. These calendars do not change the simulation clock.",
            "Гражданская дата UTC, а вне покрытия — выбранная модель UT1. Эти календари не меняют часы симуляции.")));
        _otherCalendars = SimulatorTheme.Detail("");
        _otherCalendars.Name = "CalendarLabels";
        calendars.AddChild(_otherCalendars);
        _otherCalendars.VisibilityChanged += () => { if (_otherCalendars.IsVisibleInTree()) _actions.UpdateActiveFrame(); };
    }

    internal void Update(int activeView, ObserverView active, bool synchronized, int viewCount, DeltaTModel model)
    {
        _sync.Visible = viewCount > 1;
        _sync.SetPressedNoSignal(synchronized);
        _sync.Text = synchronized ? WorkspaceText.Choose("Clocks linked", "Часы связаны")
            : WorkspaceText.Choose("Clocks independent", "Часы раздельны");
        string playback = active.Playing ? WorkspaceText.Choose("Playing", "Идёт") : WorkspaceText.Choose("Paused", "Пауза");
        _workspace.Observation.Text = $"{WorkspaceText.Choose("View", "Вид")} {activeView + 1} · {playback}";
        _timeHeading.Text = FormattableString.Invariant($"JD {active.Epoch.ToJulianDate():F6}");
        _calendarTime.Text = _civilLabel(active.Epoch, model);
        if (_otherCalendars.IsVisibleInTree())
        {
            var civilDay = CivilCalendarDisplay.CivilDay(active.Epoch, model);
            if (_otherCalendarDay != civilDay?.DayNumber || _otherCalendars.Text.Length == 0)
            {
                _otherCalendarDay = civilDay?.DayNumber;
                _otherCalendars.Text = civilDay is { } date ? CivilCalendarDisplay.Format(date) : "Civil calendars unavailable at this epoch.";
            }
        }
        _playFromPanel.Text = ViewControls.PlaybackButton(active.Playing);
    }
}

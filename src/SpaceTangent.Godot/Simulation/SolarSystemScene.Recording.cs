// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

public partial class SolarSystemScene
{
    private WindowRecording? _movie;
    private int _moviePreviousFps;
    private int? _movieFrameLimit;
    private string? _screenshotPath;
    private IObserverReplay? _replay;
    private FileStream? _replayFile;
    private double _replayRemainder;
    private bool _captureConnected;
    private bool _finishingMovie;
    private bool _captureFailed;

    internal int RecordedFrames => _movie?.FramesWritten ?? 0;
    internal bool Replaying => _replay is not null;
    private bool MovieActive => _movie is not null;

    private void ConnectCapture()
    {
        if (DisplayServer.GetName() == "headless")
            return;
        RenderingServer.FramePostDraw += CaptureDrawnFrame;
        _captureConnected = true;
    }

    private void DisconnectCapture()
    {
        if (_captureConnected)
            RenderingServer.FramePostDraw -= CaptureDrawnFrame;
        if (_movie is { } movie)
        {
            _movie = null;
            Engine.MaxFps = _moviePreviousFps;
            try { movie.Finish().GetAwaiter().GetResult(); }
            catch (Exception error) when (IsLoadError(error)) { GD.Print("Recording ended: " + error.Message); }
        }
        CloseReplay();
    }

    internal bool QueueScreenshot(string? output = null)
    {
        if (!_captureConnected)
        {
            _status.Text = "A rendered viewport is needed for a screenshot.";
            return false;
        }
        try
        {
            _screenshotPath = output is null ? CaptureStem() + ".png" : Path.GetFullPath(output);
            return true;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = "Could not prepare screenshot. " + error.Message;
            return false;
        }
    }

    private void ToggleRecording()
    {
        if (_movie is not null)
            _ = StopRecording();
        else
            StartRecording();
    }

    internal bool StartRecording(int? frameLimit = null)
    {
        if (!_captureConnected || _finishingMovie || _movie is not null || Replaying)
        {
            _status.Text = "Start recording from a rendered live scene.";
            return false;
        }
        try
        {
            Vector2 size = GetViewport().GetVisibleRect().Size;
            int width = (int)size.X & ~1, height = (int)size.Y & ~1;
            if (width is < 2 or > 8192 || height is < 2 or > 8192)
                throw new InvalidOperationException("The viewport size is outside the movie range.");
            _movie = new(CaptureStem(), width, height);
            _moviePreviousFps = Engine.MaxFps;
            Engine.MaxFps = WindowRecording.FramesPerSecond;
            _movieFrameLimit = frameLimit;
            _status.Text = "Recording · Shift+F10 to stop.";
            return true;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _status.Text = "Could not start recording. " + error.Message;
            return false;
        }
    }

    internal async Task<RecordingResult?> StopRecording()
    {
        if (_movie is not { } movie)
            return null;
        _movie = null;
        _movieFrameLimit = null;
        _finishingMovie = true;
        Engine.MaxFps = _moviePreviousFps;
        try
        {
            var result = await movie.Finish();
            if (IsInsideTree())
                _status.Text = $"Saved {result.Frames} frames · {result.Video}";
            return result;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _captureFailed = true;
            if (IsInsideTree())
                _status.Text = "Could not finish recording. " + error.Message;
            return null;
        }
        finally { _finishingMovie = false; }
    }

    private void CaptureDrawnFrame()
    {
        if (_screenshotPath is { } output)
        {
            _screenshotPath = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                using var image = GetViewport().GetTexture().GetImage();
                if (image.SavePng(output) != Error.Ok)
                    throw new IOException("PNG encoding failed.");
                _status.Text = $"Screenshot saved · {output}";
            }
            catch (Exception error) when (IsLoadError(error))
            {
                _captureFailed = true;
                _status.Text = "Could not save screenshot. " + error.Message;
            }
        }
        if (_movie is not { } movie || Session is not { } session)
            return;
        if (movie.IsDone || _movieFrameLimit is <= 0)
        {
            _ = StopRecording();
            return;
        }
        var frame = GetViewport().GetTexture().GetImage();
        if (frame.GetWidth() != movie.Width || frame.GetHeight() != movie.Height)
            frame.Resize(movie.Width, movie.Height);
        if (movie.Enqueue(frame, session.CaptureScenario(_active)))
            _movieFrameLimit--;
        else
            _ = StopRecording();
    }

    internal bool StartReplay(string path)
    {
        if (MovieActive)
        {
            _status.Text = "Stop recording before opening a replay.";
            return false;
        }
        CloseReplay();
        try
        {
            _replayFile = File.OpenRead(path);
            if (Path.GetExtension(path).Equals(".celframe", StringComparison.OrdinalIgnoreCase))
            {
                _replay = new LegacyCameraSequence(_replayFile, Session!.World, Session.DeltaT);
                _replayFile.Dispose();
                _replayFile = null;
            }
            else
                _replay = new ObserverFrameSequence.Reader(_replayFile);
            _replayRemainder = 0;
            if (_replay.ReadNext() is not { } first)
                throw new InvalidDataException("The replay is empty.");
            ApplyScenario(first);
            _status.Text = $"Replaying {_replay.FrameCount} observer frames.";
            return true;
        }
        catch (Exception error) when (IsLoadError(error))
        {
            CloseReplay();
            _status.Text = "Could not open observer replay. " + error.Message;
            return false;
        }
    }

    private bool AdvanceReplay(double delta)
    {
        if (_replay is not { } replay)
            return false;
        _replayRemainder += delta;
        double period = 1.0 / replay.FramesPerSecond;
        if (_replayRemainder < period)
            return true;
        _replayRemainder %= period;
        try
        {
            if (replay.ReadNext() is { } frame)
                ApplyScenario(frame);
            else
            {
                CloseReplay();
                for (int index = 0; index < Session!.ViewCount; index++)
                    Session.SetPlaying(index, false);
                Refresh();
                _status.Text = $"Replay complete · {replay.FrameCount} frames";
                GD.Print(_status.Text);
            }
        }
        catch (Exception error) when (IsLoadError(error))
        {
            _captureFailed = true;
            CloseReplay();
            _status.Text = "Could not advance observer replay. " + error.Message;
        }
        return true;
    }

    private void CloseReplay()
    {
        _replay?.Dispose();
        _replayFile?.Dispose();
        _replay = null;
        _replayFile = null;
    }

    private static string CaptureStem()
    {
        string? configured = System.Environment.GetEnvironmentVariable("SPACETANGENT_CAPTURE_OUTPUT");
        string directory = string.IsNullOrWhiteSpace(configured)
            ? ProjectSettings.GlobalizePath("user://captures") : Path.GetFullPath(configured);
        Directory.CreateDirectory(directory);
        string date = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        return Path.Combine(directory, $"spacetangent-{date}-{Guid.NewGuid():N}");
    }
}

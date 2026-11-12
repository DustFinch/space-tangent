// SPDX-License-Identifier: GPL-2.0-or-later
using System.Threading.Channels;
using Godot;
using SpaceTangent.Application;

namespace SpaceTangent.Godot.Simulation;

internal sealed record RecordingResult(string Video, string ObserverFrames, int Frames);

internal sealed class WindowRecording
{
    internal const int FramesPerSecond = 30;
    private sealed record Frame(Image Image, SimulationScenario Scenario);
    private readonly Channel<Frame> _queue = Channel.CreateBounded<Frame>(new BoundedChannelOptions(6)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Task<RecordingResult> _worker;
    private int _framesWritten, _limitReached;

    internal WindowRecording(string stem, int width, int height)
    {
        Width = width;
        Height = height;
        _worker = Task.Run(() => Encode(stem));
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int FramesWritten => Volatile.Read(ref _framesWritten);
    internal bool IsDone => _worker.IsCompleted || Volatile.Read(ref _limitReached) != 0;

    internal bool Enqueue(Image image, SimulationScenario scenario)
    {
        if (IsDone)
        {
            image.Dispose();
            return false;
        }
        try
        {
            _queue.Writer.WriteAsync(new(image, scenario)).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (ChannelClosedException)
        {
            image.Dispose();
            return false;
        }
    }

    internal Task<RecordingResult> Finish()
    {
        _queue.Writer.TryComplete();
        return _worker;
    }

    private async Task<RecordingResult> Encode(string stem)
    {
        string video = stem + ".avi", recording = stem + ".stframes";
        string videoTemp = video + ".partial", recordingTemp = recording + ".partial";
        try
        {
            using (var videoFile = new FileStream(videoTemp, FileMode.CreateNew, System.IO.FileAccess.Write))
            using (var stateFile = new FileStream(recordingTemp, FileMode.CreateNew, System.IO.FileAccess.Write))
            using (var movie = new AviJpegWriter(videoFile, Width, Height, FramesPerSecond))
            using (var states = new ObserverFrameSequence.Writer(stateFile, FramesPerSecond))
            {
                await foreach (Frame frame in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    using var image = frame.Image;
                    if (Volatile.Read(ref _limitReached) != 0)
                        continue;
                    if (!movie.Append(image.SaveJpgToBuffer(.9f)))
                    {
                        Volatile.Write(ref _limitReached, 1);
                        continue;
                    }
                    states.Append(frame.Scenario);
                    Volatile.Write(ref _framesWritten, movie.FrameCount);
                }
                if (movie.FrameCount == 0)
                    throw new InvalidDataException("No frames were rendered during the recording.");
                states.Complete();
            }
            File.Move(recordingTemp, recording);
            try { File.Move(videoTemp, video); }
            catch
            {
                File.Delete(recording);
                throw;
            }
            return new(video, recording, FramesWritten);
        }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out Frame? item))
                item.Image.Dispose();
            File.Delete(videoTemp);
            File.Delete(recordingTemp);
        }
    }
}

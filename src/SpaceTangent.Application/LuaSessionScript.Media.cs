// SPDX-License-Identifier: GPL-2.0-or-later
using MoonSharp.Interpreter;

namespace SpaceTangent.Application;

public abstract record ScriptMediaRequest(string File)
{
    public sealed record Overlay(string File, double Seconds, double X, double Y, double Width, double Height, double Opacity)
        : ScriptMediaRequest(File);

    public sealed record Sound(string File, double Volume, int Channel, bool Loop) : ScriptMediaRequest(File);
}

public sealed partial class LuaSessionScript
{
    private void BindMedia(Table api, Table upstream, Action<ScriptMediaRequest> present)
    {
        Bind(api, "overlay", (_, arguments) => Overlay(arguments));
        Bind(api, "play", (_, arguments) => Sound(arguments));
        Bind(upstream, "overlay", (_, arguments) => Overlay(arguments.SkipMethodCall()));
        Bind(upstream, "play", (_, arguments) => Sound(arguments.SkipMethodCall()));

        DynValue Overlay(CallbackArguments args)
        {
            if (++_running!.MediaRequests > 32)
                throw new ScriptRuntimeException("The script exceeded its media quota.");
            present(new ScriptMediaRequest.Overlay(Text(args[0]), Read(args, 1, 3, .01, 600),
                Read(args, 2, .5, -4, 4), Read(args, 3, .5, -4, 4),
                Read(args, 4, 0, 0, 4096), Read(args, 5, 0, 0, 4096), Read(args, 6, 1, 0, 1)));
            return DynValue.Nil;
        }

        DynValue Sound(CallbackArguments args)
        {
            double channel = Read(args, 2, 0, 0, 15);
            if (channel != Math.Truncate(channel))
                throw new ScriptRuntimeException("Audio channels must be whole numbers.");
            if (++_running!.MediaRequests > 32)
                throw new ScriptRuntimeException("The script exceeded its media quota.");
            present(new ScriptMediaRequest.Sound(Text(args[0]), Read(args, 1, 1, 0, 1), (int)channel,
                !args[3].IsNil() && Boolean(args[3])));
            return DynValue.Nil;
        }

        static double Read(CallbackArguments args, int index, double fallback, double min, double max)
        {
            double value = args[index].IsNil() ? fallback : Number(args[index]);
            return value >= min && value <= max ? value : throw new ScriptRuntimeException("A media parameter is outside its range.");
        }
    }
}

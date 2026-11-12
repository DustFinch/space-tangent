// SPDX-License-Identifier: GPL-2.0-or-later
using MoonSharp.Interpreter;

namespace SpaceTangent.Application;

public sealed partial class LuaSessionScript
{
    private DynValue PrintNotice(CallbackArguments arguments, bool native)
    {
        if (arguments.Count is < 1 or > 6)
            throw new ScriptRuntimeException("A script message takes text, duration, two anchors and two offsets.");
        string text = Text(arguments[0]);
        if (text.Length > 512)
            throw new ScriptRuntimeException("A script message exceeds 512 characters.");
        double duration = Optional(1, native ? 1.5 : 3);
        int horizontal = Offset(2, native ? -1 : 0), vertical = Offset(3, native ? -1 : 0);
        int column = Offset(4, 0), row = Offset(5, native ? 5 : 0);
        if (native)
        {
            horizontal = Math.Sign(horizontal);
            vertical = Math.Sign(vertical);
            row = -row;
            if (duration < 0)
                duration = 1.5;
        }
        if (horizontal is < -1 or > 1 || vertical is < -1 or > 1 || duration > 600)
            throw new ScriptRuntimeException("A script message anchor or duration is out of range.");
        Log(text);
        return Change(() => _session.ShowScriptNotice(View, new(text, horizontal, vertical, column, row), duration));

        double Optional(int index, double fallback) => arguments[index].IsNil() ? fallback : Number(arguments[index]);
        int Offset(int index, int fallback)
        {
            double value = Optional(index, fallback);
            return value is >= -4096 and <= 4096 ? (int)value : throw new ScriptRuntimeException("A script message offset is out of range.");
        }
    }
}

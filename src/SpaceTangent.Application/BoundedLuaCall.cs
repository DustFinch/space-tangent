// SPDX-License-Identifier: GPL-2.0-or-later
using MoonSharp.Interpreter;

namespace SpaceTangent.Application;

internal static class BoundedLuaCall
{
    private const int Instructions = 4096;
    private const long Allocations = 2 * 1024 * 1024;

    internal static DynValue Invoke(Script script, DynValue function, params DynValue[] arguments)
    {
        var coroutine = script.CreateCoroutine(function).Coroutine;
        coroutine.AutoYieldCounter = 1;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int step = 0; step < Instructions; step++)
        {
            var value = step == 0 ? coroutine.Resume(arguments) : coroutine.Resume();
            if (GC.GetAllocatedBytesForCurrentThread() - before > Allocations)
                throw new ScriptRuntimeException("Lua callback allocation quota exceeded.");
            if (coroutine.State == CoroutineState.Dead)
                return value;
        }
        throw new ScriptRuntimeException("Lua callback instruction quota exceeded.");
    }
}

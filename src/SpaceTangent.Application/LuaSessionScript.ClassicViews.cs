// SPDX-License-Identifier: GPL-2.0-or-later
using MoonSharp.Interpreter;

namespace SpaceTangent.Application;

public enum ScriptViewOperation { Split, Delete, Activate, Keep }

public readonly record struct ScriptViewRequest(ScriptViewOperation Operation, int View,
    ViewSplit Direction = ViewSplit.SideBySide, double Share = .5);

public sealed partial class LuaSessionScript
{
    private void BindClassicViews(Table api, Func<ScriptViewRequest, int> apply)
    {
        Bind(api, "legacyview", (_, args) =>
        {
            if (_running is null || ++_running.ViewRequests > 32)
                throw new ScriptRuntimeException("The script exceeded its view operation quota.");
            var operation = Text(args[0]) switch
            {
                "splitview" => ScriptViewOperation.Split,
                "deleteview" => ScriptViewOperation.Delete,
                "makeactiveview" => ScriptViewOperation.Activate,
                "singleview" => ScriptViewOperation.Keep,
                _ => throw new ScriptRuntimeException("Unknown classic view operation.")
            };
            double number = operation == ScriptViewOperation.Keep ? View + 1 : Number(args[1]);
            if (number < 1 || number > _session.ViewCount)
                return DynValue.Nil;
            int view = (int)number - 1;
            var request = new ScriptViewRequest(operation, view);
            if (operation == ScriptViewOperation.Split)
                request = request with
                {
                    Direction = Text(args[2]).Equals("h", StringComparison.OrdinalIgnoreCase) ? ViewSplit.Stacked : ViewSplit.SideBySide,
                    Share = Math.Clamp(Number(args[3]), .1, .9)
                };
            int active = apply(request);
            _viewIdentity = _session.ViewIdentity(active);
            _changed = true;
            return DynValue.Nil;
        });
    }
}

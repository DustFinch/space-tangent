// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using MoonSharp.Interpreter;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

public sealed partial class LuaSessionScript
{
    private Table? _objectEquality;

    private Table ObjectEquality()
    {
        if (_objectEquality is { } cached)
            return cached;
        var table = _objectEquality = new Table(_vm);
        Bind(table, "__eq", (_, arguments) => DynValue.NewBoolean(LuaObjectId(arguments[0]) == LuaObjectId(arguments[1])));
        return table;
    }

    private void BindNativeMarkers(Table upstream)
    {
        Bind(upstream, "dsos", (_, _) => DeepSkyIterator());
        Bind(upstream, "unmarkall", (_, _) => Change(() => _session.ClearScriptMarkers(View)));
        Bind(upstream, "mark", (_, arguments) =>
        {
            var args = arguments.SkipMethodCall();
            return Put(LuaObjectId(args[0]), args[1], args[2], args[3], args[4]);
        });
        Bind(upstream, "show", (_, arguments) => Visibility(arguments.SkipMethodCall(), true));
        Bind(upstream, "hide", (_, arguments) => Visibility(arguments.SkipMethodCall(), false));
    }

    private DynValue Visibility(CallbackArguments arguments, bool visible)
    {
        if (arguments.Count != 1 || !Text(arguments[0]).Equals("markers", StringComparison.OrdinalIgnoreCase))
            throw new ScriptRuntimeException("Expected the markers layer.");
        return Change(() => _session.SetScriptMarkersVisible(View, visible));
    }

    private DynValue DeepSkyIterator()
    {
        var objects = _session.World.DeepSky.Objects;
        var world = _session.World;
        int next = 0;
        return DynValue.NewCallback((_, _) =>
        {
            if (!ReferenceEquals(world, _session.World))
                throw new ScriptRuntimeException("The deep-sky catalogue changed during iteration.");
            if (next == objects.Count)
                return DynValue.Nil;
            if (_running is not { } work || work.CatalogueReads >= 2 * SimulationSession.MaximumScriptMarkers)
                throw new ScriptRuntimeException("Lua deep-sky catalogue quota exceeded.");
            // Each host record contributes a bounded allowance for the loop body.
            work.CatalogueReads++;
            return LuaObject(objects[next++].Id);
        });
    }

    private DynValue Mark(ObjectId id, CallbackArguments args) => Put(id, args[0], args[1], args[2], args[3]);

    private DynValue Put(ObjectId id, DynValue color, DynValue shape, DynValue size, DynValue opacity)
    {
        if (_session.World.Catalogue.Find(id) is null)
            throw new ScriptRuntimeException("The marked object is absent from the catalogue.");
        string hex = color.IsNil() ? "#00ff00" : Text(color);
        if (hex.Length != 7 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            throw new ScriptRuntimeException("A marker color needs six hexadecimal digits.");
        if (!shape.IsNil() && !Text(shape).Equals("disk", StringComparison.OrdinalIgnoreCase))
            throw new ScriptRuntimeException("Expected a disk marker.");
        double diameter = size.IsNil() ? 10 : Number(size), alpha = opacity.IsNil() ? 1 : Number(opacity);
        if (diameter is <= 0 or > 128 || alpha is < 0 or > 1)
            throw new ScriptRuntimeException("Marker size or opacity is out of range.");
        var channels = new Vector3d((rgb >> 16) / 255d, ((rgb >> 8) & 255) / 255d, (rgb & 255) / 255d);
        return Change(() => _session.PutScriptMarker(View, id, new(channels, diameter, alpha)));
    }
}

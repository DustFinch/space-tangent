// SPDX-License-Identifier: GPL-2.0-or-later
using MoonSharp.Interpreter;
using SpaceTangent.Contracts;

namespace SpaceTangent.Application;

public sealed partial class LuaSessionScript
{
    private void BindNativeQueries(Table upstream)
    {
        Bind(upstream, "getselection", (_, _) => LuaObject(_session[View].Selection));
        Bind(upstream, "flash", (_, arguments) =>
        {
            Log(Text(arguments.SkipMethodCall()[0]));
            return DynValue.Nil;
        });
    }

    private DynValue ObjectPosition(ObjectId id)
    {
        if (!_session.World.TryNavigationState(id, NativeSnapshot(), out var state, _session.DeltaT))
            throw new ScriptRuntimeException("The selected object has no position at this epoch.");
        return Position(state.PositionKilometers);
    }

    private DynValue AbsoluteMagnitude(ObjectId id)
    {
        double? magnitude = _session.World.DeepSky.Find(id)?.AbsoluteMagnitude;
        if (_session.World.FindStellarTarget(id) is { DistanceParsecs: > 0 } star && !star.IsBarycenter)
            magnitude = star.AbsoluteMagnitude ?? star.VisualMagnitude + 5 - 5 * Math.Log10(star.DistanceParsecs);
        return magnitude is { } value && double.IsFinite(value) ? DynValue.NewNumber(value) : DynValue.Nil;
    }

    private DynValue Position(Vector3d kilometers)
    {
        var value = new Table(_vm)
        {
            ["x"] = DynValue.NewNumber(kilometers.X),
            ["y"] = DynValue.NewNumber(kilometers.Y),
            ["z"] = DynValue.NewNumber(kilometers.Z)
        };
        Bind(value, "distanceto", (_, arguments) =>
        {
            DynValue other = arguments.SkipMethodCall()[0];
            if (other.Type != DataType.Table)
                throw new ScriptRuntimeException("A distance requires another world position.");
            var endpoint = new Vector3d(Number(other.Table.Get("x")), Number(other.Table.Get("y")), Number(other.Table.Get("z")));
            double distance = (kilometers - endpoint).Length;
            if (!double.IsFinite(distance))
                throw new ScriptRuntimeException("The distance is outside the coordinate range.");
            return DynValue.NewNumber(distance);
        });
        return DynValue.NewTable(value);
    }
}

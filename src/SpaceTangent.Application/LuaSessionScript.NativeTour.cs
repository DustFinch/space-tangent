// SPDX-License-Identifier: GPL-2.0-or-later
using MoonSharp.Interpreter;
using SpaceTangent.Contracts;
using SpaceTangent.Core.World;

namespace SpaceTangent.Application;

public sealed partial class LuaSessionScript
{
    private readonly Dictionary<ObjectId, DynValue> _luaObjects = new();
    private SimulationWorld? _treeWorld;
    private ILookup<ObjectId, CatalogueObject>? _solarChildren;
    private DynValue? _nativeObserver;
    private (SimulationWorld World, WorldSnapshot Snapshot)? _nativeSnapshot;

    private void BindNativeObjects(Table api)
    {
        Bind(api, "find", (_, arguments) => FindObject(arguments[0]));
        Bind(api, "selection", (_, _) => LuaObject(_session[View].Selection));
        Bind(api, "celxprint", (_, arguments) => PrintNotice(arguments, native: true));
    }

    private void BindNativeObserver(Table upstream)
    {
        Bind(upstream, "getobserver", (_, _) => _nativeObserver ??= MakeNativeObserver());
        BindNativeQueries(upstream);
        BindNativeMarkers(upstream);
    }

    private DynValue MakeNativeObserver()
    {
        var observer = new Table(_vm);
        Bind(observer, "follow", (_, arguments) => Change(() =>
        {
            ObjectId id = LuaObjectId(arguments.SkipMethodCall()[0]);
            _session.SelectObject(View, id);
            if (_session[View].Navigation is not { Tracking: true } current || current.Target != id)
                _session.ToggleTracking(View, NativeSnapshot());
        }));
        Bind(observer, "gotoobject", (_, arguments) =>
        {
            var args = arguments.SkipMethodCall();
            double duration = args[1].IsNil() ? 5 : Number(args[1]);
            if (duration is < 0 or > 300)
                throw new ScriptRuntimeException("Observer flight duration is outside its supported range.");
            return Change(() =>
            {
                _session.SelectObject(View, LuaObjectId(args[0]));
                _session.GoToSelection(View, duration, null, NativeSnapshot());
            });
        });
        Bind(observer, "gotodistance", (_, arguments) =>
        {
            var args = arguments.SkipMethodCall();
            ObjectId target = LuaObjectId(args[0]);
            double range = Number(args[1]), duration = args[2].IsNil() ? 5 : Number(args[2]);
            if (range <= 0 || duration is < 0 or > 300)
                throw new ScriptRuntimeException("Observer flight distance or duration is out of range.");
            return Change(() =>
            {
                _session.SelectObject(View, target);
                _session.GoToSelection(View, duration, range, NativeSnapshot());
            });
        });
        Bind(observer, "travelling", (_, _) => DynValue.NewBoolean(_session.IsFlying(View)));
        Bind(observer, "getposition", (_, _) => Position(_session.ScriptObserverEye(View, NativeSnapshot())));
        return DynValue.NewTable(observer);
    }

    internal DynValue LuaObject(ObjectId? key)
    {
        if (key is not { } id || _session.World.Catalogue.Find(id) is not { } catalogue)
            return DynValue.Nil;
        if (_luaObjects.TryGetValue(id, out var existing))
            return existing;
        if (_luaObjects.Count >= 128)
            _luaObjects.Clear();
        var item = new Table(_vm);
        item["id_value"] = id.Value;
        Bind(item, "id", (_, _) => DynValue.NewString(id.Value));
        Bind(item, "name", (_, _) => DynValue.NewString(_session.World.Catalogue.Find(id)?.Name ?? catalogue.Name));
        Bind(item, "type", (_, _) => DynValue.NewString(catalogue.Kind.ToString().ToLowerInvariant()));
        Bind(item, "radius", (_, _) => DynValue.NewNumber(_session.World.TargetRadiusKilometers(id)));
        Bind(item, "parent", (_, _) => LuaObject(catalogue.Parent));
        Bind(item, "getposition", (_, _) => ObjectPosition(id));
        Bind(item, "absmag", (_, _) => AbsoluteMagnitude(id));
        Bind(item, "children", (_, _) => Children(id));
        item.MetaTable = ObjectEquality();
        Bind(item, "mark", (_, arguments) => Mark(id, arguments.SkipMethodCall()));
        Bind(item, "unmark", (_, _) => Change(() => _session.RemoveScriptMarker(View, id)));
        var value = DynValue.NewTable(item);
        _luaObjects.Add(id, value);
        return value;
    }

    private DynValue Children(ObjectId parent)
    {
        var world = _session.World;
        if (!ReferenceEquals(_treeWorld, world))
        {
            _treeWorld = world;
            _solarChildren = world.Catalogue.Objects.Where(item => item.Parent.HasValue &&
                item.Kind is CatalogueKind.Planet or CatalogueKind.Moon or CatalogueKind.DwarfPlanet)
                .ToLookup(item => item.Parent!.Value);
        }
        var children = new Table(_vm);
        int index = 1;
        foreach (var child in _solarChildren![parent].Take(32))
            children[index++] = LuaObject(child.Id);
        return DynValue.NewTable(children);
    }

    private WorldSnapshot NativeSnapshot()
    {
        SimulationWorld world = _session.World;
        JulianInstant epoch = _session[View].Epoch;
        if (_nativeSnapshot is { } recent && ReferenceEquals(recent.World, world) && recent.Snapshot.Epoch == epoch)
            return recent.Snapshot;
        WorldSnapshot snapshot = world.Graph.Evaluate(epoch);
        _nativeSnapshot = (world, snapshot);
        return snapshot;
    }

    private static ObjectId LuaObjectId(DynValue value) =>
        value.Type == DataType.Table && value.Table.Get("id_value") is { Type: DataType.String } id
            ? new(id.String) : throw new ScriptRuntimeException("Expected an object returned by st.find.");
}

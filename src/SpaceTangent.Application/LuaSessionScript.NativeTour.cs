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
    private readonly Dictionary<int, DynValue> _nativeObservers = [];
    private (SimulationWorld World, WorldSnapshot Snapshot)? _nativeSnapshot;

    private void BindNativeObjects(Table api)
    {
        Bind(api, "find", (_, arguments) => FindObject(arguments[0]));
        Bind(api, "selection", (_, _) => LuaObject(_session[View].Selection));
        Bind(api, "celxprint", (_, arguments) => PrintNotice(arguments, native: true));
    }

    private void BindNativeObserver(Table upstream, bool plugin)
    {
        Bind(upstream, "getobserver", (_, _) => NativeObserver(View, plugin));
        Bind(upstream, "getobservers", (_, _) =>
        {
            var observers = new Table(_vm);
            if (plugin)
                observers[1] = NativeObserver(View, plugin);
            else
                for (int view = 0; view < _session.ViewCount; view++)
                    observers[view + 1] = NativeObserver(view, plugin);
            return DynValue.NewTable(observers);
        });
        BindNativeQueries(upstream);
        BindNativeMarkers(upstream);
    }

    private DynValue NativeObserver(int view, bool plugin)
    {
        int identity = _session.ViewIdentity(view);
        if (!_nativeObservers.TryGetValue(identity, out var observer))
        {
            foreach (int removed in _nativeObservers.Keys.Where(id => _session.IndexOfView(id) < 0).ToArray())
                _nativeObservers.Remove(removed);
            _nativeObservers.Add(identity, observer = MakeNativeObserver(identity, plugin));
        }
        return observer;
    }

    private DynValue MakeNativeObserver(int identity, bool plugin)
    {
        var observer = new Table(_vm);
        int Index() => _session.IndexOfView(identity) is >= 0 and var index ? index :
            throw new ScriptRuntimeException("Observer has been deleted.");
        Bind(observer, "isvalid", (_, _) => DynValue.NewBoolean(_session.IndexOfView(identity) >= 0));
        Bind(observer, "getfov", (_, _) => DynValue.NewNumber(_session[Index()].EffectiveFieldOfView * Math.PI / 180));
        Bind(observer, "setfov", (_, arguments) =>
        {
            int view = Index();
            var args = arguments.SkipMethodCall();
            if (args[0].CastToNumber() is not { } radians)
                throw new ScriptRuntimeException("Expected a field angle.");
            bool synchronize = args[1].IsNil() || Boolean(args[1]);
            double degrees = radians * 180 / Math.PI;
            return ObserverView.ValidFieldOfView(degrees)
                ? Change(() => _session.SetScriptFieldOfView(view, degrees, synchronize, synchronizeOtherViews: !plugin))
                : DynValue.Nil;
        });
        Bind(observer, "follow", (_, arguments) => Change(() =>
        {
            ObjectId id = LuaObjectId(arguments.SkipMethodCall()[0]);
            _session.SelectObject(Index(), id);
            if (_session[Index()].Navigation is not { Tracking: true } current || current.Target != id)
                _session.ToggleTracking(Index(), NativeSnapshot(Index()));
        }));
        Bind(observer, "track", (_, arguments) =>
        {
            var target = arguments.SkipMethodCall()[0];
            return Change(() => _session.SetScriptAimTarget(Index(), target.IsNil() ? null : LuaObjectId(target)));
        });
        Bind(observer, "gettrackedobject", (_, _) => LuaObject(_session[Index()].ScriptAimTarget));
        Bind(observer, "gotoobject", (_, arguments) =>
        {
            var args = arguments.SkipMethodCall();
            double duration = args[1].IsNil() ? 5 : Number(args[1]);
            if (duration is < 0 or > 300)
                throw new ScriptRuntimeException("Observer flight duration is outside its supported range.");
            return Change(() =>
            {
                _session.SelectObject(Index(), LuaObjectId(args[0]));
                _session.GoToSelection(Index(), duration, null, NativeSnapshot(Index()));
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
                _session.SelectObject(Index(), target);
                _session.GoToSelection(Index(), duration, range, NativeSnapshot(Index()));
            });
        });
        Bind(observer, "travelling", (_, _) => DynValue.NewBoolean(_session.IsFlying(Index())));
        Bind(observer, "getposition", (_, _) => Position(_session.ScriptObserverEye(Index(), NativeSnapshot(Index()))));
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
        Bind(item, "getchildren", (_, _) => Children(id));
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

    private WorldSnapshot NativeSnapshot(int? observer = null)
    {
        SimulationWorld world = _session.World;
        JulianInstant epoch = _session[observer ?? View].Epoch;
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

// SPDX-License-Identifier: GPL-2.0-or-later
using MoonSharp.Interpreter;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Application;

public sealed partial class LuaSessionScript
{
    private void BindTourActions(Table api)
    {
        Bind(api, "tryselect", (_, arguments) => TrySelectForTour(Text(arguments[0])));
        Bind(api, "cataloguetour", (context, _) => TourTargets(context.GetScript()));
        Bind(api, "gotoobject", (_, arguments) => GoTo(Number(arguments[0]), Number(arguments[1]), keepRange: false));
        Bind(api, "center", (_, arguments) => GoTo(Number(arguments[0]), 0, keepRange: true));
        Bind(api, "cancelflight", (_, _) => Change(() => _session.CancelFlight(View)));
        Bind(api, "follow", (_, _) => Change(() =>
        {
            if (_session[View].Navigation is not { Tracking: true } current || current.Target != _session[View].Selection)
                _session.ToggleTracking(View);
        }));
        Bind(api, "rate", (_, arguments) => Change(() => _session.SetPlaybackRate(View, Number(arguments[0]))));
        Bind(api, "turn", (_, arguments) => Change(() =>
            _session.AdjustCamera(View, Number(arguments[0]), Number(arguments[1]), 1)));
        Bind(api, "sky", (_, arguments) => SetTourSky(Text(arguments[0]), Boolean(arguments[1])));
    }

    private DynValue TrySelectForTour(string query)
    {
        ObjectId id;
        try
        {
            ObjectId? direct = query.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_') &&
                query.Length != 0 ? new ObjectId(query) : null;
            var catalogue = _session.World.Catalogue;
            id = direct is { } key && catalogue.Find(key) is not null ? key : SceneLink.ResolveObject(query, catalogue);
        }
        catch (InvalidDataException)
        {
            Log($"Tour target unavailable: {query}");
            return DynValue.False;
        }
        var snapshot = _session.World.Graph.Evaluate(_session[View].Epoch);
        if (!_session.World.TryNavigationState(id, snapshot, out _))
        {
            Log($"Tour target has no state at the current epoch: {query}");
            return DynValue.False;
        }
        _session.SelectObject(View, id);
        _changed = true;
        return DynValue.True;
    }

    private DynValue TourTargets(Script script)
    {
        const int limit = 32;
        var children = _session.World.Catalogue.Objects.Where(body => body.Parent.HasValue)
            .ToLookup(body => body.Parent!.Value);
        var table = new Table(script);
        var seen = new HashSet<ObjectId>();
        int count = 0;
        Visit(SolarSystemSample.SunId);
        return DynValue.NewTable(table);

        void Visit(ObjectId id)
        {
            if (count >= limit || !seen.Add(id))
                return;
            table.Set(++count, DynValue.NewString(id.Value));
            foreach (CatalogueObject body in children[id])
                Visit(body.Id);
        }
    }

    private DynValue GoTo(double duration, double multiple, bool keepRange)
    {
        if (duration is < 0 or > 300 || (!keepRange && multiple is <= 0 or > 1e8))
            throw new ScriptRuntimeException("Tour flight parameters are out of range.");
        if (_session[View].Selection is not { } target)
            return DynValue.Nil;
        double? range = keepRange ? _session[View].Navigation?.RangeKilometers :
            _session.World.TargetRadiusKilometers(target) * multiple;
        return Change(() => _session.GoToSelection(View, duration, range));
    }

    private DynValue SetTourSky(string layer, bool enabled)
    {
        SkyLayerSettings sky = _session[View].Sky;
        sky = layer switch
        {
            "stars" => sky with { Stars = enabled },
            "labels" => sky with { Labels = enabled },
            "constellations" => sky with { Constellations = enabled },
            "deep" => sky with { DeepSky = enabled },
            _ => throw new ScriptRuntimeException("Unknown tour sky layer.")
        };
        return Change(() => _session.SetSkyLayers(View, sky));
    }
}

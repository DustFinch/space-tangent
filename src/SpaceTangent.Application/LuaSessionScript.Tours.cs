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
        Bind(api, "legacybodylock", (_, _) => Change(() => _session.FollowBodyRotation(View)));
        Bind(api, "follow", (_, _) => Change(() =>
        {
            if (_session[View].Navigation is not { Tracking: true } current || current.Target != _session[View].Selection)
                _session.ToggleTracking(View);
        }));
        Bind(api, "rate", (_, arguments) => Change(() => _session.SetPlaybackRate(View, Number(arguments[0]))));
        Bind(api, "turn", (_, arguments) => Change(() =>
            _session.AdjustCamera(View, Number(arguments[0]), Number(arguments[1]), 1)));
        Bind(api, "sky", (_, arguments) => SetTourSky(Text(arguments[0]), Boolean(arguments[1])));
        Bind(api, "legacyfov", (_, arguments) =>
        {
            double degrees = Number(arguments[0]);
            return ObserverView.ValidFieldOfView(degrees)
                ? Change(() => _session.SetScriptFieldOfView(View, degrees, synchronizeZoom: false, synchronizeOtherViews: false))
                : DynValue.Nil;
        });
        Bind(api, "legacyclearmarks", (_, _) => Change(() => _session.ClearScriptMarkers(View)));
        Bind(api, "legacyframe", (_, args) => SetClassicFrame(Text(args[0]), args[1], args[2]));
        BindTourGeometry(api);
    }

    private DynValue SetClassicFrame(string frame, DynValue subject, DynValue secondary)
    {
        if (frame == "universal")
            return Change(() => _session.StartFreeFlight(View));
        if (subject.IsNil())
            return DynValue.Nil;

        ObjectId target = LuaObjectId(subject);
        bool applied = frame switch
        {
            "inertial" => _session.FollowScriptReference(View, target, NavigationFrame.Inertial),
            "equatorial" => _session.FollowScriptReference(View, target, NavigationFrame.Equatorial),
            "bodyfixed" => _session.FollowBodyRotation(View, target),
            "chase" => _session.FollowRelativeFrame(View, target, NavigationFrame.Chase, null),
            "lock" => _session.FollowRelativeFrame(View, target, NavigationFrame.PhaseLock,
                secondary.IsNil() ? null : LuaObjectId(secondary)),
            _ => throw new ScriptRuntimeException("Unknown classic observer frame.")
        };
        if (applied)
            _changed = true;
        else
            Log($"Observer frame {frame} is unavailable for {target} at this epoch.");
        return DynValue.Nil;
    }

    private DynValue TrySelectForTour(string query)
    {
        ObjectId id;
        try
        {
            ObjectId? direct = query.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_') &&
                query.Length != 0 ? new ObjectId(query) : null;
            var catalogue = _session.World.Catalogue;
            id = direct is { } key && catalogue.Find(key) is not null ? key : ResolveTourTarget(query, catalogue);
        }
        catch (InvalidDataException)
        {
            string name = query.Replace('/', ' ');
            var planet = name != query ? _session.World.Exoplanets.Bodies.FirstOrDefault(body =>
                body.Names.Contains(name, StringComparer.OrdinalIgnoreCase)) : null;
            if (planet is null)
            {
                Log($"Tour target unavailable: {query}");
                return DynValue.False;
            }
            id = planet.Id;
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

    private static ObjectId ResolveTourTarget(string query, ObjectCatalogue catalogue)
    {
        try { return SceneLink.ResolveObject(query, catalogue); }
        catch (InvalidDataException) when (query.Equals("Alpha Centauri", StringComparison.OrdinalIgnoreCase))
        {
            return SceneLink.ResolveObject("ALF Cen", catalogue);
        }
    }

    private DynValue TourTargets(Script script)
    {
        const int limit = 32;
        var children = _session.World.Catalogue.Objects.Where(body => body.Parent.HasValue && body.Kind != CatalogueKind.SurfaceLocation)
            .ToLookup(body => body.Parent!.Value);
        var table = new Table(script);
        var seen = new HashSet<ObjectId>();
        var secondary = new List<ObjectId>();
        int count = 0;
        Visit(SolarSystemSample.SunId);
        for (int i = 0; i < secondary.Count && count < limit; i++)
            Visit(secondary[i]);
        return DynValue.NewTable(table);

        void Visit(ObjectId id)
        {
            if (count >= limit || !seen.Add(id))
                return;
            table.Set(++count, DynValue.NewString(id.Value));
            foreach (CatalogueObject body in children[id])
            {
                if (body.Kind is CatalogueKind.Planet or CatalogueKind.Moon or CatalogueKind.DwarfPlanet)
                    Visit(body.Id);
                else
                    secondary.Add(body.Id);
            }
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
        if (layer == "markers")
            return Change(() => _session.SetScriptMarkersVisible(View, enabled));
        if (layer == "horizongrid")
        {
            var camera = _session[View].EarthObserver ?? EarthObserverCamera.Moscow;
            return Change(() => _session.SetEarthObserver(View, camera with { HorizonGrid = enabled }));
        }
        if (layer == "lighttime")
        {
            if (!_session.TrySetLightTimeDelay(View, enabled, out _))
                throw new ScriptRuntimeException("Light-time delay is unavailable for this selection or epoch.");
            _changed = true;
            return DynValue.Nil;
        }
        SkyLayerSettings sky = _session[View].Sky;
        sky = layer switch
        {
            "stars" => sky with { Stars = enabled },
            "labels" => sky with { Labels = enabled },
            "constellations" => sky with { Constellations = enabled },
            "deep" => sky with { DeepSky = enabled },
            "orbits" => sky with { HideOrbits = !enabled },
            "clouds" => sky with { HideClouds = !enabled },
            "atmospheres" => sky with { HideAtmospheres = !enabled },
            "comettails" => sky with { HideCometTails = !enabled },
            "boundaries" => sky with { ConstellationBoundaries = enabled },
            "grid" => sky with { EquatorialGrid = enabled },
            "eclipticgrid" => sky with { EclipticGrid = enabled },
            "galacticgrid" => sky with { GalacticGrid = enabled },
            "ecliptic" => sky with { EclipticLine = enabled },
            "minorlabels" => sky with { MinorBodyLabels = enabled },
            "places" => sky with { SurfacePlaces = enabled },
            "clusterlabels" => sky with { OpenClusterLabels = enabled },
            "nebulalabels" => sky with { NebulaLabels = enabled },
            _ => throw new ScriptRuntimeException("Unknown tour sky layer.")
        };
        return Change(() => _session.SetSkyLayers(View, sky));
    }
}

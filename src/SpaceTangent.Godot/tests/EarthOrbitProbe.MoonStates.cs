// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private static void CheckMoonStates(SimulationWorld world)
    {
        // Independent CSPICE J2000 position and velocity relative to each physical parent.
        (string Id, double Tdb, Vector3d Position, Vector3d Velocity)[] cases =
        [
            ("sol/phobos", 2451900.5, new(-7710.23337124194, 1117.12881907196, 5092.35645593824), new(-0.665460110799937, -1.94768996558558, -0.632581306246934)),
            ("sol/phobos", 2460600.5008, new(5482.95573230528, -5253.81991835828, -5527.2878424794), new(1.44493690672397, 1.57009015059316, -0.00444371833821189)),
            ("sol/phobos", 2469807.0, new(-8350.88566100789, -2521.28816432213, 3430.19837550518), new(0.128858031625377, -1.89155516157089, -0.988305843870215)),
            ("sol/deimos", 2451900.5, new(-18852.0088859307, 4467.17649880379, 13232.989298525), new(-0.510015826990447, -1.20977130625479, -0.318632645421465)),
            ("sol/deimos", 2460600.5008, new(11774.7900088919, -14944.8889133488, -13731.5949310714), new(1.03182332949903, 0.869751007074618, -0.0615881241025167)),
            ("sol/deimos", 2469807.0, new(-20312.4900721844, 404.027427361366, 11727.3625455754), new(-0.280191910115326, -1.24537238297858, -0.443125288699627)),
            ("sol/io", 2451900.5, new(422953.564840171, -14408.3766524025, 99.3733585612792), new(0.490021669882414, 15.5920448508024, 7.42841163392366)),
            ("sol/io", 2460600.5008, new(-253431.121497424, -301361.801571055, -147504.192257698), new(13.8455727199756, -9.58381515875123, -4.34373652879004)),
            ("sol/io", 2469807.0, new(-350325.486362988, -206849.269687729, -104555.165397343), new(9.61098764970882, -13.1575118417446, -6.12656158401654)),
            ("sol/europa", 2451900.5, new(-223692.13084946, -570126.807020634, -269466.324924528), new(13.0286110969507, -4.14026838509878, -1.74973845083364)),
            ("sol/europa", 2460600.5008, new(-578467.273335335, -312321.985779904, -161883.007604581), new(7.11257178876108, -10.5664436847088, -4.82628507122572)),
            ("sol/europa", 2469807.0, new(524219.329525068, 362856.607353823, 187606.491403122), new(-8.532591542867, 9.93268926765045, 4.58950860975127)),
            ("sol/ganymede", 2451900.5, new(-328878.494998376, 921739.816372924, 436768.705925665), new(-10.3401182962694, -2.96161876665138, -1.54680697949269)),
            ("sol/ganymede", 2460600.5008, new(-387994.735123547, 903036.792279902, 427000.393276641), new(-10.1320440032358, -3.4796075803068, -1.82868124272077)),
            ("sol/ganymede", 2469807.0, new(815800.831658224, 617402.129230058, 309469.482156155), new(-7.0308100555471, 7.56524441363658, 3.47044212464338)),
            ("sol/callisto", 2451900.5, new(-1879723.51134408, -213384.721370247, -128556.946576943), new(1.07820773619686, -7.30523893315713, -3.43770525474087)),
            ("sol/callisto", 2460600.5008, new(801556.355475409, -1541753.27760108, -714635.303447478), new(7.40961447495783, 3.17591989225034, 1.60644538167945)),
            ("sol/callisto", 2469807.0, new(-1867960.38122044, 291004.234379447, 109289.47306937), new(-1.3679277190908, -7.26839098344021, -3.43849718633339)),
            ("sol/titan", 2451900.5, new(-588400.021693238, -1065494.06179646, 123268.548534161), new(4.93010252249382, -2.56958722833249, -0.258053675890231)),
            ("sol/titan", 2460600.5008, new(-361417.913368959, 1183509.49129142, -47428.4136998298), new(-5.27504610177256, -1.44220524716462, 0.564829650839641)),
            ("sol/titan", 2469807.0, new(-685739.826032866, -1026019.17453293, 129264.161295032), new(4.61809842093845, -2.95438375667441, -0.219751959875638)),
        ];
        foreach (var (id, tdb, position, velocity) in cases)
        {
            var body = NaturalBodyCatalogue.Find(new(id))!;
            var snapshot = world.Graph.Evaluate(JulianInstant.FromJulianDate(tdb, TimeScale.Tdb));
            if (!snapshot.TryState(body.Id, out CartesianState target) ||
                !snapshot.TryState(body.Parent, out CartesianState parent))
                throw new InvalidOperationException($"Missing parent or target for {body.Name}.");
            CartesianState actual = target - parent;
            if ((actual.PositionKilometers - position).Length > .00002 ||
                (actual.VelocityKilometersPerSecond - velocity).Length > .00000005)
                throw new InvalidOperationException($"CSPICE disagrees for {body.Name} at JD {tdb}.");
        }
        var uncoveredEpoch = JulianInstant.FromJulianDate(2420000.5, TimeScale.Tdb);
        var uncovered = world.Graph.Evaluate(uncoveredEpoch);
        int checkedTheories = 0;
        foreach (var body in NaturalBodyCatalogue.Bodies.Where(body => body.Id != NaturalBodyCatalogue.PlutoId))
        {
            if (body.Theory is not { } theory)
            {
                if (uncovered.TryState(body.Id, out _))
                    throw new InvalidOperationException($"{body.Name} has a state outside its sampled coverage.");
                continue;
            }
            if (!uncovered.TryState(body.Id, out var state) || !uncovered.TryState(body.Parent, out var parent))
                throw new InvalidOperationException($"The historical orbit for {body.Name} is missing.");
            CartesianState expected = AnalyticSatelliteOrbit.Sample(theory, uncoveredEpoch);
            CartesianState actual = state - parent;
            if ((actual.PositionKilometers - expected.PositionKilometers).Length > .00001 ||
                (actual.VelocityKilometersPerSecond - expected.VelocityKilometersPerSecond).Length > 1e-9)
                throw new InvalidOperationException($"Wrong historical reference center for {body.Name}.");
            checkedTheories++;
        }
        if (checkedTheories != Enum.GetValues<SatelliteTheory>().Length)
            throw new InvalidOperationException("A major moon is missing from the catalogue.");
        global::Godot.GD.Print($"Natural satellites: {cases.Length} parent-relative CSPICE comparisons.");
    }
}

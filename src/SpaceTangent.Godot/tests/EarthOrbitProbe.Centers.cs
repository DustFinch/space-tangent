// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Contracts;

namespace SpaceTangent.Godot.Tests;

public partial class EarthOrbitProbe
{
    private static void CheckPlanetCenters(SimulationWorld world)
    {
        // NASA NAIF MAR099s, JUP365, SAT480, URA111 and NEP097.
        // CSPICE spkgeo, geometric J2000 states relative to each system barycenter, at TDB epochs.
        (int System, double Tdb, Vector3d Position, Vector3d Velocity)[] cases =
        [
            (4, 2451545.5, new(2.3072714772756e-05, -0.000176456233040939, -9.89878691386159e-05), new(3.42062391874117e-08, 1.35456719415426e-08, -1.20143267009874e-08)),
            (4, 2460600.5008, new(-0.000117172000439747, 0.000120498201144238, 0.000122299330376312), new(-2.62305930143204e-08, -2.79111900238327e-08, 2.25008017328529e-10)),
            (4, 2469807.0, new(0.000183800814077337, 4.08169825623252e-05, -8.30949471915365e-05), new(-1.50336486645262e-09, 3.40988436019764e-08, 1.735007057667e-08)),
            (5, 2451545.5, new(48.5254576665382, -28.7443679675095, -12.7177114070566), new(5.15976466956373e-05, 0.000729131349745425, 0.000351197828468512)),
            (5, 2460600.5008, new(11.403256495284, 38.9538116219444, 18.1988305461318), new(-0.000460142084967479, 0.000809430754479631, 0.000377975276391578)),
            (5, 2469807.0, new(45.40278559578, -64.1077535802624, -30.1650877800853), new(0.000389726890277241, 0.00018929518230723, 9.6174950907988e-05)),
            (6, 2451545.5, new(265.892924418148, -141.897867556353, -12.7750826121275), new(0.000631182800959062, 0.00110751777294291, -0.000127562268129591)),
            (6, 2460600.5008, new(84.3587683114105, -269.234803016206, 13.3039208386661), new(0.00123286880996014, 0.000329889864846868, -0.000130138367406212)),
            (6, 2469807.0, new(157.644231905251, 252.555981296814, -27.8327141458746), new(-0.00109158192670246, 0.000683497177743541, 5.32346982454867e-05)),
            (7, 2451545.5, new(29.7617036797364, -11.5925747112664, 17.8151318514737), new(0.000252073427695295, 1.89611236071958e-06, -0.000214442425013826)),
            (7, 2460600.5008, new(-3.901706334533, -4.50337953264065, 19.4126228554177), new(0.000154384439547086, -3.90190518070047e-05, 1.78144088930317e-05)),
            (7, 2469807.0, new(-35.0542144427899, 13.645513667671, -20.990224618211), new(-0.00023840552149307, -2.57607643633565e-05, 0.00028799300545448)),
            (8, 2451545.5, new(11.2695273349723, -23.045538829807, -69.434848525385), new(-0.000807627805854096, -0.000432195019094544, 1.3501934207506e-05)),
            (8, 2460600.5008, new(62.8666366026893, 32.4194196332668, -21.8338317458385), new(-6.87915318593631e-05, -0.000413892738437834, -0.000813852887240347)),
            (8, 2469807.0, new(-52.0940539344752, -16.8676142162972, 49.8599220375375), new(0.000366909854894507, 0.000605749508059092, 0.0005898267405656)),
        ];
        foreach (var (system, tdb, position, velocity) in cases)
        {
            var planet = PlanetCatalogue.Planets.Single(planet => planet.NaifId / 100 == system);
            var snapshot = world.Graph.Evaluate(JulianInstant.FromJulianDate(tdb, TimeScale.Tdb));
            if (!snapshot.TryState(planet.Id, out CartesianState body) ||
                !snapshot.TryState(planet.SystemId, out CartesianState barycenter) ||
                !snapshot.TryStateCenter(planet.Id, out ObjectId center) || center != planet.SystemId)
                throw new InvalidOperationException($"Missing distinct center or barycenter for {planet.Name}.");
            CartesianState actual = body - barycenter;
            // State subtraction at Neptune loses sub-millimeter precision in double.
            if ((actual.PositionKilometers - position).Length > .000005 ||
                (actual.VelocityKilometersPerSecond - velocity).Length > .00000001)
                throw new InvalidOperationException($"CSPICE center correction differs for {planet.Name} at JD {tdb}.");
        }
        var uncovered = world.Graph.Evaluate(JulianInstant.FromJulianDate(2420000.5, TimeScale.Tdb));
        foreach (var planet in PlanetCatalogue.Planets.Where(planet => planet.Id != planet.SystemId))
        {
            if (!uncovered.TryState(planet.Id, out CartesianState body) ||
                !uncovered.TryState(planet.SystemId, out CartesianState barycenter) || body != barycenter)
                throw new InvalidOperationException($"The uncovered epoch must fall back to the {planet.Name} system barycenter.");
        }
        global::Godot.GD.Print($"Planet centers: {cases.Length} CSPICE comparisons, plus five uncovered-epoch fallbacks.");
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SpaceTangent.Application;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

int count = args.Length == 0 ? 100000 : int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
if (count is < 1 or > 160000) throw new ArgumentOutOfRangeException(nameof(count), "Use 1–160000 stars.");
var text = new StringBuilder("id\tname\tdist_pc\tx_pc\ty_pc\tz_pc\tvx_pc_y\tvy_pc_y\tvz_pc_y\tv_mag\tb_v\tr_km\n");
for (int index = 1; index <= count; index++)
    text.Append(FormattableString.Invariant($"hip/{index}\tStar {index:000000}\t1\t1\t0\t0\t0\t0\t0\t1\t0\t1000\n"));
var catalogue = new SimulationWorld(new FixedEphemeris(), [], stellar: StellarCatalogue.Read(new StringReader(text.ToString()))).Catalogue;
var results = new List<object>();
foreach (string query in new[] { "star", FormattableString.Invariant($"Star {count - 1:000000}"), $"hip/{count}", "999", "no such target" })
foreach (bool bounded in new[] { false, true })
{
    int total = 0;
    IReadOnlyList<CatalogueObject> Search()
    {
        if (!bounded) { var all = catalogue.Search(query); total = all.Count; return all.Take(100).ToArray(); }
        var page = catalogue.SearchPage(query, 100); total = page.Total; return page.Items;
    }
    for (int index = 0; index < 10; index++) Search();
    var samples = new double[60];
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    for (int index = 0; index < samples.Length; index++)
    {
        long start = Stopwatch.GetTimestamp(); Search(); samples[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    allocated = (GC.GetAllocatedBytesForCurrentThread() - allocated) / samples.Length;
    Array.Sort(samples);
    results.Add(new { Query = query, Bounded = bounded, Total = total, MedianMs = samples[30], P95Ms = samples[56], MaxMs = samples[^1], AllocatedBytes = allocated });
}
Console.WriteLine(JsonSerializer.Serialize(new { Runtime = Environment.Version.ToString(), Stars = count, Results = results }, new JsonSerializerOptions { WriteIndented = true }));

sealed class FixedEphemeris : ISolarSystemEphemeris
{
    public JulianInstant Start => new(1, 0, TimeScale.Tdb);
    public JulianInstant End => new(4000000, 0, TimeScale.Tdb);
    public SolarSystemSample Sample(JulianInstant epoch) => new(epoch,
        new(new(1e8, 4e7, 2e7), default), default, new(new(384400, 0, 0), default), default);
}

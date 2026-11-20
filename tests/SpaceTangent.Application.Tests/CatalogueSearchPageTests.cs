// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class CatalogueSearchPageTests
{
    [Fact]
    public void BoundedResultsKeepRanksAliasesFilteringAndCountsInALargeCatalogue()
    {
        var text = new StringBuilder("id\tname\tdist_pc\tx_pc\ty_pc\tz_pc\tvx_pc_y\tvy_pc_y\tvz_pc_y\tv_mag\tb_v\tr_km\n");
        for (int index = 1; index <= 100000; index++)
            text.Append(FormattableString.Invariant($"hip/{index}\tStar {index:000000}\t1\t1\t0\t0\t0\t0\t0\t1\t0\t1000\n"));
        var catalogue = new SimulationWorld(new FixedEphemeris(), [], stellar: StellarCatalogue.Read(new StringReader(text.ToString()))).Catalogue;
        Assert.True(catalogue.Objects.Count > 100000);
        foreach (string query in new[] { "", "star", "Star 099999", "HIP/100000", "999", "no such target",
            catalogue.Objects.First(body => body.Aliases.Count != 0).Aliases[0] })
        foreach (Func<CatalogueObject, bool>? include in new Func<CatalogueObject, bool>?[] { null,
            item => item.Kind == CatalogueKind.Star, item => item.Kind != CatalogueKind.Star })
        {
            var all = catalogue.Search(query).Where(item => include?.Invoke(item) != false).ToArray();
            foreach (int limit in new[] { 0, 1, 7, 100 })
            {
                var page = catalogue.SearchPage(query, limit, include);
                Assert.Equal(all.Length, page.Total);
                Assert.Equal(all.Take(limit), page.Items);
            }
        }
        Assert.Equal(new ObjectId("hip/99999"), Assert.Single(catalogue.SearchPage("Star 099999", 1).Items).Id);
        Assert.Equal(100000, catalogue.SearchPage("star", 100).Total);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalogue.SearchPage("", -1));
    }

    private sealed class FixedEphemeris : ISolarSystemEphemeris
    {
        public JulianInstant Start => new(1, 0, TimeScale.Tdb);
        public JulianInstant End => new(4000000, 0, TimeScale.Tdb);
        public SolarSystemSample Sample(JulianInstant epoch) => new(epoch,
            new(new(1e8, 4e7, 2e7), default), default, new(new(384400, 0, 0), default), default);
    }
}

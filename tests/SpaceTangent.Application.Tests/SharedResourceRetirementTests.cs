// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Addons;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class SharedResourceRetirementTests
{
    private sealed class Resource : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }

    [Fact]
    public void RetirementWaitsForAllCapturedWorkersAndRejectsNewUses()
    {
        var owner = new SharedResourceRetirement<Resource>(); var resource = new Resource();
        var load = owner.Borrow(resource)!; var chart = owner.Borrow(resource)!;
        owner.Retire(resource); owner.Retire(resource);
        Assert.Equal(0, resource.Disposals);
        Assert.Throws<ObjectDisposedException>(() => owner.Borrow(resource));
        load.Dispose(); load.Dispose(); Assert.Equal(0, resource.Disposals);
        chart.Dispose(); chart.Dispose(); owner.Retire(resource);
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public void ReleasingBorrowedUnchangedGenerationDoesNotRetireItsResource()
    {
        var owner = new SharedResourceRetirement<Resource>(); var resource = new Resource();
        owner.Borrow(resource)!.Dispose(); Assert.Equal(0, resource.Disposals);
        using (owner.Borrow(resource)) Assert.Equal(0, resource.Disposals);
        owner.Retire(resource); Assert.Equal(1, resource.Disposals);
        Assert.Null(owner.Borrow(null)); owner.Retire(null);
    }

    [Fact]
    public async Task ConcurrentCompletionAndRetirementDisposeExactlyOnce()
    {
        var owner = new SharedResourceRetirement<Resource>(); var resource = new Resource();
        var uses = Enumerable.Range(0, 32).Select(_ => owner.Borrow(resource)!).ToArray();
        await Task.WhenAll(uses.Select(use => Task.Run(() => { use.Dispose(); use.Dispose(); }))
            .Append(Task.Run(() => { owner.Retire(resource); owner.Retire(resource); })));
        Assert.Equal(1, resource.Disposals);
    }
}

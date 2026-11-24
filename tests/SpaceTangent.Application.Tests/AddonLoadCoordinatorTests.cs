// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application.Addons;
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class AddonLoadCoordinatorTests
{
    [Fact]
    public async Task AnOlderCompletionCannotPublishAfterANewerRequest()
    {
        using var loads = new AddonLoadCoordinator();
        var olderToken = loads.Begin(); int olderGeneration = loads.Generation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> FinishOlder() { await completion.Task; return loads.IsCurrent(olderGeneration, olderToken); }
        var older = FinishOlder();
        var latestToken = loads.Begin();
        completion.SetResult();
        Assert.False(await older); Assert.True(olderToken.IsCancellationRequested);
        Assert.True(loads.IsCurrent(loads.Generation, latestToken));
    }

    [Fact]
    public void ClosingAnOwnerInvalidatesPendingPublicationAndIsIdempotent()
    {
        var loads = new AddonLoadCoordinator(); var token = loads.Begin(); int generation = loads.Generation;
        loads.Dispose(); int closed = loads.Generation; loads.Dispose();
        Assert.Equal(closed, loads.Generation); Assert.True(token.IsCancellationRequested);
        Assert.False(loads.IsCurrent(generation, token));
        Assert.False(loads.IsCurrent(closed, default));
        Assert.Throws<ObjectDisposedException>(() => loads.Begin());
    }
}

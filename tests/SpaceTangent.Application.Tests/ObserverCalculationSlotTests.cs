// SPDX-License-Identifier: GPL-2.0-or-later
using Xunit;

namespace SpaceTangent.Application.Tests;

public sealed class ObserverCalculationSlotTests
{
    [Fact]
    public void CompletingSupersededCalculationDoesNotDetachNewWork()
    {
        using var slot = new ObserverCalculationSlot();
        var old = slot.Begin(); var token = old.Token;
        using var next = slot.Begin();
        Assert.True(token.IsCancellationRequested); Assert.False(next.IsCancellationRequested);
        old.Dispose(); old.Dispose(); slot.Cancel(); Assert.True(next.IsCancellationRequested);
    }

    [Fact]
    public void ClosingCancelsButKeepsTheTokenUsableUntilWorkerCompletion()
    {
        var slot = new ObserverCalculationSlot(); using var work = slot.Begin();
        slot.Dispose(); slot.Dispose();
        Assert.True(work.Token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => slot.Begin());
    }

    [Fact]
    public void CompletingCurrentWorkAllowsCancellationAndANewRequest()
    {
        using var slot = new ObserverCalculationSlot(); slot.Begin().Dispose(); slot.Cancel();
        using var next = slot.Begin(); Assert.False(next.IsCancellationRequested);
        slot.Cancel(); Assert.True(next.IsCancellationRequested);
    }
}

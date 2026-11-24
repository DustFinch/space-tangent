// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Application.Addons;

/// <summary>
/// Owns latest-request-wins cancellation for an application lifetime. Begin,
/// publication checks and disposal run on the owner thread; workers receive only tokens.
/// Engine lifetime validation remains the adapter's responsibility.
/// </summary>
public sealed class AddonLoadCoordinator : IDisposable
{
    private CancellationTokenSource? _cancellation;
    private bool _closed;

    public int Generation { get; private set; }

    /// <exception cref="ObjectDisposedException">This owner has permanently closed; callers must not start new work.</exception>
    public CancellationToken Begin()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        Generation++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new();
        return _cancellation.Token;
    }

    public bool IsCurrent(int generation, CancellationToken cancellation) =>
        !_closed && !cancellation.IsCancellationRequested && generation == Generation;

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Generation++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
namespace SpaceTangent.Application;

/// <summary>Owns supersession and shutdown of one observer calculation. Begin,
/// Dispose and scope completion run on the owner thread; workers use only Token.</summary>
public sealed class ObserverCalculationSlot : IDisposable
{
    private Calculation? _current;
    private bool _closed;
    /// <exception cref="ObjectDisposedException">This owner has permanently closed; late UI callbacks must return before starting work.</exception>
    public Calculation Begin()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _current?.Cancel();
        return _current = new(this);
    }
    public void Cancel() => _current?.Cancel();
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Cancel();
    }

    public sealed class Calculation : IDisposable
    {
        private ObserverCalculationSlot? _owner;
        private readonly CancellationTokenSource _cancellation = new();
        internal Calculation(ObserverCalculationSlot owner) => _owner = owner;
        public CancellationToken Token => _cancellation.Token;
        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;
        internal void Cancel() => _cancellation.Cancel();
        public void Dispose()
        {
            if (_owner is not { } owner) return;
            _owner = null;
            if (ReferenceEquals(owner._current, this)) owner._current = null;
            _cancellation.Dispose();
        }
    }
}

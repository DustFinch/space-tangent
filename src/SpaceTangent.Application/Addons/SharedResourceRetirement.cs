// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;

namespace SpaceTangent.Application.Addons;

/// <summary>Defers retirement until captured workers release their uses. Keys are
/// weak: the retirement ledger does not keep a disposed catalogue alive.</summary>
public sealed class SharedResourceRetirement<T> where T : class, IDisposable
{
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<T, Entry> _entries = new();
    private sealed class Entry { internal int Uses; internal bool Retired, Disposed; }

    /// <summary>Capture before starting work; retirement rejects new users.</summary>
    public IDisposable? Borrow(T? resource)
    {
        if (resource is null) return null;
        lock (_gate)
        {
            var entry = _entries.GetOrCreateValue(resource);
            ObjectDisposedException.ThrowIf(entry.Retired, resource);
            entry.Uses++;
            return new Use(this, resource, entry);
        }
    }

    /// <summary>Call after live consumers have switched. Repeated calls are safe.</summary>
    public void Retire(T? resource)
    {
        if (resource is null) return;
        bool dispose;
        lock (_gate)
        {
            var entry = _entries.GetOrCreateValue(resource);
            entry.Retired = true;
            dispose = ClaimDisposal(entry);
        }
        if (dispose) resource.Dispose();
    }

    private void Release(T resource, Entry entry)
    {
        bool dispose;
        lock (_gate) { entry.Uses--; dispose = ClaimDisposal(entry); }
        if (dispose) resource.Dispose();
    }

    private static bool ClaimDisposal(Entry entry)
    {
        if (!entry.Retired || entry.Disposed || entry.Uses != 0) return false;
        entry.Disposed = true;
        return true;
    }

    private sealed class Use(SharedResourceRetirement<T> owner, T resource, Entry entry) : IDisposable
    {
        private SharedResourceRetirement<T>? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(resource, entry);
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.ObjectModel;
using SpaceTangent.Contracts;
using SpaceTangent.Core.Ephemerides;

namespace SpaceTangent.Application;

/// <summary>Immutable per-body observing places. Persistence identity does not change the physical view state.</summary>
public readonly struct ObserverPlaceMemory : IEquatable<ObserverPlaceMemory>
{
    public const int MaximumLocations = 16;
    private readonly ReadOnlyCollection<EarthObserverCamera>? _locations;
    public Guid ViewKey { get; }
    public IReadOnlyList<EarthObserverCamera> Locations => _locations ?? (IReadOnlyList<EarthObserverCamera>)Array.Empty<EarthObserverCamera>();
    public bool HasIdentity => ViewKey != Guid.Empty;

    private ObserverPlaceMemory(Guid key, EarthObserverCamera[] locations)
    { ViewKey = key; _locations = Array.AsReadOnly(locations); }

    public static ObserverPlaceMemory FromSaved(Guid key, IReadOnlyList<EarthObserverCamera> locations)
    {
        if (locations is null || key == Guid.Empty || locations.Count > MaximumLocations || locations.Any(camera => !camera.IsValid()) ||
            locations.Select(camera => camera.SurfaceBody).Distinct().Count() != locations.Count)
            throw new InvalidDataException("The observing place memory has an invalid identity, location or size.");
        return new(key, locations.ToArray());
    }

    public ObserverPlaceMemory EnsureIdentity() => HasIdentity ? this : new(Guid.NewGuid(), Locations.ToArray());
    public ObserverPlaceMemory ForkIdentity() => new(Guid.NewGuid(), Locations.ToArray());
    public EarthObserverCamera? Find(ObjectId body)
    {
        foreach (var camera in Locations)
            if (camera.SurfaceBody == body) return camera;
        return null;
    }

    public ObserverPlaceMemory Remember(EarthObserverCamera camera)
    {
        if (!camera.IsValid()) throw new ArgumentException("The observing place is invalid.", nameof(camera));
        var memory = EnsureIdentity();
        if (memory.Locations.Count > 0 && memory.Locations[^1] == camera) return memory;
        var locations = memory.Locations.Where(old => old.SurfaceBody != camera.SurfaceBody).ToList();
        locations.Add(camera);
        if (locations.Count > MaximumLocations)
            locations.RemoveAt(locations.FindIndex(old => old.SurfaceBody != SolarSystemSample.EarthId));
        return new(memory.ViewKey, locations.ToArray());
    }

    public bool IsValid() => Locations.Count <= MaximumLocations && (HasIdentity || Locations.Count == 0) &&
        Locations.All(camera => camera.IsValid()) && Locations.Select(camera => camera.SurfaceBody).Distinct().Count() == Locations.Count;
    public bool Equals(ObserverPlaceMemory other) => Locations.SequenceEqual(other.Locations);
    public override bool Equals(object? other) => other is ObserverPlaceMemory memory && Equals(memory);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var camera in Locations) hash.Add(camera);
        return hash.ToHashCode();
    }
    public static bool operator ==(ObserverPlaceMemory left, ObserverPlaceMemory right) => left.Equals(right);
    public static bool operator !=(ObserverPlaceMemory left, ObserverPlaceMemory right) => !left.Equals(right);
}

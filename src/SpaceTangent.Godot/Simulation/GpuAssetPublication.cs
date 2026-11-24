// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.CompilerServices;
using Godot;

namespace SpaceTangent.Godot.Simulation;

// Main-thread ownership for resources created by add-on publication. Groups are
// per asset, so reusing one unchanged mesh does not retain an old whole generation.
internal sealed class GpuAssetPublication : IDisposable
{
    private static readonly ConditionalWeakTable<Resource, AssetGroup> Groups = new();
    private readonly List<AssetGroup> _created = [];
    private bool _committed;
    private bool _closed;

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_committed) throw new InvalidOperationException("Publication already committed.");
    }
    internal AssetScope BeginAsset() { EnsureOpen(); return new(this); }

    internal PublishedRenderAssets Commit(IEnumerable<Resource?> roots)
    {
        EnsureOpen();
        var retained = new HashSet<AssetGroup>(_created);
        var borrowed = new List<AssetGroup>();
        try
        {
            foreach (Resource? root in roots)
                if (root is not null && Groups.TryGetValue(root, out var group) && retained.Add(group))
                {
                    group.Retain();
                    borrowed.Add(group);
                }
            var published = new PublishedRenderAssets(retained.ToArray());
            _committed = true;
            _created.Clear();
            return published;
        }
        catch
        {
            foreach (var group in borrowed) group.Release();
            throw;
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        for (int index = _created.Count - 1; index >= 0; index--) _created[index].Release();
        _created.Clear();
    }

    internal sealed class AssetScope(GpuAssetPublication? publication) : IDisposable
    {
        private readonly List<Resource> _owned = [];
        private bool _completed;

        internal void Own(Resource resource)
        {
            if (_completed) throw new InvalidOperationException("Asset already completed.");
            // A tiled array may repeat a pool to fill its fixed shader slots.
            if (!_owned.Contains(resource, ReferenceEqualityComparer.Instance)) _owned.Add(resource);
        }

        internal T Complete<T>(T root, params Resource?[] dependencies) where T : Resource
        {
            if (_completed) throw new InvalidOperationException("Asset already completed.");
            if (publication is not null)
            {
                publication.EnsureOpen();
                if (!_owned.Contains(root, ReferenceEqualityComparer.Instance) || Groups.TryGetValue(root, out _))
                    throw new InvalidOperationException("An asset root must be a newly owned resource.");
                var parents = dependencies.OfType<Resource>().Select(resource =>
                    Groups.TryGetValue(resource, out var group) ? group : null).OfType<AssetGroup>().Distinct().ToArray();
                var group = new AssetGroup(_owned.ToArray(), parents);
                Groups.Add(root, group);
                publication._created.Add(group);
            }
            _completed = true;
            _owned.Clear();
            return root;
        }

        public void Dispose()
        {
            for (int index = _owned.Count - 1; index >= 0; index--) _owned[index].Dispose();
            _owned.Clear();
        }
    }

    internal sealed class AssetGroup
    {
        private Resource[] _owned;
        private AssetGroup[] _dependencies;
        private int _references = 1;
        internal AssetGroup(Resource[] owned, AssetGroup[] dependencies)
        {
            _owned = owned; _dependencies = dependencies;
            int retained = 0;
            try
            {
                foreach (var group in dependencies) { group.Retain(); retained++; }
            }
            catch
            {
                for (int index = retained - 1; index >= 0; index--) dependencies[index].Release();
                throw;
            }
        }
        internal void Retain()
        {
            ObjectDisposedException.ThrowIf(_references == 0, this);
            _references++;
        }
        internal void Release()
        {
            if (_references <= 0) throw new InvalidOperationException("Asset ownership was released twice.");
            if (--_references != 0) return;
            for (int index = _owned.Length - 1; index >= 0; index--) _owned[index].Dispose();
            _owned = [];
            foreach (var group in _dependencies) group.Release();
            _dependencies = [];
        }
    }
}

internal sealed class PublishedRenderAssets(GpuAssetPublication.AssetGroup[] groups) : IDisposable
{
    private GpuAssetPublication.AssetGroup[] _groups = groups;
    public void Dispose()
    {
        var owned = _groups;
        _groups = [];
        for (int index = owned.Length - 1; index >= 0; index--) owned[index].Release();
    }
}

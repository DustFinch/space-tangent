// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class GpuAssetOwnershipProbe : Node
{
    public override async void _Ready()
    {
        int exit = 1;
        try
        {
            AbandonedCandidate();
            SharedDependency();
            AlternatingGenerations();
            FailedDependencyCapture();
            await NativeNodeKeepsResourceAlive();
            GD.Print("SpaceTangent GPU asset ownership probe passed");
            exit = 0;
        }
        catch (Exception error) { GD.PushError(error.ToString()); }
        ProbeHost.Complete(this, exit);
    }

    private static void AbandonedCandidate()
    {
        var mesh = new ArrayMesh(); var material = new StandardMaterial3D();
        ulong meshId = mesh.GetInstanceId(), materialId = material.GetInstanceId();
        using (var publication = new GpuAssetPublication())
        using (var asset = publication.BeginAsset())
        {
            asset.Own(mesh); asset.Own(material);
            // An exception before Complete leaves only this scope's new resources.
        }
        RequireReleased(mesh, meshId, "Partial mesh cleanup.");
        RequireReleased(material, materialId, "Partial material cleanup.");
        var candidate = new ArrayMesh();
        ulong candidateId = candidate.GetInstanceId();
        using (var publication = new GpuAssetPublication())
        using (var asset = publication.BeginAsset()) { asset.Own(candidate); asset.Complete(candidate); }
        RequireReleased(candidate, candidateId, "Rejected completed candidate cleanup.");
    }

    private static void SharedDependency()
    {
        var texture = new ImageTexture(); var first = new ArrayMesh();
        ulong textureId = texture.GetInstanceId(), firstId = first.GetInstanceId();
        PublishedRenderAssets old;
        using (var publication = new GpuAssetPublication())
        {
            using (var asset = publication.BeginAsset()) { asset.Own(texture); asset.Complete(texture); }
            using (var asset = publication.BeginAsset()) { asset.Own(first); asset.Complete(first, texture); }
            old = publication.Commit([first]);
            RequireThrows<InvalidOperationException>(() => publication.BeginAsset());
        }
        var replacement = new ArrayMesh(); PublishedRenderAssets next;
        ulong replacementId = replacement.GetInstanceId();
        using (var publication = new GpuAssetPublication())
        {
            using (var asset = publication.BeginAsset()) { asset.Own(replacement); asset.Complete(replacement, texture); }
            next = publication.Commit([replacement]);
        }
        old.Dispose(); old.Dispose();
        RequireReleased(first, firstId, "Retired mesh cleanup.");
        Require(GodotObject.IsInstanceValid(texture) && GodotObject.IsInstanceIdValid(textureId) && GodotObject.IsInstanceValid(replacement), "Shared texture retained by new asset.");
        next.Dispose(); next.Dispose();
        RequireReleased(texture, textureId, "Final dependency texture cleanup.");
        RequireReleased(replacement, replacementId, "Final dependent mesh cleanup.");
        var closed = new GpuAssetPublication(); closed.Dispose(); closed.Dispose();
        RequireThrows<ObjectDisposedException>(() => closed.BeginAsset());
    }

    private static void AlternatingGenerations()
    {
        ArrayMesh a, b; PublishedRenderAssets active;
        using (var publication = new GpuAssetPublication())
        {
            a = NewMesh(publication); b = NewMesh(publication);
            active = publication.Commit([a, b, a]);
        }
        for (int index = 0; index < 32; index++)
        {
            var replaced = index % 2 == 0 ? a : b;
            ulong replacedId = replaced.GetInstanceId();
            using var publication = new GpuAssetPublication();
            var fresh = NewMesh(publication);
            if (index % 2 == 0) a = fresh; else b = fresh;
            var next = publication.Commit([a, b]);
            active.Dispose(); active = next;
            RequireReleased(replaced, replacedId, "Alternating updates must not retain native generations.");
            Require(GodotObject.IsInstanceValid(a) && GodotObject.IsInstanceValid(b), "The unchanged shared asset must survive.");
        }
        ulong aId = a.GetInstanceId(), bId = b.GetInstanceId();
        active.Dispose();
        RequireReleased(a, aId, "First native asset shutdown.");
        RequireReleased(b, bId, "Second native asset shutdown.");

        static ArrayMesh NewMesh(GpuAssetPublication publication)
        {
            using var asset = publication.BeginAsset();
            var mesh = new ArrayMesh(); asset.Own(mesh); return asset.Complete(mesh);
        }
    }

    private static void FailedDependencyCapture()
    {
        var alive = new ImageTexture(); var retired = new ImageTexture();
        ulong aliveId = alive.GetInstanceId();
        PublishedRenderAssets held, gone;
        using (var publication = new GpuAssetPublication())
        {
            using var asset = publication.BeginAsset(); asset.Own(alive); asset.Complete(alive);
            held = publication.Commit([alive]);
        }
        using (var publication = new GpuAssetPublication())
        {
            using var asset = publication.BeginAsset(); asset.Own(retired); asset.Complete(retired);
            gone = publication.Commit([retired]);
        }
        gone.Dispose();
        using (var publication = new GpuAssetPublication())
            RequireThrows<ObjectDisposedException>(() => publication.Commit([alive, retired]));
        using (var publication = new GpuAssetPublication())
        using (var asset = publication.BeginAsset())
        {
            var mesh = new ArrayMesh(); asset.Own(mesh);
            RequireThrows<ObjectDisposedException>(() => asset.Complete(mesh, alive, retired));
        }
        held.Dispose();
        RequireReleased(alive, aliveId, "Failed dependency capture retained a retired texture.");
    }

    private async Task NativeNodeKeepsResourceAlive()
    {
        var mesh = new ArrayMesh();
        ulong id = mesh.GetInstanceId();
        var instance = new MeshInstance3D { Mesh = mesh };
        try
        {
            mesh.Dispose();
            Require(!GodotObject.IsInstanceValid(mesh) && GodotObject.IsInstanceIdValid(id),
                "A disposed wrapper is not proof of native deletion while a node owns the mesh.");
            instance.Mesh = null;
            instance.Free();
            // Godot may create a managed binding while the node still owns the
            // resource. Drain those bindings explicitly, outside timing tests.
            for (int pass = 0; pass < 4 && GodotObject.IsInstanceIdValid(id); pass++)
            {
                GC.Collect(); GC.WaitForPendingFinalizers();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            Require(!GodotObject.IsInstanceIdValid(id), "The last native owner must release the mesh.");
        }
        finally { if (GodotObject.IsInstanceValid(instance)) instance.Free(); mesh.Dispose(); }
    }

    // Native ObjectDB liveness is checked independently of C# wrapper state.
    // Neither this nor a freed RID proves when a driver returns reserved VRAM.
    private static void RequireReleased(GodotObject wrapper, ulong id, string message) =>
        Require(!GodotObject.IsInstanceValid(wrapper) && !GodotObject.IsInstanceIdValid(id), message);

    private static void RequireThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Require(bool condition, string message) => ProbeHost.Require(condition, message);
}

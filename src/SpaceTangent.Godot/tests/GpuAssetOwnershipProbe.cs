// SPDX-License-Identifier: GPL-2.0-or-later
using Godot;
using SpaceTangent.Godot.Simulation;

namespace SpaceTangent.Godot.Tests;

public partial class GpuAssetOwnershipProbe : Node
{
    public override void _Ready()
    {
        int exit = 1;
        try
        {
            AbandonedCandidate();
            SharedDependency();
            AlternatingGenerations();
            FailedDependencyCapture();
            GD.Print("SpaceTangent GPU asset ownership probe passed");
            exit = 0;
        }
        catch (Exception error) { GD.PushError(error.ToString()); }
        GetTree().Quit(CoverageCapture.Complete(exit));
    }

    private static void AbandonedCandidate()
    {
        var mesh = new ArrayMesh(); var material = new StandardMaterial3D();
        using (var publication = new GpuAssetPublication())
        using (var asset = publication.BeginAsset())
        {
            asset.Own(mesh); asset.Own(material);
            // An exception before Complete leaves only this scope's new resources.
        }
        Require(!GodotObject.IsInstanceValid(mesh) && !GodotObject.IsInstanceValid(material), "Partial asset cleanup.");
        var candidate = new ArrayMesh();
        using (var publication = new GpuAssetPublication())
        using (var asset = publication.BeginAsset()) { asset.Own(candidate); asset.Complete(candidate); }
        Require(!GodotObject.IsInstanceValid(candidate), "Rejected completed candidate cleanup.");
    }

    private static void SharedDependency()
    {
        var texture = new ImageTexture(); var first = new ArrayMesh();
        PublishedRenderAssets old;
        using (var publication = new GpuAssetPublication())
        {
            using (var asset = publication.BeginAsset()) { asset.Own(texture); asset.Complete(texture); }
            using (var asset = publication.BeginAsset()) { asset.Own(first); asset.Complete(first, texture); }
            old = publication.Commit([first]);
            RequireThrows<InvalidOperationException>(() => publication.BeginAsset());
        }
        var replacement = new ArrayMesh(); PublishedRenderAssets next;
        using (var publication = new GpuAssetPublication())
        {
            using (var asset = publication.BeginAsset()) { asset.Own(replacement); asset.Complete(replacement, texture); }
            next = publication.Commit([replacement]);
        }
        old.Dispose(); old.Dispose();
        Require(!GodotObject.IsInstanceValid(first), "Retired mesh cleanup.");
        Require(GodotObject.IsInstanceValid(texture) && GodotObject.IsInstanceValid(replacement), "Shared texture retained by new asset.");
        next.Dispose(); next.Dispose();
        Require(!GodotObject.IsInstanceValid(texture) && !GodotObject.IsInstanceValid(replacement), "Final dependency cleanup.");
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
            using var publication = new GpuAssetPublication();
            var fresh = NewMesh(publication);
            if (index % 2 == 0) a = fresh; else b = fresh;
            var next = publication.Commit([a, b]);
            active.Dispose(); active = next;
            Require(!GodotObject.IsInstanceValid(replaced), "Alternating updates must not retain a chain of old generations.");
            Require(GodotObject.IsInstanceValid(a) && GodotObject.IsInstanceValid(b), "The unchanged shared asset must survive.");
        }
        active.Dispose();
        Require(!GodotObject.IsInstanceValid(a) && !GodotObject.IsInstanceValid(b), "Generation shutdown.");

        static ArrayMesh NewMesh(GpuAssetPublication publication)
        {
            using var asset = publication.BeginAsset();
            var mesh = new ArrayMesh(); asset.Own(mesh); return asset.Complete(mesh);
        }
    }

    private static void FailedDependencyCapture()
    {
        var alive = new ImageTexture(); var retired = new ImageTexture();
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
        Require(!GodotObject.IsInstanceValid(alive), "Failed dependency capture retained a retired texture.");
    }

    private static void RequireThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Application;
using SpaceTangent.Core.Time;
using SpaceTangent.Core.World;

namespace SpaceTangent.Godot.Simulation;

// Captured presentation inputs; components consume the already evaluated world.
internal readonly record struct ObserverRenderFrame(ObserverView Observer, WorldSnapshot Snapshot, DeltaTModel DeltaT);

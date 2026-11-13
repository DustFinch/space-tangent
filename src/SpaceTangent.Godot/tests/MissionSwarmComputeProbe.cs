// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using SpaceTangent.Core.Addons;

namespace SpaceTangent.Godot.Tests;

/// <summary>Runs the orbit and discovery code shared by the point renderer against original WGL captures.</summary>
public partial class MissionSwarmComputeProbe : Node
{
    public override void _Ready()
    {
        try
        {
            Compare();
            GetTree().Quit();
        }
        catch (Exception error)
        {
            GD.PushError(error.ToString());
            GetTree().Quit(1);
        }
    }

    private static void Compare()
    {
        string inputs = OS.GetEnvironment("SPACETANGENT_SWARM_CORPUS");
        string oracle = OS.GetEnvironment("SPACETANGENT_SWARM_WGL");
        using var metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(oracle, "native-opengl.json")));
        byte[] captures = File.ReadAllBytes(Path.Combine(oracle, "native-opengl.bin"));
        if (Convert.ToHexString(SHA256.HashData(captures)) !=
            "D05029A55A3218DDB93FF221D436E863C14243E840B1844A54A48E777C84420F")
            throw new InvalidDataException("The original WGL capture has changed.");

        string functions = global::Godot.FileAccess.GetFileAsString("res://Shaders/mission_swarm.gdshaderinc");
        string code = """
            #version 450
            layout(local_size_x = 64) in;
            layout(set = 0, binding = 0, std430) readonly buffer Input { float data[]; };
            layout(set = 0, binding = 1, std430) writeonly buffer Output { vec4 result[]; };
            layout(push_constant, std430) uniform Params { float clock; uint count; } params;
            """ + "\n" + functions + "\n" + """
            void main() {
                uint i = gl_GlobalInvocationID.x;
                if (i >= params.count) return;
                uint at = 9 * i;
                vec4 orbit = vec4(data[at], data[at+1], data[at+2], data[at+3]);
                vec4 q = vec4(data[at+4], data[at+5], data[at+6], data[at+7]);
                result[2*i] = vec4(swarm_position(orbit, q, params.clock), 1.0);
                result[2*i+1] = swarm_appearance(params.clock - data[at+8], vec4(1.0, 0.2, 0.1, 0.6));
            }
            """;
        using var device = RenderingServer.CreateLocalRenderingDevice()
            ?? throw new InvalidOperationException("A local Vulkan rendering device is required.");
        using var source = new RDShaderSource { SourceCompute = code };
        using var spirv = device.ShaderCompileSpirVFromSource(source);
        if (spirv.CompileErrorCompute.Length != 0)
            throw new InvalidOperationException(spirv.CompileErrorCompute);
        Rid shader = device.ShaderCreateFromSpirV(spirv);
        Rid pipeline = device.ComputePipelineCreate(shader);
        int compared = 0;
        double worstPosition = 0, worstColor = 0;
        try
        {
            foreach (var catalogue in metadata.RootElement.GetProperty("catalogues").EnumerateArray())
            {
                string filename = catalogue.GetProperty("file").GetString()!;
                var format = filename.Contains("astorb", StringComparison.Ordinal) ? MissionSwarmFormat.Astorb :
                    filename.EndsWith(".kep", StringComparison.Ordinal) ? MissionSwarmFormat.KepBin : MissionSwarmFormat.Binary;
                using var file = File.OpenRead(Path.Combine(inputs, filename));
                var swarm = MissionSwarm.Read(file, format);
                int count = swarm.Count;
                if (count != catalogue.GetProperty("count").GetInt32())
                    throw new InvalidDataException($"The object count changed in {filename}.");
                byte[] attributes = MemoryMarshal.AsBytes(swarm.Attributes).ToArray();
                Rid input = device.StorageBufferCreate((uint)attributes.Length, attributes);
                Rid output = device.StorageBufferCreate((uint)(count * 32));
                using var first = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
                using var second = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
                first.AddId(input);
                second.AddId(output);
                var uniforms = new global::Godot.Collections.Array<RDUniform> { first, second };
                Rid set = device.UniformSetCreate(uniforms, shader, 0);
                try
                {
                    foreach (var sample in catalogue.GetProperty("samples").EnumerateArray())
                    {
                        var parameters = new byte[8];
                        float clock = swarm.TimeAt(sample.GetProperty("jd").GetDouble());
                        if (clock != sample.GetProperty("time").GetSingle())
                            throw new InvalidDataException($"The epoch changed in {filename}.");
                        BinaryPrimitives.WriteSingleLittleEndian(parameters, clock);
                        BinaryPrimitives.WriteUInt32LittleEndian(parameters.AsSpan(4), (uint)count);
                        long dispatch = device.ComputeListBegin();
                        device.ComputeListBindComputePipeline(dispatch, pipeline);
                        device.ComputeListBindUniformSet(dispatch, set, 0);
                        device.ComputeListSetPushConstant(dispatch, parameters, 8);
                        device.ComputeListDispatch(dispatch, (uint)((count + 63) / 64), 1, 1);
                        device.ComputeListEnd();
                        device.Submit();
                        device.Sync();

                        var actual = MemoryMarshal.Cast<byte, float>(device.BufferGetData(output));
                        var expected = MemoryMarshal.Cast<byte, float>(captures.AsSpan(
                            sample.GetProperty("offsetBytes").GetInt32(), sample.GetProperty("bytes").GetInt32()));
                        for (int i = 0; i < count; i++)
                        {
                            int at = 8 * i;
                            double x = (double)actual[at] - expected[at];
                            double y = (double)actual[at + 1] - expected[at + 1];
                            double z = (double)actual[at + 2] - expected[at + 2];
                            double error = Math.Sqrt(x * x + y * y + z * z) / Math.Max(1, swarm.Attributes[9 * i]);
                            if (!double.IsFinite(error) || error > 1e-6)
                                throw new InvalidOperationException($"{filename}, clock {clock:R}, point {i}: relative position error {error:R}.");
                            worstPosition = Math.Max(error, worstPosition);
                            for (int channel = 4; channel < 8; channel++)
                            {
                                double delta = Math.Abs((double)actual[at + channel] - expected[at + channel]);
                                if (!double.IsFinite(delta) || delta > 2e-7)
                                    throw new InvalidOperationException($"{filename}, clock {clock:R}, point {i}: color error {delta:R}.");
                                worstColor = Math.Max(delta, worstColor);
                            }
                            compared++;
                        }
                    }
                }
                finally
                {
                    device.FreeRid(set);
                    device.FreeRid(output);
                    device.FreeRid(input);
                    uniforms.Clear();
                }
            }
        }
        finally
        {
            device.FreeRid(pipeline);
            device.FreeRid(shader);
            device.Free();
        }
        if (compared != 65_004)
            throw new InvalidDataException($"Expected 65,004 WGL states, checked {compared}.");
        GD.Print(FormattableString.Invariant($"Swarm Vulkan/WGL: {compared} states, maximum relative position {worstPosition:R}, color {worstColor:R}."));
    }
}

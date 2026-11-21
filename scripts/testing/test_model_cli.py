"""Model export acceptance through the real command-line process."""
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "deps/space-tangent-core/scripts/testing"))
from cli_test_support import run_cli


@unittest.skipUnless(os.environ.get("ST_MODEL_TOOL_DLL"), "Set ST_MODEL_TOOL_DLL to the built model tool.")
class ModelCliTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="st-model-cli-")
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name).resolve()
        self.tool = Path(os.environ["ST_MODEL_TOOL_DLL"]).resolve(strict=True)
        self.sequence = 0
        self.obj = self.root / "triangle with spaces.obj"
        self.obj.write_text("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n", encoding="utf-8")

    def cli(self, *arguments, status=0, stdout="", stderr=""):
        self.sequence += 1
        result = run_cli(self.tool, arguments, self._testMethodName, self.sequence, "SpaceTangent.ModelTool")
        self.assertEqual(status, result.returncode, result.stdout + result.stderr)
        self.assertIn(stdout, result.stdout)
        self.assertIn(stderr, result.stderr)
        return result

    def glb(self, path, minimum_vertices=3):
        data = path.read_bytes()
        magic, version, size, json_size, json_kind = struct.unpack_from("<5I", data)
        self.assertEqual((0x46546c67, 2, len(data), 0x4e4f534a), (magic, version, size, json_kind))
        document = json.loads(data[20:20 + json_size])
        self.assertEqual("2.0", document["asset"]["version"])
        primitive = document["meshes"][0]["primitives"][0]
        positions = document["accessors"][primitive["attributes"]["POSITION"]]
        self.assertEqual("VEC3", positions["type"])
        self.assertGreaterEqual(positions["count"], minimum_vertices)
        self.assertFalse(list(self.root.glob("**/.model-*.tmp")))
        return document

    def test_usage_and_rejected_options_do_not_create_output(self):
        self.cli(status=2, stderr="Usage:")
        self.cli("model", self.obj, self.root / "none.glb", "--unknown", status=2, stderr="Usage:")
        self.assertFalse((self.root / "none.glb").exists())

    def test_obj_and_glb_conversion_write_valid_relocatable_geometry(self):
        output = self.root / "new directory" / "triangle.glb"
        self.cli("model", self.obj, output, "--root", self.root, stdout="1 triangles")
        self.glb(output)
        self.obj.unlink()
        second = self.root / "repacked.glb"
        self.cli("model", output, second)
        self.glb(second)

    def test_cmod_native_body_and_displaced_sphere_are_supported(self):
        model = self.root / "triangle.cmod"
        model.write_text("#celmodel__ascii\nmaterial diffuse 1 0 0 end_material\n"
            "mesh vertexdesc position f3 end_vertexdesc vertices 3 0 0 0 1 0 0 0 1 0 trilist 0 3 0 1 2 end_mesh\n", encoding="utf-8")
        for index, options in enumerate([[], ["--native-body"], ["--root", self.root, "--native-body"]]):
            output = self.root / f"cmod-{index}.glb"
            self.cli("model", model, output, *options, stdout="1 triangles")
            self.glb(output)
        sphere = self.root / "shape.cms"
        sphere.write_text("SphereDisplacementMesh { Size [2 3 4] Slices 8 Rings 5 FeatureHeight 0 }", encoding="utf-8")
        self.cli("model", sphere, self.root / "sphere.glb")
        self.glb(self.root / "sphere.glb", 12)

    def test_path_extension_and_existing_output_fail_without_replacement(self):
        target = self.root / "protected.glb"
        target.write_bytes(b"keep original")
        self.cli("model", self.obj, target, status=1, stderr="output already exists")
        self.assertEqual(b"keep original", target.read_bytes())
        self.cli("model", self.obj, self.root / "wrong.bin", status=1, stderr="must end in .glb")
        inside = self.root / "inside"
        inside.mkdir()
        self.cli("model", self.obj, inside / "out.glb", "--root", inside, status=1, stderr="inside its add-on root")
        self.cli("model", self.obj, self.root / "native.glb", "--native-body", status=1, stderr="applies to CMOD")
        self.assertFalse((inside / "out.glb").exists())
        self.assertFalse((self.root / "native.glb").exists())

    def test_missing_unknown_and_malformed_inputs_return_mesh_diagnostics(self):
        self.cli("model", self.root / "absent.obj", self.root / "missing.glb", status=1, stderr="Mesh/")
        wrong = self.root / "wrong.txt"
        wrong.write_text("not a model", encoding="utf-8")
        self.cli("model", wrong, self.root / "wrong.glb", status=1, stderr="Expected OBJ")
        self.obj.write_text("v NaN 0 0\nf 1 2 3", encoding="utf-8")
        self.cli("model", self.obj, self.root / "broken.glb", status=1, stderr="Mesh/")
        self.assertFalse((self.root / "broken.glb").exists())


if __name__ == "__main__":
    unittest.main()

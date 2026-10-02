"""Core editor round-trip and native-resource checks; never touches installed mods."""
import copy
import base64
import io
import json
import tempfile
import unittest
from pathlib import Path

import assets
import server
import studio
from xui_expr import evaluate


class StudioTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=server.workspace())
        self.home = Path(self.temp.name)
        self.root = self.home / "source"
        self.mod = self.root / "mods/JonPartyPortraits"
        self.mod.mkdir(parents=True)
        (self.mod / "ModInfo.xml").write_text('<xml><Name value="JonPartyPortraits"/></xml>')
        target = self.mod / "Config/XUi_InGame/templates.xml"
        target.parent.mkdir(parents=True)
        self.original = (server.ROOT / "mods/JonPartyPortraits/Config/XUi_InGame/templates.xml").read_bytes()
        target.write_bytes(self.original)
        self.target = target
        self.key = "XUi_InGame:templates.xml:party_entry"

    def tearDown(self):
        self.temp.cleanup()

    def test_real_xml_save_round_trip_backup_and_conflict(self):
        scene = studio.load_scene(server.GAME, self.root, self.key)
        tree = copy.deepcopy(scene["tree"])
        text = next(n for n in tree["children"][0]["children"] if n["attrs"].get("name") == "TextContent")
        text["attrs"]["text"] = "Studio verification"
        tree["children"][0]["children"].append({"id": "new-test", "tag": "sprite", "attrs": {"name": "added", "sprite": "ui_game_symbol_quest"}, "children": []})
        payload = {"key": self.key, "revision": scene["revision"], "owner": "JonPartyPortraits", "tree": tree}
        saved = studio.save_scene(server.GAME, self.root, self.home, payload)
        self.assertEqual((self.home / saved["backup"]).read_bytes(), self.original)
        # Existing hand-authored source stays before the owned editor block.
        self.assertIn(b'<remove xpath="/templates/party_entry/rect/*"', self.target.read_bytes())
        actual = saved["scene"]["tree"]
        self.assertEqual(actual["children"][0]["children"][3]["attrs"]["text"], "Studio verification")
        self.assertEqual(actual["children"][0]["children"][-1]["attrs"]["name"], "added")
        with self.assertRaises(RuntimeError):
            studio.save_scene(server.GAME, self.root, self.home, payload)
        second = {**payload, "tree": actual, "revision": saved["scene"]["revision"]}
        studio.save_scene(server.GAME, self.root, self.home, second)
        self.assertEqual(self.target.read_text().count(" begin"), 1)
        later = self.root / "mods/ZLater"
        (later / "Config/XUi_InGame").mkdir(parents=True)
        (later / "ModInfo.xml").write_text('<xml><Name value="ZLater"/></xml>')
        (later / "Config/XUi_InGame/templates.xml").write_text('<configs><set xpath="/templates/party_entry/rect/label[@name=\'TextContent\']/@text">Overridden</set></configs>')
        latest = studio.load_scene(server.GAME, self.root, self.key)
        overridden = copy.deepcopy(latest["tree"])
        overridden["children"][0]["children"][3]["attrs"]["text"] = "Do not overwrite"
        before = self.target.read_bytes()
        with self.assertRaises(RuntimeError):
            studio.save_scene(server.GAME, self.root, self.home, {**payload, "tree": overridden, "revision": latest["revision"]})
        self.assertEqual(self.target.read_bytes(), before)
        (later / "Config/XUi_InGame/windows.xml").write_text('<configs><append xpath="/windows"><window name="jonStudioCustom" width="100" height="50"><label text="Custom mod UI"/></window></append></configs>')
        custom = "XUi_InGame:windows.xml:jonStudioCustom"
        self.assertIn(custom, {scene["key"] for scene in studio.scene_catalog(server.GAME, self.root)})
        self.assertEqual(studio.load_scene(server.GAME, self.root, custom)["owner"], "ZLater")

    def test_scene_validation_and_bounded_expression_evaluation(self):
        scene = studio.load_scene(server.GAME, self.root, self.key)
        tree = copy.deepcopy(scene["tree"])
        tree["children"][0]["children"].append({"id": "new-unsafe", "tag": "external", "attrs": {}, "children": []})
        with self.assertRaises(ValueError):
            studio.validate_tree(tree, scene["tree"])
        self.assertEqual(evaluate("Round(width/2,0)-2", {"width": "100"}), 48)
        self.assertEqual(evaluate("!btn_enabled ? color(120,120,120,255) : (btn_hovered ? color(228,18,21,255) : color(255,255,255,255))", {"btn_enabled": True, "btn_hovered": False}), "255,255,255,255")
        self.assertFalse(evaluate("defined('missing') and missing", {}))
        for expr in ("__import__('os')", "(1).__class__", "'x'*1000000000", "[1 for i in range(100)]"):
            with self.assertRaises(ValueError):
                evaluate(expr, {})
        from PIL import Image
        raw = io.BytesIO()
        Image.new("RGBA", (100, 100), (25, 50, 100, 255)).save(raw, "PNG")
        payload = {"name": "Imported genuine sample", "image": "data:image/png;base64," + base64.b64encode(raw.getvalue()).decode(), "crop": [10, 20, 30, 40]}
        imported = assets.import_capture(self.home / "captures", payload)
        self.assertEqual(imported["size"], [30, 40])
        self.assertEqual(len(assets.load(self.home / "captures")["assets"]), 1)
        with self.assertRaises(ValueError):
            assets.import_capture(self.home / "captures", {**payload, "crop": [90, 90, 30, 40]})

    def test_native_atlas_font_portrait_and_template_geometry(self):
        manifest = assets.load(server.workspace() / "assets")
        if not manifest["assets"]:
            self.skipTest("Local asset extraction has not been run on this machine")
        library = manifest["assets"]
        scene = studio.load_scene(server.GAME, self.root, self.key)
        graph = studio.layout(server.GAME, self.root, scene, scene["tree"], {
            "name": "LabB", "jonlevel": "Lv 1", "distance": "15m", "healthfill": .5,
            "healthmodifiedmax": 1, "healthcurrentwithmax": "50/100", "staminafill": .8,
            "staminamodifiedmax": 1, "jondeficitfill": 0, "jonxpfill": .2,
            "partyvisible": True,
            "showicon1": False, "showicon2": False, "voicevisible": False, "showarrow": False,
        }, library)
        self.assertEqual(graph["bounds"], [0, 0, 310, 76])
        self.assertEqual(graph["warnings"], [])
        self.assertTrue(next(c for c in graph["controls"] if c["name"] == "jonPortrait")["asset"])
        image = studio.render_image(graph, library)
        self.assertEqual(image.size, (620, 152))
        self.assertGreater(image.getpixel((15, 15))[3], 0)
        self.assertEqual(next(a for a in library if a["id"] == graph["font"])["nativeName"], "opinionproextracondensed-bold")
        menu = studio.load_scene(server.GAME, server.ROOT, "XUi_Menu:windows.xml:mainMenu")
        graph = studio.layout(server.GAME, server.ROOT, menu, menu["tree"], {"btn_enabled": True, "btn_hovered": False}, library)
        labels = [c for c in graph["controls"] if c["tag"] == "label"]
        self.assertEqual(graph["warnings"], [])
        self.assertEqual(labels[1]["box"][1] - labels[0]["box"][1], 60)
        self.assertLess(labels[0]["box"][3], 50)
        self.assertEqual(next(c for c in graph["controls"] if c["name"] == "gameLogo")["box"][2], 243.2)
        inventory = studio.load_scene(server.GAME, server.ROOT, "XUi_InGame:windows.xml:windowBackpack")
        graph = studio.layout(server.GAME, server.ROOT, inventory, inventory["tree"], {"itemicon": "gunHandgunT1Pistol", "iconcolor": "255,255,255,255", "userlockedslot": False}, library)
        icons = [c for c in graph["controls"] if c["name"] == "itemIcon"]
        self.assertEqual(len(icons), 45)
        self.assertEqual(len({tuple(c["box"]) for c in icons}), 45)
        self.assertEqual(icons[1]["box"][0] - icons[0]["box"][0], 67)


if __name__ == "__main__":
    unittest.main()

"""Core workspace regressions. python -B -m unittest discover -s lab -p test_server.py"""
import json
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch
from urllib.error import HTTPError
from urllib.request import Request, urlopen

import server


class WorkspaceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(dir=server.workspace())
        self.home = Path(self.temp.name)
        self.scratch_patch = patch.object(server, "scratch", return_value=self.home)
        self.scratch_patch.start()
        server.JOBS.clear()
        server.ACTIVE = None

    def tearDown(self):
        self.scratch_patch.stop()
        self.temp.cleanup()

    def test_telemetry_retains_routes_walls_notes_and_valid_lines(self):
        path = self.home / "follow.jsonl"
        events = [{"e": "start", "t": 1, "p": [0, 0, 0]},
                  {"e": "route", "t": 2, "found": 1, "cells": 30, "walls": [[1, 2]], "pts": [[3, 0, 4]]},
                  {"e": "note", "t": 3, "what": "blocked"}]
        path.write_text("\n".join(json.dumps(e) for e in events) + '\n{"unfinished":', encoding="utf-8")
        result = server.telemetry(path)
        self.assertEqual(result["routes"][0]["walls"], [[1, 2]])
        self.assertEqual(result["notes"][0]["what"], "blocked")
        self.assertEqual(len(result["events"]), 3)
        self.assertEqual(len(result["warnings"]), 1)

    def test_isolated_telemetry_is_discovered(self):
        path = self.home / "ingame/run/A/JonFollow/telemetry/follow-run.jsonl"
        path.parent.mkdir(parents=True)
        path.write_text('{"e":"end","t":2}', encoding="utf-8")
        self.assertIn(path, server.telemetry_files())

    def test_path_confinement_and_drawing_names(self):
        with self.assertRaises(ValueError):
            server.safe_child(self.home, "../outside")
        with self.assertRaises(ValueError):
            server.drawing_path("../secret")
        self.assertEqual(server.safe_child(self.home, "valid.png"), self.home / "valid.png")
        evidence = server.workspace() / "studio/renders/visible.png"
        evidence.parent.mkdir(parents=True)
        evidence.write_bytes(b"fixture")
        for relative in ("weblab/assets/texture.png", "archive/build/Icon.png"):
            path = self.home / relative
            path.parent.mkdir(parents=True)
            path.write_bytes(b"fixture")
        with patch.object(server, "scratch", return_value=self.home) as resolve:
            self.assertEqual([p["name"] for p in server.previews()], ["visible.png"])
            self.assertEqual(resolve.call_count, 1)

    def test_jobs_stream_finish_and_can_run_again(self):
        def command(kind, params, job_id):
            return [sys.executable, "-u", "-c", "import time; print('PASS first', flush=True); time.sleep(.2); print('FAIL second')"], 10, None
        with patch.object(server, "command_for", side_effect=command):
            first = server.start_job("sim")
            with self.assertRaises(RuntimeError):
                server.start_job("sim")
            deadline = time.time() + 10
            while server.ACTIVE and time.time() < deadline:
                time.sleep(.02)
            self.assertIsNone(server.ACTIVE)
            checks = server.job_list()[0]["checks"]
            self.assertEqual([c["status"] for c in checks], ["pass", "fail"])
            self.assertEqual(server.job_list()[0]["status"], "failed")
            second = server.start_job("sim")
            self.assertNotEqual(first["id"], second["id"])
            while server.ACTIVE and time.time() < deadline:
                time.sleep(.02)
            self.assertEqual(len(list((server.workspace() / "jobs").glob("*.json"))), 2)

    def test_custom_scenarios_are_read_only_and_skips_are_not_passes(self):
        scenario = server.validate_scenario({"steps": [{"expect_console": {"tel": "hr doctor", "pattern": "0 fail"}}]})
        self.assertEqual(len(scenario["steps"]), 3)
        with self.assertRaises(ValueError):
            server.validate_scenario({"steps": [{"expect_console": {"tel": "shutdown", "pattern": ".*"}}]})
        with self.assertRaises(ValueError):
            server.validate_scenario({"steps": [{"expect_console": {"tel": "hr doctor", "pattern": "["}}]})
        with self.assertRaises(ValueError):
            server.validate_drawing({"shapes": [{"type": "pen", "points": [[float('inf'), 0]]}]})
        self.assertEqual(server.check_results("PASS UI (capture skipped)")[0]["status"], "skip")

    def test_http_requires_local_host_and_token_and_get_has_no_side_effects(self):
        http = server.ThreadingHTTPServer(("127.0.0.1", 0), server.Handler)
        threading.Thread(target=http.serve_forever, daemon=True).start()
        base = "http://127.0.0.1:" + str(http.server_port)
        try:
            with self.assertRaises(HTTPError) as error:
                urlopen(base + "/api/run?job=sim")
            self.assertEqual(error.exception.code, 404)
            data = json.dumps({"shapes": []}).encode()
            request = Request(base + "/api/drawings/test", data=data, headers={"Content-Type": "application/json"})
            with self.assertRaises(HTTPError) as error:
                urlopen(request)
            self.assertEqual(error.exception.code, 403)
            request.add_header("X-Lab-Token", server.TOKEN)
            request.add_header("Origin", "https://untrusted.example")
            with self.assertRaises(HTTPError) as error:
                urlopen(request)
            self.assertEqual(error.exception.code, 403)
            request.remove_header("Origin")
            with urlopen(request) as result:
                self.assertTrue(json.load(result)["saved"])
            with urlopen(base + "/api/drawings/test") as result:
                self.assertEqual(json.load(result)["shapes"], [])
            from PIL import Image
            import base64
            import io
            png = io.BytesIO()
            Image.new("RGB", (20, 20)).save(png, format="PNG")
            payload = json.dumps({"kind": "map", "png": "data:image/png;base64," + base64.b64encode(png.getvalue()).decode()}).encode()
            request = Request(base + "/api/exports", data=payload, headers={"X-Lab-Token": server.TOKEN})
            with urlopen(request) as result:
                saved = json.load(result)
            self.assertTrue((self.home / saved["path"]).exists())
            font = server.workspace() / "assets/native.ttf"
            font.parent.mkdir(parents=True, exist_ok=True)
            font.write_bytes(b"native-font-fixture")
            server.atomic_json(font.parent / "index.json", {"assets": [{"id": "fontfixture", "file": str(font)}]})
            with urlopen(base + "/api/assets/file/fontfixture") as result:
                self.assertEqual(result.headers["Content-Type"], "font/ttf")
            with self.assertRaises(HTTPError) as error:
                urlopen(base + "/api/assets/file/not-indexed")
            self.assertEqual(error.exception.code, 404)
            request = Request(base + "/api/jobs", headers={"Host": "evil.example"})
            with self.assertRaises(HTTPError) as error:
                urlopen(request)
            self.assertEqual(error.exception.code, 403)
        finally:
            http.shutdown()
            http.server_close()


if __name__ == "__main__":
    unittest.main()

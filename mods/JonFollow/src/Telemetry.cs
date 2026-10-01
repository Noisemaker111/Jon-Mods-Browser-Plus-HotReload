using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace JonFollow
{
    // One JSON line per event for the latest follow sessions, so a bad follow
    // can be drawn as a top-down map (scripts/follow-map.py) instead of guessed at.
    // Files: <user data>/JonFollow/telemetry/follow-*.jsonl, newest ten kept.
    public static class Telemetry
    {
        static StreamWriter writer;
        static float nextSample;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Begin(EntityPlayerLocal player, EntityPlayer target)
        {
            End("restart");
            try
            {
                string dir = Path.Combine(GameIO.GetUserGameDataDir(), "JonFollow", "telemetry");
                Directory.CreateDirectory(dir);
                var old = new List<string>(Directory.GetFiles(dir, "follow-*.jsonl"));
                old.Sort();
                for (int i = 0; i < old.Count - 9; i++) File.Delete(old[i]);
                writer = new StreamWriter(Path.Combine(dir, "follow-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv) + ".jsonl"), false, new UTF8Encoding(false));
                Write("{\"e\":\"start\",\"t\":" + F(Time.time) + ",\"player\":\"" + player.PlayerDisplayName + "\",\"leader\":\"" + target.PlayerDisplayName + "\"," + V("p", player.position) + "," + V("l", target.position) + "}");
            }
            catch (Exception e) { writer = null; Log.Warning("[JonFollow] telemetry: " + e.Message); }
        }

        public static void Sample(EntityPlayerLocal player, Vector3 leader, Vector3 aimPoint, string mode, MovementInput input, float angle, float leaderSpeed, string ahead)
        {
            if (writer == null || Time.time < nextSample) return;
            nextSample = Time.time + 0.1f;
            Write("{\"e\":\"s\",\"t\":" + F(Time.time) + "," + V("p", player.position) + "," + V("l", leader) + "," + V("w", aimPoint) +
                ",\"yaw\":" + F(player.rotation.y) + ",\"cam\":" + F(player.playerCamera != null ? player.playerCamera.transform.eulerAngles.y : 0) +
                ",\"angle\":" + F(angle) + ",\"turn\":" + F(input.rotation.y) + ",\"fwd\":" + F(input.moveForward) +
                ",\"run\":" + (input.running ? 1 : 0) + ",\"jump\":" + (input.jump ? 1 : 0) + ",\"mode\":\"" + mode + "\",\"ahead\":\"" + ahead +
                "\",\"stam\":" + F(player.Stamina) + ",\"ls\":" + F(leaderSpeed) + ",\"ground\":" + (player.onGround ? 1 : 0) + "}");
        }

        public static void Route(Vector3 from, Vector3 goal, bool found, int expanded, IList<Vector3> points, IList<Vector3i> walls)
        {
            if (writer == null) return;
            var sb = new StringBuilder();
            sb.Append("{\"e\":\"route\",\"t\":").Append(F(Time.time)).Append(',').Append(V("p", from)).Append(',').Append(V("g", goal))
              .Append(",\"found\":").Append(found ? 1 : 0).Append(",\"cells\":").Append(expanded).Append(",\"pts\":[");
            for (int i = 0; i < points.Count; i++) sb.Append(i == 0 ? "" : ",").Append('[').Append(F(points[i].x)).Append(',').Append(F(points[i].y)).Append(',').Append(F(points[i].z)).Append(']');
            sb.Append("],\"walls\":[");
            for (int i = 0; i < walls.Count; i++) sb.Append(i == 0 ? "" : ",").Append('[').Append(walls[i].x).Append(',').Append(walls[i].z).Append(']');
            Write(sb.Append("]}").ToString());
        }

        public static void Note(string what)
        {
            if (writer != null) Write("{\"e\":\"note\",\"t\":" + F(Time.time) + ",\"what\":\"" + what.Replace("\"", "'") + "\"}");
        }

        public static void End(string reason)
        {
            if (writer == null) return;
            try { Write("{\"e\":\"end\",\"t\":" + F(Time.time) + ",\"why\":\"" + (reason ?? "").Replace("\"", "'") + "\"}"); writer.Dispose(); }
            catch (Exception) { }
            writer = null;
        }

        static void Write(string line) { try { writer.WriteLine(line); writer.Flush(); } catch (Exception) { writer = null; } }
        static string F(float v) { return v.ToString("0.###", Inv); }
        static string V(string name, Vector3 v) { return "\"" + name + "\":[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]"; }
    }
}

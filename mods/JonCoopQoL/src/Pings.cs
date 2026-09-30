using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace JonCoopQoL
{
    public sealed class GroundPings : IDisposable
    {
        sealed class Ping { public TeamMessage Message; public float Started; }
        readonly Dictionary<int, Ping> active = new Dictionary<int, Ping>();
        Mesh arrows;
        Material material;
        bool meshAttempted;
        public void Clear() { active.Clear(); }
        public void Remove(int owner) { active.Remove(owner); }
        public void Add(TeamMessage m) { active[m.Owner] = new Ping { Message = m, Started = Time.unscaledTime }; }
        public void Dispose()
        {
            Clear();
            if (arrows != null) UnityEngine.Object.Destroy(arrows);
            if (material != null) UnityEngine.Object.Destroy(material);
        }
        public void HandleInput(EntityPlayerLocal player, TeamService team)
        {
            var input = Event.current;
            if (input.type != EventType.MouseDown || input.button != 2 || Cursor.lockState != CursorLockMode.Locked || player.windowManager.IsModalWindowOpen() || player.playerCamera == null) return;
            var ray = player.playerCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            var hits = Physics.RaycastAll(ray, 500, ~0, QueryTriggerInteraction.Ignore)
                .Where(h => { var entity = h.collider.GetComponentInParent<Entity>(); return entity != player && (entity == null || entity != player.AttachedToEntity); })
                .OrderBy(h => h.distance).ToArray();
            if (hits.Length == 0) return;
            var hit = hits[0];
            if (hit.normal.y < 0.3f)
            {
                // Aiming at a wall puts the arrows on the ground at that wall,
                // rather than pointing through it into an unseen room.
                if (!Physics.Raycast(hit.point + hit.normal * 0.15f + Vector3.up * 0.25f, Vector3.down, out hit, 100, ~0, QueryTriggerInteraction.Ignore) || hit.normal.y < 0.3f) return;
            }
            var position = hit.point + Origin.position;
            var forward = Vector3.ProjectOnPlane(player.playerCamera.transform.forward, Vector3.up);
            team.Ping(new TeamMessage { Kind = TeamKind.Ping, X = position.x, Y = position.y, Z = position.z,
                NX = hit.normal.x, NY = hit.normal.y, NZ = hit.normal.z, Heading = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg });
            input.Use();
        }
        public void Render(EntityPlayerLocal player)
        {
            if (player == null || player.IsDead() || Camera.current != player.playerCamera) return;
            foreach (var id in active.Keys.ToArray()) if (TeamProtocol.PingAlpha(Time.unscaledTime - active[id].Started) <= 0) active.Remove(id);
            if (active.Count == 0) return;
            if (!meshAttempted) { meshAttempted = true; MakeMesh(); }
            if (material == null) return;
            foreach (var ping in active.Values)
            {
                float age = Time.unscaledTime - ping.Started;
                var m = ping.Message;
                var normal = new Vector3(m.NX, m.NY, m.NZ).normalized;
                if (normal.y < 0.3f) normal = Vector3.up;
                var position = new Vector3(m.X, m.Y, m.Z) - Origin.position + normal * 0.04f;
                var rotation = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0, m.Heading, 0);
                // Each teammate has one current ping. Six real seconds, with a
                // two-second fade, independent of the game-time speed setting.
                material.color = new Color(0.25f, 0.9f, 1, TeamProtocol.PingAlpha(age));
                if (material.SetPass(0)) Graphics.DrawMeshNow(arrows, Matrix4x4.TRS(position, rotation, Vector3.one * TeamProtocol.PingScale(age)));
            }
        }
        void MakeMesh()
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) { Log.Warning("[JonCoopQoL] Ground ping shader unavailable."); return; }
            material = new Material(shader) { mainTexture = Texture2D.whiteTexture };
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (int i = 0; i < 3; i++)
            {
                float z = 0.8f - i * 0.65f;
                AddQuad(vertices, triangles, new Vector3(-0.55f, 0, z-0.45f), new Vector3(-0.55f, 0, z-0.2f), new Vector3(0, 0, z+0.25f), new Vector3(0, 0, z));
                AddQuad(vertices, triangles, new Vector3(0, 0, z), new Vector3(0, 0, z+0.25f), new Vector3(0.55f, 0, z-0.2f), new Vector3(0.55f, 0, z-0.45f));
            }
            arrows = new Mesh { name = "Jon team ground arrows" };
            arrows.SetVertices(vertices); arrows.SetTriangles(triangles, 0);
            arrows.colors = Enumerable.Repeat(Color.white, vertices.Count).ToArray();
            arrows.uv = Enumerable.Repeat(Vector2.zero, vertices.Count).ToArray();
            arrows.RecalculateBounds();
        }
        static void AddQuad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int start = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(start); t.Add(start+1); t.Add(start+2); t.Add(start); t.Add(start+2); t.Add(start+3);
        }
    }
}

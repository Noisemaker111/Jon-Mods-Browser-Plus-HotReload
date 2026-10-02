using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace JonGroundPings
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
            var eye = player.playerCamera.transform.position;
            foreach (var ping in active.Values)
            {
                float age = Time.unscaledTime - ping.Started;
                var m = ping.Message;
                var spot = new Vector3(m.X, m.Y, m.Z) - Origin.position;
                // Three chevrons stand over the spot pointing down at it,
                // turned to face the viewer and sized by distance, so a ping
                // reads at 5 m or 80 m and through grass. Each teammate has
                // one current ping: six real seconds, fading over the last two.
                var toEye = eye - spot; toEye.y = 0;
                var facing = toEye.sqrMagnitude > 0.001f ? Quaternion.LookRotation(-toEye.normalized) : Quaternion.identity;
                float size = Mathf.Max(1f, Vector3.Distance(eye, spot) * 0.045f) * TeamProtocol.PingScale(age);
                float alpha = TeamProtocol.PingAlpha(age);
                for (int i = 0; i < 3; i++)
                {
                    // Cascading pulse from the top chevron down to the spot.
                    float wave = 0.55f + 0.45f * Mathf.Sin((age * 3f - i * 0.35f) * Mathf.PI * 2f);
                    material.color = new Color(0.25f, 0.9f, 1, alpha * wave);
                    var at = spot + Vector3.up * size * (0.25f + i * 0.55f);
                    if (material.SetPass(0)) Graphics.DrawMeshNow(arrows, Matrix4x4.TRS(at, facing, Vector3.one * size));
                }
            }
        }
        void MakeMesh()
        {
            // The built-in colored shader takes a depth-test setting; Always
            // keeps the marker visible over terrain, grass and props.
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) { Log.Warning("[JonGroundPings] Ground ping shader unavailable."); return; }
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            material.SetInt("_ZWrite", 0);
            material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            // One downward chevron in the vertical XY plane, tip at the origin.
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            AddQuad(vertices, triangles, new Vector3(-0.5f, 0.5f, 0), new Vector3(-0.5f, 0.32f, 0), new Vector3(0, -0.18f, 0), new Vector3(0, 0, 0));
            AddQuad(vertices, triangles, new Vector3(0, 0, 0), new Vector3(0, -0.18f, 0), new Vector3(0.5f, 0.32f, 0), new Vector3(0.5f, 0.5f, 0));
            arrows = new Mesh { name = "Jon team ping chevron" };
            arrows.SetVertices(vertices); arrows.SetTriangles(triangles, 0);
            arrows.colors = Enumerable.Repeat(Color.white, vertices.Count).ToArray();
            arrows.RecalculateBounds();
        }
        static void AddQuad(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int start = v.Count; v.Add(a); v.Add(b); v.Add(c); v.Add(d);
            t.Add(start); t.Add(start+1); t.Add(start+2); t.Add(start); t.Add(start+2); t.Add(start+3);
        }
    }
}

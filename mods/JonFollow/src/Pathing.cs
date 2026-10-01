using System;
using System.Collections.Generic;
using UnityEngine;

namespace JonFollow
{
    // Client-side A* over the block grid. The game's own pathfinder only runs
    // on the server, so the follower plans with the blocks its client has loaded.
    public sealed class GridPath
    {
        // Searched a slice per movement frame so long detours (around a
        // compound wall to its gate) never stall a frame.
        const int TotalBudget = 15000, FrameBudget = 1500;
        readonly List<Vector3> points = new List<Vector3>();
        int index;
        Search search;
        public Vector3 Goal;
        public bool Has => index < points.Count;
        public bool Searching => search != null;
        public Vector3 Current => points[index];
        public int LastExpanded { get; private set; }
        public IList<Vector3> Points => points;

        // Cells around the search a player cannot walk into at their level,
        // for the telemetry map.
        public static List<Vector3i> Walls(World world, Vector3 from, Vector3 to)
        {
            var walls = new List<Vector3i>();
            var a = Cell(from); var b = Cell(to);
            int pad = 6, y = a.y;
            for (int x = Math.Min(a.x, b.x) - pad; x <= Math.Max(a.x, b.x) + pad; x++)
                for (int z = Math.Min(a.z, b.z) - pad; z <= Math.Max(a.z, b.z) + pad; z++)
                    if (!Clear(world, x, y, z) && !Standable(world, x, y + 1, z) && !Standable(world, x, y - 1, z)) walls.Add(new Vector3i(x, y, z));
            return walls;
        }
        public void Clear() { points.Clear(); index = 0; search = null; }

        public void Advance(Vector3 position)
        {
            // Resume from the closest upcoming point; a fresh route's first
            // cell can lie behind the player and must not turn them around.
            int closest = index; float best = float.MaxValue;
            for (int i = index; i < points.Count && i < index + 8; i++)
            {
                float d = Flat(points[i], position);
                if (d < best && Mathf.Abs(points[i].y - position.y) < 1.6f) { best = d; closest = i; }
            }
            index = closest;
            while (index < points.Count && Flat(points[index], position) < 0.75f && Mathf.Abs(points[index].y - position.y) < 1.6f) index++;
            // A point the player is already beyond (relative to the route's
            // direction) is passed, not something to turn back for.
            while (index + 1 < points.Count)
            {
                Vector3 toPoint = points[index] - position, along = points[index + 1] - points[index];
                toPoint.y = along.y = 0;
                if (Vector3.Dot(toPoint, along) >= 0) break;
                index++;
            }
        }

        // Aims at the farthest route point within reach that the player can
        // walk to in a straight line, so turns are smooth arcs rather than
        // cell-to-cell zig-zags.
        public Vector3 Steer(Vector3 position, Func<Vector3, bool> straight)
        {
            int pick = index;
            for (int i = index + 1; i < points.Count; i++)
            {
                if (Flat(points[i], position) > 4.5f || Mathf.Abs(points[i].y - points[i - 1].y) > 1.1f || !straight(points[i])) break;
                pick = i;
            }
            return points[pick];
        }

        static float Flat(Vector3 a, Vector3 b) { return new Vector2(a.x - b.x, a.z - b.z).magnitude; }

        public bool Begin(World world, Vector3 from, Vector3 to)
        {
            search = null;
            Goal = to;
            if (!Stand(world, ref from) || !Stand(world, ref to)) return false;
            var s = new Search { World = world, Start = Cell(from), Target = Cell(to) };
            s.First = s.Best = new Node { Pos = s.Start, G = 0, H = Distance(s.Start, s.Target) };
            s.Nodes[Key(s.Start)] = s.First; s.Open.Push(s.First);
            search = s;
            return true;
        }

        // Null while still searching, true for a route, false for none.
        public bool? Continue()
        {
            var s = search;
            if (s == null) return false;
            bool reached = false;
            for (int slice = 0; slice < FrameBudget && s.Open.Count > 0 && s.Expanded < TotalBudget; slice++)
            {
                var node = s.Open.Pop();
                if (node.Closed) continue;
                node.Closed = true; s.Expanded++;
                if (node.H < s.Best.H) s.Best = node;
                if (Math.Abs(node.Pos.x - s.Target.x) <= 1 && Math.Abs(node.Pos.z - s.Target.z) <= 1 && Math.Abs(node.Pos.y - s.Target.y) <= 2) { s.Best = node; reached = true; break; }
                foreach (var step in Steps(s.World, node.Pos))
                {
                    long key = Key(step.Pos);
                    float g = node.G + step.Cost;
                    if (s.Nodes.TryGetValue(key, out var known) && (known.Closed || known.G <= g)) continue;
                    var next = new Node { Pos = step.Pos, G = g, H = Distance(step.Pos, s.Target), Parent = node };
                    s.Nodes[key] = next; s.Open.Push(next);
                }
            }
            if (!reached && s.Open.Count > 0 && s.Expanded < TotalBudget) return null;
            search = null; LastExpanded = s.Expanded;
            points.Clear(); index = 0;
            // A partial route is used only when it really closes the distance;
            // otherwise it would lead back into the same wall.
            if (!reached && s.First.H - s.Best.H < 4f) return false;
            for (var n = s.Best; n != null && n != s.First; n = n.Parent) points.Add(new Vector3(n.Pos.x + 0.5f, n.Pos.y, n.Pos.z + 0.5f));
            points.Reverse();
            return points.Count > 0;
        }

        sealed class Search
        {
            public World World;
            public Vector3i Start, Target;
            public readonly Heap Open = new Heap();
            public readonly Dictionary<long, Node> Nodes = new Dictionary<long, Node>();
            public Node First, Best;
            public int Expanded;
        }
        struct Step { public Vector3i Pos; public float Cost; }
        sealed class Node { public Vector3i Pos; public float G, H; public Node Parent; public bool Closed; public float F => G + H; }

        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        static IEnumerable<Step> Steps(World world, Vector3i at)
        {
            for (int i = 0; i < 8; i++)
            {
                int dx = DX[i], dz = DZ[i];
                bool diagonal = dx != 0 && dz != 0;
                if (diagonal && !(Clear(world, at.x + dx, at.y, at.z) && Clear(world, at.x, at.y, at.z + dz))) continue;
                float flat = diagonal ? 1.414f : 1f;
                int x = at.x + dx, z = at.z + dz;
                if (Standable(world, x, at.y, z)) { yield return new Step { Pos = new Vector3i(x, at.y, z), Cost = flat + Hazard(world, x, at.y, z) }; continue; }
                // Up one: a slope or a ledge the player hops.
                if (Standable(world, x, at.y + 1, z) && Open(world, at.x, at.y + 2, at.z))
                { yield return new Step { Pos = new Vector3i(x, at.y + 1, z), Cost = flat + 0.6f + Hazard(world, x, at.y + 1, z) }; continue; }
                // Down up to three blocks, never an injuring fall.
                if (!Clear(world, x, at.y, z)) continue;
                for (int down = 1; down <= 3; down++)
                {
                    if (Standable(world, x, at.y - down, z)) { yield return new Step { Pos = new Vector3i(x, at.y - down, z), Cost = flat + 0.3f * down + Hazard(world, x, at.y - down, z) }; break; }
                    if (!Open(world, x, at.y - down, z)) break;
                }
            }
        }

        static bool Clear(World world, int x, int y, int z) { return Open(world, x, y, z) && Open(world, x, y + 1, z); }
        static bool Standable(World world, int x, int y, int z) { return Clear(world, x, y, z) && Solid(world, x, y - 1, z); }

        static bool Open(World world, int x, int y, int z)
        {
            var value = world.GetBlock(x, y, z);
            if (value.isair || value.isWater) return true;
            if (value.isTerrain) return false;
            var block = value.Block;
            return block == null || !block.IsMovementBlocked(world, new Vector3i(x, y, z), value, BlockFace.None);
        }

        static bool Solid(World world, int x, int y, int z)
        {
            var value = world.GetBlock(x, y, z);
            if (value.isair) return false;
            if (value.isTerrain) return true;
            var block = value.Block;
            return block != null && block.IsMovementBlocked(world, new Vector3i(x, y, z), value, BlockFace.Top);
        }

        // Water slows the player; damaging blocks (spikes, wire) are avoided.
        static float Hazard(World world, int x, int y, int z)
        {
            float cost = world.GetBlock(x, y, z).isWater ? 3f : 0f;
            var below = new Vector3i(x, y - 1, z);
            var floor = world.GetBlock(below);
            if (floor.Block != null && floor.Block.UseBuffsWhenWalkedOn(world, below, floor)) cost += 20f;
            return cost;
        }

        static bool Stand(World world, ref Vector3 position)
        {
            var c = Cell(position);
            for (int dy = 0; dy <= 2; dy++)
                foreach (int y in new[] { c.y + dy, c.y - dy })
                    if (Standable(world, c.x, y, c.z)) { position = new Vector3(c.x + 0.5f, y, c.z + 0.5f); return true; }
            return false;
        }

        static Vector3i Cell(Vector3 p) { return new Vector3i(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y + 0.1f), Mathf.FloorToInt(p.z)); }
        static long Key(Vector3i p) { return ((long)(p.x & 0x1FFFFF) << 42) | ((long)(p.y & 0x1FFFFF) << 21) | (long)(p.z & 0x1FFFFF); }
        static float Distance(Vector3i a, Vector3i b) { return Mathf.Sqrt((a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z)) + Math.Abs(a.y - b.y) * 0.5f; }

        sealed class Heap
        {
            readonly List<Node> items = new List<Node>();
            public int Count => items.Count;
            public void Push(Node n) { items.Add(n); Up(items.Count - 1); }
            public Node Pop()
            {
                var top = items[0]; var last = items[items.Count - 1];
                items.RemoveAt(items.Count - 1);
                if (items.Count > 0) { items[0] = last; Down(0); }
                return top;
            }
            void Up(int i) { while (i > 0) { int p = (i - 1) / 2; if (items[p].F <= items[i].F) break; Swap(i, p); i = p; } }
            void Down(int i)
            {
                while (true)
                {
                    int l = i * 2 + 1, r = l + 1, m = i;
                    if (l < items.Count && items[l].F < items[m].F) m = l;
                    if (r < items.Count && items[r].F < items[m].F) m = r;
                    if (m == i) return;
                    Swap(i, m); i = m;
                }
            }
            void Swap(int a, int b) { var t = items[a]; items[a] = items[b]; items[b] = t; }
        }
    }
}

// ModPack.cs - legacy catalog codes and saved portable pack discovery.
//
// A pack code is a tiny self-contained string: "HRP1-" + urlsafe-base64(gzip(json)).
// JSON = {"v":1,"n":"name","m":[slug,slug,...]}. Anyone can paste it (in-game window,
// F1 console, chat, a forum post); the receiving side resolves slugs against the catalog
// and queues them through the browser. Portable .hrpack files pin the actual bytes;
// FriendSync transports those bytes through the authenticated game connection.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HotReloadTool
{
    public static class ModPack
    {
        public class Pack
        {
            public string name = "";
            public List<string> slugs = new List<string>();
        }

        public class Entry
        {
            public string slug = "", title = "", status = "";
            public BrowserItem item;
        }

        // ------------------------------------------------------------ code <-> pack
        public static string Encode(Pack p)
        {
            var o = new JObject();
            o["v"] = 1; o["n"] = p.name ?? "";
            var a = new JArray(); foreach (var s in p.slugs) a.Add(s);
            o["m"] = a;
            var b64 = Convert.ToBase64String(Gzip(Encoding.UTF8.GetBytes(o.ToString())))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "HRP1-" + b64;
        }

        public static Pack Decode(string code)
        {
            try
            {
                code = (code ?? "").Trim();
                if (!code.StartsWith("HRP1-")) return null;
                var b64 = code.Substring(5).Replace('-', '+').Replace('_', '/');
                while (b64.Length % 4 != 0) b64 += "=";
                var o = JObject.Parse(Encoding.UTF8.GetString(UnGzip(Convert.FromBase64String(b64))));
                var p = new Pack { name = S(o["n"]) };
                if (o["m"] is JArray arr) foreach (var t in arr) p.slugs.Add(S(t));
                if ((int?)o["v"] != 1) return null;
                p.slugs = new List<string>(new HashSet<string>(p.slugs.FindAll(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase));
                return p.slugs.Count > 0 ? p : null;
            }
            catch { return null; }
        }

        static byte[] Gzip(byte[] b)
        { using (var ms = new MemoryStream()) { using (var g = new GZipStream(ms, CompressionMode.Compress)) g.Write(b, 0, b.Length); return ms.ToArray(); } }
        static byte[] UnGzip(byte[] b)
        { using (var ms = new MemoryStream(b)) using (var g = new GZipStream(ms, CompressionMode.Decompress)) using (var outp = new MemoryStream()) { g.CopyTo(outp); return outp.ToArray(); } }
        static string S(object t) { return t == null ? "" : t.ToString(); }

        // ------------------------------------------------------------ resolve + install
        // resolve every slug to a catalog item (cache first, then the detail endpoint)
        public static List<Entry> Resolve(Pack p)
        {
            var res = new List<Entry>();
            var known = new Dictionary<string, BrowserItem>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var it in ModBrowser.Items) known[it.slug] = it; } catch { }
            foreach (var slug in p.slugs)
            {
                var e = new Entry { slug = slug };
                BrowserItem it;
                if (!known.TryGetValue(slug, out it) || it == null) it = ModBrowser.FetchDetailItem(slug);
                if (it != null) { e.item = it; e.title = it.title; e.status = "ok"; }
                else { e.title = slug; e.status = "not found"; }
                res.Add(e);
            }
            return res;
        }

        // force-install everything in the pack (server-side action). Serializes through the
        // core's install queue; returns a per-mod report immediately (installs continue).
        public static string LastResult = "";
        static int applying;
        public static string ApplyAll(Pack p, string note)
        {
            if (p == null || p.slugs.Count == 0) return LastResult = "Pack is empty or invalid";
            if (System.Threading.Interlocked.CompareExchange(ref applying, 1, 0) != 0) return LastResult = "A pack is already resolving";
            LastResult = "Resolving pack " + p.name + "...";
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { LastResult = ApplyWorker(p, note); }
                catch (Exception e) { LastResult = "Pack failed: " + e.Message; }
                finally { System.Threading.Interlocked.Exchange(ref applying, 0); }
            });
            return LastResult;
        }
        static string ApplyWorker(Pack p, string note)
        {
            var entries = Resolve(p);
            int queued = 0, missing = 0, already = 0;
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                if (e.item == null) { missing++; sb.Append("\n  ? " + e.slug + " (not in catalog)"); continue; }
                var st = "";
                try { st = ModBrowser.InstalledStatus(e.item) ?? ""; } catch { }
                if (st == "installed") { already++; sb.Append("\n  = " + e.item.title + " (already installed)"); continue; }
                ModBrowser.RequestInstall(e.item, null, string.IsNullOrEmpty(note) ? "pack" : note);
                queued++;
                sb.Append("\n  + " + e.item.title);
            }
            var head = "[pack:" + p.name + "] " + queued + " queued, " + already + " already in, " + missing + " missing" +
                (queued > 0 ? "  (installing in background...)" : "");
            return head + sb;
        }

        // ------------------------------------------------------------ saved packs (local file list)
        static readonly object savedGate = new object();
        static List<Pack> saved;
        static string[] portable;
        public static void InvalidateSaved() { lock (savedGate) { saved = null; portable = null; } }
        public static string[] PortableFiles()
        {
            lock (savedGate)
            {
                if (portable == null)
                {
                    var dir = PackDir();
                    portable = dir != null && Directory.Exists(dir) ? Directory.GetFiles(dir, "*.hrpack") : new string[0];
                    Array.Sort(portable, StringComparer.OrdinalIgnoreCase);
                }
                return portable;
            }
        }
        static string PackDir()
        {
            try { return Path.Combine(HotReloadCore.ModDir, "browser", "packs"); }
            catch { return null; }
        }

        public static string SaveFile(Pack p)
        {
            try
            {
                var dir = PackDir(); if (dir == null) return "error: no dir";
                Directory.CreateDirectory(dir);
                var o = new JObject(); o["name"] = p.name;
                var a = new JArray(); foreach (var s in p.slugs) a.Add(s);
                o["slugs"] = a;
                File.WriteAllText(Path.Combine(dir, ModBrowser.SafeName(p.name) + ".json"), o.ToString());
                InvalidateSaved();
                return "saved pack '" + p.name + "' (" + p.slugs.Count + " mods)";
            }
            catch (Exception e) { return "save failed: " + e.Message; }
        }

        public static List<Pack> LoadFiles()
        {
            lock (savedGate) { if (saved == null) saved = LoadFilesFresh(); return saved; }
        }
        static List<Pack> LoadFilesFresh()
        {
            var res = new List<Pack>();
            try
            {
                var dir = PackDir(); if (dir == null || !Directory.Exists(dir)) return res;
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var o = JObject.Parse(File.ReadAllText(f));
                        var p = new Pack { name = S(o["name"]) };
                        if (o["slugs"] is JArray arr) foreach (var t in arr) p.slugs.Add(S(t));
                        if (p.slugs.Count > 0) res.Add(p);
                    }
                    catch { }
                }
            }
            catch { }
            return res;
        }

        public static string DeleteFile(string name)
        {
            try
            {
                var f = Path.Combine(PackDir(), ModBrowser.SafeName(name) + ".json");
                if (File.Exists(f)) { File.Delete(f); InvalidateSaved(); return "deleted pack '" + name + "'"; }
                return "no saved pack named '" + name + "'";
            }
            catch (Exception e) { return "delete failed: " + e.Message; }
        }
    }
}

// ModBrowserCore.cs - in-game mod catalog browser + installer for HotReloadTool.
//
// Data source: Jon's 7d2dmods.gg/api catalog; expected /v1 contract below.
//   GET  /v1/metadata                          -> categories, game versions, server sides
//   GET  /v1/mods?page&limit&q&sort&game_version&category   -> list (limit max 100)
//   GET  /v1/mods/{slug}                       -> detail incl. mod_files[]
//   POST /v1/mods/{modId}/files/{fileId}/download -> {token, wait_ms}
//   GET  /v1/mods/downloads/{token}            -> {url}  (signed, expires ~1 day)
//   GET  url                                   -> the zip
//
// Version matching: our game is V3.2; the catalog uses slug "v3" for V3 mods.
// The catalog is cached to disk so the UI opens instantly and barely uses bandwidth.
//
// Everything network-bound runs on a background thread; UI touches happen on the
// main thread via the game's main-thread task scheduler.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace HotReloadTool
{
    public class BrowserItem
    {
        public string slug = "";
        public string id = "";
        public string title = "";
        public string summary = "";
        public string author = "";
        public string cats = "";
        public string serverSide = "";
        public List<string> vers = new List<string>();
        public string version = "";          // current_version (string form)
        public long sizeBytes = 0;
        public int downloads = 0;
        public string updated = "";
        public string thumbnail = "";        // url

        public bool IsV3 { get { foreach (var v in vers) if (v == ModBrowser.GameVersionSlug) return true; return false; } }
        public string SizeText()
        {
            if (sizeBytes <= 0) return "?";
            double mb = sizeBytes / 1048576.0;
            return mb >= 100 ? ((int)mb) + " MB" : mb.ToString("0.#") + " MB";
        }
        public string VersText()
        {
            if (vers.Count == 0) return "-";
            var sb = new StringBuilder();
            foreach (var v in vers)
            {
                if (sb.Length > 0) sb.Append('/');
                sb.Append(v.ToUpperInvariant());
            }
            return sb.ToString();
        }
        public string MetaText()
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(author)) sb.Append(author);
            if (!string.IsNullOrEmpty(cats)) { if (sb.Length > 0) sb.Append("  •  "); sb.Append(cats); }
            sb.Append("  •  ").Append(SizeText());
            if (downloads > 0) sb.Append("  •  ").Append(downloads).Append(" dl");
            if (!string.IsNullOrEmpty(updated)) sb.Append("  •  ").Append(updated);
            if (!string.IsNullOrEmpty(serverSide)) sb.Append("  •  ").Append(serverSide);
            return sb.ToString();
        }
    }

    public static class ModBrowser
    {
        public const string ApiRoot = "https://7d2dmods.gg/api";
        public static string GameVersionSlug { get { return "v" + Constants.cVersionMajor; } }
        public static string CatalogDir;      // <mod>\browser
        public static string CacheFile;       // <mod>\browser\catalog.json
        public static string InstalledFile;   // <mod>\browser\installed.json
        public static string TempDir;         // <mod>\browser\tmp
        public static string TrashDir;        // <mod>\browser\trash

        // query state
        public static string Query = "";
        public static int Sort = 0;           // 0 newest, 1 updated, 2 downloads, 3 views
        public static int VersionFilter = 0;  // 0 = V3 only, 1 = all versions
        public static string CategorySlug = "";   // "" = all
        public static int Page = 1;
        public static int Limit = 24;

        public static List<BrowserItem> Items = new List<BrowserItem>();
        public static int Total = 0, TotalPages = 0;
        public static string Status = "ready";
        public static bool Loading = false;
        public static string LastError = "";

        // install state: slug -> status text
        public static readonly ConcurrentDictionary<string, string> InstallState = new ConcurrentDictionary<string, string>();
        // installed map: slug -> { folder, version, title }
        public static ConcurrentDictionary<string, JObject> InstalledMap = new ConcurrentDictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        static int _fetching = 0;
        static int requestedFetch;

        public static readonly string[] SortNames = { "Newest", "Updated", "Downloads", "Views" };
        public static readonly string[] SortSlugs = { "newest", "updated", "downloads", "views" };

        // categories: [0] = display name, [1] = slug (loaded from /v1/metadata)
        public static List<string[]> Categories = new List<string[]>();
        static int _metaFetched = 0;

        // ---------------------------------------------------------------- init
        public static void Init(string modDir)
        {
            try
            {
                CatalogDir = Path.Combine(modDir, "browser");
                CacheFile = Path.Combine(CatalogDir, "catalog.json");
                InstalledFile = Path.Combine(CatalogDir, "installed.json");
                TempDir = Path.Combine(CatalogDir, "tmp");
                TrashDir = Path.Combine(CatalogDir, "trash");
                Directory.CreateDirectory(TempDir);
                Directory.CreateDirectory(TrashDir);
                LoadInstalled();
                LoadCache();
                RequestMetadata();
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            }
            catch (Exception e) { LastError = "init: " + e.Message; }
        }

        // ---------------------------------------------------------------- helpers
        // one-off item fetch by slug (packs); current page first, then the detail API
        public static BrowserItem FetchDetailItem(string slug)
        {
            try
            {
                var items = Items ?? new List<BrowserItem>();
                foreach (var i2 in items) if (string.Equals(i2.slug, slug, StringComparison.OrdinalIgnoreCase)) return i2;
                var det = JObject.Parse(HttpGet(ApiRoot + "/v1/mods/" + Uri.EscapeDataString(slug), 30000));
                var it = new BrowserItem();
                it.slug = S(det["slug"]); it.id = S(det["id"]);
                it.title = S(det["title"]); it.summary = S(det["summary"]);
                it.version = S(det["current_version"]);
                var a = det["author"]; if (a != null) it.author = S(a["display_name"]);
                var th = det["thumbnail"]; if (th != null) it.thumbnail = S(th["url"]);
                var vs = det["game_versions"] as JArray;
                if (vs != null) foreach (var v in vs) it.vers.Add(S(v["slug"]));
                return it;
            }
            catch { return null; }
        }

        static string HttpGet(string url, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "HotReloadTool-ModBrowser/1.0";
            req.Accept = "application/json";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        static string HttpPost(string url, string body, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.UserAgent = "HotReloadTool-ModBrowser/1.0";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            var bytes = Encoding.UTF8.GetBytes(body ?? "{}");
            req.ContentLength = bytes.Length;
            using (var rs = req.GetRequestStream()) rs.Write(bytes, 0, bytes.Length);
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        // metadata (categories) - fire and forget, cached on disk
        public static void RequestMetadata()
        {
            if (Interlocked.CompareExchange(ref _metaFetched, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var metaFile = Path.Combine(CatalogDir, "metadata.json");
                    string text = null;
                    // refresh at most once per day
                    try
                    {
                        if (File.Exists(metaFile) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(metaFile)).TotalHours < 24)
                            text = File.ReadAllText(metaFile, Encoding.UTF8);
                    }
                    catch { }
                    if (text == null)
                    {
                        text = HttpGet(ApiRoot + "/v1/metadata", 20000);
                        try { File.WriteAllText(metaFile, text, Encoding.UTF8); } catch { }
                    }
                    var mj = JObject.Parse(text);
                    var list = new List<string[]>();
                    var cats = mj["categories"] as JArray;
                    if (cats != null)
                        foreach (var c in cats)
                            list.Add(new[] { S(c["name"]), S(c["slug"]) });
                    EnqueueMain(delegate { Categories = list; });
                }
                catch (Exception e) { LastError = "metadata: " + e.Message; }
            });
        }

        // ---------------------------------------------------------------- fetch
        public static void RequestRefresh()
        {
            Interlocked.Increment(ref requestedFetch);
            StartRefresh();
        }
        static void StartRefresh()
        {
            if (Interlocked.CompareExchange(ref _fetching, 1, 0) != 0) { Status = "already loading..."; return; }
            int request = Volatile.Read(ref requestedFetch);
            Loading = true;
            Status = "contacting catalog...";
            var url = BuildListUrl();
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var text = HttpGet(url, 30000);
                    var json = JObject.Parse(text);
                    var list = new List<BrowserItem>();
                    var arr = json["items"] as JArray;
                    if (arr != null)
                    {
                        foreach (var t in arr)
                        {
                            var it = new BrowserItem();
                            it.slug = S(t["slug"]); it.id = S(t["id"]);
                            it.title = S(t["title"]); it.summary = S(t["summary"]);
                            it.version = S(t["current_version"]);
                            it.downloads = I(t["download_count"]); it.sizeBytes = L(t["total_file_size"]);
                            var a = t["author"]; if (a != null) it.author = S(a["display_name"]);
                            var th = t["thumbnail"]; if (th != null) it.thumbnail = S(th["url"]);
                            var cats = t["categories"] as JArray;
                            if (cats != null)
                            {
                                var sb = new StringBuilder();
                                foreach (var c in cats) { if (sb.Length > 0) sb.Append(", "); sb.Append(S(c["name"])); }
                                it.cats = sb.ToString();
                            }
                            var vs = t["game_versions"] as JArray;
                            if (vs != null) foreach (var v in vs) it.vers.Add(S(v["slug"]));
                            var ss = t["server_side"]; if (ss != null) it.serverSide = S(ss["name"]);
                            var up = S(t["updated_at"]); if (up.Length >= 10) it.updated = up.Substring(0, 10);
                            list.Add(it);
                        }
                    }
                    int total = I(json["total"]);
                    int pages = I(json["total_pages"]);
                    EnqueueMain(delegate
                    {
                        if (request != Volatile.Read(ref requestedFetch)) return;
                        Items = list; Total = total; TotalPages = pages;
                        Status = list.Count + " shown  •  " + total + " total";
                        Loading = false;
                        SaveCache();
                    });
                }
                catch (Exception e)
                {
                    var msg = e.Message;
                    EnqueueMain(delegate { if (request == Volatile.Read(ref requestedFetch)) { LastError = msg; Status = "Catalog unavailable: " + msg; Loading = false; } });
                }
                finally
                {
                    Interlocked.Exchange(ref _fetching, 0);
                    EnqueueMain(() => { if (request != Volatile.Read(ref requestedFetch)) StartRefresh(); });
                }
            });
        }

        public static string BuildListUrl()
        {
            var sb = new StringBuilder(ApiRoot + "/v1/mods?limit=" + Limit + "&page=" + Math.Max(1, Page));
            if (VersionFilter == 0) sb.Append("&game_version=").Append(Uri.EscapeDataString(GameVersionSlug));
            if (!string.IsNullOrEmpty(CategorySlug)) sb.Append("&category=").Append(Uri.EscapeDataString(CategorySlug));
            if (!string.IsNullOrEmpty(Query)) sb.Append("&q=").Append(Uri.EscapeDataString(Query));
            sb.Append("&sort=").Append(SortSlugs[Math.Max(0, Math.Min(SortSlugs.Length - 1, Sort))]);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- cache
        static void SaveCache()
        {
            try
            {
                var o = new JObject();
                o["page"] = Page; o["total"] = Total; o["pages"] = TotalPages;
                o["query"] = Query; o["sort"] = Sort; o["vf"] = VersionFilter; o["cat"] = CategorySlug;
                var arr = new JArray();
                foreach (var it in Items)
                {
                    var j = new JObject();
                    j["slug"] = it.slug; j["id"] = it.id; j["title"] = it.title; j["summary"] = it.summary;
                    j["author"] = it.author; j["cats"] = it.cats; j["ss"] = it.serverSide;
                    j["ver"] = it.version; j["size"] = it.sizeBytes; j["dl"] = it.downloads;
                    j["updated"] = it.updated; j["thumb"] = it.thumbnail;
                    var vs = new JArray(); foreach (var v in it.vers) vs.Add(v);
                    j["vers"] = vs;
                    arr.Add(j);
                }
                o["items"] = arr;
                Directory.CreateDirectory(CatalogDir);
                File.WriteAllText(CacheFile, o.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        static void LoadCache()
        {
            try
            {
                if (!File.Exists(CacheFile)) return;
                var o = JObject.Parse(File.ReadAllText(CacheFile, Encoding.UTF8));
                var arr = o["items"] as JArray;
                if (arr == null || arr.Count == 0) return;
                var list = new List<BrowserItem>();
                foreach (var t in arr)
                {
                    var it = new BrowserItem();
                    it.slug = S(t["slug"]); it.id = S(t["id"]); it.title = S(t["title"]);
                    it.summary = S(t["summary"]); it.author = S(t["author"]); it.cats = S(t["cats"]);
                    it.serverSide = S(t["ss"]); it.version = S(t["ver"]);
                    it.sizeBytes = L(t["size"]); it.downloads = I(t["dl"]);
                    it.updated = S(t["updated"]); it.thumbnail = S(t["thumb"]);
                    var vs = t["vers"] as JArray;
                    if (vs != null) foreach (var v in vs) it.vers.Add(S(v));
                    list.Add(it);
                }
                Items = list;
                Total = I(o["total"]); TotalPages = I(o["pages"]);
                Status = "(cached) " + Items.Count + " shown  •  " + Total + " total";
            }
            catch { }
        }

        // ---------------------------------------------------------------- installed registry
        public static void LoadInstalled()
        {
            try
            {
                InstalledMap = new ConcurrentDictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
                if (!File.Exists(InstalledFile)) return;
                var o = JObject.Parse(File.ReadAllText(InstalledFile, Encoding.UTF8));
                foreach (var p in o.Properties())
                    InstalledMap[p.Name] = (JObject)p.Value;
            }
            catch { }
        }

        static readonly object registryGate = new object();
        static void SaveInstalled()
        {
            lock (registryGate)
            {
                var o = new JObject();
                foreach (var kv in InstalledMap) o[kv.Key] = kv.Value.DeepClone();
                Directory.CreateDirectory(CatalogDir);
                var temporary = InstalledFile + ".new";
                File.WriteAllText(temporary, o.ToString(), Encoding.UTF8);
                if (File.Exists(InstalledFile)) File.Replace(temporary, InstalledFile, InstalledFile + ".bak");
                else File.Move(temporary, InstalledFile);
            }
        }

        public static string InstalledStatus(BrowserItem it)
        {
            try
            {
                JObject rec;
                if (!InstalledMap.TryGetValue(it.slug, out rec)) return "";
                var folder = S(rec["folder"]);
                var ver = S(rec["version"]);
                if (string.IsNullOrEmpty(folder)) return "";
                var modsRoot = CoreEntry.ModsRoot();
                var full = string.IsNullOrEmpty(modsRoot) ? null : Path.Combine(modsRoot, folder);
                if (full == null || !Directory.Exists(full)) return "";
                if (!string.IsNullOrEmpty(ver) && !string.IsNullOrEmpty(it.version) && ver != it.version)
                    return "update " + ver + " -> " + it.version;
                return "installed";
            }
            catch { return ""; }
        }

        // ---------------------------------------------------------------- install
        // targetFolder != null -> install INTO that existing folder (update/reinstall in
        // place, preserving the folder name so the installed list stays clean).
        public static void RequestInstall(BrowserItem it) { RequestInstall(it, null, null); }

        public static void RequestInstall(BrowserItem it, string targetFolder) { RequestInstall(it, targetFolder, null); }

        // note: why the install was asked for ("pack:<name>", ...)
        public static void RequestInstall(BrowserItem it, string targetFolder, string note)
        {
            if (it == null || string.IsNullOrWhiteSpace(it.slug)) return;
            if (FriendSync.Installing || HotReloadCore.RestartRequired) { SetState(it.slug, "Restart the game before installing another mod"); return; }
            var roots = ModScanner.Roots();
            installQueue.Enqueue(it.slug, () => InstallWorker(it, targetFolder, note, roots), () => InstallState[it.slug] = "queued...");
        }

        // one install at a time: the site issues one download token per request with a
        // courtesy delay per file - parallel installs would starve each other
        static readonly InstallQueue installQueue = new InstallQueue();

        static readonly ConcurrentDictionary<string, string> _updateTarget = new ConcurrentDictionary<string, string>();

        static void SetState(string slug, string s) { InstallState[slug] = s; }

        static void InstallWorker(BrowserItem it, string targetFolder, string note, List<string> roots)
        {
            string stage = null, bundleRoot = null, bundle = null;
            try
            {
                if (FriendSync.Installing || HotReloadCore.RestartRequired) throw new InvalidOperationException("Restart the game before installing another mod");
                SetState(it.slug, "fetching details...");
                var detText = HttpGet(ApiRoot + "/v1/mods/" + Uri.EscapeDataString(it.slug), 30000);
                var det = JObject.Parse(detText);
                var gameVersions = det["game_versions"] as JArray;
                if (gameVersions != null && gameVersions.Count > 0 && !System.Linq.Enumerable.Any(gameVersions, v => (v.Type == JTokenType.String ? S(v) : S(v["slug"])) == GameVersionSlug))
                    throw new InvalidDataException("This catalog mod does not support game " + GameVersionSlug);
                var modId = S(det["id"]); if (string.IsNullOrEmpty(modId)) modId = it.id;
                string fileId = "", fileName = "";
                var files = det["mod_files"] as JArray;
                if (files != null)
                {
                    foreach (var f in files)
                    {
                        var ft = S(f["file_type"]);
                        if (ft == "main" || string.IsNullOrEmpty(fileId))
                        { fileId = S(f["id"]); fileName = S(f["filename"]); }
                    }
                }
                if (string.IsNullOrEmpty(fileId)) { SetState(it.slug, "error: no files"); return; }
                if (string.IsNullOrEmpty(fileName)) fileName = it.slug + ".zip";

                SetState(it.slug, "requesting download...");
                var reqText = HttpPost(ApiRoot + "/v1/mods/" + modId + "/files/" + fileId + "/download", "{}", 30000);
                var reqJson = JObject.Parse(reqText);
                var token = S(reqJson["token"]);
                int waitMs = I(reqJson["wait_ms"]);
                if (string.IsNullOrEmpty(token)) { SetState(it.slug, "error: no token"); return; }

                if (waitMs > 0) { SetState(it.slug, "waiting " + (waitMs / 1000) + "s (site courtesy delay)..."); Thread.Sleep(waitMs + 500); }

                SetState(it.slug, "claiming link...");
                var claimText = HttpGet(ApiRoot + "/v1/mods/downloads/" + token, 30000);
                var url = S(JObject.Parse(claimText)["url"]);
                if (string.IsNullOrEmpty(url)) { SetState(it.slug, "error: no download url"); return; }

                var zipPath = Path.Combine(TempDir, SafeName(it.slug) + ".zip");
                SetState(it.slug, "downloading...");
                DownloadFile(url, zipPath);

                SetState(it.slug, "extracting...");
                stage = Path.Combine(TempDir, Guid.NewGuid().ToString("N") + "_x");
                try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
                Directory.CreateDirectory(stage);
                ModFiles.ExtractBrowserZip(zipPath, stage);

                var installDir = DecideInstallDir(stage, it.title, it.slug);
                var modsRoot = roots.Count > 0 ? roots[0] : null;
                if (string.IsNullOrEmpty(modsRoot)) { SetState(it.slug, "error: no Mods folder"); return; }

                // update/reinstall in place? use the recorded folder (or the caller's target)
                string existing = targetFolder;
                { string t2; if (_updateTarget.TryGetValue(it.slug, out t2) && !string.IsNullOrEmpty(t2)) existing = t2; }
                if (existing == null)
                {
                    JObject rec0;
                    if (InstalledMap.TryGetValue(it.slug, out rec0)) { var f0 = S(rec0["folder"]); if (!string.IsNullOrEmpty(f0)) existing = f0; }
                }
                if (existing != null && ModFiles.Protected(existing)) throw new InvalidDataException("Protected mod folder");
                if (existing != null) ModFiles.Child(modsRoot, existing);

                var folder = existing ?? Path.GetFileName(installDir);
                if (string.Equals(installDir, stage, StringComparison.OrdinalIgnoreCase)) folder = existing ?? SafeName(it.slug);
                if (ModFiles.Protected(folder)) throw new InvalidDataException("Protected mod folder");
                bundleRoot = Path.Combine(TempDir, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(bundleRoot);
                CopyDir(installDir, ModFiles.Child(bundleRoot, folder));
                bundle = bundleRoot + ".hrpack";
                var bundleInfo = ModFiles.Export(bundleRoot, bundle, it.title, FriendSync.GameVersion);
                if (((JArray)bundleInfo["mods"]).Count != 1) throw new InvalidDataException("Cannot install a protected or invalid mod through the browser");
                // Atomic replacement with rollback, rather than deleting the old files.
                HotReloadCore.RunOnMainThreadWithResult(() =>
                {
                    HotReloadCore.SetWatch(false);
                    if (existing != null) HotReloadCore.UnloadModReport(existing);
                    return true;
                });
                try { ModFiles.Install(bundle, roots, Path.Combine(CatalogDir, "backups"), FriendSync.GameVersion, false); }
                finally { HotReloadCore.RunOnMainThreadWithResult(() => { if (!HotReloadCore.RestartRequired) HotReloadCore.SetWatch(true); return true; }); }
                var rec = new JObject();
                rec["folder"] = folder;
                rec["version"] = S(det["current_version"]);
                rec["title"] = it.title;
                rec["when"] = DateTime.UtcNow.ToString("o");
                InstalledMap[it.slug] = rec; SaveInstalled();
                var loadResult = HotReloadCore.RunOnMainThreadWithResult(() =>
                {
                    FriendSync.HostModsChanged();
                    return HotReloadCore.ReloadModsReport(null);
                });
                SetState(it.slug, "done: " + folder + " (" + loadResult + "; restart if this mod requires startup loading)");

            }
            catch (Exception e)
            {
                SetState(it.slug, "error: " + e.Message);
            }
            finally
            {
                try { File.Delete(Path.Combine(TempDir, SafeName(it.slug) + ".zip")); } catch { }
                try { if (stage != null && Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
                try { if (bundleRoot != null && Directory.Exists(bundleRoot)) Directory.Delete(bundleRoot, true); if (bundle != null) File.Delete(bundle); } catch { }
                BrowserUi.InvalidateInstalled();
            }
        }

        // a pack code waiting to be applied once the current install drains
        public static string PendingPack;

        // ---- out-of-band thumbnail resolution (Downloaded pane)
        // the UI asks for pics of installed slugs that are not in the current page; one
        // background worker resolves them against the catalog (detail endpoint), then
        // hands the url back to the UI on the main thread
        static readonly ConcurrentQueue<string> _wantThumbs = new ConcurrentQueue<string>();
        static readonly HashSet<string> _wantSeen = new HashSet<string>(StringComparer.Ordinal);
        static int _wantWorker;

        public static void WantThumb(string slug)
        {
            if (string.IsNullOrEmpty(slug) || !_wantSeen.Add(slug)) return;
            _wantThumbs.Enqueue(slug);
            if (Interlocked.CompareExchange(ref _wantWorker, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(delegate { WantThumbWorker(); });
        }

        static void WantThumbWorker()
        {
            try
            {
                while (true)
                {
                    string slug;
                    if (!_wantThumbs.TryDequeue(out slug)) return;
                    var it = FetchDetailItem(slug);
                    if (it != null && !string.IsNullOrEmpty(it.thumbnail))
                    {
                        var u2 = it.thumbnail;
                        EnqueueMain(delegate { BrowserUi.ThumbUrl(slug, u2); });
                    }
                }
            }
            finally { Interlocked.Exchange(ref _wantWorker, 0); }
        }

        static void PackAutoApply()
        {
            var code = PendingPack;
            if (string.IsNullOrEmpty(code)) return;
            var p = ModPack.Decode(code);
            if (p == null) { PendingPack = null; return; }
            PendingPack = null;
            var rep = ModPack.ApplyAll(p, "pack:" + p.name);
            Log.Out("[HotReload] " + rep.Replace("\n", " | "));
        }

        static void DownloadFile(string url, string dest)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "HotReloadTool-ModBrowser/1.0";
            req.Timeout = 60000; req.ReadWriteTimeout = 120000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var s = resp.GetResponseStream())
            using (var f = File.Create(dest))
            {
                var buf = new byte[65536];
                int n; long got = 0;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    f.Write(buf, 0, n); got += n;
                    if (got % (1024 * 1024) < 65536) { } // keep it simple; no progress spam
                }
            }
        }

        // find the folder that actually contains ModInfo.xml (zips vary)
        static string DecideInstallDir(string stage, string title, string slug)
        {
            if (File.Exists(Path.Combine(stage, "ModInfo.xml"))) return stage;
            var infos = Directory.GetFiles(stage, "ModInfo.xml", SearchOption.AllDirectories);
            if (infos.Length != 1) throw new InvalidDataException("Expected one mod folder; found " + infos.Length + ". Use portable packs for a collection.");
            return Path.GetDirectoryName(infos[0]);
        }

        static string UniqueDir(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
            for (int i = 2; i < 100; i++)
            {
                var cand = path + " (" + i + ")";
                if (!Directory.Exists(cand)) return cand;
            }
            return path + " (" + DateTime.Now.Ticks % 100000 + ")";
        }

        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        // UI-facing alias (pack panel "make from installed")
        public static ModPack.Pack PackFromInstalledUi() { return PackFromInstalled("my-pack"); }

        // build a pack from every browser-installed (tracked) mod
        static ModPack.Pack PackFromInstalled(string name)
        {
            var p = new ModPack.Pack { name = name };
            foreach (var kv in InstalledMap) if (!string.IsNullOrEmpty(kv.Key)) p.slugs.Add(kv.Key);
            return p.slugs.Count > 0 ? p : null;
        }

        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "mod";
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ' ') sb.Append(c);
                else sb.Append('_');
            }
            var r = sb.ToString().Trim().Trim('.');

            return r.Length == 0 ? "mod" : r;
        }

        // ---------------------------------------------------------------- installed list (for the UI/manage view)
        public class InstalledInfo
        {
            public string folder = "", root = "", name = "", version = "", state = "", slug = "", title = "";
            public bool required;
        }

        public static List<InstalledInfo> ListInstalled()
        {
            var res = new List<InstalledInfo>();
            try
            {
                var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    var lm = ModManager.GetLoadedMods();
                    if (lm != null) foreach (var m in lm) if (m != null && !string.IsNullOrEmpty(m.Name)) { loaded.Add(m.Name); loaded.Add(m.FolderName); }
                }
                catch { }
                foreach (var root in ModScanner.Roots())
                {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (!File.Exists(Path.Combine(dir, "ModInfo.xml"))) continue;
                    var info = new InstalledInfo();
                    info.root = root;
                    info.folder = Path.GetFileName(dir);
                    info.state = loaded.Contains(info.folder) ? "loaded" : "not loaded";
                    try
                    {
                        var xmlPath = Path.Combine(dir, "ModInfo.xml");
                        if (File.Exists(xmlPath))
                        {
                            var txt = File.ReadAllText(xmlPath);
                            info.name = XmlVal(txt, "DisplayName"); if (string.IsNullOrEmpty(info.name)) info.name = XmlVal(txt, "Name");
                            info.version = XmlVal(txt, "Version");
                            info.required = ModFiles.Protected(info.folder) || ModFiles.Protected(XmlVal(txt, "Name"));
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(info.name)) info.name = info.folder;
                    foreach (var kv in InstalledMap)
                    {
                        var f = S(kv.Value["folder"]);
                        if (string.Equals(f, info.folder, StringComparison.OrdinalIgnoreCase))
                        { info.slug = kv.Key; info.title = S(kv.Value["title"]); break; }
                    }
                    res.Add(info);
                }
                }
            }
            catch { }
            return res;
        }

        static string XmlVal(string xml, string tag)
        {
            try
            {
                var i = xml.IndexOf("<" + tag, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return "";
                var j = xml.IndexOf("value=\"", i, StringComparison.OrdinalIgnoreCase);
                if (j < 0) return "";
                j += 7;
                var k = xml.IndexOf('"', j);
                return k < 0 ? "" : xml.Substring(j, k - j);
            }
            catch { return ""; }
        }

        public static string RemoveInstalled(string folder, string requestedRoot = null)
        {
            try
            {
                var root = requestedRoot ?? CoreEntry.ModsRoot();
                if (string.IsNullOrEmpty(root)) return "no Mods folder";
                if (!ModScanner.Roots().Exists(x => string.Equals(Path.GetFullPath(x), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))) return "Invalid Mods location";
                if (ModFiles.Protected(folder)) return "This mod is required and cannot be removed here";
                var dir = ModFiles.Child(root, folder);
                if (!Directory.Exists(dir)) return "not found: " + folder;
                if (File.Exists(Path.Combine(dir, "ModInfo.xml")) && ModFiles.Protected(XmlVal(File.ReadAllText(Path.Combine(dir, "ModInfo.xml")), "Name"))) return "This mod is required and cannot be removed here";
                // try a clean unload first (drops XML patches + sweeps Harmony patches)
                HotReloadCore.RunOnMainThreadWithResult(() => HotReloadCore.UnloadModReport(folder));
                var dest = Path.Combine(TrashDir, folder + "_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(TrashDir);
                Directory.Move(dir, dest);
                foreach (var kv in new List<KeyValuePair<string, JObject>>(InstalledMap))
                    if (string.Equals(S(kv.Value["folder"]), folder, StringComparison.OrdinalIgnoreCase))
                    { JObject removed; InstalledMap.TryRemove(kv.Key, out removed); }
                SaveInstalled();
                BrowserUi.InvalidateInstalled();
                FriendSync.HostModsChanged();
                return "removed " + folder + " (moved to browser\\trash)";
            }
            catch (Exception e) { return "remove failed: " + e.Message; }
        }

        // Background completions schedule one native main-thread task.
        public static void EnqueueMain(Action action)
        {
            HotReloadCore.RunOnMainThread(() =>
            {
                try { action(); }
                catch (Exception e) { Log.Warning("[HotReload] browser completion: " + e.Message); }
            });
        }

        // ---------------------------------------------------------------- selfcheck (no Unity deps: safe offline)
        public static string SelfCheck(string slug)
        {
            var sb = new StringBuilder();
            try
            {
                var meta = HttpGet(ApiRoot + "/v1/metadata", 20000);
                var mj = JObject.Parse(meta);
                sb.AppendLine("metadata: ok, " + (mj["categories"] as JArray).Count + " categories, " + (mj["game_versions"] as JArray).Count + " versions");

                var list = HttpGet(ApiRoot + "/v1/mods?limit=3&game_version=v3&sort=newest", 20000);
                var lj = JObject.Parse(list);
                sb.AppendLine("list(v3): ok, total=" + S(lj["total"]) + " first=" + S(((JArray)lj["items"])[0]["title"]));

                var one = (JObject)((JArray)lj["items"])[0];
                var detTxt = HttpGet(ApiRoot + "/v1/mods/" + S(one["slug"]), 20000);
                var det = JObject.Parse(detTxt);
                var files = det["mod_files"] as JArray;
                sb.AppendLine("detail: ok, files=" + (files == null ? 0 : files.Count) + " version=" + S(((JObject)det["current_version"])["version"]));

                if (files != null && files.Count > 0 && slug == "__pipeline__")
                {
                    var modId = S(det["id"]); var fileId = S(files[0]["id"]);
                    var rt = HttpPost(ApiRoot + "/v1/mods/" + modId + "/files/" + fileId + "/download", "{}", 20000);
                    var tok = S(JObject.Parse(rt)["token"]);
                    int wait = I(JObject.Parse(rt)["wait_ms"]);
                    sb.AppendLine("download token: " + (string.IsNullOrEmpty(tok) ? "FAIL" : "ok, wait_ms=" + wait));
                    if (!string.IsNullOrEmpty(tok))
                    {
                        if (wait > 0) Thread.Sleep(wait + 500);
                        var cl = HttpGet(ApiRoot + "/v1/mods/downloads/" + tok, 20000);
                        var url = S(JObject.Parse(cl)["url"]);
                        sb.AppendLine("claim: " + (string.IsNullOrEmpty(url) ? "FAIL" : "ok (signed url received)"));
                    }
                }
                if (!string.IsNullOrEmpty(slug) && slug != "__pipeline__")
                {
                    var d2 = JObject.Parse(HttpGet(ApiRoot + "/v1/mods/" + Uri.EscapeDataString(slug), 20000));
                    sb.AppendLine("slug '" + slug + "': " + S(d2["title"]) + "  v" + S(((JObject)d2["current_version"])["version"]));
                }
                sb.Append("SELFCHECK-OK");
            }
            catch (Exception e) { sb.Append("SELFCHECK-FAIL: " + e.Message); }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- console
        public static string ConsoleCmd(List<string> args)
        {
            string sub = args.Count > 1 ? args[1].ToLowerInvariant() : "status";
            switch (sub)
            {
                case "status":
                    return "[browser] " + Status + "  | " + Items.Count + " items, page " + Page + "/" + Math.Max(1, TotalPages)
                        + ", " + InstalledMap.Count + " installed via browser"
                        + (string.IsNullOrEmpty(LastError) ? "" : "  | last error: " + LastError);
                case "list":
                    {
                        var sb = new StringBuilder("[browser] " + Items.Count + " items:");
                        for (int i = 0; i < Items.Count; i++)
                        {
                            var it = Items[i];
                            string extra = InstallState.ContainsKey(it.slug) ? "  [" + InstallState[it.slug] + "]" : "";
                            var inst = InstalledStatus(it);
                            if (!string.IsNullOrEmpty(inst)) extra += "  [" + inst + "]";
                            sb.Append("\n  " + (i + 1) + ". " + it.title + "  (" + it.VersText() + ", " + it.SizeText() + ")" + extra);
                        }
                        return sb.ToString();
                    }
                case "close":
                    BrowserUi.ShotCloseAfter = false;
                    BrowserUi.Close();
                    return "[browser] window closed";
                case "pack":
                {
                    if (args.Count < 3) return "[pack] usage: pack make <name> | pack apply <code|name> | pack save <name> [code] | pack list | pack delete <name>";
                    var act = args[2].ToLowerInvariant();
                    if (act == "export") return FriendSync.ExportPack(args.Count > 3 ? string.Join(" ", args.GetRange(3, args.Count - 3).ToArray()) : "my-pack");
                    if (act == "import") return FriendSync.ImportPack(args.Count > 3 ? string.Join(" ", args.GetRange(3, args.Count - 3).ToArray()).Trim('"') : "");
                    if (act == "make")
                    {
                        var p = PackFromInstalled(args.Count > 3 ? args[3] : "my-pack");
                        if (p == null) return "[pack] no browser-installed mods found";
                        var code = ModPack.Encode(p);
                        ModPack.SaveFile(p);
                        return "[pack] " + p.slugs.Count + " mods  code: " + code;
                    }
                    if (act == "apply")
                    {
                        var code = args.Count > 3 ? args[3] : "";
                        if (!code.StartsWith("HRP1-"))
                            foreach (var sp in ModPack.LoadFiles())
                                if (string.Equals(sp.name, code, StringComparison.OrdinalIgnoreCase)) { code = ModPack.Encode(sp); break; }
                        var p = ModPack.Decode(code);
                        if (p == null) return "[pack] not a valid pack code (or saved pack name)";

                        return ModPack.ApplyAll(p, "pack:" + p.name);
                    }
                    if (act == "save")
                    {
                        var name = args.Count > 3 ? args[3] : "pack";
                        var code = args.Count > 4 ? args[4] : "";
                        ModPack.Pack p;
                        if (code.StartsWith("HRP1-")) { p = ModPack.Decode(code); if (p != null) p.name = name; }
                        else p = PackFromInstalled(name);
                        if (p == null) return "[pack] nothing to save";
                        return ModPack.SaveFile(p);
                    }
                    if (act == "delete") return ModPack.DeleteFile(args.Count > 3 ? args[3] : "");
                    if (act == "list")
                    {
                        var ps = ModPack.LoadFiles();
                        if (ps.Count == 0) return "[pack] no saved packs";
                        var sb2 = new StringBuilder("[pack] " + ps.Count + " saved:");
                        foreach (var sp in ps) sb2.Append("\n  " + sp.name + " (" + sp.slugs.Count + " mods)");
                        return sb2.ToString();
                    }
                    return "[pack] usage: pack make <name> | pack apply <code|name> | pack save <name> [code] | pack list | pack delete <name>";
                }
                case "sync": return FriendSync.Status;
                case "serverpack": return FriendSync.Status;
                case "shot":
                    // self-sufficient: open the window if it is closed so a frame renders,
                    // capture on the next OnGUI, then close again
                    if (!BrowserUi.IsOpen) { BrowserUi.Open(); BrowserUi.ShotCloseAfter = true; }
                    BrowserUi.ShotSlug = args.Count > 2 ? args[2] : "page";
                    return "[browser] screenshot requested (visible in ~1s at <mod>\\browser\\cache\\_shot.png)";
                case "search":
                    {
                        var q = args.Count > 2 ? string.Join(" ", args.GetRange(2, args.Count - 2).ToArray()) : "";
                        Query = q; Page = 1; RequestRefresh();
                        return "[browser] searching: " + (string.IsNullOrEmpty(q) ? "(all)" : q) + " ...";
                    }
                case "page":
                    {
                        int p; if (args.Count > 2 && int.TryParse(args[2], out p)) { Page = Math.Max(1, p); RequestRefresh(); return "[browser] page " + Page + " ..."; }
                        return "[browser] usage: hr browser page <n>";
                    }
                case "sort":
                    {
                        if (args.Count > 2)
                        {
                            for (int i = 0; i < SortNames.Length; i++)
                                if (string.Equals(SortNames[i], args[2], StringComparison.OrdinalIgnoreCase)) { Sort = i; RequestRefresh(); return "[browser] sort: " + SortNames[i]; }
                        }
                        return "[browser] sort options: " + string.Join(", ", SortNames);
                    }
                case "all":
                    VersionFilter = 1; Page = 1; RequestRefresh();
                    return "[browser] showing ALL game versions (compat may vary)";
                case "v3":
                    VersionFilter = 0; Page = 1; RequestRefresh();
                    return "[browser] showing V3-compatible mods only";
                case "installed":
                    {
                        var sb = new StringBuilder("[browser] installed mods:");
                        foreach (var i in ListInstalled())
                            sb.Append("\n  - " + i.folder + "  v" + i.version + "  [" + i.state + "]" + (string.IsNullOrEmpty(i.slug) ? "" : "  (browser: " + i.slug + ")"));
                        return sb.ToString();
                    }
                case "install":
                    {
                        if (args.Count < 3) return "[browser] usage: hr browser install <slug|#>";
                        string key = args[2];
                        int idx;
                        BrowserItem it = null;
                        if (int.TryParse(key, out idx) && idx >= 1 && idx <= Items.Count) it = Items[idx - 1];
                        else foreach (var x in Items) if (string.Equals(x.slug, key, StringComparison.OrdinalIgnoreCase)) { it = x; break; }
                        if (it == null) return "[browser] not found in current list: " + key;
                        RequestInstall(it);
                        return "[browser] installing '" + it.title + "' ... watch: hr browser status / log";
                    }
                case "remove":
                    if (args.Count < 3) return "[browser] usage: hr browser remove <folder>";
                    return "[browser] " + RemoveInstalled(args[2]);
                case "selfcheck":
                    return "[browser] " + SelfCheck(args.Count > 2 ? args[2] : null);
                case "open":
                    return BrowserUi.OpenFromConsole();
                default:
                    return "[browser] commands: status | list | search <q> | page <n> | sort <" + string.Join("|", SortNames) + "> | v3 | all | installed | install <slug|#> | remove <folder> | selfcheck [slug|__pipeline__] | open";
            }
        }

        // small JSON helpers
        internal static string S(JToken t) { try { return t == null ? "" : t.ToString(); } catch { return ""; } }
        internal static int I(JToken t) { try { return t == null ? 0 : (int)t; } catch { return 0; } }
        internal static long L(JToken t) { try { return t == null ? 0 : (long)t; } catch { return 0; } }
    }
}

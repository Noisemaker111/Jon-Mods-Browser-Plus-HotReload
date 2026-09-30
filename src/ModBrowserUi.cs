// ModBrowserUi.cs - the in-game Mods window (split view: Downloaded | Browse).
//
// Layout (modelled on the user's mockup):
//   +--------------------------------------------------------------+
//   | Mods                                   [ search...        ] X |
//   +---------------------+----------------------------------------+
//   |    Downloaded (n)   |   Browse   [chips row......][sort][↻]   |
//   |  [pic] Title    X   |  [pic] Title                    [ver]   |
//   |        desc  [Inst] |        summary             [download]   |
//   +---------------------+----------------------------------------+
//
// Rendering: IMGUI overlay drawn from a Harmony postfix on GUIWindowManager.OnGUI
// (the game's window manager is a MonoBehaviour; its OnGUI drives all GUIWindow.OnGUI
// calls, so a postfix runs on the main thread with a valid GUI context).
//
// Entry points:
//   - "MODS" button injected into the main menu grid (Config/XUi_Menu/windows.xml)
//   - "MODS" button injected into the in-game ESC menu (Config/XUi_InGame/windows.xml)
//   - console: hr browser open
//
// Card pics: WebP thumbnails stream from the catalog (Unity 2022.2+ Texture2D.LoadImage
// decodes WebP natively), cached on disk, lazily fetched, hard-capped per open (low data).
// "hr browser shot" saves a real PNG of the rendered window to <mod>\browser\cache\_shot.png
// so the UI can be verified remotely (chat) - the agent reads that file and sees the window.
//
// IMGUI pixel note: Texture2D.SetPixels/GetPixels index row 0 as the BOTTOM row, and
// LoadImage fills memory top-down, so hand-drawn icons and loaded images each need their
// pixels flipped once or they render vertically mirrored (the "flipped X/magnifier" bug).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace HotReloadTool
{
    public static class BrowserUi
    {
        public const string HarmonyId = "hotreload.browser";

        public static bool IsOpen = false;

        // ---- input state
        static string _search = "";
        static System.Threading.Timer searchTimer;
        static bool _sortOpen = false;
        static bool _packOpen = false;          // header PACK panel state
        static string _packName = "my-pack";
        public static void InvalidateInstalled() { _installed = null; }
        static string _packBox = "";            // paste-a-code box
        static Vector2 _packScroll = Vector2.zero;
        static Vector2 _leftScroll = Vector2.zero;
        static Vector2 _rightScroll = Vector2.zero;
        static float _lastLeftY, _lastRightY;   // virtualization: last scroll offsets
        static int _dirL, _dirR;                // virtualization: scroll direction (-1 up, 1 down)
        static Vector2 _chipScroll = Vector2.zero;
        static float _chipContentW = 0f;

        static List<ModBrowser.InstalledInfo> _installed = null;
        static double _installedWhen = -100;
        static string _confirmRemove = null;

        // ---- styles / textures
        static bool _stylesReady;
        static Texture2D _texWin, _texCard, _texCardAlt, _texAccent, _texGreen, _texOrange, _texRed, _texField, _texBand, _texLine, _texWhite, _texMag, _texNoPic;
        static GUIStyle _box, _h1, _h2, _lbl, _lblDim, _lblTiny, _btn, _btnTiny, _field, _badge, _chipOn, _chipOff, _chipOnSmall, _chipOffSmall, _xBtn, _invisible;

        // ---- card thumbnails (lazy, disk-cached, capped)
        const int THUMB_MAX = 90;        // max fetches per window-open: hard data cap
        const int THUMB_INFLIGHT = 8;    // max simultaneous thumbnail downloads
        const int THUMB_PREFETCH = 6;    // rows fetched ahead of the scroll direction
        static readonly Dictionary<string, Texture2D> _thumbs = new Dictionary<string, Texture2D>();
        static readonly HashSet<string> _thumbBusy = new HashSet<string>(StringComparer.Ordinal);
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _thumbFailed = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        static readonly HashSet<string> _flipped = new HashSet<string>(StringComparer.Ordinal);
        static int _thumbBudget = THUMB_MAX;
        static int _thumbLogs = 14;   // bounded diagnostics for the thumb pipeline

        // set by "hr browser shot": the next rendered frame is saved as a PNG for remote verification
        public static string ShotSlug = null;
        public static bool ShotCloseAfter = false;

        static bool _cursorForced;
        static bool _prevCursorVisible;

        static Harmony _harmony;

        // live wiring: controllers already built when the patch lands (core hot-swap while
        // the game is running) never fire the Init postfix - sweep the XUi tree for them.
        static readonly HashSet<XUiController> _wiredControllers = new HashSet<XUiController>();
        static int _clickLogs;   // bounded diagnostics: log the first N clicks per open
        static int _evLogs;      // bounded diagnostics: log the first N mouse events per open

        // ------------------------------------------------------------------ init

        // called by the core at init: install the render + menu-button patches
        public static void Init()
        {
            try
            {
                if (_harmony == null) _harmony = new Harmony(HarmonyId);

                var onGui = AccessTools.Method(typeof(GUIWindowManager), "OnGUI");
                if (onGui != null)
                {
                    var post = typeof(BrowserUi).GetMethod("OnGuiPostfix", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    _harmony.Patch(onGui, postfix: new HarmonyMethod(post));
                    Log.Out("[HotReload] browser: render hook installed");
                }
                else Log.Warning("[HotReload] browser: GUIWindowManager.OnGUI not found (UI will not render)");

                InstallMenuPatches();
                InstallOpenPatches();
                HotReloadCore.RunOnMainThread(() => TryWireLive());
            }
            catch (Exception e) { Log.Error("[HotReload] browser ui init: " + e); }
        }

        public static void Shutdown()
        {
            if (searchTimer != null) { searchTimer.Dispose(); searchTimer = null; }
            try { Close(); } catch { }
            try { if (_harmony != null) _harmony.UnpatchSelf(); } catch { }
            Log.Out("[HotReload] browser ui shutdown");
        }

        static void InstallMenuPatches()
        {
            try
            {
                PatchInit(typeof(XUiC_MainMenuButtons), "MenuButtonsInitPostfix");
                PatchInit(typeof(XUiC_InGameMenuWindow), "InGameMenuInitPostfix");
            }
            catch (Exception e) { Log.Warning("[HotReload] browser: menu patch: " + e.Message); }
        }

        static void PatchInit(Type t, string postName)
        {
            try
            {
                var init = t.GetMethod("Init", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (init == null) { Log.Warning("[HotReload] browser: " + t.Name + ".Init not found"); return; }
                var post = typeof(BrowserUi).GetMethod(postName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                _harmony.Patch(init, postfix: new HarmonyMethod(post));
            }
            catch (Exception e) { Log.Warning("[HotReload] browser: patch " + t.Name + ": " + e.Message); }
        }

        // Wire on menu lifecycle events, including menus created after a core swap.
        static void InstallOpenPatches()
        {
            var targets = new HashSet<System.Reflection.MethodInfo>();
            foreach (var type in new[] { typeof(XUiC_MainMenuButtons), typeof(XUiC_InGameMenuWindow) })
            {
                var method = AccessTools.Method(type, "OnOpen");
                if (method != null && targets.Add(method)) _harmony.Patch(method, postfix: new HarmonyMethod(typeof(BrowserUi), "MenuOpened"));
            }
        }

        public static void MenuOpened(XUiController __instance)
        {
            if (__instance is XUiC_MainMenuButtons || __instance is XUiC_InGameMenuWindow) TryWireUnder(__instance);
        }

        // walk every window group's controller tree looking for our injected 'btnMods'
        public static int TryWireLive()
        {
            int wired = 0;
            try
            {
                var ui = LocalPlayerUI.primaryUI;
                if (ui == null) return 0;
                var xui = ui.xui;
                if (xui == null) return 0;

                if (xui.WindowGroups != null)
                {
                    foreach (var g in xui.WindowGroups)
                    {
                        if (g == null || g.Controller == null) continue;
                        var c = FindById(g.Controller, "btnMods", true) ?? FindById(g.Controller, "btnMods", false);
                        if (c != null && WireOne(c)) wired++;
                    }
                }

                if (xui.windowsAndGroupsByControllerType != null)
                {
                    foreach (var kv in xui.windowsAndGroupsByControllerType)
                    {
                        if (kv.Value == null) continue;
                        foreach (var root in kv.Value)
                        {
                            if (root == null) continue;
                            var c = FindById(root, "btnMods", true) ?? FindById(root, "btnMods", false);
                            if (c != null && WireOne(c)) wired++;
                        }
                    }
                }
            }
            catch (Exception e) { Log.Warning("[HotReload] browser wire sweep: " + e.Message); }
            return wired;
        }

        static void TryWireUnder(XUiController root)
        {
            if (root == null) return;
            try
            {
                var c = FindById(root, "btnMods", true) ?? FindById(root, "btnMods", false);
                if (c != null) WireOne(c);
            }
            catch { }
        }

        static XUiController FindById(XUiController root, string id, bool requireButton)
        {
            try
            {
                if (root == null) return null;
                var vc = root.ViewComponent;
                bool idMatch = vc != null && !string.IsNullOrEmpty(vc.ID) && string.Equals(vc.ID, id, StringComparison.OrdinalIgnoreCase);
                if (idMatch && (!requireButton || root is XUiC_Button || root is XUiC_SimpleButton)) return root;
                var kids = root.Children;
                if (kids != null)
                {
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var r = FindById(kids[i], id, requireButton);
                        if (r != null) return r;
                    }
                }
            }
            catch { }
            return null;
        }

        static bool WireOne(XUiController c)
        {
            try
            {
                if (c == null) return false;
                // if the id-matched controller isn't itself a button, wire the button at-or-under
                // it (vanilla does the same with GetChildByType<XUiC_SimpleButton>())
                var target = (c is XUiC_Button || c is XUiC_SimpleButton) ? c : (FindButtonAtOrUnder(c) ?? c);
                if (_wiredControllers.Contains(target)) return false;
                PurgeForeignHandlers(target, "OnPress");
                PurgeForeignHandlers(target, "OnPressed");

                var sb = target as XUiC_SimpleButton;
                if (sb != null) { sb.OnPressed -= OnInGameModsPressed; sb.OnPressed += OnInGameModsPressed; }
                else
                {
                    var b = target as XUiC_Button;
                    if (b != null) { b.OnPress -= OnMainMenuModsPressed; b.OnPress += OnMainMenuModsPressed; }
                    else { target.OnPress -= OnMainMenuModsPressed; target.OnPress += OnMainMenuModsPressed; }
                }
                try { RelabelMods(target); } catch { }
                _wiredControllers.Add(target);
                Log.Out("[HotReload] browser: btnMods wired (" + target.GetType().Name + ")");
                return true;
            }
            catch (Exception e) { Log.Warning("[HotReload] browser wire: " + e.Message); return false; }
        }

        // the injected caption comes from XML (applied at window rebuild); when the window was
        // built before this core hot-swapped in, relabel the live child label so both states read
        // the same ("MODS")
        static void RelabelMods(XUiController button)
        {
            try
            {
                // the label-backed button carries its own XUiV_Label (main menu: the injected
                // mainmenubutton IS a Button-controller label); simplebutton templates nest a
                // child label named btnLabel. Handle both.
                var own = button.ViewComponent as XUiV_Label;
                if (own != null) own.Text = "MODS";
                var kids = button.Children;
                if (kids == null) return;
                for (int i = 0; i < kids.Count; i++)
                {
                    var view = kids[i] != null ? kids[i].ViewComponent as XUiV_Label : null;
                    if (view != null) view.Text = "MODS";
                }
            }
            catch { }
        }

        static XUiController FindButtonAtOrUnder(XUiController root)
        {
            try
            {
                if (root == null) return null;
                if (root is XUiC_Button || root is XUiC_SimpleButton) return root;
                var kids = root.Children;
                if (kids != null)
                {
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var r = FindButtonAtOrUnder(kids[i]);
                        if (r != null) return r;
                    }
                }
            }
            catch { }
            return null;
        }

        // a core hot-swap leaves the OLD core's handler attached (event subscriptions
        // survive Harmony unpatching). Drop anything that isn't from THIS assembly.
        static void PurgeForeignHandlers(XUiController c, string eventName)
        {
            try
            {
                var f = FindEventField(c.GetType(), eventName);
                if (f == null) return;
                var del = f.GetValue(c) as Delegate;
                if (del == null) return;
                var myAsm = typeof(BrowserUi).Assembly;
                var remove = c.GetType().GetMethod("remove_" + eventName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (remove == null) return;
                var entries = del.GetInvocationList();
                for (int i = 0; i < entries.Length; i++)
                {
                    var dt = entries[i].Method != null ? entries[i].Method.DeclaringType : null;
                    if (dt == null || dt.Assembly != myAsm)
                    {
                        try { remove.Invoke(c, new object[] { entries[i] }); } catch { }
                    }
                }
            }
            catch { }
        }

        static System.Reflection.FieldInfo FindEventField(Type t, string name)
        {
            for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
            {
                var f = cur.GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
                if (f != null && typeof(Delegate).IsAssignableFrom(f.FieldType)) return f;
            }
            return null;
        }

        // ------------------------------------------------------------------ menu wiring

        // main menu: wired onto the injected 'btnMods' mainmenubutton
        public static void MenuButtonsInitPostfix(XUiC_MainMenuButtons __instance)
        {
            try
            {
                XUiC_Button btn = null;
                if (__instance.TryGetChildByIdAndType<XUiC_Button>("btnMods", out btn) && btn != null)
                {
                    WireOne(btn);
                    return;
                }
                // the version/news windows reuse XUiC_MainMenuButtons without the button grid;
                // complain only when the real button grid is missing its injected button
                XUiC_Button probe = null;
                if (__instance.TryGetChildByIdAndType<XUiC_Button>("btnPlayGame", out probe))
                    Log.Warning("[HotReload] browser: main-menu btnMods missing (xml patch not applied?)");
            }
            catch (Exception e) { Log.Warning("[HotReload] browser: main-menu wiring: " + e.Message); }
        }

        static void OnMainMenuModsPressed(XUiController sender, int mouseButton)
        {
            try { Open(); } catch (Exception e) { Log.Error("[HotReload] browser open: " + e.Message); }
        }

        // in-game ESC menu
        public static void InGameMenuInitPostfix(XUiC_InGameMenuWindow __instance)
        {
            try
            {
                var c = __instance.GetChildById("btnMods");
                if (c == null) { Log.Warning("[HotReload] browser: ingame btnMods missing (xml patch not applied?)"); return; }
                var b = c.GetChildByType<XUiC_SimpleButton>();
                WireOne(b != null ? b : c);
            }
            catch (Exception e) { Log.Warning("[HotReload] browser: in-game wiring: " + e.Message); }
        }

        static void OnInGameModsPressed(XUiController sender, int mouseButton)
        {
            try { Open(); } catch (Exception e) { Log.Error("[HotReload] browser open: " + e.Message); }
        }

        // ------------------------------------------------------------------ open/close

        public static void Open()
        {
            try { TryWireLive(); } catch { }
            IsOpen = true;
            _clickLogs = 80; _evLogs = 60;
            _installed = null;
            ModPack.InvalidateSaved();
            _sortOpen = false;
            _thumbBudget = THUMB_MAX; _thumbLogs = 14;
            try { var sp = Path.Combine(ThumbCacheDir, "_shot.png"); if (File.Exists(sp)) File.Delete(sp); } catch { }
            if (ModBrowser.Items.Count == 0 && !ModBrowser.Loading) ModBrowser.RequestRefresh();
            try
            {
                if (!_cursorForced)
                {
                    _prevCursorVisible = Cursor.visible;
                    Cursor.visible = true;
                    Cursor.lockState = CursorLockMode.None;
                    _cursorForced = true;
                }
            }
            catch { }
            Log.Out("[HotReload] browser opened");
        }

        public static void Close()
        {
            IsOpen = false;
            try
            {
                if (_cursorForced)
                {
                    Cursor.visible = _prevCursorVisible;
                    _cursorForced = false;
                }
            }
            catch { }
        }

        public static string OpenFromConsole()
        {
            try
            {
                // in a world, open the ESC menu first (pause + cursor) so the overlay
                // doesn't float over live gameplay
                var gm = GameManager.Instance;
                if (gm != null && gm.World != null)
                {
                    try
                    {
                        var ui = LocalPlayerUI.primaryUI;
                        if (ui != null && ui.windowManager != null) ui.windowManager.Open("ingameMenu", true);
                    }
                    catch { }
                }
                Open();
                return "[browser] window opened";
            }
            catch (Exception e) { return "[browser] open failed: " + e.Message; }
        }

        // ------------------------------------------------------------------ render

        public static void OnGuiPostfix()
        {
            if (!IsOpen) return;
            try { DrawOverlay(); }
            catch (Exception e) { Log.Error("[HotReload] browser draw: " + e.Message); }
            try { ShotNow(); } catch { }
        }

        static void ShotNow()
        {
            if (string.IsNullOrEmpty(ShotSlug)) return;
            if (Screen.width <= 0 || Screen.height <= 0) return;
            // capture ONLY on Repaint: OnGUI also runs Layout events before the frame is drawn,
            // and a ReadPixels there grabs the PREVIOUS frame (window not yet on screen) - the
            // capture-then-close then happens before the window ever renders.
            var ev = Event.current;
            if (ev == null || ev.type != EventType.Repaint) return;
            var slug = ShotSlug; ShotSlug = null;
            try
            {
                var tex = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
                tex.Apply(false);
                Directory.CreateDirectory(ThumbCacheDir);
                File.WriteAllBytes(Path.Combine(ThumbCacheDir, "_shot.png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
                Log.Out("[HotReload] browser: screenshot saved (" + slug + ") " + Screen.width + "x" + Screen.height);
                if (ShotCloseAfter) { ShotCloseAfter = false; Close(); }
            }
            catch (Exception e) { Log.Warning("[HotReload] browser: shot failed: " + e.Message); }
        }

        // ------------------------------------------------- card thumbnails (WebP, cached)

        static string ThumbCacheDir
        {
            get
            {
                var root = ModBrowser.CatalogDir;
                return string.IsNullOrEmpty(root) ? Path.Combine(Path.GetTempPath(), "hr-thumbs") : Path.Combine(root, "cache");
            }
        }

        static Texture2D MakeNoPic()
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, new Color(0.09f, 0.09f, 0.11f, 1f)); t.Apply();
            return t;
        }

        static string CachedThumbPath(string slug)
        {
            if (string.IsNullOrEmpty(slug)) slug = "none";
            var sb = new StringBuilder();
            for (int i = 0; i < slug.Length; i++)
            {
                var c = slug[i];
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
                if (sb.Length >= 64) break;
            }
            sb.Append(".img");
            return Path.Combine(ThumbCacheDir, sb.ToString());
        }

        static byte[] ReadFileBytes(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[8192]; int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    return ms.ToArray();
                }
            }
            catch { return null; }
        }

        static bool LoadCached(string slug)
        {
            try
            {
                var data = ReadFileBytes(CachedThumbPath(slug));
                if (data == null || data.Length < 64) return false;
                var t2 = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!t2.LoadImage(data))
                { try { File.Delete(CachedThumbPath(slug)); } catch { } return false; }  // poisoned cache (webp) - drop it
                t2.wrapMode = TextureWrapMode.Clamp;
                // NOTE: LoadImage textures draw CORRECTLY in IMGUI as-is (Unity uploads them
                // flipped); only SetPixels-drawn art needs the manual row flip. Do NOT flip here.
                _thumbs[slug] = t2;
                return true;
            }
            catch { return false; }
        }

        // draw a card pic: cached texture if we have it, else placeholder + lazy background
        // fetch (disk cache first, then WebP URL). Budget-capped per open.
        static void Thumb(string slug, string url, Rect r)
        {
            if (_texNoPic == null) _texNoPic = MakeNoPic();
            Texture2D tex;
            if (!string.IsNullOrEmpty(slug) && _thumbs.TryGetValue(slug, out tex) && tex != null)
            { GUI.DrawTexture(r, tex, ScaleMode.ScaleAndCrop); return; }
            GUI.DrawTexture(r, _texNoPic, ScaleMode.ScaleAndCrop);
            if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(url)) return;
            if (_thumbBudget <= 0 || _thumbFailed.ContainsKey(slug) || _thumbs.ContainsKey(slug)) return;
            if (LoadCached(slug)) return;
            // small limited amount: cap concurrent downloads; the rest wait for a later frame
            if (_thumbBusy.Count >= THUMB_INFLIGHT || _thumbBusy.Contains(url)) return;
            if (!_thumbBusy.Add(url)) return;
            _thumbBudget--;
            var k = slug;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Unity 2022.3 LoadImage decodes PNG/JPEG only (no WebP); the CDN keeps the
                    // ORIGINAL upload's extension alongside the .webp render - chain jpg, png.
                    var b = url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                        ? url.Substring(0, url.Length - 5) : url;
                    byte[] data = b != url ? TryFetch(b + ".jpg") ?? TryFetch(b + ".png") : TryFetch(url);
                    if (data == null || data.Length < 64)
                    {
                        _thumbFailed[url] = 1; _thumbFailed[k] = 1;
                        if (_thumbLogs > 0) { _thumbLogs--; Log.Warning("[HotReload] thumb no decodable variant: " + k); }
                        return;
                    }
                    try { Directory.CreateDirectory(ThumbCacheDir); File.WriteAllBytes(CachedThumbPath(k), data); } catch { }
                    ModBrowser.EnqueueMain(delegate
                    {
                        try
                        {
                            var t2 = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                            if (t2.LoadImage(data)) { t2.wrapMode = TextureWrapMode.Clamp; _thumbs[k] = t2; }
                            else { _thumbFailed[url] = 1; try { File.Delete(CachedThumbPath(k)); } catch { } }
                        }
                        catch { _thumbFailed[url] = 1; }
                    });
                }
                finally { _thumbBusy.Remove(url); }
            });
        }

        static byte[] TryFetch(string url)
        {
            try { using (var wc = new WebClient()) { wc.Headers["User-Agent"] = "7dtd-hotreload-browser"; return wc.DownloadData(url); } }
            catch { return null; }
        }

        // LoadImage fills texture memory top-down while SetPixels/GetPixels index row 0 as the
        // bottom row: flip ONCE at insertion or every loaded image draws vertically mirrored.
        static void FlipFixup(Texture2D tex)
        {
            try
            {
                var px = tex.GetPixels();
                int w = tex.width, h = tex.height;
                var fx = new Color[px.Length];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        fx[y * w + x] = px[(h - 1 - y) * w + x];
                tex.SetPixels(fx); tex.Apply();
            }
            catch { }
        }

        // called by the core's thumbnail resolver (main thread) with an out-of-band url
        public static void ThumbUrl(string slug, string url)
        {
            if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(url)) return;
            if (_thumbs.ContainsKey(slug) || _thumbFailed.ContainsKey(slug)) return;
            if (LoadCached(slug)) return;
            if (_thumbBusy.Contains(url)) return;
            if (!_thumbBusy.Add(url)) return;
            if (_thumbBudget <= 0) return;
            _thumbBudget--;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var b = url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                        ? url.Substring(0, url.Length - 5) : url;
                    byte[] data = b != url ? TryFetch(b + ".jpg") ?? TryFetch(b + ".png") : TryFetch(url);
                    if (data == null || data.Length < 64) { _thumbFailed[url] = 1; _thumbFailed[slug] = 1; return; }
                    try { Directory.CreateDirectory(ThumbCacheDir); File.WriteAllBytes(CachedThumbPath(slug), data); } catch { }
                    ModBrowser.EnqueueMain(delegate
                    {
                        try
                        {
                            var t2 = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                            if (t2.LoadImage(data))
                            {
                                t2.wrapMode = TextureWrapMode.Clamp;
                                _thumbs[slug] = t2;   // LoadImage textures draw correctly as-is
                            }
                            else { _thumbFailed[url] = 1; try { File.Delete(CachedThumbPath(slug)); } catch { } }
                        }
                        catch { _thumbFailed[url] = 1; }
                    });
                }
                finally { _thumbBusy.Remove(url); }
            });
        }

        public static void ClearThumbs()
        {
            try { foreach (var kv in _thumbs) if (kv.Value != null) UnityEngine.Object.Destroy(kv.Value); } catch { }
            _thumbs.Clear(); _thumbBusy.Clear(); _thumbFailed.Clear(); _flipped.Clear();
            _thumbBudget = THUMB_MAX; _thumbLogs = 14;
        }

        static Texture2D Solid(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }

        // small magnifier icon for the search field (20x20: ring + handle)
        static Texture2D MakeMagnifier()
        {
            int S = 20;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
            var clear = new Color(0, 0, 0, 0);
            var ink = new Color(0.82f, 0.82f, 0.86f, 1f);
            var px = new Color[S * S];
            for (int i = 0; i < px.Length; i++) px[i] = clear;
            float cx = 7.5f, cy = 7.5f, rOut = 6.4f, rIn = 4.4f;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);
                    bool ring = d >= rIn && d <= rOut;
                    // handle: distance from point to segment (11.5,11.5)->(16.8,16.8)
                    float hx = x + 0.5f - 11.5f, hy = y + 0.5f - 11.5f;
                    float tt = (hx + hy) / 2f; if (tt < 0f) tt = 0f; if (tt > 5.3f) tt = 5.3f;
                    float ex = hx - tt, ey = hy - tt;
                    bool handle = (hx + hy) / 2f >= -0.4f && (float)Math.Sqrt(ex * ex + ey * ey) <= 1.5f && hx + hy <= 11.0f;
                    if (ring || handle) px[y * S + x] = ink;
                }
            t.SetPixels(px); t.Apply();
            return t;
        }

        static GUIStyle Flat(Color bg, Color bgHover, Color text, int size, bool bold)
        {
            var st = new GUIStyle(GUI.skin.button) { fontSize = size, alignment = TextAnchor.MiddleCenter };
            if (bold) st.fontStyle = FontStyle.Bold;
            st.normal.background = Solid(bg); st.normal.textColor = text;
            st.hover.background = Solid(bgHover); st.hover.textColor = text;
            st.active.background = Solid(bgHover); st.active.textColor = text;
            return st;
        }

        static void EnsureStyles()
        {
            if (_stylesReady) return;
            _texWin = Solid(new Color(0.10f, 0.10f, 0.12f, 0.99f));
            _texCard = Solid(new Color(0.14f, 0.14f, 0.165f, 1f));
            _texCardAlt = Solid(new Color(0.175f, 0.175f, 0.205f, 1f));
            _texAccent = Solid(new Color(0.85f, 0.16f, 0.16f, 1f));      // game red
            _texGreen = Solid(new Color(0.25f, 0.65f, 0.30f, 1f));
            _texOrange = Solid(new Color(0.90f, 0.60f, 0.15f, 1f));
            _texRed = Solid(new Color(0.80f, 0.25f, 0.25f, 1f));
            _texField = Solid(new Color(0.085f, 0.085f, 0.10f, 1f));
            _texBand = Solid(new Color(0.21f, 0.21f, 0.24f, 1f));
            _texLine = Solid(new Color(0.30f, 0.30f, 0.33f, 1f));
            _texWhite = Solid(Color.white);
            _texMag = MakeMagnifier();
            FlipFixup(_texMag);

            _box = new GUIStyle(GUI.skin.box);
            _h1 = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold };
            _h2 = new GUIStyle(GUI.skin.label) { fontSize = 19, fontStyle = FontStyle.Bold };
            _lbl = new GUIStyle(GUI.skin.label) { fontSize = 16 };
            _lblDim = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            _lblDim.normal.textColor = new Color(0.68f, 0.68f, 0.70f);
            _lblTiny = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _lblTiny.normal.textColor = new Color(0.60f, 0.60f, 0.63f);
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 16 };
            _btnTiny = new GUIStyle(GUI.skin.button) { fontSize = 14 };
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 17, alignment = TextAnchor.MiddleLeft };
            _field.padding.left = 32;    // room for the magnifier icon (left side, like the mockup)
            _field.padding.right = 12;
            _badge = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _chipOn = Flat(new Color(0.85f, 0.16f, 0.16f, 1f), new Color(0.95f, 0.24f, 0.24f, 1f), Color.white, 15, true);
            _chipOff = Flat(new Color(0.22f, 0.22f, 0.26f, 1f), new Color(0.30f, 0.30f, 0.35f, 1f), new Color(0.88f, 0.88f, 0.90f), 15, false);
            _xBtn = Flat(new Color(0.16f, 0.16f, 0.19f, 1f), new Color(0.55f, 0.14f, 0.14f, 1f), new Color(0.92f, 0.35f, 0.35f), 18, true);
            _chipOnSmall = Flat(new Color(0.85f, 0.16f, 0.16f, 1f), new Color(0.95f, 0.24f, 0.24f, 1f), Color.white, 13, true);
            _chipOffSmall = Flat(new Color(0.22f, 0.22f, 0.26f, 1f), new Color(0.30f, 0.30f, 0.35f, 1f), new Color(0.88f, 0.88f, 0.90f), 13, false);
            _stylesReady = true;
        }

        // submit the search box right now
        static void SubmitSearch()
        {
            if (searchTimer != null) { searchTimer.Dispose(); searchTimer = null; }
            ModBrowser.Query = _search;
            ModBrowser.Page = 1;
            ModBrowser.RequestRefresh();
        }

        static void DrawOverlay()
        {
            EnsureStyles();

            // auto-search: after a typing pause, run the search without pressing anything

            // bounded input diagnostics: proves whether mouse events reach this overlay and
            // whether some other control already owns hotControl when they do
            var evt = Event.current;
            if (evt != null && _evLogs > 0 && (evt.type == EventType.MouseDown || evt.type == EventType.MouseUp))
            {
                _evLogs--;
                Log.Out("[HotReload] browser: input " + evt.type + " @ " + (int)evt.mousePosition.x + "," + (int)evt.mousePosition.y + " hot=" + GUIUtility.hotControl);
            }

            float scale = Screen.height / 1080f;
            if (scale <= 0.1f) scale = 1f;
            var oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(new Vector3((Screen.width - 1920 * scale) / 2f, (Screen.height - 1080 * scale) / 2f, 0), Quaternion.identity, new Vector3(scale, scale, 1));

            // floating window - no dim, no blur behind it (per the mockup)
            var win = new Rect(570f, 320f, 1310f, 720f);
            GUI.DrawTexture(win, _texWin);
            GUI.DrawTexture(new Rect(win.x, win.y, win.width, 62f), _texBand);
            GUI.DrawTexture(new Rect(win.x, win.y + 62f, win.width, 3f), _texAccent);

            // header: centered title (mockup has no close X up here - removal lives on the
            // Downloaded cards); signature sits at the window's bottom-right
            Centered(new Rect(win.x, win.y + 10f, win.width, 40f), "Mods", _h1);

            // pack button + search box (search stays flush right per the mockup)
            if (Btn(new Rect(win.xMax - 16f - 340f - 12f - 110f, win.y + 14f, 110f, 34f), _packOpen ? "hide pack" : "pack", _btn)) { _packOpen = !_packOpen; if (_packOpen) ModPack.InvalidateSaved(); }

            // search box: flush right (mockup), magnifier inside on the LEFT
            var fieldRect = new Rect(win.xMax - 16f - 340f, win.y + 14f, 340f, 34f);
            GUI.DrawTexture(fieldRect, _texField);
            GUI.SetNextControlName("modSearch");
            var newSearch = GUI.TextField(fieldRect, _search, _field);
            if (newSearch != _search)
            {
                _search = newSearch;
                if (searchTimer != null) searchTimer.Dispose();
                searchTimer = new System.Threading.Timer(_ =>
                {
                    try { HotReloadCore.RunOnMainThread(() => SubmitSearch()); }
                    catch (Exception e) { Log.Warning("[HotReload] search: " + e.Message); }
                }, null, 900, System.Threading.Timeout.Infinite);
            }
            GUI.DrawTexture(new Rect(fieldRect.x + 8f, fieldRect.y + 7f, 20f, 20f), _texMag);
            if (string.IsNullOrEmpty(_search))
                GUI.Label(new Rect(fieldRect.x + 34f, fieldRect.y + 8f, fieldRect.width - 44f, 20f), "search mods...", _lblTiny);

            if (evt != null && evt.type == EventType.KeyDown && (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == "modSearch")
            { SubmitSearch(); evt.Use(); }

            // ESC closes
            if (evt != null && evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
            {
                evt.Use();
                Close();
                GUI.matrix = oldMatrix;
                return;
            }

            // ---- panes
            const float pad = 16f, gap = 18f;
            float contentTop = win.y + 77f;
            float innerW = win.width - pad * 2f;
            float leftW = 500f;
            float rightW = innerW - leftW - gap;
            float leftX = win.x + pad;
            float rightX = leftX + leftW + gap;
            float bandH = 36f;
            float listTop = contentTop + bandH + 10f;
            float listH = win.yMax - listTop - 52f;

            // pane header bands
            GUI.DrawTexture(new Rect(leftX, contentTop, leftW, bandH), _texBand);
            GUI.DrawTexture(new Rect(rightX, contentTop, rightW, bandH), _texBand);

            int instCount = _installed == null ? 0 : _installed.Count;
            Centered(new Rect(leftX, contentTop + 6f, leftW, 24f), "Downloaded" + (instCount > 0 ? "  (" + instCount + ")" : ""), _h2);
            Centered(new Rect(rightX, contentTop + 6f, rightW, 24f), "Browse", _h2);

            // divider
            GUI.DrawTexture(new Rect(leftX + leftW + gap / 2f - 1f, contentTop, 1f, win.yMax - contentTop - 48f), _texLine);
            GUI.Label(new Rect(win.x + 16f, win.yMax - 24f, win.width - 32f, 22f), FriendSync.Status, _lblTiny);

            // ---- pack panel (replaces the lists while open, like the sort panel)
            if (_packOpen) { DrawPackPanel(win, contentTop); GUI.matrix = oldMatrix; return; }

            // ---- left pane: downloaded / installed mods
            if (_installed == null)
            {
                _installed = ModBrowser.ListInstalled();
                _installedWhen = Time.realtimeSinceStartup;
            }
            DrawDownloaded(new Rect(leftX, listTop, leftW, listH));

            // ---- right pane: browse
            DrawBrowsePane(rightX, listTop, rightW, listH);

            // ---- footer
            GUI.Label(new Rect(win.x + pad, win.yMax - 48f, win.width - 180f, 20f), ModBrowser.Status, _lblTiny);
            var sig = new GUIContent("by Noisemakerjon");
            var sigSz = _lblTiny.CalcSize(sig);
            GUI.Label(new Rect(win.xMax - 14f - sigSz.x, win.yMax - 48f, sigSz.x + 4f, 20f), sig, _lblTiny);

            // Click-blocker that CANNOT starve the window's own buttons: Unity IMGUI gives a
            // click to the FIRST interactive control containing it (hotControl claim on
            // MouseDown), so a fullscreen blocker drawn BEFORE the window claims every click and
            // every button after it goes dead (the v5.0.0 bug). These four strips cover only the
            // area OUTSIDE the window rect - overlap with our controls is impossible by
            // construction, so the window stays modal without the starvation. Invisible (the
            // background stays unblurred and dimmed-free).
            if (_invisible == null) _invisible = new GUIStyle(GUIStyle.none);
            const float BX = 4000f;
            float x0 = -BX, y0 = -BX, x1 = 1920f + BX, y1 = 1080f + BX;
            GUI.Button(new Rect(x0, y0, x1 - x0, win.y - y0), GUIContent.none, _invisible);               // above
            GUI.Button(new Rect(x0, win.yMax, x1 - x0, y1 - win.yMax), GUIContent.none, _invisible);       // below
            GUI.Button(new Rect(x0, win.y, win.x - x0, win.height), GUIContent.none, _invisible);          // left
            GUI.Button(new Rect(win.xMax, win.y, x1 - win.xMax, win.height), GUIContent.none, _invisible); // right

            GUI.matrix = oldMatrix;
        }

        // header pack panel: make from installed / paste a code / saved packs
        static void DrawPackPanel(Rect win, float top)
        {
            float pad = 16f;
            var panel = new Rect(win.x + pad, top + 6f, win.width - pad * 2f, win.yMax - top - 6f - 48f);
            GUI.DrawTexture(panel, _texCard);
            GUI.Label(new Rect(panel.x + 14f, panel.y + 10f, 300f, 26f), "Mod packs", _h2);
            GUI.Label(new Rect(panel.x + 14f, panel.y + 34f, panel.width - 28f, 20f),
                "Friends sync the host mods automatically on join. Portable packs include the actual files and versions.", _lblTiny);

            float bx = panel.x + 14f, by = panel.y + 62f;
            _packName = GUI.TextField(new Rect(bx, by, 220f, 30f), _packName, _field);
            if (Btn(new Rect(bx + 234f, by, 180f, 30f), "Export portable pack", _btnTiny)) ModPack.LastResult = FriendSync.ExportPack(_packName);
            by += 38f;
            if (Btn(new Rect(bx, by, 240f, 34f), "Make pack from installed", _btn))
            {
                var p = ModBrowser.PackFromInstalledUi();
                if (p == null) ModPack.LastResult = "No catalog-installed mods. Export portable pack includes manual mods.";
                else p.name = _packName;
                if (p != null)
                {
                    var code = ModPack.Encode(p);
                    ModPack.SaveFile(p);
                    _packBox = code;
                    ModPack.LastResult = "Saved " + p.name + " with " + p.slugs.Count + " mods";
                }
            }
            GUI.Label(new Rect(bx + 254f, by + 9f, 300f, 20f), "saves + puts the code in the box", _lblTiny);

            by += 44f;
            GUI.Label(new Rect(bx, by + 8f, 90f, 24f), "code / file:", _lbl);
            var fieldR = new Rect(bx + 96f, by, panel.width - 96f - 150f - 24f, 34f);
            GUI.SetNextControlName("packCode");
            _packBox = GUI.TextField(fieldR, _packBox, _field);
            if (Btn(new Rect(fieldR.xMax + 12f, by, 140f, 34f), "install pack", _btn))
            {
                var p = ModPack.Decode(_packBox);
                if (p != null) ModPack.LastResult = ModPack.ApplyAll(p, "pack:" + p.name);
                else if (File.Exists(_packBox.Trim().Trim('"'))) ModPack.LastResult = FriendSync.ImportPack(_packBox.Trim().Trim('"'));
                else ModPack.LastResult = "Paste a valid HRP1 code or a portable .hrpack file path";
            }

            by += 48f;
            GUI.Label(new Rect(bx, by, panel.width - 28f, 22f), ModPack.LastResult, _lblTiny);
            by += 24f;
            GUI.Label(new Rect(bx, by, 200f, 22f), "saved packs:", _lbl);
            var ps = ModPack.LoadFiles();
            var portable = ModPack.PortableFiles();
            by += 26f;
            var listR = new Rect(panel.x + 10f, by, panel.width - 20f, panel.yMax - by - 10f);
            var content = new Rect(0, 0, listR.width - 20f, Mathf.Max(listR.height, (ps.Count + portable.Length) * 40f));
            var sc = GUI.BeginScrollView(listR, _packScroll, content);
            _packScroll = new Vector2(0, sc.y);
            float py = 0f;
            foreach (var path in portable)
            {
                GUI.DrawTexture(new Rect(0, py, content.width, 36f), _texCard);
                GUI.Label(new Rect(6f, py + 7f, content.width - 270f, 22f), Path.GetFileNameWithoutExtension(path) + " (portable)", _lbl);
                float rx = content.width - 260f;
                if (Btn(new Rect(rx, py + 4f, 120f, 28f), "install", _btnTiny)) ModPack.LastResult = FriendSync.ImportPack(path);
                if (Btn(new Rect(rx + 128f, py + 4f, 120f, 28f), "copy path", _btnTiny)) { _packBox = path; GUIUtility.systemCopyBuffer = path; }
                py += 40f;
            }
            for (int i = 0; i < ps.Count; i++)
            {
                var pk = ps[i];
                GUI.DrawTexture(new Rect(0, py, content.width, 36f), i % 2 == 0 ? _texCardAlt : _texCard);
                GUI.Label(new Rect(6f, py + 7f, 340f, 22f), pk.name + "   (" + pk.slugs.Count + " mods)", _lbl);
                float rx = content.width - 260f;
                if (Btn(new Rect(rx, py + 4f, 120f, 28f), "install", _btnTiny))
                { var p = pk; ModPack.ApplyAll(p, "pack:" + p.name); }
                if (Btn(new Rect(rx + 128f, py + 4f, 120f, 28f), "copy code", _btnTiny))
                {
                    var code = ModPack.Encode(pk);
                    _packBox = code;
                    try { GUIUtility.systemCopyBuffer = code; } catch { }
                }
                py += 40f;
            }
            GUI.EndScrollView();
        }

        // left pane: every mod sitting in the Mods folder, with an action + remove
        static void DrawDownloaded(Rect listRect)
        {
            var installed = _installed ?? new List<ModBrowser.InstalledInfo>();
            float w = listRect.width - 22f;
            float rowH = 96f;
            var content = new Rect(0f, 0f, w - 6f, Math.Max(listRect.height, installed.Count * (rowH + 8f) + 8f));
            var scroll = GUI.BeginScrollView(listRect, _leftScroll, content);
            if (Mathf.Abs(scroll.y - _lastLeftY) > 1f) { _dirL = scroll.y > _lastLeftY ? 1 : -1; _lastLeftY = scroll.y; }
            _leftScroll = new Vector2(0f, scroll.y);

            float pitchL = rowH + 8f;
            int firstL = (int)Mathf.Floor(Mathf.Max(0f, _leftScroll.y) / pitchL);
            int visL = (int)Mathf.Ceil(listRect.height / pitchL) + 1;
            int j0 = Mathf.Max(0, firstL - 2 - (_dirL < 0 ? THUMB_PREFETCH : 0));
            int j1 = Mathf.Min(installed.Count, firstL + visL + 2 + (_dirL > 0 ? THUMB_PREFETCH : 0));

            float ry = 4f;
            for (int i = 0; i < installed.Count; i++)
            {
                if (i < j0 || i >= j1) { ry += rowH + 8f; continue; }
                var m = installed[i];
                var row = new Rect(3f, ry, w - 12f, rowH);
                GUI.DrawTexture(row, i % 2 == 0 ? _texCardAlt : _texCard);

                bool trackedL = !string.IsNullOrEmpty(m.slug);
                var catL = trackedL ? FindCatalogItem(m.slug) : null;
                Thumb(m.slug, catL != null ? catL.thumbnail : null, new Rect(row.x + 10f, row.y + 12f, 72f, 72f));
                if (catL == null) ModBrowser.WantThumb(m.slug);   // resolve pic out-of-band

                float tx = row.x + 92f;
                float tw = row.width - 92f - 44f;
                GUI.Label(new Rect(tx, row.y + 10f, tw, 26f), Clamp(m.name, 34), _h2);
                GUI.Label(new Rect(tx, row.y + 36f, tw, 20f), "v" + (string.IsNullOrEmpty(m.version) ? "?" : m.version) + "   •   " + m.state + "   •   " + Clamp(m.folder, 22), _lblDim);

                bool tracked = trackedL;
                BrowserItem cat = tracked ? catL : null;
                string upd = cat != null ? ModBrowser.InstalledStatus(cat) : "";
                bool hasUpdate = upd.StartsWith("update");

                if (tracked)
                {
                    if (hasUpdate)
                    {
                        if (Btn(new Rect(row.xMax - 160f, row.y + 58f, 124f, 30f), "Update", _btn))
                            ModBrowser.RequestInstall(cat ?? MiniItem(m), m.folder);
                        GUI.Label(new Rect(tx, row.y + 58f, tw, 20f), upd, _lblTiny);
                    }
                    else
                    {
                        if (Btn(new Rect(row.xMax - 160f, row.y + 58f, 124f, 30f), "Reinstall", _btnTiny))
                            ModBrowser.RequestInstall(cat ?? MiniItem(m), m.folder);
                        GUI.Label(new Rect(tx, row.y + 58f, tw, 20f), "from 7d2dmods.gg", _lblTiny);
                    }
                }
                else
                {
                    if (Btn(new Rect(row.xMax - 160f, row.y + 58f, 124f, 30f), "Reload", _btn))
                    {
                        try { Log.Out("[HotReload] browser reload: " + HotReloadCore.ReapplyModReport(m.folder)); } catch { }
                        _installed = null;
                    }
                    GUI.Label(new Rect(tx, row.y + 58f, tw, 20f), "manual mod", _lblTiny);
                }

                // remove (two-step confirm, X in the corner like the mockup)
                if (m.required) GUI.Label(new Rect(row.xMax - 100f, row.y + 6f, 92f, 26f), "required", _lblTiny);
                else if (_confirmRemove == Path.Combine(m.root, m.folder))
                {
                    if (Btn(new Rect(row.xMax - 62f, row.y + 6f, 52f, 26f), "sure?", _xBtn))
                    {
                        var res = ModBrowser.RemoveInstalled(m.folder, m.root);
                        ModBrowser.Status = res;
                        Log.Out("[HotReload] browser remove: " + res);
                        _confirmRemove = null; _installed = null;
                    }
                }
                else if (Btn(new Rect(row.xMax - 38f, row.y + 6f, 28f, 26f), "×", _xBtn))
                {
                    _confirmRemove = Path.Combine(m.root, m.folder);
                }

                ry += rowH + 8f;
            }

            if (installed.Count == 0)
                GUI.Label(new Rect(listRect.x + 16f, listRect.y + 16f, 400f, 26f), "no mods installed yet", _h2);

            GUI.EndScrollView();
        }

        static BrowserItem MiniItem(ModBrowser.InstalledInfo m)
        {
            var it = new BrowserItem();
            it.slug = m.slug; it.title = m.title;
            return it;
        }

        static BrowserItem FindCatalogItem(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return null;
            var items = ModBrowser.Items;
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i].slug, slug, StringComparison.OrdinalIgnoreCase)) return items[i];
            return null;
        }

        // right pane: category chips + search results with a download button
        static void DrawBrowsePane(float rx, float chipsTop, float rw, float listH)
        {
            var evt = Event.current;

            // ---- chips row: [V3 only][All][category...] ...... [sort][refresh]
            // view height 54 = chip row (34) + headroom for the horizontal scrollbar: with
            // content height == chip height the row can never grow a VERTICAL scrollbar
            // (that was the "category search cut off vertically" bug).
            float chipsW = rw - 150f - 44f - 16f;
            var chipRect = new Rect(rx, chipsTop, chipsW, 54f);

            // manual horizontal wheel scroll (the row is wider than the pane)
            if (evt != null && evt.type == EventType.ScrollWheel && chipRect.Contains(evt.mousePosition))
            {
                float max = Mathf.Max(0f, _chipContentW - chipsW);
                _chipScroll.x = Mathf.Clamp(_chipScroll.x - evt.delta.y * 60f, 0f, max);
                evt.Use();
            }

            var chipContent = new Rect(0f, 0f, Mathf.Max(chipsW, _chipContentW), 34f);
            _chipScroll = GUI.BeginScrollView(chipRect, _chipScroll, chipContent, false, false);
            float cx = 3f;
            if (Btn(new Rect(cx, 3f, 112f, 28f), ModBrowser.VersionFilter == 0 ? ModBrowser.GameVersionSlug.ToUpperInvariant() + " only" : "all versions", ModBrowser.VersionFilter == 0 ? _chipOnSmall : _chipOffSmall))
            { ModBrowser.VersionFilter = ModBrowser.VersionFilter == 0 ? 1 : 0; ModBrowser.Page = 1; ModBrowser.RequestRefresh(); }
            cx += 120f;
            if (Btn(new Rect(cx, 3f, 58f, 28f), "All", string.IsNullOrEmpty(ModBrowser.CategorySlug) ? _chipOnSmall : _chipOffSmall))
            { ModBrowser.CategorySlug = ""; ModBrowser.Page = 1; ModBrowser.RequestRefresh(); }
            cx += 66f;
            var cats = ModBrowser.Categories;
            if (cats != null)
            {
                for (int i = 0; i < cats.Count; i++)
                {
                    var c = cats[i];
                    float cw = Mathf.Clamp(c[0].Length * 7.5f + 24f, 74f, 200f);
                    bool on = string.Equals(ModBrowser.CategorySlug, c[1], StringComparison.OrdinalIgnoreCase);
                    if (Btn(new Rect(cx, 3f, cw, 28f), c[0], on ? _chipOnSmall : _chipOffSmall))
                    { ModBrowser.CategorySlug = c[1]; ModBrowser.Page = 1; ModBrowser.RequestRefresh(); }
                    cx += cw + 8f;
                }
            }
            _chipContentW = cx;
            GUI.EndScrollView();

            // sort dropdown (small, right of the chips)
            var sortRect = new Rect(rx + rw - 150f - 44f, chipsTop + 2f, 150f, 30f);
            if (Btn(sortRect, "Sort: " + ModBrowser.SortNames[ModBrowser.Sort] + "  ▾", _btn)) _sortOpen = !_sortOpen;
            if (Btn(new Rect(rx + rw - 36f, chipsTop + 2f, 36f, 30f), "↻", _btn)) { ModBrowser.RequestRefresh(); _sortOpen = false; }

            float cardsTop = chipsTop + 58f;
            float cardsH = listH - 58f - 40f;

            if (_sortOpen)
            {
                // sorting panel replaces the list while open (no click conflicts)
                var panel = new Rect(rx, cardsTop, 320f, 4f * 34f + 10f);
                GUI.DrawTexture(panel, _texCardAlt);
                for (int i = 0; i < ModBrowser.SortNames.Length; i++)
                {
                    if (Btn(new Rect(rx + 5f, cardsTop + 5f + i * 34f, 310f, 30f), ModBrowser.SortNames[i], _btn))
                    { ModBrowser.Sort = i; ModBrowser.Page = 1; _sortOpen = false; ModBrowser.RequestRefresh(); }
                }
            }
            else
            {
                DrawBrowseList(new Rect(rx, cardsTop, rw, cardsH));
            }

            // pager
            float py = cardsTop + cardsH + 6f;
            if (Btn(new Rect(rx, py, 100f, 30f), "◀ Prev", _btnTiny) && ModBrowser.Page > 1) { ModBrowser.Page--; ModBrowser.RequestRefresh(); }
            GUI.Label(new Rect(rx + 108f, py + 4f, 320f, 24f), "page " + ModBrowser.Page + " / " + Math.Max(1, ModBrowser.TotalPages) + "   (" + ModBrowser.Total + " mods)", _lbl);
            if (Btn(new Rect(rx + 400f, py, 100f, 30f), "Next ▶", _btnTiny) && ModBrowser.Page < Math.Max(1, ModBrowser.TotalPages)) { ModBrowser.Page++; ModBrowser.RequestRefresh(); }
        }

        static void DrawBrowseList(Rect listRect)
        {
            float w = listRect.width - 22f;
            float rowH = 96f;
            var items = ModBrowser.Items;
            var content = new Rect(0f, 0f, w - 6f, Math.Max(listRect.height, items.Count * (rowH + 8f) + 8f));
            var scroll = GUI.BeginScrollView(listRect, _rightScroll, content);
            if (Mathf.Abs(scroll.y - _lastRightY) > 1f) { _dirR = scroll.y > _lastRightY ? 1 : -1; _lastRightY = scroll.y; }
            _rightScroll = new Vector2(0f, scroll.y);

            // virtualized: only rows near the viewport are drawn at all (like web virtual
            // scrolling), plus a prefetch window ahead of the scroll direction so thumbs are
            // already downloading before the user reaches them. Far rows cost nothing.
            float pitchR = rowH + 8f;
            int firstR = (int)Mathf.Floor(Mathf.Max(0f, _rightScroll.y) / pitchR);
            int visR = (int)Mathf.Ceil(listRect.height / pitchR) + 1;
            int i0 = Mathf.Max(0, firstR - 2 - (_dirR < 0 ? THUMB_PREFETCH : 0));
            int i1 = Mathf.Min(items.Count, firstR + visR + 2 + (_dirR > 0 ? THUMB_PREFETCH : 0));

            float ry = 4f;
            for (int i = 0; i < items.Count; i++)
            {
                if (i < i0 || i >= i1) { ry += rowH + 8f; continue; }
                var it = items[i];
                var row = new Rect(3f, ry, w - 12f, rowH);
                GUI.DrawTexture(row, i % 2 == 0 ? _texCardAlt : _texCard);

                Thumb(it.slug, it.thumbnail, new Rect(row.x + 10f, row.y + 12f, 72f, 72f));

                float tx = row.x + 92f;
                float tw = row.width - 92f - 176f;
                GUI.Label(new Rect(tx, row.y + 8f, tw, 26f), Clamp(it.title, 44), _h2);
                GUI.Label(new Rect(tx, row.y + 34f, tw, 20f), Clamp(it.summary, 95), _lbl);
                GUI.Label(new Rect(tx, row.y + 56f, tw, 18f), Clamp(it.MetaText(), 88), _lblTiny);

                // version badge + download on the right
                float bx = row.xMax - 166f;
                Badge(new Rect(bx + 20f, row.y + 12f, 64f, 22f), it.VersText(), it.IsV3 ? _texGreen : _texOrange);

                string state;
                bool busy = ModBrowser.InstallState.TryGetValue(it.slug, out state) && state != null
                    && !state.StartsWith("done") && !state.StartsWith("error");
                string errState;
                bool errored = ModBrowser.InstallState.TryGetValue(it.slug, out errState) && errState != null && errState.StartsWith("error");
                string inst = ModBrowser.InstalledStatus(it);

                if (busy)
                {
                    GUI.Label(new Rect(bx, row.y + 44f, 150f, 40f), Clamp(state, 40), _lblDim);
                }
                else if (errored)
                {
                    if (Btn(new Rect(bx + 2f, row.y + 48f, 146f, 30f), "Retry", _btn)) ModBrowser.RequestInstall(it);
                    GUI.Label(new Rect(bx, row.y + 78f, 150f, 18f), Clamp(errState, 40), _lblTiny);
                }
                else if (inst == "installed")
                {
                    Badge(new Rect(bx + 2f, row.y + 48f, 146f, 30f), "installed", _texGreen);
                    if (Btn(new Rect(tx, row.y + 70f, 96f, 24f), "reinstall", _btnTiny)) ModBrowser.RequestInstall(it);
                }
                else if (!string.IsNullOrEmpty(inst) && inst != "installed")
                {
                    if (Btn(new Rect(bx + 2f, row.y + 48f, 146f, 30f), "Update", _btn)) ModBrowser.RequestInstall(it);
                    GUI.Label(new Rect(bx, row.y + 78f, 150f, 18f), Clamp(inst, 40), _lblTiny);
                }
                else
                {
                    if (Btn(new Rect(bx + 2f, row.y + 48f, 146f, 30f), "download", _btn))
                        ModBrowser.RequestInstall(it);
                    if (!it.IsV3)
                        GUI.Label(new Rect(bx, row.y + 78f, 150f, 18f), "version may vary", _lblTiny);
                }

                ry += rowH + 8f;
            }

            if (items.Count == 0)
                GUI.Label(new Rect(listRect.x + 16f, listRect.y + 16f, 500f, 26f), ModBrowser.Loading ? "loading..." : "no results", _h2);

            GUI.EndScrollView();
        }

        static void PicPlaceholder(Rect r)
        {
            if (_texNoPic == null) _texNoPic = MakeNoPic();
            GUI.DrawTexture(r, _texNoPic);
            GUI.Label(r, "no pic", _lblTiny);
        }

        static void Badge(Rect r, string text, Texture2D color)
        {
            GUI.DrawTexture(r, color);
            GUI.Label(r, text, _badge);
        }

        static void Centered(Rect r, string text, GUIStyle st)
        {
            var c = new GUIStyle(st) { alignment = TextAnchor.MiddleCenter };
            GUI.Label(r, text, c);
        }

        static string Clamp(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\n", " ");
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        // button draw + bounded click logging (diagnostics: proves the click path end-to-end
        // without spamming the log - budget is reset every time the window opens)
        static bool Btn(Rect r, string label, GUIStyle style)
        {
            bool clicked = GUI.Button(r, label, style);
            if (clicked && _clickLogs > 0) { _clickLogs--; Log.Out("[HotReload] browser: click '" + label + "'"); }
            return clicked;
        }
    }
}

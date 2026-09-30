// HotReload.cs - CORE of HotReloadTool for 7 Days to Die (V3.2).
//
// Architecture (v4): the game loads HotReloadTool.dll (the BOOTSTRAP, see
// src\Bootstrap.cs) which byte-loads THIS assembly from core\HotReloadCore.dll
// and hot-swaps it whenever the file changes. This file therefore contains
// everything EXCEPT the game-facing entry points (IModApi + the 'hr' command),
// which live in the bootstrap so the tool itself can be rebuilt while playing.
//
// What this core does:
//  - runtime mod loading: drop NEW mod folders while playing -> loads in ~5s
//  - XML / XUI / localization reload (the game's own reload APIs)
//  - hotpacks: hotpacks\*.dll, unpatched + re-patched on rebuild (~1.5s)
//  - src-live: a mod folder with src\*.cs is compiled by this tool (csc) and
//    byte-loaded, so saving a source file hot-swaps the mod's OWN code
//  - prebuilt dll swap: a mod's compiled root .dll is watched; rebuild it and
//    its patches are swept + the new build is loaded live (with rollback)
//  - self-healing for game updates: 'hr doctor' verifies every engine API
//    this tool uses, and src-live mods recompile against the CURRENT build
//
// Safety: Harmony unpatches are never performed on a method that is executing
// on the current thread (that freezes Mono); they defer and retry instead.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Threading;
using HarmonyLib;

namespace HotReloadTool
{
    // Entry point the bootstrap calls (via reflection): the bootstrap owns the
    // game-facing IModApi + 'hr' command; this core does the actual work and can
    // be hot-swapped underneath it at any time.
    public static class CoreEntry
    {
        public const string CoreVersion = "6.3.0-beta.2";

        public static bool Init(Mod mod)
        {
            try { HotReloadCore.Init(mod); return true; }
            catch (Exception e) { Log.Error("[HotReload] core init failed: " + e); return false; }
        }

        public static void Shutdown()
        {
            try { HotReloadCore.Shutdown(); }
            catch (Exception e) { Log.Error("[HotReload] core shutdown: " + e.Message); }
        }

        public static string Execute(List<string> args)
        {
            try { return HotReloadCore.ExecuteCommand(args != null ? args : new List<string>()); }
            catch (Exception e) { return "[HotReload] error: " + e.Message; }
        }

        // the primary user Mods folder (where the browser installs new mods)
        public static string ModsRoot()
        {
            try
            {
                var roots = ModScanner.Roots();
                if (roots != null && roots.Count > 0) return roots[0];
            }
            catch { }
            return null;
        }
    }

    public static class HotReloadCore
    {
        public const string Version = "6.3.0-beta.2";
        public static Mod HostMod;
        public static string ModDir;
        public static string HotpackDir;

        static readonly object _lock = new object();
        static readonly Dictionary<string, Harmony> _harmonies = new Dictionary<string, Harmony>();
        static readonly Dictionary<string, string> _lastErrors = new Dictionary<string, string>();
        static readonly Dictionary<string, int> _deferCount = new Dictionary<string, int>(); // on-stack deferral retries per pack

        static FileSystemWatcher _watcher;
        static readonly List<FileSystemWatcher> _modWatchers = new List<FileSystemWatcher>();
        static readonly List<FileSystemWatcher> _gameWatchers = new List<FileSystemWatcher>(); // game Data\Config (engine update / vanilla tweaks)
        static Timer _timer;
        static FileSystemWatcher _commandWatcher;
        public static bool RestartRequired { get { return JonSyncTransport.RestartRequired; } set { JonSyncTransport.RestartRequired = value; } }
        static void ScheduleReload() { if (!_shutting && _timer != null) _timer.Change(2700, Timeout.Infinite); }
        static volatile bool _pending;
        static volatile bool _pendingMods;
        static volatile bool _pendingGameXml;
        static volatile int _lastEventTicks;
        static volatile int _lastModsEventTicks;
        static volatile int _lastGameTicks;

        public static bool WatchEnabled = true;
        public static DateTime LastXmlReloadUtc = DateTime.MinValue;
        public static DateTime LastCodeReloadUtc = DateTime.MinValue;
        public static string LastCodeResult = "(none yet)";
        static volatile bool _shutting; // set by Shutdown() before a core hot-swap
        internal static bool Retired { get { return _shutting; } }

        // ---------------------------------------------------------------
        // init
        // ---------------------------------------------------------------
        public static void Init(Mod mod)
        {
            HostMod = mod;
            try
            {
                ModDir = (mod != null && !string.IsNullOrEmpty(mod.Path))
                    ? mod.Path
                    : Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            catch { ModDir = "."; }
            if (string.IsNullOrEmpty(ModDir)) ModDir = ".";
            HotpackDir = Path.Combine(ModDir, "hotpacks");
            try { Directory.CreateDirectory(HotpackDir); } catch { }

            HookAssemblyResolve();

            Log.Out("[HotReload] v" + Version + " init; hotpacks: " + HotpackDir);
            try { ReloadAllPacks(null); } catch (Exception e) { Log.Error("[HotReload] initial packs: " + e); }
            try { ModCode.InstallLoadInterceptor(); } catch (Exception e) { Log.Error("[HotReload] load intercept: " + e.Message); }
            try { ModCode.Init(); } catch (Exception e) { Log.Error("[HotReload] srclive init: " + e.Message); }

            try { ModBrowser.Init(ModDir); } catch (Exception e) { Log.Error("[HotReload] browser init: " + e.Message); }
            try { FriendSync.Init(); } catch (Exception e) { Log.Error("[HotReload] friend sync init: " + e.Message); }
            try { BrowserUi.Init(); } catch (Exception e) { Log.Error("[HotReload] browser ui init: " + e.Message); }
            try { ModScanner.SeedAttempted(); } catch (Exception e) { Log.Error("[HotReload] scanner seed: " + e.Message); }
            try { ModScanner.SeedLoadedMarkers(); } catch (Exception e) { Log.Error("[HotReload] scanner markers: " + e.Message); }

            _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
            SetWatch(true);
            var commandDir = Path.Combine(ModDir, "browser");
            Directory.CreateDirectory(commandDir);
            _commandWatcher = new FileSystemWatcher(commandDir, "cmd.txt");
            _commandWatcher.Changed += (s, e) => ScheduleReload();
            _commandWatcher.Created += (s, e) => ScheduleReload();
            _commandWatcher.Renamed += (s, e) => ScheduleReload();
            _commandWatcher.EnableRaisingEvents = true;

            // first sweep shortly after boot: compile + apply src-live mod code
            _pendingMods = true;
            _lastModsEventTicks = Environment.TickCount;
            ScheduleReload();

            // boot-time API self-check: a game update that changes engine APIs
            // shows up in the log immediately instead of failing later mid-reload
            try { Log.Out("[HotReload] " + Doctor.Run(false)); } catch (Exception e) { Log.Warning("[HotReload] doctor: " + e.Message); }
        }

        [ThreadStatic] static bool _resolving;

        static void HookAssemblyResolve()
        {
            // help hotpacks resolve game/managed dlls from their own dirs
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += (s, a) =>
                {
                    // reentrancy guard: a resolve chain that re-fires for the same
                    // load would recurse forever -> uncatchable StackOverflow kills
                    // the process. While already probing on this thread, return null.
                    if (_resolving) return null;
                    _resolving = true;
                    try
                    {
                        var shortName = new AssemblyName(a.Name).Name;
                        if (string.IsNullOrEmpty(shortName)) return null;
                        string[] probe =
                        {
                            HotpackDir,
                            ModDir,
                            Path.Combine(ModDir, "lib"),
                        };
                        foreach (var d in probe)
                        {
                            if (string.IsNullOrEmpty(d)) continue;
                            var f = Path.Combine(d, shortName + ".dll");
                            if (File.Exists(f))
                            {
                                var bytes = File.ReadAllBytes(f);
                                return Assembly.Load(bytes);
                            }
                        }
                    }
                    catch { }
                    finally { _resolving = false; }
                    return null;
                };
            }
            catch { }
        }

        // ---------------------------------------------------------------
        // code hotpacks
        // ---------------------------------------------------------------
        public static void ReloadAllPacks(string onlyPackName)
        {
            lock (_lock)
            {
                if (!Directory.Exists(HotpackDir)) return;
                var files = Directory.GetFiles(HotpackDir, "*.dll");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                int ok = 0, fail = 0, considered = 0;
                foreach (var f in files)
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (onlyPackName != null && !string.Equals(name, onlyPackName, StringComparison.OrdinalIgnoreCase)) continue;
                    considered++;
                    if (ReloadPack(f)) ok++; else fail++;
                }
                if (onlyPackName == null && _harmonies.Count > 0)
                {
                    var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var f in files) alive.Add(Path.GetFileNameWithoutExtension(f));
                    var dead = new List<string>();
                    foreach (var kv in _harmonies) if (!alive.Contains(kv.Key)) dead.Add(kv.Key);
                    foreach (var k in dead)
                    {
                        try { _harmonies[k].UnpatchSelf(); } catch { }
                        try { Harmony.UnpatchID("hotreload." + k); } catch { }
                        _harmonies.Remove(k);
                        Log.Out("[HotReload] pack '" + k + "' removed from disk; patches swept");
                    }
                }
                LastCodeResult = considered + " pack(s): " + ok + " ok, " + fail + " failed";
                LastCodeReloadUtc = DateTime.UtcNow;
            }
        }

        public static string ReloadPacksReport(string onlyPackName)
        {
            ReloadAllPacks(onlyPackName);
            var msg = "[HotReload] code reload -> " + LastCodeResult;
            var errs = ErrorsText();
            if (errs != null) msg += "\n" + errs;
            return msg;
        }

        static bool ReloadPack(string path)
        {
            var key = Path.GetFileNameWithoutExtension(path);
            var id = "hotreload." + key;

            // 1) unpatch the previous incarnation. UnpatchID first: it works across
            // Harmony instances, so it also clears patches left by an older core.
            try { Harmony.UnpatchID(id); } catch { }
            Harmony old;
            if (_harmonies.TryGetValue(key, out old))
            {
                // guard: never unpatch while a patched target is executing (freeze risk)
                int onStack = 0;
                try
                {
                    foreach (var orig in Harmony.GetAllPatchedMethods())
                    {
                        var pin = Harmony.GetPatchInfo(orig);
                        if (pin == null) continue;
                        bool ours = false;
                        foreach (var list in new[] { pin.Prefixes, pin.Postfixes, pin.Transpilers, pin.Finalizers, pin.ILManipulators })
                        {
                            if (list == null) continue;
                            foreach (var p in list) { if (p != null && p.owner == id) { ours = true; break; } }
                            if (ours) break;
                        }
                        if (ours && IsOnCurrentStack(orig)) onStack++;
                    }
                }
                catch { }
                if (onStack > 0)
                {
                    int dc; _deferCount.TryGetValue(key, out dc); dc++; _deferCount[key] = dc;
                    _lastErrors[key] = "deferred: " + onStack + " patched method(s) executing";
                    if (dc <= 8)
                    {
                        Log.Warning("[HotReload] pack '" + key + "': reload deferred (" + onStack + " patched method(s) executing now); retrying");
                        _lastEventTicks = Environment.TickCount; _pending = true;
                    }
                    else if (dc == 9)
                    {
                        Log.Warning("[HotReload] pack '" + key + "': still deferred after " + dc + " tries (target stays hot); will retry on the next file change");
                    }
                    return false;
                }
                try { old.UnpatchSelf(); }
                catch (Exception e) { Log.Error("[HotReload] unpatch " + key + ": " + e.Message); }
                try { Harmony.UnpatchID(id); }
                catch (Exception e) { Log.Error("[HotReload] unpatchID " + key + ": " + e.Message); }
            }

            // 2) read bytes now (file stays unlocked; assembly is a fresh copy)
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception e) { _lastErrors[key] = "read: " + e.Message; return false; }

            Assembly asm;
            try { asm = Assembly.Load(bytes); }
            catch (Exception e) { _lastErrors[key] = "load: " + e.Message; Log.Error("[HotReload] load " + key + ": " + e.Message); return false; }

            // 3) (re)apply [HarmonyPatch] classes
            var h = new Harmony(id);
            _harmonies[key] = h;
            int classCount = 0, classFails = 0;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }

            foreach (var t in types)
            {
                if (t == null) continue;
                if (!HasPatchAnnotations(t)) continue;
                classCount++;
                try { h.PatchAll(t); }
                catch (Exception e)
                {
                    classFails++;
                    _lastErrors[key] = t.Name + ": " + e.Message;
                    Log.Error("[HotReload] patch " + key + "." + t.Name + ": " + e.Message);
                }
            }

            // 4) optional IModApi entry points (called after patching)
            foreach (var t in types)
            {
                if (t == null || t.IsAbstract || t.IsInterface) continue;
                if (!typeof(IModApi).IsAssignableFrom(t)) continue;
                try
                {
                    var inst = (IModApi)Activator.CreateInstance(t);
                    inst.InitMod(HostMod);
                }
                catch (Exception e)
                {
                    _lastErrors[key] = "InitMod(" + t.Name + "): " + e.Message;
                    Log.Error("[HotReload] InitMod " + key + "." + t.Name + ": " + e.Message);
                }
            }

            if (classFails > 0)
            {
                Log.Out("[HotReload] pack '" + key + "' reloaded with " + classFails + " failed patch class(es)");
                return false;
            }
            _lastErrors.Remove(key);
            _deferCount.Remove(key);
            Log.Out("[HotReload] pack '" + key + "' reloaded (" + classCount + " patch class(es))");
            return true;
        }

        internal static bool HasPatchAnnotations(Type t)
        {
            try
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0) return true;
                var ms = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var m in ms)
                {
                    if (m.GetCustomAttributes(typeof(HarmonyPrefix), false).Length > 0) return true;
                    if (m.GetCustomAttributes(typeof(HarmonyPostfix), false).Length > 0) return true;
                    if (m.GetCustomAttributes(typeof(HarmonyTranspiler), false).Length > 0) return true;
                    if (m.GetCustomAttributes(typeof(HarmonyFinalizer), false).Length > 0) return true;
                }
            }
            catch { }
            return false;
        }

        public static string ErrorsText()
        {
            if (_lastErrors.Count == 0) return null;
            var s = "[HotReload] errors:";
            foreach (var kv in _lastErrors) s += "\n  " + kv.Key + ": " + kv.Value;
            return s;
        }

        // ---------------------------------------------------------------
        // watchers
        // ---------------------------------------------------------------
        public static void SetWatch(bool on)
        {
            // hotpacks watcher
            if (_watcher != null)
            {
                try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
                _watcher = null;
            }
            // mods-folder watchers
            foreach (var w in _modWatchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            _modWatchers.Clear();
            // game config watchers
            foreach (var w in _gameWatchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            _gameWatchers.Clear();

            WatchEnabled = on;
            if (!on) return;

            try
            {
                var w = new FileSystemWatcher(HotpackDir, "*.dll");
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime;
                w.IncludeSubdirectories = false;
                FileSystemEventHandler h = (s, e) => { _lastEventTicks = Environment.TickCount; _pending = true; ScheduleReload(); };
                RenamedEventHandler r = (s, e) => { _lastEventTicks = Environment.TickCount; _pending = true; ScheduleReload(); };
                w.Changed += h; w.Created += h; w.Deleted += h; w.Renamed += r;
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception e)
            {
                Log.Error("[HotReload] watcher: " + e.Message);
            }

            try
            {
                foreach (var root in ModScanner.Roots())
                {
                    if (!Directory.Exists(root)) continue;
                    var w = new FileSystemWatcher(root, "*.*");
                    w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.DirectoryName;
                    w.IncludeSubdirectories = true;
                    FileSystemEventHandler h = (s, e) => ModsChanged(e.FullPath);
                    RenamedEventHandler r = (s, e) => ModsChanged(e.FullPath);
                    w.Changed += h; w.Created += h; w.Deleted += h; w.Renamed += r;
                    w.EnableRaisingEvents = true;
                    _modWatchers.Add(w);
                    Log.Out("[HotReload] watching mods folder: " + root);
                }
            }
            catch (Exception e)
            {
                Log.Error("[HotReload] mods watcher: " + e.Message);
            }

            // game config: watch the ENGINE's XML (Data\Config + Data\Config\XUi).
            // If a game update drops new files or you tweak vanilla XML live, the
            // reload fires automatically - engine updates degrade gracefully.
            try
            {
                var cfg = GameConfigDir();
                if (!string.IsNullOrEmpty(cfg) && Directory.Exists(cfg))
                {
                    var w = new FileSystemWatcher(cfg, "*.xml");
                    w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime;
                    w.IncludeSubdirectories = true;
                    FileSystemEventHandler gh = (s2, e2) => { _lastGameTicks = Environment.TickCount; _pendingGameXml = true; ScheduleReload(); };
                    RenamedEventHandler gr = (s2, e2) => { _lastGameTicks = Environment.TickCount; _pendingGameXml = true; ScheduleReload(); };
                    w.Changed += gh; w.Created += gh; w.Deleted += gh; w.Renamed += gr;
                    w.EnableRaisingEvents = true;
                    _gameWatchers.Add(w);
                    Log.Out("[HotReload] watching game config XML: " + cfg);
                }
            }
            catch (Exception e) { Log.Error("[HotReload] game config watcher: " + e.Message); }
        }

        // the game's Data\Config folder (engine XML)
        public static string GameConfigDir()
        {
            try
            {
                var dp = UnityEngine.Application.dataPath;      // ...\7DaysToDie_Data
                if (string.IsNullOrEmpty(dp)) return null;
                var root = Path.GetDirectoryName(dp);
                if (string.IsNullOrEmpty(root)) return null;
                var cfg = Path.Combine(root, "Data", "Config");
                return cfg;
            }
            catch { return null; }
        }

        static void ModsChanged(string path)
        {
            var host = Path.GetFullPath(ModDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(path).StartsWith(host, StringComparison.OrdinalIgnoreCase) || FriendSync.Installing) return;
            _lastModsEventTicks = Environment.TickCount;
            _pendingMods = true;
            BrowserUi.InvalidateInstalled();
            ScheduleReload();
        }

        // ------------------------------------------------------------- cmd-file channel
        // <mod>\browser\cmd.txt: write one tool command (e.g. "browser open", "browser shot p1")
        // A file-change event reads it after debounce and runs it on the main thread.
        // Lets the tool be driven remotely (chat/automation) while the game runs.
        static string _cmdFile;
        static long _cmdSeenTicks = -1;

        static void ReadCommandFile()
        {
            if (_cmdFile == null)
            {
                // the tool's own mod folder (HostMod.Path); the core assembly is byte-loaded
                // so Assembly.Location is empty here
                try { _cmdFile = Path.Combine(HotReloadCore.ModDir ?? "", "browser", "cmd.txt"); }
                catch { _cmdFile = ""; }
            }
            if (string.IsNullOrEmpty(_cmdFile) || !File.Exists(_cmdFile)) return;
            long t;
            try { t = File.GetLastWriteTimeUtc(_cmdFile).Ticks; } catch { return; }
            if (t == _cmdSeenTicks) return;
            _cmdSeenTicks = t;
            string line;
            try { line = File.ReadAllText(_cmdFile).Trim(); } catch { return; }
            if (line.Length == 0) return;
            try { File.Delete(_cmdFile); } catch { }
            RunOnMainThread(() =>
            {
                try
                {
                    var args = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var r = CoreEntry.Execute(new List<string>(args));
                    Log.Out("[HotReload] cmd-file '" + line + "' -> " + (r ?? "").Replace("\n", " | "));
                }
                catch (Exception e) { Log.Warning("[HotReload] cmd-file: " + e.Message); }
            });
        }

        static void OnTimer(object state)
        {
            if (_shutting || RestartRequired) return;
            try { ReadCommandFile(); } catch { }

            try
            {
                if (_pending)
                {
                    if (unchecked(Environment.TickCount - _lastEventTicks) >= 1200)
                    {
                        _pending = false;
                        RunOnMainThread(() =>
                        {
                            try
                            {
                                ReloadAllPacks(null);
                                Log.Out("[HotReload] auto reload (watch): " + LastCodeResult);
                                Push("[HotReload] auto reload (watch): " + LastCodeResult);
                            }
                            catch (Exception e) { Log.Error("[HotReload] auto reload: " + e.Message); }
                        });
                    }
                }

                if (_pendingGameXml)
                {
                    if (unchecked(Environment.TickCount - _lastGameTicks) >= 2500)
                    {
                        _pendingGameXml = false;
                        RunOnMainThread(() =>
                        {
                            try
                            {
                                Log.Out("[HotReload] game config XML changed on disk -> reloading");
                                ReloadXml();
                                if (!GameManager.IsDedicatedServer) ReloadXui();
                            }
                            catch (Exception e) { Log.Error("[HotReload] game config reload: " + e.Message); }
                        });
                    }
                }

                if (_pendingMods)
                {
                    if (unchecked(Environment.TickCount - _lastModsEventTicks) >= 2500)
                    {
                            _pendingMods = false;
                            RunOnMainThread(() =>
                            {
                                try
                                {
                                    FriendSync.HostModsChanged();
                                    var report = ModScanner.ScanAndLoad(false, null);
                                    var changed = ModScanner.ChangedLoadedMods();
                                    if (report.loaded.Count > 0)
                                    {
                                        Log.Out("[HotReload] auto mod scan: " + report.summary);
                                        Push("[HotReload] new mod(s) loaded: " + Join(report.loaded));
                                        Push(RefreshAfterNewMods(report));
                                    }
                                    if (changed.Count > 0)
                                    {
                                        Push("[HotReload] mod change detected: " + Join(changed) + " -> re-applying");
                                        Push(RefreshExistingMods(changed));
                                    }
                                    try { ModCode.ProcessSrcChangesAsync(false); }
                                    catch (Exception e) { Log.Error("[HotReload] srclive: " + e.Message); }
                                    if (report.loaded.Count == 0 && changed.Count == 0 && report.failed.Count > 0)
                                    {
                                        Log.Error("[HotReload] mod scan failures: " + Join(report.failed));
                                    }
                                }
                                catch (Exception e) { Log.Error("[HotReload] auto mod scan: " + e.Message); }
                            });
                    }
                }
            }
            catch { }
        }

        public static string Join(List<string> l)
        {
            var s = "";
            for (int i = 0; i < l.Count; i++) { if (i > 0) s += ", "; s += l[i]; }
            return s;
        }

        // ---------------------------------------------------------------
        // runtime mod loading
        // ---------------------------------------------------------------
        public static string ReloadModsReport(string onlyFolder)
        {
            var report = ModScanner.ScanAndLoad(true, onlyFolder);
            var msg = "[HotReload] mod scan -> " + report.summary;
            if (report.loaded.Count > 0) msg += "\n" + RefreshAfterNewMods(report);
            if (report.failed.Count > 0) msg += "\n  failed: " + Join(report.failed);
            if (report.vanished.Count > 0) msg += "\n  gone from disk: " + Join(report.vanished) + " (use 'hr unload <name>' to drop them)";
            if (report.deferred)
            {
                // a folder was still being copied: arm the watcher to pick it up the moment it settles
                msg += "\n  (a folder is still changing; will auto-load when it settles)";
                _pendingMods = true;
                _lastModsEventTicks = Environment.TickCount;
            }
            return msg;
        }

        // drop a mod's XML/locale patches live (folder deleted or user asks).
        // NOTE: a code mod's Harmony patches cannot be unloaded from the runtime;
        // this removes it from the mod list so its XML no longer applies.
        public static string UnloadModReport(string which)
        {
            if (string.IsNullOrEmpty(which)) return "[HotReload] usage: hr unload <modName|folderName>";
            Mod target = null;
            try
            {
                var mods = ModManager.GetLoadedMods();
                if (mods != null)
                {
                    foreach (var m in mods)
                    {
                        if (m == null) continue;
                        if (string.Equals(m.Name, which, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.FolderName, which, StringComparison.OrdinalIgnoreCase)
                            || (m.Path != null && string.Equals(Path.GetFileName(m.Path), which, StringComparison.OrdinalIgnoreCase)))
                        { target = m; break; }
                    }
                }
            }
            catch (Exception e) { return "[HotReload] unload lookup failed: " + e.Message; }
            if (target == null) return "[HotReload] no loaded mod matches '" + which + "'";

            try { ModManager.loadedMods.Remove(target.Name); }
            catch (Exception e) { return "[HotReload] unload failed: " + e.Message; }

            int swept = 0;
            try { swept = ModCode.SweepModAssemblies(target); } catch (Exception e) { Log.Error("[HotReload] sweep on unload: " + e.Message); }

            var sb = "[HotReload] unloaded mod '" + target.Name + "' (" + target.FolderName + ").\n";
            try { ModScanner.ForgetFolder(target.Path); } catch { }
            try { sb += ReloadLoc() + "\n"; } catch (Exception e) { sb += "[HotReload] loc: " + e.Message + "\n"; }
            try { sb += ReloadXml() + "\n"; } catch (Exception e) { sb += "[HotReload] xml: " + e.Message + "\n"; }
            try
            {
                if (!GameManager.IsDedicatedServer) sb += ReloadXui() + "\n";
            }
            catch { }
            sb += "Removed " + swept + " Harmony patch(es) from its code (its assembly stays in memory but is now inert).";
            Log.Out(sb);
            return sb;
        }

        // manual: re-apply an already-loaded mod after its files changed on disk
        public static string ReapplyModReport(string which)
        {
            if (string.IsNullOrEmpty(which)) return "[HotReload] usage: hr reapply <modName|folderName>";
            var names = new List<string>();
            try
            {
                var mods = ModManager.GetLoadedMods();
                if (mods != null)
                {
                    foreach (var m in mods)
                    {
                        if (m == null) continue;
                        if (string.Equals(m.Name, which, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.FolderName, which, StringComparison.OrdinalIgnoreCase))
                        { names.Add(m.FolderName); }
                    }
                }
            }
            catch (Exception e) { return "[HotReload] reapply lookup failed: " + e.Message; }
            if (names.Count == 0) return "[HotReload] no loaded mod matches '" + which + "'";
            return RefreshExistingMods(names);
        }

        // after new mods are registered: rebuild atlases+localizations, re-apply XML, rebuild UI
        static string RefreshAfterNewMods(ModScanner.ScanReport report)
        {
            var sb = "";
            try
            {
                var cmds = ModScanner.RegisterCommands(report.newMods);
                sb += "[HotReload] registered " + cmds + " console command(s) from new mods.\n";
            }
            catch (Exception e) { sb += "[HotReload] command registration failed: " + e.Message + "\n"; }

            try
            {
                ThreadManager.RunCoroutineSync(ModManager.LoadPatchStuff(false));
                sb += "[HotReload] atlases + localizations refreshed.\n";
            }
            catch (Exception e) { sb += "[HotReload] atlas/loc refresh failed: " + e.Message + "\n"; }

            try
            {
                var t0 = DateTime.UtcNow;
                WorldStaticData.ReloadAllXmlsSync();
                LastXmlReloadUtc = DateTime.UtcNow;
                sb += "[HotReload] XML reloaded in " + (DateTime.UtcNow - t0).TotalSeconds.ToString("0.0") + "s (new mods' patches applied).\n";
            }
            catch (Exception e) { sb += "[HotReload] XML reload failed: " + e.Message + "\n"; }

            try
            {
                if (!GameManager.IsDedicatedServer)
                {
                    SdtdConsole.Instance.ExecuteSync("xui reload", null);
                    sb += "[HotReload] XUI reloaded.\n";
                }
            }
            catch (Exception e) { sb += "[HotReload] XUI reload failed: " + e.Message + "\n"; }

            try { ModCode.ProcessSrcChangesAsync(false); sb += "[HotReload] src-live: building new mod code (result in console)...\n"; } catch (Exception e) { sb += "[HotReload] src-live: " + e.Message + "\n"; }

            return sb;
        }

        // an ALREADY-loaded mod's files changed on disk: re-apply patches, refresh UI.
        // C# code inside a loaded mod cannot be swapped (only hotpacks can),
        // but XML/localization/XUI changes take effect here.
        static string RefreshExistingMods(List<string> modNames)
        {
            var sb = "[HotReload] re-applying after changes in: " + Join(modNames) + "\n";
            try { sb += ReloadLoc() + "\n"; } catch (Exception e) { sb += "[HotReload] loc: " + e.Message + "\n"; }
            try { sb += ReloadXml() + "\n"; } catch (Exception e) { sb += "[HotReload] xml: " + e.Message + "\n"; }
            try
            {
                if (!GameManager.IsDedicatedServer) sb += ReloadXui() + "\n";
                else sb += "[HotReload] XUI skipped (dedicated).\n";
            }
            catch (Exception e) { sb += "[HotReload] xui: " + e.Message + "\n"; }
            try { sb += ReloadPacksReport(null) + "\n"; } catch (Exception e) { sb += "[HotReload] code: " + e.Message + "\n"; }
            try { ModCode.ProcessSrcChangesAsync(false); sb += "[HotReload] src-live: recompiling changed mod code (~seconds; result in console)\n"; } catch (Exception e) { sb += "[HotReload] src-live: " + e.Message + "\n"; }
            Log.Out(sb);
            return sb;
        }

        // ---------------------------------------------------------------
        // reload primitives
        // ---------------------------------------------------------------
        public static string ReloadXml()
        {
            var t0 = DateTime.UtcNow;
            WorldStaticData.ReloadAllXmlsSync();
            LastXmlReloadUtc = DateTime.UtcNow;
            var msg = "[HotReload] XML reloaded in " + (DateTime.UtcNow - t0).TotalSeconds.ToString("0.0") + "s.";
            Log.Out(msg);
            return msg;
        }

        public static string ReloadXui()
        {
            if (GameManager.IsDedicatedServer)
                return "[HotReload] XUI reload skipped: dedicated server has no local UI.";
            SdtdConsole.Instance.ExecuteSync("xui reload", null);
            var msg = "[HotReload] XUI reloaded.";
            Log.Out(msg);
            return msg;
        }

        public static string ReloadLoc()
        {
            Localization.ReloadBaseLocalization();
            ThreadManager.RunCoroutineSync(ModManager.LoadLocalizations(false));
            var msg = "[HotReload] localization reloaded.";
            Log.Out(msg);
            return msg;
        }

        public static string ReloadAll()
        {
            var t0 = DateTime.UtcNow;
            var sb = "";
            try { sb += ReloadModsReport(null) + "\n"; } catch (Exception e) { sb += "[HotReload] mod scan FAILED: " + e.Message + "\n"; }
            try { sb += ReloadLoc() + "\n"; } catch (Exception e) { sb += "[HotReload] loc FAILED: " + e.Message + "\n"; }
            try { sb += ReloadXml() + "\n"; } catch (Exception e) { sb += "[HotReload] xml FAILED: " + e.Message + "\n"; }
            try
            {
                if (!GameManager.IsDedicatedServer) { sb += ReloadXui() + "\n"; }
                else { sb += "[HotReload] XUI skipped (dedicated).\n"; }
            }
            catch (Exception e) { sb += "[HotReload] xui FAILED: " + e.Message + "\n"; }
            try { sb += ReloadPacksReport(null) + "\n"; } catch (Exception e) { sb += "[HotReload] code FAILED: " + e.Message + "\n"; }
            try { ModCode.ProcessSrcChangesAsync(false); sb += "[HotReload] src-live: building changed mod code (result in console)...\n"; } catch (Exception e) { sb += "[HotReload] src-live FAILED: " + e.Message + "\n"; }
            sb += "[HotReload] TOTAL " + (DateTime.UtcNow - t0).TotalSeconds.ToString("0.0") + "s";
            Log.Out(sb);
            return sb;
        }

        public static void Push(string s) { try { SdtdConsole.Instance.Output(s); } catch { } }

        // let the watcher do one more pass (picks up edits that landed while a build was running)
        public static void RecheckSoon()
        {
            if (_shutting) return;
            try { _lastModsEventTicks = Environment.TickCount; _pendingMods = true; ScheduleReload(); } catch { }
        }

        // -- stack safety -------------------------------------------------
        // Unpatching a method whose frame is on OUR call stack re-JITs the
        // executing method on the same thread -> Mono deadlocks the main thread.
        // Always check before unpatching anything.
        static bool SameMethod(MethodBase a, MethodBase b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            try { return a.MetadataToken == b.MetadataToken && a.Module == b.Module; } catch { return false; }
        }

        public static bool IsOnCurrentStack(MethodBase m)
        {
            if (m == null) return false;
            try
            {
                var st = new StackTrace(false);
                int n = st.FrameCount;
                for (int i = 0; i < n; i++)
                {
                    var f = st.GetFrame(i);
                    if (f == null) continue;
                    if (SameMethod(f.GetMethod(), m)) return true;
                }
            }
            catch { }
            return false;
        }

        // ---------------------------------------------------------------
        // command dispatch (called by the bootstrap's 'hr' command)
        // ---------------------------------------------------------------
        public static string ExecuteCommand(List<string> args)
        {
            string sub = (args.Count > 0 ? args[0] : "status").ToLowerInvariant();
            if (RestartRequired && sub != "status" && sub != "doctor" && sub != "browser" && sub != "pack" && sub != "sync" && sub != "help")
                return "[HotReload] Restart the game after changing the multiplayer pack before reloading mods";
            switch (sub)
            {
                case "mods": case "scan": case "loadmods":
                    return ReloadModsReport(args.Count > 1 ? args[1] : null);
                case "unload": case "drop":
                    return UnloadModReport(args.Count > 1 ? args[1] : null);
                case "reapply": case "refresh":
                    return ReapplyModReport(args.Count > 1 ? args[1] : null);
                case "xml": case "reload-xml":
                    return ReloadXml();
                case "xui": case "reload-xui":
                    return ReloadXui();
                case "loc": case "local": case "localization":
                    return ReloadLoc();
                case "code": case "dll":
                    return ReloadPacksReport(args.Count > 1 ? args[1] : null);
                case "build": case "swap": case "srclive":
                    return ModCode.BuildReport(args.Count > 1 ? args[1] : null);
                case "all": case "reload":
                    return ReloadAll();
                case "browser": case "mods-browser": case "browse":
                    return ModBrowser.ConsoleCmd(args);
                case "pack": case "sync": case "serverpack":
                    var browserArgs = new List<string>(args);
                    browserArgs.Insert(0, "browser");
                    return ModBrowser.ConsoleCmd(browserArgs);
                case "watch":
                    {
                        bool on = true;
                        if (args.Count > 1 && (args[1].Equals("off", StringComparison.OrdinalIgnoreCase) || args[1] == "0")) on = false;
                        SetWatch(on);
                        return "[HotReload] watch " + (on ? "ON" : "OFF");
                    }
                case "doctor":
                    return Doctor.Run(true);
                case "status":
                    return Status();
                default:
                    return HelpText();
            }
        }

        public static string Status()
        {
            var s = "[HotReload] core v" + Version + "  watch=" + (WatchEnabled ? "ON" : "OFF") + "\n";
            try { s += "  browser: " + ModBrowser.Status + " (" + ModBrowser.InstalledMap.Count + " installed via browser)\n"; } catch { }
            try
            {
                var files = Directory.Exists(HotpackDir) ? Directory.GetFiles(HotpackDir, "*.dll") : new string[0];
                s += "  hotpacks dir: " + HotpackDir + " (" + files.Length + " dll)\n";
                foreach (var f in files) s += "    - " + Path.GetFileName(f) + "\n";
            }
            catch (Exception e) { s += "  hotpacks: " + e.Message + "\n"; }
            try
            {
                var roots = ModScanner.Roots();
                s += "  mods roots watched:\n";
                foreach (var r in roots) s += "    - " + r + "\n";
                var loaded = ModManager.GetLoadedMods();
                s += "  loaded mods: " + (loaded != null ? loaded.Count : 0) + "; last scan: " + (ModScanner.LastScanUtc == DateTime.MinValue ? "(never)" : ModScanner.LastScanUtc.ToString("HH:mm:ss")) + "\n";
            }
            catch (Exception e) { s += "  mods: " + e.Message + "\n"; }
            s += "  last code reload: " + (LastCodeReloadUtc == DateTime.MinValue ? "(never)" : LastCodeReloadUtc.ToString("HH:mm:ss")) + " -> " + LastCodeResult + "\n";
            s += "  last xml reload:  " + (LastXmlReloadUtc == DateTime.MinValue ? "(never)" : LastXmlReloadUtc.ToString("HH:mm:ss")) + "\n";
            try { s += ModCode.StatusText(); } catch (Exception e) { s += "  srclive: " + e.Message + "\n"; }
            s += "  doctor: " + Doctor.LastSummary + "\n";
            var errs = ErrorsText();
            if (errs != null) s += errs;
            return s;
        }

        public static string HelpText()
        {
            return "HotReloadTool core v" + Version + "\n" +
                   "Usage:\n" +
                   "  hr mods [folder]  scan Mods folders and load NEW mod folders live\n" +
                   "  hr unload <name>  drop a loaded mod's XML/locale patches + sweep its code\n" +
                   "  hr reapply <name> re-apply a loaded mod after its files changed\n" +
                   "  hr xml      reload all XML from disk (re-applies mod patches)\n" +
                   "  hr xui      reload XUI windows\n" +
                   "  hr loc      reload localization\n" +
                   "  hr code [n] reload hotpacks/*.dll (optionally just pack <n>)\n" +
                   "  hr build [m] recompile+swap src-live code / rebuilt dlls (all if omitted)\n" +
                   "  hr doctor   check engine API compatibility\n" +
                   "  hr all      mods + loc + xml + xui + code + build\n" +
                   "  hr watch on|off | hr status\n" +
                   "  hr core status|reload|rollback   hot-swap the tool itself\n" +
                   "\nMod Browser (7d2dmods.gg, version-matched):\n" +
                   "  hr browser open            open the browser window\n" +
                   "  hr browser search <words>  search the catalog\n" +
                   "  hr browser v3 | all        V3-compatible only / every version\n" +
                   "  hr browser sort <" + string.Join("|", ModBrowser.SortNames) + ">\n" +
                   "  hr browser page <n> | list | installed\n" +
                   "  hr browser install <#|slug>   download + install; restart for startup-dependent mods\n" +
                   "  hr browser remove <folder>\n" +
                   "Hotpacks live in " + HotpackDir;
        }

        // ---------------------------------------------------------------
        // shutdown (called by the bootstrap just before it hot-swaps this core)
        // ---------------------------------------------------------------
        public static void Shutdown()
        {
            _shutting = true;
            try { FriendSync.Shutdown(); } catch { }
            try { if (_commandWatcher != null) _commandWatcher.Dispose(); } catch { }
            try { BrowserUi.Shutdown(); } catch { }
            try { if (_timer != null) { _timer.Dispose(); _timer = null; } } catch { }
            try { SetWatch(false); } catch { }
            int swept = 0;
            try { swept = ShutdownSweep(); } catch (Exception e) { Log.Error("[HotReload] shutdown sweep: " + e.Message); }
            Log.Out("[HotReload] core v" + Version + " shutting down (" + swept + " patch(es) swept) - making way for a new core");
        }

        static int ShutdownSweep()
        {
            int total = 0;
            // remove ONLY this core's own patches:
            //  - the loader intercept it installed (id hotreload.loader)
            //  - anything whose patch method lives in the core assembly itself
            // Mod patches are deliberately left attached: the new core re-applies
            // hotpacks on init, recompiles src-live mods on its first sweep, and
            // dll-swap / runtime-loaded mods keep running untouched throughout.
            try { Harmony.UnpatchID("hotreload.loader"); } catch { }
            try { total += ModCode.SweepAssembly(typeof(HotReloadCore).Assembly); } catch { }
            return total;
        }

        // ---------------------------------------------------------------
        // boot doctor hook
        // ---------------------------------------------------------------
        public static void AnnounceDoctor()
        {
            try { Log.Out("[HotReload] doctor: " + Doctor.Run(false)); } catch { }
        }

        public static void RunOnMainThread(Action a)
        {
            try { ThreadManager.AddSingleTaskMainThread("HotReload.apply", () => { if (!_shutting) a(); }); }
            catch (Exception e) { throw new InvalidOperationException("Cannot schedule game main-thread operation", e); }
        }

        // blocking main-thread call from a background thread (install workers, pack apply):
        // Wait for that task's completion event, with a deadline for a stalled main thread.
        public static T RunOnMainThreadWithResult<T>(Func<T> f)
        {
            if (ThreadManager.IsMainThread()) return f();
            var done = new ManualResetEventSlim(false);
            T result = default(T);
            Exception err = null;
            RunOnMainThread(() =>
            {
                try { result = f(); }
                catch (Exception e) { err = e; }
                finally { try { done.Set(); } catch { } }
            });
            if (!done.Wait(30000))
            {
                Log.Warning("[HotReload] main-thread call timed out after 30s");
                throw new Exception("main thread timeout");
            }
            if (err != null) throw err;
            return result;
        }
    }

    // -------------------------------------------------------------------
    // runtime mod-folder scanner + loader
    // -------------------------------------------------------------------
    public static class ModScanner
    {
        class FolderState
        {
            public int Count;
            public long Bytes;
            public DateTime NewestWriteUtc;
            public DateTime SnapshotAtUtc;
        }

        public class ScanReport
        {
            public List<string> loaded = new List<string>();
            public List<string> failed = new List<string>();
            public List<string> vanished = new List<string>();
            public List<Mod> newMods = new List<Mod>();
            public string summary = "(no scan)";
            public bool deferred = false;
        }

        public static void ForgetFolder(string path)
        {
            try { _attempted.Remove(Norm(path)); } catch { }
            try { _snap.Remove(Norm(path)); } catch { }
        }

        // loaded mods whose folder no longer exists on disk
        public static List<string> FindVanished()
        {
            var gone = new List<string>();
            try
            {
                var mods = ModManager.GetLoadedMods();
                if (mods == null) return gone;
                foreach (var m in mods)
                {
                    if (m == null || string.IsNullOrEmpty(m.Path)) continue;
                    if (string.Equals(m.Name, "TFP_Harmony", StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (!Directory.Exists(m.Path)) gone.Add(m.Name); } catch { }
                }
            }
            catch { }
            return gone;
        }

        static readonly Dictionary<string, FolderState> _snap = new Dictionary<string, FolderState>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> _attempted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // folder -> ModInfo mtime marker
        static readonly Dictionary<string, string> _loadedMarkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // loaded mod folder -> marker at last apply
        public static DateTime LastScanUtc = DateTime.MinValue;

        // record markers for every mod that is already loaded when the tool boots
        public static void SeedLoadedMarkers()
        {
            try
            {
                var mods = ModManager.GetLoadedMods();
                if (mods == null) return;
                foreach (var m in mods)
                {
                    if (m == null || string.IsNullOrEmpty(m.Path)) continue;
                    if (IsHostFolder(m.Path)) continue;
                    try { if (Directory.Exists(m.Path)) _loadedMarkers[Norm(m.Path)] = FolderMarker(m.Path); } catch { }
                }
            }
            catch { }
        }

        internal static void RecordApplied(string folder)
        {
            _loadedMarkers[Norm(folder)] = FolderMarker(folder);
        }

        // our own mod's folder (hotpacks change constantly; handled by the hotpack watcher instead)
        internal static bool IsHostFolder(string path)
        {
            try { return PathEq(path, HotReloadCore.ModDir); } catch { return false; }
        }

        // loaded mods whose folders changed since the last apply (excluding this tool's own folder)
        public static List<string> ChangedLoadedMods()
        {
            var changed = new List<string>();
            try
            {
                var mods = ModManager.GetLoadedMods();
                if (mods == null) return changed;
                foreach (var m in mods)
                {
                    if (m == null || string.IsNullOrEmpty(m.Path)) continue;
                    if (string.Equals(m.Name, "TFP_Harmony", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsHostFolder(m.Path)) continue;
                    try
                    {
                        if (!Directory.Exists(m.Path)) continue; // vanished: handled elsewhere
                        var key = Norm(m.Path);
                        var now = FolderMarker(m.Path);
                        string prev;
                        if (_loadedMarkers.TryGetValue(key, out prev))
                        {
                            if (prev != now) { changed.Add(m.FolderName); _loadedMarkers[key] = now; }
                        }
                        else
                        {
                            _loadedMarkers[key] = now; // first time seen: don't treat as a change
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return changed;
        }

        public static List<string> Roots()
        {
            var roots = new List<string>();
            try
            {
                var r1 = GameIO.GetDeviceLocalUserGameDataDir();
                if (!string.IsNullOrEmpty(r1)) roots.Add(Norm(r1 + "/Mods"));
            }
            catch { }
            try
            {
                var r2 = ModManager.ModsBasePathLegacy;
                if (!string.IsNullOrEmpty(r2)) roots.Add(Norm(r2));
            }
            catch { }
            // dedupe
            var outList = new List<string>();
            foreach (var r in roots)
            {
                bool dup = false;
                foreach (var o in outList) { if (PathEq(o, r)) { dup = true; break; } }
                if (!dup) outList.Add(r);
            }
            return outList;
        }

        static string Norm(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return p; }
        }

        static bool PathEq(string a, string b)
        {
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }

        // folders under the mods roots (each subdir that has a ModInfo.xml)
        public static List<string> CandidateFolders()
        {
            var list = new List<string>();
            foreach (var root in Roots())
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    var subs = Directory.GetDirectories(root);
                    Array.Sort(subs, StringComparer.OrdinalIgnoreCase);
                    foreach (var d in subs)
                    {
                        try
                        {
                            if (File.Exists(Path.Combine(d, "ModInfo.xml"))) list.Add(Norm(d));
                        }
                        catch { }
                    }
                }
                catch { }
            }
            return list;
        }

        // seed attempted map so folders already handled at startup aren't retried
        public static void SeedAttempted()
        {
            foreach (var f in CandidateFolders())
            {
                _attempted[f] = FolderMarker(f);
            }
        }

        static string MtimeMarker(string folder)
        {
            try
            {
                var mi = Path.Combine(folder, "ModInfo.xml");
                return File.Exists(mi) ? File.GetLastWriteTimeUtc(mi).Ticks.ToString() : "0";
            }
            catch { return "0"; }
        }

        // any change anywhere in the folder changes this marker (so a fixed mod is retried)
        static string FolderMarker(string folder)
        {
            try
            {
                var st = Snapshot(folder);
                return st.Count + ":" + st.Bytes + ":" + st.NewestWriteUtc.Ticks;
            }
            catch { return MtimeMarker(folder); }
        }

        internal static bool IsSrcLiveNoise(string fullPath)
        {
            try
            {
                return fullPath.IndexOf("\\src\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || fullPath.IndexOf("\\source\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || fullPath.IndexOf("\\sources\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || fullPath.IndexOf("\\cache\\", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        static FolderState Snapshot(string folder)
        {
            var st = new FolderState();
            st.SnapshotAtUtc = DateTime.UtcNow;
            try
            {
                foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    if (IsSrcLiveNoise(f)) continue; // src\ + cache\ belong to the src-live engine
                    st.Count++;
                    try
                    {
                        var fi = new FileInfo(f);
                        st.Bytes += fi.Length;
                        if (fi.LastWriteTimeUtc > st.NewestWriteUtc) st.NewestWriteUtc = fi.LastWriteTimeUtc;
                    }
                    catch { }
                }
            }
            catch { }
            return st;
        }

        // true when every candidate folder hasn't changed since the previous check
        public static bool AllCandidatesStable()
        {
            bool allStable = true;
            foreach (var f in CandidateFolders())
            {
                if (!IsStable(f, false)) allStable = false;
            }
            return allStable;
        }

        static bool IsStable(string folder, bool updateSnapshot)
        {
            var now = DateTime.UtcNow;
            var cur = Snapshot(folder);
            bool stable;
            FolderState prev;
            if (_snap.TryGetValue(folder, out prev))
            {
                // unchanged since last look AND quiet for >=2s
                stable = (prev.Count == cur.Count && prev.Bytes == cur.Bytes && prev.NewestWriteUtc == cur.NewestWriteUtc
                          && (now - cur.NewestWriteUtc).TotalSeconds >= 2.0);
            }
            else
            {
                stable = (now - cur.NewestWriteUtc).TotalSeconds >= 2.0;
            }
            _snap[folder] = cur; // always keep the latest observation
            return (now - cur.NewestWriteUtc).TotalSeconds >= 2.0;
        }

        public static bool IsLoadedPath(string folder)
        {
            try
            {
                var mods = ModManager.GetLoadedMods();
                if (mods == null) return false;
                foreach (var m in mods)
                {
                    if (m == null) continue;
                    if (PathEq(m.Path, folder)) return true;
                }
            }
            catch { }
            return false;
        }

        // main entry. force=true retries everything (manual `hr mods`).
        public static ScanReport ScanAndLoad(bool force, string onlyFolder)
        {
            var rep = new ScanReport();
            LastScanUtc = DateTime.UtcNow;
            int candidates = 0, skippedLoaded = 0, skippedUnchanged = 0, attempted = 0, ok = 0, fail = 0;

            try { rep.vanished = FindVanished(); } catch { }

            foreach (var folder in CandidateFolders())
            {
                var folderName = Path.GetFileName(folder);
                if (onlyFolder != null && !string.Equals(folderName, onlyFolder, StringComparison.OrdinalIgnoreCase)) continue;
                candidates++;

                if (IsLoadedPath(folder)) { skippedLoaded++; continue; }

                if (!force)
                {
                    string marker;
                    if (_attempted.TryGetValue(folder, out marker) && marker == FolderMarker(folder))
                    {
                        skippedUnchanged++; continue;
                    }
                }

                if (!IsStable(folder, true)) { rep.summary = "deferred (folder still changing)"; rep.deferred = true; continue; }

                attempted++;
                try
                {
                    var mod = Mod.LoadDefinitionFromFolder(folder);
                    if (mod == null)
                    {
                        fail++;
                        rep.failed.Add(folderName + " (unreadable ModInfo)");
                        _attempted[folder] = FolderMarker(folder);
                        continue;
                    }
                    if (ModManager.ModLoaded(mod.Name))
                    {
                        skippedLoaded++;
                        _attempted[folder] = FolderMarker(folder);
                        continue;
                    }
                    bool loadOk = mod.LoadMod();
                    if (!loadOk)
                    {
                        fail++;
                        rep.failed.Add(folderName + " (" + mod.Name + " failed to load; check log)");
                        _attempted[folder] = FolderMarker(folder);
                        continue;
                    }
                    ModManager.loadedMods.Add(mod.Name, mod);
                    try { mod.InitModCode(); } catch (Exception e) { Log.Error("[HotReload] InitModCode " + mod.Name + ": " + e.Message); }

                    ok++;
                    rep.loaded.Add(mod.Name + " v" + (string.IsNullOrEmpty(mod.VersionString) ? "?" : mod.VersionString));
                    rep.newMods.Add(mod);
                    _attempted[folder] = FolderMarker(folder);
                    Log.Out("[HotReload] loaded NEW mod '" + mod.Name + "' from " + folder);
                }
                catch (Exception e)
                {
                    fail++;
                    rep.failed.Add(folderName + " (" + e.Message + ")");
                    _attempted[folder] = FolderMarker(folder);
                    Log.Error("[HotReload] load " + folder + ": " + e);
                }
            }

            rep.summary = candidates + " candidate folder(s): " + ok + " loaded, " + fail + " failed, "
                          + skippedLoaded + " already loaded, " + skippedUnchanged + " unchanged"
                          + (attempted > 0 && ok + fail == 0 ? ", deferred" : "");
            return rep;
        }

        // register console commands from the new mods' assemblies into the live console
        public static int RegisterCommands(List<Mod> newMods)
        {
            int added = 0;
            SdtdConsole sc = null;
            try { sc = SdtdConsole.Instance; } catch { }
            if (sc == null) return 0;

            foreach (var mod in newMods)
            {
                if (mod == null) continue;
                ReadOnlyCollection<Assembly> asms = null;
                try { asms = mod.AllAssemblies; } catch { }
                if (asms == null) continue;
                foreach (var asm in asms)
                {
                    if (asm == null) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                    foreach (var t in types)
                    {
                        if (t == null || t.IsAbstract || t.IsInterface) continue;
                        try
                        {
                            if (!typeof(IConsoleCommand).IsAssignableFrom(t)) continue;
                            var cmd = (IConsoleCommand)Activator.CreateInstance(t);
                            sc.m_Commands.Add(cmd);
                            foreach (var alias in cmd.GetCommands())
                            {
                                if (string.IsNullOrEmpty(alias)) continue;
                                if (!sc.m_CommandsAllVariants.ContainsKey(alias))
                                {
                                    sc.m_CommandsAllVariants.Add(alias, cmd);
                                    added++;
                                }
                            }
                            // permission bookkeeping (mirrors SdtdConsole.RegisterCommand)
                            try
                            {
                                var gm = GameManager.Instance;
                                if (gm != null && gm.adminTools != null)
                                {
                                    var aliases = cmd.GetCommands();
                                    if (!gm.adminTools.Commands.IsPermissionDefined(aliases) && cmd.DefaultPermissionLevel != 0)
                                    {
                                        gm.adminTools.Commands.AddCommand(cmd.GetCommands()[0], cmd.DefaultPermissionLevel, false);
                                    }
                                }
                            }
                            catch (Exception e) { Log.Error("[HotReload] cmd permission " + t.Name + ": " + e.Message); }
                        }
                        catch (Exception e) { Log.Error("[HotReload] cmd register " + t.Name + ": " + e.Message); }
                    }
                }
            }
            try { sc.m_CommandsReadOnly = new ReadOnlyCollection<IConsoleCommand>(sc.m_Commands); } catch { }
            return added;
        }
    }


    // -------------------------------------------------------------------
    // src-live: compile a mod folder's src\*.cs at runtime, byte-load it,
    // and hot-swap the mod's OWN code whenever those files change.
    //
    // Why it works: byte-loaded assemblies (a) don't lock their file,
    // (b) load again as fresh copies each time, and (c) their Harmony
    // patches can be removed by scanning Harmony.GetAllPatchedMethods()
    // for patch methods that live in the old assembly object (proved:
    // cross-instance Unpatch works, incl. for foreign Harmony ids).
    // -------------------------------------------------------------------
    public static class ModCode
    {
        public class SrcBuild
        {
            public Mod mod;
            public string folder;
            public string hash;
            public byte[] bytes;
            public string outDll;
            public string error;
            public bool compiled;
            public bool dllSwap;
        }

        static readonly Dictionary<string, string> _appliedHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Assembly> _liveAsm = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        // every assembly this tool loaded (byte-loaded or swapped): only these are ever swept,
        // so we never rip patches out of a mod the game loaded itself.
        static readonly HashSet<Assembly> _ours = new HashSet<Assembly>();
        // patch removals deferred because the target method was executing (retried later)
        public static int LastSweepDeferred;
        static readonly Dictionary<string, string> _lastResult = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static string _cscPath;
        static volatile bool _busy;
        static volatile bool _bgSweepQueued;

        public static string CompilerPath()
        {
            try { if (string.IsNullOrEmpty(_cscPath)) _cscPath = FindCsc(); } catch { }
            return _cscPath;
        }

        public static void Init()
        {
            // If we're running under the v4 bootstrap, adopt the assemblies it has
            // already byte-loaded (startup mod dlls) so deliberate rebuild-swaps can
            // sweep their old patches; without the bootstrap this is a no-op.
            try
            {
                var bt = Type.GetType("HotReloadTool.Bootstrap, HotReloadTool", false);
                if (bt != null)
                {
                    var m = bt.GetMethod("SnapshotTracked", BindingFlags.Public | BindingFlags.Static);
                    if (m != null)
                    {
                        var list = m.Invoke(null, null) as System.Collections.IEnumerable;
                        if (list != null)
                        {
                            int n = 0;
                            foreach (var o in list)
                            {
                                var a = o as Assembly;
                                if (a != null && _ours.Add(a)) n++;
                            }
                            if (n > 0) Log.Out("[HotReload] adopted " + n + " assembly(ies) from the bootstrap");
                        }
                    }
                }
            }
            catch { }

            _cscPath = FindCsc();
            if (_cscPath == null)
                Log.Warning("[HotReload] C# compiler (csc.exe) not found - src-live disabled. Set CSC_PATH env var or create " + ConfigFileHint() + " containing the csc.exe path.");
            else
                Log.Out("[HotReload] src-live compiler: " + _cscPath);
        }

        // ---------------------------------------------------------------
        // loader intercept: make the game byte-load mod dlls (file stays
        // unlocked + immune to App-Control file reputation blocks)
        // ---------------------------------------------------------------
        public static void InstallLoadInterceptor()
        {
            try
            {
                var h = new Harmony("hotreload.loader");
                var m = typeof(Mod).GetMethod("loadAssembly", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (m == null) { Log.Warning("[HotReload] Mod.loadAssembly not found; loader intercept skipped"); return; }
                var prefix = new HarmonyMethod(typeof(ModCode).GetMethod("LoadAssemblyPrefix", BindingFlags.Public | BindingFlags.Static));
                h.Patch(m, prefix, null, null, null, null);
                Log.Out("[HotReload] mod-dll loader intercepted: runtime-loaded mod dlls are byte-loaded (files stay unlocked)");
            }
            catch (Exception e) { Log.Error("[HotReload] loader intercept failed: " + e.Message); }
        }

        public static bool LoadAssemblyPrefix(string __0, ref Assembly __result)
        {
            try
            {
                if (!string.IsNullOrEmpty(__0) && File.Exists(__0))
                {
                    __result = Assembly.Load(File.ReadAllBytes(__0));
                    _ours.Add(__result);
                    return false; // skip the original loader
                }
            }
            catch (Exception e)
            {
                Log.Warning("[HotReload] byte-load '" + __0 + "' failed (" + e.Message + "); falling back to default loader");
            }
            return true; // run original
        }

        // ---------------------------------------------------------------
        // public entry points
        // ---------------------------------------------------------------
        public static string BuildReport(string which)
        {
            var candidates = new List<string>();
            try
            {
                var mods = ModManager.GetLoadedMods();
                if (mods != null)
                {
                    foreach (var m in mods)
                    {
                        if (m == null || string.IsNullOrEmpty(m.Path)) continue;
                        if (!Directory.Exists(m.Path)) continue;
                        if (ModScanner.IsHostFolder(m.Path)) continue;
                        if (string.Equals(m.Name, "TFP_Harmony", StringComparison.OrdinalIgnoreCase)) continue;
                        if (File.Exists(Path.Combine(m.Path, "nosrclive.txt"))) continue;
                        if (!string.IsNullOrEmpty(which)
                            && !string.Equals(Path.GetFileName(m.Path), which, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(m.Name, which, StringComparison.OrdinalIgnoreCase)) continue;
                        if (CountSources(m.Path) > 0 || ListModDlls(m.Path).Count > 0) candidates.Add(Path.GetFileName(m.Path));
                    }
                }
            }
            catch { }
            if (candidates.Count == 0)
                return "[HotReload] src-live: nothing to build" + (string.IsNullOrEmpty(which) ? "" : " for '" + which + "'")
                    + " (a mod needs a src\\ folder of .cs sources, or a rebuilt .dll).";
            List<string> only = string.IsNullOrEmpty(which) ? null : new List<string> { which };
            ProcessSrcChangesAsync(true, only);
            return "[HotReload] src-live: rebuilding " + Join2(candidates) + " (result in console in a few seconds)";
        }

        static string Join2(List<string> l)
        {
            var s = "";
            for (int i = 0; i < l.Count; i++) { if (i > 0) s += ", "; s += l[i]; }
            return s;
        }

        public static string StatusText()
        {
            var s = "  src-live compiler: " + (_cscPath != null ? _cscPath : (FindCsc() != null ? FindCsc() : "(not found - set CSC_PATH or " + ConfigFileHint() + ")")) + "\n";
            try
            {
                var mods = ModManager.GetLoadedMods();
                int shown = 0;
                if (mods != null)
                {
                    foreach (var m in mods)
                    {
                        if (m == null || string.IsNullOrEmpty(m.Path)) continue;
                        if (ModScanner.IsHostFolder(m.Path)) continue;
                        if (CountSources(m.Path) == 0) continue;
                        string res;
                        _lastResult.TryGetValue(KeyFor(m.Path), out res);
                        s += "    " + Path.GetFileName(m.Path) + ": " + (res != null ? res : "(not built yet)") + "\n";
                        shown++;
                    }
                }
                if (shown == 0) s += "    (no src-live mods loaded)\n";
            }
            catch { }
            return s;
        }

        // ---------------------------------------------------------------
        // watch-path entry: compile on a worker thread, apply on main
        // ---------------------------------------------------------------
        public static void ProcessSrcChangesAsync(bool force)
        {
            ProcessSrcChangesAsync(force, null);
        }

        public static void ProcessSrcChangesAsync(bool force, List<string> onlyFolders)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    if (HotReloadCore.Retired) { _busy = false; return; }
                    List<SrcBuild> builds = null;
                    try { builds = CompilePhase(force, onlyFolders); }
                    catch (Exception e) { Log.Error("[HotReload] srclive compile: " + e.Message); }
                    var bl = builds;
                    if (bl != null && bl.Count > 0)
                    {
                        HotReloadCore.RunOnMainThread(delegate
                        {
                            try
                            {
                                var sb = "";
                                foreach (var b in bl) { var r = ApplyBuild(b); if (r != null) sb += r + "\n"; }
                                if (sb.Length > 0) HotReloadCore.Push(sb);
                            }
                            catch (Exception e) { Log.Error("[HotReload] srclive apply: " + e.Message); }
                            finally { _busy = false; HotReloadCore.RecheckSoon(); }
                        });
                        return; // _busy cleared after apply
                    }
                    _busy = false;
                });
            }
            catch { _busy = false; }
        }

        public static string ProcessSrcChanges(bool force, List<string> onlyFolders)
        {
            if (_busy) return "";
            _busy = true;
            var sb = "";
            try
            {
                foreach (var b in CompilePhase(force, onlyFolders))
                {
                    var r = ApplyBuild(b);
                    if (r != null) sb += r + "\n";
                }
            }
            catch (Exception e) { sb += "[HotReload] src-live: " + e.Message + "\n"; }
            finally { _busy = false; }
            if (sb.Length > 0) HotReloadCore.RecheckSoon();
            return sb;
        }

        // sweep old patches from a worker thread (safe: the game's methods are not
        // on a worker stack, so Mono never re-JITs a frame we're inside of)
        static void QueueWorkerSweep(Mod mod)
        {
            if (_bgSweepQueued) return;
            _bgSweepQueued = true;
            try
            {
                ThreadPool.QueueUserWorkItem(delegate(object s)
                {
                    try
                    {
                        Thread.Sleep(400); // let the main thread leave the executing frames
                        if (HotReloadCore.Retired) return;
                        int n = SweepModAssemblies(mod);
                        if (n > 0) Log.Out("[HotReload] worker sweep: removed " + n + " patch(es) that were executing on the main thread");
                    }
                    catch (Exception e) { Log.Error("[HotReload] worker sweep: " + e.Message); }
                    finally { _bgSweepQueued = false; HotReloadCore.RecheckSoon(); }
                });
            }
            catch { _bgSweepQueued = false; }
        }

        // ---------------------------------------------------------------
        // compile phase (safe off main thread)
        // ---------------------------------------------------------------
        static List<SrcBuild> CompilePhase(bool force, List<string> onlyFolders)
        {
            var builds = new List<SrcBuild>();
            List<Mod> mods = null;
            try { mods = ModManager.GetLoadedMods(); } catch { }
            if (mods == null) return builds;
            foreach (var mod in mods)
            {
                if (mod == null || string.IsNullOrEmpty(mod.Path)) continue;
                try
                {
                    if (!Directory.Exists(mod.Path)) continue;
                    if (ModScanner.IsHostFolder(mod.Path)) continue;
                    if (string.Equals(mod.Name, "TFP_Harmony", StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(Path.Combine(mod.Path, "nosrclive.txt"))) continue;
                    var fn = Path.GetFileName(mod.Path);
                    if (onlyFolders != null && !ContainsIgnoreCase(onlyFolders, fn) && !ContainsIgnoreCase(onlyFolders, mod.Name)) continue;

                    var key = KeyFor(mod.Path);

                    // override: if <mod>\cache\<modname>.dll exists it wins over everything
                    // (a custom build script or the tool itself can drop a ready dll there;
                    // for src-live mods this is csc's own output and normally identical)
                    var overrideDll = Path.Combine(mod.Path, "cache", SafeName(mod.Name) + ".dll");
                    bool isSrcLive = CountSources(mod.Path) > 0;
                    if (File.Exists(overrideDll) && !isSrcLive)
                    {
                        var omark = "c:" + DllMarker(new List<string> { overrideDll });
                        string oprev;
                        if (_appliedHash.TryGetValue(key, out oprev))
                        {
                            if (oprev == omark) continue;
                        }
                        else
                        {
                            _appliedHash[key] = omark; // first sight: treat as current
                            continue;
                        }
                        var ob = new SrcBuild();
                        ob.mod = mod; ob.folder = mod.Path; ob.dllSwap = true; ob.compiled = true;
                        ob.hash = omark; ob.outDll = overrideDll;
                        builds.Add(ob);
                        continue;
                    }

                    if (isSrcLive)
                    {
                        // src\ is an explicit opt-in to source-live hot-swap
                        var hash = "s:" + SrcMarker(mod.Path);
                        string prev;
                        if (!force && !_appliedHash.ContainsKey(key) && ListModDlls(mod.Path).Count > 0)
                        {
                            _appliedHash[key] = hash; // Native startup already initialized the shipped DLL.
                            continue;
                        }
                        if (!force && _appliedHash.TryGetValue(key, out prev) && prev == hash) continue;
                        var b = CompileMod(mod, mod.Path);
                        if (b != null) builds.Add(b);
                        continue;
                    }

                    var dlls = ListModDlls(mod.Path);
                    if (dlls.Count > 0)
                    {
                        // mod ships its own compiled dll: watch it, swap it when it changes
                        var dmark = "d:" + DllMarker(dlls);
                        string dprev;
                        if (_appliedHash.TryGetValue(key, out dprev))
                        {
                            if (dprev == dmark) continue; // unchanged: skip (even when forced: never re-init a live mod)
                        }
                        else
                        {
                            _appliedHash[key] = dmark; // first sight: the game just loaded it natively
                            continue;
                        }
                        var bd = new SrcBuild();
                        bd.mod = mod; bd.folder = mod.Path; bd.dllSwap = true; bd.compiled = true; bd.hash = dmark;
                        builds.Add(bd);
                    }
                }
                catch (Exception e) { Log.Error("[HotReload] srclive " + mod.Name + ": " + e.Message); }
            }
            return builds;
        }

        static SrcBuild CompileMod(Mod mod, string folder)
        {
            var b = new SrcBuild();
            b.mod = mod;
            b.folder = folder;
            try
            {
                b.hash = "s:" + SrcMarker(folder); // so a failed build is not retried until sources change
                var srcs = ListSources(folder);
                if (srcs.Count == 0) return null;

                if (string.IsNullOrEmpty(_cscPath)) _cscPath = FindCsc();
                if (string.IsNullOrEmpty(_cscPath) || !File.Exists(_cscPath))
                {
                    b.error = "no C# compiler found (set CSC_PATH env var, or create " + ConfigFileHint() + " containing the path to csc.exe)";
                    return b;
                }

                var cache = Path.Combine(folder, "cache");
                Directory.CreateDirectory(cache);
                var name = SafeName(mod != null ? mod.Name : Path.GetFileName(folder));
                var outDll = Path.Combine(cache, name + ".dll");

                var refs = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var managed = ManagedDir();
                if (!string.IsNullOrEmpty(managed))
                    foreach (var f in Directory.GetFiles(managed, "*.dll")) { if (seen.Add(Path.GetFileName(f))) refs.Add(f); }
                var harm = HarmonyDllPath();
                if (!string.IsNullOrEmpty(harm) && seen.Add(Path.GetFileName(harm))) refs.Add(harm);
                foreach (var f in Directory.GetFiles(folder, "*.dll")) { if (seen.Add(Path.GetFileName(f))) refs.Add(f); }
                var libdir = Path.Combine(folder, "lib");
                if (Directory.Exists(libdir))
                    foreach (var f in Directory.GetFiles(libdir, "*.dll")) { if (seen.Add(Path.GetFileName(f))) refs.Add(f); }

                var compiled = ModCompiler.Compile(_cscPath, srcs, refs, outDll, freshIdentity: true);
                if (!compiled.Success)
                {
                    b.error = "compile failed (previous DLL kept):\n" + compiled.Diagnostics;
                    return b;
                }
                b.bytes = compiled.Bytes;
                b.outDll = outDll;
                b.compiled = true;
                // Keep the input marker captured BEFORE compiling. A save during
                // this build must remain a new change for the completion pass.
                return b;
            }
            catch (Exception e)
            {
                b.error = e.Message;
                return b;
            }
        }

        // ---------------------------------------------------------------
        // apply phase (must run on the main thread)
        // ---------------------------------------------------------------
        static string ApplyBuild(SrcBuild b)
        {
            if (b == null) return null;
            if (b.dllSwap) return ApplyDllSwap(b);
            if (!b.compiled || b.bytes == null)
            {
                _appliedHash[KeyFor(b.folder)] = b.hash != null ? b.hash : "err";
                _lastResult[KeyFor(b.folder)] = "build failed: " + b.error;
                var m = "[HotReload] src-live '" + FolderName(b.folder) + "': " + b.error;
                Log.Error(m);
                return m;
            }
            try
            {
                int swept = SweepModAssemblies(b.mod);
                if (LastSweepDeferred > 0)
                {
                    // some old patch targets are executing on THIS thread: unpatching them here
                    // freezes Mono. Sweep them from a worker thread instead, retry next pass.
                    QueueWorkerSweep(b.mod);
                    _lastResult[KeyFor(b.folder)] = "deferred: " + LastSweepDeferred + " patch(es) executing here; worker sweep queued, retrying";
                    var dm = "[HotReload] src-live '" + FolderName(b.folder) + "': deferred (" + LastSweepDeferred
                        + " old patch(es) executing on this thread, freezing risk) - sweeping on a worker thread, retry in a few seconds";
                    Log.Warning(dm);
                    return dm;
                }
                var asm = Assembly.Load(b.bytes);
                _ours.Add(asm);
                _liveAsm[KeyFor(b.folder)] = asm;
                try { if (b.mod != null && !b.mod.allAssemblies.Contains(asm)) b.mod.allAssemblies.Add(asm); } catch { }
                string applied = ApplyAssembly(b.mod, asm);
                // Persist a successful source rebuild for native startup and
                // host seeding; friends do not need a C# compiler installed.
                if (applied.EndsWith(", 0 failed", StringComparison.Ordinal))
                {
                    var nativeDlls = ListModDlls(b.folder);
                    var nativePath = nativeDlls.Count == 1 ? nativeDlls[0] : Path.Combine(b.folder, SafeName(b.mod.Name) + ".dll");
                    if (nativeDlls.Count > 1 && !File.Exists(nativePath)) throw new InvalidOperationException("Cannot identify the primary DLL in this multi-assembly source mod");
                    ModCompiler.Publish(b.bytes, nativePath);
                    ModScanner.RecordApplied(b.folder);
                }
                _appliedHash[KeyFor(b.folder)] = b.hash;
                int def = LastSweepDeferred;
                _lastResult[KeyFor(b.folder)] = "ok (" + swept + " old patch(es) swept, " + applied + (def > 0 ? ", " + def + " deferred (executing)" : "") + ")";
                var msg = "[HotReload] src-live '" + FolderName(b.folder) + "': rebuilt + hot-swapped (" + swept + " swept, " + applied
                    + (def > 0 ? ", " + def + " deferred: target executing, will retry" : "") + ")";
                Log.Out(msg);
                return msg;
            }
            catch (Exception e)
            {
                Log.Error("[HotReload] src-live apply: " + e);
                _lastResult[KeyFor(b.folder)] = "apply failed: " + e.Message;
                return "[HotReload] src-live apply failed for '" + FolderName(b.folder) + "': " + e.Message;
            }
        }

        static string ApplyDllSwap(SrcBuild b)
        {
            try
            {
                // 1) load every dll of the mod as a fresh copy FIRST (nothing touched yet)
                var loaded = new List<Assembly>();
                int fails = 0;
                var dlls = ListModDlls(b.folder);
                // a cache override replaces the mod's code wholesale: it's the only dll
                if (!string.IsNullOrEmpty(b.outDll) && File.Exists(b.outDll)) dlls = new List<string> { b.outDll };
                foreach (var dll in dlls)
                {
                    try
                    {
                        var asm = Assembly.Load(File.ReadAllBytes(dll));
                        _ours.Add(asm);
                        loaded.Add(asm);
                    }
                    catch (Exception e) { fails++; Log.Error("[HotReload] dll swap load " + Path.GetFileName(dll) + ": " + e.Message); }
                }
                if (loaded.Count == 0)
                {
                    _lastResult[KeyFor(b.folder)] = "dll swap failed: no dll could be loaded";
                    return "[HotReload] dll swap for '" + FolderName(b.folder) + "' FAILED: no dll could be loaded (" + fails + " failed). Old code untouched.";
                }

                // 2) refuse an empty/broken build: better to keep the previous code live
                int candidates = 0;
                foreach (var asm in loaded) candidates += CountWirable(asm);
                if (candidates == 0)
                {
                    RestoreModAssemblies(b.mod, _liveAsm.TryGetValue(KeyFor(b.folder), out var keepAsm) ? keepAsm : null);
                    _lastResult[KeyFor(b.folder)] = "dll swap refused: new build has nothing to wire (old code kept)";
                    return "[HotReload] dll swap for '" + FolderName(b.folder) + "' refused: new build has no Harmony/mod entry points. Old code re-applied, nothing replaced.";
                }

                // 3) sweep the old incarnation(s) now that we know the new one is valid
                var oldAsms = new List<Assembly>();
                try
                {
                    if (b.mod != null && b.mod.AllAssemblies != null)
                        foreach (var a in b.mod.AllAssemblies) if (IsSweepable(a)) oldAsms.Add(a);
                }
                catch { }
                int swept = SweepModAssemblies(b.mod);
                if (LastSweepDeferred > 0)
                {
                    QueueWorkerSweep(b.mod);
                    _lastResult[KeyFor(b.folder)] = "deferred: " + LastSweepDeferred + " patch(es) executing here; worker sweep queued";
                    return "[HotReload] dll swap for '" + FolderName(b.folder) + "' deferred (" + LastSweepDeferred + " executing patch(es)) - retry in a few seconds";
                }

                // 4) wire the new copy
                int wired = 0;
                foreach (var asm in loaded)
                {
                    try
                    {
                        ApplyAssembly(b.mod, asm);
                        try { if (b.mod != null && !b.mod.allAssemblies.Contains(asm)) b.mod.allAssemblies.Add(asm); } catch { }
                        wired++;
                    }
                    catch (Exception e) { fails++; Log.Error("[HotReload] dll swap wire " + asm.GetName().Name + ": " + e.Message); }
                }

                // 5) console commands: swap the old instances out for the new ones
                int cmdSwap = 0;
                try { cmdSwap = RefreshConsoleCommands(oldAsms, loaded); } catch (Exception e) { Log.Error("[HotReload] command swap: " + e.Message); }

                // 6) remember what we just wired so a later bad build can roll back to it
                if (!string.IsNullOrEmpty(b.folder)) _liveAsm[KeyFor(b.folder)] = loaded[0];

                _appliedHash[KeyFor(b.folder)] = b.hash;
                _lastResult[KeyFor(b.folder)] = "ok (dll swap: " + swept + " swept, " + wired + " reloaded, " + fails + " failed" + (cmdSwap > 0 ? ", " + cmdSwap + " cmd(s) swapped" : "") + ")";
                var msg = "[HotReload] dll hot-swapped '" + FolderName(b.folder) + "' (" + swept + " swept, " + wired + " reloaded, " + fails + " failed" + (cmdSwap > 0 ? ", " + cmdSwap + " cmd(s) swapped" : "") + ")";
                Log.Out(msg);
                return msg;
            }
            catch (Exception e)
            {
                _lastResult[KeyFor(b.folder)] = "dll swap failed: " + e.Message;
                return "[HotReload] dll swap failed for '" + FolderName(b.folder) + "': " + e.Message;
            }
        }

        // replace console commands that belonged to the old assemblies with the new ones
        static int RefreshConsoleCommands(List<Assembly> oldAsms, List<Assembly> newAsms)
        {
            SdtdConsole sc = null;
            try { sc = SdtdConsole.Instance; } catch { }
            if (sc == null || sc.m_Commands == null || sc.m_CommandsAllVariants == null) return 0;
            int swapped = 0;

            // drop stale instances (owned by the old assemblies)
            var stale = new List<IConsoleCommand>();
            foreach (var c in sc.m_Commands)
            {
                if (c == null) continue;
                try { if (oldAsms.Contains(c.GetType().Assembly)) stale.Add(c); } catch { }
            }
            foreach (var c in stale)
            {
                try
                {
                    sc.m_Commands.Remove(c);
                    var kill = new List<string>();
                    foreach (var kv in sc.m_CommandsAllVariants) if (kv.Value == c) kill.Add(kv.Key);
                    foreach (var k in kill) sc.m_CommandsAllVariants.Remove(k);
                }
                catch { }
            }

            // add the new instances
            foreach (var asm in newAsms)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                foreach (var t in types)
                {
                    if (t == null || t.IsInterface) continue;
                    try
                    {
                        if (!typeof(IConsoleCommand).IsAssignableFrom(t)) continue;
                        var cmd = (IConsoleCommand)Activator.CreateInstance(t);
                        sc.m_Commands.Add(cmd);
                        foreach (var alias in cmd.GetCommands())
                        {
                            if (string.IsNullOrEmpty(alias)) continue;
                            if (!sc.m_CommandsAllVariants.ContainsKey(alias)) sc.m_CommandsAllVariants.Add(alias, cmd);
                            else sc.m_CommandsAllVariants[alias] = cmd;
                            swapped++;
                        }
                    }
                    catch (Exception e) { Log.Error("[HotReload] cmd swap " + t.Name + ": " + e.Message); }
                }
            }
            try { sc.m_CommandsReadOnly = new ReadOnlyCollection<IConsoleCommand>(sc.m_Commands); } catch { }
            return swapped;
        }

        static int CountWirable(Assembly asm)
        {
            try
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                int n = 0;
                foreach (var t in types)
                {
                    if (t == null) continue;
                    try
                    {
                        if (typeof(IModApi).IsAssignableFrom(t)) { n++; continue; }
                        if (typeof(IConsoleCommand).IsAssignableFrom(t)) { n++; continue; }
                        if (HotReloadCore.HasPatchAnnotations(t)) n++;
                    }
                    catch { }
                }
                return n;
            }
            catch { return 0; }
        }

        // re-apply the previous incarnation's patches (rollback path)
        static void RestoreModAssemblies(Mod mod, Assembly asm)
        {
            if (asm == null) return;
            try { ApplyAssembly(mod, asm); } catch (Exception e) { Log.Error("[HotReload] rollback wire: " + e.Message); }
        }

        // hotpack-style assemblies (no IModApi) get PatchAll'd by us;
        // full mods (with IModApi) run their own InitMod wiring.
        static string ApplyAssembly(Mod mod, Assembly asm)
        {
            int classes = 0, fails = 0, inits = 0;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }

            bool hasApi = false;
            foreach (var t in types)
            {
                if (t == null || t.IsAbstract || t.IsInterface) continue;
                try { if (typeof(IModApi).IsAssignableFrom(t)) { hasApi = true; break; } } catch { }
            }

            if (!hasApi)
            {
                var h = new Harmony("srclive." + (mod != null ? mod.Name : asm.GetName().Name));
                foreach (var t in types)
                {
                    // NOTE: do NOT skip IsAbstract here - static classes are abstract+sealed
                    // in IL, and [HarmonyPatch] classes are almost always static.
                    if (t == null || t.IsInterface) continue;
                    if (!HotReloadCore.HasPatchAnnotations(t)) continue;
                    classes++;
                    try { h.PatchAll(t); }
                    catch (Exception e) { fails++; Log.Error("[HotReload] srclive patch " + t.Name + ": " + e.Message); }
                }
            }
            else
            {
                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract || t.IsInterface) continue;
                    try
                    {
                        if (!typeof(IModApi).IsAssignableFrom(t)) continue;
                        var inst = (IModApi)Activator.CreateInstance(t);
                        inst.InitMod(mod);
                        inits++;
                    }
                    catch (Exception e) { fails++; Log.Error("[HotReload] srclive InitMod " + t.Name + ": " + e); }
                }
            }
            return (classes + inits) + " type(s) wired, " + fails + " failed";
        }

        // ---------------------------------------------------------------
        // sweep: remove all Harmony patches whose patch method lives in the
        // given assembly object (works across Harmony instances/ids)
        // ---------------------------------------------------------------
        static readonly PropertyInfo _patchMethodProp =
            typeof(HarmonyLib.Patch).GetProperty("PatchMethod", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        // snapshot of assemblies this tool byte-loaded (diagnostics + future use)
        public static List<Assembly> OursSnapshot()
        {
            var l = new List<Assembly>();
            try { foreach (var a in _ours) if (a != null) l.Add(a); } catch { }
            return l;
        }

        public static void Track(Assembly a) { try { if (a != null) _ours.Add(a); } catch { } }

        // sweep several assemblies in one pass, returning total patches removed
        public static int SweepAssemblies(List<Assembly> asms)
        {
            int n = 0;
            if (asms == null) return 0;
            foreach (var a in asms)
            {
                try { if (a != null) n += SweepAssembly(a); } catch { }
            }
            return n;
        }

        public static int SweepModAssemblies(Mod mod)
        {
            int removed = 0;
            LastSweepDeferred = 0;
            try
            {
                var targets = new HashSet<Assembly>();
                if (mod != null)
                {
                    try
                    {
                        // ALL of the mod's assemblies qualify (incl. ones the game loaded at
                        // startup): a deliberate rebuild of a mod's dll must replace its old
                        // code, whether this tool loaded it or not. Only OUR OWN code and the
                        // harmony library are never swept.
                        var list = mod.AllAssemblies;
                        if (list != null)
                            foreach (var a in list)
                                if (IsSweepable(a)) targets.Add(a);
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(mod.Path))
                    {
                        Assembly live;
                        if (_liveAsm.TryGetValue(KeyFor(mod.Path), out live) && IsSweepable(live)) targets.Add(live);
                    }
                }
                foreach (var a in targets)
                {
                    if (a == null) continue;
                    if (a == typeof(HotReloadCore).Assembly) continue; // never sweep ourselves
                    removed += SweepAssembly(a);
                }
            }
            catch (Exception e) { Log.Error("[HotReload] sweep mod: " + e.Message); }
            return removed;
        }

        static bool IsSweepable(Assembly a)
        {
            if (a == null) return false;
            try
            {
                if (a == typeof(HotReloadCore).Assembly) return false;
                var n = a.GetName().Name;
                if (string.Equals(n, "HotReloadTool", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(n, "HotReloadCore", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(n, "0Harmony", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(n, "TFP_Harmony", StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(n, "TfpHarmony", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch { return false; }
        }

        public static int SweepAssembly(Assembly asm)
        {
            int removed = 0;
            try
            {
                if (_patchMethodProp == null || asm == null) return 0;
                var scratch = new Harmony("srclive.sweep");
                foreach (var orig in Harmony.GetAllPatchedMethods())
                {
                    if (orig == null) continue;
                    HarmonyLib.Patches pin;
                    try { pin = Harmony.GetPatchInfo(orig); } catch { continue; }
                    if (pin == null) continue;
                    var lists = new List<ReadOnlyCollection<HarmonyLib.Patch>>();
                    if (pin.Prefixes != null) lists.Add(pin.Prefixes);
                    if (pin.Postfixes != null) lists.Add(pin.Postfixes);
                    if (pin.Transpilers != null) lists.Add(pin.Transpilers);
                    if (pin.Finalizers != null) lists.Add(pin.Finalizers);
                    if (pin.ILManipulators != null) lists.Add(pin.ILManipulators);
                    foreach (var list in lists)
                    {
                        foreach (var p in list)
                        {
                            if (p == null) continue;
                            MethodInfo pm = null;
                            try { pm = (MethodInfo)_patchMethodProp.GetValue(p, null); } catch { }
                            if (pm == null || pm.DeclaringType == null) continue;
                            if (pm.DeclaringType.Assembly != asm) continue;
                            if (HotReloadCore.IsOnCurrentStack(orig))
                            {
                                // NEVER unpatch a method we're executing right now: Mono re-JITs
                                // the on-stack method -> hard freeze. Defer to a later pass.
                                LastSweepDeferred++;
                                continue;
                            }
                            try { scratch.Unpatch(orig, pm); removed++; }
                            catch (Exception e) { Log.Error("[HotReload] sweep unpatch: " + e.Message); }
                        }
                    }
                }
            }
            catch (Exception e) { Log.Error("[HotReload] sweep: " + e.Message); }
            return removed;
        }

        // ---------------------------------------------------------------
        // helpers
        // ---------------------------------------------------------------
        static string ConfigFileHint()
        {
            try { return Path.Combine(HotReloadCore.ModDir, "csc.txt"); } catch { return "csc.txt"; }
        }

        static string FindCsc()
        {
            try
            {
                var env = Environment.GetEnvironmentVariable("CSC_PATH");
                if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            }
            catch { }
            try
            {
                var cfg = ConfigFileHint();
                if (File.Exists(cfg))
                {
                    var p = File.ReadAllLines(cfg)[0].Trim();
                    if (p.Length > 0 && File.Exists(p)) return p;
                }
            }
            catch { }
            var roots = new List<string>();
            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)); } catch { }
            try { roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)); } catch { }
            var years = new[] { "2022", "2019" };
            var eds = new[] { "BuildTools", "Enterprise", "Professional", "Community", "Preview" };
            var rels = new[] { "MSBuild/Current/Bin/Roslyn/csc.exe", "MSBuild/15.0/Bin/Roslyn/csc.exe" };
            foreach (var r in roots)
            {
                if (string.IsNullOrEmpty(r)) continue;
                foreach (var y in years)
                    foreach (var ed in eds)
                        foreach (var rel in rels)
                        {
                            try
                            {
                                var p = Path.Combine(r, "Microsoft Visual Studio", y, ed, rel.Replace('/', Path.DirectorySeparatorChar));
                                if (File.Exists(p)) return p;
                            }
                            catch { }
                        }
            }
            return null;
        }

        static string ManagedDir()
        {
            try
            {
                var dp = UnityEngine.Application.dataPath; // ...\7DaysToDie_Data
                if (!string.IsNullOrEmpty(dp))
                {
                    var m = Path.Combine(dp, "Managed");
                    if (Directory.Exists(m)) return m;
                }
            }
            catch { }
            return null;
        }

        static string HarmonyDllPath()
        {
            try
            {
                var loc = typeof(Harmony).Assembly.Location;
                if (!string.IsNullOrEmpty(loc) && File.Exists(loc)) return loc;
            }
            catch { }
            try
            {
                var dp = UnityEngine.Application.dataPath;
                var root = Path.GetDirectoryName(dp);
                if (!string.IsNullOrEmpty(root))
                {
                    var p = Path.Combine(root, "Mods", "0_TFP_Harmony", "0Harmony.dll");
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        static List<string> ListModDlls(string folder)
        {
            var l = new List<string>();
            try
            {
                foreach (var f in Directory.GetFiles(folder, "*.dll")) l.Add(f);
                l.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return l;
        }

        static string DllMarker(List<string> dlls)
        {
            try
            {
                var sb = "";
                foreach (var f in dlls)
                {
                    var fi = new FileInfo(f);
                    sb += Path.GetFileName(f) + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + ";";
                }
                return HashOf(sb);
            }
            catch { return "err"; }
        }

        static List<string> SrcDirs(string folder)
        {
            var l = new List<string>();
            try
            {
                foreach (var d in Directory.GetDirectories(folder))
                {
                    var n = Path.GetFileName(d);
                    if (string.Equals(n, "src", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n, "source", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n, "sources", StringComparison.OrdinalIgnoreCase))
                        l.Add(d);
                }
            }
            catch { }
            return l;
        }

        static List<string> ListSources(string folder)
        {
            var l = new List<string>();
            try
            {
                foreach (var d in SrcDirs(folder))
                    foreach (var f in Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
                        l.Add(f);
            }
            catch { }
            return l;
        }

        static int CountSources(string folder)
        {
            int n = 0;
            try { foreach (var d in SrcDirs(folder)) n += Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories).Length; }
            catch { }
            return n;
        }

        static string SrcMarker(string folder)
        {
            try
            {
                var sb = "";
                foreach (var d in SrcDirs(folder))
                {
                    var files = Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (var f in files)
                    {
                        var fi = new FileInfo(f);
                        sb += f.Substring(folder.Length) + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + ";";
                    }
                }
                sb += "lib:";
                var libdir = Path.Combine(folder, "lib");
                if (Directory.Exists(libdir))
                    foreach (var f in Directory.GetFiles(libdir, "*.dll"))
                    {
                        var fi = new FileInfo(f);
                        sb += Path.GetFileName(f) + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + ";";
                    }
                var rootDlls = ListModDlls(folder);
                var primaryDll = rootDlls.Count == 1 ? rootDlls[0] : Path.Combine(folder, SafeName(ModFiles.ModName(folder)) + ".dll");
                foreach (var f in rootDlls)
                {
                    // This is the result of compiling these sources, not an
                    // input change. Publishing it must not schedule another build.
                    if (string.Equals(f, primaryDll, StringComparison.OrdinalIgnoreCase)) continue;
                    var fi = new FileInfo(f);
                    sb += Path.GetFileName(f) + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + ";";
                }
                sb += "csc:" + (_cscPath != null ? _cscPath : "");
                return HashOf(sb);
            }
            catch { return "err"; }
        }

        static string HashOf(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return h.ToString("x8");
            }
        }

        static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "mod";
            var o = "";
            foreach (var ch in s)
                o += (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.') ? ch : '_';
            return o;
        }

        static string KeyFor(string folder)
        {
            try { return Path.GetFullPath(folder).TrimEnd('\\', '/'); } catch { return folder; }
        }

        static string FolderName(string folder)
        {
            try { return Path.GetFileName(folder.TrimEnd('\\', '/')); } catch { return folder; }
        }

        static bool ContainsIgnoreCase(List<string> l, string v)
        {
            if (l == null || v == null) return false;
            foreach (var s in l) if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }


    }

    // -------------------------------------------------------------------
    // doctor: engine-update self-check
    //
    // Every API this tool calls is verified here by reflection. After a game
    // update, run 'hr doctor': anything that changed shows up as FAIL instead
    // of crashing mid-reload. src-live mods are recompiled against the NEW
    // Managed assemblies, so their code follows the engine version too.
    // -------------------------------------------------------------------
    public static class Doctor
    {
        public static string LastSummary = "(not run)";
        static string _details = "";

        public static string Run(bool verbose)
        {
            var sb = new System.Text.StringBuilder();
            int pass = 0, fail = 0;

            sb.Append("[HotReload] doctor: checking engine API compatibility...\n");

            // -- reload APIs --
            Chk(sb, ref pass, ref fail, "WorldStaticData.ReloadAllXmlsSync()", () =>
                typeof(WorldStaticData).GetMethod("ReloadAllXmlsSync", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null) != null);
            Chk(sb, ref pass, ref fail, "Localization.ReloadBaseLocalization()", () =>
                typeof(Localization).GetMethod("ReloadBaseLocalization", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null) != null);
            Chk(sb, ref pass, ref fail, "ModManager.LoadLocalizations(bool)", () =>
                typeof(ModManager).GetMethod("LoadLocalizations", BindingFlags.Public | BindingFlags.Static) != null);
            Chk(sb, ref pass, ref fail, "ModManager.LoadPatchStuff(bool)", () =>
                typeof(ModManager).GetMethod("LoadPatchStuff", BindingFlags.Public | BindingFlags.Static) != null);

            // -- runtime mod loading --
            Chk(sb, ref pass, ref fail, "Mod.LoadDefinitionFromFolder(string)", () =>
                typeof(Mod).GetMethod("LoadDefinitionFromFolder", BindingFlags.Public | BindingFlags.Static) != null);
            Chk(sb, ref pass, ref fail, "Mod.LoadMod()", () => typeof(Mod).GetMethod("LoadMod", BindingFlags.Public | BindingFlags.Instance) != null);
            Chk(sb, ref pass, ref fail, "Mod.InitModCode()", () => typeof(Mod).GetMethod("InitModCode", BindingFlags.Public | BindingFlags.Instance) != null);
            Chk(sb, ref pass, ref fail, "Mod.loadAssembly(string) [loader intercept]", () =>
                typeof(Mod).GetMethod("loadAssembly", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null) != null);
            Chk(sb, ref pass, ref fail, "ModManager.loadedMods (DictionaryList)", () => ModManager.loadedMods != null);

            // -- console internals (command registration) --
            Chk(sb, ref pass, ref fail, "SdtdConsole.m_Commands / m_CommandsAllVariants", () =>
                typeof(SdtdConsole).GetField("m_Commands", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null
                && typeof(SdtdConsole).GetField("m_CommandsAllVariants", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null);

            // -- marshalling --
            Chk(sb, ref pass, ref fail, "ThreadManager.AddSingleTaskMainThread(string, Action)", () =>
                typeof(ThreadManager).GetMethod("AddSingleTaskMainThread", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Action) }, null) != null);
            Chk(sb, ref pass, ref fail, "ThreadManager.IsMainThread()", () =>
                typeof(ThreadManager).GetMethod("IsMainThread", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null) != null);

            // -- harmony surface used for hot-swap --
            Chk(sb, ref pass, ref fail, "Harmony.GetAllPatchedMethods()", () =>
                typeof(Harmony).GetMethod("GetAllPatchedMethods", BindingFlags.Public | BindingFlags.Static) != null);
            Chk(sb, ref pass, ref fail, "Harmony.GetPatchInfo(MethodBase)", () =>
                typeof(Harmony).GetMethod("GetPatchInfo", BindingFlags.Public | BindingFlags.Static) != null);
            Chk(sb, ref pass, ref fail, "Patch.PatchMethod (private prop, sweep)", () =>
                typeof(HarmonyLib.Patch).GetProperty("PatchMethod", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null);

            // -- patch targets used by the bundled demo (optional, warn only) --
            Chk(sb, ref pass, ref fail, "target: EntityPlayerLocal.OnUpdateEntity", () =>
                typeof(EntityPlayerLocal).GetMethod("OnUpdateEntity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null);
            Chk(sb, ref pass, ref fail, "target: GameManager.Update", () =>
                typeof(GameManager).GetMethod("Update", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null);

            // -- src-live prerequisites --
            Chk(sb, ref pass, ref fail, "C# compiler found (csc.exe)", () => ModCode.CompilerPath() != null);
            Chk(sb, ref pass, ref fail, "Managed dir found", () =>
            {
                try { var dp = UnityEngine.Application.dataPath; return !string.IsNullOrEmpty(dp) && Directory.Exists(Path.Combine(dp, "Managed")); }
                catch { return false; }
            });

            // -- environment notes --
            try
            {
                sb.Append("  notes:\n");
                try { sb.Append("    game version: " + Constants.cVersionMajor + "." + Constants.cVersionMinor + "." + Constants.cVersionBuild + "\n"); }
                catch { sb.Append("    game version: (unavailable)\n"); }
                sb.Append("    compiler: " + (ModCode.CompilerPath() ?? "(none)") + "\n");
                sb.Append("    mods roots: " + HotReloadCore.Join(ModScanner.Roots()) + "\n");
                sb.Append("    loaded mods: " + (ModManager.GetLoadedMods() != null ? ModManager.GetLoadedMods().Count : 0) + "\n");
            }
            catch (Exception e) { sb.Append("    notes failed: " + e.Message + "\n"); }

            LastSummary = pass + " pass, " + fail + " fail" + (fail > 0 ? ("  <- " + _details) : "");
            sb.Append("[HotReload] doctor: " + pass + " pass, " + fail + " fail");
            if (fail > 0) sb.Append("  <- API changed since this build; check for a tool update (or tell Hermes)");
            sb.Append("\n");
            var text = sb.ToString();
            Log.Out(text);
            return text;
        }

        static void Chk(System.Text.StringBuilder sb, ref int pass, ref int fail, string label, Func<bool> test)
        {
            bool ok;
            try { ok = test(); }
            catch { ok = false; }
            if (ok) { pass++; if (_details.Length < 400) sb.Append("  ok   " + label + "\n"); }
            else { fail++; sb.Append("  FAIL " + label + "\n"); _details = (label + " " + _details); }
        }
    }
}

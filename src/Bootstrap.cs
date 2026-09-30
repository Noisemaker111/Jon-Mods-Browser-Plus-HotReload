// Bootstrap.cs - the game-facing mod ENTRY POINT for HotReloadTool v4.
//
// The game loads THIS assembly (as HotReloadTool.dll at startup) and calls
// InitMod. Everything real lives in core\HotReloadCore.dll, which this
// bootstrap byte-loads through a Harmony interceptor on Mod.loadAssembly.
//
// The core can be rebuilt and swapped during play. The bootstrap stays loaded
// because it owns negotiated network package types; bootstrap updates require
// a full game restart.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace HotReloadTool
{
    /// <summary>Game-facing entry point. Delegates to the hot-swappable core.</summary>
    public class HotReloadApi : IModApi
    {
        public void InitMod(Mod _modInstance)
        {
            try { Bootstrap.Start(_modInstance); }
            catch (Exception e) { Log.Error("[HotReload] bootstrap start failed: " + e); }
        }
    }

    /// <summary>Always-loaded half: owns the core file path, the core loader and the 'hr' command.</summary>
    public static class Bootstrap
    {
        public const string BootstrapVersion = "6.3.0-beta.1";
        const string CORE_FILE = "HotReloadCore.dll";

        // every dll we byte-loaded (mods + core): the core adopts this so deliberate
        // rebuild-swaps can sweep patches from assemblies WE loaded.
        static readonly List<Assembly> _tracked = new List<Assembly>();
        static readonly object _trackedLock = new object();

        // folders of byte-loaded mod dlls: used to resolve THEIR dependency dlls the
        // way Assembly.LoadFrom would have (byte-loading alone doesn't probe folders).
        static readonly List<string> _modDirs = new List<string>();
        static readonly object _dirLock = new object();

        public static List<Assembly> SnapshotTracked()
        {
            lock (_trackedLock) { return new List<Assembly>(_tracked); }
        }

        public static List<string> SnapshotModDirs()
        {
            lock (_dirLock) { return new List<string>(_modDirs); }
        }

        static void Track(Assembly a)
        {
            try { if (a != null) lock (_trackedLock) { if (!_tracked.Contains(a)) _tracked.Add(a); } } catch { }
        }

        public static Mod HostMod;
        public static string ModDir;
        static string _coreDir;
        static object _core;                 // loaded CoreEntry type's Assembly
        static Type _coreEntry;
        static MethodInfo _mInit, _mShutdown, _mExecute;
        static string _coreFileVersion = "(none)";
        static volatile bool _swapping;
        static Harmony _loaderHarmony;
        static readonly object _loadLock = new object();

        public static string CoreStatus
        {
            get
            {
                try
                {
                    if (_core == null || _coreEntry == null) return "(not loaded)";
                    return _coreFileVersion + " from " + (_core is Assembly a ? a.GetName().Name : "?");
                }
                catch { return "?"; }
            }
        }

        // -------------------------------------------------------------------
        // start
        // -------------------------------------------------------------------
        public static void Start(Mod mod) { StartInternal(mod, null); }

        // called by an OLDER bootstrap's self-watcher when HotReloadTool.dll is rebuilt
        public static void StartHotSwap(Mod mod, Type oldBootstrap) { StartInternal(mod, oldBootstrap); }

        static void StartInternal(Mod mod, Type oldBootstrap)
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

            Log.Out("[HotReload] bootstrap v" + BootstrapVersion + (oldBootstrap == null ? " at " : " taking over at ") + ModDir);

            if (oldBootstrap == null)
            {
                if (!Harmony.HasAnyPatches("hotreload.bootstrap.loader")) InstallInterceptors();
                else Log.Out("[HotReload] bootstrap: loader already intercepted; reusing existing hooks");
                SeedTrackedFromLoadedMods();
                InstallResolveProbe();          // let byte-loaded mods find their own dependency dlls
                LoadCore(logMissing: true);     // first load of the core
            }
            else
            {
                // hot-swap path: adopt the running core, take over the command, retire the old bootstrap
                try
                {
                    var m = oldBootstrap.GetMethod("GetCoreHandles", BindingFlags.Public | BindingFlags.Static);
                    var h = (object[])m.Invoke(null, null);
                    _core = (Assembly)h[0]; _coreEntry = (Type)h[1];
                    _mInit = (MethodInfo)h[2]; _mShutdown = (MethodInfo)h[3]; _mExecute = (MethodInfo)h[4];
                    _coreFileVersion = (string)h[5];
                    Log.Out("[HotReload] bootstrap: adopted running core (" + _coreFileVersion + ")");
                }
                catch (Exception e) { Log.Error("[HotReload] bootstrap adopt core: " + e.Message); }

                // adopt the previous bootstrap's tracked assemblies + mod folders so the
                // core's sweep logic and dependency resolution keep working seamlessly
                try
                {
                    var snap = oldBootstrap.GetMethod("SnapshotTracked", BindingFlags.Public | BindingFlags.Static);
                    var list = snap.Invoke(null, null) as System.Collections.IEnumerable;
                    if (list != null) foreach (var o in list) { var a = o as Assembly; if (a != null) Track(a); }
                    var getDirs = oldBootstrap.GetMethod("SnapshotModDirs", BindingFlags.Public | BindingFlags.Static);
                    if (getDirs != null)
                    {
                        var dirs = getDirs.Invoke(null, null) as System.Collections.IEnumerable;
                        if (dirs != null) foreach (var o in dirs) { var d = o as string; if (!string.IsNullOrEmpty(d)) lock (_dirLock) { if (!_modDirs.Contains(d)) _modDirs.Add(d); } }
                    }
                }
                catch { }
                if (_coreEntry == null || _mExecute == null) LoadCore(logMissing: false);

                try { SwapConsoleCommand(oldBootstrap); } catch (Exception e) { Log.Error("[HotReload] bootstrap command swap: " + e.Message); }
                try { oldBootstrap.GetMethod("Deactivate", BindingFlags.Public | BindingFlags.Static).Invoke(null, null); }
                catch (Exception e) { Log.Warning("[HotReload] bootstrap deactivate old: " + e.Message); }
            }

            StartCoreWatcher();             // watch core\ for rebuilds -> core hot-swap
            // Networking bootstrap updates load on the next game start.
        }

        // handles the current core so a newer bootstrap can adopt it without reloading
        public static object[] GetCoreHandles()
        {
            return new object[] { _core, _coreEntry, _mInit, _mShutdown, _mExecute, _coreFileVersion };
        }

        // retire this bootstrap (a newer one has taken over the command + watchers)
        public static void Deactivate()
        {
            _deactivated = true;
            try { if (_coreTimer != null) { _coreTimer.Dispose(); _coreTimer = null; } } catch { }
            try { if (_coreWatcher != null) { _coreWatcher.EnableRaisingEvents = false; _coreWatcher.Dispose(); _coreWatcher = null; } } catch { }
            try { if (_bootTimer != null) { _bootTimer.Dispose(); _bootTimer = null; } } catch { }
            try { if (_bootWatcher != null) { _bootWatcher.EnableRaisingEvents = false; _bootWatcher.Dispose(); _bootWatcher = null; } } catch { }
            Log.Out("[HotReload] bootstrap v" + BootstrapVersion + " deactivated (superseded by a newer bootstrap)");
        }

        // point the live 'hr' command at THIS assembly's command class
        static void SwapConsoleCommand(Type oldBootstrap)
        {
            SdtdConsole sc = null;
            try { sc = SdtdConsole.Instance; } catch { }
            if (sc == null || sc.m_Commands == null) return;
            int removed = 0;
            for (int i = sc.m_Commands.Count - 1; i >= 0; i--)
            {
                var c = sc.m_Commands[i];
                if (c == null) continue;
                try { if (c.GetType().Assembly == oldBootstrap.Assembly) { sc.m_Commands.RemoveAt(i); removed++; } } catch { }
            }
            var newCmd = (IConsoleCommand)Activator.CreateInstance(typeof(ConsoleCmdHotReload));
            sc.m_Commands.Add(newCmd);
            foreach (var alias in newCmd.GetCommands())
            {
                if (string.IsNullOrEmpty(alias)) continue;
                if (sc.m_CommandsAllVariants != null)
                {
                    if (sc.m_CommandsAllVariants.ContainsKey(alias)) sc.m_CommandsAllVariants[alias] = newCmd;
                    else sc.m_CommandsAllVariants.Add(alias, newCmd);
                }
            }
            try { sc.m_CommandsReadOnly = new ReadOnlyCollection<IConsoleCommand>(sc.m_Commands); } catch { }
            Log.Out("[HotReload] command instance swapped to bootstrap v" + BootstrapVersion + " (removed " + removed + " old instance(s))");
        }

        // ---------------------------------------------------------------
        // self-watch: HotReloadTool.dll rebuild -> bootstrap hot-swap
        // ---------------------------------------------------------------
        static void StartSelfWatcher()
        {
            try
            {
                var p = OwnDllPath();
                if (p == null || !File.Exists(p)) return;
                _ownHash = FileHash(p);
                var w = new FileSystemWatcher(ModDir, "HotReloadTool.dll");
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime;
                FileSystemEventHandler h = (s2, e2) => { _bootDirty = Environment.TickCount; _bootTimer.Change(2100, Timeout.Infinite); };
                RenamedEventHandler r = (s2, e2) => { _bootDirty = Environment.TickCount; _bootTimer.Change(2100, Timeout.Infinite); };
                w.Changed += h; w.Created += h; w.Deleted += h; w.Renamed += r;
                w.EnableRaisingEvents = true;
                _bootWatcher = w;
                _bootTimer = new Timer(OnBootTimer, null, Timeout.Infinite, Timeout.Infinite);
                Log.Out("[HotReload] bootstrap: watching itself for rebuilds: " + p);
            }
            catch (Exception e) { Log.Error("[HotReload] bootstrap self-watch: " + e.Message); }
        }

        static string OwnDllPath()
        {
            try { return Path.Combine(ModDir, "HotReloadTool.dll"); } catch { return null; }
        }

        static string FileHash(string p)
        {
            try
            {
                var b = File.ReadAllBytes(p);
                unchecked
                {
                    uint h = 2166136261u;
                    for (int i = 0; i < b.Length; i++) { h ^= b[i]; h *= 16777619u; }
                    return h.ToString("x8") + ":" + b.Length;
                }
            }
            catch { return ""; }
        }

        static void OnBootTimer(object state)
        {
            try
            {
                if (_deactivated || _swappingBootstrap || _bootDirty == 0) return;

                _bootDirty = 0;
                var p = OwnDllPath();
                if (p == null) return;
                var h = FileHash(p);
                if (string.IsNullOrEmpty(h) || h == _ownHash) return; // same content: nothing to do
                _ownHash = h;
                try { ThreadManager.AddSingleTaskMainThread("hr.bootstrapSwap", () => SwapSelf(p)); }
                catch { SwapSelf(p); }
            }
            catch (Exception e) { Log.Error("[HotReload] bootstrap swap timer: " + e.Message); }
        }

        static void SwapSelf(string path)
        {
            if (_swappingBootstrap || _deactivated) return;
            _swappingBootstrap = true;
            try
            {
                var asm = Assembly.Load(File.ReadAllBytes(path));
                var t = asm.GetType("HotReloadTool.Bootstrap");
                if (t == null) { Log.Error("[HotReload] bootstrap swap: new dll has no Bootstrap type"); return; }
                var m = t.GetMethod("StartHotSwap", BindingFlags.Public | BindingFlags.Static);
                if (m == null) { Log.Error("[HotReload] bootstrap swap: new dll has no StartHotSwap"); return; }
                m.Invoke(null, new object[] { HostMod, typeof(Bootstrap) });
                Log.Out("[HotReload] bootstrap hot-swapped -> " + path);
            }
            catch (Exception e) { Log.Error("[HotReload] bootstrap swap failed: " + e); }
            finally { _swappingBootstrap = false; }
        }

        // -------------------------------------------------------------------
        // interception
        // -------------------------------------------------------------------
        static void InstallInterceptors()
        {
            try
            {
                _loaderHarmony = new Harmony("hotreload.bootstrap.loader");

                var mMod = typeof(Mod).GetMethod("loadAssembly", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (mMod != null)
                {
                    var p = new HarmonyMethod(typeof(Bootstrap).GetMethod("LoadAssemblyPrefix", BindingFlags.Public | BindingFlags.Static));
                    _loaderHarmony.Patch(mMod, p, null, null, null, null);
                    Log.Out("[HotReload] bootstrap: mod-dll loader intercepted (byte-load; files stay unlocked)");
                }
                else Log.Warning("[HotReload] bootstrap: Mod.loadAssembly not found; loader intercept skipped");

                var mLoad = typeof(Assembly).GetMethod("LoadFrom", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (mLoad != null)
                {
                    var p = new HarmonyMethod(typeof(Bootstrap).GetMethod("AssemblyLoadFromPrefix", BindingFlags.Public | BindingFlags.Static));
                    _loaderHarmony.Patch(mLoad, p, null, null, null, null);
                    Log.Out("[HotReload] bootstrap: Assembly.LoadFrom intercepted (our dlls load as fresh copies)");
                }
            }
            catch (Exception e) { Log.Error("[HotReload] bootstrap interceptors: " + e.Message); }
        }

        // record assemblies the GAME loaded for every already-loaded mod, so the core
        // has a complete picture of mod code even though our intercept came later
        static void SeedTrackedFromLoadedMods()
        {
            try
            {
                var mods = ModManager.GetLoadedMods();
                if (mods == null) return;
                int n = 0;
                foreach (var m in mods)
                {
                    if (m == null) continue;
                    try
                    {
                        var list = m.AllAssemblies;
                        if (list == null) continue;
                        foreach (var a in list)
                        {
                            if (a == null) continue;
                            Track(a); n++;
                            try
                            {
                                var loc = a.Location;
                                if (!string.IsNullOrEmpty(loc))
                                {
                                    var d = Path.GetDirectoryName(loc);
                                    if (!string.IsNullOrEmpty(d)) lock (_dirLock) { if (!_modDirs.Contains(d)) _modDirs.Add(d); }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                if (n > 0) Log.Out("[HotReload] bootstrap: seeded " + n + " already-loaded mod assembly(ies)");
            }
            catch { }
        }

        // an AssemblyResolve probe: byte-loaded assemblies can't probe their own folder,
        // so we do it for them (looks in every known mod folder + its lib\ subfolder).
        [ThreadStatic] static bool _resolving;

        static void InstallResolveProbe()
        {
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += (s2, e2) =>
                {
                    // reentrancy guard: if this handler re-fires for the same load
                    // (self-referencing chain) it would recurse until the stack blows.
                    if (_resolving) return null;
                    _resolving = true;
                    try
                    {
                        var shortName = new AssemblyName(e2.Name).Name;
                        if (string.IsNullOrEmpty(shortName)) return null;
                        string[] dirs;
                        lock (_dirLock) { dirs = _modDirs.ToArray(); }
                        foreach (var d in dirs)
                        {
                            if (string.IsNullOrEmpty(d)) continue;
                            var cands = new[] { Path.Combine(d, shortName + ".dll"), Path.Combine(d, "lib", shortName + ".dll") };
                            foreach (var f in cands)
                            {
                                try
                                {
                                    if (File.Exists(f))
                                    {
                                        var a = Assembly.Load(File.ReadAllBytes(f));
                                        Track(a);
                                        Log.Out("[HotReload] resolved dependency '" + shortName + "' from " + f);
                                        return a;
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    finally { _resolving = false; }
                    return null;
                };
            }
            catch (Exception e) { Log.Warning("[HotReload] resolve probe: " + e.Message); }
        }

        public static bool LoadAssemblyPrefix(string __0, ref Assembly __result)
        {
            try
            {
                if (!string.IsNullOrEmpty(__0) && File.Exists(__0)
                    && (IsOurFile(__0) || __0.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                {
                    // opt-out: a mod can keep classic LoadFrom semantics with a nohotload.txt marker
                    try
                    {
                        var dir = Path.GetDirectoryName(Path.GetFullPath(__0));
                        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "nohotload.txt")))
                        {
                            lock (_dirLock) { if (!_modDirs.Contains(dir)) _modDirs.Add(dir); }
                            return true; // classic loader; folder still probed for the swap flow
                        }
                        lock (_dirLock) { if (!_modDirs.Contains(dir)) _modDirs.Add(dir); }
                    }
                    catch { }
                    __result = Assembly.Load(File.ReadAllBytes(__0));
                    Track(__result);
                    return false;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[HotReload] byte-load '" + __0 + "' failed (" + e.Message + "); falling back to default loader");
            }
            return true;
        }

        // Only redirect OUR OWN folder - mods' own LoadFrom calls keep working normally.
        public static bool AssemblyLoadFromPrefix(string __0, ref Assembly __result)
        {
            try
            {
                if (!string.IsNullOrEmpty(__0) && File.Exists(__0) && IsOurFile(__0))
                {
                    __result = Assembly.Load(File.ReadAllBytes(__0));
                    Track(__result);
                    return false;
                }
            }
            catch { }
            return true;
        }

        static bool IsOurFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(ModDir)) return false;
                var full = Path.GetFullPath(path).TrimEnd('\\', '/');
                var dir = Path.GetFullPath(ModDir).TrimEnd('\\', '/');
                return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // -------------------------------------------------------------------
        // core loading / hot-swap
        // -------------------------------------------------------------------
        static string CoreBakPath()
        {
            try { return Path.Combine(Path.GetDirectoryName(CorePath()), "HotReloadCore.bak.dll"); } catch { return null; }
        }

        static string CorePath()
        {
            try
            {
                if (_coreDir == null) _coreDir = Path.Combine(ModDir, "core");
                return Path.Combine(_coreDir, CORE_FILE);
            }
            catch { return null; }
        }

        static bool LoadCore(bool logMissing)
        {
            lock (_loadLock)
            {
                var path = CorePath();
                if (path == null || !File.Exists(path))
                {
                    if (logMissing) Log.Error("[HotReload] core not found: " + (path ?? "(no path)") + " - run build-hotreload.bat to create it");
                    return false;
                }
                try
                {
                    var asm = Assembly.Load(File.ReadAllBytes(path));
                    var t = asm.GetType("HotReloadTool.CoreEntry", false);
                    if (t == null) { Log.Error("[HotReload] core has no HotReloadTool.CoreEntry type"); return false; }

                    var mInit = t.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
                    var mShut = t.GetMethod("Shutdown", BindingFlags.Public | BindingFlags.Static);
                    var mExec = t.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                    if (mInit == null || mExec == null) { Log.Error("[HotReload] core missing Init/Execute"); return false; }

                    _core = asm; _coreEntry = t; _mInit = mInit; _mShutdown = mShut; _mExecute = mExec;
                    try
                    {
                        var vf = t.GetField("CoreVersion", BindingFlags.Public | BindingFlags.Static);
                        _coreFileVersion = vf != null ? ("v" + vf.GetRawConstantValue()) : "v?";
                    }
                    catch { _coreFileVersion = "v?"; }

                    bool ok = false;
                    try { ok = (bool)mInit.Invoke(null, new object[] { HostMod }); }
                    catch (TargetInvocationException tie)
                    {
                        Log.Error("[HotReload] core Init threw: " + (tie.InnerException != null ? tie.InnerException.ToString() : tie.Message));
                        ok = false;
                    }
                    if (ok)
                    {
                        // this core works - keep it as the rollback target
                        try { File.Copy(path, CoreBakPath(), true); } catch { }
                    }
                    Log.Out("[HotReload] core loaded: " + path + "  init " + (ok ? "ok" : "reported failure"));
                    return ok;
                }
                catch (Exception e)
                {
                    Log.Error("[HotReload] core load failed: " + e);
                    return false;
                }
            }
        }

        static void StartCoreWatcher()
        {
            try
            {
                var dir = Path.GetDirectoryName(CorePath());
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var w = new FileSystemWatcher(dir, CORE_FILE);  // exactly the core file (not .bak/.new)
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime;
                w.EnableRaisingEvents = false;
                FileSystemEventHandler h = (s, e) => { _coreDirty = Environment.TickCount; _coreTimer.Change(1600, Timeout.Infinite); };
                RenamedEventHandler r = (s, e) => { _coreDirty = Environment.TickCount; _coreTimer.Change(1600, Timeout.Infinite); };
                w.Changed += h; w.Created += h; w.Deleted += h; w.Renamed += r;
                _coreWatcher = w;
                _coreTimer = new Timer(OnCoreTimer, null, Timeout.Infinite, Timeout.Infinite);
                w.EnableRaisingEvents = true;
                Log.Out("[HotReload] bootstrap: watching core folder for rebuilds: " + dir);
            }
            catch (Exception e) { Log.Error("[HotReload] core watcher: " + e.Message); }
        }

        static FileSystemWatcher _coreWatcher;
        static Timer _coreTimer;
        static volatile int _coreDirty;
        static volatile bool _deactivated;
        static volatile bool _swappingBootstrap;
        static FileSystemWatcher _bootWatcher;
        static Timer _bootTimer;
        static volatile int _bootDirty;
        static string _ownHash = "";

        static void OnCoreTimer(object state)
        {
            try
            {
                if (_deactivated || _swapping || _coreDirty == 0) return;

                _coreDirty = 0;
                // always perform the swap on the game's main thread
                try { ThreadManager.AddSingleTaskMainThread("HotReload.coreSwap", () => SwapCore(null)); }
                catch (Exception e) { Log.Error("[HotReload] Cannot schedule core swap: " + e.Message); }
            }
            catch (Exception e) { Log.Error("[HotReload] core swap timer: " + e.Message); }
        }

        /// <summary>Tear down the old core and load the new one from disk.</summary>
        public static string SwapCore(string expectedVersion)
        {
            if (JonSyncTransport.RestartRequired) return "[HotReload] Restart the game before swapping the core after a pack install";
            if (_deactivated) return "[HotReload] bootstrap deactivated (a newer one handles 'hr')";
            if (_swapping) return "[HotReload] core swap already in progress";
            _swapping = true;
            try
            {
                // 1) tell the old core to let go (stops timers/watchers, sweeps patches)
                if (_mShutdown != null)
                {
                    try { _mShutdown.Invoke(null, null); }
                    catch (Exception e) { Log.Warning("[HotReload] core shutdown: " + e.Message); }
                }

                // 2) drop the core type handles so the fresh assembly is reachable
                _core = null; _coreEntry = null; _mInit = _mShutdown = _mExecute = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // 3) load + init the new core
                var ok = LoadCore(false);
                if (!ok)
                {
                    // the new core is broken: put the previous one back so the tool stays usable
                    var bak = CoreBakPath();
                    if (bak != null && File.Exists(bak))
                    {
                        try
                        {
                            File.Copy(bak, CorePath(), true);
                            if (LoadCore(false))
                            {
                                Log.Warning("[HotReload] new core failed; rolled back to the previous core");
                                return "[HotReload] core swap FAILED (new core broken) - rolled back to the previous core. Fix and rebuild.";
                            }
                        }
                        catch (Exception e) { Log.Error("[HotReload] rollback: " + e.Message); }
                    }
                    return "[HotReload] core swap FAILED: new core did not load; run build-hotreload.bat then retry";
                }

                var ver = _coreFileVersion;
                var msg = "[HotReload] core hot-swapped -> " + ver;
                Log.Out(msg);
                try { SdtdConsole.Instance.Output(msg); } catch { }
                return msg + (expectedVersion != null ? " (expected " + expectedVersion + ")" : "");
            }
            finally { _swapping = false; }
        }

        // -------------------------------------------------------------------
        // command surface (stays alive across core swaps)
        // -------------------------------------------------------------------
        public static string Execute(List<string> args)
        {
            if (_deactivated) return "[HotReload] bootstrap deactivated (a newer one handles 'hr')";
            var sub = (args != null && args.Count > 0) ? args[0].ToLowerInvariant() : "status";
            if (sub == "core")
            {
                var act = (args.Count > 1) ? args[1].ToLowerInvariant() : "status";
                switch (act)
                {
                    case "reload": case "swap":
                        return SwapCore(null);
                    case "status":
                        return "[HotReload] bootstrap v" + BootstrapVersion + "  core: " + CoreStatus + "  file: " + CorePath() + "\n  (hr doctor for the full API check)";
                    default:
                        return "[HotReload] usage: hr core status|reload";
                }
            }

            if (_mExecute == null)
                return "[HotReload] core not loaded - check the log (bootstrap v" + BootstrapVersion + ")";

            try
            {
                var result = (string)_mExecute.Invoke(null, new object[] { args });
                if (sub == "status")
                {
                    // surface the bootstrap + core layering in plain status output
                    return "[HotReload] bootstrap v" + BootstrapVersion + " / core " + CoreStatus + "\n" + result;
                }
                return result;
            }
            catch (TargetInvocationException tie)
            {
                var inner = tie.InnerException != null ? tie.InnerException.Message : tie.Message;
                return "[HotReload] core error: " + inner;
            }
            catch (Exception e)
            {
                return "[HotReload] core error: " + e.Message;
            }
        }
    }

    /// <summary>The 'hr' command. Delegates to the core; survives core hot-swaps.</summary>
    public class ConsoleCmdHotReload : ConsoleCmdAbstract
    {
        public override string[] getCommands() { return new[] { "hr", "hotreload" }; }

        public override string getDescription()
        {
            return "Hot reload: new mods, XML/XUI/localization, C# code - and the tool itself. No restarts.";
        }

        public override string getHelp()
        {
            return "HotReloadTool v" + Bootstrap.BootstrapVersion + " (bootstrap; core: " + Bootstrap.CoreStatus + ")\\n" +
                   "  hr mods|unload|reapply|xml|xui|loc|code|build|all|watch|status|doctor\\n" +
                   "  hr core status|reload   - the tool's own hot-swap\\n" +
                   "Core help: run 'hr' with no args.";
        }

        public override void Execute(List<string> args, CommandSenderInfo _senderInfo)
        {
            // telnet/network commands arrive on a server thread: marshal to the main thread
            if (!ThreadManager.IsMainThread())
            {
                try
                {
                    ThreadManager.AddSingleTaskMainThread("HotReload.cmd", () => Inner(args, _senderInfo));
                    return;
                }
                catch { }
            }
            Inner(args, _senderInfo);
        }

        void Inner(List<string> args, CommandSenderInfo _senderInfo)
        {
            string msg;
            try { msg = Bootstrap.Execute(args); }
            catch (Exception e) { msg = "[HotReload] command failed: " + e; Log.Exception(e); }
            try { SdtdConsole.Instance.Output(msg); } catch { }
        }
    }
}

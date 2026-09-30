using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Newtonsoft.Json.Linq;

namespace HotReloadTool
{
    // Portable packs contain the actual files, including manually installed mods.
    // This class is also the install boundary for multiplayer transfers.
    public static class ModFiles
    {
        public static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        public static string Child(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":"))
                throw new InvalidDataException("Invalid pack path");
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Pack path escapes its folder");
            return full;
        }

        public static bool Protected(string folder)
        {
            return string.Equals(folder, "HotReloadTool", StringComparison.OrdinalIgnoreCase)
                || string.Equals(folder, "TFP_Harmony", StringComparison.OrdinalIgnoreCase)
                || folder.StartsWith("0_TFP_", StringComparison.OrdinalIgnoreCase);
        }

        public static void ExtractBrowserZip(string archive, string stage)
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    var path = Child(stage, entry.FullName);
                    if (!paths.Add(path)) throw new InvalidDataException("Duplicate archive path");
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    entry.ExtractToFile(path);
                }
            }
        }

        internal static string ModName(string folder)
        {
            var xml = new XmlDocument { XmlResolver = null };
            xml.Load(Path.Combine(folder, "ModInfo.xml"));
            var name = xml.SelectSingleNode("/xml/Name/@value") ?? xml.SelectSingleNode("/xml/ModInfo/Name/@value");
            if (name == null || string.IsNullOrWhiteSpace(name.Value)) throw new InvalidDataException("Missing mod name: " + folder);
            return name.Value;
        }

        public static JObject Inventory(string root, string name, string game)
        { return Inventory(new[] { root }, name, game); }

        static IEnumerable<string> ModDirectories(IEnumerable<string> roots)
        {
            foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
                if (Directory.Exists(root))
                    foreach (var dir in Directory.GetDirectories(root))
                        if (File.Exists(Path.Combine(dir, "ModInfo.xml")) && !Protected(Path.GetFileName(dir)) && !Protected(ModName(dir)))
                            yield return dir;
        }
        static IEnumerable<string> Files(string directory, bool sourceRoot = true)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked mod folders cannot be shared");
            foreach (var file in Directory.GetFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked files cannot be shared");
                yield return file;
            }
            foreach (var child in Directory.GetDirectories(directory))
            {
                // Source-live compiler output is local runtime state, not mod
                // content. Its fresh Unity identities differ after every reload.
                if (sourceRoot && string.Equals(Path.GetFileName(child), "cache", StringComparison.OrdinalIgnoreCase)
                    && Directory.GetDirectories(directory).Any(d => new[] { "src", "source", "sources" }.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))) continue;
                foreach (var file in Files(child, false)) yield return file;
            }
        }

        public static JObject Inventory(IEnumerable<string> roots, string name, string game)
        {
            var mods = new JArray();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in ModDirectories(roots).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var folder = Path.GetFileName(dir);
                if (!folders.Add(folder)) throw new InvalidDataException("Duplicate mod folder across Mods locations: " + folder);
                var modName = ModName(dir);
                if (!names.Add(modName)) throw new InvalidDataException("Duplicate mod name: " + modName);
                var files = new JArray();
                foreach (var file in Files(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked files cannot be shared");
                    var relative = file.Substring(dir.Length + 1).Replace('\\', '/');
                    files.Add(new JObject { ["path"] = relative, ["sha256"] = HashFile(file), ["size"] = new FileInfo(file).Length });
                }
                mods.Add(new JObject { ["folder"] = folder, ["name"] = modName, ["files"] = files });
            }
            return new JObject { ["format"] = 2, ["name"] = name, ["game"] = game, ["mods"] = mods };
        }

        public static string Fingerprint(JObject manifest)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(manifest["mods"].ToString(Newtonsoft.Json.Formatting.None))))
                    .Replace("-", "").ToLowerInvariant();
        }

        public static JObject Export(string root, string destination, string name, string game)
        { return Export(new[] { root }, destination, name, game); }

        public static JObject Export(IEnumerable<string> roots, string destination, string name, string game)
        {
            var locations = ModDirectories(roots).ToDictionary(Path.GetFileName, x => x, StringComparer.OrdinalIgnoreCase);
            var manifest = Inventory(roots, name, game);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var info = zip.CreateEntry("pack.json");
                using (var writer = new StreamWriter(info.Open(), new UTF8Encoding(false))) writer.Write(manifest.ToString());
                foreach (var mod in (JArray)manifest["mods"])
                    foreach (var file in (JArray)mod["files"])
                    {
                        var relative = (string)mod["folder"] + "/" + (string)file["path"];
                        var source = Child(locations[(string)mod["folder"]], (string)file["path"]);
                        zip.CreateEntryFromFile(source, "Mods/" + relative, CompressionLevel.Optimal);
                        if (HashFile(source) != (string)file["sha256"]) throw new IOException("Mod changed while packing; try again");
                    }
            }
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            return manifest;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static JObject ReadManifest(string archive)
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                var entry = zip.GetEntry("pack.json");
                if (entry == null) throw new InvalidDataException("This is not a portable mod pack");
                using (var reader = new StreamReader(entry.Open())) return JObject.Parse(reader.ReadToEnd());
            }
        }

        public static void Install(string archive, string root, string backupRoot, string game, bool exact)
        { Install(archive, new[] { root }, backupRoot, game, exact); }

        public static void Install(string archive, IEnumerable<string> roots, string backupRoot, string game, bool exact)
        { lock (installGate) InstallLocked(archive, roots, backupRoot, game, exact); }
        static readonly object installGate = new object();
        static void InstallLocked(string archive, IEnumerable<string> roots, string backupRoot, string game, bool exact)
        {
            var targets = roots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (targets.Length == 0) throw new InvalidDataException("No Mods folder");
            var root = targets[0];
            var manifest = ReadManifest(archive);
            if ((int?)manifest["format"] != 2 || (string)manifest["game"] != game) throw new InvalidDataException("Pack requires game " + manifest["game"]);
            var mods = manifest["mods"] as JArray;
            if (mods == null) throw new InvalidDataException("Missing mod list");
            var work = Path.Combine(backupRoot, Guid.NewGuid().ToString("N"));
            var stage = Path.Combine(work, "staged");
            var backup = Path.Combine(work, "previous");
            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(backup);
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var zip = ZipFile.OpenRead(archive))
            {
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    Child(stage, entry.FullName);
                    if (entries.ContainsKey(entry.FullName)) throw new InvalidDataException("Duplicate archive path");
                    entries.Add(entry.FullName, entry);
                }
                foreach (var mod in mods)
                {
                    var folder = (string)mod["folder"];
                    if (string.IsNullOrWhiteSpace(folder) || Path.GetFileName(folder) != folder || folder.Contains('/') || folder.Contains('\\')
                        || Protected(folder) || Protected((string)mod["name"] ?? "") || !folders.Add(folder) || !names.Add((string)mod["name"])) throw new InvalidDataException("Invalid or duplicate mod folder");
                    var files = mod["files"] as JArray;
                    if (files == null) throw new InvalidDataException("Missing file list");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files)
                    {
                        var path = (string)file["path"];
                        var target = Child(Child(stage, folder), path);
                        if (!seen.Add(target)) throw new InvalidDataException("Duplicate mod file");
                        ZipArchiveEntry entry;
                        if (!entries.TryGetValue("Mods/" + folder + "/" + path, out entry) || entry.Length != (long)file["size"])
                            throw new InvalidDataException("Missing or changed file: " + path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target);
                        if (HashFile(target) != (string)file["sha256"]) throw new InvalidDataException("File checksum failed: " + path);
                    }
                    if (ModName(Child(stage, folder)) != (string)mod["name"]) throw new InvalidDataException("Mod identity mismatch");
                }
            }
            // All bytes are verified before any existing mod is moved. Roll back the
            // whole operation on disk errors; backups remain available afterwards.
            var moved = new List<KeyValuePair<string, string>>();
            var installed = new List<string>();
            try
            {
                Directory.CreateDirectory(root);
                foreach (var location in targets)
                {
                if (!Directory.Exists(location)) continue;
                foreach (var dir in Directory.GetDirectories(location))
                {
                    var folder = Path.GetFileName(dir);
                    if (Protected(folder) || (File.Exists(Path.Combine(dir, "ModInfo.xml")) && Protected(ModName(dir)))) continue;
                    if (folders.Contains(folder) || (File.Exists(Path.Combine(dir, "ModInfo.xml")) && (exact || names.Contains(ModName(dir)))))
                    {
                        var saved = Child(backup, Array.IndexOf(targets, location) + "/" + folder);
                        Directory.CreateDirectory(Path.GetDirectoryName(saved));
                        Directory.Move(dir, saved);
                        moved.Add(new KeyValuePair<string, string>(dir, saved));
                    }
                }
                }
                foreach (var folder in folders)
                {
                    Directory.Move(Child(stage, folder), Child(root, folder));
                    installed.Add(folder);
                }
            }
            catch
            {
                foreach (var folder in installed) Directory.Move(Child(root, folder), Child(stage, folder));
                for (int i = moved.Count - 1; i >= 0; i--) Directory.Move(moved[i].Value, moved[i].Key);
                throw;
            }
        }
    }
}

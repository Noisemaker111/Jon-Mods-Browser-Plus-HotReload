using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HotReloadTool
{
    // Shared by live editing and the offline mod build/package workflow.
    public static class ModCompiler
    {
        public static void Publish(byte[] bytes, string destination)
        {
            var temporary = destination + ".publish-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public class Result
        {
            public bool Success;
            public byte[] Bytes;
            public string Diagnostics;
            public long Milliseconds;
        }

        public static Result Compile(string compiler, IEnumerable<string> sources, IEnumerable<string> references, string destination, int timeoutMilliseconds = 30000, bool freshIdentity = false)
        {
            var clock = Stopwatch.StartNew();
            var result = new Result();
            var parent = Path.GetDirectoryName(Path.GetFullPath(destination));
            var temporary = Path.Combine(parent, ".compile-" + Guid.NewGuid().ToString("N"));
            // Unity caches component types by assembly identity. A live rebuild
            // must have its own identity even though the on-disk cache stays stable.
            var output = Path.Combine(temporary, freshIdentity ? Path.GetFileNameWithoutExtension(destination) + "_live_" + Guid.NewGuid().ToString("N") + ".dll" : Path.GetFileName(destination));
            var response = Path.Combine(temporary, "build.rsp");
            try
            {
                Directory.CreateDirectory(temporary);
                var options = new List<string> { "-nologo", "-target:library", "-platform:x64", "-langversion:latest", "-deterministic+", "-out:\"" + output + "\"" };
                options.AddRange(references.Select(x => "-r:\"" + Path.GetFullPath(x) + "\""));
                options.AddRange(sources.Select(x => "\"" + Path.GetFullPath(x) + "\""));
                File.WriteAllLines(response, options);
                var start = new ProcessStartInfo(compiler, "-noconfig @\"" + response + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = parent
                };
                using (var process = Process.Start(start))
                {
                    // Drain both streams while waiting for the process exit event.
                    // Waiting first can deadlock on a full compiler diagnostics pipe.
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    bool exited = process.WaitForExit(timeoutMilliseconds);
                    if (!exited) { process.Kill(); process.WaitForExit(); }
                    Task.WaitAll(stdout, stderr);
                    result.Diagnostics = (stdout.Result + "\n" + stderr.Result).Trim();
                    if (!exited) { result.Diagnostics = "Compiler timed out.\n" + result.Diagnostics; return result; }
                    if (process.ExitCode != 0 || !File.Exists(output))
                    {
                        if (string.IsNullOrEmpty(result.Diagnostics)) result.Diagnostics = "Compiler exited with code " + process.ExitCode + " without a new DLL";
                        return result;
                    }
                }
                var bytes = File.ReadAllBytes(output);
                if (File.Exists(destination)) File.Replace(output, destination, null);
                else File.Move(output, destination);
                result.Bytes = bytes;
                result.Success = true;
                return result;
            }
            catch (Exception e) { result.Diagnostics = e.ToString(); return result; }
            finally
            {
                result.Milliseconds = clock.ElapsedMilliseconds;
                try { if (File.Exists(response)) File.Delete(response); if (File.Exists(output)) File.Delete(output); if (Directory.Exists(temporary)) Directory.Delete(temporary); } catch { }
            }
        }
    }
}

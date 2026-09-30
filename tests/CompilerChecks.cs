using System;
using System.IO;
using System.Text;
using HotReloadTool;

class CompilerChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    static int Main(string[] args)
    {
        try
        {
            string root = args[0], compiler = args[1];
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "Mod.cs"), dll = Path.Combine(root, "Mod.dll");
            File.WriteAllText(source, "public class Mod { public static string Value = \"first\"; }");
            var built = ModCompiler.Compile(compiler, new[] { source }, new string[0], dll);
            Check(built.Success && built.Bytes.Length > 0 && File.Exists(dll), "real compiler produces a fresh usable DLL");
            byte[] previous = File.ReadAllBytes(dll);
            File.WriteAllText(source, "this is invalid C#");
            var failed = ModCompiler.Compile(compiler, new[] { source }, new string[0], dll);
            Check(!failed.Success && failed.Bytes == null && !string.IsNullOrEmpty(failed.Diagnostics), "compile failure is reported despite an existing DLL");
            Check(Convert.ToBase64String(previous) == Convert.ToBase64String(File.ReadAllBytes(dll)), "compile failure preserves the previous successful DLL");
            var manyErrors = new StringBuilder("public class Broken {\n");
            for (int i = 0; i < 1000; i++) manyErrors.Append("MissingType" + i + " field" + i + ";\n");
            manyErrors.Append("}");
            File.WriteAllText(source, manyErrors.ToString());
            var verbose = ModCompiler.Compile(compiler, new[] { source }, new string[0], dll);
            Check(!verbose.Success && verbose.Diagnostics.Contains("MissingType999"), "large compiler diagnostics finish without pipe deadlock or truncated errors");
            File.WriteAllText(source, "public class Mod { public static string Value = \"updated\"; }");
            var updated = ModCompiler.Compile(compiler, new[] { source }, new string[0], dll);
            Check(updated.Success && Convert.ToBase64String(previous) != Convert.ToBase64String(updated.Bytes), "the next corrected edit replaces the old DLL");
            var timed = ModCompiler.Compile(compiler, new[] { source }, new string[0], dll, 1);
            Check(!timed.Success && timed.Diagnostics.Contains("timed out") && Convert.ToBase64String(updated.Bytes) == Convert.ToBase64String(File.ReadAllBytes(dll)), "timeout ends the compiler and preserves the last good DLL");
            Check(Directory.GetDirectories(root, ".compile-*").Length == 0, "all completed, failed and timed-out compiler work is cleaned up");
            Console.WriteLine("Compiler checks passed"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}

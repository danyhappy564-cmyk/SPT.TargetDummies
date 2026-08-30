using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

// One-off tool: dumps type/member info from the real installed EFT client assemblies so the
// SPT 4.0.13 backport can be fixed against actual signatures instead of guesses. Not part of
// the mod - delete this project once the backport is done.
//
// Round 5: rounds 1-4 resolved everything except EBoundItem - its enum members (ItemG, ItemV,
// Item1..Item10) have no semantic naming, so there's no way to guess which one backs
// EFTInventoryClass.Equipment from reflection alone. Decompiles that property's getter (and
// .Stash's, for comparison) to read the actual IL/C#, isolated in a child process so a crash here
// (same StackOverflowException risk noted in spt-hideout-shootout's own DumpTool for the game's
// heavily obfuscated assemblies) only loses this one lookup.
class Program
{
    static string ManagedDir;
    static string BepInExCoreDir;

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--decompile-one")
        {
            RunDecompileWorker(args);
            return;
        }

        string sptRoot = args.Length > 0 ? args[0] : @"E:\SPT 4.0.10";
        ManagedDir = Path.Combine(sptRoot, "EscapeFromTarkov_Data", "Managed");
        BepInExCoreDir = Path.Combine(sptRoot, "BepInEx", "core");

        if (!Directory.Exists(ManagedDir))
        {
            Console.WriteLine("Managed folder not found at: " + ManagedDir);
            Console.WriteLine("Pass the SPT root as the first argument if it's not " + sptRoot);
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(ManagedDir, name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
            if (Directory.Exists(BepInExCoreDir))
            {
                path = Path.Combine(BepInExCoreDir, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };

        using var w = new StreamWriter("dump.txt", false, Encoding.UTF8);

        w.WriteLine("=== decompiling EFTInventoryClass.Equipment / .Stash getters (child-process isolated) ===");
        w.Flush();

        string exePath = Process.GetCurrentProcess().MainModule.FileName;

        void RunOne(string header, string methodName)
        {
            w.WriteLine("--- " + header + " ---");
            w.Flush();

            string outFile = Path.GetTempFileName();
            try
            {
                var workerArgs = new List<string> { "--decompile-one", sptRoot, outFile, "EFTInventoryClass", methodName };
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    // ProcessStartInfo.ArgumentList isn't available on net48 - build the command
                    // line by hand, quoting every argument (sptRoot alone contains a space).
                    Arguments = string.Join(" ", workerArgs.Select(a => "\"" + a.Replace("\"", "\\\"") + "\"")),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using (Process proc = Process.Start(psi))
                {
                    bool exited = proc.WaitForExit(30000);
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                        w.WriteLine("  <decompile worker timed out after 30s>");
                    }
                    else if (proc.ExitCode != 0)
                    {
                        w.WriteLine($"  <decompile worker crashed or exited with code {proc.ExitCode}>");
                    }
                    else if (File.Exists(outFile) && new FileInfo(outFile).Length > 0)
                    {
                        w.WriteLine(File.ReadAllText(outFile, Encoding.UTF8));
                    }
                    else
                    {
                        w.WriteLine("  <worker exited cleanly but produced no output>");
                    }
                }
            }
            catch (Exception ex)
            {
                w.WriteLine("  <failed to run decompile worker: " + ex.Message + ">");
            }
            finally
            {
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            }

            w.WriteLine();
            w.Flush();
        }

        RunOne("EFTInventoryClass.get_Equipment", "get_Equipment");
        RunOne("EFTInventoryClass.get_Stash", "get_Stash");

        Console.WriteLine("Wrote dump.txt");
    }

    // Runs in a separate child process (spawned by RunOne above), isolated so a
    // StackOverflowException from decompiling this method only kills this child.
    // args: --decompile-one <sptRoot> <outFile> <fullTypeName> <methodName>
    static void RunDecompileWorker(string[] args)
    {
        string sptRoot = args[1];
        string outFile = args[2];
        string fullTypeName = args[3];
        string methodName = args[4];

        ManagedDir = Path.Combine(sptRoot, "EscapeFromTarkov_Data", "Managed");
        BepInExCoreDir = Path.Combine(sptRoot, "BepInEx", "core");

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(ManagedDir, name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
            if (Directory.Exists(BepInExCoreDir))
            {
                path = Path.Combine(BepInExCoreDir, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };

        var allTypes = new List<Type>();
        foreach (string dllPath in Directory.GetFiles(ManagedDir, "*.dll"))
        {
            try
            {
                var asm = Assembly.LoadFrom(dllPath);
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                allTypes.AddRange(types);
            }
            catch
            {
                // ignore load failures
            }
        }

        Type target = allTypes.FirstOrDefault(t => t.FullName == fullTypeName || t.Name == fullTypeName);

        string result;
        if (target == null)
        {
            result = "NOT FOUND.";
        }
        else
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            MethodInfo[] methods = target.GetMethods(flags).Where(m => m.Name == methodName).ToArray();
            result = methods.Length == 0
                ? "METHOD NOT FOUND on this type."
                : string.Join("\n", methods.Select(m => TryDecompile(target.Module.FullyQualifiedName, MetadataTokens.EntityHandle(m.MetadataToken))));
        }

        File.WriteAllText(outFile, result, Encoding.UTF8);
    }

    static readonly Dictionary<string, CSharpDecompiler> _decompilerCache = new();

    static CSharpDecompiler GetDecompiler(string dllPath)
    {
        if (_decompilerCache.TryGetValue(dllPath, out var cached))
            return cached;

        var mainModule = new PEFile(dllPath);
        string targetFramework = mainModule.DetectTargetFrameworkId();

        var resolver = new UniversalAssemblyResolver(dllPath, throwOnError: false, targetFramework);
        resolver.AddSearchDirectory(ManagedDir);
        if (Directory.Exists(BepInExCoreDir)) resolver.AddSearchDirectory(BepInExCoreDir);

        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        var decompiler = new CSharpDecompiler(mainModule, resolver, settings);
        _decompilerCache[dllPath] = decompiler;
        return decompiler;
    }

    static string TryDecompile(string dllPath, EntityHandle handle)
    {
        try
        {
            return GetDecompiler(dllPath).DecompileAsString(new[] { handle });
        }
        catch (Exception ex)
        {
            return "  <decompile failed: " + ex + ">";
        }
    }
}

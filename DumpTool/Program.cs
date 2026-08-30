using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// One-off tool: dumps type/member info from the real installed EFT client assemblies so the
// SPT 4.0.13 backport can be fixed against actual signatures instead of guesses. Not part of
// the mod - delete this project once the backport is done.
//
// Round 5 (v2): the first round-5 attempt crashed the PARENT process immediately, before writing
// anything - most likely because DecompilerHelper's static field
// (Dictionary<string, CSharpDecompiler>) forced the CLR to resolve ICSharpCode.Decompiler's types
// at Program's class-load time, even though the parent never actually decompiles anything itself
// (only the --decompile-one child process does). Moved every ICSharpCode.Decompiler-touching type
// into its own class (Decompiling, in DecompileWorker.cs) that only RunDecompileWorker references,
// so the parent process's Main() never triggers loading that assembly at all. Also swapped
// Process.GetCurrentProcess().MainModule.FileName (native process introspection, another possible
// crash source) for the simpler Assembly.GetExecutingAssembly().Location.
class Program
{
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--decompile-one")
        {
            DecompileWorker.Run(args);
            return;
        }

        try
        {
            RunParent(args);
        }
        catch (Exception ex)
        {
            // Deliberately not using ex.ToString() here - if something is bad enough for that to
            // itself throw, at least the type name/message printed via string concatenation (not
            // interpolation-driven formatting of the whole exception) has a chance of getting out.
            string message = "Fatal error in DumpTool: " + ex.GetType().FullName + ": " + ex.Message
                + "\n" + ex.StackTrace;
            Console.WriteLine(message);
            try { File.WriteAllText("crash.txt", message, Encoding.UTF8); } catch { }
        }
    }

    static void RunParent(string[] args)
    {
        string sptRoot = args.Length > 0 ? args[0] : @"E:\SPT 4.0.10";
        string managedDir = Path.Combine(sptRoot, "EscapeFromTarkov_Data", "Managed");

        if (!Directory.Exists(managedDir))
        {
            Console.WriteLine("Managed folder not found at: " + managedDir);
            Console.WriteLine("Pass the SPT root as the first argument if it's not " + sptRoot);
            return;
        }

        using var w = new StreamWriter("dump.txt", false, Encoding.UTF8);

        w.WriteLine("=== decompiling EFTInventoryClass.Equipment / .Stash getters (child-process isolated) ===");
        w.Flush();

        string exePath = Assembly.GetExecutingAssembly().Location;

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
}

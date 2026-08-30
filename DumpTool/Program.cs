using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

// One-off tool: dumps type/member info from the real installed EFT client assemblies so the
// SPT 4.0.13 backport can be fixed against actual signatures instead of guesses. Not part of
// the mod - delete this project once the backport is done.
//
// Round 6: EFT.HideoutGame doesn't have GameWorld/Profile/NextPlayerId directly - plain reflection
// dump (no decompiling needed this time) of its full member list, own + inherited, plus its base
// type chain, to find whatever replaced them.
class Program
{
    static void Main(string[] args)
    {
        string sptRoot = args.Length > 0 ? args[0] : @"E:\SPT 4.0.10";
        string managedDir = Path.Combine(sptRoot, "EscapeFromTarkov_Data", "Managed");

        if (!Directory.Exists(managedDir))
        {
            Console.WriteLine("Managed folder not found at: " + managedDir);
            Console.WriteLine("Pass the SPT root as the first argument if it's not " + sptRoot);
            return;
        }

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(managedDir, name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        var allTypes = new List<Type>();
        foreach (string dllPath in Directory.GetFiles(managedDir, "*.dll"))
        {
            string fileName = Path.GetFileNameWithoutExtension(dllPath);
            if (fileName.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                || fileName is "mscorlib" or "netstandard" or "UnityEngine")
            {
                continue;
            }

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

        using var w = new StreamWriter("dump.txt", false, Encoding.UTF8);

        Type hideoutGameType = allTypes.FirstOrDefault(t => t.FullName == "EFT.HideoutGame");
        if (hideoutGameType == null)
        {
            w.WriteLine("EFT.HideoutGame not found.");
            w.Flush();
            Console.WriteLine("Wrote dump.txt");
            return;
        }

        w.WriteLine("=== EFT.HideoutGame base type chain ===");
        for (Type bt = hideoutGameType; bt != null; bt = bt.BaseType)
        {
            w.WriteLine("  " + bt);
        }
        w.Flush();

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

        w.WriteLine();
        w.WriteLine("=== EFT.HideoutGame properties (own + inherited) ===");
        foreach (var p in SafeGet(() => hideoutGameType.GetProperties(flags)))
        {
            w.WriteLine("  prop: " + SafeTypeName(p.PropertyType) + " " + p.Name + " (declared on " + p.DeclaringType + ")");
        }
        w.Flush();

        w.WriteLine();
        w.WriteLine("=== EFT.HideoutGame fields (own + inherited) ===");
        foreach (var f in SafeGet(() => hideoutGameType.GetFields(flags)))
        {
            w.WriteLine("  field: " + SafeTypeName(f.FieldType) + " " + f.Name + " (declared on " + f.DeclaringType + ")");
        }
        w.Flush();

        w.WriteLine();
        w.WriteLine("=== EFT.HideoutGame methods matching Player/Id/World/Profile (own + inherited) ===");
        foreach (var m in SafeGet(() => hideoutGameType.GetMethods(flags).Where(m => !m.IsSpecialName).ToArray()))
        {
            if (m.Name.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0
                || m.Name.IndexOf("World", StringComparison.OrdinalIgnoreCase) >= 0
                || m.Name.IndexOf("Profile", StringComparison.OrdinalIgnoreCase) >= 0
                || m.Name.IndexOf("Id", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                w.WriteLine("  method: " + m + " (declared on " + m.DeclaringType + ")");
            }
        }
        w.Flush();

        Console.WriteLine("Wrote dump.txt");
    }

    static T[] SafeGet<T>(Func<T[]> get)
    {
        try { return get(); }
        catch { return Array.Empty<T>(); }
    }

    static string SafeTypeName(Type t)
    {
        if (t == null) return "null";
        try { return t.ToString(); }
        catch (Exception ex) { return "<?:" + ex.GetType().Name + ">"; }
    }
}

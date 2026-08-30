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
// Round 7: hand-converting ResourceKey to a bundle-name string (ToAssetName()/rcid/path) is
// unreliable - confirmed in-game that even ToAssetName() alone still produces backslash-mixed,
// unsuffixed strings that 404 for some resource key types (clothing/weapon mods), while working
// fine for others (character head/body). Rather than keep guessing per-key-type string formats,
// dump every method on IAssetsManager/PoolManagerClass whose name contains "Bundle" or "Load", to
// find one that accepts ResourceKey[] directly (the type the profile's own GetAllPrefabPaths
// already returns) and does the string conversion correctly internally per key - which is
// presumably what a real raid's own bot-spawning code path uses.
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
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        void DumpBundleMethods(string typeFullName)
        {
            w.WriteLine("=== " + typeFullName + " methods containing 'Bundle' or 'Load' ===");
            Type t = allTypes.FirstOrDefault(x => x.FullName == typeFullName || x.Name == typeFullName);
            if (t == null)
            {
                w.WriteLine("  not found");
                w.WriteLine();
                w.Flush();
                return;
            }

            foreach (var m in SafeGet(() => t.GetMethods(flags).Where(m => !m.IsSpecialName).ToArray()))
            {
                if (m.Name.IndexOf("Bundle", StringComparison.OrdinalIgnoreCase) >= 0
                    || m.Name.IndexOf("Load", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    w.WriteLine("  method: " + FormatMethod(m) + (m.IsPublic ? " [public]" : " [non-public]") + (m.IsStatic ? " [static]" : ""));
                }
            }
            w.WriteLine();
            w.Flush();
        }

        DumpBundleMethods("EFT.AssetsManager.IAssetsManager");
        DumpBundleMethods("PoolManagerClass");
        DumpBundleMethods("AssetsManagerSingletonClass");

        // Also search ALL loaded types for anything with a method named exactly "LoadBundlesAsync"
        // or similar, in case IAssetsManager isn't the only implementor worth checking.
        w.WriteLine("=== all types with a method containing both 'Bundle' and 'Async' ===");
        foreach (var t in allTypes)
        {
            MethodInfo[] methods;
            try
            {
                methods = t.GetMethods(flags).Where(m => !m.IsSpecialName
                    && m.Name.IndexOf("Bundle", StringComparison.OrdinalIgnoreCase) >= 0
                    && m.Name.IndexOf("Async", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            }
            catch { continue; }

            foreach (var m in methods)
            {
                w.WriteLine("  " + t.FullName + " :: " + FormatMethod(m));
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

    static string FormatMethod(MethodBase m)
    {
        string ret = "";
        if (m is MethodInfo mi)
        {
            try { ret = mi.ReturnType.ToString(); }
            catch (Exception ex) { ret = "<?:" + ex.GetType().Name + ">"; }
        }

        ParameterInfo[] parameters;
        try
        {
            parameters = m.GetParameters();
        }
        catch (Exception ex)
        {
            return $"{m.Name}(<could not get parameters: {ex.Message}>)";
        }

        var parts = new List<string>();
        foreach (var p in parameters)
        {
            try { parts.Add(p.ParameterType + " " + p.Name); }
            catch (Exception ex) { parts.Add("<?:" + ex.GetType().Name + "> " + (p.Name ?? "?")); }
        }

        return $"{ret} {m.Name}({string.Join(", ", parts)})".Trim();
    }
}

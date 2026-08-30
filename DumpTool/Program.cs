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
// Targets the 4 symbols the compiler couldn't find on 4.0.13: CorpseRagdoll, Profile.HealthInfo,
// InventoryDescriptor, ProfileDescriptor. Run with no arguments (defaults to E:\SPT 4.0.10) or
// pass the SPT root as the first argument. Writes dump.txt next to the exe.
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
                // ignore load failures - not relevant to this targeted search
            }
        }

        using var w = new StreamWriter("dump.txt", false, Encoding.UTF8);

        Type profileType = allTypes.FirstOrDefault(t => t.FullName == "EFT.Profile")
            ?? allTypes.FirstOrDefault(t => t.Name == "Profile" && t.Namespace != null && t.Namespace.StartsWith("EFT"));

        if (profileType == null)
        {
            w.WriteLine("Could not find a type named 'Profile' under an EFT.* namespace.");
        }
        else
        {
            w.WriteLine("=== " + profileType.FullName + " constructors ===");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var ctor in SafeGet(() => profileType.GetConstructors(flags)))
            {
                var pars = SafeGet(() => ctor.GetParameters());
                w.WriteLine("  ctor(" + string.Join(", ", pars.Select(p => SafeTypeName(p.ParameterType) + " " + p.Name)) + ")");

                foreach (var p in pars)
                {
                    DumpCandidateDescriptor(w, p.ParameterType, indent: "    ");
                }
            }

            w.WriteLine();
            w.WriteLine("=== " + profileType.FullName + " nested types ===");
            foreach (var nt in SafeGet(() => profileType.GetNestedTypes(flags)))
            {
                w.WriteLine("  nested: " + nt.FullName);
                DumpFields(w, nt, "    ");
            }
        }

        w.WriteLine();
        w.WriteLine("=== searching all loaded types for CorpseRagdoll's shape (fields _owner + _onRigidbodyStopped, or name containing 'Ragdoll') ===");
        foreach (var t in allTypes)
        {
            try
            {
                var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                bool hasOwner = fields.Any(f => f.Name == "_owner");
                bool hasStoppedEvent = fields.Any(f => f.Name.Contains("onRigidbodyStopped", StringComparison.OrdinalIgnoreCase));
                bool nameMatch = t.Name.Contains("Ragdoll", StringComparison.OrdinalIgnoreCase);

                if ((hasOwner && hasStoppedEvent) || nameMatch)
                {
                    w.WriteLine("  candidate: " + t.FullName + " (hasOwner=" + hasOwner + ", hasStoppedEvent=" + hasStoppedEvent + ", nameMatch=" + nameMatch + ")");
                    DumpFields(w, t, "    ");
                    var methods = SafeGet(() => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
                    foreach (var m in methods.Where(m => m.Name == "Start" || m.Name.Contains("RigidbodyStopped")))
                    {
                        w.WriteLine("    method: " + m);
                    }
                }
            }
            catch
            {
                // ignore - one bad type shouldn't stop the scan
            }
        }

        w.Flush();
        Console.WriteLine("Wrote dump.txt");
    }

    // Prints the fields of a ctor parameter type. Its own field dump surfaces any
    // "descriptor"-shaped sub-fields (e.g. ProfileDescriptor's Inventory field reveals
    // InventoryDescriptor's rename, and its Health field reveals HealthInfo's, without needing
    // to guess either separately).
    static void DumpCandidateDescriptor(StreamWriter w, Type t, string indent)
    {
        if (t == null)
        {
            return;
        }

        w.WriteLine(indent + "type: " + t.FullName);
        DumpFields(w, t, indent + "  ");
    }

    static void DumpFields(StreamWriter w, Type t, string indent)
    {
        try
        {
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var f in fields)
            {
                w.WriteLine(indent + "field: " + SafeTypeName(f.FieldType) + " " + f.Name);
            }

            var props = t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var p in props)
            {
                w.WriteLine(indent + "prop: " + SafeTypeName(p.PropertyType) + " " + p.Name);
            }
        }
        catch (Exception ex)
        {
            w.WriteLine(indent + "<error dumping fields: " + ex.Message + ">");
        }
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

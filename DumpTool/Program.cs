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
// Round 2: CompleteProfileDescriptorClass (Profile's ctor parameter, i.e. 4.1's ProfileDescriptor)
// has many more fields than the 4.1 mod set, and several of those fields are themselves
// obfuscated DTOs whose own shape we still need (EFTInventoryClass, ProfileInfoClass, the two
// nested types inside ProfileHealthClass). Recurses a few levels into "interesting" field types
// instead of hardcoding names, so this round should surface everything in one pass.
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
        var visited = new HashSet<Type>();

        Type profileType = allTypes.FirstOrDefault(t => t.FullName == "EFT.Profile")
            ?? allTypes.FirstOrDefault(t => t.Name == "Profile" && t.Namespace != null && t.Namespace.StartsWith("EFT"));

        if (profileType == null)
        {
            w.WriteLine("Could not find a type named 'Profile' under an EFT.* namespace.");
        }
        else
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            w.WriteLine("=== " + profileType.FullName + " constructors, recursed 3 levels into descriptor-shaped fields ===");
            foreach (var ctor in SafeGet(() => profileType.GetConstructors(flags)))
            {
                var pars = SafeGet(() => ctor.GetParameters());
                w.WriteLine("  ctor(" + string.Join(", ", pars.Select(p => SafeTypeName(p.ParameterType) + " " + p.Name)) + ")");

                foreach (var p in pars)
                {
                    DumpTypeDeep(w, p.ParameterType, "    ", depth: 3, visited);
                }
            }

            w.WriteLine();
            w.WriteLine("=== " + profileType.FullName + " nested types (top level only) ===");
            foreach (var nt in SafeGet(() => profileType.GetNestedTypes(flags)))
            {
                w.WriteLine("  nested: " + nt.FullName);
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
                bool hasStoppedEvent = fields.Any(f => f.Name.IndexOf("onRigidbodyStopped", StringComparison.OrdinalIgnoreCase) >= 0);
                bool nameMatch = t.Name.IndexOf("Ragdoll", StringComparison.OrdinalIgnoreCase) >= 0;

                if ((hasOwner && hasStoppedEvent) || nameMatch)
                {
                    w.WriteLine("  candidate: " + t.FullName + " (hasOwner=" + hasOwner + ", hasStoppedEvent=" + hasStoppedEvent + ", nameMatch=" + nameMatch + ")");
                    DumpTypeDeep(w, t, "    ", depth: 0, visited: new HashSet<Type>());

                    var candidateMethods = SafeGet(() => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
                    foreach (var m in candidateMethods.Where(m => m.Name == "Start" || m.Name.IndexOf("RigidbodyStopped", StringComparison.OrdinalIgnoreCase) >= 0))
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

        // PlayerBody is the strongest guess for RagdollClass's "_owner" equivalent (RagdollClass
        // has a PlayerBody_0 field, and _owner.TryGetComponent<LocalPlayer>(...) only compiles if
        // _owner's type inherits Component.TryGetComponent). Print PlayerBody's base type chain to
        // confirm it actually is a Component/MonoBehaviour.
        Type playerBodyType = allTypes.FirstOrDefault(t => t.FullName == "EFT.PlayerBody");
        if (playerBodyType != null)
        {
            w.WriteLine();
            w.WriteLine("=== EFT.PlayerBody base type chain (confirms whether it has TryGetComponent) ===");
            for (Type bt = playerBodyType; bt != null; bt = bt.BaseType)
            {
                w.WriteLine("  " + bt.FullName);
            }
        }

        w.Flush();
        Console.WriteLine("Wrote dump.txt");
    }

    // Recursively dumps ctors/fields/props of a type, following non-system field types (unwrapping
    // Dictionary<,>'s value type, List<>'s element type, Nullable<>'s underlying type, and arrays)
    // up to `depth` levels, so obfuscated DTO field types (EFTInventoryClass, ProfileInfoClass,
    // etc.) surface without needing their names guessed ahead of time. `visited` prevents infinite
    // recursion on cyclic references (e.g. Profile+Class1413.profile_0 : Profile).
    static void DumpTypeDeep(StreamWriter w, Type t, string indent, int depth, HashSet<Type> visited)
    {
        if (t == null || t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(object) || t == typeof(decimal))
        {
            return;
        }

        if (!visited.Add(t))
        {
            w.WriteLine(indent + "type: " + t.FullName + " (already dumped above)");
            return;
        }

        w.WriteLine(indent + "type: " + t.FullName);

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (var ctor in SafeGet(() => t.GetConstructors(flags)))
        {
            var pars = SafeGet(() => ctor.GetParameters());
            w.WriteLine(indent + "  ctor(" + string.Join(", ", pars.Select(p => SafeTypeName(p.ParameterType) + " " + p.Name)) + ")" + (ctor.IsPublic ? " [public]" : " [non-public]"));
        }

        var fields = SafeGet(() => t.GetFields(flags));
        foreach (var f in fields)
        {
            w.WriteLine(indent + "  field: " + SafeTypeName(f.FieldType) + " " + f.Name + (f.IsPublic ? " [public]" : " [non-public]"));
        }

        var props = SafeGet(() => t.GetProperties(flags));
        foreach (var p in props)
        {
            w.WriteLine(indent + "  prop: " + SafeTypeName(p.PropertyType) + " " + p.Name);
        }

        if (depth <= 0)
        {
            return;
        }

        var toRecurse = new List<Type>();
        foreach (var f in fields)
        {
            Type ft = f.FieldType;

            if (ft.IsArray)
            {
                ft = ft.GetElementType();
            }
            else if (ft.IsGenericType)
            {
                Type genDef = ft.GetGenericTypeDefinition();
                Type[] genArgs = ft.GetGenericArguments();
                if (genDef == typeof(Dictionary<,>)) ft = genArgs[1];
                else if (genDef == typeof(List<>)) ft = genArgs[0];
                else if (genDef == typeof(Nullable<>)) ft = genArgs[0];
                else continue;
            }

            if (ft == null || ft.IsPrimitive || ft.IsEnum || ft == typeof(string))
            {
                continue;
            }

            if (ft.Namespace != null &&
                (ft.Namespace.StartsWith("System") || ft.Namespace.StartsWith("Unity") || ft.Namespace.StartsWith("RootMotion")))
            {
                continue;
            }

            toRecurse.Add(ft);
        }

        foreach (var ft in toRecurse.Distinct())
        {
            DumpTypeDeep(w, ft, indent + "  ", depth - 1, visited);
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

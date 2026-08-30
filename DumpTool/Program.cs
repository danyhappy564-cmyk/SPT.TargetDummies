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
// Round 3: round 2's deep recursive dump got most of what we needed but appears to have hit a
// StackOverflowException partway through (dump.txt cut off mid-type, with the rest of the file -
// including the CorpseRagdoll search - missing entirely; StackOverflowException can't be caught
// and skips the StreamWriter's Dispose/Flush). Narrowed to exactly the 4 remaining unknowns, each
// its own small/safe lookup, flushing after every section so a crash anywhere only loses that one
// section instead of everything after it.
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
        const BindingFlags instanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // 1) CorpseRagdoll search + PlayerBody chain first - cheapest, and round 2 never reached
        // this because it ran after the (crashing) deep recursion.
        w.WriteLine("=== searching all loaded types for CorpseRagdoll's shape (fields _owner + _onRigidbodyStopped, or name containing 'Ragdoll') ===");
        w.Flush();
        foreach (var t in allTypes)
        {
            try
            {
                var fields = t.GetFields(instanceFlags);
                bool hasOwner = fields.Any(f => f.Name == "_owner");
                bool hasStoppedEvent = fields.Any(f => f.Name.IndexOf("onRigidbodyStopped", StringComparison.OrdinalIgnoreCase) >= 0);
                bool nameMatch = t.Name.IndexOf("Ragdoll", StringComparison.OrdinalIgnoreCase) >= 0;

                if ((hasOwner && hasStoppedEvent) || nameMatch)
                {
                    w.WriteLine("  candidate: " + t.FullName + " (hasOwner=" + hasOwner + ", hasStoppedEvent=" + hasStoppedEvent + ", nameMatch=" + nameMatch + ")");
                    foreach (var f in fields)
                    {
                        w.WriteLine("    field: " + SafeTypeName(f.FieldType) + " " + f.Name + (f.IsPublic ? " [public]" : " [non-public]"));
                    }

                    var methods = SafeGet(() => t.GetMethods(instanceFlags));
                    foreach (var m in methods.Where(m => m.Name == "Start" || m.Name.IndexOf("RigidbodyStopped", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        w.WriteLine("    method: " + m + (m.IsPublic ? " [public]" : " [non-public]"));
                    }
                    w.Flush();
                }
            }
            catch
            {
                // ignore - one bad type shouldn't stop the scan
            }
        }

        // 2) PlayerBody's base type chain - confirms it inherits Component.TryGetComponent, which
        // the CorpseRagdoll._owner.TryGetComponent<LocalPlayer>(...) call requires.
        w.WriteLine();
        w.WriteLine("=== EFT.PlayerBody base type chain ===");
        Type playerBodyType = allTypes.FirstOrDefault(t => t.FullName == "EFT.PlayerBody");
        if (playerBodyType == null)
        {
            w.WriteLine("  not found");
        }
        else
        {
            for (Type bt = playerBodyType; bt != null; bt = bt.BaseType)
            {
                w.WriteLine("  " + bt.FullName);
            }
        }
        w.Flush();

        // Rounds 1-3 already answered CorpseRagdoll/PlayerBody/MongoID/InventoryDescriptorClass -
        // removed from this round to keep output small. Round 4 targets the 3 new unknowns from
        // the latest compile errors.

        // 5) WaveInfoClass - the compile error already told us session.LoadBots wants
        // List<WaveInfoClass> instead of 4.1's List<CountTypeBotWave>; need its ctor shape.
        w.WriteLine("=== WaveInfoClass (replaces CountTypeBotWave) ===");
        Type waveInfoType = allTypes.FirstOrDefault(t => t.Name == "WaveInfoClass");
        if (waveInfoType == null)
        {
            w.WriteLine("  not found");
        }
        else
        {
            w.WriteLine("  FullName: " + waveInfoType.FullName);
            foreach (var ctor in SafeGet(() => waveInfoType.GetConstructors(instanceFlags)))
            {
                var pars = SafeGet(() => ctor.GetParameters());
                w.WriteLine("  ctor(" + string.Join(", ", pars.Select(p => SafeTypeName(p.ParameterType) + " " + p.Name)) + ")" + (ctor.IsPublic ? " [public]" : " [non-public]"));
            }
            foreach (var f in SafeGet(() => waveInfoType.GetFields(instanceFlags)))
            {
                w.WriteLine("  field: " + SafeTypeName(f.FieldType) + " " + f.Name + (f.IsPublic ? " [public]" : " [non-public]"));
            }
            foreach (var p in SafeGet(() => waveInfoType.GetProperties(instanceFlags)))
            {
                w.WriteLine("  prop: " + SafeTypeName(p.PropertyType) + " " + p.Name);
            }
        }
        w.Flush();

        // 6) EFT.InventoryLogic.EBoundItem's real enum member names - the Equipment guess was wrong.
        w.WriteLine();
        w.WriteLine("=== EFT.InventoryLogic.EBoundItem enum values ===");
        Type eBoundItemType = allTypes.FirstOrDefault(t => t.FullName == "EFT.InventoryLogic.EBoundItem");
        if (eBoundItemType == null)
        {
            w.WriteLine("  not found");
        }
        else
        {
            w.WriteLine("  " + string.Join(", ", Enum.GetNames(eBoundItemType)));
        }
        w.Flush();

        // 7) TarkovApplication.HideoutControllerAccess's actual return type members - task_0
        // doesn't exist on it (per the compile error), need whatever field/prop replaces it.
        w.WriteLine();
        w.WriteLine("=== TarkovApplication.HideoutControllerAccess return type members ===");
        Type tarkovAppType = allTypes.FirstOrDefault(t => t.Name == "TarkovApplication");
        if (tarkovAppType == null)
        {
            w.WriteLine("  TarkovApplication not found");
        }
        else
        {
            PropertyInfo hideoutControllerAccessProp = tarkovAppType.GetProperty("HideoutControllerAccess", instanceFlags | BindingFlags.FlattenHierarchy);
            if (hideoutControllerAccessProp == null)
            {
                w.WriteLine("  HideoutControllerAccess property not found on TarkovApplication");
            }
            else
            {
                Type accessType = hideoutControllerAccessProp.PropertyType;
                w.WriteLine("  HideoutControllerAccess : " + accessType.FullName);
                foreach (var f in SafeGet(() => accessType.GetFields(instanceFlags)))
                {
                    w.WriteLine("    field: " + SafeTypeName(f.FieldType) + " " + f.Name + (f.IsPublic ? " [public]" : " [non-public]"));
                }
                foreach (var p in SafeGet(() => accessType.GetProperties(instanceFlags)))
                {
                    w.WriteLine("    prop: " + SafeTypeName(p.PropertyType) + " " + p.Name);
                }
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

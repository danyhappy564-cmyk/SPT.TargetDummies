using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

// Everything that touches ICSharpCode.Decompiler lives here, in its own class, so that Program's
// normal (non-worker) run path never references this class and never forces the CLR to resolve
// ICSharpCode.Decompiler's types. Only reached via --decompile-one, i.e. only in the isolated
// child process Program.RunParent spawns.
static class DecompileWorker
{
    static string _managedDir;
    static string _bepInExCoreDir;

    // args: --decompile-one <sptRoot> <outFile> <fullTypeName> <methodName>
    public static void Run(string[] args)
    {
        string sptRoot = args[1];
        string outFile = args[2];
        string fullTypeName = args[3];
        string methodName = args[4];

        _managedDir = Path.Combine(sptRoot, "EscapeFromTarkov_Data", "Managed");
        _bepInExCoreDir = Path.Combine(sptRoot, "BepInEx", "core");

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(_managedDir, name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
            if (Directory.Exists(_bepInExCoreDir))
            {
                path = Path.Combine(_bepInExCoreDir, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };

        var allTypes = new List<Type>();
        foreach (string dllPath in Directory.GetFiles(_managedDir, "*.dll"))
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

    static Dictionary<string, CSharpDecompiler> _decompilerCache;

    static CSharpDecompiler GetDecompiler(string dllPath)
    {
        _decompilerCache ??= new Dictionary<string, CSharpDecompiler>();

        if (_decompilerCache.TryGetValue(dllPath, out var cached))
            return cached;

        var mainModule = new PEFile(dllPath);
        string targetFramework = mainModule.DetectTargetFrameworkId();

        var resolver = new UniversalAssemblyResolver(dllPath, throwOnError: false, targetFramework);
        resolver.AddSearchDirectory(_managedDir);
        if (Directory.Exists(_bepInExCoreDir)) resolver.AddSearchDirectory(_bepInExCoreDir);

        var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
        var decompiler = new CSharpDecompiler(mainModule, resolver, settings);
        _decompilerCache[dllPath] = decompiler;
        return decompiler;
    }

    static string TryDecompile(string dllPath, System.Reflection.Metadata.EntityHandle handle)
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

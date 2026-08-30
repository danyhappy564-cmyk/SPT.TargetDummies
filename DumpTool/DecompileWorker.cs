using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

// Round 5 v3: ICSharpCode.Decompiler's full C# reconstruction needs the whole type system
// resolved, and that failed trying to load Queue<T> out of the game's own netstandard.dll facade
// (a type-forwarding assembly whose forwarding targets weren't all in the resolver's search path).
// Rather than fight assembly resolution further, this reads the raw IL bytes directly via plain
// System.Reflection (MethodBody.GetILAsByteArray + Module.ResolveField/Method/etc.) - no NuGet
// package, no whole-assembly type system needed. For a simple property getter this is just as
// readable as decompiled C#, and it's what we actually need: which field it reads, and what
// integer literal (the EBoundItem enum's underlying int) it uses.
static class DecompileWorker
{
    // args: --decompile-one <sptRoot> <outFile> <fullTypeName> <methodName>
    public static void Run(string[] args)
    {
        string sptRoot = args[1];
        string outFile = args[2];
        string fullTypeName = args[3];
        string methodName = args[4];

        string managedDir = Path.Combine(sptRoot, "EscapeFromTarkov_Data", "Managed");
        string bepInExCoreDir = Path.Combine(sptRoot, "BepInEx", "core");

        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string name = new AssemblyName(e.Name).Name;
            string path = Path.Combine(managedDir, name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
            if (Directory.Exists(bepInExCoreDir))
            {
                path = Path.Combine(bepInExCoreDir, name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };

        var allTypes = new List<Type>();
        foreach (string dllPath in Directory.GetFiles(managedDir, "*.dll"))
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

        using var w = new StringWriter();
        if (target == null)
        {
            w.WriteLine("NOT FOUND.");
        }
        else
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            MethodInfo[] methods = target.GetMethods(flags).Where(m => m.Name == methodName).ToArray();
            if (methods.Length == 0)
            {
                w.WriteLine("METHOD NOT FOUND on this type.");
            }
            else
            {
                foreach (var m in methods)
                {
                    w.WriteLine("=== " + m + " ===");
                    PrintRawIL(w, m);
                    w.WriteLine();
                }
            }
        }

        File.WriteAllText(outFile, w.ToString(), Encoding.UTF8);
    }

    static readonly Lazy<Dictionary<short, OpCode>> OpCodeMap = new(() =>
    {
        var map = new Dictionary<short, OpCode>();
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.FieldType == typeof(OpCode))
            {
                var code = (OpCode)field.GetValue(null);
                map[code.Value] = code;
            }
        }
        return map;
    });

    static void PrintRawIL(TextWriter w, MethodInfo method)
    {
        MethodBody body;
        try { body = method.GetMethodBody(); }
        catch (Exception ex) { w.WriteLine("  <GetMethodBody failed: " + ex.Message + ">"); return; }

        if (body == null)
        {
            w.WriteLine("  <no method body (abstract/extern?)>");
            return;
        }

        byte[] il;
        try { il = body.GetILAsByteArray(); }
        catch (Exception ex) { w.WriteLine("  <GetILAsByteArray failed: " + ex.Message + ">"); return; }

        if (il == null)
        {
            w.WriteLine("  <IL byte array is null>");
            return;
        }

        Module module = method.Module;
        Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType
            ? method.DeclaringType.GetGenericArguments() : null;
        Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;

        Dictionary<short, OpCode> opcodeMap = OpCodeMap.Value;
        int pos = 0;
        while (pos < il.Length)
        {
            int startPos = pos;
            short opcodeValue = il[pos];
            pos++;
            if (opcodeValue == 0xFE)
            {
                opcodeValue = (short)(0xFE00 | il[pos]);
                pos++;
            }

            if (!opcodeMap.TryGetValue(opcodeValue, out OpCode opcode))
            {
                w.WriteLine($"  IL_{startPos:X4}: <unknown opcode 0x{opcodeValue:X}>");
                break;
            }

            string operand = "";
            try
            {
                switch (opcode.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        operand = il[pos].ToString(); pos += 1; break;
                    case OperandType.InlineVar:
                        operand = BitConverter.ToInt16(il, pos).ToString(); pos += 2; break;
                    case OperandType.InlineI:
                        operand = BitConverter.ToInt32(il, pos).ToString(); pos += 4; break;
                    case OperandType.InlineI8:
                        operand = BitConverter.ToInt64(il, pos).ToString(); pos += 8; break;
                    case OperandType.ShortInlineR:
                        operand = BitConverter.ToSingle(il, pos).ToString(); pos += 4; break;
                    case OperandType.InlineR:
                        operand = BitConverter.ToDouble(il, pos).ToString(); pos += 8; break;
                    case OperandType.ShortInlineBrTarget:
                        operand = "IL_" + (startPos + 2 + (sbyte)il[pos]).ToString("X4"); pos += 1; break;
                    case OperandType.InlineBrTarget:
                        operand = "IL_" + (startPos + 5 + BitConverter.ToInt32(il, pos)).ToString("X4"); pos += 4; break;
                    case OperandType.InlineField:
                        {
                            int token = BitConverter.ToInt32(il, pos); pos += 4;
                            try { FieldInfo f = module.ResolveField(token, typeArgs, methodArgs); operand = f.DeclaringType + "::" + f.Name; }
                            catch (Exception ex) { operand = $"<field token 0x{token:X}: {ex.Message}>"; }
                            break;
                        }
                    case OperandType.InlineMethod:
                        {
                            int token = BitConverter.ToInt32(il, pos); pos += 4;
                            try { MethodBase m = module.ResolveMethod(token, typeArgs, methodArgs); operand = m.DeclaringType + "::" + m; }
                            catch (Exception ex) { operand = $"<method token 0x{token:X}: {ex.Message}>"; }
                            break;
                        }
                    case OperandType.InlineType:
                        {
                            int token = BitConverter.ToInt32(il, pos); pos += 4;
                            try { Type t = module.ResolveType(token, typeArgs, methodArgs); operand = t.ToString(); }
                            catch (Exception ex) { operand = $"<type token 0x{token:X}: {ex.Message}>"; }
                            break;
                        }
                    case OperandType.InlineTok:
                        {
                            int token = BitConverter.ToInt32(il, pos); pos += 4;
                            try { MemberInfo member = module.ResolveMember(token, typeArgs, methodArgs); operand = member.ToString(); }
                            catch (Exception ex) { operand = $"<member token 0x{token:X}: {ex.Message}>"; }
                            break;
                        }
                    case OperandType.InlineString:
                        {
                            int token = BitConverter.ToInt32(il, pos); pos += 4;
                            try { operand = "\"" + module.ResolveString(token) + "\""; }
                            catch (Exception ex) { operand = $"<string token 0x{token:X}: {ex.Message}>"; }
                            break;
                        }
                    case OperandType.InlineSig:
                        {
                            int token = BitConverter.ToInt32(il, pos); pos += 4;
                            operand = $"<signature token 0x{token:X}>";
                            break;
                        }
                    case OperandType.InlineSwitch:
                        {
                            int count = BitConverter.ToInt32(il, pos); pos += 4;
                            var targets = new List<string>();
                            int baseOffset = pos + count * 4;
                            for (int i = 0; i < count; i++)
                            {
                                int delta = BitConverter.ToInt32(il, pos); pos += 4;
                                targets.Add("IL_" + (baseOffset + delta).ToString("X4"));
                            }
                            operand = "[" + string.Join(", ", targets) + "]";
                            break;
                        }
                    default:
                        operand = "<unhandled operand type " + opcode.OperandType + ">";
                        break;
                }
            }
            catch (Exception ex)
            {
                w.WriteLine($"  IL_{startPos:X4}: {opcode.Name} <operand read failed: {ex.Message}>");
                break;
            }

            w.WriteLine($"  IL_{startPos:X4}: {opcode.Name} {operand}".TrimEnd());
        }
    }
}

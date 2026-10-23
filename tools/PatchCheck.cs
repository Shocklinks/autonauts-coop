// AutonautsCoop - tools/PatchCheck.cs - checks that every Harmony patch in the mod finds its game method
// Version 0.12.19 - 2026-10-23 - Created with Claude
// One bad patch target (missing or ambiguous) makes PatchAll stop and the whole mod does nothing in the game.
// Run: mono PatchCheck.exe <AutonautsCoop.dll> <game Managed dir> <BepInEx core dir>   (exit 1 on any problem)
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

static class PatchCheck
{
    static int Main(string[] a)
    {
        string[] dirs = { a[1], a[2], Path.GetDirectoryName(Path.GetFullPath(a[0])) };
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var n = new AssemblyName(e.Name).Name + ".dll";
            foreach (var d in dirs) { var p = Path.Combine(d, n); if (File.Exists(p)) return Assembly.LoadFrom(p); }
            return null;
        };
        var asm = Assembly.LoadFrom(a[0]);
        int ok = 0, bad = 0;
        Type[] types;
        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
        foreach (var t in types)
        {
            var attrs = HarmonyMethodExtensions.GetFromType(t);
            if (attrs == null || attrs.Count == 0) continue;
            var info = HarmonyMethod.Merge(attrs);
            try
            {
                var tm = t.GetMethod("TargetMethods", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var tm1 = t.GetMethod("TargetMethod", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (tm != null)
                {
                    var list = ((System.Collections.IEnumerable)tm.Invoke(null, null)).Cast<MethodBase>().ToList();
                    if (list.Count == 0 || list.Any(m => m == null)) { Console.WriteLine("BAD  " + t.Name + ": TargetMethods gave none or null"); bad++; } else ok++;
                    continue;
                }
                if (tm1 != null) { if (tm1.Invoke(null, null) == null) { Console.WriteLine("BAD  " + t.Name + ": TargetMethod null"); bad++; } else ok++; continue; }
                if (info.declaringType == null) continue;   // attribute-only helper class
                MethodBase m;
                if (info.methodType == MethodType.Constructor) m = AccessTools.DeclaredConstructor(info.declaringType, info.argumentTypes);
                else if (info.methodType == MethodType.Getter) m = AccessTools.DeclaredPropertyGetter(info.declaringType, info.methodName);
                else if (info.methodType == MethodType.Setter) m = AccessTools.DeclaredPropertySetter(info.declaringType, info.methodName);
                else
                {
                    m = AccessTools.DeclaredMethod(info.declaringType, info.methodName, info.argumentTypes);
                    if (m == null) m = AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes);
                }
                if (m == null) { Console.WriteLine("BAD  " + t.Name + ": no " + info.declaringType.Name + "." + info.methodName); bad++; }
                else ok++;
            }
            catch (Exception e) { Console.WriteLine("BAD  " + t.Name + " (" + info.declaringType?.Name + "." + info.methodName + "): " + (e.InnerException ?? e).GetType().Name + " " + (e.InnerException ?? e).Message); bad++; }
        }
        Console.WriteLine("patch check: " + ok + " ok, " + bad + " bad");
        return bad > 0 ? 1 : 0;
    }
}

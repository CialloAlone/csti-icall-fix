using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;

namespace CstiICallFix;

/// <summary>
/// 缓存破除器。
///
/// 问题：Il2CppInterop 的托管包装在**更早的时刻**（MelonLoader 启动期）就已把
/// “ICall 解析失败”缓存成一个 missing-icall 委托；之后无论我们后来往引擎 ICall 表里写什么，
/// 那个包装都不再重新解析 —— 于是我们的 shim 永远收不到调用。
///
/// 做法：等 shim 就绪（模板表已建 + 已写入引擎 ICall 表）后，
///   ① 清掉 Il2CppInterop 内部可能存在的 missing-icall 缓存
///   ② 把 UnityEngine.ScriptableObject 代理里所有与目标方法相关的静态缓存字段重置为 0，
///      让包装下次调用时**重新解析**（此时引擎表已是我们的实现）
/// </summary>
public static class CacheBreaker
{
    public static void Run(string icallName, string methodNameFragment)
    {
        try
        {
            // ① 只读检查 Il2CppInterop 内部结构（★绝不修改：曾经误清空 ourImagesMap 导致崩溃）
            var tIl2cpp = typeof(Il2CppInterop.Runtime.IL2CPP);
            foreach (var f in tIl2cpp.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.Name.IndexOf("Map", StringComparison.OrdinalIgnoreCase) < 0 &&
                    f.Name.IndexOf("Cache", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    var v = f.GetValue(null);
                    var cnt = (v is System.Collections.ICollection col) ? col.Count.ToString() : "?";
                    MelonLoader.MelonLogger.Msg("[CACHE] 只读: IL2CPP." + f.Name + " (" + f.FieldType.Name + ") 条目=" + cnt);
                }
                catch { }
            }

            // ② 重置代理类型里与该 ICall 相关的静态缓存字段
            Type proxy = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { proxy = asm.GetType("UnityEngine.ScriptableObject"); } catch { }
                if (proxy != null) break;
            }
            if (proxy == null) { MelonLogger.Warning("[CACHE] 找不到 UnityEngine.ScriptableObject 代理"); return; }

            int reset = 0;
            foreach (var f in proxy.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.Name.IndexOf(methodNameFragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    var before = f.GetValue(null);
                    if (f.FieldType == typeof(IntPtr))
                    {
                        f.SetValue(null, IntPtr.Zero);   // 促使其重新解析
                        reset++;
                        MelonLogger.Msg("[CACHE] 重置字段 " + f.Name + " (原值 " + before + ")");
                    }
                }
                catch (Exception e) { MelonLogger.Warning("[CACHE] 重置 " + f.Name + " 失败: " + e.Message); }
            }
            MelonLogger.Msg("[CACHE] ScriptableObject 代理字段重置数 = " + reset + "（目标: " + methodNameFragment + "）");

            // ③ 重新解析一次，确认引擎表现在返回我们的实现
            var p = Il2CppInterop.Runtime.IL2CPP.il2cpp_resolve_icall(icallName);
            MelonLogger.Msg("[CACHE] 重新解析 " + icallName + " -> 0x" + p.ToInt64().ToString("X"));
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[CACHE] 失败: " + e.GetType().Name + " " + e.Message);
        }
    }
}

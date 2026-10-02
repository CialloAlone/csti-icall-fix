using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using MelonLoader;

[assembly: MelonInfo(typeof(CstiICallFix.ICallFixMod), "CstiICallFix", "2.0.0", "dsh")]
[assembly: MelonGame(null, null)]

namespace CstiICallFix;

/// <summary>
/// 本质解法：给 il2cpp_resolve_icall 挂原生 detour，接管「未注册 ICall」。
///
/// 背景：本游戏是裁剪版引擎，libil2cpp.so 的 ICall 注册表缺一大片；MelonLoader 的 Mono 分支
/// 碰到未注册 ICall 会 abort 进程，于是每个功能都要绕（绕不完）。
///
/// 做法（不改游戏、不换引擎）：
///   1. 用 MelonLoader 自带的 libdobby.so（DobbyHook 已确认导出）hook il2cpp_resolve_icall
///   2. detour 先调原实现：非 0 → 原样返回，行为完全不变
///   3. 返回 0（未注册）→ 查我们的 shim 表：
///        · 已实现的名字 → 返回真实现
///        · 其余        → 返回安全桩（返回 0，绝不 abort）
///      同时把拦到的名字登记到 icall_intercepted.txt（这就是权威的“真正缺失”清单）
/// </summary>
public class ICallFixMod : MelonMod
{
    internal static readonly HashSet<string> Intercepted = new HashSet<string>();
    private static string _reportPath;

    // ---- 只读取证扩展：记录**成功解析**的 ICall（名字去重、限量）----
    // 目的：拿到「这个裁剪版引擎到底注册了哪些 ICall」的权威名单。
    // 性能考量：il2cpp 生成的 wrapper 会把解析结果缓存进 static 变量，所以解析调用次数
    // 有上界（≈ 被引用的 icall 数量，实测 1000+），这里再用 namePtr 去重，只有首次才 marshal 字符串。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, byte> SeenOk =
        new System.Collections.Concurrent.ConcurrentDictionary<IntPtr, byte>();
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> Resolved =
        new System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr>();

    /// <summary>仅记录，不改行为。返回 true 表示这次是新的名字。</summary>
    internal static void NoteResolved(IntPtr namePtr, IntPtr fp)
    {
        try
        {
            if (SeenOk.Count > 20000) return;
            if (!SeenOk.TryAdd(namePtr, 0)) return;
            var n = Marshal.PtrToStringAnsi(namePtr);
            if (string.IsNullOrEmpty(n)) return;
            if (Resolved.Count > 20000) return;
            Resolved.TryAdd(n, fp);
            if (n.IndexOf("Audio", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("Sound", StringComparison.Ordinal) >= 0 ||
                n.IndexOf("Microphone", StringComparison.Ordinal) >= 0)
                MelonLoader.MelonLogger.Msg("[ICALLFIX] 已注册(命中) " + n + " = 0x" + fp.ToInt64().ToString("X"));
        }
        catch { }
    }

    public static void DumpResolved()
    {
        try
        {
            var dir = Path.Combine("/sdcard/MelonLoader", "com.winterspringgames.survivaljourney");
            Directory.CreateDirectory(dir);
            var all = new List<string>(Resolved.Keys);
            all.Sort(StringComparer.Ordinal);
            var sbAll = new StringBuilder();
            sbAll.AppendLine("# 本次运行中**成功解析**的 ICall 去重清单: " + all.Count);
            foreach (var n in all) sbAll.AppendLine(n);
            File.WriteAllText(Path.Combine(dir, "icall_resolved_all.txt"), sbAll.ToString());

            var sbA = new StringBuilder();
            var audio = all.FindAll(n => n.IndexOf("Audio", StringComparison.Ordinal) >= 0
                                      || n.IndexOf("Sound", StringComparison.Ordinal) >= 0
                                      || n.IndexOf("Microphone", StringComparison.Ordinal) >= 0);
            sbA.AppendLine("# 音频相关：成功解析的 ICall (" + audio.Count + ")");
            foreach (var n in audio) sbA.AppendLine(n + "  = 0x" + Resolved[n].ToInt64().ToString("X"));
            sbA.AppendLine();
            sbA.AppendLine("# 对照：本轮被拦下（未注册）的音频相关 ICall");
            foreach (var n in Intercepted)
                if (n.IndexOf("Audio", StringComparison.Ordinal) >= 0 || n.IndexOf("Sound", StringComparison.Ordinal) >= 0)
                    sbA.AppendLine(n);
            File.WriteAllText(Path.Combine(dir, "icall_resolved_audio.txt"), sbA.ToString());
            MelonLoader.MelonLogger.Msg("[ICALLFIX] 解析清单已写入: 成功 " + all.Count + " 条（音频 " + audio.Count + "）");
        }
        catch (Exception e)
        {
            MelonLoader.MelonLogger.Warning("[ICALLFIX] DumpResolved 失败: " + e.Message);
        }
    }

    public override void OnInitializeMelon()
    {
        try
        {
            _reportPath = Path.Combine("/sdcard/MelonLoader", "com.winterspringgames.survivaljourney", "icall_intercepted.txt");
            RealShims.RegisterAll();                       // 注册我们实现的真 shim
            bool ok = ResolverHook.Install();
            LoggerInstance.Msg("[ICALLFIX] resolver detour 安装 " + (ok ? "成功" : "失败")
                               + "  已实现 shim 数 = " + RealShims.Count);
            // 真机一次性音频取证（三重触发，见 AudioProbe）
            AudioProbe.StartBackground(20000);
            LoggerInstance.Msg("[ICALLFIX] AudioProbe 后台线程已启动（20s 后跑）");
        }
        catch (Exception e)
        {
            LoggerInstance.Error("[ICALLFIX] 初始化失败: " + e);
        }
    }

    public override void OnApplicationQuit()
    {
        AudioProbe.Run("OnApplicationQuit");
        DumpIntercepted();
        DumpResolved();
    }

    private float _nextDump;
    private float _probeAt = -1f;

    public override void OnUpdate()
    {
        // OnUpdate 泵可能不可用，这里只在可用时周期性落盘；不可用时由 OnApplicationQuit 兜底
        float t = UnityEngine.Time.realtimeSinceStartup;
        if (_probeAt < 0f) _probeAt = t + 15f;           // AudioProbe 的第二重触发
        if (t >= _probeAt) { _probeAt = float.MaxValue; AudioProbe.Run("OnUpdate"); }
        if (t - _nextDump < 5f) return;
        _nextDump = t;
        DumpIntercepted();
        DumpResolved();
    }

    internal static void DumpIntercepted()
    {
        try
        {
            if (_reportPath == null) return;
            var sb = new StringBuilder();
            sb.AppendLine("# 被拦截（引擎未注册）的 ICall 数量: " + Intercepted.Count);
            foreach (var n in Intercepted) sb.AppendLine(n);
            File.WriteAllText(_reportPath, sb.ToString());
            MelonLoader.MelonLogger.Msg("[ICALLFIX] 拦截清单已写入 " + _reportPath + "（" + Intercepted.Count + " 条）");
        }
        catch { }
    }
}

/// <summary>il2cpp_resolve_icall 的原生 detour。</summary>
public static class ResolverHook
{
    [DllImport("libdobby.so", EntryPoint = "DobbyHook")]
    private static extern int DobbyHook(IntPtr target, IntPtr replace, out IntPtr origin);

    private delegate IntPtr ResolveDelegate(IntPtr name);
    private static ResolveDelegate _original;
    private static IntPtr _originalPtr;
    private static bool _installed;

    /// <summary>阶段 2/3 开关：true 时对缺失项返回我们的 shim；阶段 1 保持 false（只测量）。</summary>
    public static bool ApplyShims = true;   // 只对 RealShims 里已实现的名字生效

    public static bool Install()
    {
        if (_installed) return true;
        try
        {
            var lib = System.Runtime.InteropServices.NativeLibrary.Load("libil2cpp.so");
            IntPtr target = System.Runtime.InteropServices.NativeLibrary.GetExport(lib, "il2cpp_resolve_icall");
            if (target == IntPtr.Zero) { MelonLogger.Warning("[ICALLFIX] 找不到 il2cpp_resolve_icall"); return false; }

            unsafe
            {
                delegate* unmanaged[Cdecl]<IntPtr, IntPtr> hook = &ResolveHook;
                int rc = DobbyHook(target, (IntPtr)hook, out _originalPtr);
                if (rc != 0) { MelonLogger.Warning("[ICALLFIX] DobbyHook 返回 " + rc); return false; }
            }

            _original = Marshal.GetDelegateForFunctionPointer<ResolveDelegate>(_originalPtr);
            _installed = true;
            return true;
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[ICALLFIX] 安装异常: " + e.GetType().Name + " " + e.Message);
            return false;
        }
    }

    /// <summary>detour 本体：先问原实现，未注册才接管。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr ResolveHook(IntPtr namePtr)
    {
        try
        {
            IntPtr result = _original != null ? _original(namePtr) : IntPtr.Zero;
            if (result != IntPtr.Zero)
            {
                ICallFixMod.NoteResolved(namePtr, result);   // 只记录，不改行为
                return result;
            }

            var name = Marshal.PtrToStringAnsi(namePtr);
            if (!string.IsNullOrEmpty(name) && ICallFixMod.Intercepted.Add(name))
            {
                MelonLoader.MelonLogger.Warning("[ICALLFIX] 引擎未注册 ICall（已接管）: " + name);
            }
            if (name == "UnityEngine.ScriptableObject::CreateScriptableObjectInstanceFromName")
            {
                RealShims.NoteSoCreateAsked();     // 记录“创建请求来过”，供自检
            }
            // ★ 只有「我们此刻真的能服务」的名字才返回 shim：
            //   一旦返回非 0 指针，运行时就会把它缓存为“已解析”，之后无法退回；
            //   若那时我们内部还没准备好（例如模板表未建），就会返回 null 并让引擎/托管类型进入坏状态。
            if (ApplyShims && RealShims.CanServe(name) && RealShims.TryGet(name, out var fp)) return fp;
            return IntPtr.Zero;
        }
        catch
        {
            return Shims.StubPointer;
        }
    }
}

/// <summary>我们的 shim 表：重要项给真实现，其余给安全桩。</summary>
public static class Shims
{
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IntPtr Stub(IntPtr a, IntPtr b, IntPtr c, IntPtr d) => IntPtr.Zero;

    private static IntPtr _stubPtr;

    public static IntPtr StubPointer
    {
        get
        {
            if (_stubPtr == IntPtr.Zero)
            {
                unsafe { _stubPtr = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&Stub; }
            }
            return _stubPtr;
        }
    }

    public static IntPtr Resolve(string name)
    {
        if (string.IsNullOrEmpty(name)) return StubPointer;
        if (RealShims.TryGet(name, out var fp)) return fp;
        return StubPointer;
    }
}

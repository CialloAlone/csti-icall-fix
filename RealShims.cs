using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace CstiICallFix;

/// <summary>
/// 真 shim 表（阶段 3）。
///
/// 重要设计：
///   · shim 用 **委托**（Marshal.GetFunctionPointerForDelegate）暴露为原生函数指针，
///     而不是 [UnmanagedCallersOnly] —— 因为委托 thunk 允许回调托管代码，而 UnmanagedCallersOnly 不允许。
///   · 策略：**只对我们真正实现过的名字返回 shim**；其余未注册项返回 0（与原生"未解析"完全一致）——
///     这样绝不会像上一版那样把 Component::GetComponent 之类核心项也打成桩而搞崩游戏。
/// </summary>
public static class RealShims
{
    private static readonly Dictionary<string, IntPtr> Map = new Dictionary<string, IntPtr>();
    private static readonly List<Delegate> KeepAlive = new List<Delegate>();

    // ---------- 原生侧工具 ----------
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr IcallPtr2(IntPtr a, IntPtr b);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr IcallPtr1(IntPtr a);

    private static IntPtr Resolve(string name)
    {
        try { return IL2CPP.il2cpp_resolve_icall(name); } catch { return IntPtr.Zero; }
    }

    /// <summary>把 Il2CppString* 转成托管 string。IL2CPP 字符串：klass(8)+monitor(8)+length(4)+chars@0x14。</summary>
    internal static string Il2CppStringToManaged(IntPtr str)
    {
        if (str == IntPtr.Zero) return null;
        try
        {
            int len = Marshal.ReadInt32(str, 0x10);
            if (len <= 0 || len > 4096) return null;
            return Marshal.PtrToStringUni(str + 0x14, len);
        }
        catch { return null; }
    }

    public static int Count => Map.Count;

    public static bool TryGet(string name, out IntPtr fp) => Map.TryGetValue(name, out fp);

    /// <summary>此刻能否真正服务这个名字（不能就不要暴露 shim，否则会被缓存成“已解析”而无法退回）。</summary>
    /// <summary>此刻能否服务该名字（ScriptableObject 创建要求模板表已建，否则会在类型初始化期间被调用并返回 null）。</summary>
    public static bool CanServe(string name)
    {
        if (!Map.ContainsKey(name)) return false;
        if (name == "UnityEngine.ScriptableObject::CreateScriptableObjectInstanceFromName") return TemplatesReady;
        return true;
    }

    /// <summary>ScriptableObject 创建被请求的次数（自检用）。</summary>
    public static int SoCreateAsked { get; private set; }
    public static void NoteSoCreateAsked() { SoCreateAsked++; }

    [DllImport("libil2cpp.so", EntryPoint = "il2cpp_add_internal_call")]
    private static extern void il2cpp_add_internal_call(IntPtr name, IntPtr method);

    public static void Register(string name, Delegate d)
    {
        KeepAlive.Add(d);
        var fp = Marshal.GetFunctionPointerForDelegate(d);
        Map[name] = fp;

        MelonLogger.Msg("[SHIM] 已登记真实现（未写入引擎表，等模板表就绪后再写）: " + name);
    }

    // ================== shim #1：ScriptableObject 创建 ==================
    // 原生签名（Unity）：ScriptableObject* CreateScriptableObjectInstanceFromName(Il2CppString* className, MethodInfo*)
    // 引擎里这条被裁掉了，无法走原厂路径；这里用「克隆一个同类型的现成实例」代替新建：
    // Unity 的 ScriptableObject 必须由原生侧构造，而 Object::Internal_CloneSingle 是**已注册**的 ICall，
    // 克隆出来的对象带完整原生部分，之后 MiniLoader 会用 mod 数据回填字段。
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateSoByNameDelegate(IntPtr className, IntPtr methodInfo);


    private static IntPtr CreateSoByName(IntPtr classNamePtr, IntPtr methodInfo)
    {
        // ★ 重要：本函数会在 ScriptableObject 静态构造期间被调用 —— 绝不能碰任何托管引擎类型，
        //   否则会重入初始化并抛异常。这里只做：读字符串 -> 查预建表 -> 调原生 clone。
        try
        {
            var name = Il2CppStringToManaged(classNamePtr);
            if (string.IsNullOrEmpty(name)) return IntPtr.Zero;

            if (!TemplatesReady) return IntPtr.Zero;          // 表没准备好就保持原生失败行为
            if (!TemplateMap.TryGetValue(name, out var tmpl) || tmpl == IntPtr.Zero)
            {
                if (_templateMissLogged.Add(name))
                    MelonLogger.Warning("[SHIM] 没有可克隆的模板实例: " + name);
                return IntPtr.Zero;
            }

            if (_cloneFnPtr == IntPtr.Zero) _cloneFnPtr = Resolve("UnityEngine.Object::Internal_CloneSingle");
            if (_cloneFnPtr == IntPtr.Zero) return IntPtr.Zero;

            var clone = Marshal.GetDelegateForFunctionPointer<IcallPtr1>(_cloneFnPtr)(tmpl);
            if (clone != IntPtr.Zero && _cloneLogged.Add(name))
                MelonLogger.Msg("[SHIM] 克隆创建 " + name + " -> 0x" + clone.ToInt64().ToString("X"));
            return clone;
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[SHIM] CreateSoByName 异常: " + e.GetType().Name + " " + e.Message);
            return IntPtr.Zero;
        }
    }

    private static readonly HashSet<string> _templateMissLogged = new HashSet<string>();
    private static readonly HashSet<string> _cloneLogged = new HashSet<string>();

    /// <summary>类型名 → 模板对象指针（由外部在游戏就绪后一次性预建）。</summary>
    private static readonly Dictionary<string, IntPtr> TemplateMap = new Dictionary<string, IntPtr>();
    private static IntPtr _cloneFnPtr;
    public static bool TemplatesReady { get; private set; }

    /// <summary>
    /// 预建模板表。★ 必须在「游戏已就绪、且不在 ScriptableObject 静态构造期间」调用，
    /// 由 MiniLoader 的每帧泵在资源注册表导入之后调用（通过反射，避免硬依赖）。
    /// </summary>
    public static void PrimeTemplates()
    {
        if (TemplatesReady) return;
        try
        {
            var dict = UniqueIDScriptable.AllUniqueObjects;
            if (dict == null || dict.Count == 0) return;

            int n = 0, skipped = 0;
            foreach (var kv in dict)
            {
                var o = kv.Value;
                if (o == null) { skipped++; continue; }

                // ★ 纯原生取类名：不碰任何托管引擎类型，避免重入/初始化问题
                var objPtr = IL2CPP.Il2CppObjectBaseToPtr(o);
                if (objPtr == IntPtr.Zero) { skipped++; continue; }
                var cls = IL2CPP.il2cpp_object_get_class(objPtr);
                if (cls == IntPtr.Zero) { skipped++; continue; }
                var namePtr = IL2CPP.il2cpp_class_get_name(cls);
                var name = namePtr == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(namePtr);
                if (string.IsNullOrEmpty(name)) { skipped++; continue; }

                if (!TemplateMap.ContainsKey(name)) { TemplateMap[name] = objPtr; n++; }
            }

            TemplatesReady = n > 0;
            MelonLogger.Msg("[SHIM] 模板表已建: " + n + " 个类型（跳过 " + skipped + "）");
            if (TemplatesReady)
            {
                PublishToEngine();                    // ★ 只有此刻才把 shim 写进引擎 ICall 表
                CacheBreaker.Run("UnityEngine.ScriptableObject::CreateScriptableObjectInstanceFromName",
                                 "CreateScriptableObjectInstanceFromName");   // 并破除已缓存的 missing-icall
            }
            var sample = new List<string>(TemplateMap.Keys);
            sample.Sort();
            MelonLogger.Msg("[SHIM] 模板类型抽样: " + string.Join(", ", sample.GetRange(0, Math.Min(10, sample.Count))));
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[SHIM] PrimeTemplates 失败: " + e.GetType().Name + " " + e.Message);
        }
    }

    /// <summary>
    /// 把已登记的 shim 写进引擎 ICall 表。
    /// ★ 时序要求：必须等 ScriptableObject 类型初始化完成、且模板表建好之后再写，
    ///   否则静态构造期间会被调用并拿到 null，导致类型初始化永久失败。
    /// </summary>
    private static bool _published;
    public static void PublishToEngine()
    {
        if (_published) return;
        _published = true;
        foreach (var kv in Map)
        {
            try
            {
                var namePtr = Marshal.StringToHGlobalAnsi(kv.Key);   // 不释放：引擎会保留
                il2cpp_add_internal_call(namePtr, kv.Value);
                MelonLogger.Msg("[SHIM] 已写入引擎 ICall 表: " + kv.Key);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[SHIM] 写引擎表失败 " + kv.Key + " : " + e.Message);
            }
        }
    }

    /// <summary>
    /// 供 MiniLoader 直接调用的“克隆式创建”：用同类型现成实例克隆一个，再包成托管代理。
    /// 不走任何被裁剪的 ICall（只用到已注册的 Object::Internal_CloneSingle）。
    /// </summary>
    public static object CreateLike(Type type)
    {
        if (type == null) return null;
        try
        {
            if (!TemplateMap.TryGetValue(type.Name, out var tmpl) || tmpl == IntPtr.Zero)
            {
                if (_templateMissLogged.Add(type.Name))
                    MelonLogger.Warning("[SHIM] CreateLike: 没有模板 " + type.Name);
                return null;
            }
            if (_cloneFnPtr == IntPtr.Zero) _cloneFnPtr = Resolve("UnityEngine.Object::Internal_CloneSingle");
            if (_cloneFnPtr == IntPtr.Zero) return null;

            var clonePtr = Marshal.GetDelegateForFunctionPointer<IcallPtr1>(_cloneFnPtr)(tmpl);
            if (clonePtr == IntPtr.Zero) return null;

            var managed = Activator.CreateInstance(type, new object[] { clonePtr });
            if (managed != null && _cloneLogged.Add(type.Name))
                MelonLogger.Msg("[SHIM] CreateLike 克隆成功: " + type.Name + " -> 0x" + clonePtr.ToInt64().ToString("X"));
            return managed;
        }
        catch (Exception e)
        {
            MelonLogger.Warning("[SHIM] CreateLike 失败 " + type.Name + ": " + e.GetType().Name + " " + e.Message);
            return null;
        }
    }

    /// <summary>注册所有已实现的 shim（由 ICallFixMod 在安装 detour 后调用）。</summary>
    public static void RegisterAll()
    {
        Register("UnityEngine.ScriptableObject::CreateScriptableObjectInstanceFromName",
                 new CreateSoByNameDelegate(CreateSoByName));
    }
}


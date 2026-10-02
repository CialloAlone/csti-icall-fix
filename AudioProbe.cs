using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Il2CppInterop.Runtime;
using MelonLoader;

namespace CstiICallFix;

/// <summary>
/// 真机一次性取证（第二轮）：直接验证「用 AudioSampleProvider 把 mod 的 PCM 喂进 AudioSource」这条
/// **不创建 AudioClip** 的播放链路，并顺带枚举游戏已有的 AudioClip。
///
/// 依据（两条独立证据）：
///   A. 真机注册表扫描：`UnityEngine.Experimental.Audio.AudioSampleProvider::*` 与
///      `AudioSourceExtensionsInternal::Internal_RegisterSampleProviderWithAudioSource` 全部**已注册**；
///      `AudioClip::Construct_Internal/SetData/CreateUserSound/...` 全部缺失。
///   B. interop 元数据：`UnityEngine.Experimental.Audio.AudioSampleProvider` 类存在且未裁剪
///      （public static Create(ushort,uint) / QueueSampleFrames / freeSampleFrameCount / sampleFramesAvailable）。
///
/// 上一轮实测修正：
///   · `AudioSource::PlayOneShotHelper` = MISS（不要用 PlayOneShot）
///   · `Resources::FindObjectsOfTypeAll` = MISS（要枚举对象请用 `Object::FindObjectsOfType(Type)`）
///   · `MelonMod.OnUpdate` 在本构建里不触发 → 探针只靠后台线程跑
///
/// 触发方式：后台线程（已实测可用）/ OnUpdate / OnApplicationQuit，先到先跑。
/// </summary>
public static class AudioProbe
{
    private const string Pkg = "com.winterspringgames.survivaljourney";
    private static readonly string Path_ = "/sdcard/MelonLoader/" + Pkg + "/audio_probe.txt";

    private static int _ran;
    private static readonly object Gate = new object();

    public static void Write(string s)
    {
        lock (Gate)
        {
            try { File.AppendAllText(Path_, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + "\n"); }
            catch { }
        }
    }

    public static void StartBackground(int delayMs)
    {
        var t = new Thread(() =>
        {
            try { Thread.Sleep(delayMs); } catch { }
            Run("bgthread");
        })
        { IsBackground = true, Name = "AudioProbe" };
        t.Start();
    }

    public static void Run(string trigger)
    {
        if (Interlocked.Exchange(ref _ran, 1) != 0) return;
        try { File.WriteAllText(Path_, ""); } catch { }
        Write("== AudioProbe(第二版) 触发源=" + trigger + " ==");
        try
        {
            try
            {
                var dom = IL2CPP.il2cpp_domain_get();
                var th = IL2CPP.il2cpp_thread_attach(dom);
                Write("il2cpp_thread_attach domain=0x" + dom.ToInt64().ToString("X")
                      + " thread=0x" + th.ToInt64().ToString("X"));
            }
            catch (Exception e) { Write("thread_attach 失败(继续): " + e.GetType().Name + " " + e.Message); }

            ProbeResolveTable();
            ProbeClipList();
            ProbeSampleProvider();
        }
        catch (Exception e)
        {
            Write("!! 顶层异常: " + e.GetType().Name + " " + e.Message);
            Write(e.StackTrace ?? "");
        }
        Write("== AudioProbe 结束 ==");
    }

    private static IntPtr R(string name)
    {
        try { return IL2CPP.il2cpp_resolve_icall(name); } catch { return IntPtr.Zero; }
    }

    // ---- 1. 音频/采样器相关 ICall 解析表 ----
    private static readonly string[] AudioNames =
    {
        "UnityEngine.AudioClip::Construct_Internal",
        "UnityEngine.AudioClip::CreateUserSound",
        "UnityEngine.AudioClip::SetData",
        "UnityEngine.AudioClip::get_length()",
        "UnityEngine.AudioSource::Play(System.Double)",
        "UnityEngine.AudioSource::PlayHelper(UnityEngine.AudioSource,System.UInt64)",
        "UnityEngine.AudioSource::PlayOneShotHelper",
        "UnityEngine.AudioSource::set_clip(UnityEngine.AudioClip)",
        "UnityEngine.AudioSource::get_isPlaying",
        "UnityEngine.AudioSource::Stop(System.Boolean)",
        "UnityEngine.GameObject::Internal_AddComponentWithType(System.Type)",
        "UnityEngine.Object::FindObjectsOfType(System.Type)",
        "UnityEngine.Resources::FindObjectsOfTypeAll",
        // ★ 本轮主角
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalCreateSampleProvider",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalQueueSampleFrames",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetMaxSampleFrameCount",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetFreeSampleFrameCount",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetFreeSampleFrameCountLowThreshold",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalSetFreeSampleFrameCountLowThreshold",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalIsValid",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetFormatInfo",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalSetEnableSilencePadding",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetEnableSilencePadding",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalSetEnableSampleFramesAvailableEvents",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalSetSampleFramesAvailableNativeHandler",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetConsumeSampleFramesNativeFunctionPtr",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetScriptingPtr",
        "UnityEngine.Experimental.Audio.AudioSampleProvider::InternalSetScriptingPtr",
        "UnityEngine.Experimental.Audio.AudioSourceExtensionsInternal::Internal_RegisterSampleProviderWithAudioSource",
        "UnityEngine.Experimental.Audio.AudioSourceExtensionsInternal::Internal_UnregisterSampleProviderFromAudioSource",
    };

    private static void ProbeResolveTable()
    {
        Write("-- [1] ICall 解析表 --");
        int have = 0, miss = 0;
        foreach (var n in AudioNames)
        {
            var p = R(n);
            if (p != IntPtr.Zero) have++; else miss++;
            Write((p != IntPtr.Zero ? "  [HAVE] " : "  [MISS] ") + n + "  = 0x" + p.ToInt64().ToString("X"));
        }
        Write("  小结: 已注册 " + have + " / 缺失 " + miss + " / 共 " + AudioNames.Length);
    }

    // ---- 2. 枚举游戏已有 AudioClip（用实测已注册的 Object::FindObjectsOfType）----
    private static void ProbeClipList()
    {
        Write("-- [2] 枚举游戏已有 AudioClip --");
        try
        {
            var find = R("UnityEngine.Object::FindObjectsOfType(System.Type)");
            var getName = R("UnityEngine.Object::GetName(UnityEngine.Object)");
            var getLen = R("UnityEngine.AudioClip::get_length()");
            Write("  FindObjectsOfType=0x" + find.ToInt64().ToString("X")
                  + "  GetName=0x" + getName.ToInt64().ToString("X")
                  + "  get_length=0x" + getLen.ToInt64().ToString("X"));
            if (find == IntPtr.Zero) { Write("  FindObjectsOfType 未注册，跳过"); return; }

            var clipType = Il2CppType.Of<UnityEngine.AudioClip>();
            IntPtr arr;
            unsafe { var d = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)find; arr = d(clipType.Pointer); }
            if (arr == IntPtr.Zero) { Write("  FindObjectsOfType(AudioClip) 返回 null（运行时活动对象里没有 clip 资源，属正常）"); }
            else
            {
                int len = Marshal.ReadInt32(arr, 0x18);
                Write("  数组长度 = " + len);
                var names = new List<string>();
                for (int i = 0; i < len && i < 20000; i++)
                {
                    IntPtr c = Marshal.ReadIntPtr(arr, 0x20 + i * IntPtr.Size);
                    if (c == IntPtr.Zero) continue;
                    string nm = "(?)"; float dur = -1f;
                    try
                    {
                        if (getName != IntPtr.Zero)
                        {
                            IntPtr sp;
                            unsafe { var d = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)getName; sp = d(c); }
                            nm = Il2CppStringToManaged(sp) ?? "(null)";
                        }
                        if (getLen != IntPtr.Zero)
                        {
                            unsafe { var d = (delegate* unmanaged[Cdecl]<IntPtr, float>)getLen; dur = d(c); }
                        }
                    }
                    catch (Exception e) { nm = "(异常 " + e.GetType().Name + ")"; }
                    names.Add(nm);
                    if (i < 300) Write("    #" + i + " 0x" + c.ToInt64().ToString("X") + " len=" + dur.ToString("0.###") + "s name=[" + nm + "]");
                }
                Write("  AudioClip 总数 = " + len);
                try { File.WriteAllText("/sdcard/MelonLoader/" + Pkg + "/audio_clip_names.txt", string.Join("\n", names)); } catch { }
            }

            // 游戏自己的 SoundManager 命名 clip（方案乙映射表需要的正是这些）
            DumpSoundManagerClips();
        }
        catch (Exception e) { Write("  !! 枚举异常: " + e.GetType().Name + " " + e.Message); }
    }

    /// <summary>反射读游戏 SoundManager / MenuSoundManager 里的命名 AudioClip 字段。</summary>
    private static void DumpSoundManagerClips()
    {
        try
        {
            Type t = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType("SoundManager", false); } catch { }
                if (t != null) break;
            }
            if (t == null) { Write("  [SoundManager] 找不到类型"); return; }
            object inst = null;
            try
            {
                var pi = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (pi != null) inst = pi.GetValue(null);
            }
            catch (Exception e) { Write("  [SoundManager] Instance 取值失败: " + e.GetType().Name); }
            if (inst == null) { Write("  [SoundManager] Instance 为 null（游戏还没初始化）"); return; }

            int n = 0;
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                var ft = f.FieldType;
                if (ft != typeof(UnityEngine.AudioClip) && ft != typeof(UnityEngine.AudioClip[])) continue;
                try
                {
                    var v = f.GetValue(inst);
                    if (v == null) { n++; continue; }
                    if (ft == typeof(UnityEngine.AudioClip))
                    {
                        var clip = (UnityEngine.AudioClip)v;
                        Write("  [SoundManager] " + f.Name + " = [" + SafeName(clip) + "] len=" + SafeLen(clip).ToString("0.###"));
                    }
                    else
                    {
                        var arr = (UnityEngine.AudioClip[])v;
                        var sb = new StringBuilder();
                        for (int i = 0; i < arr.Length && i < 6; i++) sb.Append("[" + SafeName(arr[i]) + "] ");
                        Write("  [SoundManager] " + f.Name + " (" + arr.Length + ") " + sb);
                    }
                    n++;
                }
                catch { }
            }
            Write("  [SoundManager] 音频字段共 " + n + " 个");
        }
        catch (Exception e) { Write("  [SoundManager] 失败: " + e.GetType().Name + " " + e.Message); }
    }

    private static string SafeName(UnityEngine.AudioClip c)
    {
        try { return c == null ? "null" : c.name; } catch { return "?"; }
    }

    private static float SafeLen(UnityEngine.AudioClip c)
    {
        try { return c == null ? -1f : c.length; } catch { return -1f; }
    }

    // ---- 3. ★ 核心：AudioSampleProvider → AudioSource，不创建任何 AudioClip ----
    private static void ProbeSampleProvider()
    {
        Write("-- [3] ★ AudioSampleProvider 直喂 PCM 到 AudioSource（不创建 AudioClip）--");
        var pCreate = R("UnityEngine.Experimental.Audio.AudioSampleProvider::InternalCreateSampleProvider");
        var pQueue = R("UnityEngine.Experimental.Audio.AudioSampleProvider::InternalQueueSampleFrames");
        var pMax = R("UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetMaxSampleFrameCount");
        var pFree = R("UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetFreeSampleFrameCount");
        var pValid = R("UnityEngine.Experimental.Audio.AudioSampleProvider::InternalIsValid");
        var pSetSilence = R("UnityEngine.Experimental.Audio.AudioSampleProvider::InternalSetEnableSilencePadding");
        var pSetEvents = R("UnityEngine.Experimental.Audio.AudioSampleProvider::InternalSetEnableSampleFramesAvailableEvents");
        var pScriptPtr = R("UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetScriptingPtr");
        var pReg = R("UnityEngine.Experimental.Audio.AudioSourceExtensionsInternal::Internal_RegisterSampleProviderWithAudioSource");
        var pAddComp = R("UnityEngine.GameObject::Internal_AddComponentWithType(System.Type)");
        var pPlay = R("UnityEngine.AudioSource::Play(System.Double)");
        var pIsPlaying = R("UnityEngine.AudioSource::get_isPlaying");
        // 追加的多路判据（都不依赖 isPlaying）
        var pAvail = R("UnityEngine.Experimental.Audio.AudioSampleProvider::InternalGetAvailableSampleFrameCount");
        var pSetLoop = R("UnityEngine.AudioSource::set_loop");
        var pDspTime = R("UnityEngine.AudioSettings::get_dspTime");
        var pGetOut = R("UnityEngine.AudioListener::GetOutputDataHelper");
        Write("  判据指针: avail=0x" + pAvail.ToInt64().ToString("X")
              + " setLoop=0x" + pSetLoop.ToInt64().ToString("X")
              + " dspTime=0x" + pDspTime.ToInt64().ToString("X")
              + " getOutputData=0x" + pGetOut.ToInt64().ToString("X"));

        foreach (var kv in new (string, IntPtr)[]
                 {
                     ("CreateSampleProvider", pCreate), ("QueueSampleFrames", pQueue), ("GetMaxSampleFrameCount", pMax),
                     ("GetFreeSampleFrameCount", pFree), ("IsValid", pValid), ("SetEnableSilencePadding", pSetSilence),
                     ("SetEnableSampleFramesAvailableEvents", pSetEvents), ("GetScriptingPtr", pScriptPtr),
                     ("RegisterSampleProviderWithAudioSource", pReg), ("AddComponentWithType", pAddComp),
                     ("AudioSource::Play", pPlay), ("AudioSource::get_isPlaying", pIsPlaying),
                 })
            Write("  " + (kv.Item2 != IntPtr.Zero ? "[ok] " : "[缺口] ") + kv.Item1 + " = 0x" + kv.Item2.ToInt64().ToString("X"));

        if (pCreate == IntPtr.Zero || pQueue == IntPtr.Zero || pReg == IntPtr.Zero || pAddComp == IntPtr.Zero || pPlay == IntPtr.Zero)
        {
            Write("  关键 ICall 缺失，无法验证");
            return;
        }

        const ushort ch = 1;
        const uint rate = 48000;
        const int frames = 48000;   // 1 秒

        uint id;
        unsafe { var d = (delegate* unmanaged[Cdecl]<ushort, uint, uint>)pCreate; id = d(ch, rate); }
        Write("  InternalCreateSampleProvider(1ch, 48000) -> providerId = " + id);
        if (id == 0) { Write("  providerId=0 创建失败，停止"); return; }

        if (pValid != IntPtr.Zero)
        {
            bool ok;
            unsafe { var d = (delegate* unmanaged[Cdecl]<uint, byte>)pValid; ok = d(id) != 0; }
            Write("  InternalIsValid = " + ok);
        }
        if (pSetSilence != IntPtr.Zero)
        {
            unsafe { var d = (delegate* unmanaged[Cdecl]<uint, byte, void>)pSetSilence; d(id, 0); }
            Write("  enableSilencePadding = false");
        }
        if (pSetEvents != IntPtr.Zero)
        {
            unsafe { var d = (delegate* unmanaged[Cdecl]<uint, byte, void>)pSetEvents; d(id, 0); }
            Write("  enableSampleFramesAvailableEvents = false（纯 push 模式）");
        }
        if (pScriptPtr != IntPtr.Zero)
        {
            IntPtr sp;
            unsafe { var d = (delegate* unmanaged[Cdecl]<uint, IntPtr>)pScriptPtr; sp = d(id); }
            Write("  InternalGetScriptingPtr -> 0x" + sp.ToInt64().ToString("X"));
        }
        if (pMax != IntPtr.Zero)
        {
            uint mx;
            unsafe { var d = (delegate* unmanaged[Cdecl]<uint, uint>)pMax; mx = d(id); }
            Write("  maxSampleFrameCount = " + mx);
        }

        // 造 1 秒 440Hz 正弦（交织 float32）
        int bytes = frames * ch * 4;
        var buf = Marshal.AllocHGlobal(bytes);
        try
        {
            unsafe
            {
                var f = (float*)buf;
                for (int i = 0; i < frames; i++)
                    f[i] = 0.25f * (float)Math.Sin(2.0 * Math.PI * 440.0 * i / rate);
            }
            uint queued;
            unsafe { var d = (delegate* unmanaged[Cdecl]<uint, IntPtr, uint, uint>)pQueue; queued = d(id, buf, frames); }
            Write("  InternalQueueSampleFrames(440Hz 正弦, " + frames + " frames) -> 入队 = " + queued);

            if (pFree != IntPtr.Zero)
            {
                uint fr;
                unsafe { var d = (delegate* unmanaged[Cdecl]<uint, uint>)pFree; fr = d(id); }
                Write("  freeSampleFrameCount = " + fr);
            }

            Write("  new GameObject(\"audioProbe\") ...");
            var go = new UnityEngine.GameObject("audioProbe");
            Write("  GameObject ok 0x" + go.Pointer.ToInt64().ToString("X"));

            var srcType = Il2CppType.Of<UnityEngine.AudioSource>();
            IntPtr src;
            unsafe { var d = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr>)pAddComp; src = d(go.Pointer, srcType.Pointer); }
            Write("  AddComponent(AudioSource) -> 0x" + src.ToInt64().ToString("X"));
            if (src == IntPtr.Zero) { Write("  AudioSource 创建失败，停止"); return; }

            unsafe { var d = (delegate* unmanaged[Cdecl]<IntPtr, uint, void>)pReg; d(src, id); }
            Write("  ★ Internal_RegisterSampleProviderWithAudioSource 已调用");

            if (pSetLoop != IntPtr.Zero) { unsafe { var d = (delegate* unmanaged[Cdecl]<IntPtr, byte, void>)pSetLoop; d(src, 0); } }

            unsafe { var d = (delegate* unmanaged[Cdecl]<IntPtr, double, void>)pPlay; d(src, 0.0); }
            Write("  AudioSource::Play(0) 已调用");

            // ---- 四路独立判据 ----
            // 1) isPlaying —— 注意：AudioSource 没有 clip 时它**可能一直是 false**，不能单独当结论
            // 2) availableSampleFrameCount 随时间**下降** = 混音器真的在消费我们推的帧 ← 主判据
            // 3) freeSampleFrameCount 上升（互补）
            // 4) AudioListener::GetOutputDataHelper 抓真实混音输出，算 440Hz 频点幅度 ← 最强判据
            uint avail0 = pAvail != IntPtr.Zero ? ReadAvail(pAvail, id) : 0;
            double dsp0 = pDspTime != IntPtr.Zero ? ReadDsp(pDspTime) : 0;
            double rmsBefore = MeasureOutput(pGetOut, 440.0, out double toneBefore);
            Write("  基线(before Play 生效): availFrames=" + avail0 + "  dspTime=" + dsp0.ToString("0.000")
                  + "  outRMS=" + rmsBefore.ToString("0.000000") + "  440Hz幅度=" + toneBefore.ToString("0.000000"));

            bool anyPlaying = false;
            uint minAvail = avail0;
            double maxTone = toneBefore, maxRms = rmsBefore;
            uint availPrev = avail0;
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(150);
                bool pl = false;
                if (pIsPlaying != IntPtr.Zero)
                {
                    unsafe { var d = (delegate* unmanaged[Cdecl]<IntPtr, byte>)pIsPlaying; pl = d(src) != 0; }
                }
                uint avail = pAvail != IntPtr.Zero ? ReadAvail(pAvail, id) : 0;
                uint free = 0;
                if (pFree != IntPtr.Zero) { unsafe { var d = (delegate* unmanaged[Cdecl]<uint, uint>)pFree; free = d(id); } }
                double dsp = pDspTime != IntPtr.Zero ? ReadDsp(pDspTime) : 0;
                double rms = MeasureOutput(pGetOut, 440.0, out double tone);
                if (avail < minAvail) minAvail = avail;
                if (tone > maxTone) maxTone = tone;
                if (rms > maxRms) maxRms = rms;
                Write("    t=+" + ((i + 1) * 150) + "ms  isPlaying=" + pl
                      + "  availFrames=" + avail + "(Δ" + ((long)avail - (long)availPrev) + ")"
                      + "  freeFrames=" + free
                      + "  dspTime=" + dsp.ToString("0.000")
                      + "  outRMS=" + rms.ToString("0.000000") + "  440Hz=" + tone.ToString("0.000000"));
                availPrev = avail;
                if (pl) anyPlaying = true;
            }

            bool consumed = avail0 > 0 && minAvail < avail0;
            bool toneRose = maxTone > toneBefore * 3.0 && maxTone > 0.0005;
            bool dspMoved = pDspTime != IntPtr.Zero && ReadDsp(pDspTime) > dsp0;
            Write("  ==== 判据汇总 ====");
            Write("   isPlaying      : " + anyPlaying + (anyPlaying ? "  ✔" : "  （无 clip 时本项可能恒 false，不作否定依据）"));
            Write("   帧被消费       : " + consumed + "   avail " + avail0 + " -> " + minAvail + (consumed ? "  ✔ 混音器在拉数据" : "  ✘ 没有消费"));
            Write("   dspTime 前进   : " + dspMoved + (dspMoved ? "  ✔ 音频 DSP 在跑" : "  ✘"));
            Write("   输出出现440Hz  : " + toneRose + "   " + toneBefore.ToString("0.000000") + " -> " + maxTone.ToString("0.000000")
                  + (toneRose ? "  ✔ 真的出声了" : ""));
            Write("   ★★ 结论: 采样器→AudioSource 链路 "
                  + ((consumed || toneRose || anyPlaying) ? "**打通（至少一项正证据）**" : "**本次未观测到正证据**"));
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static uint ReadAvail(IntPtr pAvail, uint id)
    {
        try { unsafe { var d = (delegate* unmanaged[Cdecl]<uint, uint>)pAvail; return d(id); } } catch { return 0; }
    }

    private static double ReadDsp(IntPtr pDsp)
    {
        try { unsafe { var d = (delegate* unmanaged[Cdecl]<double>)pDsp; return d(); } } catch { return 0; }
    }

    /// <summary>
    /// 抓 AudioListener 的真实混音输出，在 400–480 Hz 扫频取最大幅度（避开输出采样率未知的问题），
    /// 同时返回总 RMS。返回 0 表示这条判据不可用（没有 AudioListener / ICall 异常），不影响其它判据。
    /// </summary>
    private static double MeasureOutput(IntPtr pGetOut, double ignored, out double toneMag)
    {
        toneMag = 0;
        if (pGetOut == IntPtr.Zero) return 0;
        const int N = 1024;
        try
        {
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(N);
            unsafe { var d = (delegate* unmanaged[Cdecl]<IntPtr, int, void>)pGetOut; d(arr.Pointer, 0); }

            double sum = 0;
            var v = new double[N];
            for (int i = 0; i < N; i++) { v[i] = arr[i]; sum += v[i] * v[i]; }
            double rms = Math.Sqrt(sum / N);

            double best = 0;
            for (double f = 400; f <= 480; f += 5)
            {
                double re = 0, im = 0;
                for (int i = 0; i < N; i++)
                {
                    double ph = 2.0 * Math.PI * f * i / 48000.0;
                    re += v[i] * Math.Cos(ph);
                    im += v[i] * Math.Sin(ph);
                }
                double mag = 2.0 * Math.Sqrt(re * re + im * im) / N;
                if (mag > best) best = mag;
            }
            toneMag = best;
            return rms;
        }
        catch { return 0; }
    }

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
}

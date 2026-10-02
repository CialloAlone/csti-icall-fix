# csti-icall-fix — 接管裁剪版 IL2CPP 构建中缺失的引擎 ICall

Android 版《Card Survival: Tropical Island》的 `libil2cpp.so` 是**裁剪过的构建**：
Il2CppInterop 生成代理程序集时只保留了"托管可见名"，被删掉的引擎 ICall 在运行时统一报
`Exception: ICall with signature UnityEngine.X::Y was not resolved`，更糟的情况是直接 SIGSEGV
（典型：`Sprite.Create`、`ImageConversion.LoadImage` 这类托管包装）。

本 mod 在启动早期把这些缺口补回去，让依赖它们的 mod（图标管道、贴图处理等）能正常工作。

## 这是什么

- 一份**缺失清单**：本机实测 234 条（见 `icall_inventory.txt`）；
- 一套**接管机制**：优先用 `X_Injected` / `XImpl` 形式的等价实现（libil2cpp 里确实注册了它们，
  只是名字不同），必要时用 C# 侧自实现的等价 shim；
- 只接管"当前未注册"的项 —— 已注册的一律不动（日志逐条打印 `引擎未注册 ICall（已接管）: …`），
  避免遮蔽游戏自身实现；
- 附带 `AudioProbe` / `CacheBreaker` 两个诊断开关，用于区分"缺 ICall"与"缓存/时序"问题。

## 构建

```powershell
# 需要 MelonLoader 0.6.5 的 net8 托管 dll + Windows 侧用 Il2CppInterop 生成的游戏代理程序集
dotnet build -c Release -p:ML06=<你的 ml-installer-06 目录>
# 产物：bin\Release\CstiICallFix.dll
```

`-p:ML06` 指向的目录需包含：

```
mldata\MelonLoader\net8\{MelonLoader,0Harmony,Il2CppInterop.Runtime}.dll
interop_out\*.dll                     # Il2CppInterop 生成的游戏代理程序集
```

## 部署到设备

```powershell
adb push bin\Release\CstiICallFix.dll /sdcard/MelonLoader/com.winterspringgames.survivaljourney/Mods/
adb shell am force-stop com.winterspringgames.survivaljourney
adb shell am start -n com.winterspringgames.survivaljourney/com.x.shell.JPolicyActivity
# 看日志：/sdcard/MelonLoader/<包名>/MelonLoader/Latest.log
```

**只推这一个 DLL**，不要把 `bin\Release\` 整个目录推上去（里面是引用拷贝，会污染 `Mods\`）。

## 依赖版本

| 项 | 版本 |
|---|---|
| MelonLoader | **0.6.5**（.NET 8） |
| 目标框架 | `net8.0` |
| Il2CppInterop | MelonLoader 0.6 自带运行时 + Windows 侧生成的代理程序集 |
| 游戏 | Android arm64，包名 `com.winterspringgames.survivaljourney` |

## 已知限制

- **清单是按本机这一个 APK 版本实测的**（234 条）。换游戏版本 / 换构建需要重新扫描
  （工具见 [`csti-android-toolkit`](https://github.com/CialloAlone/csti-android-toolkit)）。
- 只能补"有等价实现"的项：真正没有实现的接口（例如 `AudioClip::Construct_Internal` 这类
  在 wrapper 名单里根本不存在的）**补不了**，需要另找路径。
- 接管发生在引擎 ICall 表上，属于全局副作用；若其它 mod 也做同样的事，需要约定顺序。
- 本仓库**不含**游戏本体、APK、签名密钥或任何上游二进制。

## 相关仓库

| 仓库 | 内容 |
|---|---|
| [`csti-android-toolkit`](https://github.com/CialloAlone/csti-android-toolkit) | 探针工具 + 逆向文档（ICall 清单、加载流程、warp 机理） |
| [`csti-quickmenu`](https://github.com/CialloAlone/csti-quickmenu) | 悬浮窗快捷菜单 |
| [`CSTI-ModLoader@android-06-port`](https://github.com/CialloAlone/CSTI-ModLoader/tree/android-06-port) | 移植版 MiniLoader（本轮主体） |

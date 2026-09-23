# ScreenTranslator · Phase 3.1

当前已完成 Phase 3.1 的离线神经翻译升级：在不修改既有 Windows Graphics Capture、演示区域检测和 OCR 核心的前提下，将有限短语表替换为 MarianMT / OPUS-MT 量化 ONNX 整句英译中模型。普通 PPT 英文句子会生成完整中文，不再只替换少数命中词。Phase 2/3 截图、IoU 和检测资料见 [Phase 3 交付报告](docs/Phase3/Report.md)，本次翻译结果见 [Phase 3.1 报告](docs/Phase3.1/Report.md)。

Windows 本地桌面应用。C# / .NET 10 / WPF，使用 Windows Graphics Capture + Direct3D 11、OpenCvSharp、Windows.Media.Ocr 和本地语言包。没有 PDF 解析、在线翻译、模型下载或网络服务。

**当前交付状态（Phase 3.1，2026-09-13）：** WPS 编辑模式和全屏放映模式沿用已通过的真实桌面检测/OCR路径；翻译层已升级并完成十组自由文本、真实 Windows OCR 三行页面、缓存、取消和模型完整性验收。Microsoft PowerPoint 因本机未安装而未做实机兼容验收。`Release\ScreenTranslator.exe` 启动后默认待机，不会自动读取屏幕。

## 直接运行

最终项目目录：`C:\翻译器`。

1. 双击 `Launch.cmd`，或打开 `Release\ScreenTranslator.exe`。
2. 默认进入首页、待机中。需要换屏时进入“实时识别”或“设置”。
3. 在首页快速设置中选择 **DeepL**、**DeepSeek V4 Pro** 或 **DeepSeek Flash**，然后点击 **开始识别**；程序在本机完成 OCR，只把识别文字发送给所选在线翻译服务。
4. 在“实时识别”点击 **暂停识别** 保留画面，再点 **继续识别** 恢复。
5. **停止识别** 结束捕获并清空预览；此时可以换屏或点击 **刷新显示器** 重新枚举。

发布包包含 .NET 运行时，不需要安装 Python、Visual Studio 或 .NET，也不依赖网络。正常运行使用普通用户权限，但必须符合所在机器的应用程序控制策略。支持 Windows 10 2004（19041）及以上的 x64 桌面系统，建议使用受支持的 Windows 11。需要支持 D3D11 和 Windows Graphics Capture 的显卡驱动及交互式桌面会话。

0.3.1 使用自包含单文件发布，将托管翻译依赖嵌入 `ScreenTranslator.exe`，避免 Windows Smart App Control 在首次加载翻译引擎时拦截单独的未签名托管 DLL。原生 ONNX Runtime 保持微软有效签名，模型仍作为外部只读数据文件随包分发。

**FPS 是实际成功采集的新帧数**，默认目标 30。静止桌面或系统不提供新帧时 FPS 会下降；程序不会用重复帧伪造性能。实际分辨率来自帧内容的物理像素，不是 WPF 的逻辑尺寸。

控制窗口在捕获期间通过 Windows 的窗口捕获排除标志从屏幕帧中移除，避免预览递归。系统不支持该标志时仍可把控制窗口放到另一块屏幕。

## 工程结构

```text
C:\翻译器\
├─ ScreenTranslator.sln
├─ global.json / NuGet.Config / appsettings.json
├─ Launch.cmd / Build.ps1 / Test.ps1
├─ App/                    WPF 入口、窗口、ViewModel、命令
├─ Core/                   ApplicationState
├─ Capture/                独立捕获接口、服务、帧、显示器、WGC/COM 桥接
├─ Services/               配置与轮转日志
├─ Vision/                 演示窗口跟踪、候选生成、评分、全屏检测和时序稳定
├─ OCR/                    Windows 本地 OCR、纠错、阅读顺序
├─ Translation/            本地语言包、翻译接口与术语管理
├─ Recognition/            页面指纹、防抖、缓存与任务调度
├─ Overlay/                三种透明翻译显示模式
├─ Models/ / Utils/        识别与翻译数据结构
├─ Assets/ / ModelsData/   English → 中文 MarianMT 离线语言包
├─ Tests/                  真实桌面集成测试程序
├─ docs/                   架构、开发步骤、验收记录
└─ Release/                可直接运行的 x64 离线发布包
```

## 核心代码

- `Capture/IScreenCaptureService.cs`：Start / Pause / Stop、状态、FPS 和最新帧所有权转移。接口不引用 WPF，未来可拆为独立程序集。
- `Capture/ScreenCaptureService.cs`：独立采集线程、限帧、生命周期互斥、取消、恢复重试和单帧缓冲。UI 从不调用 GPU 捕获方法。
- `Capture/WindowsGraphicsCaptureSession.cs`：为指定 HMONITOR 创建 WGC 会话，使用两帧系统池、可复用的 D3D11 staging texture 和正确的 Map/Unmap。尺寸变化时重建帧池。方向由 WGC 输出处理。
- `Capture/WinRtCaptureInterop.cs`：少量标准 Windows COM 桥接；C# 实现，没有额外 C++ 模块。
- `Capture/CapturedFrame.cs`：UTC Timestamp、Width、Height、Stride、MonitorId 和连续 BGRA32 PixelData。帧拥有租用的数组，使用完必须 Dispose；不可在 Dispose 后持有 PixelData。
- `Capture/MonitorService.cs`：枚举全部已连接屏幕，读取名称、物理尺寸、缩放和主屏标记；优先以显示设备接口路径保存选择。
- `Vision/VisionPipeline.cs`：有界单任务后台管线；只处理最新帧，停止后丢弃迟到结果，不阻塞 UI。
- `Vision/TargetWindowTracker.cs`：选择当前显示器上的 WPS/PowerPoint 演示窗口，并记录遮挡窗口。
- `Vision/PresentationDetector.cs`：编辑模式候选生成/评分、全屏黑边扫描、遮挡抑制与统一输出。
- `Vision/PresentationRegionTracker.cs`：三帧确认、高置信度快速接受、平滑、异常帧抑制和短暂丢失保持。
- `Recognition/RecognitionPipeline.cs`：PresentationRegion 裁剪、页面稳定防抖、OCR/翻译取消、缓存与 Overlay 调度。
- `OCR/WindowsOcrEngine.cs`：封装 Windows.Media.Ocr en-US 本地引擎；`IReadingOrderResolver` 处理标题和双栏顺序。
- `Translation/MarianOnnxTranslationEngine.cs`：加载随包的 MarianMT 量化 ONNX 编码器/解码器，执行整句英译中、分段、取消和有界内存缓存。
- `Translation/MarianTokenizerAdapter.cs`：用 SentencePiece 解析源/目标文本，并按照 Marian 联合词表完成模型 token ID 映射。
- `Translation/LocalPhraseTranslationEngine.cs`：保留为诊断和回退组件；正常识别流程不再使用它作为主引擎。
- `Overlay/WpfTranslationOverlay.cs`：透明、Topmost、不激活、鼠标穿透的三模式显示窗口，并从捕获帧中排除自身。
- `App/MainViewModel.cs`：按钮状态、屏幕选择、低优先级 UI 定时器和复用 WriteableBitmap。仅将最新帧复制到预览，不创建逐帧 Dispatcher 队列。
- `Services/`：本地 JSON 配置、原子替换保存和有容量上限的日志文件。

更详细的资源所有权与扩展说明见 `docs/Architecture.md`。

## 配置和日志

初始配置模板在项目根目录和 `Release\appsettings.json`。首次启动读取发布目录模板，后续读取：

```text
%LOCALAPPDATA%\ScreenTranslator\appsettings.json
%LOCALAPPDATA%\ScreenTranslator\logs\screen-translator.log
```

`SelectedMonitor` 保存设备接口 ID，`TargetFPS` 支持 1–60，默认 30。设置页提供 20 / 30 / 60 三个常用目标。`DebugMode` 默认为 true，控制实时识别页的高级诊断显示，不再隐藏产品预览。`AutoStart` 始终为 false，即使配置写入 true 也不会自动采集。修改配置前退出程序；保存的用户配置优先于模板。

日志记录启动、显示器、开始、暂停、停止、设备丢失/捕获错误、异常和每 10 秒一次的 FPS。日志约 2 MB 轮转一次，最多保留当前文件和三个备份。生产程序不保存屏幕图片。

测试程序使用 `SCREEN_TRANSLATOR_DATA_DIR` 将测试配置和日志隔离到测试目录，并额外写入一张本地预览验证图片。它不会改变正常用户配置。

## 从源码构建

安装 .NET 10 SDK（`global.json` 固定 10.0.401，允许同主次版本的后续 feature band）。Visual Studio 用户需安装支持 .NET 10 的版本及“.NET 桌面开发”工作负载，打开 Solution 并将 App 设为启动项目。

在项目目录运行：

```powershell
dotnet restore .\ScreenTranslator.sln --configfile .\NuGet.Config --locked-mode
dotnet build .\ScreenTranslator.sln -c Release --no-restore
dotnet run --project .\App\ScreenTranslator.csproj -c Release --no-build
```

也可执行 `powershell -ExecutionPolicy Bypass -File .\Build.ps1` 生成完整发布包，或通过 `-DotnetPath` 指定独立 SDK 的 dotnet.exe。首次还原开发依赖需要网络；生成的 Release 程序运行不依赖网络。NuGet 包版本和哈希保存在锁文件中。

## 测试与验收

在已登录、屏幕未锁定的 Windows 桌面执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\Test.ps1 -Seconds 600
```

测试会打开真实控制窗口和一个小型动态色块窗口，验证实际 GPU 像素、预览、按钮绑定、暂停恢复、停止、8 次资源重建，并采样 10 分钟的实际预览 FPS、Private Bytes、Working Set、托管内存和句柄。保持窗口可见，测试期间不要锁屏或运行额外的高负载程序。

验收以 **每个 10 秒采样区间的真实预览 FPS ≥20** 为标准。内存检查比较预热后 60–180 秒与末尾 120 秒的平均私有内存，增幅需低于 64 MB，且句柄净增低于 30；这是有限时间的泄漏筛查，不是对所有驱动组合的数学保证。测试不强制 GC，不用重复帧增加 FPS。

已执行结果和未覆盖的硬件场景见 `docs/Acceptance.md`。不要将仅有的编译成功当作 GPU 运行验证。

## 故障定位

- **没有显示器**：点 Refresh，检查日志中的 Monitor detected；拔插后在 Stop 状态重新选择。
- **捕获失败**：看日志里的 HRESULT。系统捕获服务、显卡驱动或会话不支持时会有限重试 5 次，然后进入 Faulted；可 Stop / Refresh / Start 重试。
- **受限运行器报“指定的服务未安装”**：请从正常 Windows 桌面直接运行 Release 程序。开发沙箱可能无法访问系统捕获服务，不表示源码需要线上服务。
- **画面黑色/锁屏时无帧**：受保护内容、安全桌面与某些远程会话可能禁止系统捕获；程序不能绕过系统保护。
- **多屏或分辨率变化**：捕获中尺寸由 WGC 自适应；显示器断开后会报告错误，请 Stop 后 Refresh。物理多屏、旋转、热插拔仍需在对应硬件上按清单验收。
- **HDR**：当前调试预览为 8 位 BGRA；不做 HDR 色调映射。此阶段面向普通 SDR 文档桌面。

## 范围边界

Phase 3.1 实现了离线 MarianMT 神经翻译、SentencePiece 分词、模型 SHA256 校验、有界翻译缓存和完整英文句子覆盖。模型在当前 CPU 上单句通常约 0.15–0.30 秒；首次开始识别需要校验并加载约 110 MB 权重。它是通用机器翻译模型，专业领域术语仍可能需要用户词库和更高质量模型。PDF、网页、插件和自动模型下载未实现；Microsoft PowerPoint 兼容路径仍需在安装了桌面版 PowerPoint 的机器上补充实测。

发布目录是可分发的便携程序，尚未添加安装器或商业代码签名。第三方组件与上游许可证见 `docs/ThirdPartyNotices.md`。


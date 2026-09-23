# ScreenTranslator Phase 3 交付报告

生成日期：2026-09-13  
版本：0.3.0  
测试系统：Windows 11，2560 × 1440，缩放 150%，WPS Office 12.1.0.28505

## 完成范围

Phase 3 已接通以下全离线链路：

```text
Windows Graphics Capture
→ Phase 2 PresentationRegion
→ SlideFingerprint / 350 ms 稳定防抖
→ 仅裁剪绿色框内像素
→ Windows.Media.Ocr en-US
→ OCR 轻量纠错与规则阅读顺序
→ English → 中文本地语言包
→ 中文覆盖 / 中英对照 / 侧边栏
→ 页面缓存
```

应用仍坚持手动开始。启动时没有 Capture Session、OCR 或屏幕读取。点击“开始识别”后先初始化本地 OCR 和语言包，再启动捕获；Stop 会取消当前 OCR、丢弃迟到结果、停止视觉与捕获、隐藏 Overlay 并清空预览。

本阶段没有加入 PDF 解析、网页识别、在线 API、模型下载、安装器或 Phase 4 功能。

## 实际截图

### WPS 编辑模式：真实检测、OCR 与翻译

![WPS 编辑模式绿色框](evidence/editor-paused-green-frame.png)

最大化 WPS 编辑窗口并保留右侧属性面板时，应用显示 95% 检测置信度；OCR 结果 `Artificial Intelligence`，离线翻译结果 `人工智能`，缓存命中累计 4 次。

### WPS 全屏放映：真实绿色框与缓存恢复

![WPS 全屏绿色框](evidence/fullscreen-paused-green-frame.png)

全屏 4:3 放映时检测置信度为 96%，绿色框排除左右黑边；返回第一页后显示“已从页面缓存恢复翻译”，结果仍为 `Artificial Intelligence → 人工智能`。

### 测试源画面

![WPS 全屏测试源](evidence/wps-fullscreen-source.png)

Overlay 窗口使用 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`，避免译文重新进入 Screen Capture / OCR 形成反馈。因此捕获帧和基于 Windows Graphics Capture 的验收截图会有意排除实际 Overlay；实时识别页中的 OCR/翻译结果和模式状态用于复核输出。

## 检测 IoU 与置信度

Phase 3 没有修改 Phase 2 检测器。IoU 来自同一机器、同一测试文件的人工页面边界标注，Phase 3 的现场置信度来自本次真实 WPS 运行。

| 场景 | IoU | Phase 3 现场置信度 | 结果 |
|---|---:|---:|---|
| WPS 编辑 4:3，最大化并打开右侧面板 | 0.999905 | 95% | OCR 与离线翻译成功 |
| WPS 全屏 4:3 | 1.000000 | 96% | OCR 与离线翻译成功，返回页命中缓存 |
| WPS 编辑 4:3，较小窗口 | 未重新标注 | 81% | OCR 与离线翻译成功 |

检测置信度是特征评分，不是经过概率校准的统计置信区间。原始数据见 [actual-wps-validation.json](data/actual-wps-validation.json) 和 [Phase 2 accuracy.json](../Phase2/data/accuracy.json)。

## OCR Pipeline

- `IOcrEngine` 隔离引擎生命周期、初始化、取消和识别。
- `WindowsOcrEngine` 使用 Windows 本机 `Windows.Media.Ocr` 的 en-US 离线资源，当前执行设备为 CPU。
- `RecognitionPipeline` 先把 `PresentationRegion` 转为当前显示器局部坐标，只复制该裁剪区域；Ribbon、任务栏、其他窗口不进入 OCR。
- 大页面最长边限制为 2400 像素，降低 OCR 峰值开销；文字框再映射回原始裁剪坐标。
- `LocalOcrPostProcessor` 只处理明显字符混淆、异常空格、标点空格和跨行连字符。
- `RuleReadingOrderResolver` 先处理标题/跨栏块，再判断双栏分割并在栏内排序。

## Translation Pipeline 与模型结构

`ITranslationEngine` 接收 `TranslationRequest`，包含源/目标语言、当前 OCRBlock、块类型和 `TranslationContext`。每个 OCRBlock 单独翻译，同时传入标题和最近三个块的上下文。

当前随包提供 `ModelsData/Translation/en-zh/`：

```text
en-zh/
├─ metadata.json
├─ config.json
├─ vocabulary.json
├─ LICENSE.txt
├─ README.md
├─ tokenizer/rules.json
└─ model/phrase-table.json
```

语言包总计 4,343 bytes，版本 1.0.0，质量档为 Fast。它是项目自建的确定性短语与术语模型，不是神经机器翻译模型。验收术语 `Artificial Intelligence`、`Machine Learning`、`Neural Network` 有完整翻译；词表以外的内容保留英文并标记为 partial，避免把未知文本伪装成已完成翻译。它验证了真实离线模型加载、缺失处理、块级上下文、术语一致性、缓存和 Overlay 管线；广泛自由文本的产品级翻译质量仍需要后续替换为可再分发的 ONNX/CTranslate2 模型。

模型逐文件大小与 SHA256 见 [model-manifest.json](data/model-manifest.json)。运行时代码没有 HTTP 客户端、在线翻译调用或自动下载逻辑。

## Overlay Pipeline

- `ITranslationOverlay` 隔离显示层；`WpfTranslationOverlay` 是透明、无边框、Topmost、不激活、鼠标穿透的 WPF 窗口。
- 中文覆盖将 OCRBlock 坐标按区域 DPI 映射到演示页，并使用可调半透明圆角背景。
- 中英对照把中文优先放在英文下方，使用简单碰撞规避。
- 侧边栏按阅读顺序显示原文与译文；页面两侧没有足够空间时隐藏，避免遮住全屏 PPT。
- PresentationRegion 移动或缩放时更新窗口位置；区域丢失超过 1 秒会隐藏 Overlay。
- 三种模式已在运行中通过设置页切换并回到默认中文覆盖。Stop 隐藏 Overlay 的行为同时由自动测试验证。

## Slide Change 与缓存

- `SlideFingerprint` 将页面采样为 16 × 9 量化灰度签名，并以 SHA256 作为稳定键。
- 指纹最多每 125 ms 计算一次；变化后需稳定 350 ms 且至少两次一致观察才触发 OCR。
- 当前页未明显变化时不重复 OCR。新页面会取消上一页任务并只保留最新任务。
- `SlideRecognitionCache` 是容量 64 的线程安全 LRU，保存 OCR、翻译与布局。返回已识别页时更新 PresentationRegion 并直接恢复结果。
- 本次 WPS 验收中，第二页发生一次真实 OCR；返回第一页及编辑/全屏切换累计观察到 4 次缓存命中。

## 性能

| 指标 | 实测值 |
|---|---:|
| Phase 3 WPS 实际活跃捕获时长 | 520.37 s |
| 正常产帧区间平均 FPS | 29.947 |
| 正常产帧区间最低 / 最高 FPS | 29.5 / 30.3 |
| 首次编辑页 OCR / 翻译 / 总计 | 36.0 / 0.3 / 88.4 ms |
| 第二个变化页 OCR / 翻译 / 总计 | 14.2 / 0.1 / 15.6 ms |
| 600 秒 Protected Capture Core 平均 FPS | 28.125 |
| 600 秒最低 10 秒区间 FPS | 25.597 |
| 600 秒私有内存预热后增量 | +2.9 MB |
| 600 秒句柄净增长 | < 30 |

WGC 在静止页面或窗口切换时可能不产生新内容帧，因此报告平均值只统计日志中大于 0 的现场 FPS 样本。完整数据见 [performance.json](data/performance.json)，筛选后的原始日志见 [actual-run-log.txt](data/actual-run-log.txt)，600 秒基线见 [result.json](../test-results/soak-10min/result.json)。

## 测试结果

Release 构建：0 errors。离线环境无法读取 NuGet 漏洞元数据，产生 NU1900 警告；已锁定依赖可正常构建，运行时无需联网。

Phase 3 单元与本机 OCR 测试共 14 项全部通过：

1. Windows OCR 识别验收短语及定位框。
2. `Artificial Intelligence → 人工智能`。
3. 术语一致性与语言包元数据。
4. 标题和双栏阅读顺序。
5. OCR 纠错与去断词。
6. 初始页稳定防抖。
7. 静态页不重复 OCR。
8. 有意义变化触发新识别。
9. 返回页命中缓存。
10. 识别工作在 UI 线程之外启动。
11. Stop 取消活跃 OCR、丢弃迟到结果并隐藏 Overlay。

逐项输出见 [checks.txt](../../test-results/phase3-unit-final/checks.txt)。真实桌面另行验证了手动 Start、Pause、Resume、Stop、编辑模式、全屏模式、29.5–30.3 FPS、OCR、翻译和缓存。受限测试进程内直接调用 WGC 会返回 `0x80070424`，这是沙箱无法访问 Windows 捕获服务；同一台机器从正常桌面运行 Release 已成功完成真实 WGC 验收。

## Protected Core SHA256

以下 9 个文件与 Phase 2 基线逐字节一致，`AllMatch = true`：

| 文件 | SHA256 |
|---|---|
| Capture/CapturedFrame.cs | `6B680A8ABBC95B822E9CFC6018D6AB6653136F4CD10B64325B90EF9489E54B64` |
| Capture/IScreenCaptureService.cs | `3E75FD26ED43B8F18B559AEEE7432BF2335EF77564F92F39D55EB97919D3948C` |
| Capture/MonitorInfo.cs | `769182E7B77A9E0F8A7311BD0810A9C8045DF7DC259A5D0739848395FAF41BB7` |
| Capture/MonitorService.cs | `4576C435E7BBE7F163BA423403A8BAD23E19AF6A3ADD1CECDD6E8C1251042E52` |
| Capture/PixelCopy.cs | `CB268E9EB95850478A148CFDB56FBDF7C7B9629B8C4B2680EEB21B1B637B7310` |
| Capture/ScreenCaptureService.cs | `880BF19AB7AA4350B7126E7A59AF5DF6EBC2661E7E225B33F6395C25850942DC` |
| Capture/WindowsGraphicsCaptureSession.cs | `FA4ACA4C6E115B4C49AC4CB6CA0153DACD650F19A0BBDD5BF1E6A2D63B4E7C9B` |
| Capture/WinRtCaptureInterop.cs | `C02893B88D8219DD62F47C397DC5EAA85F2639BE6D8DFF4B4E74B1B925CCACFB` |
| Core/ApplicationState.cs | `629B9097A8A24F94C3F80BE6DF74A8E4EF9331F6F0180F2C5AFC2A3B1DCCF92B` |

机器可读结果见 [protected-core-sha256.json](data/protected-core-sha256.json)。

## 文件变更

新增 24 个 Phase 3 源码、测试、模型与元数据文件；修改 11 个集成/UI/配置文件。完整机器可读清单见 [changed-files.json](data/changed-files.json)。Capture Core 和 `Core/ApplicationState.cs` 均未修改。

主要新增：

- `Models/RecognitionModels.cs`
- `OCR/IOcrEngine.cs`、`WindowsOcrEngine.cs`、`IReadingOrderResolver.cs`、`LocalOcrPostProcessor.cs`
- `Translation/ITranslationEngine.cs`、`LocalLanguagePackService.cs`、`LocalPhraseTranslationEngine.cs`、`TerminologyManager.cs`
- `Recognition/RecognitionPipeline.cs`、`RecognitionSettings.cs`、`SlideFingerprint.cs`、`SlideRecognitionCache.cs`
- `Overlay/ITranslationOverlay.cs`、`WpfTranslationOverlay.cs`
- `ModelsData/Translation/en-zh/**`
- `Tests/Phase3Tests.cs`、`Tests/Fixtures/Phase3Acceptance.pptx`

主要修改：

- `App/MainViewModel.cs`
- `App/Views/HomeView.xaml`
- `App/Views/RecognitionView.xaml`
- `App/Views/SettingsView.xaml`
- `App/Views/LanguagePackView.xaml`
- `App/Views/PreviewPane.xaml`、`PresentationPreviewOverlay.cs`
- `Services/AppSettings.cs`、`appsettings.json`
- `App/ScreenTranslator.csproj`、`Tests/Program.cs`

## 运行方法

直接运行 `C:\翻译器\Release\ScreenTranslator.exe` 或双击 `C:\翻译器\Launch.cmd`。打开 WPS 演示后，在 ScreenTranslator 中点击“开始识别”。设置页可切换中文覆盖、中英对照和侧边栏；实时识别页可暂停、继续或停止。

源代码构建需要 .NET 10 SDK：

```powershell
dotnet build .\ScreenTranslator.sln -c Release --no-restore
dotnet publish .\App\ScreenTranslator.csproj -c Release -r win-x64 --self-contained true -o .\Release
```

## Phase 3 验收清单

- [x] 启动保持 Idle，不自动捕获或 OCR。
- [x] Start 先加载本地 OCR 与 English → 中文语言包。
- [x] OCR 输入严格限制在 PresentationRegion。
- [x] WPS 编辑模式自动检测、OCR、翻译。
- [x] WPS 全屏模式跟踪并恢复结果。
- [x] `Artificial Intelligence → 人工智能` 实际通过。
- [x] 页面变化后稳定再识别；静态页不重复 OCR。
- [x] 返回上一页优先使用缓存。
- [x] 中文覆盖、中英对照、侧边栏三种模式可运行时切换。
- [x] Overlay Topmost、无边框、不抢焦点、鼠标穿透，并跟随区域。
- [x] 区域丢失隐藏 Overlay。
- [x] Pause 保留预览，Resume 恢复同一状态。
- [x] Stop 取消 OCR/翻译任务、隐藏 Overlay、停止捕获、清空预览并回到 Idle。
- [x] 全链路无在线 API、无截图上传、无自动模型下载。
- [x] Release 自包含，可在断网环境运行。
- [x] Protected Core SHA256 全部匹配。
- [x] 600 秒捕获基线保持 ≥20 FPS，内存进入平台区。
- [ ] Microsoft PowerPoint 实机兼容验收：本机未安装，仅完成 WPS 实测。
- [ ] 广泛自由文本的产品级神经翻译质量：当前 Fast 包是有限短语/术语模型。

Phase 3 到此停止，不进入 PDF、网页、插件或下一阶段。

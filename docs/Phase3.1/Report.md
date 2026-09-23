# Phase 3.1：离线整句英译中升级报告

日期：2026-09-13  
版本：0.3.1  
范围：只替换翻译能力并完善相应状态、缓存、校验和测试；没有新增 PPT 检测、PDF 解析、网络服务或在线 API。

## 结论

原来的 `LocalPhraseTranslationEngine` 只对少量短语和词汇做替换，未命中文本保留英文，因此实际页面经常只能翻出几个词。当前主流程已切换为 `MarianOnnxTranslationEngine`，使用随程序打包的 MarianMT / OPUS-MT English → Chinese 量化 ONNX 编码器和解码器。十组未写入短语表的普通英文句子全部生成了完整中文，真实 Windows OCR 读取的三行英文页面也全部进入神经翻译。

程序运行时不下载模型、不创建 HTTP 客户端、不访问翻译服务器。第一次点击“开始识别”时在后台校验并加载本地模型；程序启动后仍保持待机，不会自动捕获屏幕。

## Windows Smart App Control 兼容修复

首次多文件发布在用户点击“开始识别”并延迟加载分词器时，被 Windows Smart App Control 拦截。Code Integrity 事件 3077/3033 明确指向未签名的 `Microsoft.ML.Tokenizers.dll`，与屏幕捕获权限无关。

正式发布现已改为 .NET 自包含单文件模式：应用及未签名的托管依赖嵌入 `ScreenTranslator.exe`，签名有效的原生 `onnxruntime.dll` 和模型数据保持外置。正式目录中不再存在 `Microsoft.ML.Tokenizers.dll` 或 `Google.Protobuf.dll`。在 `C:\翻译器` 相同策略位置运行内置离线翻译自检，退出码为 0，实际输出“离线翻译引擎已经就绪”，之后 Code Integrity 日志没有出现候选版本的拦截事件。

## 实现

- `MarianOnnxTranslationEngine`：ONNX Runtime CPU 推理、贪心解码、取消、模型生命周期和完整性错误。
- `MarianTokenizerAdapter`：读取 `source.spm` / `target.spm`，将 SentencePiece piece 映射到 Marian 的 65,001 项联合词表。
- `TranslationTextSegmenter`：按句号和长度拆分长 OCR 文本，避免超过模型上下文。
- `TranslationMemoryCache`：512 项有界 LRU 内存缓存；重复页面和重复句子不再次推理。
- 旧短语表只保留明确术语和验收短语的精确快速路径，不再承担普通句子的翻译。
- 模型目录 `ModelsData/Translation/en-zh-neural` 包含权重、分词器、词表、来源、许可证和逐文件 SHA256 清单。

## 自由文本实测

| 英文 | 实际中文 |
|---|---|
| The meeting starts at nine o'clock tomorrow morning. | 明早九点开会。 |
| Please review the quarterly sales report before Friday. | 请在星期五前审查季度销售报告。 |
| Our new system improves accuracy and reduces processing time. | 我们的新系统提高了准确性，减少了处理时间。 |
| Students can access the course materials from any device. | 学生可以从任何设备上获取教材。 |
| This chart shows the growth of renewable energy in Asia. | 本图显示了亚洲可再生能源的增长。 |
| Data privacy is essential for every organization. | 数据隐私对每个组织都至关重要。 |
| The project team completed all major tasks on schedule. | 项目小组按时完成了所有主要任务。 |
| Artificial intelligence helps people understand complex information quickly. | 人工智能能帮助人们快速理解复杂的信息。 |
| Customer feedback helps us improve product quality. | 客户反馈有助于我们提高产品质量。 |
| Turn off the computer after the presentation has finished. | 演示文稿完成后关闭计算机。 |

原始逐句输出和单句耗时：`data/translation-acceptance.txt`。

## OCR 到翻译端到端实测

测试程序用 WPF 渲染一张包含三行英文的 1400 × 620 页面，交给真实 `Windows.Media.Ocr`，再把每个 OCR block 交给当前神经翻译引擎。结果：

```text
Building Reliable Offline Software => 建设可靠的离线软件
The system processes every image on this computer. => 系统处理电脑上的所有图像。
Clear information helps teams make better decisions. => 明确的信息有助于各小组作出更好的决定。
```

三行均被 OCR 找到，三行均生成中文；没有使用 Fake OCR 或 Fake Translation。原始结果：`data/rendered-slide-ocr-translation.txt`。

当前会话的 Windows Control 应用表面已关闭，所以本次没有重新自动操作 WPS 并生成新的实机截图。Phase 3 已保存的 WPS 编辑模式、全屏放映模式、绿色框、IoU、置信度和 OCR 结果仍适用，因为本次没有改动 Capture、Vision 或 OCR 文件。新翻译层由上面的真实 Windows OCR 页面测试覆盖。

## 性能

测试环境：Windows 11 预览版本 10.0.26200、x64、CPU 推理、量化模型。

| 指标 | 结果 |
|---|---:|
| 冷初始化（含 SHA256 校验、分词器和两个 ONNX Session） | 1082.5 ms |
| 10 句平均翻译 | 190.3 ms / 句 |
| 最快 | 159.1 ms |
| 最慢 | 223.5 ms |
| 初始化前 Private Bytes | 159.6 MB |
| 初始化后 Private Bytes | 356.4 MB |
| 重复文本 | 0 ms，命中内存缓存 |
| 模型与分词资源 | 约 117 MB |
| 单文件发布包总大小 | 约 318 MB |

翻译模型常驻带来约 197 MB Private Bytes 增量，属于 ONNX Session、量化权重和运行时工作区，不是逐句增长。30 秒真实屏幕捕获回归期间进程 Private Bytes 三次采样为 587.57、582.86、587.00 MB，没有单向上涨。

## Capture 回归

在正常交互式 Windows 桌面会话中重新运行 30 秒真实 WGC 回归：

- 原生捕获分辨率：2560 × 1440，Windows 缩放 150%。
- 实际 Preview FPS 平均 28.83，最低十秒区间 28.54。
- Start / Pause / Resume / Stop 全部通过。
- Pause 保留预览且 FPS 回到 0。
- Stop 清空预览并回到 Idle。
- 8 次 Restart 全部通过。
- 125% / 150% / 175% DPI，1100 × 700 和 1400 × 900 五个页面均无交互控件水平溢出。

原始数据：`data/capture-regression-30s.json`。

## 单元与集成检查

本次 Phase 3.1 检查全部通过：

- Windows OCR 真实文字读取与坐标。
- 10 组自由英文整句翻译。
- 3 行渲染页面真实 OCR → 神经翻译。
- 精确术语一致性。
- 翻译缓存。
- 取消操作。
- 模型缺失错误。
- 模型 SHA256 损坏拒绝。
- 标题与双栏阅读顺序。
- OCR 纠错和断词合并。
- 页面稳定防抖、页面变化和页面缓存。
- Stop 取消活动 OCR 并隐藏 Overlay。

完整检查：`data/phase31-unit-summary.txt`。

## Protected Core

以下九个文件与 Phase 3 基线逐文件 SHA256 一致，`AllMatch = true`：

- `Capture/CapturedFrame.cs`
- `Capture/IScreenCaptureService.cs`
- `Capture/MonitorInfo.cs`
- `Capture/MonitorService.cs`
- `Capture/PixelCopy.cs`
- `Capture/ScreenCaptureService.cs`
- `Capture/WindowsGraphicsCaptureSession.cs`
- `Capture/WinRtCaptureInterop.cs`
- `Core/ApplicationState.cs`

原始校验结果：`data/protected-core-sha256.json`。

## 已知边界

- 当前是量化 MarianMT 小型通用模型。普通课件句子覆盖明显优于短语表，但专业术语、极长句、OCR 严重错字和复杂表格仍可能出现译法不理想。
- 解码采用 CPU 贪心策略以保持屏幕翻译延迟；没有加入更慢的四路 beam search。
- 当前语言方向是 English → Simplified Chinese。
- 本次没有进入 PDF/PPTX 文件解析、在线模型下载、Overlay 新模式或 Phase 2 检测算法修改。

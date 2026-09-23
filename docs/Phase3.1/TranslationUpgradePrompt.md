# ScreenTranslator Phase 3.1 — 真正的离线英译中模型升级提示词

你是一名资深 Windows 桌面软件工程师、机器翻译工程师、ONNX Runtime 工程师和系统架构师。

继续开发现有项目：

```text
C:\翻译器
```

如果开发环境使用工作副本，应在验证完成后把最终结果同步回：

```text
C:\翻译器
```

不要重新创建 Solution，不要删除现有项目，不要重写已经通过测试的 Windows Graphics Capture、显示器枚举、PPT/WPS 区域检测、OCR、页面指纹、缓存或 Overlay 基础架构。

当前已经完成：

- C#、.NET 10、WPF 产品 UI。
- Windows Graphics Capture + Direct3D 11。
- 多显示器、Start、Pause、Resume、Stop、FPS、Preview。
- PowerPoint/WPS PresentationRegion 自动检测。
- Windows.Media.Ocr 离线英文 OCR。
- SlideFingerprint、页面稳定防抖和 LRU 页面缓存。
- 中文覆盖、中英对照、侧边栏 Overlay。
- `IOcrEngine`、`ITranslationEngine`、`ITranslationOverlay` 接口。

当前问题：

```text
OCR 已经识别出完整英文时，程序通常只翻译 Artificial Intelligence、
Machine Learning、Neural Network 等少数预设短语；普通英文句子大量原样返回。
```

原因是当前 `LocalPhraseTranslationEngine` 加载的语言包只有几 KB，本质上是验收用短语和术语表，不是真正的通用机器翻译模型。

本任务只解决“英文识别后无法完整翻译成中文”。不要在本任务中重构屏幕捕获、PPT Region Detection 或 OCR 完整度；OCR 漏字属于后续独立任务。测试翻译引擎时可以直接输入完整英文字符串，避免把 OCR 问题混入翻译验收。

## 一、最终目标

把当前短语表翻译实现升级为真正的本地 English → Simplified Chinese 神经机器翻译语言包。

目标运行链路：

```text
OCRBlock.OriginalText
→ 文本规范化
→ 术语占位保护
→ SentencePiece / Marian Tokenizer
→ 本地 Encoder ONNX
→ 本地 Decoder ONNX 自回归生成
→ 中文解码
→ 术语恢复
→ TranslationResponse
```

必须支持普通完整英文句子，而不是仅依赖硬编码字典。例如下面这些句子必须产生有意义的中文：

```text
Artificial intelligence is changing the way people work and learn.
Machine learning models can recognize patterns in large amounts of data.
This course introduces neural networks and their practical applications.
The system processes all screen content locally and does not upload screenshots.
Please review the key findings before the next presentation.
```

翻译结果不能把整句英文原样返回，也不能只替换一两个术语后保留大部分英文。

## 二、100% 离线要求

产品运行时禁止：

- GPT、Claude、Gemini 或任何大模型 API。
- Google、DeepL、Microsoft、百度或其他在线翻译接口。
- 上传 OCR 文本、截图或翻译内容。
- 后台联网下载模型。
- Python 作为产品运行时。
- 启动本地 HTTP 服务来桥接翻译。

开发阶段允许从模型作者或可信模型仓库下载公开模型文件和构建依赖，但必须：

- 记录来源 URL、版本、许可证、原始文件 SHA256。
- 下载后随本地语言包发布。
- 完成发布后断网仍可运行。
- 不执行仓库内未知脚本或模型卡中的任意命令。

## 三、模型选择

首选模型：

```text
Helsinki-NLP/opus-mt-en-zh
```

首选部署形式：

```text
Marian Encoder/Decoder ONNX，CPU INT8 或动态量化版本
```

可使用可信的 ONNX 转换版本，但必须核对它与原模型一致，并保留原模型 Apache-2.0 许可证及转换来源。建议语言包至少包含：

```text
ModelsData/Translation/en-zh-neural/
├─ metadata.json
├─ config.json
├─ LICENSE.txt
├─ NOTICE.md
├─ tokenizer.json 或 source.spm / target.spm / vocab.json
├─ encoder_model_quantized.onnx
└─ decoder_model_quantized.onnx
```

若现成 ONNX 模型提供 merged decoder 或 decoder-with-past，可在确认输入输出稳定后使用。先保证 greedy decoding 正确，再考虑 beam search 和 KV cache。不得为了追求 GPU 而阻塞 CPU 可用版本。

## 四、运行时技术路线

优先使用：

```text
Microsoft.ML.OnnxRuntime
```

第一版必须在 x64 CPU 上工作。`InferenceDevice.Auto` 和 `Cpu` 走 CPU Execution Provider。GPU 路径可以在同一接口中保留；如果 DirectML/WinML 初始化失败，必须记录一次日志并自动回落 CPU，不能让 Start 崩溃。

ONNX Runtime Session 必须：

- Start 初始化时创建一次，不得每个 OCRBlock 重建。
- Stop 可以取消正在进行的翻译生成。
- Dispose 时释放 Encoder、Decoder、输入张量和临时结果。
- 不允许多个线程同时操作不支持并发的同一 GPU Session。
- 对每次生成设置最大输入和最大输出 token，防止异常内存增长。

## 五、Tokenizer

实现与 Marian 模型匹配的 SentencePiece/Tokenizer。优先读取语言包中的 tokenizer 配置，而不是在 C# 中硬编码整个词表。

要求：

- 支持 Unicode、标点、数字和英文缩写。
- 正确添加模型要求的 decoder start token、EOS 和 pad token。
- 正确处理 English → Simplified Chinese 目标语言 token。
- 输入超过限制时按句子或语义块拆分，禁止在 UTF-16 代理项或英文单词中间粗暴截断。
- 解码后移除特殊 token、SentencePiece 空格标记和重复标点。
- 为 tokenizer 写固定输入输出测试。

如果使用 `tokenizer.json`，应实现或使用可再分发的本地 tokenizer 库；如果增加 NuGet 包，必须核对许可证、锁定版本并加入 ThirdPartyNotices。不要调用外部 Python 进程进行运行时分词。

## 六、Translation Engine 结构

保留已有接口：

```csharp
public interface ITranslationEngine : IAsyncDisposable
{
    string Name { get; }
    bool IsInitialized { get; }
    LanguagePackMetadata? LanguagePack { get; }
    Task InitializeAsync(InferenceDevice preferredDevice, CancellationToken cancellationToken);
    Task<TranslationResponse> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}
```

新增真正的模型实现，例如：

```text
MarianOnnxTranslationEngine
MarianTokenizer
MarianGreedyDecoder
TranslationTextSegmenter
```

当前 `LocalPhraseTranslationEngine` 不再作为普通文本主引擎。它可以保留为：

- 术语快速路径。
- 神经模型缺失时明确报错前的诊断工具。
- 单元测试 Fake。

不得在语言包缺失时悄悄退回几 KB 字典并让 UI 显示“翻译成功”。缺少神经模型时应显示：

```text
English → Chinese 神经语言包未安装或损坏
```

## 七、术语一致性

保留 `TerminologyManager`，但将其用于模型前后处理：

```text
Artificial Intelligence → 人工智能
Machine Learning → 机器学习
Neural Network → 神经网络
```

优先使用不会被 tokenizer 拆坏的占位策略。翻译后恢复占位符；如果模型改变占位符格式，要有可靠的恢复逻辑。不能仅仅在模型输出后做全局字符串替换而破坏普通句子。

同一 PPT 中相同术语应保持一致。

## 八、句子、OCRBlock 与上下文

不要把整页文本无条件拼成一段，也不要把每个很短的 OCR 行完全孤立翻译。

规则：

- 标题可单独翻译。
- 同一 Bullet 内因 OCR 换行产生的相邻行应先合并。
- 同一段落的连续短行应合并后翻译，再映射回 Overlay。
- 双栏内容不得跨栏合并。
- 表格单元格保持独立。
- `TranslationContext` 继续携带 SlideTitle、PreviousBlocks、KnownTerms 和 CurrentSlideTopic。
- 第一版 Marian 模型如果不能直接利用上下文，可使用标题作为短前缀或通过段落合并提高连贯性，但不得把标题内容重复显示到译文中。

## 九、缓存

继续使用 SlideFingerprint 页面缓存，并增加文本级翻译缓存：

```text
Key = ModelId + ModelVersion + SourceLanguage + TargetLanguage
      + NormalizedText + TerminologyVersion
```

要求：

- 相同英文不重复运行模型。
- 模型或术语版本变化时自动失效。
- 有界 LRU，禁止无限增长。
- Stop 不必清空有效缓存，但必须取消当前生成。
- 缓存统计应区分页面缓存和文本翻译缓存。

## 十、状态和 UI

应用启动仍然必须 Idle，不加载模型、不捕获屏幕。

点击“开始识别”后：

```text
正在验证 English → Chinese 神经语言包…
正在加载离线翻译模型…
离线翻译模型已就绪
```

语言包页面显示：

- English → 中文。
- Installed / Missing / Invalid。
- Offline。
- Neural Marian ONNX。
- 版本。
- 模型大小。
- CPU / GPU 实际运行设备。
- 安装路径。

如果模型缺失或 SHA256 不匹配，Start 进入可恢复错误状态，不启动 Capture；错误信息应对普通用户可读，详细路径和异常写日志。

## 十一、Stop 优先级

Stop 必须优先于页面翻译。用户点击停止后必须：

- Cancel 当前 Encoder/Decoder 生成循环。
- 不发布停止后完成的迟到译文。
- 隐藏 Overlay。
- 停止 OCR、Vision 和 Capture。
- 释放当前输入张量和 ONNX 输出对象。
- State 返回 Idle。

增加一个阻塞 Decoder Fake，验证 Stop 可以取消生成并丢弃迟到结果。

## 十二、性能和内存

目标硬件：普通 Windows 11 x64 笔记本。

目标：

- 模型只加载一次。
- 单个常见英文句子 CPU 翻译目标小于 1 秒。
- 一页常见 PPT 的翻译目标小于 2 秒。
- 页面缓存恢复小于 100 ms。
- 每次推理及时 Dispose `OrtValue`、`NamedOnnxValue`、`IDisposableReadOnlyCollection`。
- 连续翻页测试后私有内存和句柄进入平台区。
- 不降低 Capture FPS，不让 UI Thread 执行模型推理。

如果 greedy decoding 的质量不足，再增加 beam size 2；不要一开始使用 beam 6 造成明显等待。

## 十三、测试语料

建立独立翻译测试集，至少覆盖：

```text
Artificial intelligence is changing the way people work and learn.
Machine learning models can recognize patterns in large amounts of data.
This course introduces neural networks and their practical applications.
The system processes all screen content locally and does not upload screenshots.
Please review the key findings before the next presentation.
Our results demonstrate significant improvements in accuracy and efficiency.
Students should complete the assignment before Friday.
The chart compares revenue growth across three different regions.
Click the button to save your changes.
No internet connection is required.
```

测试不能只断言“输出非空”。至少验证：

- 输出不是输入原文。
- 中文字符占比达到合理阈值。
- 关键术语正确。
- 不包含 `<pad>`、`</s>`、`▁` 等特殊标记。
- 相同输入确定性一致。
- 缓存命中不再次调用 Decoder。
- 长句分段后顺序正确。
- CancellationToken 可以终止生成。
- 模型文件缺失和损坏时给出正确错误。

创建真实 WPS 测试页，放置两到三段普通英文句子，验证最终 UI 显示完整中文，而不是只翻译个别术语。

## 十四、Protected Core

以下文件属于受保护捕获核心，不允许修改：

```text
Capture/CapturedFrame.cs
Capture/IScreenCaptureService.cs
Capture/MonitorInfo.cs
Capture/MonitorService.cs
Capture/PixelCopy.cs
Capture/ScreenCaptureService.cs
Capture/WindowsGraphicsCaptureSession.cs
Capture/WinRtCaptureInterop.cs
Core/ApplicationState.cs
```

修改前后计算 SHA256，必须与 `docs/Phase3/data/protected-core-sha256.json` 基线完全一致。

## 十五、开发步骤

按照以下顺序执行：

1. 分析当前 `ITranslationEngine`、`RecognitionPipeline`、语言包和测试。
2. 创建本提示词和 Phase 3.1 基线记录。
3. 选择并核验 Marian English → Chinese ONNX 文件、许可证和 SHA256。
4. 建立 `en-zh-neural` 语言包目录和严格的 manifest 校验。
5. 实现本地 tokenizer，并先通过 tokenizer 测试。
6. 实现 Encoder 和 greedy Decoder 的纯离线推理。
7. 接入术语保护、分段、文本缓存和取消。
8. 通过 `ITranslationEngine` 替换短语表主引擎。
9. 更新语言包页、状态和错误信息。
10. 运行独立字符串翻译测试。
11. 运行 WPS 编辑模式真实翻译测试。
12. 运行 Start/Pause/Resume/Stop、缓存和 Capture 回归。
13. 进行内存、延迟和断网测试。
14. 核验 Protected Core SHA256。
15. 发布自包含 Release，并同步到 `C:\翻译器`。

每个步骤记录：

- 新增和修改文件。
- 为什么修改。
- 测试方法和结果。
- 当前风险或限制。
- Protected Core 是否保持不变。

## 十六、禁止做法

- 不要通过增加几十条硬编码词典冒充机器翻译。
- 不要调用在线翻译作为“临时回退”。
- 不要把 Python、Node.js 或浏览器运行时打包为主翻译服务。
- 不要每个 OCRBlock 重载模型。
- 不要在 UI Thread 执行 Encoder/Decoder。
- 不要吞掉模型损坏、tokenizer 不匹配或输出异常。
- 不要伪造翻译截图、性能或测试结果。
- 不要修改 Protected Capture Core 来迁就翻译代码。
- 不要进入 OCR 检测模型、PDF、网页、插件或安装器开发。

## 十七、最终验收

只有满足以下条件才算完成：

- 普通完整英文句子可以翻译为完整中文。
- 不再依赖 4 KB 短语表作为主翻译能力。
- 语言包及模型全部保存在本地。
- 拔掉网络后仍能初始化和翻译。
- WPS 页面真实 OCR 文本能进入神经翻译模型。
- 页面静止不重复翻译。
- 返回上一页优先使用缓存。
- Stop 可以取消正在进行的生成并隐藏 Overlay。
- UI 不冻结，Capture FPS 保持正常。
- Protected Core SHA256 全部匹配。

最终报告必须提供：

1. 实际模型名称、来源、许可证、文件大小和 SHA256。
2. Tokenizer、Encoder、Decoder、术语和缓存说明。
3. 十条普通英文测试及实际中文输出。
4. WPS 编辑模式实际截图。
5. 首次翻译、整页翻译和缓存命中性能。
6. Stop/Cancellation 测试结果。
7. Capture Regression 结果。
8. Protected Core SHA256 结果。
9. 新增和修改文件清单。
10. 最终运行方法和已知限制。

完成后停止，不进入 OCR 完整度修复或其他新阶段。

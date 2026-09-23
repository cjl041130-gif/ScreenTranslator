# ScreenTranslator Phase 2 / AI OCR 最终验收报告

生成日期：2026-09-19

## 最终结论

- Phase 2 的 PPT/WPS 文档区域检测、编辑模式与全屏模式已完成真实运行验收。
- AI 精译已成为默认翻译方式，使用 DeepSeek Chat Completions API 进行整页结构化翻译。
- 屏幕捕获、页面检测和 OCR 均在本机执行；AI 请求只发送 OCR 文字块与排版元数据，不发送截图。
- PaddleOCR v5 ONNX 为主 OCR，Windows OCR 仅用于初始化失败或低置信度补救。
- API Key 保存在 Windows Credential Manager，不写入配置文件、日志或截图。
- Capture Core 六个受保护文件的当前 SHA256 与基线完全一致。
- Release self-contained 发布成功，0 警告、0 错误。
- 本机 Smart App Control 已关闭，最新 600 秒运行回归和 WPS 实机验收均已通过。
- 仍待用户在软件设置页自行配置 DeepSeek API Key 后完成真实网络翻译截图与耗时验收；正式对外发布仍建议签署可信代码签名。

## 实际绿色框截图

### WPS 编辑模式

![WPS 编辑模式绿色框](evidence/wps-editor-latest.png)

- 目标窗口：`Phase3Acceptance.pptx - WPS Office`
- 检测区域：`X=533.333, Y=277.552, Width=1815.467, Height=1022.672`
- 宽高比：`1.775219`，接近 16:9。
- 现场置信度：`0.949915`，约 `94.99%`。
- 单次检测延迟：`27.688 ms`。
- 10 秒内采集 38 个区域样本，区域稳定 IoU：`1.000000`。
- Phase 2 同算法、同 16:9 布局的人工标注空间 IoU：`0.999060`。本次最新截图未重新人工描框，因此该值不冒充本次的新人工标注结果。

原始桌面帧：![WPS 编辑模式原始桌面](evidence/wps-editor-source-latest.png)

### WPS 全屏模式

![WPS 全屏模式绿色框](evidence/wps-fullscreen-latest.png)

- 目标窗口：`WPS Presentation Slide Show - [Phase3Acceptance.pptx]`
- 检测区域：`X=0, Y=0, Width=2560, Height=1440`。
- 人工真值与检测区域均为整个 2560×1440 放映画面，空间 IoU：`1.000000`。
- 现场置信度：`0.908836`，约 `90.88%`。
- 检测方式：`Fullscreen uniform-border scan`。
- 单次检测延迟：`200.711 ms`。
- 10 秒内采集 38 个区域样本，稳定 IoU：`1.000000`，X/Y/宽/高标准差均为 0。
- CPU 占全部核心：`0.506%`。

原始桌面帧：![WPS 全屏模式原始桌面](evidence/wps-fullscreen-source-latest.png)

全屏画面静止时 Windows Graphics Capture 不持续发送新变化帧，因此该轮 Preview FPS / Vision FPS 显示为 0；检测器仍成功产生 38 个稳定样本。这是静态画面的按需帧行为，不是捕获失败。动态桌面的 600 秒 FPS 结果见下文。

## OCR 验收

| 指标 | 结果 |
|---|---:|
| CER | 2.147% |
| WER | 8.602% |
| 文字行召回率 | 100% |
| 平均模型置信度 | 0.9721 |
| 平均 OCR 耗时 | 5002.9 ms |
| 最快 / 最慢 | 2704.8 / 10680.6 ms |
| OCR 基准过程内存变化 | +132.4 MB |
| 模型加载 | 636.5 ms |

测试集覆盖 WPS 编辑、WPS 全屏、浏览器课件、PDF 小字、1080p、2K、4K、100%/125%/150%、明暗背景、标题、列表和双栏。综合基准 5 个场景全部检出完整文字行；字符错误主要是少量相似字母和标点，不再是只识别个别单词。

## AI 翻译实现与测试

- 默认配置：`Ai / DeepSeek / deepseek-flash / https://api.deepseek.com/chat/completions`。
- 页面稳定且本地 OCR 得到有效英文后才允许请求网络。
- 每页只发起一个结构化请求，包含全部 Block ID、类型、阅读顺序和 BoundingBox。
- 使用 DeepSeek JSON Output；本地严格校验缺失、重复、未知 ID、空结果、异常英文残留、Markdown 和解释性文字。
- 首次校验失败只允许一次严格修复请求；第二次失败不显示覆盖层、不缓存、不回退到旧机器翻译。
- 401 不重试；429 与 5xx 最多重试两次；支持超时、暂停、停止、页面切换和程序退出取消。
- 内存 LRU 使用 SHA256 Key；相同页面、模型和翻译风格命中缓存时不重复请求。
- 自动测试使用 Fake `IAiTranslationClient`，没有调用真实付费 API。
- DeepSeek/AI/OCR 自动测试：`76 PASS，0 FAIL`。
- 修复连接测试使用空矩形时产生非有限坐标、导致 JSON 序列化失败的问题；所有发送坐标现会在本地转换为有限数值。
- “测试连接”增加 15 秒总超时，并对网络超时和未知异常显示明确状态。
- DeepSeek 请求契约已验证：官方 Chat Completions 地址、`deepseek-flash`、非思考模式、JSON 输出、整页系统/用户消息、响应与 Token 解析。
- 真实捕获与 UI 回归：`81 PASS，0 FAIL`；平均 FPS `28.898`，最低采样区间 `28.856`。

## 600 秒 Capture Regression

| 指标 | 结果 |
|---|---:|
| 检查数 | 142 PASS，0 FAIL |
| 平均真实预览 FPS | 28.367 |
| 最低 10 秒区间 FPS | 27.565 |
| 内存平台基线 | 491.2 MB |
| 结束内存 | 509.9 MB |
| 平台变化 | +18.7 MB（阈值 64 MB） |
| 句柄平台基线 | 1290.7 |
| 结束句柄 | 1278.3 |
| 句柄变化 | -12.4（阈值 30） |
| Start / Pause / Resume / Stop | 通过 |
| 8 次 Restart | 通过 |
| Preview / 资源释放 / UI 状态 | 通过 |
| DPI 125% / 150% / 175% | 1100×700 与 1400×900 均通过 |

关闭 ONNX Runtime CPU memory arena 与 memory pattern 后，10 分钟运行内存稳定在约 510 MB，未出现持续上涨。此前约 2.84 GB 的高平台问题已消除。

## 最新产品界面证据

### 启动待机状态

![首页待机状态](evidence/home-idle-latest.png)

### 设置页面

![DeepSeek 设置页面](evidence/settings-deepseek.png)

### 运行中实时识别页面

![运行中实时识别页面](evidence/recognition-running-latest.png)

## Protected Capture Core SHA256

| 文件 | SHA256 | 状态 |
|---|---|---|
| `Capture/IScreenCaptureService.cs` | `3E75FD26ED43B8F18B559AEEE7432BF2335EF77564F92F39D55EB97919D3948C` | 未修改 |
| `Capture/ScreenCaptureService.cs` | `880BF19AB7AA4350B7126E7A59AF5DF6EBC2661E7E225B33F6395C25850942DC` | 未修改 |
| `Capture/WindowsGraphicsCaptureSession.cs` | `FA4ACA4C6E115B4C49AC4CB6CA0153DACD650F19A0BBDD5BF1E6A2D63B4E7C9B` | 未修改 |
| `Capture/MonitorService.cs` | `4576C435E7BBE7F163BA423403A8BAD23E19AF6A3ADD1CECDD6E8C1251042E52` | 未修改 |
| `Capture/CapturedFrame.cs` | `6B680A8ABBC95B822E9CFC6018D6AB6653136F4CD10B64325B90EF9489E54B64` | 未修改 |
| `Capture/MonitorInfo.cs` | `769182E7B77A9E0F8A7311BD0810A9C8045DF7DC259A5D0739848395FAF41BB7` | 未修改 |

## 主要新增文件

- `Translation/AI/AiTranslationOptions.cs`
- `Translation/AI/AiTranslationContracts.cs`
- `Translation/AI/AiTranslationEngine.cs`
- `Translation/AI/DeepSeekTranslationClient.cs`
- `Translation/AI/AiTranslationResultValidator.cs`
- `Translation/AI/AiTranslationCache.cs`
- `Translation/Security/IApiKeyStore.cs`
- `Translation/Security/WindowsApiKeyStore.cs`
- `OCR/OcrSettings.cs`
- `OCR/PaddleOcrOnnxEngine.cs`
- `OCR/AdaptiveOcrEngine.cs`
- `App/PasswordBoxBinding.cs`
- `Tests/AiTranslationTests.cs`
- `Tests/OcrAccuracyTests.cs`
- `ModelsData/OCR/PP-OCRv5_server_det/*`
- `ModelsData/OCR/en_PP-OCRv5_mobile_rec/*`

## 主要修改文件

- `Services/AppSettings.cs`
- `Translation/ITranslationEngine.cs`
- `Models/RecognitionModels.cs`
- `OCR/WindowsOcrEngine.cs`
- `Recognition/RecognitionPipeline.cs`
- `App/MainViewModel.cs`
- `App/MainWindow.xaml`
- `App/ScreenTranslator.csproj`
- `App/Views/HomeView.xaml`
- `App/Views/RecognitionView.xaml`
- `App/Views/DocumentView.xaml`
- `App/Views/LanguagePackView.xaml`
- `App/Views/SettingsView.xaml`
- `Tests/Phase3Tests.cs`
- `Tests/Program.cs`
- `appsettings.json`

## 发布与运行

- 启动文件：`C:\翻译器\Release\DeepSeek-v0.3.3-20260919\ScreenTranslator.exe`
- self-contained 发布目录：42 个文件，约 390.1 MB。
- EXE SHA256：`8F2C5E17A453749976C0FC76F5A45924BEBFCF13A309CDA19DB3196EFB2F0F31`。
- 当前产物未使用受信任 CA 的代码签名证书签名。

## 证据数据

- `data/ai-ocr-checks-latest.txt`
- `data/ocr-accuracy-report-latest.json`
- `data/paddle-ocr-smoke-latest.json`
- `data/capture-regression-600s-result.json`
- `data/capture-regression-600s-checks.txt`
- `data/capture-regression-600s-samples-latest.csv`
- `data/wps-editor-detection-latest.json`
- `data/wps-editor-performance-latest.json`
- `data/wps-fullscreen-detection-latest.json`
- `data/wps-fullscreen-performance-latest.json`
- `data/protected-core-sha256.json`
- `data/deepseek-ai-ocr-checks.txt`
- `data/deepseek-capture-checks.txt`
- `data/deepseek-capture-result.json`
- `data/deepseek-connection-fix-checks.txt`

## 尚待真实 AI 联网验收

1. 用户在设置页自行填写 DeepSeek API Key，并点击“测试连接”。密钥不要发到聊天中。
2. 取得 AI 翻译中、AI 翻译完成两张真实截图。
3. 记录实际网络条件下的 AI 请求耗时与端到端耗时。
4. 正式公开分发前完成可信代码签名。

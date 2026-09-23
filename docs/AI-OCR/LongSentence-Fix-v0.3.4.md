# ScreenTranslator v0.3.4：真实 WPS 长句与延迟修复报告

日期：2026-09-20

## 复现结论

用户报告的两个问题均已在真实 WPS 演示 `Lab 2.pptx` 中复现。

1. 旧版对普通 1080p/2K 页面仍按 1408 像素切片，导致 PaddleOCR 对同一页执行多次检测。历史实际运行记录中，一页 488 个字符耗时为 OCR 3836.7 ms、DeepSeek 5334.3 ms、总计 9172.8 ms。
2. PaddleOCR 按视觉行返回文字。旧流程把每一行作为独立 ID 交给 DeepSeek，因此跨两行的长句会被分别翻译并显示为互不相连的中文块。
3. 短产品名保留英文时，旧校验器可能误判整页翻译无效，并触发第二次整页请求。

## 修复内容

- 新增 `OcrParagraphMerger`，在翻译前按阅读顺序、行距、水平重叠、文本类型和句末状态合并同一段落的视觉行。
- 保护标题、双栏、项目符号、代码、公式、表格、页脚等边界。
- 支持页顶大字号正文被几何规则误标为标题时的安全续行合并。
- 1826×1025 等普通页面使用单次 PaddleOCR 检测；长边超过 3000 像素时才启用切片。
- 短产品名或专有名词保留英文时不再触发整页修复请求；较长的未翻译英文仍会被拒绝。
- DeepSeek 提示明确要求把已合并段落作为连续语义翻译。

## 真实 WPS 测试

测试窗口：`Lab 2.pptx - WPS Office`，第 5 页 `TOKENIZATION`。

- 窗口捕获：2560×1439
- 检测页面：1826×1025
- 页面检测置信度：0.93635
- 原始 OCR 视觉行：12
- 翻译文字块：11
- 合并的跨行段落：1
- 最长原始行：73 字符
- 合并后最长段落：110 字符
- OCR：2661.4 ms
- DeepSeek V4 Pro：4283.4 ms
- OCR + 翻译：6944.9 ms
- DeepSeek 返回块：11/11
- 长段落中文连续性检查：通过

绿色大框表示页面区域；橙色细框表示 OCR 视觉行；绿色文字框表示交给翻译引擎的长文字块。第一段两行英文已形成一个统一绿色段落框。

## 自动回归

- AI / OCR / 段落 / 生命周期：83 PASS，0 FAIL
- Windows Graphics Capture / UI / DPI：80 PASS，0 FAIL
- 15 秒捕获平均 FPS：29.11
- 最低采样 FPS：28.70
- Start / Pause / Resume / Stop：通过
- 8 次 Restart：通过
- 125% / 150% / 175% DPI：通过
- PaddleOCR 字符错误率：1.84%
- PaddleOCR 词错误率：6.45%
- PaddleOCR 行召回率：100%

## Protected Capture Core SHA256

以下文件未修改，哈希与修改前一致：

- `Capture/IScreenCaptureService.cs`: `3E75FD26ED43B8F18B559AEEE7432BF2335EF77564F92F39D55EB97919D3948C`
- `Capture/ScreenCaptureService.cs`: `880BF19AB7AA4350B7126E7A59AF5DF6EBC2661E7E225B33F6395C25850942DC`
- `Capture/WindowsGraphicsCaptureSession.cs`: `FA4ACA4C6E115B4C49AC4CB6CA0153DACD650F19A0BBDD5BF1E6A2D63B4E7C9B`
- `Capture/MonitorService.cs`: `4576C435E7BBE7F163BA423403A8BAD23E19AF6A3ADD1CECDD6E8C1251042E52`
- `Capture/CapturedFrame.cs`: `6B680A8ABBC95B822E9CFC6018D6AB6653136F4CD10B64325B90EF9489E54B64`
- `Capture/MonitorInfo.cs`: `769182E7B77A9E0F8A7311BD0810A9C8045DF7DC259A5D0739848395FAF41BB7`

## 发布文件

- 目录：`C:\翻译器\Release\DeepSeek-v0.3.4-20260920`
- EXE SHA256：`0C83E6CC7F42E26B4F2FC18DE89426354D51F42AE091D2A7B67FA8E5E9D0B7D9`
- 真实 WPS 证据：`C:\翻译器\docs\AI-OCR\evidence-v0.3.4-20260920`

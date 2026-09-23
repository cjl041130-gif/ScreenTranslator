# ScreenTranslator v0.3.6 — 网页、浏览器 PDF 与在线文档识别修复

## 结果

本次修复使网页、浏览器内 PDF、常见 PDF 阅读器和飞书在线文档能够进入现有屏幕 OCR 与 AI 翻译链路。Windows Graphics Capture 后端未修改。

## 根因

原 `TargetWindowTracker` 仅允许 `POWERPNT` 和 `wpp`。Chrome、Edge、Firefox、PDF 阅读器及飞书窗口在 OCR 前就被排除，因此界面会一直显示正在查找 PPT，OCR 实际没有收到网页像素。

另有两个放大问题：

- 网页只要被任意小浮层或任务栏交叠，就会整页拒绝识别。
- 宽网页正文被阅读顺序规则误判为标题，较短的换行被移到后面，造成句子断开。

## 修复内容

- 窗口目标新增浏览器、浏览器 PDF、独立 PDF 阅读器、飞书/Lark 支持。
- 浏览器和 PDF 使用可见正文区域作为 OCR 区域，不再要求存在 PPT 矩形边框。
- 遮挡判断改为面积阈值；小浮层不会禁用整页，大面积遮挡仍会阻止误识别。
- 前台受支持窗口优先于旧的后台目标，切换网页后不会继续识别旧窗口。
- PaddleOCR 启用时同步初始化 Windows OCR 备用引擎，低置信度回退不会再抛出“尚未初始化”。
- 网页宽行保持真实纵向顺序，并与短换行合并成完整句子。
- 修复已覆盖产品状态文案和实时识别页说明。

## 真实网页验证

测试环境：2560 × 1440，Windows 缩放 150%，真实 Google Chrome 页面，正式整屏捕获链路。

- 内容区域：2360 × 1204
- OCR 可视行：9
- 平均模型置信度：97.98%
- 最长连续英文块：112 字符
- OCR 用时：4699.5 ms（CPU，首次模型运行）
- 真实网页目标、通用正文区域、英文 OCR、长文本块均通过

证据：

- `C:\翻译器\test-results\web-ocr-live-working-20260922\web-ocr-annotated.png`
- `C:\翻译器\test-results\web-ocr-live-working-20260922\web-ocr-result.json`
- `C:\翻译器\test-results\web-ocr-live-working-20260922\checks.txt`

## 回归结果

- 网页/PDF/飞书视觉单元测试：40 PASS / 0 FAIL
- OCR、AI 翻译、缓存、翻页取消、段落测试：89 PASS / 0 FAIL
- 整屏捕获与 UI/DPI 回归：81 PASS / 0 FAIL
- 30 秒平均预览 FPS：27.76
- 最低采样 FPS：27.57
- 8 次停止/重启：全部通过

## Protected Capture Core

以下文件未修改，SHA256 与 v0.3.5 一致：

| 文件 | SHA256 |
|---|---|
| `Capture/IScreenCaptureService.cs` | `3E75FD26ED43B8F18B559AEEE7432BF2335EF77564F92F39D55EB97919D3948C` |
| `Capture/ScreenCaptureService.cs` | `880BF19AB7AA4350B7126E7A59AF5DF6EBC2661E7E225B33F6395C25850942DC` |
| `Capture/WindowsGraphicsCaptureSession.cs` | `FA4ACA4C6E115B4C49AC4CB6CA0153DACD650F19A0BBDD5BF1E6A2D63B4E7C9B` |
| `Capture/MonitorService.cs` | `4576C435E7BBE7F163BA423403A8BAD23E19AF6A3ADD1CECDD6E8C1251042E52` |
| `Capture/CapturedFrame.cs` | `6B680A8ABBC95B822E9CFC6018D6AB6653136F4CD10B64325B90EF9489E54B64` |
| `Capture/MonitorInfo.cs` | `769182E7B77A9E0F8A7311BD0810A9C8045DF7DC259A5D0739848395FAF41BB7` |

## 发布程序

`C:\翻译器\Release\DeepSeek-v0.3.6-20260922\ScreenTranslator.exe`

版本：`0.3.6.0`

EXE SHA256：`00E3287461D4CEE8CAC449757F7D912035608169B46F0A7E4AF72E12BCDE9386`


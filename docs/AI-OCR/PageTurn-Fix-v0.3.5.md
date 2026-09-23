# ScreenTranslator v0.3.5 — 翻页识别与翻译修复报告

日期：2026-09-20

## 问题与根因

1. WPS/PPT 翻页结束后，Windows Graphics Capture 在画面完全静止时可能只提交一张最终帧。旧的页面稳定判断必须再次收到相同帧才会启动 OCR，因此页面可能停留在“已识别区域”但迟迟不翻译，直到鼠标或屏幕再次变化。
2. AI 已返回大部分中文时，旧校验器会因为一个英文课程名、产品名或库名仍为英文而判定整页失败，并再发一次整页修复请求。真实日志中两次网络请求分别耗时约 6–9 秒，最终仍可能丢弃整页结果。
3. 代码演示页把大量源码逐块发送给 DeepSeek；这些内容不应该翻译，却显著增加请求长度和等待时间。

## 修复内容

- 页面稳定检测保存最新候选帧，并在稳定时间到达后主动确认。即使 WGC 没有继续提交重复桌面帧，也会启动 OCR。
- 翻页发生时继续立即取消旧页 OCR/AI 任务，防止旧页结果覆盖新页。
- 英文残留按整页比例评估。少量专有名词保留英文时接受其余中文结果；只有绝大多数有效文本仍为英文才判定失败。
- 根据文本框底色识别深色代码区，将代码和公式从 DeepSeek 请求中排除，并且不在代码上绘制空白中文覆盖框。
- 保留现有整页缓存、取消令牌、结构化 JSON 校验和资源释放机制。

## 真实 WPS 验证

测试窗口：`Lab 2.pptx - WPS Office`

- WPS 页面区域：1826 × 1025
- 区域检测置信度：0.9368458067
- PaddleOCR：29 个可视文本行，2367.15 ms
- 代码块：23 个，均未发送给 DeepSeek
- DeepSeek 请求：6 个自然语言块、61 个字符、一次请求
- DeepSeek 网络与解析：3067.94 ms
- OCR + 翻译总耗时：5435.09 ms
- 中文结果：4 个自然语言块
- 修复前同一代码页总耗时：16697.9 ms
- 修复后相对下降：约 67.5%

网络延迟仍受 DeepSeek 服务和 VPN/网络状况影响。当前使用 `deepseek-v4-pro`，正常文本页通常仍需 OCR 时间加一次 API 往返。

## 自动化测试

- Phase 3/OCR/AI 单元与集成测试：88 PASS / 0 FAIL
- Windows 捕获、MVVM 状态、8 次重启、暂停恢复、资源释放、DPI 回归：80 PASS / 0 FAIL
- 15 秒真实捕获平均预览 FPS：26.07
- 最低采样 FPS：25.75
- DPI：125%、150%、175%，1100×700 与 1400×900，横向溢出均为 0
- 专项回归通过：单张最终 WGC 帧在无鼠标移动时自动确认，并且只执行一次 OCR/翻译

## 修改文件

- `App/ScreenTranslator.csproj`
- `Recognition/RecognitionPipeline.cs`
- `Recognition/SlideFingerprint.cs`
- `OCR/PaddleOcrOnnxEngine.cs`
- `Translation/AI/AiTranslationEngine.cs`
- `Translation/AI/AiTranslationResultValidator.cs`
- `Tests/AiTranslationTests.cs`
- `Tests/Phase3Tests.cs`
- `Tests/WpsOcrLiveTests.cs`

## Protected Capture Core SHA256

下列捕获核心文件未修改，SHA256 与保护基线完全一致：

- `Capture/IScreenCaptureService.cs` — `3E75FD26ED43B8F18B559AEEE7432BF2335EF77564F92F39D55EB97919D3948C`
- `Capture/ScreenCaptureService.cs` — `880BF19AB7AA4350B7126E7A59AF5DF6EBC2661E7E225B33F6395C25850942DC`
- `Capture/WindowsGraphicsCaptureSession.cs` — `FA4ACA4C6E115B4C49AC4CB6CA0153DACD650F19A0BBDD5BF1E6A2D63B4E7C9B`
- `Capture/MonitorService.cs` — `4576C435E7BBE7F163BA423403A8BAD23E19AF6A3ADD1CECDD6E8C1251042E52`
- `Capture/CapturedFrame.cs` — `6B680A8ABBC95B822E9CFC6018D6AB6653136F4CD10B64325B90EF9489E54B64`
- `Capture/MonitorInfo.cs` — `769182E7B77A9E0F8A7311BD0810A9C8045DF7DC259A5D0739848395FAF41BB7`

## 发布入口

`C:\翻译器\Release\DeepSeek-v0.3.5-20260920\ScreenTranslator.exe`


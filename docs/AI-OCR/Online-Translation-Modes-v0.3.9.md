# ScreenTranslator v0.3.9 — 在线翻译三模式

## 用户可选模式

运行时翻译模式固定为三项：

1. DeepL（默认）。
2. DeepSeek V4 Pro。
3. DeepSeek Flash。

首页“快速设置”和设置页引用同一个选择状态。DeepSeek 模型使用固定下拉框，不接受手工模型名称。切换服务或模型时会清除旧页面翻译缓存、取消正在执行的旧请求并重新等待页面稳定，避免显示另一个引擎生成的缓存结果。

## DeepL 接入

- 使用 DeepL `/v2/translate` 文本 API。
- `:fx` Free/Developer Key 使用 `api-free.deepl.com`，其他 Key 使用 `api.deepl.com`。
- 每批最多 50 个 OCR 文字块，并保持稳定 Block ID 映射。
- English → 简体中文显式发送为 `EN` → `ZH-HANS`。
- DeepL 与 DeepSeek API Key 分别保存在 Windows 凭据管理器。
- 日志不记录 API Key 或 OCR 原文。
- 只发送 OCR 文字，不发送截图。

## 验证

- 编译：0 个警告，0 个错误。
- Phase 3：98 项通过。
- UI、捕获和 DPI：84 项通过。
- 30 秒真实捕获平均 26.59 FPS，最低采样 25.85 FPS。
- 125%、150%、175% DPI 与 1100×700、1400×900 窗口布局通过。

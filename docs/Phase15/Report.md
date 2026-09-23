# Phase 1.5 — Product UI Redesign

日期：2026-09-10。基于现有 ScreenTranslator 工程完成，没有重建项目、没有重写捕获后端。本阶段只新增和修改界面、页面 ViewModel、导航、视觉状态、手动启动配置约束及 UI 测试。

## 实际结果

- 统一浅色蓝白主题、现代标题栏、五项导航、圆角卡片和按钮；原生窗口拖动、缩放、最小化、最大化与关闭保留。
- 默认首页，待机状态；开始按钮可用，停止按钮禁用。仅用户明确执行开始命令后创建捕获会话。
- 首页和实时识别页引用同一个 MainViewModel / IScreenCaptureService。暂停保留同一张 WriteableBitmap，停止清空；切页不创建第二个采集服务。
- OCR、翻译、文档导入、覆盖模式、语言包安装和全局快捷键没有实现，入口均禁用或标注待接入。没有硬编码翻译结果，没有新增网络请求。
- AutoStart 固定 false；缺失配置、损坏配置及配置显式写 true 都不能触发自动采集。
- 详细诊断进入“实时识别 → 高级信息”，日志目录入口进入“设置 → 开发者”。调试开关不再影响产品预览。

## 实际截图

以下是测试过程中**真实 WPF 窗口内容**的 RenderTargetBitmap 渲染截图，非设计稿、非网页、非生成式图片。正常窗口截图采自本机实际 150% DPI。运行截图中的桌面来自原有 WGC 服务，递归画面是捕获自身窗口产生的正常结果。测试用动态色块用于产生实际新帧，不是 OCR 或翻译结果。

### Main Window / 首页 / 运行前

![主窗口与首页待机状态](screenshots/01-main-home-idle.png)

### 首页 / 运行中

![首页共享运行状态](screenshots/07-home-running.png)

### 实时识别 / 运行中

![真实屏幕预览和 FPS](screenshots/06-recognition-running.png)

### 设置

![设置页面](screenshots/02-settings.png)

![设置中的快捷键与开发者选项](screenshots/10-settings-advanced.png)

### 实时识别 / 运行前与暂停

![实时识别待机](screenshots/05-recognition-idle.png)

![暂停保留画面](screenshots/08-recognition-paused.png)

### 其他页面

![文档翻译占位页](screenshots/03-document.png)

![语言包占位页](screenshots/04-language-pack.png)

## Capture Regression 与 UI State Test

最终源码构建 0 警告、0 错误。测试进程退出码 0。

| 检查 | 结果 |
|---|---|
| 原有真实 GPU 帧、原生分辨率、非空像素检查 | 通过，2560×1440 |
| Start / Pause / Resume / Stop | 通过 |
| Pause 保留 Preview；FPS 归零 | 通过 |
| Stop 清空 Preview；回到待机 | 通过 |
| 连续 8 次 Restart 与 Stop | 通过 |
| Dispose 后不能重启 | 通过 |
| 真实 Window.Close 退出，无 Closing 重入异常 | 通过 |
| 初始 Start Enabled、Stop Disabled、Idle | 通过 |
| Running：Start Disabled、Pause/Stop Enabled | 通过 |
| Paused：Resume Enabled、Pause Disabled、Stop Enabled | 通过 |
| 首页与实时识别跨页面同步 | 通过，共享命令实例与服务状态 |
| 启动与闲置期间 CaptureSpy.Start 调用数 | 0 |
| 缺失/损坏/显式 true 配置的 AutoStart | 全部保持 false |
| 最终 30 秒实际预览平均 FPS | 28.27 |
| 最低 10 秒采样区间 FPS | 28.05 |

原始证据：`tests/checks.txt`、`tests/result.json`、`tests/samples.csv`。本轮是 UI 改版后的短回归，**没有将此前的 600 秒长测冒充本次长测**。原 600 秒证据仍保留在 Phase 1 验收目录中，其捕获核心源文件逐字节保持一致。

## DPI / Resize

- 五个页面 × 125% / 150% / 175% × 1100×700 / 1400×900，共 **30 组**检查通过，交互控件横向越界数为 0。
- 本机真实显示缩放为 150%；125% 和 175% 使用 WPF `VisualTreeHelper.SetRootDpi` 注入视觉树 DPI，并在真实 Window 上变更逻辑尺寸。没有改动用户 Windows 显示设置，不能将其描述为三块不同 DPI 物理屏幕的实测。
- 实际页面截图经视觉检查。默认首页完整呈现状态、结果与运行记录；最小尺寸下内容可以纵向滚动。实时识别页将 FPS 与分辨率保持在默认窗口的可见范围内。
- 初始窗口会按可用工作区缩小，避免高缩放时默认 1400×900 超出工作区，最低仍遵循 1100×700。
- 本次没有物理跨屏 DPI 切换、硬件旋转或拔插测试。证据和有效 DPI 数值见 `tests/dpi-layout.json`，对应图像位于 `screenshots/dpi-*.png`。

## 修改文件清单

修改：

- `App/App.xaml`：接入统一主题字典。
- `App/MainWindow.xaml`：现代顶栏、导航、页面模板和滚动布局。
- `App/MainWindow.xaml.cs`：窗口命令、初始尺寸约束；保留异步释放流程，并将最后的 Close 调度到 Closing 事件返回之后，修复同步完成清理时的关闭重入异常。
- `App/MainViewModel.cs`：统一产品状态、页面切换、运行摘要、设置绑定；沿用捕获接口、原始帧消费和资源生命周期。
- `Services/AppSettings.cs`：新增强制关闭的 AutoStart 配置。
- `appsettings.json`：加入 AutoStart=false。
- `Tests/Program.cs`：保留原有捕获断言，适配页面定位；增加状态、配置、截图和布局检查。
- `README.md`：更新产品入口与配置说明。

新增：

- `App/Themes/LightTheme.xaml`。
- `App/ViewModels/PageViewModel.cs`。
- `App/ViewModels/HomeViewModel.cs`、`RecognitionViewModel.cs`、`DocumentViewModel.cs`、`LanguagePackViewModel.cs`、`SettingsViewModel.cs`。
- `App/Views/HomeView.xaml`、`RecognitionView.xaml`、`DocumentView.xaml`、`LanguagePackView.xaml`、`SettingsView.xaml`、`PreviewPane.xaml`，以及对应仅调用 InitializeComponent 的 `.xaml.cs`。
- `docs/Phase15/`：本报告、实际截图、测试结果、变更清单及核心校验记录。
- `Release/`：从最终源码重新生成的自包含 Windows x64 程序；保持既有运行时和依赖版本。

机器可读清单见 `changed-files.json`。未新增捕获后端、OCR/翻译依赖、文档解析或联网代码。

## 未修改 Capture Core 清单

下列文件改造前后 SHA256 完全一致，完整证据见 `core-integrity.json`：

1. `Capture/IScreenCaptureService.cs`
2. `Capture/ScreenCaptureService.cs`
3. `Capture/WindowsGraphicsCaptureSession.cs`
4. `Capture/WinRtCaptureInterop.cs`
5. `Capture/MonitorService.cs`
6. `Capture/MonitorInfo.cs`
7. `Capture/CapturedFrame.cs`
8. `Capture/PixelCopy.cs`
9. `Core/ApplicationState.cs`

`Services/AppLogger.cs`、原有项目依赖和 Windows DPI manifest 也未修改。

## 分步开发记录

| Step | 文件与原因 | 检查 / 预期 | 核心保护 |
|---|---|---|---|
| 1 分析 | 读取现有窗口、VM、配置、测试；记录核心哈希 | 确认唯一捕获服务与现有发布副本 | 不修改 |
| 2 Theme | LightTheme.xaml、App.xaml | 字典可解析，0 编译错误 | 不修改 |
| 3 Shell | MainWindow、PageViewModel 与五个页面 VM | 五项侧栏、标题栏、最小尺寸 | 不修改 |
| 4 Home | HomeView、PreviewPane | Hero、四栏摘要、真实预览、禁用设置、空结果 | 不修改 |
| 5 Recognition | RecognitionView | 迁移显示器、暂停、FPS、分辨率与高级信息 | 不修改 |
| 6 State | MainViewModel、AutoStart 配置 | 统一命令、手动开始、暂停保留、停止清空 | 不修改 |
| 7 Shell pages | Document/LanguagePack/Settings Views | 禁用未来功能、实际配置和日志入口 | 不修改 |
| 8 Regression | Tests/Program.cs | 原有回归及 UI State 全通过 | 哈希一致 |
| 9 DPI/Resize | UI 间距/初始尺寸、布局测试 | 30 组检查及实际截图复核 | 哈希一致 |

## 运行与范围

项目仍位于 `C:\翻译器`。入口为 `Launch.cmd` / `Release\ScreenTranslator.exe`；源码构建、发布和测试仍使用原有 `Build.ps1` / `Test.ps1`。启动默认首页待机，在首页开始或进入实时识别选择显示器后开始。

最终交付已从 C:\翻译器\Release\ScreenTranslator.exe 直接启动成功，窗口标题为 ScreenTranslator，默认首页待机。此前 Phase 1 曾遭遇签名策略拦截；本次没有关闭策略、添加信任根或改变签名设置，不推断该环境限制为何发生变化。独立启动证据见 deployment.json。更新时正常关闭了旧程序，并修复其 UI Closing 重入问题；没有修改捕获后端。

到此停止 Phase 1.5，不进入 Phase 2；没有 PPT Detection、OCR、翻译、解析器或 Overlay 实现。



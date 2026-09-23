# Phase 1 分步交付记录

本记录对应本次按顺序开发的七个步骤。工作区完成构建测试后，最终交付到 `C:\翻译器`。文中路径均相对于项目目录。

| 步骤 | 新增 | 修改 | 运行与预期 | 故障位置 |
|---|---|---|---|---|
| 1 Solution | ScreenTranslator.sln、App/ScreenTranslator.csproj、global.json、.gitignore | 无 | Visual Studio 打开 Solution；识别 WPF 工程 | .NET SDK 版本、构建输出 |
| 2 基础目录 | Core/ApplicationState.cs、Services/AppSettings.cs、Services/AppLogger.cs、appsettings.json；功能预留目录 | 无 | 无独立入口；后续启动自动加载配置并记录日志 | 用户 AppData 配置和 logs |
| 3 UI | App/App.xaml、App.xaml.cs、MainWindow.xaml、MainWindow.xaml.cs、app.manifest、NuGet.Config | 工程配置 | dotnet run --project App/ScreenTranslator.csproj；出现控制窗口 | WPF 构建错误、启动日志 |
| 4 显示器 | Capture/MonitorInfo.cs、MonitorService.cs、App/MainViewModel.cs | MainWindow.xaml.cs、csproj | 启动后显示全部屏幕的名称、物理尺寸、缩放、主屏标记 | Monitor detected 日志 |
| 5 捕获 | IScreenCaptureService.cs、ScreenCaptureService.cs、CapturedFrame.cs；初版 Desktop Duplication 实现 | csproj、packages.lock.json | 服务后台采集，UI 不执行抓屏 | Capture error、Device lost、HRESULT |
| 6 预览 | App/AsyncCommand.cs | ViewModel、窗口代码 | Start 显示实时画面，Pause 保留，Stop 清空 | FPS、状态文字、采集日志 |
| 7 测试及修复 | Tests 工程、Build.ps1、Test.ps1、Launch.cmd、docs 文档、Release 发布包 | 见下文 | 运行 Test.ps1 -Seconds 600；结果写入 artifacts | samples.csv、checks.txt、failure.txt、result.json |

## 测试驱动的修正

- 本机 Desktop Duplication 在真实桌面会话返回 DXGI_ERROR_UNSUPPORTED。因此最终生产实现改为 Windows Graphics Capture，删除未通过本机验证的 DesktopDuplicationSession.cs，新增 WindowsGraphicsCaptureSession.cs、WinRtCaptureInterop.cs、PixelCopy.cs。服务接口保持不变。
- App / Tests 的目标框架改为 net10.0-windows10.0.19041.0，增加微软 WinRT API 引用。
- 受限进程无法访问 WGC 服务；测试改在正常交互式桌面进程运行后成功获取实际 GPU 画面。
- 实际预览检查发现浅色按钮中文字对比度不足，已在 App.xaml / MainWindow.xaml 修正。
- 添加测试专用数据目录覆盖，不污染正常配置。
- 关闭窗口增加防重复关闭保护，确保重复点击关闭按钮不会提前结束正在等待的资源回收。
- 本机硬件只有一块 2560×1440 / 150% 屏幕。验收记录明确区分已执行的单屏测试与未执行的多屏、热插拔等场景。

最终核心算法和资源处理只包含屏幕捕获，没有 OCR、翻译、PPT/PDF 识别或 Overlay 实现。

## 最终交付检查

2026-09-10：项目复制到 C:\翻译器，467 个文件 SHA256 校验通过。最终目录可执行文件启动被 Windows Code Integrity 3077/3033 拦截，要求受策略认可的签名；目前发布包未签名，机器没有可用的用户代码签名证书。因此 Step 7 的真实捕获测试通过，但最终发布启动验收尚未通过，不能将 Phase 1 标为全部完成。已将证据与处理条件写入 Acceptance.md。

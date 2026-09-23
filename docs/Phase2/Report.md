# ScreenTranslator Phase 2 交付报告

生成时间：2026-09-12  
版本：0.2.0  
范围：PowerPoint / WPS 演示页面区域检测与绿色预览框  
结论：**WPS 场景通过；Phase 2 部分验收通过。** 本机没有安装 Microsoft PowerPoint，因此 PowerPoint 实机兼容仍待补测；本机只有一台物理显示器，因此多屏坐标只完成自动测试，没有第二块实屏验收。

## 1. 完成内容

- 保留原有 Windows Graphics Capture、显示器枚举、Start / Pause / Stop、FPS、预览和资源释放实现。
- 新增 PowerPoint / WPS 目标窗口跟踪，支持编辑窗口和全屏放映窗口。
- 新增 OpenCV 候选生成：闭合边缘轮廓、色彩对比轮廓和直线交点候选。
- 新增候选评分：面积、宽高比、矩形完整度、边缘、中心、窗口包含、内部特征和时序稳定。
- 新增全屏统一边栏扫描，可排除 4:3 放映时的左右黑边。
- 新增三帧稳定、高置信度快速接受、指数平滑、异常帧抑制和短暂丢失保持。
- 新增遮挡窗口抑制，避免把其他应用覆盖区域误认为幻灯片。
- 新增真实绿色框预览；停止时清空，暂停时保留。
- 新增有界后台视觉管线：最多一个在途任务，只保留最新帧，停止后丢弃迟到结果。
- 仍未实现 OCR、翻译、PDF 解析、Overlay 翻译覆盖、模型或任何网络请求。

## 2. 实际绿色框截图

### 编辑模式 16:9

![WPS 编辑模式 16:9 绿色框](evidence/edit-16x9-green.png)

### 编辑模式 4:3，包含右侧属性面板

绿色框只覆盖实际幻灯片，没有包含左侧缩略图、顶部功能区或右侧属性面板。

![WPS 编辑模式 4:3 与属性面板](evidence/edit-4x3-right-panel-green.png)

### 全屏模式 4:3

绿色框排除了左右黑色柱状边栏。

![WPS 4:3 全屏 60 秒最终验收](evidence/fullscreen-4x3-final-60s-green.png)

### 全屏模式 16:9

![WPS 16:9 全屏](evidence/fullscreen-16x9-green.png)

### 发布版启动前状态

0.2.0 自包含发布版直接启动成功，状态为“待机中”，开始按钮可用、停止按钮禁用，没有自动捕获。

![ScreenTranslator 0.2.0 待机](evidence/release-idle-0.2.0.png)

## 3. IoU 与置信度

坐标均为所选显示器的物理像素。Ground Truth 通过截图中可见幻灯片边界手工标注。编辑模式验收线为 IoU ≥ 0.85，全屏模式验收线为 IoU ≥ 0.95。

| 场景 | Ground Truth `(x,y,w,h)` | 检测 `(x,y,w,h)` | IoU | 置信度 | 延迟 |
|---|---:|---:|---:|---:|---:|
| WPS 编辑 16:9 | 587,337,1709,963 | 586.67,337.33,1708.80,962.89 | **0.9991** | 0.9509 | 47.09 ms |
| WPS 编辑 4:3 | 799,337,1284,963 | 800.00,337.33,1282.13,962.89 | **0.9985** | 0.9556 | 53.24 ms |
| WPS 编辑 4:3 + 属性面板 | 572,337,1284,963 | 571.73,337.33,1284.27,962.89 | **0.9999** | 0.9446 | 30.41 ms |
| WPS 全屏 16:9 | 0,0,2560,1440 | 0,0,2560,1440 | **1.0000** | 0.9615 | 9.28 ms |
| WPS 全屏 4:3 | 320,0,1920,1440 | 320,0,1920,1440 | **1.0000** | 0.9659 | 10.31 ms |

置信度是候选特征加权得到的启发式分数，用于候选排序、接受和切换阈值；它不是经过概率校准的统计概率。原始数据见 [accuracy.json](data/accuracy.json)。

## 4. 编辑模式结果

- 16:9 WPS 编辑窗口连续运行 60.13 秒，227 个区域样本稳定 IoU 为 1.0，位置与尺寸标准差接近 0。
- 4:3 页面通过，正确适应不同宽高比。
- 右侧属性面板打开、幻灯片与背景灰度对比低时，色彩对比轮廓仍正确定位页面。
- 当另一个顶层窗口遮挡候选区域时，检测器拒绝被遮挡候选并给出用户提示。
- WPS 最小化或没有演示窗口时，不生成绿色框；实测 Target 和 Region 均为 null。

## 5. 全屏模式结果

- 16:9 放映覆盖整个 2560 × 1440 显示器，检测框为整屏。
- 4:3 放映正确检测中间 1920 × 1440 页面，并排除左右各 320 像素黑边。
- 最终无干扰 60 秒全屏验收中，227 个区域样本全部为同一矩形，Stable IoU 为 1.0，四个坐标维度标准差均为 0。
- 另一次 60 秒 WPS 实际序列覆盖白色、深色、彩色、图像和密集文本测试页，最终区域保持 `(320,0,1920,1440)`，平均稳定 IoU 为 1.0。
- 纯黑且页面与边栏无可见分界时不伪造边界；单元测试要求返回空结果。

## 6. 性能

测试环境：Windows，2560 × 1440，缩放 150%，一台物理显示器，目标捕获 30 FPS。

| 场景 | 时长 | 预览 FPS | 视觉 FPS | 全核 CPU | 检测延迟（均值 / 最小 / 最大） | Private MB（首 / 末） |
|---|---:|---:|---:|---:|---:|---:|
| WPS 编辑 16:9 | 60.13 s | 29.20 | 1.91 | 4.94% | 55.67 / 40.92 / 106.80 ms | 474.57 / 410.85 |
| WPS 编辑 4:3 + 属性面板，静态桌面 | 60.07 s | 0.13 | 0.12 | 0.32% | 32.27 / 23.74 / 41.30 ms | 474.98 / 405.45 |
| WPS 全屏 4:3，最终无干扰 | 60.05 s | 28.93 | 1.92 | 3.77% | 11.80 / 8.02 / 15.51 ms | 473.84 / 464.52 |
| 捕获回归 | 30 s | 25.63，最低区间 25.05 | — | — | — | 449.91 / 448.41 |

静态桌面项的低 FPS 是 Windows Graphics Capture 只提交内容变化帧的结果，不代表处理吞吐下降；动态编辑与全屏场景均接近目标 30 FPS。三个 60 秒测试的末尾 Private Bytes 均未高于首样本。详细数据见 [performance-summary.json](data/performance-summary.json)。

## 7. Capture Regression 与 UI 状态

最终捕获回归全部通过：

- 启动默认 Idle，无自动捕获、无 Preview、FPS 为 0。
- Start / Pause / Resume / Stop 命令状态和统一 ViewModel 状态正确。
- Pause 保留 Preview 与绿色框且停止视觉工作。
- Stop 清空 Preview、候选和稳定区域。
- 真实 GPU 像素非空，捕获物理分辨率为 2560 × 1440。
- 8 次 Restart / Stop 循环全部通过。
- 30 秒平均真实预览 25.63 FPS，所有 10 秒区间最低 25.05 FPS。
- Private Bytes 449.91 MB → 448.41 MB，句柄 1457 → 1454。
- 125%、150%、175% DPI 下，1100 × 700 与 1400 × 900、全部页面交互控件无横向溢出。

完整记录见 [capture-regression-checks.txt](data/capture-regression-checks.txt) 和 [capture-regression.json](data/capture-regression.json)。

## 8. 视觉自动测试

34 项全部通过，覆盖：

- 100%、125%、150%、175% 坐标往返和 Preview letterbox 映射。
- 负坐标显示器原点与跨屏裁剪。
- 遮挡/非遮挡候选判断。
- 16:9、4:3、1.6:1，浅色/深色/彩色 9 组合成编辑页，IoU 0.996–0.997。
- 16:9 / 4:3 全屏黑边排除、纯黑无边界。
- 三帧稳定、异常帧抵抗、丢失过期、高置信度首次接受。
- Idle 不处理、最多一个在途 Worker、Stop 等待并丢弃迟到结果、窗口 Hook 解除。

完整记录见 [vision-unit-checks.txt](data/vision-unit-checks.txt) 和 [synthetic-accuracy.json](data/synthetic-accuracy.json)。

## 9. Protected Core SHA256

Phase 2 前后 9 个受保护核心文件 SHA256 全部一致：

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

机器可读校验见 [protected-core-sha256.json](data/protected-core-sha256.json)，`AllMatch = true`。

## 10. 新增与修改文件

新增 Vision：

- `Vision/VisionModels.cs`
- `Vision/VisionSettings.cs`
- `Vision/CoordinateMapper.cs`
- `Vision/TargetWindowTracker.cs`
- `Vision/PresentationCandidateGenerator.cs`
- `Vision/FullscreenPresentationDetector.cs`
- `Vision/PresentationRegionScorer.cs`
- `Vision/PresentationRegionTracker.cs`
- `Vision/PresentationDetector.cs`
- `Vision/VisionPipeline.cs`

新增/修改集成与 UI：

- `App/MainViewModel.cs`
- `App/MainWindow.xaml.cs`
- `App/Views/PresentationPreviewOverlay.cs`
- `App/Views/PreviewPane.xaml`
- `App/Views/HomeView.xaml`
- `App/Views/RecognitionView.xaml`
- `App/Views/SettingsView.xaml`
- `App/Themes/LightTheme.xaml`
- `Services/AppSettings.cs`
- `App/ScreenTranslator.csproj`
- `App/packages.lock.json`
- `appsettings.json`

测试与资料：

- `Tests/Program.cs`
- `Tests/VisionTests.cs`
- `Tests/Fixtures/TestPresentation16x9.pptx`
- `Tests/Fixtures/TestPresentation4x3.pptx`
- `README.md`
- `docs/ThirdPartyNotices.md`
- `docs/Phase2/**`

## 11. 运行与发布

发布包为 `win-x64` 自包含运行时，版本 0.2.0，包含 OpenCvSharp 托管与本机组件，可断网运行。`ScreenTranslator.exe` SHA256：

`96AD3A0AA84210EB9DC764BCF664BE58B0A011A2DB1CA76C3065FEE88880B849`

运行：双击 `Release/ScreenTranslator.exe` 或项目根目录的 `Launch.cmd`。启动后保持待机；点击“开始识别”才创建捕获会话。

## 12. 验收限制与下一步边界

- WPS Office 12.1.0.28505 的编辑与全屏模式已实测。
- Microsoft PowerPoint 目标窗口代码路径已实现，但本机没有 PowerPoint，不能把它报告为实机通过。
- 只有一台物理显示器；负坐标和缩放坐标已自动验证，仍建议在双屏、旋转屏和不同 DPI 组合补充硬件验收。
- 60 秒性能与 30 秒最终捕获回归已通过；Phase 1 既有 600 秒稳定性结果仍有效，因为 Protected Core 未改变。Phase 2 视觉管线仍建议在更多驱动与办公软件版本上做长时间产品化矩阵。
- 到此停止 Phase 2，不进入 OCR、翻译或 Phase 3。

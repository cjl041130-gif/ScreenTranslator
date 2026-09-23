# Phase 1 架构与所有权

```text
WPF commands ─── async lifecycle calls ──→ IScreenCaptureService
                                               │
                                  dedicated capture worker
                                               │
                               Windows Graphics Capture (2 GPU frames)
                                               │
                                  reused D3D11 staging texture
                                               │ Map + row copy + Unmap
                                      pooled CapturedFrame
                                               │
                               Interlocked latest frame slot (capacity 1)
                                               │ ownership transfer
                                WPF DispatcherTimer / Background priority
                                               │ WritePixels
                                       reused WriteableBitmap
```

## 线程

StartAsync 启动专用 LongRunning 线程后返回，Starting / Capturing / Recovering / Faulted 表示实际初始化结果。线程独占 immediate context、WGC session、staging texture，WinRT 自有线程只负责向其固定大小池生产 GPU 帧。没有 UI 线程捕获、忙等或每帧 Task.Run。

UI 每约 16 ms 尝试取最新帧，统计每 250 ms 刷新；更新分辨率只在尺寸改变时通知。UI 上传位图仍发生在 UI 线程，这是 WPF 位图所有权要求；GPU 抓屏和 CPU 读回都在后台，主线程只承担一份预览复制。未来 GPU 直接预览可替换这层而不改变捕获接口。

## 帧所有权与内存上限

1. 生产者从私有 ArrayPool 租用 BGRA 数组，填充后成为 CapturedFrame。
2. `Interlocked.Exchange` 发布新帧，并立即 Dispose 尚未消费的旧帧。
3. `TakeLatestFrame` 将所有权转交唯一消费者。消费者 using / Dispose 在写入预览后归还数组。
4. 同一分辨率通常最多有生产、待消费、正消费三份租用帧。池每个尺寸 bucket 最多缓存三份，没有历史帧队列。不同分辨率切换可能保留不同 bucket，这是有界缓存。
5. 一帧 1920×1080 BGRA 有效数据约 7.9 MiB；2560×1440 约 14.1 MiB，数组池可能向上对齐。WGC、staging、WPF 另外持有 GPU/原生资源，因此进程总内存大于这几份数组。
6. 单次 GPU → CPU 复制和 CPU → WPF 上传是当前 CPU PixelData 接口的必要路径；不编码 PNG、不创建 GDI Bitmap、不使用 Graphics.CopyFromScreen。

WGC 帧、surface、texture、映射、COM ABI 引用均成对释放。停止时先取消并 await worker，再释放 pending frame。Pause 同样关闭采集会话，但保留 UI 的最后一张 WriteableBitmap。Stop 额外清空预览。关闭窗口等待服务 DisposeAsync 后再结束窗口。

`PixelData` 是借用内存视图，必须与 owning frame 同生命周期。未来 OCR 等消费者不能将它存到后台任务后立即 Dispose；应设计显式租约/引用计数或下游有界队列，避免隐式共享与重复释放。目前接口仅支持一个消费者。

## 错误与恢复

捕获异常会释放本轮 GPU 资源，清除 pending frame 和 FPS，间隔 1 秒重新枚举原设备 ID 并重建，连续失败最多 5 次。成功收到帧才重置失败计数。设备丢失 HRESULT 单独标识在日志中。用户操作由 SemaphoreSlim 串行化，UI 命令同时有 busy 防重入。

WGC 捕获项 Closed 事件只设置原子标志，后续由采集线程完成资源退出，事件线程不操作 D3D。系统分辨率改变时先释放旧帧，再 Recreate 帧池并按新尺寸重建 staging / WriteableBitmap。

## 配置和离线

开发工具及 NuGet 首次获取发生于构建阶段。发布包自带桌面运行时，运行代码没有 HTTP 客户端、模型自动下载、遥测或在线翻译调用。配置优先读取用户 AppData，保存使用同目录临时文件与替换，避免写入受保护的安装目录。

后续模块目录为空。Phase 1 没有提前定义不稳定的 OCR/翻译对象模型，也没有引入 OpenCV/ONNX 依赖；这些可通过独立 C# 类库和消费者接入。

## 官方依据

- [Microsoft: WGC CreateFreeThreaded](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded)：无需 UI DispatcherQueue 的帧池。
- [Microsoft: IGraphicsCaptureItemInterop::CreateForMonitor](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor)：从 HMONITOR 创建捕获目标。
- [Microsoft: CreateDirect3D11DeviceFromDXGIDevice](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/nf-windows-graphics-directx-direct3d11-interop-createdirect3d11devicefromdxgidevice)：D3D11 与 WinRT 设备桥接。
- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows)：D3D11 / DXGI .NET 绑定。

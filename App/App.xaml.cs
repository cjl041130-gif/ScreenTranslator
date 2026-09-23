using System.IO;
using System.Windows;
using ScreenTranslator.Models;
using ScreenTranslator.Services;
using ScreenTranslator.Translation;
using ScreenTranslator.Document;

namespace ScreenTranslator;

public partial class App : Application
{
    public AppLogger Log { get; private set; } = null!;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        PdfChineseFontResolver.Configure();
        Log = new AppLogger();
        Log.Info($"Application startup v{typeof(App).Assembly.GetName().Version}; {Environment.OSVersion}");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show("发生异常，请查看日志。程序将退出。\n" + args.Exception.Message, "屏幕翻译器");
            args.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled process exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };
        if (e.Args.Length >= 2 && e.Args[0].Equals("--translation-self-test", StringComparison.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunTranslationSelfTestAsync(e.Args[1]);
            return;
        }
        new MainWindow().Show();
    }

    private async Task RunTranslationSelfTestAsync(string outputPath)
    {
        var exitCode = 1;
        try
        {
            await using var engine = new MarianOnnxTranslationEngine();
            await engine.InitializeAsync(InferenceDevice.Cpu, CancellationToken.None);
            var context = new TranslationContext("", [], new Dictionary<string, string>(), "");
            var result = await engine.TranslateAsync(new("en", "zh-CN",
                "The offline translation engine is ready.", context, OcrBlockType.Body), CancellationToken.None);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, $"PASS\n{result.Text}\n{result.ProcessingMilliseconds:F1} ms\n");
            exitCode = result.Text.Any(c => c is >= '\u3400' and <= '\u9fff') ? 0 : 2;
        }
        catch (Exception ex)
        {
            try { await File.WriteAllTextAsync(outputPath, "FAIL\n" + ex); } catch { }
            Log.Error("Translation self-test failed", ex);
        }
        finally { Shutdown(exitCode); }
    }
    protected override void OnExit(ExitEventArgs e) { Log?.Info("Application exit"); base.OnExit(e); }
}

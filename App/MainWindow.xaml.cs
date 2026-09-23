using System.Windows;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using ScreenTranslator.Core;

namespace ScreenTranslator;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closed;
    private bool _closeRequested;
    public MainWindow() : this(new MainViewModel(((App)Application.Current).Log)) { }
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _viewModel.PropertyChanged += ViewModelStateChanged;
        // Start as a compact corner panel and keep it inside the current high-DPI work area.
        SourceInitialized += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 16));
            Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 16));
            Left = Math.Max(workArea.Left + 8, workArea.Right - Width - 12);
            Top = Math.Max(workArea.Top + 8, workArea.Bottom - Height - 12);
        };
        Closing += OnClosing;
        CommandBindings.Add(new(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new(SystemCommands.MaximizeWindowCommand, (_, _) => { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }));
        CommandBindings.Add(new(SystemCommands.CloseWindowCommand, (_, _) => Close()));
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closeRequested) return;
        _closeRequested = true;
        _viewModel.PropertyChanged -= ViewModelStateChanged;
        IsEnabled = false;
        await _viewModel.DisposeAsync();
        _closed = true;
        // Dispose can complete synchronously while WPF is still dispatching Closing.
        // Post the final close so it never re-enters WPF's active close transaction.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
    private void ViewModelStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.State)) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        // Exclude only our control window while capturing, preventing recursive preview and self-detection.
        // This changes window composition metadata; the protected capture backend is untouched.
        var active = _viewModel.State is ApplicationState.Starting or ApplicationState.Capturing or ApplicationState.Recovering;
        if (!SetWindowDisplayAffinity(handle, active ? 0x11u : 0u))
            ((App)Application.Current).Log.Info($"Preview window exclusion unavailable: {Marshal.GetLastWin32Error()}; keep the presentation visible.");
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
}

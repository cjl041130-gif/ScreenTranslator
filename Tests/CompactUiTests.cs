using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScreenTranslator.Core;
using ScreenTranslator.ViewModels;
using ScreenTranslator.Views;

namespace ScreenTranslator.Tests;

internal static partial class Program
{
    private static async Task RunCompactUiTestsAsync(MainWindow window)
    {
        var vm = (MainViewModel)window.DataContext;
        vm.CurrentPage = vm.Pages.OfType<HomeViewModel>().Single();
        await Task.Delay(180);
        window.UpdateLayout();

        Check(window.Width <= 800 && window.Height <= 600, "Default window is sized for a compact screen-corner panel");
        Check(vm.State == ApplicationState.Stopped && vm.StartCommand.CanExecute(null) &&
              !vm.PauseCommand.CanExecute(null) && !vm.StopCommand.CanExecute(null),
            "Compact home starts idle with Start enabled and Pause/Stop disabled");
        Check(FindButton(window, "StartCapture").Command == vm.StartCommand &&
              FindButton(window, "Pause").Command == vm.PauseCommand &&
              FindButton(window, "Stop").Command == vm.StopCommand,
            "Compact home keeps the real Start/Pause/Stop commands");

        var host = (FrameworkElement)window.FindName("PageHost");
        Check(!Descendants(host).OfType<PreviewPane>().Any(), "Home has no screen preview, thumbnail, or preview pane");
        vm.CurrentPage = vm.Pages.OfType<RecognitionViewModel>().Single();
        await Task.Delay(100);
        window.UpdateLayout();
        Check(!Descendants(host).OfType<PreviewPane>().Any(), "Recognition controls retain capture state without rendering screen preview");

        vm.CurrentPage = vm.Pages.OfType<HomeViewModel>().Single();
        await LayoutChecksAsync(window, vm);
        await vm.DisposeAsync();
        window.Close();
        await Until(() => !window.IsLoaded, "Compact UI test window closes cleanly");
    }
}

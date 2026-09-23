using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ScreenTranslator.ViewModels;
namespace ScreenTranslator.Views;
public partial class DocumentView : UserControl
{
    public DocumentView() { InitializeComponent(); }
    private void OnDragEnter(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = files?.Any(IsSupported) == true ? DragDropEffects.Copy : DragDropEffects.None;
        if (e.Effects == DragDropEffects.Copy) { DropZone.BorderBrush = (Brush)FindResource("Accent"); DropZone.Background = (Brush)FindResource("AccentSoft"); }
        e.Handled = true;
    }
    private void OnDragLeave(object sender, DragEventArgs e) => ResetDropZone();
    private async void OnDrop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        if (DataContext is DocumentViewModel viewModel && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            await viewModel.HandleDroppedPathsAsync(files);
        e.Handled = true;
    }
    private void ResetDropZone() { DropZone.ClearValue(Border.BorderBrushProperty); DropZone.ClearValue(Border.BackgroundProperty); }
    private static bool IsSupported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".pptx" or ".docx";
}

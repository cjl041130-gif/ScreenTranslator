namespace ScreenTranslator.ViewModels;
public abstract class PageViewModel(MainViewModel capture, string title, string icon)
{
    public MainViewModel Capture { get; } = capture;
    public string Title { get; } = title;
    public string Icon { get; } = icon;
}


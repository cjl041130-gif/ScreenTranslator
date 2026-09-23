namespace ScreenTranslator.Capture;

public sealed record MonitorInfo(string Id, string Name, int Width, int Height,
    int Left, int Top, double Scale, bool IsPrimary, nint Handle, int Index)
{
    public string DisplayLabel => $"Monitor {Index} · {Name} · {Width} × {Height} · {Scale:P0}{(IsPrimary ? " · 主显示器" : "")}";
}

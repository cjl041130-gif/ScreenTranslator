using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenTranslator.Vision;
using ScreenTranslator.Models;
namespace ScreenTranslator.Views;

// Preview-only drawing. Does not own frames, perform detection or create a desktop overlay.
public sealed class PresentationPreviewOverlay : FrameworkElement
{
    public static readonly DependencyProperty FrameProperty = Register(nameof(Frame), typeof(BitmapSource), null);
    public static readonly DependencyProperty SnapshotProperty = Register(nameof(Snapshot), typeof(DetectionSnapshot), null);
    public static readonly DependencyProperty ShowSearchProperty = Register(nameof(ShowSearch), typeof(bool), false);
    public static readonly DependencyProperty ShowCandidatesProperty = Register(nameof(ShowCandidates), typeof(bool), false);
    public static readonly DependencyProperty ShowScoresProperty = Register(nameof(ShowScores), typeof(bool), false);
    public static readonly DependencyProperty ShowStableProperty = Register(nameof(ShowStable), typeof(bool), true);
    public static readonly DependencyProperty RecognitionResultProperty = Register(nameof(RecognitionResult), typeof(SlideRecognitionResult), null);
    public static readonly DependencyProperty ShowOcrBlocksProperty = Register(nameof(ShowOcrBlocks), typeof(bool), true);
    private static DependencyProperty Register(string name, Type type, object? value) => DependencyProperty.Register(name, type, typeof(PresentationPreviewOverlay), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
    public BitmapSource? Frame { get => (BitmapSource?)GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public DetectionSnapshot? Snapshot { get => (DetectionSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public bool ShowSearch { get => (bool)GetValue(ShowSearchProperty); set => SetValue(ShowSearchProperty, value); }
    public bool ShowCandidates { get => (bool)GetValue(ShowCandidatesProperty); set => SetValue(ShowCandidatesProperty, value); }
    public bool ShowScores { get => (bool)GetValue(ShowScoresProperty); set => SetValue(ShowScoresProperty, value); }
    public bool ShowStable { get => (bool)GetValue(ShowStableProperty); set => SetValue(ShowStableProperty, value); }
    public SlideRecognitionResult? RecognitionResult { get => (SlideRecognitionResult?)GetValue(RecognitionResultProperty); set => SetValue(RecognitionResultProperty, value); }
    public bool ShowOcrBlocks { get => (bool)GetValue(ShowOcrBlocksProperty); set => SetValue(ShowOcrBlocksProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        if (Frame is null || Snapshot is null) return;
        if (ShowSearch) Draw(Snapshot.SearchRegion, "VisionSearch", 1.5);
        if (ShowCandidates) foreach (var c in Snapshot.Candidates.Take(15)) Draw(c.Bounds, "VisionCandidate", 1, ShowScores ? $"{c.Scores?.FinalScore:F2}" : null);
        if (ShowStable && Snapshot.Region is { } region) Draw(region.Bounds, "VisionStable", 2.5);
        if (ShowOcrBlocks && RecognitionResult is { } recognition)
            foreach (var block in recognition.Blocks)
                Draw(new Rect(recognition.Presentation.X + block.Source.X, recognition.Presentation.Y + block.Source.Y, block.Source.Width, block.Source.Height), "Accent", 1.2);
        void Draw(Rect bounds, string resource, double width, string? text = null)
        {
            var mapped = PreviewCoordinateMapper.Map(bounds, Frame.PixelWidth, Frame.PixelHeight, ActualWidth, ActualHeight);
            if (mapped.IsEmpty) return;
            var brush = (Brush)FindResource(resource);
            dc.DrawRectangle(null, new Pen(brush, width), mapped);
            if (text is not null) dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), mapped.TopLeft);
        }
    }
}

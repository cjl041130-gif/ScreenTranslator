namespace ScreenTranslator.Document;

public sealed class DocumentTranslationOptions
{
    public int MaximumFileSizeMB { get; set; } = 200;
    public int MaximumPages { get; set; } = 500;
    public int TranslationBatchCharacters { get; set; } = 3500;
    public int MaximumConcurrentRequests { get; set; } = 2;
    public bool EnableScannedPdfOcr { get; set; } = true;
    public DocumentExportMode DefaultPptxExportMode { get; set; } = DocumentExportMode.Replace;
    public DocumentExportMode DefaultPdfExportMode { get; set; } = DocumentExportMode.Replace;
    public DocumentExportMode DefaultDocxExportMode { get; set; } = DocumentExportMode.Replace;
    public bool KeepCompletedTaskCache { get; set; } = true;
    public int TaskCacheCapacity { get; set; } = 20;
    public int TemporaryFileRetentionMinutes { get; set; } = 30;
    public long MaximumExpandedPackageBytes { get; set; } = 1024L * 1024 * 1024;

    public void Normalize()
    {
        MaximumFileSizeMB = Math.Clamp(MaximumFileSizeMB, 1, 500);
        MaximumPages = Math.Clamp(MaximumPages, 1, 1000);
        TranslationBatchCharacters = Math.Clamp(TranslationBatchCharacters, 500, 8000);
        MaximumConcurrentRequests = Math.Clamp(MaximumConcurrentRequests, 1, 2);
        TaskCacheCapacity = Math.Clamp(TaskCacheCapacity, 1, 100);
        TemporaryFileRetentionMinutes = Math.Clamp(TemporaryFileRetentionMinutes, 5, 1440);
        MaximumExpandedPackageBytes = Math.Clamp(MaximumExpandedPackageBytes, 64L * 1024 * 1024, 2L * 1024 * 1024 * 1024);
        if (!Enum.IsDefined(DefaultPptxExportMode)) DefaultPptxExportMode = DocumentExportMode.Replace;
        if (!Enum.IsDefined(DefaultPdfExportMode)) DefaultPdfExportMode = DocumentExportMode.Replace;
        if (!Enum.IsDefined(DefaultDocxExportMode)) DefaultDocxExportMode = DocumentExportMode.Replace;
    }
}

using System.Diagnostics;
using System.Security.Cryptography;
using ScreenTranslator.Services;

namespace ScreenTranslator.Document;

public sealed class DocumentImportService(
    DocumentValidationService validation,
    IEnumerable<IDocumentParser> parsers,
    DocumentTranslationOptions options,
    AppLogger log) : IDocumentImportService
{
    public async Task<TranslationDocument> ImportAsync(string path, IProgress<DocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var type = validation.Validate(path);
        var parser = parsers.FirstOrDefault(x => x.DocumentType == type)
            ?? throw new DocumentUserException("此文档类型的本地解析器尚未安装。");
        var watch = Stopwatch.StartNew();
        progress?.Report(new("正在读取文档", 0, 0, 0, 0, 0));
        try
        {
            var document = await parser.ParseAsync(path, progress, cancellationToken).ConfigureAwait(false);
            if (document.PageCount > options.MaximumPages)
                throw new DocumentUserException($"文档超过 {options.MaximumPages} 页限制。");
            log.Info($"Document imported: type={type}, bytes={document.FileSizeBytes}, pages={document.PageCount}, blocks={document.TextBlockCount}, ocrPages={document.OcrPageCount}, elapsedMs={watch.Elapsed.TotalMilliseconds:F0}");
            return document;
        }
        catch (DocumentUserException) { throw; }
        catch (UnauthorizedAccessException ex) { throw new DocumentUserException("没有读取此文件的权限，请复制到可访问的位置后重试。", ex); }
        catch (IOException ex) { throw new DocumentUserException("无法读取此文档。文件可能正被其他程序占用或已经损坏。", ex); }
        catch (Exception ex)
        {
            log.Error($"Document import failed: type={type}", ex);
            if (type == DocumentType.Pdf && ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
                throw new DocumentUserException("此 PDF 已加密，当前版本暂不支持。", ex);
            throw new DocumentUserException("无法读取此文档。文件可能已经损坏或格式不受支持。", ex);
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}


using System.IO.Compression;

namespace ScreenTranslator.Document;

public sealed class DocumentValidationService(DocumentTranslationOptions options)
{
    public DocumentType Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new DocumentUserException("文件不存在，请重新选择。");
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var type = extension switch
        {
            ".pdf" => DocumentType.Pdf,
            ".pptx" => DocumentType.Pptx,
            ".docx" => DocumentType.Docx,
            ".doc" => throw new DocumentUserException("当前支持 DOCX，暂不支持旧版 DOC 文件。请先另存为 DOCX。"),
            _ => throw new DocumentUserException("当前仅支持 PDF、PPTX 和 DOCX 文件。")
        };
        var info = new FileInfo(path);
        if (info.Length <= 0) throw new DocumentUserException("文档内容为空。");
        if (info.Length > options.MaximumFileSizeMB * 1024L * 1024)
            throw new DocumentUserException($"文件超过 {options.MaximumFileSizeMB} MB 限制。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) < 4) throw new DocumentUserException("无法读取此文档。文件可能已经损坏或格式不受支持。");
        if (type == DocumentType.Pdf)
        {
            if (!header[..5].SequenceEqual("%PDF-"u8))
                throw new DocumentUserException("此文件不是有效的 PDF 文档。");
            return type;
        }
        if (header[0] != (byte)'P' || header[1] != (byte)'K')
            throw new DocumentUserException("此文件不是有效的 Office Open XML 文档。");
        stream.Position = 0;
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 20_000) throw new DocumentUserException("文档内部文件数量异常，已停止读取。");
            long expanded = 0;
            foreach (var entry in archive.Entries)
            {
                expanded = checked(expanded + entry.Length);
                if (expanded > options.MaximumExpandedPackageBytes)
                    throw new DocumentUserException("文档解压后体积异常，已停止读取。");
                if (entry.CompressedLength > 0 && entry.Length > 100L * 1024 * 1024 && entry.Length / entry.CompressedLength > 200)
                    throw new DocumentUserException("文档压缩比例异常，已停止读取。");
            }
            var required = type == DocumentType.Pptx ? "ppt/presentation.xml" : "word/document.xml";
            if (archive.GetEntry("[Content_Types].xml") is null || archive.GetEntry(required) is null)
                throw new DocumentUserException("无法读取此文档。文件可能已经损坏或格式不受支持。");
        }
        catch (InvalidDataException ex) { throw new DocumentUserException("无法读取此文档。文件可能已经损坏或格式不受支持。", ex); }
        catch (OverflowException ex) { throw new DocumentUserException("文档内部数据规模异常，已停止读取。", ex); }
        return type;
    }
}


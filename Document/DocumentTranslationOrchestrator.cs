using System.Text.Json;
using System.Windows;
using ScreenTranslator.Models;
using ScreenTranslator.Translation;

namespace ScreenTranslator.Document;

public sealed class DocumentTranslationOrchestrator(DocumentTranslationOptions options,
    DocumentTranslationCache cache) : IDocumentTranslationOrchestrator
{
    public async Task TranslateAsync(TranslationDocument document, IPageTranslationEngine engine,
        IProgress<DocumentProgress>? progress, CancellationToken cancellationToken)
    {
        var pendingList = new List<DocumentTextBlock>();
        foreach (var block in document.Pages.SelectMany(p => p.TextBlocks).Where(b => b.ShouldTranslate))
        {
            if (cache.TryGet(document, engine, block, out var cachedText, out _))
            {
                block.TranslatedText = cachedText;
                block.TranslationStatus = DocumentBlockTranslationStatus.Completed;
            }
            else pendingList.Add(block);
        }
        var pending = pendingList.ToArray();
        var total = document.Pages.SelectMany(p => p.TextBlocks).Count(b => b.ShouldTranslate);
        var alreadyDone = total - pending.Length;
        var batches = CreateBatches(pending, options.TranslationBatchCharacters).ToArray();
        var completed = alreadyDone;
        var started = DateTimeOffset.UtcNow;
        using var gate = new SemaphoreSlim(options.MaximumConcurrentRequests);
        var tasks = batches.Select(async batch =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var block in batch) block.TranslationStatus = DocumentBlockTranslationStatus.Translating;
                var ocrBlocks = batch.Select(ToOcrBlock).ToArray();
                var responses = engine is IDocumentPageTranslationEngine documentEngine
                    ? await documentEngine.TranslateDocumentBatchAsync(ocrBlocks, document.SourceLanguage, document.TargetLanguage, document.DocumentType.ToString(), cancellationToken).ConfigureAwait(false)
                    : await engine.TranslatePageAsync(ocrBlocks, document.SourceLanguage, document.TargetLanguage, cancellationToken).ConfigureAwait(false);
                if (responses.Count != batch.Count) throw new InvalidDataException("AI 返回的文字块数量与请求不一致。");
                for (var i = 0; i < batch.Count; i++)
                {
                    var translated = responses[i].Text?.Trim();
                    if (string.IsNullOrWhiteSpace(translated)) throw new InvalidDataException($"AI 未返回文字块 {batch[i].StableId} 的译文。");
                    batch[i].TranslatedText = translated;
                    batch[i].TranslationStatus = DocumentBlockTranslationStatus.Completed;
                    cache.Put(document, engine, batch[i]);
                }
                var done = Interlocked.Add(ref completed, batch.Count);
                var elapsed = DateTimeOffset.UtcNow - started;
                TimeSpan? remaining = done <= 0 ? null : TimeSpan.FromTicks((long)(elapsed.Ticks * (total - done) / (double)done));
                progress?.Report(new("正在等待 AI 翻译", document.Pages.Count(p => p.TextBlocks.All(b => !b.ShouldTranslate || b.TranslationStatus == DocumentBlockTranslationStatus.Completed)),
                    document.PageCount, done, total, total == 0 ? 1 : done / (double)total, remaining));
            }
            catch
            {
                foreach (var block in batch.Where(x => x.TranslationStatus == DocumentBlockTranslationStatus.Translating))
                    block.TranslationStatus = DocumentBlockTranslationStatus.Faulted;
                throw;
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        cache.Trim();
    }

    private static IEnumerable<IReadOnlyList<DocumentTextBlock>> CreateBatches(IEnumerable<DocumentTextBlock> blocks, int limit)
    {
        var batch = new List<DocumentTextBlock>();
        var count = 0;
        foreach (var block in blocks.OrderBy(x => x.PageIndex).ThenBy(x => x.ReadingOrder))
        {
            if (batch.Count > 0 && (count + block.OriginalText.Length > limit || batch[0].PageIndex != block.PageIndex))
            { yield return batch.ToArray(); batch.Clear(); count = 0; }
            batch.Add(block); count += block.OriginalText.Length;
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }

    private static OcrBlock ToOcrBlock(DocumentTextBlock block) => new(block.StableId, block.OriginalText,
        block.Confidence, block.Bounds == Rect.Empty ? new Rect(0, block.ReadingOrder * 30, 800, Math.Max(20, block.FontSize * 1.4)) : block.Bounds,
        block.ReadingOrder, block.ReadingOrder, block.BlockType switch
        {
            DocumentBlockType.Title => OcrBlockType.Title, DocumentBlockType.Bullet => OcrBlockType.Bullet,
            DocumentBlockType.TableCell => OcrBlockType.Table, DocumentBlockType.Code => OcrBlockType.Code,
            DocumentBlockType.Formula => OcrBlockType.Formula, DocumentBlockType.Caption => OcrBlockType.Caption,
            DocumentBlockType.Footer => OcrBlockType.Footer, _ => OcrBlockType.Body
        });
}

public sealed class DocumentTranslationCache(DocumentTranslationOptions options)
{
    private readonly object _sync = new();
    private string DirectoryPath => Path.Combine(ScreenTranslator.Services.AppSettings.DataDirectory, "DocumentCache");

    public bool TryGet(TranslationDocument document, IPageTranslationEngine engine, DocumentTextBlock block,
        out string translated, out string key)
    {
        key = CreateKey(document, engine, block);
        translated = "";
        if (!options.KeepCompletedTaskCache) return false;
        var path = Path.Combine(DirectoryPath, key + ".json");
        lock (_sync)
        {
            try
            {
                if (!File.Exists(path)) return false;
                var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
                if (entry is null || string.IsNullOrWhiteSpace(entry.TranslatedText)) return false;
                translated = entry.TranslatedText; File.SetLastAccessTimeUtc(path, DateTime.UtcNow); return true;
            }
            catch { return false; }
        }
    }

    public void Put(TranslationDocument document, IPageTranslationEngine engine, DocumentTextBlock block)
    {
        if (!options.KeepCompletedTaskCache || string.IsNullOrWhiteSpace(block.TranslatedText)) return;
        lock (_sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            var key = CreateKey(document, engine, block);
            var path = Path.Combine(DirectoryPath, key + ".json");
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new CacheEntry(block.TranslatedText, DateTimeOffset.UtcNow)));
            File.Move(temp, path, true);
        }
    }

    public long SizeBytes => Directory.Exists(DirectoryPath) ? new DirectoryInfo(DirectoryPath).EnumerateFiles("*.json").Sum(x => x.Length) : 0;
    public void Clear() { lock (_sync) { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); } }
    public void Trim()
    {
        lock (_sync)
        {
            if (!Directory.Exists(DirectoryPath)) return;
            foreach (var file in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.json").OrderByDescending(x => x.LastAccessTimeUtc).Skip(options.TaskCacheCapacity * 500))
                try { file.Delete(); } catch { }
        }
    }

    private static string CreateKey(TranslationDocument document, IPageTranslationEngine engine, DocumentTextBlock block) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{document.ContentSha256}|{block.PageIndex}|{block.StableId}|{block.OriginalText}|{document.SourceLanguage}|{document.TargetLanguage}|{engine.GetType().FullName}|{(engine as ScreenTranslator.Translation.AI.AiTranslationEngine)?.Options.Provider}|{(engine as ScreenTranslator.Translation.AI.AiTranslationEngine)?.Options.Model}|{(engine as ScreenTranslator.Translation.AI.AiTranslationEngine)?.Options.Style}")));
    private sealed record CacheEntry(string TranslatedText, DateTimeOffset CreatedAt);
}

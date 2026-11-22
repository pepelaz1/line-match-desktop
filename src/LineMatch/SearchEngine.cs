using System.Diagnostics;
using System.IO;
using System.Text;

namespace LineMatch;

public enum SourceDisposition { Keep, Move, Delete }
public sealed record Match(string FileName, long LineNumber, string Value);
public sealed record SearchOptions(string SmallPath, string WorkPath, string OutputPath, int Threads, int ChunkLines, Encoding Encoding, SourceDisposition Disposition);
public sealed record SearchProgress(string CurrentFile, int CompletedFiles, int TotalFiles, long CheckedLines, long Hits, double Percent, double LinesPerSecond, long WorkingSetBytes);

public sealed class SearchEngine
{
    private readonly ManualResetEventSlim _gate = new(true);
    public bool IsPaused { get; private set; }
    public void Pause() { _gate.Reset(); IsPaused = true; }
    public void Resume() { _gate.Set(); IsPaused = false; }

    public async Task RunAsync(SearchOptions options, IProgress<SearchProgress> progress, Action<Match> onMatch, Action<string> log, CancellationToken token)
    {
        if (!File.Exists(options.SmallPath)) throw new FileNotFoundException("Не найден эталонный файл", options.SmallPath);
        if (!Directory.Exists(options.WorkPath)) throw new DirectoryNotFoundException(options.WorkPath);
        if (Path.GetFullPath(options.OutputPath).Equals(Path.GetFullPath(options.SmallPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Выходной файл совпадает с эталонным.");
        if (File.Exists(options.OutputPath)) throw new IOException("success.txt уже существует. Переместите его перед новым запуском.");
        var files = Directory.EnumerateFiles(options.WorkPath, "*.txt")
            .Select(path => (Path: path, Number: int.TryParse(Path.GetFileNameWithoutExtension(path), out var n) ? n : -1))
            .Where(x => x.Number >= 1 && !Path.GetFullPath(x.Path).Equals(Path.GetFullPath(options.SmallPath), StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Number).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Select(x => x.Path).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("В папке work нет файлов 1.txt, 2.txt и т. д.");

        log("Загружаю small.txt в память...");
        var reference = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = OpenReader(options.SmallPath, options.Encoding))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(token)) is not null)
            {
                token.ThrowIfCancellationRequested();
                if (line.Length > 0) reference.Add(line);
            }
        }
        log($"Загружено {reference.Count:N0} уникальных строк. Найдено файлов: {files.Length}.");
        long checkedLines = 0, hits = 0, completedBytes = 0;
        var totalBytes = files.Sum(FileLength);
        var timer = Stopwatch.StartNew();
        StreamWriter? output = null;
        try
        {
            for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
            {
                token.ThrowIfCancellationRequested();
                var file = files[fileIndex];
                var fileName = Path.GetFileName(file);
                var length = FileLength(file);
                log($"Обрабатываю {fileName} ({length:N0} байт)");
                using (var reader = OpenReader(file, options.Encoding))
                {
                    long lineNumber = 0;
                    while (true)
                    {
                        _gate.Wait(token);
                        var chunk = new List<(long Number, string Text)>(options.ChunkLines);
                        while (chunk.Count < options.ChunkLines)
                        {
                            var line = await reader.ReadLineAsync(token);
                            if (line is null) break;
                            chunk.Add((++lineNumber, line));
                        }
                        if (chunk.Count == 0) break;
                        var found = new List<Match>();
                        var sync = new object();
                        Parallel.ForEach(chunk, new ParallelOptions { MaxDegreeOfParallelism = options.Threads, CancellationToken = token }, item =>
                        {
                            _gate.Wait(token);
                            if (reference.Contains(item.Text)) lock (sync) found.Add(new Match(fileName, item.Number, item.Text));
                        });
                        foreach (var match in found.OrderBy(x => x.LineNumber))
                        {
                            output ??= new StreamWriter(new FileStream(options.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                            await output.WriteLineAsync($"{match.FileName}\t{match.LineNumber}\t{match.Value}".AsMemory(), token);
                            onMatch(match);
                            hits++;
                        }
                        if (output is not null) await output.FlushAsync(token);
                        checkedLines += chunk.Count;
                        var bytes = completedBytes + Math.Min(length, reader.BaseStream.Position);
                        progress.Report(new(fileName, fileIndex, files.Length, checkedLines, hits, totalBytes == 0 ? 0 : 100.0 * bytes / totalBytes, checkedLines / Math.Max(0.001, timer.Elapsed.TotalSeconds), Process.GetCurrentProcess().WorkingSet64));
                    }
                }
                // The input handle is closed before any move or deletion. A failed read or cancellation never reaches this block.
                if (options.Disposition == SourceDisposition.Move)
                {
                    var destination = Path.Combine(options.WorkPath, "processed");
                    Directory.CreateDirectory(destination);
                    var target = Path.Combine(destination, fileName);
                    if (File.Exists(target)) throw new IOException($"Уже существует {target}; исходник оставлен на месте.");
                    File.Move(file, target);
                    log($"Перемещён: {fileName}");
                }
                else if (options.Disposition == SourceDisposition.Delete)
                {
                    File.Delete(file);
                    log($"Удалён: {fileName}");
                }
                completedBytes += length;
                progress.Report(new(fileName, fileIndex + 1, files.Length, checkedLines, hits, totalBytes == 0 ? 100 : 100.0 * completedBytes / totalBytes, checkedLines / Math.Max(0.001, timer.Elapsed.TotalSeconds), Process.GetCurrentProcess().WorkingSet64));
            }
            log($"Готово. Проверено {checkedLines:N0} строк; совпадений {hits:N0}; время {timer.Elapsed:g}.");
        }
        finally { if (output is not null) await output.DisposeAsync(); }
    }

    private static long FileLength(string path) => new FileInfo(path).Length;
    private static StreamReader OpenReader(string path, Encoding encoding) => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan), encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 1024 * 1024);
}

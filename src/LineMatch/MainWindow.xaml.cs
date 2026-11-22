using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace LineMatch;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Match> _matches = [];
    private readonly SearchEngine _engine = new();
    private CancellationTokenSource? _cancellation;
    private bool _running;
    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "config.json");

    public MainWindow()
    {
        InitializeComponent();
        MatchesGrid.ItemsSource = _matches;
        SmallPath.Text = Path.Combine(AppContext.BaseDirectory, "small.txt");
        WorkPath.Text = Path.Combine(AppContext.BaseDirectory, "work");
        try
        {
            if (File.Exists(_configPath))
            {
                var config = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_configPath));
                if (config is not null)
                {
                    SmallPath.Text = config.SmallPath;
                    WorkPath.Text = config.WorkPath;
                    Threads.Text = config.Threads.ToString();
                    ChunkSize.Text = config.ChunkSize.ToString();
                    EncodingChoice.SelectedIndex = Math.Clamp(config.EncodingIndex, 0, 2);
                    // Destructive mode is never restored automatically.
                }
            }
        }
        catch (Exception ex) { AppendLog($"Не удалось прочитать настройки: {ex.Message}"); }
        Closing += (_, _) => { _cancellation?.Cancel(); _engine.Resume(); SaveSettings(); };
    }

    private void BrowseSmall_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) SmallPath.Text = dialog.FileName;
    }

    private void BrowseWork_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true) WorkPath.Text = dialog.FolderName;
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (!int.TryParse(Threads.Text, out var threads) || threads is < 1 or > 64 || !int.TryParse(ChunkSize.Text, out var chunk) || chunk is < 100 or > 1000000)
        {
            MessageBox.Show(this, "Потоков: 1–64; буфер: 100–1 000 000 строк.", "Проверьте настройки"); return;
        }
        var disposition = Enum.Parse<SourceDisposition>(((ComboBoxItem)Disposition.SelectedItem).Tag.ToString()!);
        if (disposition == SourceDisposition.Delete && MessageBox.Show(this, "После успешной обработки каждый исходный файл будет безвозвратно удалён. Продолжить?", "Подтверждение удаления", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Encoding encoding = EncodingChoice.SelectedIndex switch { 1 => new UnicodeEncoding(false, true, true), 2 => new UnicodeEncoding(true, true, true), _ => new UTF8Encoding(false, true) };
        var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(SmallPath.Text))!, "success.txt");
        var options = new SearchOptions(SmallPath.Text, WorkPath.Text, output, threads, chunk, encoding, disposition);
        SaveSettings();
        _matches.Clear(); LogBox.Clear(); Progress.Value = 0;
        _cancellation = new CancellationTokenSource();
        _running = true; StartButton.IsEnabled = false; PauseButton.IsEnabled = true; StopButton.IsEnabled = true;
        Status.Text = "Запуск...";
        var logPath = Path.Combine(Path.GetDirectoryName(output)!, "line-match.log");
        try
        {
            var progress = new Progress<SearchProgress>(p =>
            {
                FilesCount.Text = $"{p.CompletedFiles} / {p.TotalFiles}";
                LinesCount.Text = p.CheckedLines.ToString("N0");
                HitsCount.Text = p.Hits.ToString("N0");
                Speed.Text = $"{p.LinesPerSecond:N0} строк/с";
                Memory.Text = $"{p.WorkingSetBytes / 1048576:N0} МБ";
                Progress.Value = Math.Clamp(p.Percent, 0, 100);
                Status.Text = $"{p.CurrentFile} — {p.Percent:N1}%";
            });
            await Task.Run(() => _engine.RunAsync(options, progress,
                match => Dispatcher.BeginInvoke(() => { if (_matches.Count < 10000) _matches.Add(match); }),
                line => Dispatcher.BeginInvoke(() => { AppendLog(line); File.AppendAllText(logPath, $"{DateTime.Now:O} {line}{Environment.NewLine}"); }),
                _cancellation.Token));
            Status.Text = "Готово";
        }
        catch (OperationCanceledException) { Status.Text = "Остановлено; текущий файл сохранён"; AppendLog("Остановлено пользователем."); }
        catch (Exception ex) { Status.Text = "Ошибка"; AppendLog($"ОШИБКА: {ex}"); MessageBox.Show(this, ex.Message, "Ошибка обработки", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _engine.Resume(); _cancellation.Dispose(); _cancellation = null; _running = false; StartButton.IsEnabled = true; PauseButton.IsEnabled = false; StopButton.IsEnabled = false; PauseButton.Content = "Пауза"; }
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_engine.IsPaused) { _engine.Resume(); PauseButton.Content = "Пауза"; AppendLog("Продолжено."); }
        else { _engine.Pause(); PauseButton.Content = "Продолжить"; AppendLog("Пауза."); }
    }
    private void Stop_Click(object sender, RoutedEventArgs e) { _cancellation?.Cancel(); _engine.Resume(); }
    private void AppendLog(string message) { LogBox.AppendText(message + Environment.NewLine); LogBox.ScrollToEnd(); }
    private void SaveSettings()
    {
        try { File.WriteAllText(_configPath, JsonSerializer.Serialize(new Settings(SmallPath.Text, WorkPath.Text, int.TryParse(Threads.Text, out var t) ? t : 4, int.TryParse(ChunkSize.Text, out var c) ? c : 10000, EncodingChoice.SelectedIndex), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }
    private sealed record Settings(string SmallPath, string WorkPath, int Threads, int ChunkSize, int EncodingIndex);
}

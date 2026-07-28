using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Backstage.Desktop;

/// <summary>Linha da lista de resultados.</summary>
public sealed class ChartRow
{
    public Chart Chart { get; init; }
    public string Name => Chart.Name ?? "?";
    public string Artist => Chart.Artist ?? "?";
    public string Genre => Chart.Genre ?? "-";
    public string Charter => Chart.Charter ?? "?";
    public string ArtUrl => Chart.AlbumArtMd5 is { Length: > 0 } md5 ? $"https://files.enchor.us/{md5}.jpg" : null;
    public string DiffText { get; init; }
    public string LengthText { get; init; }
    public string OwnedText { get; init; }
    public Brush OwnedBrush { get; init; }
    public string DownloadText { get; init; }
}

public partial class MainWindow : Window
{
    readonly ChorusClient _chorus = new();
    readonly ObservableCollection<ChartRow> _rows = new();
    readonly HashSet<string> _ownedSongs = new();
    readonly HashSet<string> _ownedCharts = new();
    readonly Queue<Chart> _queue = new();
    SearchResult _last;
    string _lastQuery = "";
    bool _busy, _loadingMore, _downloading;
    int _completed;

    // Pasta do jogo: padrao Documents\Clone Hero, sobrescreve com CLONEHERO_DIR.
    static readonly string GameDir =
        Environment.GetEnvironmentVariable("CLONEHERO_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clone Hero");

    static readonly string[] FieldValues = { null, "artist", "name", "genre", "charter", "album" };
    static readonly string[] InstValues = { null, "guitar", "bass", "drums", "keys" };
    static readonly string[] DiffValues = { null, "expert", "hard", "medium", "easy" };

    public MainWindow()
    {
        InitializeComponent();
        Results.ItemsSource = _rows;
        FieldCombo.ItemsSource = new[] { "Em: Tudo", "Em: Artista", "Em: Música", "Em: Gênero", "Em: Charter", "Em: Álbum" };
        InstCombo.ItemsSource = new[] { "Inst: Qualquer", "Inst: Guitarra", "Inst: Baixo", "Inst: Bateria", "Inst: Teclas" };
        DiffCombo.ItemsSource = new[] { "Dif: Qualquer", "Dif: Expert", "Dif: Hard", "Dif: Medium", "Dif: Easy" };
        FieldCombo.SelectedIndex = InstCombo.SelectedIndex = DiffCombo.SelectedIndex = 0;
        FieldCombo.SelectionChanged += (_, _) => Research();
        InstCombo.SelectionChanged += (_, _) => Research();
        DiffCombo.SelectionChanged += (_, _) => Research();
        LoadLibrary();
        SearchBox.Focus();
    }

    /// <summary>Le o export que o plugin escreve no boot do jogo (backstage_library.txt).</summary>
    void LoadLibrary()
    {
        try
        {
            var path = Path.Combine(GameDir, "backstage_library.txt");
            if (!File.Exists(path))
            {
                StatusText.Text = $"biblioteca local nao encontrada em {path} — abra o Clone Hero uma vez com o mod pra gerar";
                return;
            }
            foreach (var line in File.ReadLines(path))
            {
                var full = line.Trim();
                if (full.Length == 0) continue;
                _ownedCharts.Add(full);
                int cut = full.LastIndexOf('|');
                if (cut > 0) _ownedSongs.Add(full[..cut]);
            }
            StatusText.Text = $"{_ownedSongs.Count} músicas locais carregadas pro dedup · digite e aperte Enter";
        }
        catch (Exception e) { StatusText.Text = $"biblioteca local: {e.Message}"; }
    }

    static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

    void Research() { if (_last != null && !_busy) _ = SearchAsync(_lastQuery, reset: true); }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) _ = SearchAsync(SearchBox.Text, reset: true); }

    void SearchBtn_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(SearchBox.Text, reset: true);

    async Task SearchAsync(string query, bool reset)
    {
        if (_busy || string.IsNullOrWhiteSpace(query)) return;
        _busy = true;
        _lastQuery = query;
        SearchBtn.Content = "...";
        StatusText.Text = $"buscando \"{query}\"...";

        try
        {
            string field = FieldValues[Math.Max(0, FieldCombo.SelectedIndex)];
            string inst = InstValues[Math.Max(0, InstCombo.SelectedIndex)];
            string diff = DiffValues[Math.Max(0, DiffCombo.SelectedIndex)];

            var result = field == null
                ? await _chorus.SearchAsync(query, inst, diff)
                : await _chorus.SearchFieldAsync(field, query, inst, diff);

            _last = result;
            if (reset) { _rows.Clear(); Scroller.ScrollToTop(); }
            foreach (var chart in result.Data) _rows.Add(Row(chart));
            StatusText.Text = $"{result.Found} charts para \"{query}\"" +
                              (field != null ? $" em {field}" : "") +
                              (inst != null ? $" · {inst}" : "") + (diff != null ? $" · {diff}" : "");
        }
        catch (Exception ex) { StatusText.Text = $"busca falhou: {ex.Message}"; }
        finally { _busy = false; SearchBtn.Content = "Buscar"; }
    }

    ChartRow Row(Chart chart)
    {
        var key = Norm(chart.Artist) + "|" + Norm(chart.Name);
        bool ownedChart = _ownedCharts.Contains(key + "|" + Norm(chart.Charter));
        bool ownedSong = ownedChart || _ownedSongs.Contains(key);

        int? diff = InstCombo.SelectedIndex switch
        {
            2 => chart.DiffBass, 3 => chart.DiffDrums, 4 => chart.DiffKeys,
            _ => chart.DiffGuitar,
        };

        return new ChartRow
        {
            Chart = chart,
            DiffText = diff is > 0 ? diff.ToString() : "-",
            LengthText = chart.SongLengthMs is > 0
                ? TimeSpan.FromMilliseconds(chart.SongLengthMs.Value).ToString(@"m\:ss") : "-",
            OwnedText = ownedChart ? "✔ já tem este" : ownedSong ? "≈ tem a música" : "",
            OwnedBrush = ownedChart ? (Brush)FindResource("Green") : (Brush)FindResource("Blue"),
            DownloadText = ownedChart ? "de novo" : "Baixar",
        };
    }

    /// <summary>Rolagem infinita: perto do fim, puxa a proxima pagina (so busca geral).</summary>
    async void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_last == null || _busy || _loadingMore) return;
        if (FieldCombo.SelectedIndex != 0) return;
        if (_rows.Count >= _last.Found) return;
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 200) return;

        _loadingMore = true;
        try
        {
            string inst = InstValues[Math.Max(0, InstCombo.SelectedIndex)];
            string diff = DiffValues[Math.Max(0, DiffCombo.SelectedIndex)];
            var more = await _chorus.SearchAsync(_lastQuery, inst, diff, _rows.Count / 25 + 1);
            foreach (var chart in more.Data) _rows.Add(Row(chart));
        }
        catch { /* fim silencioso; a proxima rolagem tenta de novo */ }
        finally { _loadingMore = false; }
    }

    void Preview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChartRow row) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://enchor.us/chart/{row.Chart.Md5}") { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = $"nao abriu o navegador: {ex.Message}"; }
    }

    void Download_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChartRow row) return;
        _queue.Enqueue(row.Chart);
        StatusText.Text = $"na fila: {row.Name} ({_queue.Count} aguardando)";
        _ = PumpQueueAsync();
    }

    async Task PumpQueueAsync()
    {
        if (_downloading) return;
        _downloading = true;
        DlBar.Visibility = Visibility.Visible;

        try
        {
            while (_queue.Count > 0)
            {
                var chart = _queue.Dequeue();
                var dest = Path.Combine(GameDir, "Songs", "Backstage");
                var progress = new Progress<(long done, long total)>(p =>
                {
                    DlBar.Maximum = Math.Max(1, p.total);
                    DlBar.Value = p.done;
                    StatusText.Text = $"baixando {chart.Name}  {p.done / 1048576f:F1}/{p.total / 1048576f:F1} MB · fila: {_queue.Count}";
                });

                try
                {
                    await _chorus.DownloadSngAsync(chart, dest, progress);
                    _completed++;
                    ScanBtn.Content = $"Escanear no jogo ({_completed} baixada{(_completed > 1 ? "s" : "")})";
                }
                catch (Exception ex) { StatusText.Text = $"download falhou: {ex.Message}"; }
            }
            StatusText.Text = $"{_completed} baixada(s) — clique em Escanear pro jogo enxergar (com o CH aberto)";
        }
        finally { _downloading = false; DlBar.Visibility = Visibility.Collapsed; }
    }

    /// <summary>Manda o plugin (dentro do jogo) apertar o Scan Songs nativo, via arquivo de comando.</summary>
    void Scan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            File.WriteAllText(Path.Combine(GameDir, "backstage_cmd.txt"), "scan");
            StatusText.Text = "scan enviado — se o Clone Hero estiver aberto com o mod, a biblioteca atualiza sozinha";
        }
        catch (Exception ex) { StatusText.Text = $"scan falhou: {ex.Message}"; }
    }
}

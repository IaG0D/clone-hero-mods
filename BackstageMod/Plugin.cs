using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace Backstage;

[BepInPlugin(Id, "Backstage", "0.1.0")]
public class BackstagePlugin : BasePlugin
{
    public const string Id = "com.iag0d.backstage";

    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        L.LogInfo("Backstage 0.1.0 — by IaG0D (F5 abre/fecha)");
        AddComponent<BackstageUI>();
    }
}

/// <summary>
/// UI v0 em IMGUI: feia de proposito, funcional primeiro (decisao do CONTEXT.md — logica
/// com UI feia, bonita depois). Busca no Chorus, marca o que voce ja tem, fila de download
/// com progresso, e gatilho do Scan Songs no fim.
/// </summary>
public class BackstageUI : MonoBehaviour
{
    public BackstageUI(IntPtr ptr) : base(ptr) { }

    // ---- estado (so main thread encosta em objetos do jogo) ----
    static readonly ConcurrentQueue<Action> _onMain = new();
    static ChorusClient _chorus;
    static bool _visible;
    static string _query = "";
    static string _status = "digite e clique Buscar";
    static SearchResult _results;
    static readonly HashSet<string> _ownedSongs = new();    // artista|titulo
    static readonly HashSet<string> _ownedCharts = new();   // artista|titulo|charter
    static int _ownedFrom = -1;
    static readonly Queue<Chart> _queue = new();
    static Chart _downloading;
    static long _dlDone, _dlTotal;
    static int _completed;
    static bool _busy;

    static string SongsDir => Path.Combine(BepInEx.Paths.GameRootPath, "Songs", "Backstage");
    static readonly string CmdPath = Path.Combine(BepInEx.Paths.GameRootPath, "backstage_cmd.txt");
    static int _pollFrame;

    void Update()
    {
        while (_onMain.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { BackstagePlugin.L.LogError($"Backstage (main): {e}"); }
        }

        try
        {
            if (Input.GetKeyDown(KeyCode.F5)) _visible = !_visible;

            // Digitacao do campo de busca capturada aqui: GUI.TextField foi stripped do
            // build IL2CPP (unstripping falha), entao o campo e Box+Label e o teclado entra
            // por Input.inputString.
            if (_visible)
            {
                foreach (var c in Input.inputString)
                {
                    if (c == '\b') { if (_query.Length > 0) _query = _query[..^1]; }
                    else if (c is '\n' or '\r') StartSearch();
                    else if (!char.IsControl(c)) _query += c;
                }
            }
        }
        catch { /* input legado indisponivel: cmd show/hide/search cobre */ }

        RefreshOwned();
        PumpQueue();
        if (++_pollFrame >= 30) { _pollFrame = 0; PollCmd(); }
    }

    /// <summary>Dedup por artista+titulo normalizados contra a lista-mestre do jogo.
    /// ponytail: md5 local exigiria hashear 17k pastas; artista+titulo cobre o caso real
    /// ("nao baixar o que ja tenho"). Charter junto = "ja tenho ESTE chart".</summary>
    static void RefreshOwned()
    {
        var master = Anchors.MasterSongs;
        if (master == null || _ownedFrom == master.Count) return;

        _ownedSongs.Clear();
        _ownedCharts.Clear();
        for (int i = 0; i < master.Count; i++)
        {
            var song = master[i];
            if (song == null) continue;
            var key = Norm(song.Artist_StrippedTags) + "|" + Norm(song.Name_StrippedTags);
            _ownedSongs.Add(key);
            _ownedCharts.Add(key + "|" + Norm(song.Charter_StrippedTags));
        }
        _ownedFrom = master.Count;
        BackstagePlugin.L.LogInfo($"dedup: {_ownedFrom} musicas locais indexadas.");
    }

    static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

    static void StartSearch()
    {
        if (_busy || string.IsNullOrWhiteSpace(_query)) return;
        _busy = true;
        _status = $"buscando \"{_query}\"...";
        _chorus ??= new ChorusClient();

        var query = _query;
        Task.Run(async () =>
        {
            try
            {
                var result = await _chorus.SearchAsync(query);
                _onMain.Enqueue(() =>
                {
                    _results = result;
                    _status = $"{result.Found} charts no Chorus para \"{query}\"";
                    _busy = false;
                });
            }
            catch (Exception e)
            {
                _onMain.Enqueue(() => { _status = $"busca falhou: {e.Message}"; _busy = false; });
            }
        });
    }

    static void PumpQueue()
    {
        if (_downloading != null || _queue.Count == 0) return;

        _downloading = _queue.Dequeue();
        _dlDone = 0; _dlTotal = -1;
        var chart = _downloading;
        _chorus ??= new ChorusClient();

        Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<(long done, long total)>(p =>
                {
                    _dlDone = p.done; _dlTotal = p.total;
                });
                var path = await _chorus.DownloadSngAsync(chart, SongsDir, progress);
                _onMain.Enqueue(() =>
                {
                    _completed++;
                    _downloading = null;
                    _status = $"baixado: {Path.GetFileName(path)} — {_completed} na sessao. " +
                              "Clique 'Escanear' quando terminar a fila.";
                });
            }
            catch (Exception e)
            {
                _onMain.Enqueue(() => { _downloading = null; _status = $"download falhou: {e.Message}"; });
            }
        });
    }

    static SongScan FindScan()
    {
        // Find* genericos com bool sao stripped; este e metodo il2cpp nativo de verdade
        // (tem ponteiro no interop), e o "true" inclui objetos inativos.
        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<SongScan>(), includeInactive: true);
        if (all == null) return null;
        for (int i = 0; i < all.Length; i++)
        {
            var scan = all[i].TryCast<SongScan>();
            if (scan != null) return scan;
        }
        return null;
    }

    static void TriggerScan()
    {
        var scan = FindScan();
        if (scan == null) { _status = "SongScan nao encontrado nesta tela."; return; }
        try
        {
            Anchors.TriggerFullScan(scan);
            _status = $"rescan disparado; isScanning={Anchors.IsScanning(scan)}";
            BackstagePlugin.L.LogInfo(_status);
        }
        catch (Exception e) { _status = $"scan falhou: {e.Message}"; }
    }

    // ---- UI ----

    void OnGUI()
    {
        if (!_visible) return;

        // Compacto e no canto de baixo, sem tampar o menu — feedback do jogador.
        // ponytail: isto e prototipo de mouse; a versao final e tela nativa clonada do jogo.
        const int W = 720, H = 380;
        float x = (Screen.width - W) / 2f, y = Screen.height - H - 96;

        GUI.Box(new Rect(x, y, W, H), "");
        GUI.Label(new Rect(x + 16, y + 10, 400, 24), "<b>Backstage</b> — busca e download do Chorus Encore");
        GUI.Label(new Rect(x + W - 110, y + 10, 100, 24), "by IaG0D");

        // busca — campo "fake" (GUI.TextField e stripped no IL2CPP); teclado entra pelo Update
        GUI.Box(new Rect(x + 16, y + 40, W - 200, 26), "");
        GUI.Label(new Rect(x + 22, y + 43, W - 212, 22), (_query ?? "") + "▌");
        if (GUI.Button(new Rect(x + W - 176, y + 40, 80, 26), _busy ? "..." : "Buscar") && !_busy)
            StartSearch();
        if (GUI.Button(new Rect(x + W - 90, y + 40, 74, 26), "Fechar"))
            _visible = false;

        // resultados
        float rowY = y + 80;
        if (_results != null)
        {
            GUI.Label(new Rect(x + 16, rowY - 4, 60, 20), "<i>tem?</i>");
            GUI.Label(new Rect(x + 70, rowY - 4, 330, 20), "<i>música</i>");
            GUI.Label(new Rect(x + 404, rowY - 4, 200, 20), "<i>artista</i>");
            GUI.Label(new Rect(x + 608, rowY - 4, 150, 20), "<i>charter</i>");
            rowY += 22;

            int shown = 0;
            foreach (var chart in _results.Data)
            {
                if (shown++ >= 7) break; // painel compacto: 7 linhas
                var key = Norm(chart.Artist) + "|" + Norm(chart.Name);
                var ownedChart = _ownedCharts.Contains(key + "|" + Norm(chart.Charter));
                var ownedSong = ownedChart || _ownedSongs.Contains(key);

                GUI.Label(new Rect(x + 16, rowY, 60, 22), ownedChart ? "✔ este" : ownedSong ? "≈ tem" : "");
                GUI.Label(new Rect(x + 70, rowY, 330, 22), chart.Name ?? "?");
                GUI.Label(new Rect(x + 404, rowY, 200, 22), chart.Artist ?? "?");
                GUI.Label(new Rect(x + 608, rowY, 150, 22), chart.Charter ?? "?");

                if (GUI.Button(new Rect(x + W - 96, rowY, 80, 22), ownedChart ? "de novo" : "Baixar"))
                {
                    _queue.Enqueue(chart);
                    _status = $"na fila: {chart.Artist} - {chart.Name} ({_queue.Count} aguardando)";
                }
                rowY += 26;
            }
        }

        // rodape: fila + progresso + scan
        float footY = y + H - 66;
        if (_downloading != null)
        {
            var pct = _dlTotal > 0 ? (float)_dlDone / _dlTotal : 0f;
            GUI.Box(new Rect(x + 16, footY, W - 32, 20), "");
            GUI.Box(new Rect(x + 16, footY, (W - 32) * pct, 20), "");
            GUI.Label(new Rect(x + 22, footY, W - 44, 20),
                $"baixando {_downloading.Artist} - {_downloading.Name}  {_dlDone / 1048576f:F1}/{(_dlTotal > 0 ? _dlTotal / 1048576f : 0):F1} MB  (fila: {_queue.Count})");
        }
        if (GUI.Button(new Rect(x + 16, footY + 26, 130, 26), "Escanear agora"))
            TriggerScan();
        GUI.Label(new Rect(x + 156, footY + 30, W - 172, 22), _status);
    }

    // ---- canal de comando p/ teste autonomo (sai na 1.0) ----

    static void PollCmd()
    {
        try
        {
            if (!File.Exists(CmdPath)) return;
            var cmd = File.ReadAllText(CmdPath).Trim();
            File.Delete(CmdPath);
            BackstagePlugin.L.LogInfo($"cmd: {cmd}");

            var parts = cmd.Split(' ', 2);
            switch (parts[0])
            {
                case "show": _visible = true; break;
                case "hide": _visible = false; break;
                case "search": _query = parts[1]; StartSearch(); break;
                case "dl":
                    int i = int.Parse(parts[1]);
                    if (_results != null && i < _results.Data.Count) _queue.Enqueue(_results.Data[i]);
                    break;
                case "scan": TriggerScan(); break;
                case "state":
                    var scan = FindScan();
                    BackstagePlugin.L.LogInfo(
                        $"state: visible={_visible} results={_results?.Data.Count ?? -1} fila={_queue.Count} " +
                        $"baixando={_downloading?.Name ?? "-"} completos={_completed} master={Anchors.MasterSongs?.Count} " +
                        $"songScan={(scan == null ? "null" : Anchors.IsScanning(scan) ? "SCANNING" : "idle")}");
                    break;
            }
        }
        catch (Exception e) { BackstagePlugin.L.LogError($"cmd falhou: {e.Message}"); }
    }
}

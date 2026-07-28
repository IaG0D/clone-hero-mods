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

[BepInPlugin(Id, "Backstage", "0.2.0")]
public class BackstagePlugin : BasePlugin
{
    public const string Id = "com.iag0d.backstage";

    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        L.LogInfo("Backstage 0.2.0 — by IaG0D (F5 abre/fecha)");
        AddComponent<BackstageUI>();
    }
}

/// <summary>
/// Painel de busca/download do Chorus. IMGUI (prototipo de mouse; a janela desktop separada
/// e o proximo passo). Com o painel aberto os mapas de input do Rewired sao desligados para
/// digitar nao navegar o menu por tras (s/a sao frets no teclado).
/// </summary>
public class BackstageUI : MonoBehaviour
{
    public BackstageUI(IntPtr ptr) : base(ptr) { }

    // ---- estado (so main thread encosta em objetos do jogo) ----
    static readonly ConcurrentQueue<Action> _onMain = new();
    static ChorusClient _chorus;
    static bool _visible;
    static string _query = "";
    static string _status = "digite a busca e aperte Enter";
    static SearchResult _results;
    static readonly HashSet<string> _ownedSongs = new();
    static readonly HashSet<string> _ownedCharts = new();
    static int _ownedFrom = -1;
    static readonly Queue<Chart> _queue = new();
    static Chart _downloading;
    static long _dlDone, _dlTotal;
    static int _completed;
    static bool _busy;

    // filtros (valores da API, mapeados do codigo do Bridge)
    static readonly string[] InstValues = { null, "guitar", "bass", "drums", "keys" };
    static readonly string[] InstNames = { "Inst: Qualquer", "Inst: Guitarra", "Inst: Baixo", "Inst: Bateria", "Inst: Teclas" };
    static readonly string[] DiffValues = { null, "expert", "hard", "medium", "easy" };
    static readonly string[] DiffNames = { "Dif: Qualquer", "Dif: Expert", "Dif: Hard", "Dif: Medium", "Dif: Easy" };
    // campo da busca: geral (tudo) ou um campo especifico via /search/advanced
    static readonly string[] FieldValues = { null, "artist", "name", "charter", "album" };
    static readonly string[] FieldNames = { "Em: Tudo", "Em: Artista", "Em: Música", "Em: Charter", "Em: Álbum" };
    static int _inst, _diff, _field;

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
            if (Input.GetKeyDown(KeyCode.F5)) SetVisible(!_visible);
            if (_visible && Input.GetKeyDown(KeyCode.Escape)) SetVisible(false);

            // GUI.TextField e stripped no IL2CPP: o campo e desenhado na mao e o teclado
            // entra por aqui.
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
        catch { /* input legado indisponivel: cmd cobre */ }

        RefreshOwned();
        PumpQueue();
        if (++_pollFrame >= 30) { _pollFrame = 0; PollCmd(); }
    }

    void OnDestroy() => SetGameInput(true); // nunca deixar o jogo sem input

    static void SetVisible(bool visible)
    {
        _visible = visible;
        SetGameInput(!visible);
    }

    /// <summary>Desliga/religa os mapas de input do Rewired dos jogadores. Sem isso, digitar
    /// no painel navega o menu por tras (s/a/setas sao bindings do jogo).</summary>
    static void SetGameInput(bool enabled)
    {
        try
        {
            var players = Rewired.ReInput.players;
            for (int i = 0; i < players.playerCount; i++)
                players.GetPlayer(i).controllers.maps.SetAllMapsEnabled(enabled);
            BackstagePlugin.L.LogInfo($"input do jogo {(enabled ? "religado" : "bloqueado")}");
        }
        catch (Exception e)
        {
            BackstagePlugin.L.LogWarning($"nao consegui alternar input do jogo: {e.Message}");
        }
    }

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

        string query = _query, inst = InstValues[_inst], diff = DiffValues[_diff], field = FieldValues[_field];
        Task.Run(async () =>
        {
            try
            {
                var result = field == null
                    ? await _chorus.SearchAsync(query, inst, diff)
                    : await _chorus.SearchFieldAsync(field, query, inst, diff);
                _onMain.Enqueue(() =>
                {
                    _results = result;
                    _status = $"{result.Found} charts para \"{query}\"" +
                              (field != null ? $" em {field}" : "") +
                              (inst != null ? $" · {inst}" : "") + (diff != null ? $" · {diff}" : "");
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
                var progress = new Progress<(long done, long total)>(p => { _dlDone = p.done; _dlTotal = p.total; });
                var path = await _chorus.DownloadSngAsync(chart, SongsDir, progress);
                _onMain.Enqueue(() =>
                {
                    _completed++;
                    _downloading = null;
                    _status = $"baixado: {Path.GetFileName(path)} — clique Escanear quando terminar a fila.";
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
            _status = "rescan rodando — a lista atualiza sozinha ao terminar.";
            BackstagePlugin.L.LogInfo("rescan disparado");
        }
        catch (Exception e) { _status = $"scan falhou: {e.Message}"; }
    }

    // ---- UI ----

    const string Gold = "#ffd75e";
    const string Dim = "#9ab0c4";
    const string Green = "#7dde8b";
    const string Blue = "#7fd4ff";

    // Texturas solidas: e o que da cara de painel de verdade em vez de caixa cinza do IMGUI.
    static Texture2D _texPanel, _texHeader, _texRowA, _texRowB, _texInput, _texBarBg, _texBarFill, _texAccent;
    static bool _texReady, _texFailed;

    static Texture2D Solid(float r, float g, float b, float a)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        t.SetPixel(0, 0, new Color(r, g, b, a));
        t.Apply();
        return t;
    }

    static void EnsureTextures()
    {
        if (_texReady || _texFailed) return;
        try
        {
            _texPanel = Solid(0.07f, 0.09f, 0.12f, 0.97f);   // fundo
            _texHeader = Solid(0.10f, 0.13f, 0.17f, 1f);      // barra de titulo
            _texRowA = Solid(0.09f, 0.11f, 0.15f, 1f);        // zebra A
            _texRowB = Solid(0.07f, 0.09f, 0.12f, 1f);        // zebra B
            _texInput = Solid(0.05f, 0.06f, 0.09f, 1f);       // campo de busca
            _texBarBg = Solid(0.05f, 0.06f, 0.09f, 1f);       // trilho do progresso
            _texBarFill = Solid(1f, 0.84f, 0.37f, 1f);        // preenchimento dourado
            _texAccent = Solid(1f, 0.84f, 0.37f, 1f);         // filete de destaque
            _texReady = true;
        }
        catch { _texFailed = true; /* cai no visual de caixas */ }
    }

    static void Panel(Rect r, Texture2D tex)
    {
        if (_texReady)
        {
            // DrawTexture pode estar stripped (como TextField e Find*<T> estavam);
            // na primeira falha degrada para Box e nunca mais tenta.
            try { GUI.DrawTexture(r, tex); return; }
            catch { _texReady = false; _texFailed = true; }
        }
        GUI.Box(r, "");
    }

    void OnGUI()
    {
        if (!_visible) return;
        EnsureTextures();

        const int W = 820, H = 460;
        float x = (Screen.width - W) / 2f, y = Screen.height - H - 84;

        Panel(new Rect(x, y, W, H), _texPanel);
        Panel(new Rect(x, y, W, 34), _texHeader);
        Panel(new Rect(x, y + 34, W, 2), _texAccent); // filete dourado sob o titulo
        GUI.Label(new Rect(x + 16, y + 7, 560, 24),
            $"<size=15><b><color={Gold}>BACKSTAGE</color></b></size>  <color={Dim}>busca e download · Chorus Encore</color>");
        GUI.Label(new Rect(x + W - 96, y + 9, 84, 20), $"<color={Dim}>by IaG0D</color>");

        // busca
        Panel(new Rect(x + 16, y + 46, W - 290, 28), _texInput);
        GUI.Label(new Rect(x + 24, y + 50, W - 306, 22), $"<size=13>{_query}<color={Gold}>▌</color></size>");
        if (GUI.Button(new Rect(x + W - 264, y + 46, 82, 28), _busy ? "..." : "Buscar") && !_busy) StartSearch();
        if (GUI.Button(new Rect(x + W - 176, y + 46, 150, 28), FieldNames[_field]))
        { _field = (_field + 1) % FieldValues.Length; if (_results != null) StartSearch(); }

        // filtros em linha propria; trocar refaz a busca na hora
        if (GUI.Button(new Rect(x + 16, y + 80, 130, 26), InstNames[_inst]))
        { _inst = (_inst + 1) % InstValues.Length; if (_results != null) StartSearch(); }
        if (GUI.Button(new Rect(x + 152, y + 80, 118, 26), DiffNames[_diff]))
        { _diff = (_diff + 1) % DiffValues.Length; if (_results != null) StartSearch(); }
        if (GUI.Button(new Rect(x + W - 44, y + 80, 28, 26), "✕")) SetVisible(false);

        // resultados
        float rowY = y + 114;
        if (_results != null && _results.Data.Count > 0)
        {
            Panel(new Rect(x + 8, rowY - 2, W - 16, 22), _texHeader);
            GUI.Label(new Rect(x + 16, rowY, 44, 20), $"<color={Dim}><i>tem?</i></color>");
            GUI.Label(new Rect(x + 64, rowY, 268, 20), $"<color={Dim}><i>música</i></color>");
            GUI.Label(new Rect(x + 336, rowY, 168, 20), $"<color={Dim}><i>artista</i></color>");
            GUI.Label(new Rect(x + 508, rowY, 118, 20), $"<color={Dim}><i>charter</i></color>");
            GUI.Label(new Rect(x + 630, rowY, 30, 20), $"<color={Dim}><i>dif</i></color>");
            GUI.Label(new Rect(x + 662, rowY, 46, 20), $"<color={Dim}><i>tempo</i></color>");
            rowY += 24;

            int shown = 0;
            foreach (var chart in _results.Data)
            {
                if (shown >= 8) break;
                Panel(new Rect(x + 8, rowY - 2, W - 16, 26), shown % 2 == 0 ? _texRowA : _texRowB);
                shown++;

                var key = Norm(chart.Artist) + "|" + Norm(chart.Name);
                var ownedChart = _ownedCharts.Contains(key + "|" + Norm(chart.Charter));
                var ownedSong = ownedChart || _ownedSongs.Contains(key);
                if (ownedSong) Panel(new Rect(x + 8, rowY - 2, 3, 26), _texAccent); // filete "voce tem"

                var diff = _inst switch
                {
                    2 => chart.DiffBass, 3 => chart.DiffDrums, 4 => chart.DiffKeys,
                    _ => chart.DiffGuitar,
                };
                var len = chart.SongLengthMs is > 0
                    ? TimeSpan.FromMilliseconds(chart.SongLengthMs.Value).ToString(@"m\:ss") : "-";

                GUI.Label(new Rect(x + 16, rowY, 44, 22),
                    ownedChart ? $"<color={Green}>✔ este</color>" : ownedSong ? $"<color={Blue}>≈ tem</color>" : "");
                GUI.Label(new Rect(x + 64, rowY, 268, 22), $"<size=13>{chart.Name}</size>");
                GUI.Label(new Rect(x + 336, rowY, 168, 22), $"<color={Dim}>{chart.Artist}</color>");
                GUI.Label(new Rect(x + 508, rowY, 118, 22), $"<color={Dim}>{chart.Charter}</color>");
                GUI.Label(new Rect(x + 630, rowY, 30, 22),
                    diff is > 0 ? $"<color={Gold}>{diff}</color>" : $"<color={Dim}>-</color>");
                GUI.Label(new Rect(x + 662, rowY, 46, 22), $"<color={Dim}>{len}</color>");

                if (GUI.Button(new Rect(x + W - 92, rowY - 1, 76, 23), ownedChart ? "de novo" : "Baixar"))
                {
                    _queue.Enqueue(chart);
                    _status = $"na fila: {chart.Name} ({_queue.Count} aguardando)";
                }
                rowY += 28;
            }

            GUI.Label(new Rect(x + 16, rowY + 2, W - 32, 20),
                $"<color={Dim}><size=11>mostrando {Math.Min(8, _results.Data.Count)} de {_results.Found} — refine a busca para ver outros</size></color>");
        }
        else if (_results != null)
        {
            GUI.Label(new Rect(x + 16, rowY, W - 32, 22), $"<color={Dim}>nada encontrado com esses filtros.</color>");
        }

        // rodape
        float footY = y + H - 64;
        if (_downloading != null)
        {
            var pct = _dlTotal > 0 ? (float)_dlDone / _dlTotal : 0f;
            Panel(new Rect(x + 16, footY, W - 32, 16), _texBarBg);
            Panel(new Rect(x + 16, footY, (W - 32) * pct, 16), _texBarFill);
            GUI.Label(new Rect(x + 22, footY - 2, W - 44, 20),
                $"<size=11><color=#10151c><b>{_downloading.Name}  {_dlDone / 1048576f:F1}/{(_dlTotal > 0 ? _dlTotal / 1048576f : 0):F1} MB · fila: {_queue.Count}</b></color></size>");
        }
        if (GUI.Button(new Rect(x + 16, footY + 22, 130, 28), "Escanear agora")) TriggerScan();
        GUI.Label(new Rect(x + 156, footY + 26, W - 172, 22), $"<color={Dim}>{_status}</color>");
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
                case "show": SetVisible(true); break;
                case "hide": SetVisible(false); break;
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

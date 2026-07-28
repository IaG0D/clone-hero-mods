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

[BepInPlugin(Id, "Backstage", "0.5.0")]
public class BackstagePlugin : BasePlugin
{
    public const string Id = "com.iag0d.backstage";

    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        L.LogInfo("Backstage 0.5.0 — by IaG0D (F5 abre/fecha)");
        AddComponent<BackstageUI>();

        // O Control Remapper abre com Espaco por fora dos mapas do Rewired (Input System
        // direto), entao desligar mapas nao basta: com o painel aberto, o Open() dele
        // simplesmente nao roda.
        try
        {
            var harmony = new HarmonyLib.Harmony(Id);
            int patched = 0;
            foreach (var m in typeof(Rewired.UI.ControlMapper.ControlMapper).GetMethods())
                if (m.Name is "Open" or "Toggle" && !m.IsGenericMethod)
                {
                    try
                    {
                        harmony.Patch(m, prefix: new HarmonyLib.HarmonyMethod(
                            typeof(BackstageUI).GetMethod(nameof(BackstageUI.BlockWhenPanelOpen))));
                        patched++;
                    }
                    catch { }
                }
            L.LogInfo($"ControlMapper: {patched} metodo(s) guardado(s) contra abrir com o painel ativo.");
        }
        catch (Exception e) { L.LogWarning($"guarda do ControlMapper falhou: {e.Message}"); }
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
    static readonly string[] FieldValues = { null, "artist", "name", "genre", "charter", "album" };
    static readonly string[] FieldNames = { "Em: Tudo", "Em: Artista", "Em: Música", "Em: Gênero", "Em: Charter", "Em: Álbum" };
    static int _inst, _diff, _field;
    static int _scroll;          // primeira linha visivel dos resultados
    static bool _loadingMore;    // buscando a proxima pagina da API

    /// <summary>Prefix Harmony: com o painel aberto, bloqueia o metodo original (ex.: abrir
    /// o Control Remapper com Espaco).</summary>
    public static bool BlockWhenPanelOpen() => !_visible;

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

                // scroll dos resultados pela roda do mouse
                var wheel = Input.mouseScrollDelta.y;
                if (wheel > 0f) Scroll(-1);
                else if (wheel < 0f) Scroll(1);
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
            // O system player fica fora do laco acima e e ele que abre o Control Remapper
            // com Espaco — sem desligar aqui, digitar espaco na busca abria o remapper.
            players.SystemPlayer?.controllers?.maps?.SetAllMapsEnabled(enabled);
            BackstagePlugin.L.LogInfo($"input do jogo {(enabled ? "religado" : "bloqueado")} (players + system)");
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

    /// <summary>Move a janela de resultados; chegando perto do fim, puxa a proxima pagina.</summary>
    static void Scroll(int delta)
    {
        if (_results == null) return;
        _scroll = Math.Max(0, Math.Min(_scroll + delta, Math.Max(0, _results.Data.Count - 8)));

        // faltam menos de 8 pra acabar o carregado e a API tem mais? busca proxima pagina.
        if (!_loadingMore && !_busy && FieldValues[_field] == null &&
            _scroll + 16 > _results.Data.Count && _results.Data.Count < _results.Found)
        {
            _loadingMore = true;
            string query = _query;
            int page = _results.Data.Count / 25 + 1;
            string inst = InstValues[_inst], diff = DiffValues[_diff];
            Task.Run(async () =>
            {
                try
                {
                    var more = await _chorus.SearchAsync(query, inst, diff, page);
                    _onMain.Enqueue(() =>
                    {
                        if (_results != null && _query == query) _results.Data.AddRange(more.Data);
                        _loadingMore = false;
                    });
                }
                catch { _onMain.Enqueue(() => _loadingMore = false); }
            });
        }
    }

    static void StartSearch()
    {
        if (_busy || string.IsNullOrWhiteSpace(_query)) return;
        _busy = true;
        _scroll = 0;
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
    static Texture2D _texPanel, _texHeader, _texRowA, _texRowB, _texInput, _texBarBg, _texBarFill, _texAccent, _texEdge, _texBtn, _texBtnHover;
    static bool _texReady, _texFailed;

    // Botao com cara propria: azul-escuro, texto dourado, hover. GUIStyle customizado e
    // outra area minada de stripping, entao tudo guardado com fallback pro botao padrao.
    static GUIStyle _btnStyle;
    static bool _btnTried;

    static bool NiceButton(Rect r, string label)
    {
        if (!_btnTried)
        {
            _btnTried = true;
            try
            {
                _btnStyle = new GUIStyle(GUI.skin.button);
                _btnStyle.normal.background = _texBtn;
                _btnStyle.hover.background = _texBtnHover;
                _btnStyle.active.background = _texBtnHover;
                _btnStyle.normal.textColor = new Color(1f, 0.84f, 0.37f);
                _btnStyle.hover.textColor = new Color(1f, 0.92f, 0.65f);
                _btnStyle.active.textColor = Color.white;
                _btnStyle.richText = true;
            }
            catch { _btnStyle = null; }
        }
        try { if (_btnStyle != null) return GUI.Button(r, label, _btnStyle); }
        catch { _btnStyle = null; }
        return GUI.Button(r, label);
    }

    /// <summary>Previa: abre a pagina do chart no enchor.us — o player oficial do Chorus toca
    /// a musica la. Previa nativa exigiria baixar o .sng inteiro (nao existe endpoint leve).</summary>
    static void OpenPreview(Chart chart)
    {
        var url = $"https://enchor.us/chart/{chart.Md5}";
        try { Application.OpenURL(url); return; }
        catch { }
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) { _status = $"nao abriu o navegador: {e.Message}"; }
    }

    /// <summary>Moldura de 2px em volta de um retangulo — o "fru fru" barato do IMGUI.</summary>
    static void Border(Rect r, Texture2D tex, float t = 2f)
    {
        Panel(new Rect(r.x, r.y, r.width, t), tex);                    // topo
        Panel(new Rect(r.x, r.yMax - t, r.width, t), tex);             // base
        Panel(new Rect(r.x, r.y, t, r.height), tex);                   // esquerda
        Panel(new Rect(r.xMax - t, r.y, t, r.height), tex);            // direita
    }

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
            _texPanel = Solid(0.055f, 0.07f, 0.10f, 1f);      // fundo OPACO: nada vaza por tras
            _texHeader = Solid(0.10f, 0.13f, 0.17f, 1f);      // barra de titulo
            _texRowA = Solid(0.085f, 0.105f, 0.145f, 1f);     // zebra A
            _texRowB = Solid(0.06f, 0.075f, 0.105f, 1f);      // zebra B
            _texInput = Solid(0.035f, 0.045f, 0.07f, 1f);     // campo de busca
            _texBarBg = Solid(0.035f, 0.045f, 0.07f, 1f);     // trilho do progresso
            _texBarFill = Solid(1f, 0.84f, 0.37f, 1f);        // preenchimento dourado
            _texAccent = Solid(1f, 0.84f, 0.37f, 1f);         // dourado de destaque
            _texEdge = Solid(0.55f, 0.45f, 0.22f, 1f);        // moldura dourada escura
            _texBtn = Solid(0.13f, 0.18f, 0.26f, 1f);         // botao azul-escuro
            _texBtnHover = Solid(0.18f, 0.25f, 0.36f, 1f);    // botao com mouse em cima
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
        Panel(new Rect(x, y + 34, W, 2), _texAccent);          // filete dourado sob o titulo
        Border(new Rect(x - 2, y - 2, W + 4, H + 4), _texEdge); // moldura externa
        GUI.Label(new Rect(x + 16, y + 7, 620, 24),
            $"<size=15><color={Gold}><b>♪ BACKSTAGE</b></color></size>  <color={Dim}>busca e download · Chorus Encore</color>");
        GUI.Label(new Rect(x + W - 130, y + 9, 118, 20), $"<color={Dim}>by IaG0D · v0.5</color>");

        // busca
        Panel(new Rect(x + 16, y + 46, W - 290, 28), _texInput);
        GUI.Label(new Rect(x + 24, y + 50, W - 306, 22), $"<size=13>{_query}<color={Gold}>▌</color></size>");
        if (NiceButton(new Rect(x + W - 264, y + 46, 82, 28), _busy ? "..." : "Buscar") && !_busy) StartSearch();
        if (NiceButton(new Rect(x + W - 176, y + 46, 150, 28), FieldNames[_field]))
        { _field = (_field + 1) % FieldValues.Length; if (_results != null) StartSearch(); }

        // filtros em linha propria; trocar refaz a busca na hora
        if (NiceButton(new Rect(x + 16, y + 80, 130, 26), InstNames[_inst]))
        { _inst = (_inst + 1) % InstValues.Length; if (_results != null) StartSearch(); }
        if (NiceButton(new Rect(x + 152, y + 80, 118, 26), DiffNames[_diff]))
        { _diff = (_diff + 1) % DiffValues.Length; if (_results != null) StartSearch(); }
        if (NiceButton(new Rect(x + W - 44, y + 80, 28, 26), "✕")) SetVisible(false);

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

            // botoes de scroll na borda direita (a roda do mouse tambem funciona)
            if (NiceButton(new Rect(x + W - 36, rowY, 24, 100), "▲")) Scroll(-3);
            if (NiceButton(new Rect(x + W - 36, rowY + 112, 24, 100), "▼")) Scroll(3);

            int shown = 0;
            for (int idx = _scroll; idx < _results.Data.Count; idx++)
            {
                var chart = _results.Data[idx];
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

                if (NiceButton(new Rect(x + W - 154, rowY - 1, 28, 23), "♪"))
                    OpenPreview(chart); // previa no navegador (player oficial do Chorus)
                if (NiceButton(new Rect(x + W - 122, rowY - 1, 76, 23), ownedChart ? "de novo" : "Baixar"))
                {
                    _queue.Enqueue(chart);
                    _status = $"na fila: {chart.Name} ({_queue.Count} aguardando)";
                }
                rowY += 28;
            }

            var extra = FieldValues[_field] == null
                ? " — role com a RODA do mouse"
                : " — busca por campo mostra as primeiras 25";
            GUI.Label(new Rect(x + 16, rowY + 2, W - 32, 20),
                $"<color={Dim}><size=11>mostrando {_scroll + 1}–{Math.Min(_scroll + 8, _results.Data.Count)} de {_results.Found}{extra}{(_loadingMore ? " · carregando mais..." : "")}</size></color>");
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
            Panel(new Rect(x + 16, footY - 22, W - 32, 16), _texBarBg);
            Panel(new Rect(x + 16, footY - 22, (W - 32) * pct, 16), _texBarFill);
            GUI.Label(new Rect(x + 22, footY - 24, W - 44, 20),
                $"<size=11><color=#10151c><b>{_downloading.Name}  {_dlDone / 1048576f:F1}/{(_dlTotal > 0 ? _dlTotal / 1048576f : 0):F1} MB · fila: {_queue.Count}</b></color></size>");
        }
        // status em linha propria, LONGE do botao de scan (colado parecia "escanear 132")
        GUI.Label(new Rect(x + 16, footY - 2, W - 32, 20), $"<color={Dim}>{_status}</color>");

        Panel(new Rect(x + 8, footY + 18, W - 16, 1), _texHeader); // separador do rodape
        var scanLabel = _completed > 0 ? $"Escanear ({_completed} baixada{(_completed > 1 ? "s" : "")})" : "Escanear biblioteca";
        if (NiceButton(new Rect(x + 16, footY + 24, 170, 28), scanLabel)) TriggerScan();
        GUI.Label(new Rect(x + 196, footY + 28, W - 212, 20),
            $"<size=11><color={Dim}>baixe tudo primeiro e escaneie UMA vez — o scan e o da biblioteca inteira (rapido, usa cache)</color></size>");
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

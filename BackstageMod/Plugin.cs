using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Backstage;

[BepInPlugin(Id, "Backstage", "0.9.35")]
public class BackstagePlugin : BasePlugin
{
    public const string Id = "com.iag0d.backstage";

    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        L.LogInfo("Backstage 0.9.35 — by IaG0D (F5 abre/fecha)");

        // O Control Remapper abre com Espaco por fora dos mapas do Rewired (Input System
        // direto), entao desligar mapas nao basta: com o painel aberto, o Open() dele
        // simplesmente nao roda.
        try
        {
            var harmony = new HarmonyLib.Harmony(Id);
            harmony.Patch(
                typeof(ObjectPublicInLi1SoExLi1SoStHaUnique).GetMethod(
                    nameof(ObjectPublicInLi1SoExLi1SoStHaUnique.Method_Private_Boolean_SongEntry_PDM_4)),
                prefix: new HarmonyLib.HarmonyMethod(
                    typeof(BackstageUI).GetMethod(nameof(BackstageUI.CaptureSongCache))));
            foreach (var method in typeof(SongScan).GetMethods())
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 1 &&
                    parameters[0].ParameterType == typeof(ObjectPublicInLi1SoExLi1SoStHaUnique))
                    harmony.Patch(method, prefix: new HarmonyLib.HarmonyMethod(
                        typeof(BackstageUI).GetMethod(nameof(BackstageUI.CaptureSongCacheArgument))));
            }
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
    static bool _backstageSongsLoaded;
    static ObjectPublicInLi1SoExLi1SoStHaUnique _songCache;
    static Il2CppSystem.Func<SongEntry, bool> _showAllSongs;

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
    static float _px = -1, _py = -1;  // posicao do painel (arrastavel); -1 = posicao padrao
    static bool _drag;
    static Vector2 _dragOff;

    /// <summary>Prefix Harmony: com o painel aberto, bloqueia o metodo original (ex.: abrir
    /// o Control Remapper com Espaco).</summary>
    public static bool BlockWhenPanelOpen() => !_visible;
    public static void CaptureSongCache(ObjectPublicInLi1SoExLi1SoStHaUnique __instance)
    {
        if (_songCache == null) BackstagePlugin.L.LogInfo("gerenciador nativo de músicas capturado");
        _songCache = __instance;
    }
    public static void CaptureSongCacheArgument(ObjectPublicInLi1SoExLi1SoStHaUnique param_1) =>
        CaptureSongCache(param_1);

    static string SongsDir => Path.Combine(BepInEx.Paths.GameRootPath, "Songs", "Backstage");
    static readonly string CmdPath = Path.Combine(BepInEx.Paths.GameRootPath, "backstage_cmd.txt");
    static readonly string AckPath = Path.Combine(BepInEx.Paths.GameRootPath, "backstage_ack.txt");
    static readonly string VisualDefaultsPath =
        Path.Combine(BepInEx.Paths.ConfigPath, "com.iag0d.backstage.visuals.txt");
    static Texture2D _liveHighwayTexture, _liveBackgroundTexture;
    static GameObject _liveVideoObject;
    static RawImage _liveVideoImage;
    static VideoPlayer _liveVideoPlayer;
    static RenderTexture _liveVideoTexture;
    static readonly List<Texture2D> _liveSkinTextures = new();
    static readonly List<Sprite> _liveSkinSprites = new();
    sealed class HighwaySnapshot
    {
        public Sprite Sprite;
        public bool Enabled, VideoWasPlaying;
        public SpriteDrawMode DrawMode;
        public Vector2 Size, TextureScale, TextureOffset;
        public Vector3 LocalPosition, LocalScale;
    }
    static readonly Dictionary<int, HighwaySnapshot> _originalHighways = new();
    static readonly Dictionary<int, Sprite[]> _originalSkinSprites = new();
    static readonly Dictionary<int, Sprite[]> _originalFretSprites = new();
    static string _defaultHighway = "", _defaultBackground = "", _defaultSkin = "";
    static DateTime _visualDefaultsStamp = DateTime.MinValue;
    static int _defaultHighwayTarget, _defaultBackgroundTarget, _defaultSkinTarget;
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
        RegisterBackstageSongsOnce();
        PumpQueue();
        if (++_pollFrame >= 30)
        {
            _pollFrame = 0;
            PollCmd();
            ApplyPersistentVisuals();
        }
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

        // Exporta a biblioteca pro app desktop fazer o "voce ja tem" fora do jogo.
        try
        {
            var sb = new System.Text.StringBuilder(_ownedCharts.Count * 48);
            foreach (var key in _ownedCharts) sb.Append(key).Append('\n');
            File.WriteAllText(Path.Combine(BepInEx.Paths.GameRootPath, "backstage_library.txt"), sb.ToString());
        }
        catch (Exception e) { BackstagePlugin.L.LogWarning($"export da biblioteca falhou: {e.Message}"); }
    }

    static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

    /// <summary>IMGUI nao corta texto que estoura a coluna; truncamos na mao.</summary>
    static string Fit(string s, int max) =>
        string.IsNullOrEmpty(s) ? "-" : s.Length <= max ? s : s[..(max - 1)] + "…";

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

    static bool SamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void RebuildSongLibrary()
    {
        _showAllSongs ??=
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Func<SongEntry, bool>>(
                new System.Func<SongEntry, bool>(_ => true));
        Anchors.RebuildSongLibrary(_showAllSongs);
    }

    static string RegisterSong(string path, bool rebuild = true)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(SongsDir) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(fullPath), ".sng", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("arquivo fora de Songs\\Backstage ou formato diferente de .sng");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("música baixada não encontrada", fullPath);

        var songs = Anchors.MasterSongs;
        if (songs == null || songs.Count == 0)
            throw new InvalidOperationException("biblioteca do jogo ainda não carregou");
        for (int i = 0; i < songs.Count; i++)
        {
            var existing = songs[i];
            if (existing != null &&
                (SamePath(existing.folderPath, fullPath) ||
                 SamePath(existing.Sng?.field_Public_String_0, fullPath)))
                return "ok song already registered";
        }

        var entry = new SongEntry(fullPath, true);
        var package = entry.Sng ??
            throw new InvalidOperationException("Clone Hero não abriu o pacote .sng");
        var files = package.Method_Public_Il2CppStringArray_PDM_0();
        for (int i = 0; i < files.Length; i++)
        {
            var name = files[i];
            var ext = Path.GetExtension(name);
            if (entry.chartName == null &&
                (ext.Equals(".chart", StringComparison.OrdinalIgnoreCase) ||
                 ext.Equals(".mid", StringComparison.OrdinalIgnoreCase) ||
                 ext.Equals(".midi", StringComparison.OrdinalIgnoreCase)))
                entry.chartName = name;
            if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".avi", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                entry.videoBackground = true;
        }
        if (entry.chartName == null)
            throw new InvalidOperationException("pacote .sng não contém notes.chart ou notes.mid");
        entry.dateAdded = Il2CppSystem.DateTime.Now;

        var cache = _songCache;
        if (cache == null)
        {
            var scan = FindScan() ??
                throw new InvalidOperationException("scanner do jogo indisponível nesta tela");
            cache = new ObjectPublicInLi1SoExLi1SoStHaUnique(scan);
            BackstagePlugin.L.LogWarning("registro individual usando gerenciador isolado");
        }
        if (!cache.Method_Private_Boolean_SongEntry_PDM_4(entry))
            throw new InvalidOperationException("Clone Hero rejeitou os metadados da música");
        if (!cache.Method_Private_Boolean_SongEntry_PDM_2(entry))
            throw new InvalidOperationException("Clone Hero rejeitou o chart da música");
        if (!entry.metadataLoaded)
            throw new InvalidOperationException("Clone Hero não carregou os metadados da música");

        songs.Add(entry);
        try
        {
            if (rebuild) RebuildSongLibrary();
            _ownedFrom = -1;
            return $"ok song added {entry.Artist_StrippedTags} - {entry.Name_StrippedTags}";
        }
        catch
        {
            songs.Remove(entry);
            throw;
        }
    }

    static void RegisterBackstageSongsOnce()
    {
        if (_backstageSongsLoaded || Anchors.MasterSongs == null || Anchors.MasterSongs.Count == 0)
            return;
        _backstageSongsLoaded = true;
        if (!Directory.Exists(SongsDir)) return;

        int added = 0;
        foreach (var path in Directory.GetFiles(SongsDir, "*.sng", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (RegisterSong(path, rebuild: false).StartsWith("ok song added",
                        StringComparison.Ordinal))
                    added++;
            }
            catch (Exception e)
            {
                BackstagePlugin.L.LogWarning(
                    $"registro individual ignorou {Path.GetFileName(path)}: {e}");
            }
        }
        if (added == 0) return;
        RebuildSongLibrary();
        _ownedFrom = -1;
        BackstagePlugin.L.LogInfo($"{added} música(s) registrada(s) sem scan completo.");
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

        const int W = 990, H = 480;
        float x = _px >= 0 ? _px : (Screen.width - W) / 2f;
        float y = _py >= 0 ? _py : Screen.height - H - 76;

        // arrastavel pela barra de titulo (mouse puro, nenhuma API stripped envolvida)
        try
        {
            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (Input.GetMouseButtonDown(0) && new Rect(x, y, W - 44, 34).Contains(mouse))
            { _drag = true; _dragOff = new Vector2(mouse.x - x, mouse.y - y); }
            if (_drag)
            {
                if (Input.GetMouseButton(0))
                {
                    _px = Mathf.Clamp(mouse.x - _dragOff.x, 80 - W, Screen.width - 80);
                    _py = Mathf.Clamp(mouse.y - _dragOff.y, 0, Screen.height - 60);
                    x = _px; y = _py;
                }
                else _drag = false;
            }
        }
        catch { /* sem input legado, sem arrastar */ }

        Panel(new Rect(x, y, W, H), _texPanel);
        Panel(new Rect(x, y, W, 34), _texHeader);
        Panel(new Rect(x, y + 34, W, 2), _texAccent);          // filete dourado sob o titulo
        Border(new Rect(x - 2, y - 2, W + 4, H + 4), _texEdge); // moldura externa
        GUI.Label(new Rect(x + 16, y + 7, 620, 24),
            $"<size=15><color={Gold}><b>♪ BACKSTAGE</b></color></size>  <color={Dim}>busca e download · Chorus Encore</color>");
        GUI.Label(new Rect(x + W - 130, y + 9, 118, 20), $"<color={Dim}>by IaG0D · v0.6</color>");

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
            // colunas: tem | musica | artista | genero | charter | dif | tempo | ♪ | Baixar
            Panel(new Rect(x + 8, rowY - 2, W - 16, 22), _texHeader);
            GUI.Label(new Rect(x + 16, rowY, 46, 20), $"<color={Dim}><i>tem?</i></color>");
            GUI.Label(new Rect(x + 66, rowY, 250, 20), $"<color={Dim}><i>música</i></color>");
            GUI.Label(new Rect(x + 322, rowY, 158, 20), $"<color={Dim}><i>artista</i></color>");
            GUI.Label(new Rect(x + 486, rowY, 118, 20), $"<color={Dim}><i>gênero</i></color>");
            GUI.Label(new Rect(x + 610, rowY, 112, 20), $"<color={Dim}><i>charter</i></color>");
            GUI.Label(new Rect(x + 728, rowY, 28, 20), $"<color={Dim}><i>dif</i></color>");
            GUI.Label(new Rect(x + 760, rowY, 46, 20), $"<color={Dim}><i>tempo</i></color>");
            rowY += 24;

            // scroll discreto: dois botoes pequenos no canto (a roda do mouse e o principal)
            if (NiceButton(new Rect(x + W - 40, rowY - 26, 24, 20), "▲")) Scroll(-3);
            if (NiceButton(new Rect(x + W - 40, rowY + 8 * 28, 24, 20), "▼")) Scroll(3);

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

                GUI.Label(new Rect(x + 16, rowY, 46, 22),
                    ownedChart ? $"<color={Green}>✔ este</color>" : ownedSong ? $"<color={Blue}>≈ tem</color>" : "");
                GUI.Label(new Rect(x + 66, rowY, 250, 22), $"<size=13>{Fit(chart.Name, 31)}</size>");
                GUI.Label(new Rect(x + 322, rowY, 158, 22), $"<color={Dim}>{Fit(chart.Artist, 20)}</color>");
                GUI.Label(new Rect(x + 486, rowY, 118, 22), $"<color={Dim}>{Fit(chart.Genre, 15)}</color>");
                GUI.Label(new Rect(x + 610, rowY, 112, 22), $"<color={Dim}>{Fit(chart.Charter, 14)}</color>");
                GUI.Label(new Rect(x + 728, rowY, 28, 22),
                    diff is > 0 ? $"<color={Gold}>{diff}</color>" : $"<color={Dim}>-</color>");
                GUI.Label(new Rect(x + 760, rowY, 46, 22), $"<color={Dim}>{len}</color>");

                if (NiceButton(new Rect(x + 812, rowY - 1, 26, 23), "♪"))
                    OpenPreview(chart); // previa no navegador (player oficial do Chorus)
                if (NiceButton(new Rect(x + 844, rowY - 1, 72, 23), ownedChart ? "de novo" : "Baixar"))
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
                $"<color={Dim}><size=11>mostrando {_scroll + 1}–{Math.Min(_scroll + 8, _results.Data.Count)} de {_results.Found}{extra}{(_loadingMore ? " · carregando mais..." : "")}" +
                $"      <color={Green}>✔ este</color> = já tem este chart · <color={Blue}>≈ tem</color> = tem a música por outro charter · ♪ = prévia no navegador</size></color>");
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
        var scanLabel = _completed > 0 ? $"Scan completo ({_completed})" : "Scan completo";
        if (NiceButton(new Rect(x + 16, footY + 24, 170, 28), scanLabel)) TriggerScan();
        GUI.Label(new Rect(x + 196, footY + 28, W - 212, 20),
            $"<size=11><color={Dim}>baixe tudo primeiro e escaneie UMA vez — o scan e o da biblioteca inteira (rapido, usa cache)</color></size>");
    }

    // ---- canal de comando p/ teste autonomo (sai na 1.0) ----

    static string RequireCustomFile(string path, string folder)
    {
        var root = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "Custom", folder))
                   + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"arquivo precisa estar em Custom\\{folder}");
        if (!File.Exists(fullPath)) throw new FileNotFoundException("arquivo não encontrado", fullPath);
        return fullPath;
    }

    static string RequireCustomDirectory(string path, string folder)
    {
        var root = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, "Custom", folder))
                   + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"pasta precisa estar em Custom\\{folder}");
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException(fullPath);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar);
    }

    static int ApplyLiveHighway(string path)
    {
        path = RequireCustomFile(path, "Highways");
        var texture = Anchors.LoadTexture(path) ?? throw new InvalidOperationException("imagem inválida");
        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<HighwayScroll>(), includeInactive: true);
        int applied = 0;

        for (int i = 0; i < all.Length; i++)
        {
            var highway = all[i].TryCast<HighwayScroll>();
            if (highway == null || !highway.gameObject.activeInHierarchy) continue;
            try
            {
                var renderer = highway.field_Private_SpriteRenderer_0;
                var material = renderer?.sharedMaterial;
                if (renderer?.sprite != null && !_originalHighways.ContainsKey(highway.GetInstanceID()))
                    _originalHighways[highway.GetInstanceID()] = new HighwaySnapshot
                    {
                        Sprite = renderer.sprite,
                        Enabled = renderer.enabled,
                        DrawMode = renderer.drawMode,
                        Size = renderer.size,
                        LocalPosition = renderer.transform.localPosition,
                        LocalScale = renderer.transform.localScale,
                        TextureScale = material?.mainTextureScale ?? Vector2.one,
                        TextureOffset = material?.mainTextureOffset ?? Vector2.zero,
                        VideoWasPlaying = highway.field_Private_VideoPlayer_0?.isPlaying == true,
                    };
                if (_originalHighways.TryGetValue(highway.GetInstanceID(), out var original))
                    RestoreHighwayState(highway, renderer, original, resumeVideo: false);
                highway.field_Private_VideoPlayer_0?.Stop();
                Anchors.ApplyHighwayTexture(highway, texture);
                if (highway.field_Private_SpriteRenderer_0 != null)
                    highway.field_Private_SpriteRenderer_0.enabled = true;
                applied++;
            }
            catch (Exception e) { BackstagePlugin.L.LogWarning($"highway ativa #{i}: {e.Message}"); }
        }

        if (_liveHighwayTexture != null) UnityEngine.Object.Destroy(_liveHighwayTexture);
        _liveHighwayTexture = texture;
        _status = applied > 0
            ? $"highway aplicada ao vivo em {applied} pista(s)"
            : "highway instalada; entre em uma música para aplicar ao vivo";
        return applied;
    }

    static string KeepNativeHighway(string path)
    {
        path = RequireCustomFile(path, "Highways");
        return SetNativeHighway(Path.GetFileNameWithoutExtension(path));
    }

    static string SetNativeHighway(string name)
    {
        var profiles = GlobalVariables.instance?.profileManager?
            .prop_List_1_Object1PublicObBoObStBoObObObObUnique_0;
        if (profiles == null || profiles.Count == 0)
            throw new InvalidOperationException("perfil do Clone Hero ainda não carregou");
        profiles[0].field_Public_ObjectPublicStBoStObBoStStStStStUnique_1.prop_String_1 = name;
        return name;
    }

    static void RestoreHighwayState(
        HighwayScroll highway, SpriteRenderer renderer, HighwaySnapshot original, bool resumeVideo)
    {
        renderer.sprite = original.Sprite;
        renderer.enabled = original.Enabled;
        renderer.drawMode = original.DrawMode;
        renderer.size = original.Size;
        renderer.transform.localPosition = original.LocalPosition;
        renderer.transform.localScale = original.LocalScale;
        if (renderer.sharedMaterial != null)
        {
            renderer.sharedMaterial.mainTextureScale = original.TextureScale;
            renderer.sharedMaterial.mainTextureOffset = original.TextureOffset;
        }
        if (resumeVideo && original.VideoWasPlaying) highway.field_Private_VideoPlayer_0?.Play();
    }

    static int ResetLiveHighway()
    {
        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<HighwayScroll>(), includeInactive: true);
        int restored = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var highway = all[i].TryCast<HighwayScroll>();
            if (highway == null ||
                !_originalHighways.TryGetValue(highway.GetInstanceID(), out var original) ||
                original.Sprite == null || highway.field_Private_SpriteRenderer_0 == null) continue;
            var renderer = highway.field_Private_SpriteRenderer_0;
            RestoreHighwayState(highway, renderer, original, resumeVideo: true);
            restored++;
        }
        if (_liveHighwayTexture != null) UnityEngine.Object.Destroy(_liveHighwayTexture);
        _liveHighwayTexture = null;
        _originalHighways.Clear();
        _defaultHighway = "";
        _defaultHighwayTarget = 0;
        return restored;
    }

    static bool IsVideo(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".webm" or ".mp4" or ".avi" or ".ogv" or ".mpeg";
    }

    static RawImage CreateLiveBackgroundLayer(SongBackground background)
    {
        DestroyLiveBackgroundLayer();
        _liveVideoObject = new GameObject(
            "BackstageLiveBackground",
            Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>(),
            Il2CppInterop.Runtime.Il2CppType.Of<CanvasRenderer>(),
            Il2CppInterop.Runtime.Il2CppType.Of<RawImage>());
        var rect = _liveVideoObject
            .GetComponent(Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>())
            .TryCast<RectTransform>();
        rect.SetParent(background.backgroundImage.transform.parent, false);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.SetSiblingIndex(background.backgroundImage.transform.GetSiblingIndex() + 1);
        _liveVideoImage = _liveVideoObject
            .GetComponent(Il2CppInterop.Runtime.Il2CppType.Of<RawImage>())
            .TryCast<RawImage>();
        if (_liveVideoImage == null) throw new InvalidOperationException("camada de background inválida");
        _liveVideoImage.color = Color.white;
        _liveVideoImage.raycastTarget = false;
        return _liveVideoImage;
    }

    static void DestroyLiveBackgroundLayer()
    {
        var player = _liveVideoPlayer;
        var videoObject = _liveVideoObject;
        var texture = _liveVideoTexture;
        _liveVideoObject = null;
        _liveVideoImage = null;
        _liveVideoPlayer = null;
        _liveVideoTexture = null;
        if (player) player.Stop();
        if (videoObject) UnityEngine.Object.Destroy(videoObject);
        if (texture)
        {
            texture.Release();
            UnityEngine.Object.Destroy(texture);
        }
    }

    static int ResetLiveBackground()
    {
        int restored = _liveVideoObject || _liveBackgroundTexture ? 1 : 0;
        DestroyLiveBackgroundLayer();
        if (_liveBackgroundTexture != null) UnityEngine.Object.Destroy(_liveBackgroundTexture);
        _liveBackgroundTexture = null;
        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<SongBackground>(), includeInactive: true);
        for (int i = 0; i < all.Length; i++)
        {
            var background = all[i].TryCast<SongBackground>();
            var player = background?.field_Private_VideoPlayer_0;
            if (background != null && background.gameObject.activeInHierarchy &&
                background.hasVideo && player)
                player.Play();
        }
        _defaultBackground = "";
        _defaultBackgroundTarget = 0;
        return restored;
    }

    static int ApplyLiveBackground(string path)
    {
        bool video = IsVideo(path);
        path = RequireCustomFile(path, video ? "Video Backgrounds" : "Image Backgrounds");
        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<SongBackground>(), includeInactive: true);
        int applied = 0;

        Texture2D texture = null;
        if (!video)
            texture = Anchors.LoadTexture(path) ?? throw new InvalidOperationException("imagem inválida");

        for (int i = 0; i < all.Length; i++)
        {
            var background = all[i].TryCast<SongBackground>();
            if (background == null || !background.gameObject.activeInHierarchy) continue;
            try
            {
                if (background.backgroundImage == null) continue;
                var nativePlayer = background.field_Private_VideoPlayer_0;
                if (nativePlayer) nativePlayer.Stop();
                var layer = CreateLiveBackgroundLayer(background);
                if (video)
                {
                    _liveVideoPlayer = _liveVideoObject
                        .AddComponent(Il2CppInterop.Runtime.Il2CppType.Of<VideoPlayer>())
                        .TryCast<VideoPlayer>()
                        ?? throw new InvalidOperationException("player de vídeo inválido");
                    _liveVideoPlayer.playOnAwake = false;
                    _liveVideoPlayer.source = VideoSource.Url;
                    _liveVideoPlayer.url = new Uri(path).AbsoluteUri;
                    _liveVideoPlayer.isLooping = true;
                    _liveVideoPlayer.skipOnDrop = true;
                    _liveVideoPlayer.audioOutputMode = VideoAudioOutputMode.None;
                    _liveVideoPlayer.renderMode = VideoRenderMode.RenderTexture;
                    _liveVideoPlayer.aspectRatio = VideoAspectRatio.FitOutside;
                    _liveVideoTexture = new RenderTexture(Screen.width, Screen.height, 0);
                    _liveVideoTexture.Create();
                    _liveVideoPlayer.targetTexture = _liveVideoTexture;
                    layer.texture = _liveVideoTexture;
                    _liveVideoPlayer.Play();
                }
                else
                {
                    layer.texture = texture;
                }
                applied++;
                break;
            }
            catch (Exception e) { BackstagePlugin.L.LogWarning($"background ativo #{i}: {e}"); }
        }

        if (!video)
        {
            if (_liveBackgroundTexture != null) UnityEngine.Object.Destroy(_liveBackgroundTexture);
            _liveBackgroundTexture = texture;
        }
        _status = applied > 0
            ? $"background aplicado ao vivo em {applied} cena(s)"
            : "background instalado; entre em uma música para aplicar ao vivo";
        return applied;
    }

    static int ApplyLiveColorProfile(string path)
    {
        path = RequireCustomFile(path, "Colors");
        var manager = GlobalVariables.instance?.colorManager
            ?? throw new InvalidOperationException("gerenciador de cores indisponível");
        manager.Method_Public_Void_0();
        var name = Path.GetFileNameWithoutExtension(path);
        var source = manager.Method_Public_ObjectPublicObObBoStObObObObObObUnique_String_0(name)
            ?? throw new InvalidOperationException($"perfil não carregado: {name}");
        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<BasePlayer>(), includeInactive: true);
        int applied = 0;

        for (int i = 0; i < all.Length; i++)
        {
            var player = all[i].TryCast<BasePlayer>();
            if (player == null || !player.gameObject.activeInHierarchy || player.ColorProfile == null) continue;
            player.ColorProfile.Method_Public_Void_ObjectPublicObObBoStObObObObObObUnique_0(source);
            applied++;
        }

        var noteSets = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<GuitarNoteSprites>(), includeInactive: true);
        for (int i = 0; i < noteSets.Length; i++)
        {
            var notes = noteSets[i].TryCast<GuitarNoteSprites>();
            notes?.ColorProfile?.Method_Public_Void_ObjectPublicObObBoStObObObObObObUnique_0(source);
        }

        var fretSets = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<BaseFretAnimator>(), includeInactive: true);
        for (int i = 0; i < fretSets.Length; i++)
        {
            var fret = fretSets[i].TryCast<BaseFretAnimator>();
            fret?.prop_ObjectPublicCoCoCoCoCoCoCoCoCoCoUnique_0
                ?.Method_Public_Void_ObjectPublicObObBoStObObObObObObUnique_0(source);
        }

        return applied;
    }

    static int ApplyLiveSkin(string path)
    {
        path = RequireCustomDirectory(path, "Backstage Skins");
        var profileName = File.ReadAllText(Path.Combine(path, "profile.txt")).Trim();
        if (profileName.Length == 0 || Path.GetFileName(profileName) != profileName)
            throw new InvalidOperationException("perfil do skin inválido");
        ApplyLiveColorProfile(Path.Combine(
            BepInEx.Paths.GameRootPath, "Custom", "Colors", profileName));
        var textures = new List<Texture2D>();
        var sprites = new List<Sprite>();

        Texture2D Load(string name)
        {
            var texture = Anchors.LoadTexture(Path.Combine(path, name + ".png"))
                          ?? throw new InvalidOperationException($"sprite inválido: {name}.png");
            textures.Add(texture);
            return texture;
        }

        var standard = Load("standard");
        var hopo = Load("hopo");
        var tap = Load("tap");
        var star = Load("star");
        var open = Load("open");
        var fretHead = Load("fret-head");
        var fretLift = Load("fret-lift");
        var fretLight = Load("fret-light");
        float noteScale = Path.GetFileName(path).Equals("band-stage", StringComparison.OrdinalIgnoreCase)
            ? 0.68f
            : 1f;
        Sprite Replace(Texture2D texture, Sprite original, float scale = 1f)
        {
            var sprite = Anchors.CreateSpriteLike(texture, original, scale)
                         ?? throw new InvalidOperationException("não foi possível criar o sprite");
            sprites.Add(sprite);
            return sprite;
        }
        Sprite ReplaceExact(Texture2D texture, Sprite original)
        {
            var sprite = Anchors.CreateSpriteExact(texture, original)
                         ?? throw new InvalidOperationException("não foi possível criar o fret");
            sprites.Add(sprite);
            return sprite;
        }

        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<GuitarNoteSprites>(), includeInactive: true);
        int applied = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var notes = all[i].TryCast<GuitarNoteSprites>();
            if (notes == null) continue;
            if (!_originalSkinSprites.ContainsKey(notes.GetInstanceID()))
                _originalSkinSprites[notes.GetInstanceID()] = new[]
                {
                    notes.StandardCapSprite, notes.StandardStarCapSprite,
                    notes.HopoCapSprite, notes.HopoStarCapSprite,
                    notes.TapCapSprite, notes.TapStarCapSprite,
                    notes.AltTapCapSprite, notes.AltTapStarCapSprite,
                    notes.OpenBaseSprite, notes.OpenBodySprite, notes.OpenHopoGlowSprite,
                    notes.StandardBodySprite, notes.TapBodySprite,
                };

            var original = _originalSkinSprites[notes.GetInstanceID()];
            Sprite Themed(Texture2D texture, Sprite template, Sprite current) =>
                template == null ? current : Replace(texture, template, noteScale);
            notes.StandardCapSprite = Themed(standard, original[0], notes.StandardCapSprite);
            notes.StandardStarCapSprite = Themed(star, original[1], notes.StandardStarCapSprite);
            notes.HopoCapSprite = Themed(hopo, original[2], notes.HopoCapSprite);
            notes.HopoStarCapSprite = Themed(star, original[3], notes.HopoStarCapSprite);
            notes.TapCapSprite = Themed(tap, original[4], notes.TapCapSprite);
            notes.TapStarCapSprite = Themed(star, original[5], notes.TapStarCapSprite);
            notes.AltTapCapSprite = Themed(tap, original[6], notes.AltTapCapSprite);
            notes.AltTapStarCapSprite = Themed(star, original[7], notes.AltTapStarCapSprite);
            notes.OpenBaseSprite = Themed(open, original[8], notes.OpenBaseSprite);
            applied++;
        }

        var fretObjects = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<GuitarFretAnimator>(), includeInactive: true);
        int frets = 0;
        for (int i = 0; i < fretObjects.Length; i++)
        {
            var fret = fretObjects[i].TryCast<GuitarFretAnimator>();
            if (fret == null) continue;
            if (!_originalFretSprites.ContainsKey(fret.GetInstanceID()))
                _originalFretSprites[fret.GetInstanceID()] = new[]
                {
                    fret.hook?.sprite, fret.head?.sprite, fret.lift?.sprite,
                    fret.Base?.sprite, fret.cover?.sprite, fret.halfCover?.sprite,
                    fret.headCover?.sprite, fret.headLight?.sprite,
                };
            var original = _originalFretSprites[fret.GetInstanceID()];
            if (fret.hook != null) fret.hook.sprite = null;
            if (fret.head != null && original[1] != null)
                fret.head.sprite = ReplaceExact(fretHead, original[1]);
            if (fret.lift != null && original[2] != null)
                fret.lift.sprite = ReplaceExact(fretLift, original[2]);
            if (fret.Base != null) fret.Base.sprite = null;
            if (fret.cover != null) fret.cover.sprite = null;
            if (fret.halfCover != null) fret.halfCover.sprite = null;
            if (fret.headCover != null) fret.headCover.sprite = null;
            if (fret.headLight != null && original[7] != null)
                fret.headLight.sprite = ReplaceExact(fretLight, original[7]);
            frets++;
        }

        foreach (var sprite in _liveSkinSprites) UnityEngine.Object.Destroy(sprite);
        foreach (var texture in _liveSkinTextures) UnityEngine.Object.Destroy(texture);
        _liveSkinSprites.Clear();
        _liveSkinTextures.Clear();
        _liveSkinSprites.AddRange(sprites);
        _liveSkinTextures.AddRange(textures);
        _status = applied > 0
            ? $"skin aplicado: {applied} notas, sustain nativo e {frets} botões"
            : "skin instalado; entre em uma música para aplicar";
        return applied;
    }

    static int ResetLiveSkin()
    {
        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<GuitarNoteSprites>(), includeInactive: true);
        int restored = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var notes = all[i].TryCast<GuitarNoteSprites>();
            if (notes == null ||
                !_originalSkinSprites.TryGetValue(notes.GetInstanceID(), out var sprites)) continue;
            notes.StandardCapSprite = sprites[0];
            notes.StandardStarCapSprite = sprites[1];
            notes.HopoCapSprite = sprites[2];
            notes.HopoStarCapSprite = sprites[3];
            notes.TapCapSprite = sprites[4];
            notes.TapStarCapSprite = sprites[5];
            notes.AltTapCapSprite = sprites[6];
            notes.AltTapStarCapSprite = sprites[7];
            notes.OpenBaseSprite = sprites[8];
            notes.OpenBodySprite = sprites[9];
            notes.OpenHopoGlowSprite = sprites[10];
            notes.StandardBodySprite = sprites[11];
            notes.TapBodySprite = sprites[12];
            restored++;
        }
        var fretObjects = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<GuitarFretAnimator>(), includeInactive: true);
        for (int i = 0; i < fretObjects.Length; i++)
        {
            var fret = fretObjects[i].TryCast<GuitarFretAnimator>();
            if (fret == null ||
                !_originalFretSprites.TryGetValue(fret.GetInstanceID(), out var sprites)) continue;
            if (fret.hook != null) fret.hook.sprite = sprites[0];
            if (fret.head != null) fret.head.sprite = sprites[1];
            if (fret.lift != null) fret.lift.sprite = sprites[2];
            if (fret.Base != null) fret.Base.sprite = sprites[3];
            if (fret.cover != null) fret.cover.sprite = sprites[4];
            if (fret.halfCover != null) fret.halfCover.sprite = sprites[5];
            if (fret.headCover != null) fret.headCover.sprite = sprites[6];
            if (fret.headLight != null) fret.headLight.sprite = sprites[7];
        }
        foreach (var sprite in _liveSkinSprites) UnityEngine.Object.Destroy(sprite);
        foreach (var texture in _liveSkinTextures) UnityEngine.Object.Destroy(texture);
        _liveSkinSprites.Clear();
        _liveSkinTextures.Clear();
        _originalSkinSprites.Clear();
        _originalFretSprites.Clear();
        var defaultColors = Path.Combine(
            BepInEx.Paths.GameRootPath, "Custom", "Colors", "DefaultColors.ini");
        if (File.Exists(defaultColors)) ApplyLiveColorProfile(defaultColors);
        _defaultSkin = "";
        _defaultSkinTarget = 0;
        return restored;
    }

    static void RefreshVisualDefaults()
    {
        var stamp = File.Exists(VisualDefaultsPath)
            ? File.GetLastWriteTimeUtc(VisualDefaultsPath)
            : DateTime.MinValue;
        if (stamp == _visualDefaultsStamp) return;

        _visualDefaultsStamp = stamp;
        var lines = File.Exists(VisualDefaultsPath)
            ? File.ReadAllLines(VisualDefaultsPath)
            : Array.Empty<string>();
        _defaultHighway = lines.Length > 0 ? lines[0].Trim() : "";
        _defaultBackground = lines.Length > 1 ? lines[1].Trim() : "";
        _defaultSkin = lines.Length > 2 ? lines[2].Trim() : "";
        _defaultHighwayTarget = _defaultBackgroundTarget = _defaultSkinTarget = 0;
    }

    static int ActiveHighwayTarget(out bool usesLiveTexture)
    {
        usesLiveTexture = _liveHighwayTexture != null;
        int target = 0;
        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<HighwayScroll>(), includeInactive: true);
        for (int i = 0; i < all.Length; i++)
        {
            var highway = all[i].TryCast<HighwayScroll>();
            if (highway == null || !highway.gameObject.activeInHierarchy) continue;
            if (target == 0) target = highway.GetInstanceID();
            var texture = highway.field_Private_SpriteRenderer_0?.sprite?.texture;
            if (texture == null || _liveHighwayTexture == null ||
                texture.GetInstanceID() != _liveHighwayTexture.GetInstanceID())
                usesLiveTexture = false;
        }
        return target;
    }

    static int ActiveBackgroundTarget(out bool usesLiveBackground)
    {
        usesLiveBackground = false;
        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<SongBackground>(), includeInactive: true);
        for (int i = 0; i < all.Length; i++)
        {
            var background = all[i].TryCast<SongBackground>();
            if (background != null && background.gameObject.activeInHierarchy)
            {
                var parent = background.backgroundImage?.transform.parent;
                usesLiveBackground = _liveVideoObject && parent &&
                    _liveVideoObject.transform.parent == parent;
                return background.GetInstanceID();
            }
        }
        return 0;
    }

    static int ActiveSkinTarget(out bool usesLiveSkin)
    {
        usesLiveSkin = _liveSkinTextures.Count > 0;
        var all = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<BasePlayer>(), includeInactive: true);
        int target = 0;
        for (int i = 0; i < all.Length; i++)
        {
            var player = all[i].TryCast<BasePlayer>();
            if (player != null && player.gameObject.activeInHierarchy)
            {
                target = player.GetInstanceID();
                break;
            }
        }

        var sprites = UnityEngine.Object.FindObjectsOfType(
            Il2CppInterop.Runtime.Il2CppType.Of<GuitarNoteSprites>(), includeInactive: true);
        usesLiveSkin = usesLiveSkin && sprites.Length > 0;
        if (sprites.Length == 0) return 0;
        for (int i = 0; i < sprites.Length; i++)
        {
            var notes = sprites[i].TryCast<GuitarNoteSprites>();
            if (notes?.StandardCapSprite?.texture == null ||
                _liveSkinTextures.Count == 0 ||
                notes.StandardCapSprite.texture.GetInstanceID() != _liveSkinTextures[0].GetInstanceID())
            {
                usesLiveSkin = false;
                break;
            }
        }
        return target;
    }

    static void ApplyPersistentVisuals()
    {
        RefreshVisualDefaults();

        if (_defaultHighway.Length > 0)
        {
            int highwayTarget = ActiveHighwayTarget(out bool usesLiveHighway);
            if (highwayTarget != 0 &&
                (highwayTarget != _defaultHighwayTarget || !usesLiveHighway))
            {
                try
                {
                    if (ApplyLiveHighway(_defaultHighway) > 0)
                        _defaultHighwayTarget = highwayTarget;
                }
                catch (Exception e)
                {
                    BackstagePlugin.L.LogWarning($"highway padrão ignorada: {e.Message}");
                    _defaultHighwayTarget = highwayTarget;
                }
            }
        }

        if (_defaultBackground.Length > 0)
        {
            int backgroundTarget = ActiveBackgroundTarget(out bool usesLiveBackground);
            if (backgroundTarget != 0 &&
                (backgroundTarget != _defaultBackgroundTarget || !usesLiveBackground))
            {
                try
                {
                    if (ApplyLiveBackground(_defaultBackground) > 0)
                        _defaultBackgroundTarget = backgroundTarget;
                }
                catch (Exception e)
                {
                    BackstagePlugin.L.LogWarning($"background padrão ignorado: {e.Message}");
                    _defaultBackgroundTarget = backgroundTarget;
                }
            }
        }

        if (_defaultSkin.Length > 0)
        {
            int skinTarget = ActiveSkinTarget(out bool usesLiveSkin);
            if (skinTarget != 0 && (skinTarget != _defaultSkinTarget || !usesLiveSkin))
            {
                try
                {
                    if (ApplyLiveSkin(_defaultSkin) > 0)
                        _defaultSkinTarget = skinTarget;
                }
                catch (Exception e)
                {
                    BackstagePlugin.L.LogWarning($"skin padrão ignorado: {e.Message}");
                    _defaultSkinTarget = skinTarget;
                }
            }
        }
    }

    static void Ack(string message)
    {
        try { File.WriteAllText(AckPath, message); }
        catch { }
    }

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
                case "add-song":
                    Ack(RegisterSong(parts[1]));
                    break;
                case "apply-highway":
                    Ack($"ok highway {ApplyLiveHighway(parts[1])}");
                    break;
                case "keep-highway":
                    Ack($"ok highway nativa {KeepNativeHighway(parts[1])}");
                    break;
                case "apply-bg":
                    Ack($"ok background {ApplyLiveBackground(parts[1])}");
                    break;
                case "apply-skin":
                    Ack($"ok skin {ApplyLiveSkin(parts[1])}");
                    break;
                case "reset-highway":
                    var resetHighways = ResetLiveHighway();
                    try { SetNativeHighway("default"); }
                    catch (Exception e) { BackstagePlugin.L.LogWarning($"perfil nativo: {e.Message}"); }
                    Ack($"ok highway reset {resetHighways}");
                    break;
                case "reset-bg":
                    Ack($"ok background reset {ResetLiveBackground()}");
                    break;
                case "reset-skin":
                    Ack($"ok skin reset {ResetLiveSkin()}");
                    break;
                case "state":
                    var scan = FindScan();
                    BackstagePlugin.L.LogInfo(
                        $"state: visible={_visible} results={_results?.Data.Count ?? -1} fila={_queue.Count} " +
                        $"baixando={_downloading?.Name ?? "-"} completos={_completed} master={Anchors.MasterSongs?.Count} " +
                        $"songScan={(scan == null ? "null" : Anchors.IsScanning(scan) ? "SCANNING" : "idle")}");
                    break;
            }
        }
        catch (Exception e)
        {
            Ack($"erro {e.Message}");
            BackstagePlugin.L.LogError($"cmd falhou: {e.Message}");
        }
    }
}

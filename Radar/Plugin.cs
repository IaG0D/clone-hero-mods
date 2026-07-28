using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;
using Il2CppSection = ObjectPublicStLi1SoBoInStInInUnique;

namespace Radar;

[BepInPlugin(Id, "Radar", "1.0.0")]
public class RadarPlugin : BasePlugin
{
    public const string Id = "com.iag0d.radar";

    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        L.LogInfo("Radar 1.0.0 — by IaG0D");

        var search = Anchors.Method(typeof(SongSelectSearch), Anchors.SearchOverSections, L);
        if (search == null) return; // ancora perdida ja foi logada; nao derruba o jogo.

        var harmony = new Harmony(Id);
        harmony.Patch(search, prefix: new HarmonyMethod(
            typeof(SearchHook).GetMethod(nameof(SearchHook.OnSearch))));
        harmony.Patch(AccessTools.Method(typeof(SongSelectSearch), "OnDisable"),
            postfix: new HarmonyMethod(typeof(SearchHook).GetMethod(nameof(SearchHook.OnSearchClosed))));

        AddComponent<RadarTicker>(); // relogio de frames p/ aplicar o filtro FORA do fluxo de fechamento

        L.LogInfo("Radar enganchado na busca.");
    }
}

/// <summary>Update() roda todo frame; e o que permite "daqui a N frames, faca X".</summary>
public class RadarTicker : MonoBehaviour
{
    public RadarTicker(IntPtr ptr) : base(ptr) { }
    void Update() => SearchHook.Tick();
}

/// <summary>
/// Fluxo: digitar = comportamento nativo intocado (pulo). Ao FECHAR a busca com texto,
/// espera o fluxo de fechamento do jogo terminar (2 frames) e ai chama o filtro nativo da
/// biblioteca com o predicado do Radar. Fechar com o campo vazio restaura a lista completa.
/// Nada de inFilterMode: forcar esse estado quebrava a saida da busca.
/// </summary>
internal static class SearchHook
{
    static SongIndex _index;
    static readonly List<SongEntry> _songs = new();   // paralelo ao indice: id -> SongEntry
    static int _indexedFrom = -1;

    static string _query = string.Empty;
    static Field _field = Field.All;
    static SongSelectSearch _search;                  // p/ checar se a tela ainda existe no apply

    static int _framesLeft;                           // contagem regressiva do apply adiado
    static bool _filterOn;
    static Il2CppSystem.Func<SongEntry, bool> _keep;  // referencia viva p/ o delegate nao ser coletado
    static bool _dead;

    public static void OnSearch(SongSelectSearch __instance,
                                Il2CppSystem.Collections.Generic.List<Il2CppSection> __0)
    {
        if (_dead) return;
        try
        {
            EnsureIndex();
            if (__instance == null || !__instance.isActive) return;

            _search = __instance;

            var text = __instance.searchText?.text ?? string.Empty;
            _query = text == Anchors.SearchPlaceholder ? string.Empty : text;

            // O seletor Song/Artist/... aparece no rotulo de filtro da tela de busca.
            var mode = (__instance.filterText?.text ?? string.Empty).ToLowerInvariant();
            _field = mode.Contains("artist") ? Field.Artist
                   : mode.Contains("album") ? Field.Album
                   : mode.Contains("genre") ? Field.Genre
                   : mode.Contains("charter") ? Field.Charter
                   : Field.All; // "song" (padrao) = multi-campo
        }
        catch (Exception e) { Die(e); }
    }

    public static void OnSearchClosed()
    {
        if (_dead) return;
        // So agenda: aplicar aqui dentro corre contra o fechamento e deixa a tela em branco.
        if (!string.IsNullOrWhiteSpace(_query) || _filterOn) _framesLeft = 2;
    }

    public static void Tick()
    {
        if (_dead || _framesLeft == 0) return;
        if (--_framesLeft > 0) return;

        try
        {
            // Tela de musicas sumiu (jogador saiu do menu)? Nao mexe em nada.
            if (_search == null || _search.songSelect == null ||
                !_search.songSelect.gameObject.activeInHierarchy)
            {
                RadarPlugin.L.LogInfo("apply cancelado: tela de musicas fechada.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_query)) { RestoreAll(); return; }
            Apply(_query, _field);
        }
        catch (Exception e) { Die(e); }
    }

    static void Apply(string query, Field field)
    {
        EnsureIndex();
        if (_index == null) return;

        var sw = Stopwatch.StartNew();
        var hits = _index.Search(query, field);

        if (hits.Length == 0)
        {
            RadarPlugin.L.LogInfo($"\"{query}\": nenhum resultado, lista intacta.");
            return; // filtrar para zero deixaria o menu vazio.
        }

        var matched = new HashSet<IntPtr>();
        foreach (var hit in hits) matched.Add(_songs[hit.Id].Pointer);

        _keep = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<SongEntry, bool>>(
            new Func<SongEntry, bool>(song => song != null && matched.Contains(song.Pointer)));

        Anchors.RunLibraryFilter(_keep, $"Radar: {query}");
        RebuildView(); // sem isso a tela so acompanha os dados por sorte de viewport
        _filterOn = true;
        sw.Stop();

        RadarPlugin.L.LogInfo(
            $"\"{query}\" [{field}] -> {hits.Length} de {_songs.Count} em {sw.Elapsed.TotalMilliseconds:F1} ms (aplicado pos-fechamento)");
    }

    static void RestoreAll()
    {
        if (!_filterOn) return;
        _keep = DelegateSupport.ConvertDelegate<Il2CppSystem.Func<SongEntry, bool>>(
            new Func<SongEntry, bool>(_ => true));
        // Rotulo NUNCA vazio: rotulo vazio corrompe a lista de forma irreversivel
        // (o bug do "filtro nunca sai"). Comprovado por screenshot em 2026-07-28.
        Anchors.RunLibraryFilter(_keep, "Radar");
        RebuildView();
        _filterOn = false;
        RadarPlugin.L.LogInfo("lista completa restaurada.");
    }

    /// <summary>isReturningFromSearch e o flag que o Update do SongSelect le para reconstruir
    /// a view a partir dos dados filtrados. Achado por sonda com screenshot; sem ele a lista
    /// visivel fica defasada ou em branco.</summary>
    static void RebuildView()
    {
        var select = UnityEngine.Object.FindObjectOfType<SongSelect>();
        if (select != null) select.isReturningFromSearch = true;
    }

    /// <summary>Indice sai da lista-mestre (nao das secoes visiveis, que encolhem com filtro).
    /// Reconstroi so quando a biblioteca muda de tamanho (rescan).</summary>
    static void EnsureIndex()
    {
        var master = Anchors.MasterSongs;
        if (master == null || _indexedFrom == master.Count) return;

        var sw = Stopwatch.StartNew();
        _songs.Clear();
        var entries = new List<Entry>();

        for (int i = 0; i < master.Count; i++)
        {
            var song = master[i];
            if (song == null) continue;

            entries.Add(new Entry(
                entries.Count,
                song.Name_StrippedTags ?? string.Empty,
                song.Artist_StrippedTags ?? string.Empty,
                song.Album_StrippedTags ?? string.Empty,
                song.Genre_StrippedTags ?? string.Empty,
                song.Charter_StrippedTags ?? string.Empty));
            _songs.Add(song);
        }

        _index = new SongIndex(entries);
        _indexedFrom = master.Count;
        sw.Stop();

        RadarPlugin.L.LogInfo($"Indice montado da lista-mestre: {entries.Count} musicas, {sw.ElapsedMilliseconds} ms.");
    }

    static void Die(Exception e)
    {
        _dead = true; // um erro basta: Radar vira observador inerte, jogo segue intacto.
        RadarPlugin.L.LogError($"Radar desativado nesta sessao: {e}");
    }
}

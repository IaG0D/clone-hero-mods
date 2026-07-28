using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppSection = ObjectPublicStLi1SoBoInStInInUnique;

namespace Radar;

[BepInPlugin(Id, "Radar", "0.1.0")]
public class RadarPlugin : BasePlugin
{
    public const string Id = "com.iag0d.radar";

    internal static ManualLogSource L;

    public override void Load()
    {
        L = Log;
        L.LogInfo("Radar 0.1.0 — by IaG0D");

        var target = Anchors.Method(typeof(SongSelectSearch), Anchors.SearchOverSections, L);
        if (target == null) return; // ancora perdida ja foi logada; nao derruba o jogo.

        new Harmony(Id).Patch(target, prefix: new HarmonyMethod(
            typeof(SearchHook).GetMethod(nameof(SearchHook.Prefix))));

        L.LogInfo("Radar enganchado na busca.");
    }
}

/// <summary>
/// v0: so observa. Constroi o indice a partir da lista real do jogo e mede, para validar
/// velocidade e qualidade contra uma biblioteca de verdade antes de mexer no comportamento.
/// </summary>
internal static class SearchHook
{
    static SongIndex _index;
    static int _indexedFrom = -1;

    public static void Prefix(Il2CppSystem.Collections.Generic.List<Il2CppSection> __0)
    {
        try
        {
            if (__0 == null) return;
            if (_indexedFrom == __0.Count) return; // ja indexado para esta lista.

            var sw = Stopwatch.StartNew();
            var entries = new List<Entry>();

            for (int s = 0; s < __0.Count; s++)
            {
                var songs = __0[s]?.field_Public_List_1_SongEntry_0;
                if (songs == null) continue;

                for (int i = 0; i < songs.Count; i++)
                {
                    var song = songs[i];
                    if (song == null) continue;

                    entries.Add(new Entry(
                        entries.Count,
                        song.Name_StrippedTags ?? string.Empty,
                        song.Artist_StrippedTags ?? string.Empty,
                        song.Album_StrippedTags ?? string.Empty,
                        song.Genre_StrippedTags ?? string.Empty,
                        song.Charter_StrippedTags ?? string.Empty));
                }
            }

            _index = new SongIndex(entries);
            _indexedFrom = __0.Count;
            sw.Stop();

            RadarPlugin.L.LogInfo(
                $"Indice montado: {entries.Count} musicas em {__0.Count} secoes, {sw.ElapsedMilliseconds} ms.");

            Probe("metallica");
            Probe("master");
        }
        catch (Exception e)
        {
            // Falhar aqui nao pode derrubar o menu do jogo.
            RadarPlugin.L.LogError($"Radar falhou ao indexar, seguindo sem ele: {e}");
            _indexedFrom = int.MaxValue;
        }
    }

    static void Probe(string query)
    {
        var sw = Stopwatch.StartNew();
        var hits = _index.Search(query);
        sw.Stop();
        RadarPlugin.L.LogInfo($"  \"{query}\" -> {hits.Length} de {_index.Count} em {sw.Elapsed.TotalMilliseconds:F1} ms");
    }
}

using System;
using System.Reflection;
using BepInEx.Logging;
using Il2CppLibrary = ObjectPublicAbstractSealedLi1SoDi2ObInLi1SoUnique;

namespace Radar;

/// <summary>
/// Tudo que quebra quando o Clone Hero atualiza mora AQUI. Se o Radar parar de funcionar
/// depois de um update do jogo, o conserto e neste arquivo e em mais nenhum.
///
/// Alvo: Clone Hero v1.1.0.6142-final — Unity 2022.3.62f2, IL2CPP, ofuscado com Beebyte.
///
/// Os nomes abaixo nao sao os originais do jogo (esses estao ofuscados). Sao nomes que o
/// Il2CppInterop gera a partir da ASSINATURA, entao so mudam se a assinatura mudar.
/// Sufixo "_PDM_" = "potentially dead method"; o alvo vivo nunca tem esse sufixo.
///
/// Historico do que NAO funciona, pra ninguem re-tentar:
///  - SongEntry.filtered: campo morto. Setar nao muda nada na tela.
///  - Reescrever as List&lt;SongEntry&gt; das secoes: esconde, mas quebra o scroller
///    virtualizado (indices pre-calculados). Inutilizavel sem o rebuild do jogo.
/// </summary>
internal static class Anchors
{
    /// <summary>
    /// SongSelectSearch.&lt;busca&gt;(List&lt;Secao&gt;, Func&lt;SongEntry,bool&gt;) -> (Secao, SongEntry).
    /// A busca nativa (type-to-jump). Serve de gatilho para ler a query e saber que a
    /// tela de busca esta em uso.
    /// </summary>
    public const string SearchOverSections =
        "Method_Private_ValueTuple_2_ObjectPublicStLi1SoBoInStInInUnique_SongEntry_" +
        "List_1_ObjectPublicStLi1SoBoInStInInUnique_Func_2_SongEntry_Boolean_0";

    /// <summary>Query digitada mora em SongSelectSearch.searchText.text; vazio = este placeholder.</summary>
    public const string SearchPlaceholder = "Start typing...";

    /// <summary>
    /// Biblioteca estatica de musicas (ObjectPublicAbstractSealedLi1SoDi2ObInLi1SoUnique):
    /// dona da List&lt;Secao&gt; e do filtro nativo. O metodo abaixo reconstroi a lista visivel
    /// a partir de um predicado, com todos os indices/contadores refeitos pelo proprio jogo.
    /// </summary>
    public const string LibraryFilter =
        "Method_Public_Static_Void_Func_2_SongEntry_Boolean_String_Boolean_0";

    /// <summary>Lista-mestre de todas as musicas na biblioteca estatica (sobrevive a filtros).</summary>
    public static Il2CppSystem.Collections.Generic.List<SongEntry> MasterSongs =>
        Il2CppLibrary.field_Public_Static_List_1_SongEntry_0;

    /// <summary>Tipo da biblioteca estatica, para patchear o filtro nativo.</summary>
    public static Type LibraryType => typeof(Il2CppLibrary);

    /// <summary>Aplica o filtro nativo do jogo: quem o predicado aprovar fica na lista,
    /// e o proprio jogo reconstroi secoes, indices e scroll. Rotulo aparece na UI de filtro.</summary>
    public static void RunLibraryFilter(Il2CppSystem.Func<SongEntry, bool> keep, string label) =>
        Il2CppLibrary.Method_Public_Static_Void_Func_2_SongEntry_Boolean_String_Boolean_0(keep, label, false);

    /// <summary>Troca o modo de sort pelo NOME DE EXIBICAO ("Song", "Artist", "Album", "Genre",
    /// "Charter", "Playlist"). Descoberto via espiao: o jogo chama isto com "Song" ao montar a
    /// lista por titulo. "Song" e o nome de exibicao do sort por Name.</summary>
    public static void SetSort(string displayName) =>
        Il2CppLibrary.Method_Public_Static_Void_String_0(displayName);

    const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic
                           | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Resolve um metodo e loga limpo se ele sumiu, em vez de deixar estourar excecao no jogo.</summary>
    public static MethodInfo Method(Type owner, string name, ManualLogSource log)
    {
        var found = owner.GetMethod(name, Any);
        if (found == null)
            log.LogError($"Ancora perdida: {owner.Name}.{name}. " +
                         "O Clone Hero provavelmente atualizou. O Radar fica inerte, o jogo segue normal.");
        return found;
    }
}

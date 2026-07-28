using System;
using System.Reflection;
using BepInEx.Logging;

namespace Radar;

/// <summary>
/// Tudo que quebra quando o Clone Hero atualiza mora AQUI. Se o Radar parar de funcionar
/// depois de um update do jogo, o conserto e neste arquivo e em mais nenhum.
///
/// Alvo: Clone Hero v1.1.0.6142-final — Unity 2022.3.62f2, IL2CPP.
///
/// Os nomes abaixo nao sao os nomes originais do jogo (esses estao ofuscados). Sao nomes
/// que o Il2CppInterop gera a partir da ASSINATURA do metodo, entao so mudam se a assinatura
/// mudar. Sufixo "_PDM_" significa "potentially dead method" — o alvo vivo nunca tem esse sufixo.
/// </summary>
internal static class Anchors
{
    /// <summary>
    /// SongSelectSearch.&lt;busca&gt;(List&lt;Secao&gt;, Func&lt;SongEntry,bool&gt;) -> (Secao, SongEntry).
    /// A busca nativa varre as secoes com um predicado e devolve so o PRIMEIRO match — e por isso
    /// que ela pula em vez de filtrar. Interessa aqui porque a lista completa passa por este metodo.
    /// </summary>
    public const string SearchOverSections =
        "Method_Private_ValueTuple_2_ObjectPublicStLi1SoBoInStInInUnique_SongEntry_" +
        "List_1_ObjectPublicStLi1SoBoInStInInUnique_Func_2_SongEntry_Boolean_0";

    /// <summary>Campo List&lt;SongEntry&gt; dentro de uma secao da lista.</summary>
    public const string SectionSongs = "field_Public_List_1_SongEntry_0";

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

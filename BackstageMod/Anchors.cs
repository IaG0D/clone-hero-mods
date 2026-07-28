using System;
using Il2CppLibrary = ObjectPublicAbstractSealedLi1SoDi2ObInLi1SoUnique;

namespace Backstage;

/// <summary>
/// Nomes frageis-a-versao do Backstage. Quebrou depois de update do CH? Conserta AQUI.
/// Alvo: Clone Hero v1.1.0.6142-final (Unity 2022.3.62f2, IL2CPP, ofuscado com Beebyte).
/// Copiado de Radar/Anchors.cs de proposito: mods independentes, sem DLL compartilhada.
/// </summary>
internal static class Anchors
{
    /// <summary>Lista-mestre de todas as musicas na biblioteca estatica do jogo.</summary>
    public static Il2CppSystem.Collections.Generic.List<SongEntry> MasterSongs =>
        Il2CppLibrary.field_Public_Static_List_1_SongEntry_0;

    /// <summary>SongScan.isScanning: vira true enquanto o scanner nativo roda.</summary>
    public static bool IsScanning(SongScan scan) => scan != null && scan.isScanning;

    /// <summary>
    /// Gatilho do "Scan Songs" nativo, descoberto por sonda em 2026-07-28:
    /// SongScan.Method_Public_Coroutine_Boolean_0(true) = rescan completo real (isScanning
    /// vira true, biblioteca recarrega do disco). Com false, so valida o cache e NAO
    /// enxerga musica nova. Method_Public_Void_0() nao dispara nada.
    /// </summary>
    public static void TriggerFullScan(SongScan scan) =>
        scan.Method_Public_Coroutine_Boolean_0(true);
}

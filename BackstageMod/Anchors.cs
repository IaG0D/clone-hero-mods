using System;
using UnityEngine;
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

    public static void RebuildSongLibrary(Il2CppSystem.Func<SongEntry, bool> keep)
    {
        Il2CppLibrary.Method_Public_Static_Void_String_0("Song");
        Il2CppLibrary.Method_Public_Static_Void_Func_2_SongEntry_Boolean_String_Boolean_0(
            keep, "Backstage", false);
    }

    /// <summary>Carregador de imagens e criador de sprites nativos do próprio jogo.</summary>
    public static Texture2D LoadTexture(string path) =>
        GlobalVariables.Method_Public_Static_Texture2D_String_Boolean_0(path, false);

    public static Sprite CreateSprite(Texture2D texture) =>
        GlobalVariables.Method_Public_Static_Sprite_Texture2D_0(texture);

    public static Sprite CreateSpriteLike(Texture2D texture, Sprite template, float scale = 1f)
    {
        if (template == null) return CreateSprite(texture);
        var pixels = texture.GetPixels32();
        int minX = texture.width, minY = texture.height, maxX = -1, maxY = -1;
        for (int y = 0; y < texture.height; y++)
            for (int x = 0; x < texture.width; x++)
                if (pixels[y * texture.width + x].a > 8)
                {
                    minX = Mathf.Min(minX, x);
                    minY = Mathf.Min(minY, y);
                    maxX = Mathf.Max(maxX, x);
                    maxY = Mathf.Max(maxY, y);
                }

        var source = maxX >= minX
            ? new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1)
            : new Rect(0, 0, texture.width, texture.height);
        var target = template.rect;
        var pivot = new Vector2(template.pivot.x / target.width, template.pivot.y / target.height);
        var targetWidth = target.width / template.pixelsPerUnit;
        var targetHeight = target.height / template.pixelsPerUnit;
        var pixelsPerUnit = Mathf.Max(source.width / targetWidth, source.height / targetHeight)
                            / Mathf.Max(0.1f, scale);
        return Sprite.Create(texture, source, pivot, pixelsPerUnit);
    }

    public static Sprite CreateSpriteExact(Texture2D texture, Sprite template)
    {
        if (template == null) return CreateSprite(texture);
        var target = template.rect;
        var pivot = new Vector2(template.pivot.x / target.width, template.pivot.y / target.height);
        return Sprite.Create(
            texture, new Rect(0, 0, texture.width, texture.height), pivot,
            template.pixelsPerUnit * texture.width / target.width);
    }

    /// <summary>Método usado pelo jogo para recalcular o material/escala da highway.</summary>
    public static void ApplyHighwayTexture(HighwayScroll highway, Texture2D texture) =>
        highway.Method_Public_Void_Texture2D_0(texture);
}

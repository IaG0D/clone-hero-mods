using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Microsoft.VisualBasic.FileIO;

namespace Backstage.Desktop;

public sealed record HighwayItem(string Name, string Style, string ResourcePath);
public sealed record PresetItem(string Name, string Highway, string Background, string Skin)
{
    static string Label(string path) => string.IsNullOrWhiteSpace(path) ? "—" : Path.GetFileName(path);
    public string HighwayLabel => Label(Highway);
    public string BackgroundLabel => Label(Background);
    public string SkinLabel => Label(Skin);
}
public sealed record SkinItem(
    string Name, string Style, string Slug, string Green, string Red, string Yellow,
    string Blue, string Orange, string Accent, string Flame)
{
    public string StandardPath => $"Assets/Skins/{Slug}/standard.png";
    public string HopoPath => $"Assets/Skins/{Slug}/hopo.png";
    public string TapPath => $"Assets/Skins/{Slug}/tap.png";
    public string StarPath => $"Assets/Skins/{Slug}/star.png";
    public string OpenPath => $"Assets/Skins/{Slug}/open.png";
    public string FretPath => $"Assets/Skins/{Slug}/fret-head.png";
    public string FretHookPath => $"Assets/Skins/{Slug}/fret-hook.png";
    public string FretLiftPath => $"Assets/Skins/{Slug}/fret-lift.png";
    public string FretCoverPath => $"Assets/Skins/{Slug}/fret-cover.png";
    public string FretHalfCoverPath => $"Assets/Skins/{Slug}/fret-half-cover.png";
    public string FretLightPath => $"Assets/Skins/{Slug}/fret-light.png";
    public string HighwayPath => $"Assets/Skins/{Slug}/highway.jpg";
}
public sealed record BackgroundItem(
    string Id, string Title, string PreviewUrl, string MediaUrl,
    int Width, int Height, string Kind, string Source, bool Animated, int Relevance = 0)
{
    ImageSource _previewSource;
    public string Meta => (Width > 0 ? $"{Width} × {Height} · {Source}" : $"Arquivo local · {Source}") +
                          (Animated ? " · prévia estática" : "");
    public long Pixels => (long)Width * Height;
    public bool IsLocal => Source == "Meus";
    public ImageSource PreviewSource => _previewSource ??= LoadPreview();
    public string ActionText => IsLocal ? "Aplicar" : "Baixar e aplicar";
    public Visibility DeleteVisibility => IsLocal ? Visibility.Visible : Visibility.Collapsed;

    public void SetPreview(byte[] data)
    {
        using var stream = new MemoryStream(data);
        _previewSource = LoadPreview(stream);
    }

    ImageSource LoadPreview()
    {
        if (!IsLocal || !Uri.TryCreate(PreviewUrl, UriKind.Absolute, out var uri) || !uri.IsFile)
            return new BitmapImage(new Uri(PreviewUrl, UriKind.RelativeOrAbsolute));
        using var stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return LoadPreview(stream);
    }

    static ImageSource LoadPreview(Stream stream)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
public sealed record BackgroundPage(IReadOnlyList<BackgroundItem> Items, string Cursor, bool HasMore);

public sealed class ChartRow
{
    public Chart Chart { get; init; }
    public string Name => Chart.Name ?? "?";
    public string Artist => Chart.Artist ?? "?";
    public string Genre => Chart.Genre ?? "-";
    public string Charter => Chart.Charter ?? "?";
    public string ArtUrl => Chart.AlbumArtMd5 is { Length: > 0 } md5
        ? $"https://files.enchor.us/{md5}.jpg" : "Assets/App/cover-placeholder-v2.jpg";
    public string VideoText => Chart.HasVideoBackground ? "VIDEO" : "";
    public string DiffText { get; init; }
    public string DifficultyVisual { get; init; }
    public string LengthText { get; init; }
    public string OwnedText { get; init; }
    public Brush OwnedBrush { get; init; }
    public string OwnedBadgeText { get; init; }
    public Brush OwnedBadgeBrush { get; init; }
    public string DownloadText { get; init; }
    public int Relevance { get; init; }
    public int Difficulty { get; init; }
    public long LengthMs => Chart.SongLengthMs ?? 0;
}

public sealed class DownloadRow : INotifyPropertyChanged
{
    string _state = "Aguardando";
    string _filePath;
    long _done, _total = -1;

    public Chart Chart { get; init; }
    public bool IncludeVideo { get; init; }
    public string Name => Chart.Name ?? "?";
    public string Charter => Chart.Charter ?? "?";
    public string ArtUrl => Chart.AlbumArtMd5 is { Length: > 0 } md5
        ? $"https://files.enchor.us/{md5}.jpg" : "Assets/App/cover-placeholder-v2.jpg";
    public string State
    {
        get => _state;
        set
        {
            _state = value;
            Changed();
            Changed(nameof(ProgressText));
            Changed(nameof(ScanText));
            Changed(nameof(CanScan));
        }
    }
    public string FilePath
    {
        get => _filePath;
        set { _filePath = value; Changed(); Changed(nameof(CanScan)); }
    }
    public string ScanText => State switch
    {
        "Registrando" => "Registrando…",
        "Pronto" => "Já tenho",
        _ => "Scan individual",
    };
    public bool CanScan =>
        !string.IsNullOrWhiteSpace(FilePath) && State is not "Registrando" and not "Pronto";
    public double Progress => _total > 0 ? _done * 100d / _total : 0;
    public string ProgressText => _total > 0
        ? $"{_done / 1048576f:F1} MB / {_total / 1048576f:F1} MB"
        : State;

    public void SetProgress(long done, long total)
    {
        _done = done;
        _total = total;
        Changed(nameof(Progress));
        Changed(nameof(ProgressText));
    }

    public event PropertyChangedEventHandler PropertyChanged;
    void Changed([CallerMemberName] string name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class MainWindow : Window
{
    readonly ChorusClient _chorus = new();
    readonly ObservableCollection<ChartRow> _rows = new();
    readonly ObservableCollection<DownloadRow> _downloads = new();
    readonly ObservableCollection<BackgroundItem> _backgrounds = new();
    readonly ObservableCollection<PresetItem> _presets = new();
    readonly HttpClient _mediaClient = new();
    readonly HttpClient _pinterestClient = new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.All,
    });
    readonly HashSet<string> _ownedSongs = new();
    readonly HashSet<string> _ownedCharts = new();
    readonly Queue<DownloadRow> _queue = new();
    SearchResult _last;
    string _lastQuery = "";
    bool _busy, _loadingMore, _downloading, _bgLoading, _bgHasMore;
    int _completed, _bgPage, _bgSearchVersion;
    string _bgQuery = "", _bgCursor, _pinterestAppVersion = "";
    string _currentHighway = "", _currentBackground = "", _currentSkin = "";

    static readonly string GameDir =
        Environment.GetEnvironmentVariable("CLONEHERO_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clone Hero");
    static readonly string VisualDefaultsPath =
        Path.Combine(GameDir, "BepInEx", "config", "com.iag0d.backstage.visuals.txt");
    static readonly string PresetsPath =
        Path.Combine(GameDir, "BepInEx", "config", "com.iag0d.backstage.presets.json");
    static readonly string ColorsDir = Path.Combine(GameDir, "Custom", "Colors");
    static readonly object VisualDefaultsLock = new();

    static string[] NormalizeVisualDefaults(IEnumerable<string> lines) =>
        lines.Concat(new[] { "", "", "" }).Take(3).Select(line => (line ?? "").Trim()).ToArray();

    static string[] ReadVisualDefaults() =>
        File.Exists(VisualDefaultsPath) ? NormalizeVisualDefaults(File.ReadAllLines(VisualDefaultsPath)) : null;

    static void SaveVisualDefault(int index, string path)
    {
        lock (VisualDefaultsLock)
        {
            var values = ReadVisualDefaults() ?? new[] { "", "", "" };
            values[index] = path ?? "";
            Directory.CreateDirectory(Path.GetDirectoryName(VisualDefaultsPath));
            var tmp = VisualDefaultsPath + $".{Environment.ProcessId}.tmp";
            try
            {
                File.WriteAllLines(tmp, values);
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Move(tmp, VisualDefaultsPath, overwrite: true); break; }
                    catch (IOException) when (attempt < 4) { System.Threading.Thread.Sleep(25); }
                }
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
    }

    static string[] WithNativeHighway(IEnumerable<string> source, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidOperationException("nome de highway inválido");
        var lines = source.ToList();
        int start = lines.FindIndex(line =>
            line.Trim().Equals("[profile0]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) throw new InvalidOperationException("perfil 0 do Clone Hero não encontrado");
        int end = lines.FindIndex(start + 1, line => line.TrimStart().StartsWith("["));
        if (end < 0) end = lines.Count;
        int key = lines.FindIndex(start + 1, end - start - 1, line =>
            line.TrimStart().StartsWith("highway_name", StringComparison.OrdinalIgnoreCase));
        if (key < 0) lines.Insert(end, $"highway_name = {name}");
        else lines[key] = $"highway_name = {name}";
        return lines.ToArray();
    }

    static void SaveNativeHighway(string name)
    {
        var path = Path.Combine(GameDir, "profiles.ini");
        if (!File.Exists(path))
            throw new FileNotFoundException("abra o Clone Hero uma vez para criar o perfil", path);
        var tmp = path + $".{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllLines(tmp, WithNativeHighway(File.ReadAllLines(path), name));
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    void LoadPresets()
    {
        if (!File.Exists(PresetsPath)) return;
        foreach (var preset in JsonSerializer.Deserialize<List<PresetItem>>(File.ReadAllText(PresetsPath)) ?? [])
            _presets.Add(preset);
    }

    void SavePresets()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PresetsPath));
        var tmp = PresetsPath + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_presets, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, PresetsPath, overwrite: true);
    }

    static bool IsLocalBackgroundPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return new[] { "Image Backgrounds", "Video Backgrounds" }
            .Select(folder => Path.GetFullPath(Path.Combine(GameDir, "Custom", folder))
                              + Path.DirectorySeparatorChar)
            .Any(root => fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    static readonly string[] FieldValues = { null, "artist", "name", "genre", "charter", "album" };
    static readonly string[] InstValues = { null, "guitar", "bass", "drums", "keys" };
    static readonly string[] DiffValues = { null, "expert", "hard", "medium", "easy" };
    const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";

    static readonly HighwayItem[] Highways =
    {
        new("Afterburner Gold", "Rock · palco dourado", "Assets/Highways/afterburner-gold.jpg"),
        new("Black Ice", "Metal · gelo elétrico", "Assets/Highways/black-ice.jpg"),
        new("Cathedral Noir", "Gótico · ferro e vinho", "Assets/Highways/cathedral-noir.jpg"),
        new("Cosmic Drift", "Space rock · nebulosa", "Assets/Highways/cosmic-drift.jpg"),
        new("Crimson Pulse", "Hard rock · lava rubra", "Assets/Highways/crimson-pulse.jpg"),
        new("Hazard Drive", "Industrial · zona de risco", "Assets/Highways/hazard-drive.jpg"),
        new("Mecha Sunrise", "Anime mecha · energia", "Assets/Highways/mecha-sunrise.jpg"),
        new("Midnight Manga", "Anime noir · mangá", "Assets/Highways/midnight-manga.jpg"),
        new("Soul Blade Pages", "Mangá monocromático · lâminas espirituais", "Assets/Highways/soul-blade-pages.jpg"),
        new("Hollow Moon Panels", "Mangá monocromático · máscaras e lua", "Assets/Highways/hollow-moon-panels.jpg"),
        new("Cursed Ink Rite", "Mangá monocromático · ritual e tinta", "Assets/Highways/cursed-ink-rite.jpg"),
        new("Black Katana Rush", "Mangá monocromático · ação e katanas", "Assets/Highways/black-katana-rush.jpg"),
        new("Phantom City Frames", "Mangá monocromático · cidade e chuva", "Assets/Highways/phantom-city-frames.jpg"),
        new("Bone Mask Chronicle", "Mangá ghoul · máscaras brancas", "Assets/Highways/bone-mask-chronicle.jpg"),
        new("Thunder Panel", "Mangá monocromático · energia e impacto", "Assets/Highways/thunder-panel.jpg"),
        new("Scarlet Margin", "Mangá noir · branco, preto e vermelho", "Assets/Highways/scarlet-margin.jpg"),
        new("Silent Duel Ink", "Mangá monocromático · duelo silencioso", "Assets/Highways/silent-duel-ink.jpg"),
        new("Spirit Gate Manga", "Mangá sobrenatural · portais e espíritos", "Assets/Highways/spirit-gate-manga.jpg"),
        new("Riot Ink", "Punk rock · tinta rebelde", "Assets/Highways/riot-ink.jpg"),
        new("Road Worn", "Classic rock · estrada", "Assets/Highways/road-worn.jpg"),
        new("Sakura Voltage", "Anime · sakura neon", "Assets/Highways/sakura-voltage.jpg"),
        new("Shonen Impact", "Anime · impacto shonen", "Assets/Highways/shonen-impact.jpg"),
        new("Valve Gold", "Classic rock · amplificador", "Assets/Highways/valve-gold.jpg"),
        new("Vapor Grid", "Synthwave · arcade neon", "Assets/Highways/vapor-grid.jpg"),
        new("Void Circuit", "Cyber rock · circuito azul", "Assets/Highways/void-circuit.jpg"),
        new("Inkblade Scroll", "Mangá · tinta e lâminas", "Assets/Highways/inkblade-scroll.jpg"),
        new("Neon Ronin", "Anime cyberpunk · neon", "Assets/Highways/neon-ronin.jpg"),
        new("Celestial Shoujo", "Anime shoujo · estrelas", "Assets/Highways/celestial-shoujo.jpg"),
        new("Kaiju Warning", "Mangá industrial · alerta", "Assets/Highways/kaiju-warning.jpg"),
        new("Spirit Lanterns", "Anime folclórico · lanternas", "Assets/Highways/spirit-lanterns.jpg"),
        new("Pixel Crypt", "Games retrô · dungeon 16-bit", "Assets/Highways/pixel-crypt.jpg"),
        new("Mana Circuit", "RPG fantasia · runas", "Assets/Highways/mana-circuit.jpg"),
        new("Boss Phase", "Games raid · lava e ouro", "Assets/Highways/boss-phase.jpg"),
        new("Turbo Apex", "Games corrida · arcade", "Assets/Highways/turbo-apex.jpg"),
        new("Combo Burst", "Anime fighter · impacto", "Assets/Highways/combo-burst.jpg"),
        new("Event Horizon", "Galáxia · buracos negros", "Assets/Highways/event-horizon.jpg"),
        new("Nebula Bloom", "Galáxia · nebulosa", "Assets/Highways/nebula-bloom.jpg"),
        new("Solar Flare", "Cosmos · plasma solar", "Assets/Highways/solar-flare.jpg"),
        new("Abyssal Tide", "Fantasia oceânica · bioluz", "Assets/Highways/abyssal-tide.jpg"),
        new("Emerald Dragon", "Fantasia · escamas", "Assets/Highways/emerald-dragon.jpg"),
        new("Frostbound", "Fantasia gélida · aurora", "Assets/Highways/frostbound.jpg"),
        new("Lost Temple", "Aventura · ruínas", "Assets/Highways/lost-temple.jpg"),
        new("Toxic Reactor", "Sci-fi horror · reator", "Assets/Highways/toxic-reactor.jpg"),
        new("Crimson Cathedral", "Gótico · vitrais rubros", "Assets/Highways/crimson-cathedral.jpg"),
        new("Candy Glitch", "Anime arcade · kawaii", "Assets/Highways/candy-glitch.jpg"),
        new("GH3 · Axel Steel", "Guitar Hero III · personagem", "Assets/Highways/gh3-axel.jpg"),
        new("GH3 · Casey Lynch", "Guitar Hero III · personagem", "Assets/Highways/gh3-casey.jpg"),
        new("GH3 · Izzy Sparks", "Guitar Hero III · personagem", "Assets/Highways/gh3-izzy.jpg"),
        new("GH3 · Johnny Napalm", "Guitar Hero III · personagem", "Assets/Highways/gh3-johnny.jpg"),
        new("GH3 · Judy Nails", "Guitar Hero III · personagem", "Assets/Highways/gh3-judy.jpg"),
        new("GH3 · Lars Ümlaüt", "Guitar Hero III · personagem", "Assets/Highways/gh3-lars.jpg"),
        new("GH3 · Midori", "Guitar Hero III · personagem", "Assets/Highways/gh3-midori.jpg"),
        new("GH3 · Tom Morello", "Guitar Hero III · chefe", "Assets/Highways/gh3-morello.jpg"),
        new("GH3 · Grim Ripper", "Guitar Hero III · personagem", "Assets/Highways/gh3-ripper.jpg"),
        new("GH3 · God of Rock", "Guitar Hero III · personagem", "Assets/Highways/gh3-rock-god.jpg"),
        new("GH3 · Lou", "Guitar Hero III · chefe", "Assets/Highways/gh3-satan.jpg"),
        new("GH3 · Slash", "Guitar Hero III · chefe", "Assets/Highways/gh3-slash.jpg"),
        new("GH3 · Xavier Stone", "Guitar Hero III · personagem", "Assets/Highways/gh3-xavier.jpg"),
        new("Crimson Vampire", "Gótico · vampiro e catedral", "Assets/Highways/crimson-vampire.jpg"),
        new("Hollow Moon", "Anime dark · espírito mascarado", "Assets/Highways/hollow-moon.jpg"),
        new("Ghoul Eclipse", "Anime urbano · ghoul original", "Assets/Highways/ghoul-eclipse.jpg"),
        new("Soulstorm Samurai", "Anime · lâmina espiritual", "Assets/Highways/soulstorm-samurai.jpg"),
        new("Neon Oni", "Cyberpunk · demônio neon", "Assets/Highways/neon-oni.jpg"),
        new("Sakura Ronin", "Anime · ronin e sakura", "Assets/Highways/sakura-ronin.jpg"),
        new("Kaiju Core", "Games · titã e reator", "Assets/Highways/kaiju-core.jpg"),
        new("Mecha Valkyrie", "Anime mecha · valquíria", "Assets/Highways/mecha-valkyrie.jpg"),
        new("Dragon Shrine", "Fantasia · dragão celestial", "Assets/Highways/dragon-shrine.jpg"),
        new("Cursed Cathedral", "Dark fantasy · cavaleiro caído", "Assets/Highways/cursed-cathedral.jpg"),
        new("Witch Coven", "Gótico · bruxas e corvos", "Assets/Highways/witch-coven.jpg"),
        new("Pharaoh Curse", "Aventura · faraó morto-vivo", "Assets/Highways/pharaoh-curse.jpg"),
        new("Cyber Assassin", "Sci-fi · androide e chuva", "Assets/Highways/cyber-assassin.jpg"),
        new("Pirate Revenant", "Fantasia · capitão espectral", "Assets/Highways/pirate-revenant.jpg"),
        new("Werewolf Midnight", "Horror · lobisomem lunar", "Assets/Highways/werewolf-midnight.jpg"),
        new("Cosmic Seraph", "Galáxia · serafim astral", "Assets/Highways/cosmic-seraph.jpg"),
        new("Demon Arcade", "Games retrô · demônio arcade", "Assets/Highways/demon-arcade.jpg"),
        new("Frost Samurai", "Anime · samurai de gelo", "Assets/Highways/frost-samurai.jpg"),
        new("Toxic Mutant", "Punk sci-fi · mutante tóxico", "Assets/Highways/toxic-mutant.jpg"),
        new("Abyssal Oracle", "Horror cósmico · oráculo abissal", "Assets/Highways/abyssal-oracle.jpg"),
    };

    static readonly SkinItem[] Skins =
    {
        new("Jujutsu Kaisen · Void Technique", "vazio roxo · singularidade e energia espacial", "void-technique", "#70E8FF", "#FF55C8", "#E8D8FF", "#557CFF", "#A855F7", "#C066FF", "#53D9FF"),
        new("Kimetsu no Yaiba · Hinokami Flux", "respiração · água turquesa e fogo solar", "hinokami-flux", "#49D8D5", "#E63A32", "#F3E5C5", "#42BCEB", "#FF7A2A", "#F7F0DF", "#FF4A2D"),
        new("Naruto · Crimson Moon", "akatsuki · nuvens carmesim e aço ninja", "crimson-moon-syndicate", "#CDD0D3", "#C7192E", "#B08B61", "#586A82", "#8C1524", "#E43A45", "#A90F25"),
        new("Evangelion · Berserk Unit", "mecha biomecânico · violeta e energia ácida", "berserk-unit", "#B4FF35", "#8D4DDB", "#E7D46A", "#7067E8", "#FF7B20", "#C5FF42", "#FF8A22"),
        new("Berserk · Eclipse Brand", "mangá dark fantasy · ferro, osso e eclipse", "eclipse-brand", "#E8DCC8", "#B51C2C", "#A98C71", "#59606B", "#7E1520", "#F0E1C6", "#C5222D"),
        new("One Piece · Dawn Gear", "pirata anime · nuvens, ouro e liberdade", "dawn-gear", "#65D9FF", "#DB2637", "#F1C75B", "#52AEEB", "#FF7A27", "#FFF2CE", "#FF3C32"),
        new("Dragon Ball · Instinct Breaker", "ki prateado · aura ciano e azul real", "instinct-breaker", "#8EEBFF", "#4F6BFF", "#E8EDF2", "#258CFF", "#FF8B2B", "#ECFAFF", "#2AD8FF"),
        new("Chainsaw Man · Chainsaw Riot", "devil punk · motor, serra e ignição", "chainsaw-riot", "#FF9A2E", "#D6282F", "#E7D7B9", "#59616A", "#FF6A1A", "#FFB042", "#E72C23"),
        new("Fullmetal Alchemist · Equivalent Gate", "alquimia · latão, automail e reação azul", "equivalent-gate", "#C6A159", "#B32831", "#E7D7A9", "#3C8CE8", "#D8792B", "#F1D98B", "#38A5FF"),
        new("JoJo · Stardust Stand", "batalha extravagante · violeta, ouro e esmeralda", "stardust-stand", "#39D68A", "#8C4BD5", "#E5BD52", "#4FCDE0", "#A455D8", "#F0D16C", "#43E3A0"),
        new("Cyberpunk Edgerunners · Edgerunner Pulse", "cyberpunk · amarelo, ciano e glitch vermelho", "edgerunner-pulse", "#FFD31F", "#FF3148", "#E8EAF0", "#19CBE8", "#FF8A1D", "#FFF05A", "#17D8F2"),
        new("Death Note · Requiem Ledger", "gótico · couro, papel, pena e tinta", "requiem-ledger", "#E8DDC8", "#A91E2B", "#B9BEC5", "#66778D", "#D03A35", "#EFE7D5", "#B51B2B"),
        new("Mangá · Inkstorm Panels", "folhas de mangá · pena, quadros e linhas de ação", "inkstorm-panels", "#F0E9D8", "#B51F2E", "#B9B5AD", "#6D7682", "#D34A37", "#FFF7E5", "#D62D32"),
        new("Mangá · Screentone Abyss", "horror gráfico · retícula, hachura e painéis", "screentone-abyss", "#F0F0EC", "#7B49B5", "#B9BAC0", "#777F94", "#4C326B", "#FFFFFF", "#8D55D1"),
        new("Mangá · Ronin Brush", "washi e sumi-e · pincel, tinta e aço samurai", "ronin-brush", "#E7D5B7", "#B72B24", "#C0A272", "#314D78", "#C8442C", "#F1E2C7", "#C42D27"),
        new("Bleach · Vasto Lorde", "Ichigo Full Hollow · máscara, chifres e reiatsu", "bleach-soul-eclipse", "#F2EFE7", "#C9152C", "#8B0E1C", "#D9DDE3", "#686872", "#FF3B4D", "#C1122F"),
        new("Tokyo Ghoul · Centipede", "kakugan · máscara e kagune", "tokyo-ghoul-centipede", "#F0ECE8", "#D61F35", "#B8A9A3", "#59616B", "#8E1027", "#FF3557", "#B10E2E"),
        new("Attack on Titan · Divisão de Exploração", "asas da liberdade · capas e equipamento 3D", "attack-on-titan-last-wall", "#E7E3D7", "#8B3E2F", "#B18A52", "#496B8F", "#344A3B", "#E8EEF2", "#7F9CB5"),
        new("Hunter × Hunter · Nen Hunters", "Nen · correntes, cartas e aventura", "hunter-x-hunter-nen", "#62C45B", "#E84C3D", "#F3CD4E", "#62B5FF", "#D99A32", "#B7FF78", "#52D8FF"),
        new("Solo Leveling · Shadow Monarch", "monarca · sombras e adagas", "solo-leveling-shadow-monarch", "#83E9FF", "#5D7BFF", "#8B55FF", "#C33CFF", "#27214C", "#A565FF", "#4DDCFF"),
        new("Arena Classic", "metal cromado · gemas facetadas", "arena-classic", "#F4F5F7", "#C8CCD2", "#979DA6", "#FFB04A", "#FF7A1A", "#FFF1D6", "#FF681F"),
        new("Band Stage", "joias redondas · palco moderno", "band-stage", "#FFFFFF", "#DFE2E6", "#AEB4BC", "#737B86", "#E64A4A", "#FFFFFF", "#D93131"),
        new("Arcade Neon", "cristal neon · fliperama", "arcade-neon", "#00EFFF", "#2EC4FF", "#7A5CFF", "#C83CFF", "#FF2BD6", "#E73CFF", "#00EFFF"),
        new("Anime Impact", "shuriken cel-shaded · mangá", "anime-impact", "#FFFFFF", "#E6E6E6", "#B8BCC4", "#FF596D", "#D91F3D", "#FFFFFF", "#FF304F"),
        new("Galaxy Forge", "obsidiana · nebulosa e ouro", "galaxy-forge", "#5B32A3", "#7545C7", "#9968E8", "#C094FF", "#E5B94B", "#B986FF", "#F0C85C"),
        new("Crimson Requiem", "vampiro gótico · vitral e sangue", "crimson-requiem", "#F2E9DD", "#C8B9AA", "#9D1835", "#C9163C", "#F04454", "#FFF0E5", "#C1123F"),
        new("Soul Reaper", "anime espiritual · máscara e chama", "soul-reaper", "#FFFFFF", "#DDE8ED", "#77E8F2", "#4DC7E8", "#8E62D9", "#E9FFFF", "#7450C9"),
        new("Mecha Overdrive", "mecha · reator e gunmetal", "mecha-overdrive", "#AEB7C2", "#747F8C", "#425065", "#FFB13B", "#FF6B22", "#D9E4F2", "#FF7A1F"),
        new("Eldritch Void", "horror cósmico · obsidiana e vazio", "eldritch-void", "#5526A8", "#7131D4", "#8E46F0", "#AE63FF", "#D18AFF", "#C08AFF", "#7A2EE6"),
        new("Pixel Boss", "arcade 16-bit · fase final", "pixel-boss", "#A8FF1A", "#D7FF22", "#FFE63B", "#FF9A1F", "#FF4B1F", "#E9FF20", "#FF6721"),
        new("Cyber Oni", "anime cyberpunk · oni e neon", "cyber-oni", "#00EFFF", "#29C9FF", "#B13CFF", "#FF2D95", "#FF3155", "#00F5FF", "#FF2D95"),
        new("Frost Wyrm", "fantasia · dragão glacial", "frost-wyrm", "#E9FDFF", "#C9F4FF", "#9DDEFF", "#6FBFFF", "#8C9DFF", "#F2FFFF", "#66BFFF"),
        new("Pharaoh's Curse", "Egito místico · ouro e lápis-lazúli", "pharaohs-curse", "#39CDB8", "#28AFA8", "#2874C7", "#D9A62F", "#FFD35A", "#67E8E0", "#E6B83F"),
        new("Inferno Rider", "heavy metal · motor e fogo", "inferno-rider", "#8F191D", "#C52226", "#EF3E22", "#FF7417", "#FFD34D", "#FFF0B0", "#FF4B17"),
        new("Western Outlaw", "western sombrio · ferro e turquesa", "western-outlaw", "#39434A", "#68737A", "#A56A3E", "#C99A45", "#42B8AD", "#D9C596", "#D49A43"),
        new("Mono Pulse", "minimalista · preto, branco e aço", "mono-pulse", "#F4F4F4", "#CFCFCF", "#A0A0A0", "#747474", "#FFFFFF", "#FFFFFF", "#D8D8D8"),
        new("Arctic Glass", "clean · cristal glacial e ciano", "arctic-glass", "#F3FEFF", "#C9F4FF", "#8DE2FF", "#50C4F2", "#8FA8FF", "#E9FDFF", "#70D7FF"),
        new("Solar Edge", "clean · grafite, âmbar e ouro", "solar-edge", "#FFE9B0", "#FFD060", "#FFAD33", "#FF7A1A", "#E85A13", "#FFD56A", "#FF8A1F"),
        new("Sakura Air", "clean · marfim, blush e rosé", "sakura-air", "#FFF0F4", "#FFD1DF", "#F7A8C2", "#E982A7", "#C95C87", "#FFF4F7", "#F08CAF"),
        new("Toxic Signal", "clean · carvão e verde ácido", "toxic-signal", "#E8FF8A", "#C5FF3C", "#9BEC18", "#67C913", "#DFFF54", "#DFFF54", "#A8FF1A"),
        new("Royal Vector", "clean · azul-marinho e ouro", "royal-vector", "#FFF5D6", "#E5C977", "#BD9740", "#657AB5", "#23457E", "#F4D986", "#D5AC50"),
        new("Ocean Pearl", "clean · teal, vidro e pérola", "ocean-pearl", "#E8FFFF", "#A6F3EC", "#57D8CE", "#1AABA5", "#0A7480", "#CFFFF9", "#58E2D8"),
        new("Vapor Lite", "clean · violeta, ciano e rosa", "vapor-lite", "#62F0FF", "#70C9FF", "#A971FF", "#ED61F4", "#FF79B9", "#E887FF", "#FF65D4"),
        new("Ember Alloy", "clean · gunmetal e brasa", "ember-alloy", "#FFE8AD", "#FFC04D", "#FF8127", "#F04425", "#B91E27", "#FF9B32", "#FF4926"),
        new("Ghost Protocol", "clean · fumaça, branco e gelo", "ghost-protocol", "#FFFFFF", "#D9F5FF", "#ABDDF4", "#7DAAC9", "#B9C9D4", "#EAFBFF", "#9BDFFF"),
    };

    static string SkinColorFor(string key, SkinItem skin)
    {
        key = key.ToLowerInvariant();
        if (key.StartsWith("sp_") || key.Contains("_sp_") || key.EndsWith("_sp") ||
            key is "general_sp" or "general_sp_active")
            return skin.Accent;
        if (key.Contains("flame") || key.Contains("spark") || key.Contains("particles"))
            return skin.Flame;
        if (key.Contains("striker_base_"))
            return "#FFFFFF";
        if (key.StartsWith("note_") && !key.StartsWith("note_anim_"))
            return "#FFFFFF";
        if (key.Contains("tap") || key.Contains("hopo") || key.Contains("open"))
            return skin.Accent;
        if (key.Contains("green") || key == "combo_one") return skin.Green;
        if (key.Contains("red")) return skin.Red;
        if (key.Contains("yellow") || key == "combo_two") return skin.Yellow;
        if (key.Contains("blue") || key == "combo_three") return skin.Blue;
        if (key.Contains("orange") || key == "combo_four") return skin.Orange;
        return null;
    }

    static string[] BuildSkinProfile(IEnumerable<string> template, SkinItem skin) =>
        template.Select(line =>
        {
            var match = Regex.Match(line, @"^(?<prefix>\s*(?<key>[^=\s]+)\s*=\s*)#[0-9a-fA-F]{6,8}\s*$");
            if (!match.Success) return line;
            var color = SkinColorFor(match.Groups["key"].Value, skin);
            return color == null ? line : match.Groups["prefix"].Value + color;
        }).ToArray();

    static void RefreshInstalledSkinProfiles()
    {
        var templatePath = Path.Combine(ColorsDir, "DefaultColors.ini");
        if (!File.Exists(templatePath)) return;
        var template = File.ReadAllLines(templatePath);
        foreach (var skin in Skins)
        {
            var skinDirectory = Path.Combine(GameDir, "Custom", "Backstage Skins", skin.Slug);
            if (!Directory.Exists(skinDirectory)) continue;
            File.WriteAllLines(
                Path.Combine(ColorsDir, $"Backstage Skin - {skin.Slug}.ini"),
                BuildSkinProfile(template, skin));
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        Debug.Assert(Highways.Length == 78 && Highways.Select(x => x.ResourcePath).Distinct().Count() == 78);
        Debug.Assert(IsLocalBackgroundPath(Path.Combine(GameDir, "Custom", "Image Backgrounds", "check.jpg")) &&
                     !IsLocalBackgroundPath(Path.Combine(GameDir, "Custom", "check.jpg")));
        Debug.Assert(NormalizeVisualDefaults(new[] { " highway ", " background " })
                         .SequenceEqual(new[] { "highway", "background", "" }) &&
                     NormalizeVisualDefaults(Array.Empty<string>()).SequenceEqual(new[] { "", "", "" }));
        Debug.Assert(WithNativeHighway(
                new[] { "[profile0]", "highway_name = default", "[profile1]", "highway_name = other" },
                "midnight-manga")
            .SequenceEqual(new[]
                { "[profile0]", "highway_name = midnight-manga", "[profile1]", "highway_name = other" }));
        Debug.Assert(Skins.Select(skin => skin.Slug).Distinct().Count() == Skins.Length);
        var presetCheck = new PresetItem("Teste", "highway.jpg", "background.mp4", "skin");
        Debug.Assert(JsonSerializer.Deserialize<PresetItem>(JsonSerializer.Serialize(presetCheck)) == presetCheck);
        Debug.Assert(BuildSkinProfile(
                new[] { "note_green = #00FF00", "sustain_green = #00FF00",
                        "note_anim_green = #00FF00", "striker_base_green = #FFFFFF",
                        "general_sp = #00FFFF", "unknown = #123456" }, Skins[0])
            .SequenceEqual(new[] {
                "note_green = #FFFFFF",
                $"sustain_green = {Skins[0].Green}",
                $"note_anim_green = {Skins[0].Green}",
                "striker_base_green = #FFFFFF",
                $"general_sp = {Skins[0].Accent}",
                "unknown = #123456",
            }));
        try
        {
            RefreshInstalledSkinProfiles();
            var defaults = ReadVisualDefaults();
            if (defaults != null)
            {
                (_currentHighway, _currentBackground, _currentSkin) = (defaults[0], defaults[1], defaults[2]);
                PersistHighwayCheck.IsChecked = defaults[0].Length > 0;
                PersistBackgroundCheck.IsChecked = defaults[1].Length > 0;
                PersistSkinCheck.IsChecked = defaults[2].Length > 0;
            }
            LoadPresets();
        }
        catch { }

        Results.ItemsSource = _rows;
        DownloadResults.ItemsSource = _downloads;
        HighwayResults.ItemsSource = Highways;
        SkinResults.ItemsSource = Skins;
        BackgroundResults.ItemsSource = _backgrounds;
        PresetResults.ItemsSource = _presets;
        UpdatePresetCount();
        SortCombo.ItemsSource = new[] { "Recentes / Relevância", "Nome A–Z", "Artista A–Z", "Maior duração", "Mais difícil" };
        SortCombo.SelectedIndex = 0;
        BgTypeCombo.ItemsSource = new[] { "Tipo: Vídeo", "Tipo: GIF", "Tipo: Imagem", "Tipo: Meus" };
        BgQualityCombo.ItemsSource = new[] { "Qual: 1080p+", "Qual: 4K+", "Qual: Qualquer" };
        BgTypeCombo.SelectedIndex = BgQualityCombo.SelectedIndex = 0;
        FieldCombo.ItemsSource = new[] { "Em: Tudo", "Em: Artista", "Em: Música", "Em: Gênero", "Em: Charter", "Em: Álbum" };
        InstCombo.ItemsSource = new[] { "Inst: Qualquer", "Inst: Guitarra", "Inst: Baixo", "Inst: Bateria", "Inst: Teclas" };
        DiffCombo.ItemsSource = new[] { "Dif: Qualquer", "Dif: Expert", "Dif: Hard", "Dif: Medium", "Dif: Easy" };
        FieldCombo.SelectedIndex = InstCombo.SelectedIndex = DiffCombo.SelectedIndex = 0;
        FieldCombo.SelectionChanged += (_, _) => Research();
        InstCombo.SelectionChanged += (_, _) => Research();
        DiffCombo.SelectionChanged += (_, _) => Research();
        LoadLibrary();
        _ = SearchAsync("*", reset: true);
        SearchBox.Focus();
    }

    void MusicNav_Checked(object sender, RoutedEventArgs e) =>
        ShowSection(MusicView, $"{_ownedSongs.Count} músicas locais carregadas · digite e aperte Enter.");

    void HighwayNav_Checked(object sender, RoutedEventArgs e) =>
        ShowSection(HighwayView, "78 highways prontas · aplique durante qualquer música.");

    void BackgroundNav_Checked(object sender, RoutedEventArgs e) =>
        ShowSection(BackgroundView, "Importe uma imagem ou vídeo e aplique durante a música.");

    void SkinNav_Checked(object sender, RoutedEventArgs e) =>
        ShowSection(SkinView, $"{Skins.Length} skins completas · notas, frets, HOPO, tap, open, sustains e efeitos.");

    void PresetNav_Checked(object sender, RoutedEventArgs e) =>
        ShowSection(PresetView, $"{_presets.Count} preset(s) salvo(s) · aplique o conjunto com um clique.");

    void DownloadsNav_Checked(object sender, RoutedEventArgs e)
    {
        ShowSection(MusicView, $"{_downloads.Count} item(ns) na fila de downloads.");
        DownloadTray?.BringIntoView();
    }

    void ShowSection(FrameworkElement view, string status)
    {
        if (MusicView == null || HighwayView == null || BackgroundView == null ||
            SkinView == null || PresetView == null) return;
        MusicView.Visibility = view == MusicView ? Visibility.Visible : Visibility.Collapsed;
        HighwayView.Visibility = view == HighwayView ? Visibility.Visible : Visibility.Collapsed;
        BackgroundView.Visibility = view == BackgroundView ? Visibility.Visible : Visibility.Collapsed;
        SkinView.Visibility = view == SkinView ? Visibility.Visible : Visibility.Collapsed;
        PresetView.Visibility = view == PresetView ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
        if (view == MusicView) SearchBox.Focus();
    }

    void UpdatePresetCount() =>
        PresetCountText.Text = _presets.Count == 0
            ? "Nenhum preset salvo"
            : $"{_presets.Count} preset(s) salvo(s)";

    void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = PresetNameBox.Text.Trim();
            if (name.Length == 0) name = $"Preset {_presets.Count + 1}";
            var preset = new PresetItem(name, _currentHighway, _currentBackground, _currentSkin);
            var existing = _presets
                .Select((item, index) => (item, index))
                .FirstOrDefault(x => x.item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing.item == null) _presets.Add(preset);
            else _presets[existing.index] = preset;
            SavePresets();
            PresetNameBox.Clear();
            UpdatePresetCount();
            StatusText.Text = $"{name} salvo com skin, background e highway atuais.";
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível salvar o preset: {ex.Message}"; }
    }

    async void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PresetItem preset) return;
        try
        {
            bool keep = PresetPersistCheck.IsChecked == true;
            var visuals = new[]
            {
                (preset.Highway, "apply-highway", "reset-highway", false),
                (preset.Background, "apply-bg", "reset-bg", false),
                (preset.Skin, "apply-skin", "reset-skin", true),
            };
            foreach (var (path, apply, reset, directory) in visuals)
            {
                if (path.Length > 0 && !(directory ? Directory.Exists(path) : File.Exists(path)))
                    throw new FileNotFoundException($"asset do preset não encontrado: {path}");
                if (apply == "apply-highway" && path.Length == 0) SaveNativeHighway("default");
                var ack = apply == "apply-highway" && path.Length > 0
                    ? await ApplyHighwayAsync(path, keep)
                    : await SendLiveCommandAsync(path.Length == 0 ? reset : $"{apply} {path}");
                if (ack?.StartsWith("erro", StringComparison.OrdinalIgnoreCase) == true)
                    throw new InvalidOperationException(ack);
            }

            (_currentHighway, _currentBackground, _currentSkin) =
                (preset.Highway, preset.Background, preset.Skin);
            if (keep)
            {
                SaveVisualDefault(0, preset.Highway);
                SaveVisualDefault(1, preset.Background);
                SaveVisualDefault(2, preset.Skin);
            }
            PersistHighwayCheck.IsChecked = keep && preset.Highway.Length > 0;
            PersistBackgroundCheck.IsChecked = keep && preset.Background.Length > 0;
            PersistSkinCheck.IsChecked = keep && preset.Skin.Length > 0;
            StatusText.Text = Process.GetProcessesByName("Clone Hero").Length == 0
                ? keep
                    ? $"{preset.Name} preparado para a próxima abertura do jogo."
                    : $"{preset.Name} salvo · abra o jogo e aplique novamente."
                : $"{preset.Name} aplicado AO VIVO.";
            if (keep) StatusText.Text += " · padrão salvo.";
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível aplicar o preset: {ex.Message}"; }
    }

    void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PresetItem preset) return;
        if (MessageBox.Show(this, $"Excluir o preset “{preset.Name}”?", "Excluir preset",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _presets.Remove(preset);
        SavePresets();
        UpdatePresetCount();
        StatusText.Text = $"{preset.Name} excluído.";
    }

    void LoadLibrary()
    {
        try
        {
            var path = Path.Combine(GameDir, "backstage_library.txt");
            if (!File.Exists(path))
            {
                StatusText.Text = $"Biblioteca local ainda não encontrada · abra o Clone Hero com o mod uma vez.";
                return;
            }

            foreach (var line in File.ReadLines(path))
            {
                var full = line.Trim();
                if (full.Length == 0) continue;
                _ownedCharts.Add(full);
                int cut = full.LastIndexOf('|');
                if (cut > 0) _ownedSongs.Add(full[..cut]);
            }
            StatusText.Text = $"{_ownedSongs.Count} músicas locais carregadas · digite e aperte Enter.";
        }
        catch (Exception e) { StatusText.Text = $"Biblioteca local: {e.Message}"; }
    }

    static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

    void Research() { if (_last != null && !_busy) _ = SearchAsync(_lastQuery, reset: true); }

    void SearchBox_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) _ = SearchAsync(SearchBox.Text, reset: true); }

    void SearchBtn_Click(object sender, RoutedEventArgs e) => _ = SearchAsync(SearchBox.Text, reset: true);

    async Task SearchAsync(string query, bool reset)
    {
        if (_busy) return;
        query = string.IsNullOrWhiteSpace(query) ? "*" : query.Trim();
        bool recent = query == "*";
        _busy = true;
        _lastQuery = query;
        SearchBtn.Content = "…";
        StatusText.Text = recent ? "Carregando adições recentes…" : $"Buscando “{query}”…";

        try
        {
            string field = FieldValues[Math.Max(0, FieldCombo.SelectedIndex)];
            string inst = InstValues[Math.Max(0, InstCombo.SelectedIndex)];
            string diff = DiffValues[Math.Max(0, DiffCombo.SelectedIndex)];

            var result = field == null || recent
                ? await _chorus.SearchAsync(query, inst, diff)
                : await _chorus.SearchFieldAsync(field, query, inst, diff);

            _last = result;
            if (reset) { _rows.Clear(); Scroller.ScrollToTop(); }
            foreach (var chart in result.Data) _rows.Add(Row(chart));
            ResultCountText.Text = recent
                ? "Adições mais recentes"
                : $"{result.Found:N0} resultados para “{query}”";
            StatusText.Text = (recent ? "Charts adicionados recentemente" : $"{result.Found} charts para “{query}”") +
                              (!recent && field != null ? $" em {field}" : "") +
                              (inst != null ? $" · {inst}" : "") +
                              (diff != null ? $" · {diff}" : "");
        }
        catch (Exception ex) { StatusText.Text = $"Busca falhou: {ex.Message}"; }
        finally { _busy = false; SearchBtn.Content = "Buscar"; }
    }

    ChartRow Row(Chart chart)
    {
        var key = Norm(chart.Artist) + "|" + Norm(chart.Name);
        bool ownedChart = _ownedCharts.Contains(key + "|" + Norm(chart.Charter));
        bool ownedSong = ownedChart || _ownedSongs.Contains(key);

        int? diff = InstCombo.SelectedIndex switch
        {
            2 => chart.DiffBass,
            3 => chart.DiffDrums,
            4 => chart.DiffKeys,
            _ => chart.DiffGuitar,
        };

        return new ChartRow
        {
            Chart = chart,
            DiffText = diff is > 0 ? diff.ToString() : "-",
            DifficultyVisual = diff is > 0
                ? new string('●', Math.Min(5, diff.Value)) + new string('○', Math.Max(0, 5 - diff.Value))
                : "○○○○○",
            LengthText = chart.SongLengthMs is > 0
                ? TimeSpan.FromMilliseconds(chart.SongLengthMs.Value).ToString(@"m\:ss") : "-",
            OwnedText = ownedChart ? "✓ já tem este" : ownedSong ? "≈ tem a música" : "",
            OwnedBrush = ownedChart ? (Brush)FindResource("Green") : (Brush)FindResource("Blue"),
            OwnedBadgeText = ownedSong ? "JÁ TENHO" : "",
            OwnedBadgeBrush = ownedSong ? (Brush)FindResource("Gold") : Brushes.Transparent,
            DownloadText = ownedSong ? "Já tenho" : "Baixar",
            Relevance = _rows.Count,
            Difficulty = diff ?? 0,
        };
    }

    void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortCombo == null) return;
        var view = CollectionViewSource.GetDefaultView(_rows);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(SortCombo.SelectedIndex switch
        {
            1 => new SortDescription(nameof(ChartRow.Name), ListSortDirection.Ascending),
            2 => new SortDescription(nameof(ChartRow.Artist), ListSortDirection.Ascending),
            3 => new SortDescription(nameof(ChartRow.LengthMs), ListSortDirection.Descending),
            4 => new SortDescription(nameof(ChartRow.Difficulty), ListSortDirection.Descending),
            _ => new SortDescription(nameof(ChartRow.Relevance), ListSortDirection.Ascending),
        });
    }

    async void Scroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_last == null || _busy || _loadingMore) return;
        if (FieldCombo.SelectedIndex != 0 || _rows.Count >= _last.Found) return;
        if (e.VerticalOffset < e.ExtentHeight - e.ViewportHeight - 200) return;

        _loadingMore = true;
        try
        {
            string inst = InstValues[Math.Max(0, InstCombo.SelectedIndex)];
            string diff = DiffValues[Math.Max(0, DiffCombo.SelectedIndex)];
            var more = await _chorus.SearchAsync(_lastQuery, inst, diff, _rows.Count / 25 + 1);
            foreach (var chart in more.Data) _rows.Add(Row(chart));
        }
        catch { }
        finally { _loadingMore = false; }
    }

    void Preview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChartRow row) return;
        OpenUrl($"https://enchor.us/chart/{row.Chart.Md5}");
    }

    void AlbumArt_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image image && image.Source?.ToString().Contains("cover-placeholder-v2") != true)
            image.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/App/cover-placeholder-v2.jpg"));
    }

    void Download_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ChartRow row) return;
        var download = new DownloadRow
        {
            Chart = row.Chart,
            IncludeVideo = IncludeVideoCheck.IsChecked == true,
        };
        _queue.Enqueue(download);
        _downloads.Add(download);
        UpdateDownloadTray();
        StatusText.Text = $"Na fila: {row.Name} ({_queue.Count} aguardando).";
        _ = PumpQueueAsync();
    }

    async Task PumpQueueAsync()
    {
        if (_downloading) return;
        _downloading = true;
        DlBar.Visibility = Visibility.Visible;

        try
        {
            while (_queue.Count > 0)
            {
                var item = _queue.Dequeue();
                var chart = item.Chart;
                item.State = "Baixando";
                var dest = Path.Combine(GameDir, "Songs", "Backstage");
                var progress = new Progress<(long done, long total)>(p =>
                {
                    item.SetProgress(p.done, p.total);
                    DlBar.Maximum = Math.Max(1, p.total);
                    DlBar.Value = p.done;
                    StatusText.Text = $"Baixando {chart.Name} · {p.done / 1048576f:F1}/{p.total / 1048576f:F1} MB · fila: {_queue.Count}";
                });

                try
                {
                    var path = await _chorus.DownloadSngAsync(chart, dest, progress, includeVideo: item.IncludeVideo);
                    item.FilePath = path;
                    item.State = "Baixado";
                    _completed++;
                    ScanBtn.Content = "Scan completo";
                }
                catch (Exception ex)
                {
                    item.State = "Falhou";
                    StatusText.Text = $"Download falhou: {ex.Message}";
                }
            }
            StatusText.Text = $"{_completed} baixada(s) · use Scan individual na música desejada.";
        }
        finally
        {
            _downloading = false;
            DlBar.Visibility = Visibility.Collapsed;
        }
    }

    void UpdateDownloadTray()
    {
        DownloadCountText.Text = $"Fila de downloads ({_downloads.Count})";
        DownloadEmptyText.Visibility = _downloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ScanDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadRow item || !item.CanScan) return;
        _ = RegisterDownloadAsync(item);
    }

    async Task RegisterDownloadAsync(DownloadRow item)
    {
        item.State = "Registrando";
        try
        {
            var ack = await SendLiveCommandAsync($"add-song {item.FilePath}");
            if (ack == null)
            {
                item.State = "Baixado";
                StatusText.Text = "Música salva · abra o Clone Hero para ela ser registrada automaticamente.";
            }
            else if (ack.StartsWith("ok ", StringComparison.Ordinal))
            {
                item.State = "Pronto";
                var key = Norm(item.Chart.Artist) + "|" + Norm(item.Chart.Name);
                _ownedSongs.Add(key);
                _ownedCharts.Add(key + "|" + Norm(item.Chart.Charter));
                for (int i = 0; i < _rows.Count; i++)
                    if (Norm(_rows[i].Chart.Artist) + "|" + Norm(_rows[i].Chart.Name) == key)
                        _rows[i] = Row(_rows[i].Chart);
                StatusText.Text = "Música adicionada sem reescanear a biblioteca.";
            }
            else
            {
                item.State = "Baixado";
                StatusText.Text = ack.StartsWith("erro ", StringComparison.Ordinal)
                    ? $"Registro individual falhou: {ack[5..]}"
                    : $"O mod respondeu: {ack}";
            }
        }
        catch (Exception ex)
        {
            item.State = "Baixado";
            StatusText.Text = $"Registro individual falhou: {ex.Message}";
        }
    }

    static async Task<string> ApplyHighwayAsync(string path, bool keep)
    {
        if (keep) SaveNativeHighway(Path.GetFileNameWithoutExtension(path));
        var ack = await SendLiveCommandAsync($"apply-highway {path}");
        if (!keep || ack == null || ack.StartsWith("erro", StringComparison.OrdinalIgnoreCase))
            return ack;
        var nativeAck = await SendLiveCommandAsync($"keep-highway {path}");
        if (nativeAck?.StartsWith("erro", StringComparison.OrdinalIgnoreCase) == true)
            throw new InvalidOperationException(nativeAck);
        return ack;
    }

    async void ApplyHighway_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HighwayItem highway) return;
        var directory = Path.Combine(GameDir, "Custom", "Highways");
        var finalPath = Path.Combine(directory, Path.GetFileName(highway.ResourcePath));
        var tmpPath = finalPath + ".tmp";

        try
        {
            Directory.CreateDirectory(directory);
            var resource = Application.GetResourceStream(new Uri(highway.ResourcePath, UriKind.Relative))
                           ?? throw new FileNotFoundException("Highway não encontrada no aplicativo.");
            using (resource.Stream)
            using (var output = File.Create(tmpPath))
                resource.Stream.CopyTo(output);
            File.Move(tmpPath, finalPath, overwrite: true);
            _currentHighway = finalPath;
            bool keep = PersistHighwayCheck.IsChecked == true;
            SaveVisualDefault(0, keep ? finalPath : "");
            var ack = await ApplyHighwayAsync(finalPath, keep);
            StatusText.Text = ack == null
                ? $"{highway.Name} instalada · abra o jogo e clique Aplicar durante a música."
                : ack.StartsWith("ok highway 0")
                    ? $"{highway.Name} instalada · entre em uma música e clique Aplicar novamente."
                    : ack.StartsWith("ok ")
                        ? $"{highway.Name} aplicada AO VIVO em todas as pistas."
                        : $"O mod respondeu: {ack}";
            if (keep) StatusText.Text += " · padrão salvo.";
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível aplicar: {ex.Message}"; }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    async Task ResetVisualAsync(int index, string command, CheckBox persistCheck, string name)
    {
        try
        {
            SaveVisualDefault(index, "");
            if (index == 0)
            {
                _currentHighway = "";
                SaveNativeHighway("default");
            }
            else if (index == 1) _currentBackground = "";
            else _currentSkin = "";
            persistCheck.IsChecked = false;
            var ack = await SendLiveCommandAsync(command);
            StatusText.Text = ack == null
                ? $"{name} padrão removido · abra o jogo para usar o visual original."
                : ack.StartsWith("ok ")
                    ? $"{name} restaurado para o visual original do jogo."
                    : $"Padrão removido · o mod respondeu: {ack}";
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível restaurar: {ex.Message}"; }
    }

    async void ResetHighway_Click(object sender, RoutedEventArgs e) =>
        await ResetVisualAsync(0, "reset-highway", PersistHighwayCheck, "Highway");

    async void ResetBackground_Click(object sender, RoutedEventArgs e) =>
        await ResetVisualAsync(1, "reset-bg", PersistBackgroundCheck, "Background");

    async void ResetSkin_Click(object sender, RoutedEventArgs e) =>
        await ResetVisualAsync(2, "reset-skin", PersistSkinCheck, "Skin");

    void OpenHighwaysFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(GameDir, "Custom", "Highways");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível abrir a pasta: {ex.Message}"; }
    }

    static void InstallResource(string resourcePath, string finalPath)
    {
        var tmpPath = finalPath + ".tmp";
        var resource = Application.GetResourceStream(new Uri(resourcePath, UriKind.Relative))
                       ?? throw new FileNotFoundException($"Asset não encontrado: {resourcePath}");
        using (resource.Stream)
        using (var output = File.Create(tmpPath))
            resource.Stream.CopyTo(output);
        File.Move(tmpPath, finalPath, overwrite: true);
    }

    async void ApplySkin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SkinItem skin) return;
        var templatePath = Path.Combine(ColorsDir, "DefaultColors.ini");
        var skinDirectory = Path.Combine(GameDir, "Custom", "Backstage Skins", skin.Slug);
        var highwayPath = Path.Combine(GameDir, "Custom", "Highways", $"Backstage - {skin.Slug}.jpg");
        var profilePath = Path.Combine(ColorsDir, $"Backstage Skin - {skin.Slug}.ini");
        var profileTmp = profilePath + ".tmp";

        try
        {
            if (!File.Exists(templatePath))
                throw new FileNotFoundException("abra o Clone Hero uma vez para gerar DefaultColors.ini");
            Directory.CreateDirectory(skinDirectory);
            Directory.CreateDirectory(ColorsDir);
            File.WriteAllLines(profileTmp, BuildSkinProfile(File.ReadLines(templatePath), skin));
            File.Move(profileTmp, profilePath, overwrite: true);
            InstallResource(skin.StandardPath, Path.Combine(skinDirectory, "standard.png"));
            InstallResource(skin.HopoPath, Path.Combine(skinDirectory, "hopo.png"));
            InstallResource(skin.TapPath, Path.Combine(skinDirectory, "tap.png"));
            InstallResource(skin.StarPath, Path.Combine(skinDirectory, "star.png"));
            InstallResource(skin.OpenPath, Path.Combine(skinDirectory, "open.png"));
            InstallResource(skin.FretPath, Path.Combine(skinDirectory, "fret-head.png"));
            InstallResource(skin.FretHookPath, Path.Combine(skinDirectory, "fret-hook.png"));
            InstallResource(skin.FretLiftPath, Path.Combine(skinDirectory, "fret-lift.png"));
            InstallResource(skin.FretCoverPath, Path.Combine(skinDirectory, "fret-cover.png"));
            InstallResource(skin.FretHalfCoverPath, Path.Combine(skinDirectory, "fret-half-cover.png"));
            InstallResource(skin.FretLightPath, Path.Combine(skinDirectory, "fret-light.png"));
            File.WriteAllText(Path.Combine(skinDirectory, "profile.txt"), Path.GetFileName(profilePath));

            _currentSkin = skinDirectory;
            bool keep = PersistSkinCheck.IsChecked == true;
            SaveVisualDefault(2, keep ? skinDirectory : "");
            var ack = await SendLiveCommandAsync($"apply-skin {skinDirectory}");
            if (ApplySkinHighwayCheck.IsChecked == true)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(highwayPath)!);
                InstallResource(skin.HighwayPath, highwayPath);
                _currentHighway = highwayPath;
                SaveVisualDefault(0, keep ? highwayPath : "");
                PersistHighwayCheck.IsChecked = keep;
                var highwayAck = await ApplyHighwayAsync(highwayPath, keep);
                if (highwayAck?.StartsWith("erro ", StringComparison.Ordinal) == true)
                    throw new InvalidOperationException(highwayAck[5..]);
            }
            StatusText.Text = ack == null
                ? $"{skin.Name} instalado · será aplicado ao abrir o jogo."
                : ack.StartsWith("ok skin 0")
                    ? $"{skin.Name} instalado · será aplicado ao entrar na próxima música."
                    : ack.StartsWith("ok ")
                        ? $"{skin.Name} aplicado AO VIVO."
                        : $"Skin instalado · o mod respondeu: {ack}";
            if (ApplySkinHighwayCheck.IsChecked == true) StatusText.Text += " · highway combinando aplicada.";
            if (keep) StatusText.Text += " · padrão salvo.";
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível aplicar o skin: {ex.Message}"; }
        finally { if (File.Exists(profileTmp)) File.Delete(profileTmp); }
    }

    void OpenSkinsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(GameDir, "Custom", "Backstage Skins");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível abrir a pasta: {ex.Message}"; }
    }

    void OpenRootFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(GameDir) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Não foi possível abrir a pasta: {ex.Message}"; }
    }

    void BgSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = StartBackgroundSearchAsync();
    }

    async void SearchBackgrounds_Click(object sender, RoutedEventArgs e) =>
        await StartBackgroundSearchAsync();

    async void BgQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        bool local = BgTypeCombo.SelectedIndex == 3;
        BgQualityColumn.Width = local ? new GridLength(0) : new GridLength(173);
        BgQualityCombo.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        BgQualityCombo.IsEnabled = !local;
        BgSearchBtn.Content = local ? "Atualizar" : "Buscar";
        if (local || !string.IsNullOrWhiteSpace(BgSearchBox.Text))
            await StartBackgroundSearchAsync();
    }

    async Task StartBackgroundSearchAsync()
    {
        var query = BgSearchBox.Text.Trim();
        bool local = BgTypeCombo.SelectedIndex == 3;
        if (query.Length == 0 && !local) return;
        int version = ++_bgSearchVersion;
        while (_bgLoading) await Task.Delay(50);
        if (version != _bgSearchVersion) return;

        _bgQuery = query;
        _bgCursor = null;
        _pinterestAppVersion = "";
        _bgPage = 1;
        _bgHasMore = true;
        _backgrounds.Clear();
        BgScroller.ScrollToTop();
        BgEmptyState.Visibility = Visibility.Collapsed;
        BgMoreBtn.Visibility = Visibility.Collapsed;

        if (local)
        {
            await LoadLocalBackgroundsAsync(query);
            return;
        }

        for (int attempt = 0; attempt < 3 && _backgrounds.Count < 8 && _bgHasMore; attempt++)
            await LoadMoreBackgroundsAsync();
    }

    async Task LoadLocalBackgroundsAsync(string query)
    {
        _backgrounds.Clear();
        string[] imageExtensions = { ".jpg", ".jpeg", ".png" };
        string[] videoExtensions = { ".webm", ".mp4", ".avi", ".ogv", ".mpeg" };
        var folders = new[]
        {
            (Path.Combine(GameDir, "Custom", "Image Backgrounds"), imageExtensions, false),
            (Path.Combine(GameDir, "Custom", "Video Backgrounds"), videoExtensions, true),
        };

        var files = folders.SelectMany(folder =>
            Directory.Exists(folder.Item1)
                ? Directory.EnumerateFiles(folder.Item1)
                    .Where(path => !Path.GetFileName(path).Contains(".tmp.", StringComparison.OrdinalIgnoreCase) &&
                                   folder.Item2.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                    .Select(path => (File: new FileInfo(path), Animated: folder.Item3))
                : Enumerable.Empty<(FileInfo File, bool Animated)>())
            .Where(item => query.Length == 0 ||
                           item.File.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.File.LastWriteTime)
            .ToList();

        foreach (var item in files)
        {
            var previewPath = item.Animated ? VideoPreviewPath(item.File.FullName) : item.File.FullName;
            if (item.Animated && !File.Exists(previewPath))
            {
                try { await CreateVideoPreviewAsync(item.File.FullName); }
                catch { previewPath = ""; }
            }
            var size = item.Animated ? (0, 0) : ReadImageSize(item.File.FullName);
            if (item.Animated)
                try { size = await ProbeVideoSizeAsync(item.File.FullName); }
                catch { }
            _backgrounds.Add(new BackgroundItem(
                item.File.FullName,
                Path.GetFileNameWithoutExtension(item.File.Name),
                File.Exists(previewPath) ? new Uri(previewPath).AbsoluteUri : "Assets/App/cover-placeholder-v2.jpg",
                item.File.FullName,
                size.Item1,
                size.Item2,
                item.Animated ? "VÍDEO" : item.File.Extension.TrimStart('.').ToUpperInvariant(),
                "Meus",
                item.Animated));
        }

        _bgHasMore = false;
        BgQualityCombo.IsEnabled = false;
        BgSearchBtn.Content = "Atualizar";
        BgEmptyState.Visibility = _backgrounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BgResultText.Text = _backgrounds.Count == 0
            ? "Nenhum background baixado"
            : $"{_backgrounds.Count} background(s) baixado(s)";
        StatusText.Text = _backgrounds.Count == 0
            ? "Seus backgrounds aparecerão aqui depois de baixar ou importar."
            : "Meus backgrounds · aplique ou exclua sem baixar novamente.";
    }

    static string VideoPreviewPath(string videoPath) => videoPath + ".preview.jpg";

    static async Task CreateVideoPreviewAsync(string videoPath)
    {
        var preview = VideoPreviewPath(videoPath);
        var tmp = preview + ".tmp.jpg";
        try
        {
            await RunFfmpegAsync(new[]
            {
                "-hide_banner", "-loglevel", "error", "-y", "-ss", "1", "-i", videoPath,
                "-frames:v", "1", "-vf", "scale=640:-2", "-q:v", "5", tmp,
            });
            File.Move(tmp, preview, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    static (int, int) ReadImageSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch { return (0, 0); }
    }

    async void BgScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_bgHasMore && e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 220)
            await LoadMoreBackgroundsAsync();
    }

    async void BgMoreBtn_Click(object sender, RoutedEventArgs e) =>
        await LoadMoreBackgroundsAsync();

    async Task LoadMoreBackgroundsAsync()
    {
        if (_bgLoading || !_bgHasMore || _bgQuery.Length == 0) return;
        int version = _bgSearchVersion;
        int typeIndex = BgTypeCombo.SelectedIndex;
        _bgLoading = true;
        BgSearchBtn.IsEnabled = BgTypeCombo.IsEnabled = BgQualityCombo.IsEnabled = false;
        BgSearchBtn.Content = "Buscando…";
        BgLoadBar.Visibility = Visibility.Visible;
        BgMoreBtn.Visibility = Visibility.Collapsed;
        BgResultText.Text = _backgrounds.Count == 0 ? "Buscando em alta resolução…" : "Carregando mais…";

        try
        {
            var page = typeIndex switch
            {
                0 => await SearchMixkitAsync(_bgQuery, _bgPage),
                2 => await SearchWallhavenAsync(_bgQuery, _bgPage),
                _ => await SearchPinterestAsync(_bgQuery, _bgCursor),
            };
            if (version != _bgSearchVersion) return;
            var keys = _backgrounds.Select(x => $"{x.Source}:{x.Id}").ToHashSet();
            foreach (var item in page.Items)
                if (keys.Add($"{item.Source}:{item.Id}"))
                    _backgrounds.Add(item with { Relevance = _backgrounds.Count });

            _bgCursor = page.Cursor;
            _bgPage++;
            _bgHasMore = page.HasMore;
            ApplyBackgroundSort();
            BgEmptyState.Visibility = _backgrounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var kind = typeIndex switch
            {
                0 => "vídeos reais HD",
                1 => "GIFs HD",
                _ => "imagens HD",
            };
            BgResultText.Text = $"{_backgrounds.Count} {kind}" +
                                (_bgHasMore ? " · role ou clique em Carregar mais" : "");
            StatusText.Text = _backgrounds.Count > 0
                ? "Catálogo carregado dentro do Backstage."
                : "Nada nessa resolução nesta página · procurando a próxima…";
        }
        catch (Exception ex)
        {
            if (version != _bgSearchVersion) return;
            _bgHasMore = false;
            BgEmptyState.Visibility = _backgrounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BgResultText.Text = "Busca indisponível";
            StatusText.Text = $"Busca de backgrounds falhou: {ex.Message}";
        }
        finally
        {
            _bgLoading = false;
            BgSearchBtn.IsEnabled = BgTypeCombo.IsEnabled = BgQualityCombo.IsEnabled = true;
            BgSearchBtn.Content = "Buscar";
            BgLoadBar.Visibility = Visibility.Collapsed;
            BgMoreBtn.Visibility = _bgHasMore && _backgrounds.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void ApplyBackgroundSort()
    {
        var view = CollectionViewSource.GetDefaultView(_backgrounds);
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(nameof(BackgroundItem.Pixels), ListSortDirection.Descending));
    }

    (int width, int height) MinimumBackgroundSize() => BgQualityCombo.SelectedIndex switch
    {
        1 => (3840, 2160),
        2 => (0, 0),
        _ => (1920, 1080),
    };

    async Task<BackgroundPage> SearchPinterestAsync(string search, string cursor)
    {
        var minimum = MinimumBackgroundSize();
        var quality = minimum.width > 0 ? $"{minimum.width}x{minimum.height}" : "HD";
        var query = $"{search} {quality} animated gif background";
        var source = $"/search/pins/?q={Uri.EscapeDataString(query)}";

        if (cursor == null)
        {
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, $"https://www.pinterest.com{source}");
            pageRequest.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
            using var pageResponse = await _pinterestClient.SendAsync(pageRequest);
            pageResponse.EnsureSuccessStatusCode();
            var page = await pageResponse.Content.ReadAsStringAsync();
            _pinterestAppVersion = Regex.Match(page, @"app_version"":""([^""]+)""").Groups[1].Value;
            if (_pinterestAppVersion.Length == 0)
                throw new InvalidOperationException("Pinterest não informou a versão da busca.");
        }

        var data = JsonSerializer.Serialize(new
        {
            options = new { query, scope = "pins", rs = "typed", bookmarks = new[] { cursor ?? "" } },
            context = new { },
        });
        var url = "https://www.pinterest.com/resource/BaseSearchResource/get/" +
                  $"?source_url={Uri.EscapeDataString(source)}&data={Uri.EscapeDataString(data)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*, q=0.01");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("X-App-Version", _pinterestAppVersion);
        request.Headers.TryAddWithoutValidation("X-Pinterest-AppState", "active");
        request.Headers.TryAddWithoutValidation("X-Pinterest-PWS-Handler", "www/search/[scope].js");
        request.Headers.Referrer = new Uri($"https://www.pinterest.com{source}");

        using var response = await _pinterestClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var results = root.GetProperty("resource_response").GetProperty("data").GetProperty("results");
        var items = new List<BackgroundItem>();

        foreach (var pin in results.EnumerateArray())
        {
            if (!pin.TryGetProperty("images", out var images)) continue;
            var preview = images.TryGetProperty("736x", out var image736)
                ? image736.GetProperty("url").GetString()
                : images.TryGetProperty("474x", out var image474) ? image474.GetProperty("url").GetString() : null;
            var title = pin.TryGetProperty("seo_alt_text", out var alt) ? alt.GetString() : null;
            if (string.IsNullOrWhiteSpace(title)) title = "Background animado";
            var id = pin.TryGetProperty("id", out var idProperty)
                ? idProperty.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");

            BackgroundItem best = null;
            if (images.TryGetProperty("orig", out var original) &&
                original.TryGetProperty("url", out var gifProperty) &&
                original.TryGetProperty("width", out var gifWidthProperty) &&
                original.TryGetProperty("height", out var gifHeightProperty))
            {
                var media = gifProperty.GetString() ?? "";
                int width = gifWidthProperty.GetInt32(), height = gifHeightProperty.GetInt32();
                double ratio = height == 0 ? 0 : width / (double)height;
                if (media.Contains(".gif", StringComparison.OrdinalIgnoreCase) &&
                    ratio is >= 1.55 and <= 1.95 &&
                    width >= minimum.width && height >= minimum.height)
                    best = new BackgroundItem(id, title, preview ?? media, media,
                        width, height, "GIF", "Pinterest", true);
            }
            if (best != null) items.Add(best);
        }

        string next = null;
        if (root.TryGetProperty("resource", out var resource) &&
            resource.TryGetProperty("options", out var options) &&
            options.TryGetProperty("bookmarks", out var bookmarks) &&
            bookmarks.GetArrayLength() > 0)
            next = bookmarks[0].GetString();
        return new BackgroundPage(items, next, !string.IsNullOrWhiteSpace(next) && next != "-end-");
    }

    async Task<BackgroundPage> SearchMixkitAsync(string search, int page)
    {
        var minimum = MinimumBackgroundSize();
        var terms = Regex.Matches(search.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value switch
            {
                "rock" => "guitar",
                "metal" => "concert",
                "bg" or "background" => "abstract",
                _ => match.Value,
            })
            .Distinct().Take(2).ToArray();
        var candidates = new List<BackgroundItem>();
        bool hasMore = false;

        foreach (var term in terms)
        {
            var url = $"https://mixkit.co/free-stock-video/{Uri.EscapeDataString(term)}/?page={page}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
            using var response = await _mediaClient.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            var cards = Regex.Matches(html,
                @"<div class=""item-grid-video-player(?<classes>(?:\s[^""]*)?)""(?<card>.*?)(?=<div class=""item-grid-video-player(?:\s|"")|\z)",
                RegexOptions.Singleline);
            hasMore |= cards.Count >= 24;

            foreach (Match match in cards)
            {
                var card = match.Groups["card"].Value;
                var id = Regex.Match(card,
                    @"data-item-grid--video-player-item-id-value=""([^""]+)""").Groups[1].Value;
                var preview = Regex.Match(card,
                    @"<img src=""([^""]+)"" class=""item-grid-video-player__thumb""").Groups[1].Value;
                var title = WebUtility.HtmlDecode(Regex.Match(card,
                    @"class=""item-grid-video-player__thumb"" alt=""([^""]*)""").Groups[1].Value);
                var video = Regex.Match(card, @"<video src=""([^""]+-360\.mp4)""").Groups[1].Value;
                bool is4K = match.Groups["classes"].Value.Contains(
                    "item-grid-video-player--4k", StringComparison.Ordinal);
                int width = minimum.width >= 3840 ? 3840 : minimum.width == 0 && is4K ? 3840 : 1920;
                if (id.Length == 0 || preview.Length == 0 || video.Length == 0 ||
                    minimum.width >= 3840 && !is4K) continue;
                var media = video.Replace("-360.mp4", width >= 3840 ? "-2160.mp4" : "-1080.mp4");
                candidates.Add(new BackgroundItem(
                    id, title, preview, media, width, width >= 3840 ? 2160 : 1080,
                    "VÍDEO", "Mixkit", true));
            }
        }

        var items = new List<BackgroundItem>();
        foreach (var batch in candidates.DistinctBy(item => item.Id).Chunk(8))
        {
            var verified = await Task.WhenAll(batch.Select(async item =>
            {
                using var check = new HttpRequestMessage(HttpMethod.Head, item.MediaUrl);
                check.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
                using var response = await _mediaClient.SendAsync(check);
                return response.IsSuccessStatusCode ? item : null;
            }));
            items.AddRange(verified.Where(item => item != null));
        }
        var youtube = await SearchYouTubeAsync(search, page);
        items.AddRange(youtube.Items);
        return new BackgroundPage(items, null, hasMore || youtube.HasMore);
    }

    async Task<BackgroundPage> SearchYouTubeAsync(string search, int page)
    {
        const int pageSize = 8;
        int first = (page - 1) * pageSize + 1;
        int last = page * pageSize;
        var minimum = MinimumBackgroundSize();
        int maxHeight = minimum.height >= 2160 ? 2160 : 1080;
        var query = search.Contains("anime", StringComparison.OrdinalIgnoreCase)
            ? $"{search} fight scene {maxHeight}p"
            : $"{search} scene {maxHeight}p";
        var output = await RunYtDlpAsync(new[]
        {
            "--skip-download", "--playlist-start", first.ToString(), "--playlist-end", last.ToString(),
            "-f", $"bv*[vcodec^=avc1][height<={maxHeight}]/bv*[height<={maxHeight}]",
            "--print", "%(.{id,title,duration,thumbnail,width,height,url})j",
            $"ytsearch{last}:{query}",
        });
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        var blockedTitles = new[]
        {
            "live wallpaper", "animated wallpaper", "moving wallpaper", "motion wallpaper",
            "wallpaper engine", "parallax", "slideshow", "still image", "screensaver",
        };
        var items = new List<BackgroundItem>();
        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            var title = root.GetProperty("title").GetString() ?? "Vídeo";
            if (blockedTitles.Any(term => title.Contains(term, StringComparison.OrdinalIgnoreCase)))
                continue;
            int width = root.GetProperty("width").GetInt32();
            int height = root.GetProperty("height").GetInt32();
            double ratio = height == 0 ? 0 : width / (double)height;
            if (ratio is < 1.55 or > 1.95 ||
                width < minimum.width || height < minimum.height)
                continue;
            var id = root.GetProperty("id").GetString() ?? "";
            var preview = root.GetProperty("thumbnail").GetString() ?? "";
            var media = root.GetProperty("url").GetString() ?? "";
            if (id.Length == 0 || preview.Length == 0 || media.Length == 0) continue;
            items.Add(new BackgroundItem(
                id, title, preview, media,
                width, height, "VÍDEO", "YouTube", true));
        }
        return new BackgroundPage(items, null, lines.Length == pageSize);
    }

    async Task<BackgroundPage> SearchWallhavenAsync(string search, int page)
    {
        var minimum = MinimumBackgroundSize();
        var atLeast = minimum.width > 0 ? $"&atleast={minimum.width}x{minimum.height}" : "";
        var url = "https://wallhaven.cc/api/v1/search" +
                  $"?q={Uri.EscapeDataString(search)}&categories=111&purity=100&ratios=16x9" +
                  $"{atLeast}&sorting=relevance&order=desc&page={page}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Backstage/0.9");
        using var response = await _mediaClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var items = new List<BackgroundItem>();

        foreach (var wall in root.GetProperty("data").EnumerateArray())
        {
            int width = wall.GetProperty("dimension_x").GetInt32();
            int height = wall.GetProperty("dimension_y").GetInt32();
            var mime = wall.GetProperty("file_type").GetString() ?? "image/jpeg";
            items.Add(new BackgroundItem(
                wall.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                $"{search} · wallpaper",
                wall.GetProperty("thumbs").GetProperty("large").GetString() ?? "",
                wall.GetProperty("path").GetString() ?? "",
                width, height, mime.Contains("png") ? "PNG" : "JPG", "Wallhaven", false));
        }

        var meta = root.GetProperty("meta");
        int current = meta.GetProperty("current_page").GetInt32();
        int last = meta.GetProperty("last_page").GetInt32();
        return new BackgroundPage(items, (current + 1).ToString(), current < last);
    }

    async void ApplyBackgroundResult_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackgroundItem item) return;
        if (item.IsLocal)
        {
            try
            {
                _currentBackground = item.MediaUrl;
                bool keep = PersistBackgroundCheck.IsChecked == true;
                SaveVisualDefault(1, keep ? item.MediaUrl : "");
                var ack = await SendLiveCommandAsync($"apply-bg {item.MediaUrl}");
                StatusText.Text = ack?.StartsWith("ok ") == true
                    ? "Background aplicado AO VIVO."
                    : ack == null
                        ? "Abra o Clone Hero e aplique novamente durante a música."
                        : $"O mod respondeu: {ack}";
                if (keep) StatusText.Text += " · padrão salvo.";
            }
            catch (Exception ex) { StatusText.Text = $"Não foi possível aplicar: {ex.Message}"; }
            return;
        }

        var directory = Path.Combine(GameDir, "Custom",
            item.Animated ? "Video Backgrounds" : "Image Backgrounds");
        var minimum = MinimumBackgroundSize();
        var outputExtension = item.Animated ? "mp4" : "jpg";
        var finalPath = Path.Combine(directory,
            $"backstage-{item.Source.ToLowerInvariant()}-{item.Id}.{outputExtension}");
        var tmpPath = finalPath + $".tmp.{outputExtension}";
        string downloadPath = null;

        try
        {
            Directory.CreateDirectory(directory);
            StatusText.Text = item.Animated
                ? $"Baixando {item.Width}×{item.Height} e compactando em alta qualidade…"
                : "Baixando e compactando a imagem em 1080p…";

            if (item.Source == "YouTube")
            {
                downloadPath = Path.Combine(Path.GetTempPath(), $"backstage-{Guid.NewGuid():N}.mp4");
                await DownloadYouTubeVideoAsync(
                    $"https://www.youtube.com/watch?v={item.Id}", downloadPath, item.Height);
            }
            else
            {
                var extension = Path.GetExtension(new Uri(item.MediaUrl).AbsolutePath);
                downloadPath = Path.Combine(Path.GetTempPath(), $"backstage-{Guid.NewGuid():N}{extension}");
                using var request = new HttpRequestMessage(HttpMethod.Get, item.MediaUrl);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserAgent);
                if (item.Source == "Pinterest")
                    request.Headers.Referrer = new Uri("https://www.pinterest.com/");
                using var response = await _mediaClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 512L * 1024 * 1024)
                    throw new InvalidOperationException("o vídeo original passa de 512 MB; escolha outro resultado.");
                await using var output = File.Create(downloadPath);
                await response.Content.CopyToAsync(output);
            }
            if (item.Animated) await ConvertAnimatedAsync(downloadPath, tmpPath);
            else await CompactImageAsync(downloadPath, tmpPath);

            if (item.Animated)
            {
                var actual = await ProbeVideoSizeAsync(tmpPath);
                if (actual.width < minimum.width || actual.height < minimum.height)
                {
                    var choice = MessageBox.Show(this,
                        $"O vídeo real é {actual.width}×{actual.height}, abaixo do filtro {minimum.width}×{minimum.height}.\n\n" +
                        "Sim — aplicar mesmo assim\nNão — excluir este download",
                        "Qualidade abaixo do filtro", MessageBoxButton.YesNo,
                        MessageBoxImage.Warning, MessageBoxResult.No);
                    if (choice != MessageBoxResult.Yes)
                    {
                        StatusText.Text = "Download excluído · nenhuma alteração foi aplicada.";
                        return;
                    }
                }
            }
            File.Move(tmpPath, finalPath, overwrite: true);
            if (item.Animated) await CreateVideoPreviewAsync(finalPath);
            _currentBackground = finalPath;
            bool keep = PersistBackgroundCheck.IsChecked == true;
            SaveVisualDefault(1, keep ? finalPath : "");
            var ack = await SendLiveCommandAsync($"apply-bg {finalPath}");
            StatusText.Text = ack?.StartsWith("ok ") == true
                ? "Background aplicado AO VIVO."
                : ack == null
                    ? "Background instalado · abra o jogo e aplique novamente."
                    : $"O mod respondeu: {ack}";
            if (keep) StatusText.Text += " · padrão salvo.";
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível aplicar: {ex.Message}"; }
        finally
        {
            if (downloadPath != null && File.Exists(downloadPath)) File.Delete(downloadPath);
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    void BackgroundPreview_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackgroundItem { Kind: "VÍDEO" } item ||
            (sender as FrameworkElement)?.FindName("BackgroundVideoPreview") is not MediaElement player ||
            !Uri.TryCreate(item.MediaUrl, UriKind.Absolute, out var uri)) return;
        player.Opacity = 0;
        player.Source = uri;
        player.Play();
    }

    void BackgroundPreview_MouseLeave(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.FindName("BackgroundVideoPreview") is not MediaElement player) return;
        player.Stop();
        player.Close();
        player.Opacity = 0;
    }

    void BackgroundPreview_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is MediaElement player) player.Opacity = 1;
    }

    void BackgroundPreview_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not MediaElement player) return;
        player.Close();
        player.Opacity = 0;
    }

    static Task ConvertAnimatedAsync(string input, string output)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        arguments.AddRange(new[]
        {
            "-i", input, "-t", "30",
            "-vf", "fps=30,scale='min(3840,iw)':'min(2160,ih)':force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2",
            "-an", "-c:v", "libx264", "-preset", "medium", "-crf", "18",
            "-pix_fmt", "yuv420p", "-movflags", "+faststart", output,
        });
        return RunFfmpegAsync(arguments);
    }

    static Task CompactImageAsync(string input, string output) => RunFfmpegAsync(new[]
    {
        "-hide_banner", "-loglevel", "error", "-y", "-i", input,
        "-vf", "scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080",
        "-frames:v", "1", "-q:v", "4", output,
    });

    static async Task DownloadYouTubeVideoAsync(string url, string output, int height)
    {
        int maxHeight = height >= 2160 ? 2160 : 1080;
        await RunYtDlpAsync(new[]
        {
            "--no-playlist", "--quiet", "--no-warnings", "--download-sections", "*0-30",
            "-f", $"bv*[height<={maxHeight}]+ba/b[height<={maxHeight}]",
            "--merge-output-format", "mp4", "-o", output, url,
        });
        if (!File.Exists(output))
            throw new InvalidOperationException("o YouTube não entregou o arquivo de vídeo.");
    }

    static async Task<string> RunYtDlpAsync(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("yt-dlp")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start); }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("yt-dlp não está instalado; instale com: winget install yt-dlp.yt-dlp");
        }
        using (process)
        {
            if (process == null) throw new InvalidOperationException("yt-dlp não iniciou.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"yt-dlp falhou: {await error}");
            return await output;
        }
    }

    static async Task RunFfmpegAsync(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("FFmpeg não iniciou.");
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg falhou: {await error}");
    }

    static async Task<(int width, int height)> ProbeVideoSizeAsync(string path)
    {
        var start = new ProcessStartInfo("ffprobe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
            "-of", "csv=p=0:s=x", path,
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("FFprobe não iniciou.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var parts = (await output).Trim().Split('x');
        if (process.ExitCode != 0 || parts.Length != 2 ||
            !int.TryParse(parts[0], out int width) || !int.TryParse(parts[1], out int height))
            throw new InvalidOperationException($"não foi possível medir o vídeo: {await error}");
        return (width, height);
    }

    async void ImportBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolha um background",
            Filter = "Backgrounds|*.jpg;*.jpeg;*.png;*.gif;*.webm;*.mp4;*.avi;*.ogv;*.mpeg",
        };
        if (dialog.ShowDialog(this) != true) return;

        var extension = Path.GetExtension(dialog.FileName);
        var gif = extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
        var video = gif || new[] { ".webm", ".mp4", ".avi", ".ogv", ".mpeg" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase);
        var directory = Path.Combine(GameDir, "Custom", video ? "Video Backgrounds" : "Image Backgrounds");
        var finalPath = Path.Combine(directory, gif
            ? Path.GetFileNameWithoutExtension(dialog.FileName) + ".mp4"
            : Path.GetFileName(dialog.FileName));
        var tmpPath = finalPath + (gif ? ".tmp.mp4" : ".tmp");

        try
        {
            Directory.CreateDirectory(directory);
            if (gif) await ConvertAnimatedAsync(dialog.FileName, tmpPath);
            else File.Copy(dialog.FileName, tmpPath, overwrite: true);
            File.Move(tmpPath, finalPath, overwrite: true);
            if (video) await CreateVideoPreviewAsync(finalPath);
            _currentBackground = finalPath;
            bool keep = PersistBackgroundCheck.IsChecked == true;
            SaveVisualDefault(1, keep ? finalPath : "");
            var ack = await SendLiveCommandAsync($"apply-bg {finalPath}");
            StatusText.Text = ack == null
                ? $"Background instalado · abra o jogo e aplique novamente durante a música."
                : ack.StartsWith("ok background 0")
                    ? "Background instalado · entre em uma música e aplique novamente."
                    : ack.StartsWith("ok ")
                    ? "Background aplicado AO VIVO."
                    : $"O mod respondeu: {ack}";
            if (keep) StatusText.Text += " · padrão salvo.";
            if (BgTypeCombo.SelectedIndex == 3) await LoadLocalBackgroundsAsync(BgSearchBox.Text.Trim());
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível aplicar o background: {ex.Message}"; }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }

    void DeleteBackgroundResult_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BackgroundItem { IsLocal: true } item) return;
        if (MessageBox.Show(this, $"Enviar “{item.Title}” para a Lixeira?", "Excluir background",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        try
        {
            var fullPath = Path.GetFullPath(item.MediaUrl);
            if (!IsLocalBackgroundPath(fullPath))
                throw new InvalidOperationException("arquivo fora das pastas de backgrounds");
            if (File.Exists(fullPath))
                FileSystem.DeleteFile(fullPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            var previewPath = VideoPreviewPath(fullPath);
            if (File.Exists(previewPath))
                FileSystem.DeleteFile(previewPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            var defaults = ReadVisualDefaults();
            if (defaults != null &&
                string.Equals(defaults[1], fullPath, StringComparison.OrdinalIgnoreCase))
            {
                SaveVisualDefault(1, "");
                PersistBackgroundCheck.IsChecked = false;
            }
            _backgrounds.Remove(item);
            BgEmptyState.Visibility = _backgrounds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            BgResultText.Text = $"{_backgrounds.Count} background(s) baixado(s)";
            StatusText.Text = $"{item.Title} enviado para a Lixeira.";
        }
        catch (Exception ex) { StatusText.Text = $"Não foi possível excluir: {ex.Message}"; }
    }

    void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Não abriu o navegador: {ex.Message}"; }
    }

    static void WriteCommand(string command)
    {
        var path = Path.Combine(GameDir, "backstage_cmd.txt");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, command);
        File.Move(tmp, path, overwrite: true);
    }

    static async Task<string> SendLiveCommandAsync(string command)
    {
        if (Process.GetProcessesByName("Clone Hero").Length == 0) return null;
        var ack = Path.Combine(GameDir, "backstage_ack.txt");
        if (File.Exists(ack)) File.Delete(ack);
        WriteCommand(command);

        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(100);
            if (!File.Exists(ack)) continue;
            var response = File.ReadAllText(ack).Trim();
            File.Delete(ack);
            return response;
        }
        return "erro: o mod não respondeu";
    }

    void Scan_Click(object sender, RoutedEventArgs e)
        => StartFullScan();

    bool StartFullScan()
    {
        if (MessageBox.Show(
                "Este fallback reescaneia a biblioteca inteira do Clone Hero. Use somente se a música nova não apareceu. Continuar?",
                "Scan completo", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;
        try
        {
            WriteCommand("scan");
            StatusText.Text = "Scan completo enviado ao Clone Hero.";
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Scan falhou: {ex.Message}";
            return false;
        }
    }
}

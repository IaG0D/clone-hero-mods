using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Radar;

/// <summary>Uma musica da biblioteca, ja normalizada. Sem nada de Unity aqui de proposito:
/// isso deixa o miolo da busca testavel fora do jogo.</summary>
public readonly struct Entry
{
    public readonly int Id;
    public readonly string Title, Artist, Album, Genre, Charter;
    public readonly string[] TitleW, ArtistW, AlbumW, GenreW, CharterW; // palavras, p/ typo-match

    public Entry(int id, string title, string artist, string album, string genre, string charter)
    {
        Id = id;
        Title = Text.Normalize(title);
        Artist = Text.Normalize(artist);
        Album = Text.Normalize(album);
        Genre = Text.Normalize(genre);
        Charter = Text.Normalize(charter);
        TitleW = Split(Title); ArtistW = Split(Artist); AlbumW = Split(Album);
        GenreW = Split(Genre); CharterW = Split(Charter);
    }

    static string[] Split(string s) =>
        s.Length == 0 ? Array.Empty<string>() : s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

public readonly struct Hit
{
    public readonly int Id;
    public readonly int Score;
    public Hit(int id, int score) { Id = id; Score = score; }
}

/// <summary>Campo escolhido na UI do jogo. All = modo padrao "Song", que o Radar trata
/// como multi-campo; os demais sao escolha explicita do jogador e valem so no campo.</summary>
public enum Field { All, Title, Artist, Album, Genre, Charter }

public static class Text
{
    // Artigo inicial so atrapalha: quem procura "Trooper" quer achar "The Trooper".
    static readonly string[] Articles = { "the ", "an ", "a ", "os ", "as ", "o ", "uma ", "um " };

    /// <summary>Minusculas invariantes, sem acento, pontuacao virando espaco.
    /// "Motorhead" acha "Motörhead", "coracao" acha "Coração", "acdc" chega perto de "AC/DC".</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
        }
        while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
        return sb.ToString();
    }

    public static string StripArticle(string s)
    {
        foreach (var a in Articles)
            if (s.Length > a.Length && s.StartsWith(a, StringComparison.Ordinal))
                return s.Substring(a.Length);
        return s;
    }

    public static string[] Tokenize(string query) =>
        Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}

public sealed class SongIndex
{
    // Pontuacao por qualidade do match. Sem ranking a busca e inutil:
    // digitar "one" devolveria centenas de resultados em ordem aleatoria.
    const int Exact = 1000;
    const int Prefix = 800;
    const int WordPrefix = 600;
    const int Substring = 400;
    const int NearMatch = 300;   // typo de 1 edicao ("mettalli" -> "metallica")

    // Titulo pesa mais que artista, que pesa mais que o resto.
    const int TitleWeight = 3;
    const int ArtistWeight = 2;

    readonly Entry[] _entries;

    // Pilha de resultados: ao digitar mais uma letra filtramos o resultado anterior,
    // nao a biblioteca inteira. Ao apagar, desempilha. Trocar de campo invalida a pilha.
    readonly List<(string Query, int[] Ids)> _stack = new();
    Field _stackField = Field.All;

    public SongIndex(IReadOnlyList<Entry> entries)
    {
        _entries = new Entry[entries.Count];
        for (int i = 0; i < entries.Count; i++) _entries[i] = entries[i];
    }

    public int Count => _entries.Length;

    public Hit[] Search(string rawQuery, Field field = Field.All)
    {
        var normalized = Text.Normalize(rawQuery);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (field != _stackField) { _stack.Clear(); _stackField = field; }

        if (tokens.Length == 0)
        {
            _stack.Clear();
            return Array.Empty<Hit>();
        }

        // Desempilha tudo que nao e mais prefixo da query atual (usuario apagou letras).
        while (_stack.Count > 0 && !normalized.StartsWith(_stack[_stack.Count - 1].Query, StringComparison.Ordinal))
            _stack.RemoveAt(_stack.Count - 1);

        var candidates = _stack.Count > 0 ? _stack[_stack.Count - 1].Ids : null;

        var hits = new List<Hit>();
        if (candidates == null)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                int score = Score(_entries[i], tokens, field);
                if (score > 0) hits.Add(new Hit(i, score));
            }
        }
        else
        {
            foreach (int id in candidates)
            {
                int score = Score(_entries[id], tokens, field);
                if (score > 0) hits.Add(new Hit(id, score));
            }
        }

        hits.Sort(static (a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Id.CompareTo(b.Id));

        var result = hits.ToArray();
        var ids = new int[result.Length];
        for (int i = 0; i < result.Length; i++) ids[i] = result[i].Id;
        if (_stack.Count == 0 || _stack[_stack.Count - 1].Query != normalized)
            _stack.Add((normalized, ids));

        return result;
    }

    /// <summary>AND entre tokens, OR entre campos: todo token precisa achar algum campo.
    /// E isso que faz "metallica master" achar Master of Puppets — artista e titulo na mesma query.</summary>
    static int Score(in Entry e, string[] tokens, Field field)
    {
        int total = 0;
        foreach (var token in tokens)
        {
            int best;
            switch (field)
            {
                // Escolha explicita do jogador: so o campo escolhido conta.
                case Field.Title:   best = FieldScore(e.Title, e.TitleW, token); break;
                case Field.Artist:  best = FieldScore(e.Artist, e.ArtistW, token); break;
                case Field.Album:   best = FieldScore(e.Album, e.AlbumW, token); break;
                case Field.Genre:   best = FieldScore(e.Genre, e.GenreW, token); break;
                case Field.Charter: best = FieldScore(e.Charter, e.CharterW, token); break;

                default: // modo padrao: multi-campo, e o que faz "metallica master" funcionar.
                    best = FieldScore(e.Title, e.TitleW, token) * TitleWeight;
                    int artist = FieldScore(e.Artist, e.ArtistW, token) * ArtistWeight;
                    if (artist > best) best = artist;
                    if (best < Exact)
                    {
                        int other = Math.Max(FieldScore(e.Album, e.AlbumW, token),
                                    Math.Max(FieldScore(e.Charter, e.CharterW, token),
                                             FieldScore(e.Genre, e.GenreW, token)));
                        if (other > best) best = other;
                    }
                    break;
            }

            if (best == 0) return 0; // token sem match: musica fora.
            total += best;
        }
        return total;
    }

    static int FieldScore(string field, string[] words, string token)
    {
        if (field.Length == 0) return 0;
        if (field == token) return Exact;

        var stripped = Text.StripArticle(field);
        if (stripped == token) return Exact;
        if (field.StartsWith(token, StringComparison.Ordinal) || stripped.StartsWith(token, StringComparison.Ordinal))
            return Prefix;

        int at = field.IndexOf(token, StringComparison.Ordinal);
        if (at > 0 && field[at - 1] == ' ') return WordPrefix;
        if (at >= 0) return Substring;

        // Typo: token a 1 edicao de distancia do prefixo de alguma palavra.
        // Curto demais gera falso positivo, por isso o piso de 4 letras.
        if (token.Length >= 4)
            foreach (var word in words)
                if (NearPrefix(word, token)) return NearMatch;

        return 0;
        // ponytail: subsequencia estilo fzf foi removida de proposito — em biblioteca de 15k
        // ela enchia o resultado de falso positivo e o filtro parecia bugado. NearPrefix cobre typo.
    }

    /// <summary>Token casa com prefixo da palavra tolerando UMA edicao (troca, sobra ou falta
    /// de letra). "mettalli" e "metalica" casam com "metallica"; subsequencia pura nao pega
    /// letra duplicada, e typo de digitacao rapida e quase sempre uma edicao so.</summary>
    static bool NearPrefix(string word, string token)
    {
        int i = 0;
        while (i < token.Length && i < word.Length && token[i] == word[i]) i++;
        if (i == token.Length) return true;               // token e prefixo exato
        return Rest(token, i + 1, word, i + 1)            // troca de letra
            || Rest(token, i + 1, word, i)                // letra sobrando no token
            || Rest(token, i, word, i + 1);               // letra faltando no token
    }

    static bool Rest(string token, int ti, string word, int wi)
    {
        while (ti < token.Length && wi < word.Length && token[ti] == word[wi]) { ti++; wi++; }
        return ti == token.Length; // resto do token casou como prefixo do resto da palavra
    }
}

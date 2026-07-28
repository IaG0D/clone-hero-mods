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

    public Entry(int id, string title, string artist, string album, string genre, string charter)
    {
        Id = id;
        Title = Text.Normalize(title);
        Artist = Text.Normalize(artist);
        Album = Text.Normalize(album);
        Genre = Text.Normalize(genre);
        Charter = Text.Normalize(charter);
    }
}

public readonly struct Hit
{
    public readonly int Id;
    public readonly int Score;
    public Hit(int id, int score) { Id = id; Score = score; }
}

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
    const int FuzzyBase = 60;

    // Titulo pesa mais que artista, que pesa mais que o resto.
    const int TitleWeight = 3;
    const int ArtistWeight = 2;

    readonly Entry[] _entries;

    // Pilha de resultados: ao digitar mais uma letra filtramos o resultado anterior,
    // nao a biblioteca inteira. Ao apagar, desempilha.
    readonly List<(string Query, int[] Ids)> _stack = new();

    public SongIndex(IReadOnlyList<Entry> entries)
    {
        _entries = new Entry[entries.Count];
        for (int i = 0; i < entries.Count; i++) _entries[i] = entries[i];
    }

    public int Count => _entries.Length;

    public Hit[] Search(string rawQuery)
    {
        var normalized = Text.Normalize(rawQuery);
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

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
                int score = Score(_entries[i], tokens);
                if (score > 0) hits.Add(new Hit(i, score));
            }
        }
        else
        {
            foreach (int id in candidates)
            {
                int score = Score(_entries[id], tokens);
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
    static int Score(in Entry e, string[] tokens)
    {
        int total = 0;
        foreach (var token in tokens)
        {
            int best = FieldScore(e.Title, token) * TitleWeight;
            int artist = FieldScore(e.Artist, token) * ArtistWeight;
            if (artist > best) best = artist;

            if (best < Exact)
            {
                int other = Math.Max(FieldScore(e.Album, token),
                            Math.Max(FieldScore(e.Charter, token), FieldScore(e.Genre, token)));
                if (other > best) best = other;
            }

            if (best == 0) return 0; // token sem match em campo nenhum: musica fora.
            total += best;
        }
        return total;
    }

    static int FieldScore(string field, string token)
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

        return Subsequence(field, token);
    }

    /// <summary>Match fuzzy estilo fzf: as letras do token aparecem em ordem, nao precisam ser vizinhas.
    /// Sequencia contigua maior pontua mais, senao "abc" casaria com qualquer coisa.</summary>
    static int Subsequence(string hay, string needle)
    {
        if (needle.Length < 2) return 0; // uma letra so casaria com metade da biblioteca.

        int from = 0, streak = 0, longest = 0;
        for (int i = 0; i < needle.Length; i++)
        {
            int found = hay.IndexOf(needle[i], from);
            if (found < 0) return 0;
            streak = (found == from && i > 0) ? streak + 1 : 1;
            if (streak > longest) longest = streak;
            from = found + 1;
        }
        return FuzzyBase + longest * 5;
    }
}

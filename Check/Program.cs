using System;
using System.Collections.Generic;
using System.Diagnostics;
using Radar;

// Check do miolo do Radar. Roda com: dotnet run --project Check
// Nao e suite de teste, e o menor troco que quebra se a busca quebrar.

int failures = 0;

void Check(string what, bool ok)
{
    Console.WriteLine((ok ? "  ok   " : "  FALHA") + "  " + what);
    if (!ok) failures++;
}

var songs = new List<Entry>
{
    new(0, "Master of Puppets",   "Metallica",  "Master of Puppets", "Thrash Metal", "Charter A"),
    new(1, "One",                 "Metallica",  "...And Justice",    "Thrash Metal", "Charter B"),
    new(2, "The Trooper",         "Iron Maiden","Piece of Mind",     "Heavy Metal",  "Charter C"),
    new(3, "Ace of Spades",       "Motörhead",  "Ace of Spades",     "Speed Metal",  "Charter D"),
    new(4, "Coração de Estudante","Milton N.",  "Ao Vivo",           "MPB",          "Charter E"),
    new(5, "Thunderstruck",       "AC/DC",      "The Razors Edge",   "Hard Rock",    "Charter F"),
};
var index = new SongIndex(songs);

int[] Ids(string q)
{
    var hits = index.Search(q);
    var ids = new int[hits.Length];
    for (int i = 0; i < hits.Length; i++) ids[i] = hits[i].Id;
    return ids;
}
bool Top(string q, int id) { var r = Ids(q); return r.Length > 0 && r[0] == id; }
bool Has(string q, int id) { foreach (var i in Ids(q)) if (i == id) return true; return false; }

Console.WriteLine("normalizacao");
Check("acento some: 'Motorhead' acha Motörhead",        Has("motorhead", 3));
Check("acento some: 'coracao' acha Coração",            Has("coracao", 4));
Check("pontuacao vira espaco: 'ac dc' acha AC/DC",      Has("ac dc", 5));
Check("artigo inicial ignorado: 'trooper' no topo",     Top("trooper", 2));

Console.WriteLine("busca multi-campo (o que a busca nativa nao faz)");
Check("'metallica master' acha Master of Puppets",      Top("metallica master", 0));
Check("'metallica' traz as duas do Metallica",          Ids("metallica").Length >= 2);
Check("token sem match nenhum zera o resultado",        Ids("metallica zzzzq").Length == 0);

Console.WriteLine("ranking");
Check("match exato ganha do substring: 'one'",          Top("one", 1));
Check("'puppets' acha por palavra no meio do titulo",   Has("puppets", 0));

Console.WriteLine("pilha incremental (digitar e apagar)");
index.Search("m"); index.Search("me"); index.Search("met");
var afterTyping = Ids("metallica");
index.Search("met");                       // apagou letras
var afterDeleting = Ids("metallica");      // e digitou de novo
Check("resultado igual digitando e apagando",
      afterTyping.Length == afterDeleting.Length && afterTyping.Length > 0);

Console.WriteLine("velocidade com biblioteca grande");
var big = new List<Entry>(20000);
for (int i = 0; i < 20000; i++)
    big.Add(new Entry(i, "Song Number " + i, "Band " + (i % 500), "Album " + (i % 900), "Rock", "Charter " + (i % 50)));
var bigIndex = new SongIndex(big);
var sw = Stopwatch.StartNew();
bigIndex.Search("band 42");
sw.Stop();
Console.WriteLine($"         20.000 musicas, varredura fria: {sw.Elapsed.TotalMilliseconds:F1} ms");
Check("varredura fria abaixo de 100 ms",                sw.Elapsed.TotalMilliseconds < 100);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "tudo ok" : $"{failures} falha(s)");
return failures == 0 ? 0 : 1;

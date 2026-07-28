using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Backstage;

// Check do motor do Backstage: UMA busca real + UM download pequeno, fora do jogo.
// Roda com: dotnet run --project Backstage [-- "query"]
// Etiqueta: o teste faz o minimo de requisicoes possivel (1 busca + 1 download).

var query = args.Length > 0 ? string.Join(' ', args) : "metallica master of puppets";
var failures = 0;

void Check(string what, bool ok)
{
    Console.WriteLine((ok ? "  ok   " : "  FALHA") + "  " + what);
    if (!ok) failures++;
}

using var chorus = new ChorusClient();

Console.WriteLine($"busca: \"{query}\"");
var result = await chorus.SearchAsync(query);
Check($"achou resultados ({result.Found} de {result.OutOf} charts no Chorus)", result.Found > 0);
Check("todo resultado tem md5 de 32 hex", result.Data.All(c => c.Md5.Length == 32));

foreach (var c in result.Data.Take(5))
    Console.WriteLine($"         {c.Artist} - {c.Name}  [charter: {c.Charter}]  md5:{c.Md5[..8]}...");

Console.WriteLine("cache");
var again = await chorus.SearchAsync(query);
Check("segunda busca identica vem do cache (mesmo objeto)", ReferenceEquals(result, again));

Console.WriteLine("download (.sng, direto pro scratch — nao toca na pasta do jogo)");
var dest = Path.Combine(Path.GetTempPath(), "backstage-check");
var pick = result.Data.First();
long lastDone = 0, lastTotal = 0;
var progress = new Progress<(long done, long total)>(p => { lastDone = p.done; lastTotal = p.total; });

var path = await chorus.DownloadSngAsync(pick, dest, progress);
var info = new FileInfo(path);
Check($"arquivo baixado ({info.Length / 1024.0 / 1024.0:F1} MB)", info.Exists && info.Length > 0);
Check("progresso reportado ate o fim", lastDone == info.Length && lastTotal == info.Length);

// Formato .sng comeca com o magic "SNGPKG" — se nao tiver, veio HTML de erro ou lixo.
var magic = new byte[6];
await using (var f = File.OpenRead(path)) _ = await f.ReadAsync(magic);
Check("magic SNGPKG confere", Encoding.ASCII.GetString(magic) == "SNGPKG");

File.Delete(path); // limpeza: o check nao deixa rastro

Console.WriteLine();
Console.WriteLine(failures == 0 ? "tudo ok" : $"{failures} falha(s)");
return failures == 0 ? 0 : 1;

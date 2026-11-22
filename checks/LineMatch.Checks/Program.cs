using System.Text;
using LineMatch;

var root = Path.Combine(Path.GetTempPath(), "line-match-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
    var small = Path.Combine(root, "small.txt");
    var output = Path.Combine(root, "success.txt");
    File.WriteAllLines(small, ["ALPHA", "BETA"]);
    File.WriteAllLines(Path.Combine(work, "2.txt"), ["BETA", "other"]);
    File.WriteAllLines(Path.Combine(work, "1.txt"), ["other", "ALPHA"]);
    var matches = new List<Match>();
    var engine = new SearchEngine();
    var options = new SearchOptions(small, work, output, 4, 100, new UTF8Encoding(false, true), SourceDisposition.Keep);
    await engine.RunAsync(options, new Progress<SearchProgress>(), matches.Add, Console.WriteLine, CancellationToken.None);
    if (matches.Count != 2 || matches[0] != new Match("1.txt", 2, "ALPHA") || matches[1] != new Match("2.txt", 1, "BETA")) throw new Exception("Ordering or exact matching failed");
    if (File.ReadAllLines(output).Length != 2 || !File.Exists(Path.Combine(work, "1.txt"))) throw new Exception("Output or source retention failed");
    File.Delete(output);
    File.WriteAllLines(small, ["NO_MATCH"]);
    matches.Clear();
    await engine.RunAsync(options, new Progress<SearchProgress>(), matches.Add, _ => {}, CancellationToken.None);
    if (File.Exists(output) || matches.Count > 0) throw new Exception("Zero-match case failed");
    Console.WriteLine("PASS: ordering, exact matches, source retention, no output on zero matches");
}
finally { Directory.Delete(root, true); }

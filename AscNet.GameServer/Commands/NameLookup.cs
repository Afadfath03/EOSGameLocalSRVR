using System.Text;

namespace AscNet.GameServer.Commands
{
    /// Resolves a free-text name typed in chat (split into space-separated tokens) to table rows.
    /// Matching order: numeric id, exact name, all tokens matched as word prefixes, then the best
    /// partial matches are returned as suggestions so the caller can report them.
    internal static class NameLookup
    {
        internal sealed record Candidate<T>(T Row, int Id, string Name);

        internal sealed record Result<T>(Candidate<T>? Match, IReadOnlyList<Candidate<T>> Suggestions);

        internal static Result<T> Resolve<T>(
            IEnumerable<T> rows,
            Func<T, int> id,
            Func<T, IEnumerable<string?>> names,
            IReadOnlyList<string> tokens)
        {
            string query = string.Join(' ', tokens).Trim();
            List<(T Row, int Id, List<string> Names)> entries = rows
                .Select(row => (Row: row, Id: id(row), Names: names(row)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()))
                .Where(entry => entry.Names.Count > 0)
                .ToList();

            if (int.TryParse(query, out int numericId))
            {
                var byId = entries.FirstOrDefault(entry => entry.Id == numericId);
                return byId.Names is null
                    ? new(null, [])
                    : new(new(byId.Row, byId.Id, byId.Names[0]), []);
            }

            string normalizedQuery = Normalize(query);
            List<(T Row, int Id, List<string> Names)> exact = entries
                .Where(entry => entry.Names.Any(name => Normalize(name) == normalizedQuery))
                .ToList();
            if (exact.Count > 0)
                return Pick(exact, exact.SelectMany(entry => entry.Names.Where(name => Normalize(name) == normalizedQuery)).First());

            string[] queryWords = Words(query);
            if (queryWords.Length == 0)
                return new(null, []);

            List<(T Row, int Id, List<string> Names, string Name, double Score)> scored = entries
                .Select(entry =>
                {
                    (string name, double score) = entry.Names
                        .Select(name => (name, score: Score(queryWords, Words(name))))
                        .OrderByDescending(pair => pair.score)
                        .First();
                    return (entry.Row, entry.Id, entry.Names, Name: name, Score: score);
                })
                .Where(entry => entry.Score > 0)
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Name.Length)
                .ThenBy(entry => entry.Id)
                .ToList();

            List<(T Row, int Id, List<string> Names, string Name, double Score)> full = scored
                .Where(entry => entry.Score >= queryWords.Length)
                .ToList();
            if (full.Count > 0)
            {
                // Every typed word matched; prefer the rows whose names have no extra words.
                int fewestWords = full.Min(entry => Words(entry.Name).Length);
                List<(T Row, int Id, List<string> Names, string Name, double Score)> tightest = full
                    .Where(entry => Words(entry.Name).Length == fewestWords)
                    .ToList();
                if (tightest.Select(entry => Normalize(entry.Name)).Distinct().Count() == 1)
                {
                    var first = tightest.OrderBy(entry => entry.Id).First();
                    return new(new(first.Row, first.Id, first.Name), tightest.Count > 1
                        ? tightest.Skip(1).Select(entry => new Candidate<T>(entry.Row, entry.Id, entry.Name)).ToList()
                        : []);
                }
                return new(null, full.Take(10).Select(entry => new Candidate<T>(entry.Row, entry.Id, entry.Name)).ToList());
            }

            // No row matched every word: suggest the best rows for each typed word, interleaved, so a
            // query like "simulated coin" offers both the Simulation* and the *Coin rows.
            List<List<Candidate<T>>> perWord = queryWords
                .Select(word => scored
                    .Select(entry => (entry, score: Score([word], Words(entry.Name))))
                    .Where(pair => pair.score > 0)
                    .OrderByDescending(pair => pair.score)
                    .ThenBy(pair => pair.entry.Name.Length)
                    .ThenBy(pair => pair.entry.Id)
                    .Take(5)
                    .Select(pair => new Candidate<T>(pair.entry.Row, pair.entry.Id, pair.entry.Name))
                    .ToList())
                .ToList();
            List<Candidate<T>> suggestions = new();
            for (int rank = 0; rank < 5 && suggestions.Count < 10; rank++)
                foreach (List<Candidate<T>> list in perWord)
                    if (rank < list.Count && suggestions.All(existing => existing.Id != list[rank].Id))
                        suggestions.Add(list[rank]);
            return new(null, suggestions.Take(10).ToList());
        }

        internal static string FormatSuggestions<T>(string kind, string query, IReadOnlyList<Candidate<T>> suggestions)
        {
            if (suggestions.Count == 0)
                return $"No {kind} matches \"{query}\".";
            StringBuilder text = new($"No exact {kind} match for \"{query}\". Did you mean: ");
            text.Append(string.Join(", ", suggestions.Select(candidate => $"{candidate.Name} ({candidate.Id})")));
            text.Append('?');
            return text.ToString();
        }

        private static Result<T> Pick<T>(List<(T Row, int Id, List<string> Names)> matches, string name)
        {
            var first = matches.OrderBy(entry => entry.Id).First();
            return new(new(first.Row, first.Id, name), matches.Count > 1
                ? matches.OrderBy(entry => entry.Id).Skip(1).Select(entry => new Candidate<T>(entry.Row, entry.Id, name)).ToList()
                : []);
        }

        // Each typed word scores 1 when it is a prefix of a name word (or vice versa for short names),
        // and a fractional score for a long shared stem such as "simulated" ~ "simulation".
        private static double Score(string[] queryWords, string[] nameWords)
        {
            double total = 0;
            foreach (string word in queryWords)
            {
                double best = 0;
                foreach (string candidate in nameWords)
                {
                    if (candidate == word || candidate.StartsWith(word, StringComparison.Ordinal))
                    {
                        best = 1;
                        break;
                    }
                    int shared = SharedPrefix(word, candidate);
                    if (shared >= 5)
                        best = Math.Max(best, 0.5 + 0.4 * shared / Math.Max(word.Length, candidate.Length));
                }
                total += best;
            }
            return total;
        }

        private static int SharedPrefix(string left, string right)
        {
            int length = Math.Min(left.Length, right.Length), index = 0;
            while (index < length && left[index] == right[index])
                index++;
            return index;
        }

        private static string Normalize(string value) => string.Join(' ', Words(value));

        private static string[] Words(string value) => value
            .ToLowerInvariant()
            .Split([' ', ':', '-', '_', '·', '.', ',', '\'', '"', '(', ')', '[', ']', '/'], StringSplitOptions.RemoveEmptyEntries);
    }
}

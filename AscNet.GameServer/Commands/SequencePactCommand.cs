using System.Text;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fuben.awareness;

namespace AscNet.GameServer.Commands
{
    // Sequence Pact (Awareness) GM command. Progress goes through AwarenessModule's own rules. The server has
    // no Awareness notify, so the client picks the data up when the Sequence Pact panel requests
    // AwarenessGetDataRequest, or on the next login (NotifyLogin AwarenessInfo).
    [CommandName("sequencepact")]
    internal class SequencePactCommand : Command
    {
        public SequencePactCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Sequence Pact: /sequencepact status | complete [all|<chapter>] | assign <chapter> <character name|id|none> | autoassign | reset. <chapter> is a ChapterId (3001-3006) or index 1-6.";

        [Argument(0, @"^(status|complete|assign|autoassign|reset)$", "Operation: status, complete, assign, autoassign or reset", ArgumentFlags.IgnoreCase)]
        protected string Action { get; set; } = string.Empty;

        [Argument(1, @"^(all|[0-9]+)$", "Chapter: all, a ChapterId or a 1-based chapter index", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        protected string Chapter { get; set; } = string.Empty;

        [Argument(2, @"^.+$", "Character TradeName, LogName (e.g. Lucia: Pyroath), id, or none", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        protected string CharacterName { get; set; } = string.Empty;

        private AwarenessState State => session.player.Awareness ??= new();

        public override void Execute()
        {
            switch (Action.ToLowerInvariant())
            {
                case "status":
                    throw new CommandMessageCallbackException(BuildStatus());
                case "complete":
                    Complete();
                    break;
                case "assign":
                    Assign();
                    break;
                case "autoassign":
                    AutoAssign();
                    break;
                case "reset":
                    Reset();
                    break;
            }
            throw new CommandMessageCallbackException(Help);
        }

        private string BuildStatus()
        {
            StringBuilder text = new("Sequence Pact chapters:\n");
            foreach (AwarenessChapterTable chapter in AwarenessModule.ChapterTables)
            {
                AwarenessChapterState? record = State.Chapters.FirstOrDefault(saved => saved.ChapterId == chapter.Id);
                AwarenessChallengeState? challenge = State.Challenges.FirstOrDefault(saved => saved.ChapterId == chapter.Id);
                string progress = AwarenessModule.IsChapterCompleted(State, chapter.Id)
                    ? "completed"
                    : $"stages {challenge?.FinishStageIds.Count(chapter.StageId.Contains) ?? 0}/{chapter.StageId.Count(id => id != 0)}";
                string occupant = record is { CharacterId: > 0 } ? DescribeCharacter(record.CharacterId) : "none";
                text.AppendLine($"{chapter.Id} (#{chapter.Site}): {progress}, occupant {occupant}");
            }
            return text.ToString().TrimEnd();
        }

        private void Complete()
        {
            List<AwarenessChapterTable> chapters = ResolveChapters(allowAll: true, defaultAll: true);
            List<int> changed = chapters.Where(chapter => AwarenessModule.CompleteChapter(State, chapter.Id)).Select(chapter => chapter.Id).ToList();
            if (changed.Count > 0)
                session.player.Save();
            throw new CommandMessageCallbackException((changed.Count == 0
                    ? "Selected Sequence Pact chapters were already complete."
                    : $"Completed Sequence Pact chapters {string.Join(", ", changed)}.")
                + " Use /sequencepact autoassign to occupy them. " + RefreshHint);
        }

        private void Assign()
        {
            AwarenessChapterTable chapter = ResolveChapters(allowAll: false, defaultAll: false).Single();
            string query = string.Join(' ', args.Skip(2)).Trim();
            if (query.Length == 0)
                throw new CommandMessageCallbackException("Usage: /sequencepact assign <chapter> <character name|id|none>");
            long characterId = query.Equals("none", StringComparison.OrdinalIgnoreCase) || query == "0" ? 0 : ResolveCharacter(query);
            int code = AwarenessModule.SetChapterCharacter(session, chapter.Id, characterId, out bool changed);
            if (code != 0)
                throw new CommandMessageCallbackException(DescribeCode(code, chapter.Id, characterId));
            if (changed)
                session.player.Save();
            throw new CommandMessageCallbackException((characterId == 0
                    ? $"Chapter {chapter.Id} is no longer occupied."
                    : $"{DescribeCharacter(characterId)} now occupies chapter {chapter.Id}.")
                + " " + RefreshHint);
        }

        private void AutoAssign()
        {
            HashSet<long> occupied = State.Chapters.Where(record => record.CharacterId > 0).Select(record => record.CharacterId).ToHashSet();
            List<AwarenessChapterTable> open = AwarenessModule.ChapterTables
                .Where(chapter => AwarenessModule.IsChapterCompleted(State, chapter.Id)
                    && State.Chapters.All(record => record.ChapterId != chapter.Id || record.CharacterId == 0))
                .ToList();
            Dictionary<int, List<CharacterData>> candidates = open.ToDictionary(
                chapter => chapter.Id,
                chapter => session.character.Characters
                    .Where(character => !occupied.Contains(character.Id)
                        && AwarenessModule.CanSelectCharacter(session, chapter, character))
                    .ToList());

            List<string> assigned = [];
            List<int> unfilled = [];
            // Fill the most constrained chapter first and spend the character that the fewest other open chapters can use.
            while (candidates.Count > 0)
            {
                int chapterId = candidates
                    .OrderBy(entry => entry.Value.Count(character => !occupied.Contains(character.Id)))
                    .ThenBy(entry => entry.Key)
                    .First().Key;
                List<CharacterData> pool = candidates[chapterId].Where(character => !occupied.Contains(character.Id)).ToList();
                candidates.Remove(chapterId);
                CharacterData? pick = pool
                    .OrderBy(character => candidates.Values.Count(other => other.Contains(character)))
                    .ThenByDescending(character => character.Ability)
                    .ThenByDescending(character => character.Level)
                    .ThenBy(character => character.Id)
                    .FirstOrDefault();
                if (pick is null || AwarenessModule.SetChapterCharacter(session, chapterId, pick.Id, out _) != 0)
                {
                    unfilled.Add(chapterId);
                    continue;
                }
                occupied.Add(pick.Id);
                assigned.Add($"{chapterId} -> {DescribeCharacter(pick.Id)}");
            }

            if (assigned.Count > 0)
                session.player.Save();
            List<int> locked = AwarenessModule.ChapterTables.Where(chapter => !AwarenessModule.IsChapterCompleted(State, chapter.Id)).Select(chapter => chapter.Id).ToList();
            StringBuilder text = new(assigned.Count == 0 ? "No Sequence Pact chapter was assigned." : "Assigned:\n" + string.Join("\n", assigned));
            if (unfilled.Count > 0)
                text.Append($"\nNo eligible free character for chapters {string.Join(", ", unfilled)}.");
            if (locked.Count > 0)
                text.Append($"\nNot completed (use /sequencepact complete): {string.Join(", ", locked)}.");
            text.Append(' ').Append(RefreshHint);
            throw new CommandMessageCallbackException(text.ToString());
        }

        private void Reset()
        {
            State.Chapters.Clear();
            State.Challenges.Clear();
            State.Teams.Clear();
            session.player.Save();
            throw new CommandMessageCallbackException("Sequence Pact progress, occupations and teams were reset. Relog so the client drops its cached chapter records.");
        }

        private const string RefreshHint = "Reopen the Sequence Pact screen (or relog) to refresh the client; fight bonuses apply from the next battle.";

        private List<AwarenessChapterTable> ResolveChapters(bool allowAll, bool defaultAll)
        {
            IReadOnlyList<AwarenessChapterTable> chapters = AwarenessModule.ChapterTables;
            if (Chapter.Length == 0 && defaultAll || allowAll && Chapter.Equals("all", StringComparison.OrdinalIgnoreCase))
                return chapters.ToList();
            if (!int.TryParse(Chapter, out int id))
                throw new CommandMessageCallbackException("Specify a chapter: a ChapterId (" + string.Join(", ", chapters.Select(chapter => chapter.Id)) + $") or an index 1-{chapters.Count}.");
            AwarenessChapterTable? match = chapters.FirstOrDefault(chapter => chapter.Id == id)
                ?? (id >= 1 && id <= chapters.Count ? chapters[id - 1] : null);
            return match is null
                ? throw new CommandMessageCallbackException($"Unknown Sequence Pact chapter {id}. Valid: {string.Join(", ", chapters.Select(chapter => chapter.Id))}.")
                : [match];
        }

        private long ResolveCharacter(string query)
        {
            List<CharacterTable> rows = TableReaderV2.Parse<CharacterTable>();
            HashSet<long> owned = session.character.Characters.Select(character => (long)character.Id).ToHashSet();
            List<CharacterTable> matches;
            if (long.TryParse(query, out long id))
                matches = rows.Where(row => row.Id == id).ToList();
            else
            {
                string key = Normalize(query);
                matches = rows.Where(row => Normalize(row.TradeName) == key || Normalize(row.LogName) == key).ToList();
                if (matches.Count == 0)
                    matches = rows.Where(row => Normalize(row.Name) == key).ToList();
            }
            if (matches.Count == 0)
                throw new CommandMessageCallbackException($"No character matches '{query}'. Use a TradeName (e.g. Pyroath), LogName (e.g. Lucia: Pyroath) or id.");
            List<CharacterTable> ownedMatches = matches.Where(row => owned.Contains(row.Id)).ToList();
            if (ownedMatches.Count == 1)
                return ownedMatches[0].Id;
            if (ownedMatches.Count == 0)
                throw new CommandMessageCallbackException($"You do not own {string.Join(", ", matches.Select(Describe))}.");
            throw new CommandMessageCallbackException($"'{query}' is ambiguous: {string.Join(", ", ownedMatches.Select(Describe))}.");
        }

        private static string Normalize(string? value) =>
            new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private static string Describe(CharacterTable row) => $"{(string.IsNullOrEmpty(row.LogName) ? row.Name : row.LogName)} ({row.Id})";

        private static string DescribeCharacter(long characterId) =>
            TableReaderV2.Parse<CharacterTable>().FirstOrDefault(row => row.Id == characterId) is { } row ? Describe(row) : characterId.ToString();

        private string DescribeCode(int code, int chapterId, long characterId) => code switch
        {
            AwarenessModule.ChapterUnfinished => characterId != 0 && session.character.Characters.All(owned => owned.Id != characterId)
                ? $"You do not own {DescribeCharacter(characterId)}."
                : AwarenessModule.IsChapterCompleted(State, chapterId)
                    ? $"{DescribeCharacter(characterId)} does not meet chapter {chapterId}'s occupation conditions."
                    : $"Chapter {chapterId} is not completed; use /sequencepact complete {chapterId} first.",
            AwarenessModule.CharacterOccupied => $"{DescribeCharacter(characterId)} already occupies another chapter.",
            AwarenessModule.ChallengeMissing => $"Chapter {chapterId} has no occupant record yet; complete it first.",
            _ => $"Sequence Pact rejected the change (code {code})."
        };
    }

    [CommandName("awareness")]
    internal sealed class AwarenessCommand : SequencePactCommand
    {
        public AwarenessCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }
    }
}

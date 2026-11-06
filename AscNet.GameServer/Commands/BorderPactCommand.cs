using System.Text;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.character;
using AscNet.Table.V2.share.fuben.assign;

namespace AscNet.GameServer.Commands
{
    // Border Pact (Assign) GM command. Progress is written through AssignModule's own rules; the server has
    // no Assign notify, so the client picks the data up when the Border Pact panel requests AssignGetDataRequest
    // or on the next login (NotifyLogin.AssignChapterRecord).
    [CommandName("borderpact")]
    internal class BorderPactCommand : Command
    {
        public BorderPactCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Border Pact: /borderpact status | complete [all|<chapter>] | assign <chapter> <character name|id|none> | autoassign | reset. <chapter> is a ChapterId (2001-2008) or index 1-8.";

        [Argument(0, @"^(status|complete|assign|autoassign|reset)$", "Operation: status, complete, assign, autoassign or reset", ArgumentFlags.IgnoreCase)]
        protected string Action { get; set; } = string.Empty;

        [Argument(1, @"^(all|[0-9]+)$", "Chapter: all, a ChapterId or a 1-based chapter index", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        protected string Chapter { get; set; } = string.Empty;

        [Argument(2, @"^.+$", "Character TradeName, LogName (e.g. Lucia: Pyroath), id, or none", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        protected string CharacterName { get; set; } = string.Empty;

        private AssignState State => session.player.Assign ??= new();

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
            StringBuilder text = new("Border Pact chapters:\n");
            foreach (AssignChapterTable chapter in AssignModule.ChapterTables)
            {
                AssignChapterState? record = State.Chapters.FirstOrDefault(saved => saved.ChapterId == chapter.ChapterId);
                int cleared = chapter.GroupId.Count(groupId => State.Groups.Any(group => group.GroupId == groupId && group.Count > 0));
                bool completed = AssignModule.IsChapterCompleted(State, chapter);
                string reward = !completed ? "locked" : record?.IsGetReward == true ? "claimed" : "claimable";
                string occupant = record is { CharacterId: > 0 } ? DescribeCharacter(record.CharacterId) : "none";
                text.AppendLine($"{chapter.ChapterId}: groups {cleared}/{chapter.GroupId.Count}, reward {reward}, occupant {occupant}");
            }
            return text.ToString().TrimEnd();
        }

        private void Complete()
        {
            List<AssignChapterTable> chapters = ResolveChapters(allowAll: true, defaultAll: true);
            List<int> changed = chapters.Where(chapter => AssignModule.CompleteChapter(State, chapter)).Select(chapter => chapter.ChapterId).ToList();
            if (changed.Count > 0)
                session.player.Save();
            throw new CommandMessageCallbackException((changed.Count == 0
                    ? "Selected Border Pact chapters were already complete."
                    : $"Completed Border Pact chapters {string.Join(", ", changed)}; rewards are claimable in the Border Pact screen.")
                + " Use /borderpact autoassign to occupy them. " + RefreshHint);
        }

        private void Assign()
        {
            AssignChapterTable chapter = ResolveChapters(allowAll: false, defaultAll: false).Single();
            string query = string.Join(' ', args.Skip(2)).Trim();
            if (query.Length == 0)
                throw new CommandMessageCallbackException("Usage: /borderpact assign <chapter> <character name|id|none>");
            long characterId = query.Equals("none", StringComparison.OrdinalIgnoreCase) || query == "0" ? 0 : ResolveCharacter(query);
            if (characterId != 0)
            {
                CharacterData character = session.character.Characters.First(owned => owned.Id == characterId);
                if (AssignModule.IsChapterCompleted(State, chapter)
                    && !ExhibitionModule.MeetsCharacterConditions(session, character, chapter.SelectCharCondition))
                    throw new CommandMessageCallbackException($"{DescribeCharacter(characterId)} does not meet chapter {chapter.ChapterId}'s occupation conditions.");
            }
            int code = AssignModule.SetChapterCharacter(session, chapter.ChapterId, characterId, out bool changed);
            if (code != 0)
                throw new CommandMessageCallbackException(DescribeCode(code, chapter.ChapterId, characterId));
            if (changed)
                session.player.Save();
            throw new CommandMessageCallbackException((characterId == 0
                    ? $"Chapter {chapter.ChapterId} is no longer occupied."
                    : $"{DescribeCharacter(characterId)} now occupies chapter {chapter.ChapterId}.")
                + " " + RefreshHint);
        }

        private void AutoAssign()
        {
            HashSet<long> occupied = State.Chapters.Where(record => record.CharacterId > 0).Select(record => record.CharacterId).ToHashSet();
            List<AssignChapterTable> open = AssignModule.ChapterTables
                .Where(chapter => AssignModule.IsChapterCompleted(State, chapter)
                    && State.Chapters.All(record => record.ChapterId != chapter.ChapterId || record.CharacterId == 0))
                .ToList();
            Dictionary<int, List<CharacterData>> candidates = open.ToDictionary(
                chapter => chapter.ChapterId,
                chapter => session.character.Characters
                    .Where(character => !occupied.Contains(character.Id)
                        && ExhibitionModule.MeetsCharacterConditions(session, character, chapter.SelectCharCondition))
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
                if (pick is null || AssignModule.SetChapterCharacter(session, chapterId, pick.Id, out _) != 0)
                {
                    unfilled.Add(chapterId);
                    continue;
                }
                occupied.Add(pick.Id);
                assigned.Add($"{chapterId} -> {DescribeCharacter(pick.Id)}");
            }

            if (assigned.Count > 0)
                session.player.Save();
            List<int> locked = AssignModule.ChapterTables.Where(chapter => !AssignModule.IsChapterCompleted(State, chapter)).Select(chapter => chapter.ChapterId).ToList();
            StringBuilder text = new(assigned.Count == 0 ? "No Border Pact chapter was assigned." : "Assigned:\n" + string.Join("\n", assigned));
            if (unfilled.Count > 0)
                text.Append($"\nNo eligible free character for chapters {string.Join(", ", unfilled)}.");
            if (locked.Count > 0)
                text.Append($"\nNot completed (use /borderpact complete): {string.Join(", ", locked)}.");
            text.Append(' ').Append(RefreshHint);
            throw new CommandMessageCallbackException(text.ToString());
        }

        private void Reset()
        {
            State.Chapters.Clear();
            State.Groups.Clear();
            State.Teams.Clear();
            session.player.Save();
            throw new CommandMessageCallbackException("Border Pact progress, occupations and teams were reset. Relog so the client drops its cached chapter records.");
        }

        private const string RefreshHint = "Reopen the Border Pact screen (or relog) to refresh the client; fight bonuses apply from the next battle.";

        private List<AssignChapterTable> ResolveChapters(bool allowAll, bool defaultAll)
        {
            IReadOnlyList<AssignChapterTable> chapters = AssignModule.ChapterTables;
            if (Chapter.Length == 0 && defaultAll || allowAll && Chapter.Equals("all", StringComparison.OrdinalIgnoreCase))
                return chapters.ToList();
            if (!int.TryParse(Chapter, out int id))
                throw new CommandMessageCallbackException("Specify a chapter: a ChapterId (" + string.Join(", ", chapters.Select(chapter => chapter.ChapterId)) + $") or an index 1-{chapters.Count}.");
            AssignChapterTable? match = chapters.FirstOrDefault(chapter => chapter.ChapterId == id)
                ?? (id >= 1 && id <= chapters.Count ? chapters[id - 1] : null);
            return match is null
                ? throw new CommandMessageCallbackException($"Unknown Border Pact chapter {id}. Valid: {string.Join(", ", chapters.Select(chapter => chapter.ChapterId))}.")
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
            AssignModule.ChapterUnfinished => $"Chapter {chapterId} is not completed; use /borderpact complete {chapterId} first.",
            AssignModule.CharacterOccupied => $"{DescribeCharacter(characterId)} already occupies another chapter.",
            AssignModule.CharacterMissing => characterId == 0 ? $"Chapter {chapterId} has no occupant." : $"You do not own {DescribeCharacter(characterId)}.",
            _ => $"Border Pact rejected the change (code {code})."
        };
    }

    [CommandName("assign")]
    internal sealed class AssignCommand : BorderPactCommand
    {
        public AssignCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }
    }
}

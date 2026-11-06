using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.Table.V2.share.fuben.awareness;
using MessagePack;

namespace AscNet.GameServer.Handlers;

[MessagePackObject(true)] public sealed class AwarenessGetDataRequest { }
[MessagePackObject(true)] public sealed class NotifyLoginAwarenessInfo { public AwarenessInfo AwarenessInfo { get; set; } = new(); }
[MessagePackObject(true)] public sealed class AwarenessGetDataResponse { public AwarenessInfo AwarenessInfo { get; set; } = new(); }
[MessagePackObject(true)] public sealed class AwarenessSetCharacterRequest { public int ChapterId { get; set; } public long CharacterId { get; set; } }
[MessagePackObject(true)] public sealed class AwarenessSetCharacterResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class AwarenessSetTeamRequest { public List<List<long>> TeamList { get; set; } = new(); public List<int> FirstFightPosList { get; set; } = new(); public int ChapterId { get; set; } public List<int> CaptainPosList { get; set; } = new(); }
[MessagePackObject(true)] public sealed class AwarenessSetTeamResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class AwarenessResetStageRequest { public int ChapterId { get; set; } public int StageId { get; set; } }
[MessagePackObject(true)] public sealed class AwarenessResetStageResponse { public int Code { get; set; } }
[MessagePackObject(true)] public sealed class AwarenessInfo { public List<AwarenessChapterInfo> ChapterRecords { get; set; } = new(); public List<AwarenessChallengeInfo> ChallengeRecords { get; set; } = new(); public List<AwarenessTeamInfo> TeamRecords { get; set; } = new(); }
[MessagePackObject(true)] public sealed class AwarenessChapterInfo { public int ChapterId { get; set; } public long CharacterId { get; set; } }
[MessagePackObject(true)] public sealed class AwarenessChallengeInfo { public int ChapterId { get; set; } public int Count { get; set; } public List<int> FinishStageIds { get; set; } = new(); }
[MessagePackObject(true)] public sealed class AwarenessTeamInfo { public int ChapterId { get; set; } public List<List<long>> TeamInfoList { get; set; } = new(); public List<int> CaptainPosList { get; set; } = new(); public List<int> FirstFightPosList { get; set; } = new(); }

internal static class AwarenessModule
{
    internal const int ChapterMissing = 20182001, TeamMissing = 20182002, StageFinished = 20182003, TeamUnset = 20182004, FightCardCount = 20182005, FightCardMismatch = 20182006, StageMissing = 20182007, SettleStageMismatch = 20182008, TeamListEmpty = 20182009, TeamCountMismatch = 20182010, DuplicateMember = 20182011, ChapterUnfinished = 20182012, CharacterOccupied = 20182013, ChallengeMissing = 20182014, ResetUnfinished = 20182015, FinishedTeamImmutable = 20182016;
    private sealed record Data(IReadOnlyList<AwarenessChapterTable> Chapters, IReadOnlyDictionary<int, AwarenessChapterTable> ByChapter, IReadOnlyDictionary<int, AwarenessTeamInfoTable> Teams);
    private static readonly Lazy<Data> Runtime = new(() =>
    {
        List<AwarenessChapterTable> chapters = TableReaderV2.Parse<AwarenessChapterTable>().OrderBy(x => x.Site).ToList();
        return new(chapters, chapters.ToDictionary(x => x.Id), TableReaderV2.Parse<AwarenessTeamInfoTable>().ToDictionary(x => x.Id));
    });

    internal static NotifyLoginAwarenessInfo BuildLoginData(Player player) => new() { AwarenessInfo = BuildInfo(player) };
    internal static AwarenessInfo BuildInfo(Player player)
    {
        AwarenessState state = player.Awareness ??= new();
        return new AwarenessInfo
        {
            ChapterRecords = Runtime.Value.Chapters.Select(row => state.Chapters.FirstOrDefault(x => x.ChapterId == row.Id)).OfType<AwarenessChapterState>().Select(x => new AwarenessChapterInfo { ChapterId = x.ChapterId, CharacterId = x.CharacterId }).ToList(),
            ChallengeRecords = Runtime.Value.Chapters.Select(row => { AwarenessChallengeState? x = state.Challenges.FirstOrDefault(y => y.ChapterId == row.Id); return new AwarenessChallengeInfo { ChapterId = row.Id, Count = x?.Count ?? 0, FinishStageIds = x?.FinishStageIds.Where(row.StageId.Contains).ToList() ?? new() }; }).ToList(),
            TeamRecords = Runtime.Value.Chapters.Select(row => { AwarenessTeamState? x = state.Teams.FirstOrDefault(y => y.ChapterId == row.Id); return new AwarenessTeamInfo { ChapterId = row.Id, TeamInfoList = x?.TeamInfoList.Select(y => y.ToList()).ToList() ?? new(), CaptainPosList = x?.CaptainPosList.ToList() ?? new(), FirstFightPosList = x?.FirstFightPosList.ToList() ?? new() }; }).ToList()
        };
    }

    [RequestPacketHandler("AwarenessGetDataRequest")]
    public static void GetData(Session session, Packet.Request packet) => session.SendResponse(new AwarenessGetDataResponse { AwarenessInfo = BuildInfo(session.player) }, packet.Id);

    [RequestPacketHandler("AwarenessSetCharacterRequest")]
    public static void SetCharacter(Session session, Packet.Request packet)
    {
        AwarenessSetCharacterRequest r = packet.Deserialize<AwarenessSetCharacterRequest>();
        int code = SetChapterCharacter(session, r.ChapterId, r.CharacterId, out bool changed);
        if (changed) session.player.Save();
        SendCharacter(session, packet, code);
    }

    internal static IReadOnlyList<AwarenessChapterTable> ChapterTables => Runtime.Value.Chapters;

    internal static bool IsChapterCompleted(AwarenessState state, int chapterId) =>
        state.Chapters.Any(x => x.ChapterId == chapterId) || state.Challenges.Any(x => x.ChapterId == chapterId && x.Count > 0);

    internal static bool CanSelectCharacter(Session session, AwarenessChapterTable chapter, CharacterData character) => CanSelect(session, chapter, character);

    /// <summary>Applies AwarenessSetCharacterRequest's rules; the caller saves when <paramref name="changed"/> is set.</summary>
    internal static int SetChapterCharacter(Session session, int chapterId, long characterId, out bool changed)
    {
        changed = false;
        if (!Runtime.Value.ByChapter.TryGetValue(chapterId, out AwarenessChapterTable? chapter)) return ChapterMissing;
        AwarenessState state = session.player.Awareness ??= new();
        bool completed = IsChapterCompleted(state, chapterId);
        if (characterId == 0)
        {
            AwarenessChapterState? own = state.Chapters.FirstOrDefault(x => x.ChapterId == chapterId);
            if (!completed || own is null) return ChallengeMissing;
            if (own.CharacterId != 0) { own.CharacterId = 0; changed = true; }
            return 0;
        }
        if (!completed || session.character.Characters.FirstOrDefault(x => x.Id == characterId) is not CharacterData character || !CanSelect(session, chapter, character)) return ChapterUnfinished;
        if (state.Chapters.Any(x => x.ChapterId != chapterId && x.CharacterId == characterId)) return CharacterOccupied;
        AwarenessChapterState record = Chapter(state, chapterId);
        if (record.CharacterId != characterId) { record.CharacterId = characterId; changed = true; }
        return 0;
    }

    /// <summary>GM completion: records one full clear exactly as TrySettle does after the last stage.</summary>
    internal static bool CompleteChapter(AwarenessState state, int chapterId)
    {
        if (!Runtime.Value.ByChapter.ContainsKey(chapterId) || IsChapterCompleted(state, chapterId)) return false;
        AwarenessChallengeState challenge = Challenge(state, chapterId);
        challenge.Count++;
        challenge.FinishStageIds.Clear();
        Chapter(state, chapterId);
        return true;
    }

    [RequestPacketHandler("AwarenessSetTeamRequest")]
    public static void SetTeam(Session session, Packet.Request packet)
    {
        AwarenessSetTeamRequest r = packet.Deserialize<AwarenessSetTeamRequest>();
        if (!Runtime.Value.ByChapter.TryGetValue(r.ChapterId, out AwarenessChapterTable? chapter)) { SendTeam(session, packet, ChapterMissing); return; }
        if (r.TeamList.Count == 0) { SendTeam(session, packet, TeamListEmpty); return; }
        if (r.TeamList.Count != chapter.TeamInfoId.Count || r.CaptainPosList.Count != r.TeamList.Count || r.FirstFightPosList.Count != r.TeamList.Count) { SendTeam(session, packet, TeamCountMismatch); return; }
        AwarenessState state = session.player.Awareness ??= new();
        AwarenessChallengeState challenge = Challenge(state, r.ChapterId);
        for (int i = 0; i < r.TeamList.Count; i++)
        {
            if (!Runtime.Value.Teams.TryGetValue(chapter.TeamInfoId[i], out AwarenessTeamInfoTable? config)) { SendTeam(session, packet, TeamMissing); return; }
            List<long> team = r.TeamList[i];
            if (team.Count != config.NeedCharacter || team.Any(x => x <= 0) || team.Distinct().Count() != team.Count || team.Any(x => session.character.Characters.All(c => c.Id != x))) { SendTeam(session, packet, DuplicateMember); return; }
            if (r.CaptainPosList[i] < 1 || r.FirstFightPosList[i] < 1 || r.CaptainPosList[i] > team.Count || r.FirstFightPosList[i] > team.Count) { SendTeam(session, packet, TeamCountMismatch); return; }
            if (i < chapter.StageId.Count && challenge.FinishStageIds.Contains(chapter.StageId[i])) { SendTeam(session, packet, FinishedTeamImmutable); return; }
        }
        if (r.TeamList.SelectMany(x => x).Distinct().Count() != r.TeamList.Sum(x => x.Count)) { SendTeam(session, packet, DuplicateMember); return; }
        AwarenessTeamState target = Team(state, r.ChapterId);
        target.TeamInfoList = r.TeamList.Select(x => x.ToList()).ToList(); target.CaptainPosList = r.CaptainPosList.ToList(); target.FirstFightPosList = r.FirstFightPosList.ToList();
        session.player.Save(); SendTeam(session, packet);
    }

    [RequestPacketHandler("AwarenessResetStageRequest")]
    public static void ResetStage(Session session, Packet.Request packet)
    {
        AwarenessResetStageRequest r = packet.Deserialize<AwarenessResetStageRequest>();
        if (!Runtime.Value.ByChapter.TryGetValue(r.ChapterId, out AwarenessChapterTable? chapter)) { SendReset(session, packet, ChapterMissing); return; }
        if (!chapter.StageId.Contains(r.StageId)) { SendReset(session, packet, StageMissing); return; }
        AwarenessChallengeState? state = session.player.Awareness?.Challenges.FirstOrDefault(x => x.ChapterId == r.ChapterId);
        if (state is null) { SendReset(session, packet, ChallengeMissing); return; }
        if (!state.FinishStageIds.Remove(r.StageId)) { SendReset(session, packet, ResetUnfinished); return; }
        session.player.Save(); SendReset(session, packet);
    }

    internal static bool ApplyPreFight(Session session, PreFightRequest.PreFightRequestPreFightData data, out int code)
    {
        code = 0;
        AwarenessChapterTable? chapter = Runtime.Value.Chapters.FirstOrDefault(x => x.StageId.Contains((int)data.StageId));
        if (chapter is null) return false;
        AwarenessChallengeState challenge = Challenge(session.player.Awareness ??= new(), chapter.Id);
        int index = chapter.StageId.FindIndex(x => x == (int)data.StageId);
        if (index < 0 || challenge.FinishStageIds.Contains((int)data.StageId)) { code = StageFinished; return true; }
        int current = chapter.StageId.FindIndex(x => !challenge.FinishStageIds.Contains(x));
        if (index != current) { code = StageMissing; return true; }
        AwarenessTeamState? team = session.player.Awareness.Teams.FirstOrDefault(x => x.ChapterId == chapter.Id);
        if (team is null || team.TeamInfoList.Count != chapter.TeamInfoId.Count || team.TeamInfoList[index].Count == 0 || team.TeamInfoList[index].Any(id => session.character.Characters.All(character => character.Id != id))) { code = TeamUnset; return true; }
        IReadOnlyList<uint> cards = data.CardIds ?? [];
        if (cards.Count != team.TeamInfoList[index].Count) { code = FightCardCount; return true; }
        if (!cards.Select(x => (long)x).SequenceEqual(team.TeamInfoList[index])) { code = FightCardMismatch; return true; }
        return true;
    }

    internal static bool TrySettle(Session session, FightSettleResult result, out FightSettleResponse response)
    {
        response = null!;
        AwarenessChapterTable? chapter = Runtime.Value.Chapters.FirstOrDefault(x => x.StageId.Contains((int)result.StageId));
        if (chapter is null) return false;
        if (!result.IsWin || result.IsForceExit)
        {
            response = new FightSettleResponse { Code = 0, Settle = new FightSettleResponse.FightSettleResponseSettle { IsWin = false, StageId = result.StageId, LeftTime = checked((int)result.LeftTime), NpcHpInfo = result.NpcHpInfo, RewardGoodsList = [], MultiRewardGoodsList = [], ChallengeCount = 0 } };
            return true;
        }
        AwarenessState state = session.player.Awareness ??= new(); AwarenessChallengeState challenge = Challenge(state, chapter.Id);
        int index = chapter.StageId.FindIndex(x => x == (int)result.StageId);
        int current = chapter.StageId.FindIndex(x => !challenge.FinishStageIds.Contains(x));
        PreFightRequest.PreFightRequestPreFightData? preFight = session.fight?.PreFight.PreFightData;
        if (index != current || preFight is null || preFight.StageId != result.StageId || !ApplyPreFight(session, preFight, out int preFightCode) || preFightCode != 0) { response = new FightSettleResponse { Code = SettleStageMismatch }; return true; }
        if (!challenge.FinishStageIds.Contains((int)result.StageId)) challenge.FinishStageIds.Add((int)result.StageId);
        if (chapter.StageId.All(challenge.FinishStageIds.Contains)) { challenge.Count++; challenge.FinishStageIds.Clear(); Chapter(state, chapter.Id); }
        session.player.Save();
        response = new FightSettleResponse { Code = 0, Settle = new FightSettleResponse.FightSettleResponseSettle { IsWin = true, StageId = result.StageId, LeftTime = checked((int)result.LeftTime), NpcHpInfo = result.NpcHpInfo, RewardGoodsList = [], MultiRewardGoodsList = [], ChallengeCount = 0 } };
        return true;
    }

    private static AwarenessChapterState Chapter(AwarenessState state, int id) => state.Chapters.FirstOrDefault(x => x.ChapterId == id) ?? Add(state.Chapters, new AwarenessChapterState { ChapterId = id });
    private static AwarenessChallengeState Challenge(AwarenessState state, int id) => state.Challenges.FirstOrDefault(x => x.ChapterId == id) ?? Add(state.Challenges, new AwarenessChallengeState { ChapterId = id });
    private static AwarenessTeamState Team(AwarenessState state, int id) => state.Teams.FirstOrDefault(x => x.ChapterId == id) ?? Add(state.Teams, new AwarenessTeamState { ChapterId = id });
    private static T Add<T>(List<T> rows, T row) { rows.Add(row); return row; }
    private static void SendCharacter(Session s, Packet.Request p, int code = 0) => s.SendResponse(new AwarenessSetCharacterResponse { Code = code }, p.Id);
    private static void SendTeam(Session s, Packet.Request p, int code = 0) => s.SendResponse(new AwarenessSetTeamResponse { Code = code }, p.Id);
    private static void SendReset(Session s, Packet.Request p, int code = 0) => s.SendResponse(new AwarenessResetStageResponse { Code = code }, p.Id);
    private static bool CanSelect(Session s, AwarenessChapterTable chapter, CharacterData character) =>
        ExhibitionModule.MeetsCharacterConditions(s, character, chapter.SelectCharCondition);
}

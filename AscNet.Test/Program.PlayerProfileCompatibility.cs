using System.Reflection;
using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using MongoDB.Driver;
using CharacterRow = AscNet.Table.V2.share.character.CharacterTable;
using TaskRow = AscNet.Table.V2.share.task.TaskTable;
using DormitoryRow = AscNet.Table.V2.share.dormitory.DormitoryTable;
using AssignChapterRow = AscNet.Table.V2.share.fuben.assign.AssignChapterTable;
using AwarenessChapterRow = AscNet.Table.V2.share.fuben.awareness.AwarenessChapterTable;

namespace AscNet.Test;

internal partial class Program
{
    // Profile (UiPlayerInfo) request/state-to-response coverage. All Mongo collections touched by the
    // handlers are replaced by in-memory recording proxies; nothing reaches a real database.
    private static void ValidatePlayerProfileCompatibility()
    {
        List<int> characterIds = TableReaderV2.Parse<CharacterRow>().Select(row => row.Id).Distinct().Order().Take(7).ToList();
        List<int> achievementTasks = TableReaderV2.Parse<TaskRow>().Where(row => row.Type == 4).Select(row => row.Id).Order().Take(3).ToList();
        int nonAchievementTask = TableReaderV2.Parse<TaskRow>().Where(row => row.Type != 4).Select(row => row.Id).Min();
        List<DormitoryRow> dorms = TableReaderV2.Parse<DormitoryRow>().OrderBy(row => row.InitNumber).Take(2).ToList();
        int assignChapter = TableReaderV2.Parse<AssignChapterRow>().Select(row => row.ChapterId).Min();
        int awarenessChapter = TableReaderV2.Parse<AwarenessChapterRow>().Select(row => row.Id).Min();
        if (characterIds.Count < 7 || achievementTasks.Count < 3 || dorms.Count < 2 || dorms[0].InitNumber == dorms[1].InitNumber)
            throw new InvalidDataException("Player profile compatibility requires 7 characters, 3 achievement tasks and 2 ordered dormitories.");

        IMongoCollection<Player> originalPlayers = Player.collection;
        IMongoCollection<Character> originalCharacters = Character.collection;
        IMongoCollection<Guild> originalGuilds = Guild.collection;
        IMongoCollection<Player> players = DispatchProxy.Create<IMongoCollection<Player>, RecordingMongoCollectionProxy<Player>>();
        IMongoCollection<Character> characters = DispatchProxy.Create<IMongoCollection<Character>, RecordingMongoCollectionProxy<Character>>();
        IMongoCollection<Guild> guilds = DispatchProxy.Create<IMongoCollection<Guild>, RecordingMongoCollectionProxy<Guild>>();
        RecordingMongoCollectionProxy<Player> playerProxy = (RecordingMongoCollectionProxy<Player>)(object)players;
        RecordingMongoCollectionProxy<Character> characterProxy = (RecordingMongoCollectionProxy<Character>)(object)characters;
        ((RecordingMongoCollectionProxy<Guild>)(object)guilds).FindResults = [];
        Player.collection = players;
        Character.collection = characters;
        Guild.collection = guilds;
        try
        {
            const long selfUid = 48_901, otherUid = 48_902, unknownUid = 48_903;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Character selfCharacter = ProfileCharacter(selfUid, characterIds.Take(4));
            selfCharacter.Equips =
            [
                new EquipData { Id = 1, TemplateId = 1, CharacterId = characterIds[0] },
                new EquipData { Id = 2, TemplateId = 2, CharacterId = characterIds[1] }
            ];
            selfCharacter.Partners = [new PartnerData { Id = 7, TemplateId = 7, CharacterId = characterIds[0] }];
            selfCharacter.WeaponFashions =
            [
                new WeaponFashionData { Id = 501, ExpireTime = 0, UseCharacterList = [characterIds[0]] },
                new WeaponFashionData { Id = 502, ExpireTime = now - 10, UseCharacterList = [characterIds[1]] }
            ];
            Player self = CreateDrawCompatibilityPlayer(selfUid);
            self.PlayerData.Likes = 7;
            self.PlayerData.Sign = "profile-self";
            self.PlayerData.Birthday = new Birthday { Mon = 4, Day = 9 };
            self.PlayerData.AppearanceSettingInfo = new AppearanceSettingInfo { TitleType = 1, CharacterType = 1, FashionType = 1, WeaponFashionType = 1, DormitoryType = 1 };
            self.GatherRewards = [11, 12];
            self.UnlockedMedals = [new MedalUnlockState { Id = 1, Time = now - 100 }, new MedalUnlockState { Id = 2, Time = now - 100, KeepTime = 10 }];
            self.MissionProgress.ClaimedTaskIds = [achievementTasks[0], achievementTasks[1], nonAchievementTask];
            self.Dorm.Rooms = [new PlayerDormRoom { Id = (uint)dorms[1].Id, Name = "Second" }, new PlayerDormRoom { Id = (uint)dorms[0].Id, Name = "First" }];
            self.AssistCharacterId = characterIds[2];
            self.Assign.Chapters = [new AssignChapterState { ChapterId = assignChapter, CharacterId = characterIds[3] }];
            self.Awareness.Chapters = [new AwarenessChapterState { ChapterId = awarenessChapter, CharacterId = characterIds[2] }];

            using LoopbackSessionHarness harness = new(selfCharacter, self, CreateDrawCompatibilityInventory(selfUid, []), "player-profile-compat");
            int packetId = 9100;
            T Call<T>(string request, object payload) where T : class
            {
                int id = ++packetId;
                InvokeRegisteredRequestHandler(request, harness.Session, id, payload);
                return ReadResponsePayload<T>(harness, id, request.Replace("Request", "Response"), $"Profile {request} #{id}");
            }

            // Self detail: every field the preview reads, derived from persisted state.
            QueryPlayerDetailResponse detail = Call<QueryPlayerDetailResponse>("QueryPlayerDetailRequest", new QueryPlayerDetailRequest { PlayerId = selfUid });
            AssertEqual(0, detail.Code, "Profile self detail code");
            PlayerDetail selfDetail = detail.Detail ?? throw new InvalidDataException("Profile self detail missing.");
            AssertEqual(selfUid, selfDetail.Id, "Profile detail id");
            AssertEqual(7L, selfDetail.Likes, "Profile detail likes");
            AssertEqual("profile-self", selfDetail.Sign, "Profile detail sign");
            AssertEqual(9L, selfDetail.Birthday?.Day ?? 0, "Profile detail birthday");
            AssertEqual(true, selfDetail.GatherIds.SequenceEqual([11, 12]), "Profile detail gather ids");
            AssertEqual(true, selfDetail.MedalInfos.Select(medal => medal.Id).SequenceEqual([1]), "Profile detail drops expired medals");
            AssertEqual(2, selfDetail.AchievementDetail.Achievement, "Profile achievement count uses claimed Type-4 tasks only");
            AssertEqual((long)dorms[0].Id, selfDetail.DormDetail?.DormitoryId ?? 0, "Profile unset dormitory falls back to lowest InitNumber");
            AssertEqual((long)characterIds[2], selfDetail.AssistCharacterDetail?.Id ?? 0, "Profile assist character");
            AssertEqual(true, selfDetail.GuildDetail is null, "Profile guildless player has no guild detail");

            self.PlayerData.Likes = 9;
            self.MissionProgress.ClaimedTaskIds.Add(achievementTasks[2]);
            PlayerDetail changed = Call<QueryPlayerDetailResponse>("QueryPlayerDetailRequest", new QueryPlayerDetailRequest { PlayerId = selfUid }).Detail!;
            AssertEqual(9L, changed.Likes, "Profile detail likes follows state");
            AssertEqual(3, changed.AchievementDetail.Achievement, "Profile achievement count follows state");

            // SetAppearance validation (state untouched on failure).
            Dictionary<string, object?> Appearance(int type, object characters, long title = 2, long character = 1, long fashion = 3, long dormId = 0) => new()
            {
                ["CharactersAppearanceType"] = type,
                ["Characters"] = characters,
                ["AppearanceSettingInfo"] = new Dictionary<string, object>
                {
                    ["TitleType"] = title, ["CharacterType"] = character, ["FashionType"] = fashion,
                    ["WeaponFashionType"] = 2L, ["DormitoryType"] = 1L, ["DormitoryId"] = dormId
                }
            };
            int saves = playerProxy.ReplaceOneCalls;
            AssertEqual(20009011, Call<SetAppearanceResponse>("SetAppearanceRequest", Appearance(1, new object[] { characterIds[6] })).Code, "SetAppearance rejects unowned character");
            AssertEqual(5, Call<SetAppearanceResponse>("SetAppearanceRequest", Appearance(2, Array.Empty<object>())).Code, "SetAppearance rejects unknown appearance type");
            AssertEqual(5, Call<SetAppearanceResponse>("SetAppearanceRequest", Appearance(1, Array.Empty<object>(), title: 4)).Code, "SetAppearance rejects unknown show type");
            AssertEqual(5, Call<SetAppearanceResponse>("SetAppearanceRequest", Appearance(1, new object[] { characterIds[0], characterIds[0] })).Code, "SetAppearance rejects duplicates");
            AssertEqual(5, Call<SetAppearanceResponse>("SetAppearanceRequest", Appearance(1, characterIds.Take(6).Cast<object>().ToArray())).Code, "SetAppearance rejects more than five");
            AssertEqual(5, Call<SetAppearanceResponse>("SetAppearanceRequest", Appearance(1, Array.Empty<object>(), dormId: 1)).Code, "SetAppearance rejects unowned dormitory");
            AssertEqual(saves, playerProxy.ReplaceOneCalls, "SetAppearance failures do not save");
            AssertEqual(0L, self.PlayerData.AppearanceShowType, "SetAppearance failures do not mutate");

            AssertEqual(0, Call<SetAppearanceResponse>("SetAppearanceRequest",
                Appearance(1, new object[] { characterIds[1], characterIds[0] }, dormId: dorms[1].Id)).Code, "SetAppearance success");
            AssertEqual(saves + 1, playerProxy.ReplaceOneCalls, "SetAppearance saves player");
            AssertEqual(1L, self.PlayerData.AppearanceShowType, "SetAppearance persists show type");
            AssertEqual(true, self.PlayerData.ShowCharacters.SequenceEqual([(long)characterIds[1], characterIds[0]]), "SetAppearance persists characters in order");
            AssertEqual(3L, self.PlayerData.AppearanceSettingInfo!.FashionType, "SetAppearance persists settings");
            PlayerDetail saved = Call<QueryPlayerDetailResponse>("QueryPlayerDetailRequest", new QueryPlayerDetailRequest { PlayerId = selfUid }).Detail!;
            AssertEqual(1L, saved.AppearanceShowType, "Profile detail reflects show type");
            AssertEqual(2L, saved.AppearanceSettingInfo?.TitleType ?? 0, "Profile detail reflects settings");
            AssertEqual((long)dorms[1].Id, saved.DormDetail?.DormitoryId ?? 0, "Profile detail reflects chosen dormitory");
            AssertEqual("Second", saved.DormDetail?.DormitoryName, "Profile detail dormitory name");

            QueryPlayerCharacterListResponse selfList = Call<QueryPlayerCharacterListResponse>("QueryPlayerCharacterListRequest", new QueryPlayerProfileRequest { PlayerId = selfUid });
            AssertEqual(true, selfList.CharacterShows.Select(row => (int)row.Id).SequenceEqual([characterIds[1], characterIds[0]]), "Profile select showcase keeps order");

            AssertEqual(0, Call<SetAppearanceResponse>("SetAppearanceRequest", Appearance(0, new Dictionary<object, object>())).Code, "SetAppearance accepts empty Lua table as map");
            AssertEqual(0, self.PlayerData.ShowCharacters.Count, "SetAppearance empty Lua table clears showcase");
            selfList = Call<QueryPlayerCharacterListResponse>("QueryPlayerCharacterListRequest", new QueryPlayerProfileRequest { PlayerId = selfUid });
            AssertEqual(4, selfList.CharacterShows.Count, "Profile all-characters showcase lists every owned character");

            // Self character info (self may open any owned character).
            QueryCharacterInfoResponse info = Call<QueryCharacterInfoResponse>("QueryCharacterInfoRequest", new QueryCharacterInfoRequest { PlayerId = selfUid, CharacterId = characterIds[0] });
            AssertEqual(0, info.Code, "Profile character info code");
            AssertEqual((uint)characterIds[0], info.CharacterData?.Id ?? 0, "Profile character info character");
            AssertEqual(true, info.EquipData.Select(equip => equip.Id).SequenceEqual([1u]), "Profile character info equips only that character's");
            AssertEqual(7, info.PartnerData?.Id ?? 0, "Profile character info partner");
            AssertEqual(501, info.WeaponFashionId, "Profile character info weapon coating");
            AssertEqual(true, info.AssignChapterRecords.Select(record => (record.ChapterId, record.CharacterId)).SequenceEqual([(assignChapter, (long)characterIds[3])]), "Profile assign chapter records");
            AssertEqual(true, info.AwarenessSetPositions.SequenceEqual([awarenessChapter]), "Profile awareness set positions");
            QueryCharacterInfoResponse second = Call<QueryCharacterInfoResponse>("QueryCharacterInfoRequest", new QueryCharacterInfoRequest { PlayerId = selfUid, CharacterId = characterIds[1] });
            AssertEqual(0, second.WeaponFashionId, "Profile expired weapon coating is not applied");
            AssertEqual(true, second.PartnerData is null, "Profile character without partner");
            AssertEqual(20037004, Call<QueryCharacterInfoResponse>("QueryCharacterInfoRequest", new QueryCharacterInfoRequest { PlayerId = selfUid, CharacterId = characterIds[6] }).Code, "Profile unowned character info");

            // Another (offline) player: sections are gated by that player's appearance settings.
            Character otherCharacter = ProfileCharacter(otherUid, characterIds.Skip(4).Take(2));
            otherCharacter.Fashions = [new FashionList { Id = 9001 }];
            otherCharacter.FashionColors = new() { [9001] = [3, 3, 4] };
            Player other = CreateDrawCompatibilityPlayer(otherUid);
            other.PlayerData.AppearanceShowType = 1;
            other.PlayerData.ShowCharacters = [characterIds[5]];
            other.PlayerData.AppearanceSettingInfo = new AppearanceSettingInfo { TitleType = 3, CharacterType = 1, FashionType = 2, WeaponFashionType = 1, DormitoryType = 3 };
            other.Dorm.Rooms = [new PlayerDormRoom { Id = (uint)dorms[0].Id, Name = "Hidden" }];
            playerProxy.FindResults = [other];
            characterProxy.FindResults = [otherCharacter];

            PlayerDetail otherDetail = Call<QueryPlayerDetailResponse>("QueryPlayerDetailRequest", new QueryPlayerDetailRequest { PlayerId = otherUid }).Detail!;
            AssertEqual(otherUid, otherDetail.Id, "Profile other detail id");
            AssertEqual(true, otherDetail.DormDetail is null, "Profile other self-only dormitory is withheld");
            QueryPlayerCharacterListResponse otherList = Call<QueryPlayerCharacterListResponse>("QueryPlayerCharacterListRequest", new QueryPlayerProfileRequest { PlayerId = otherUid });
            AssertEqual(true, otherList.CharacterShows.Select(row => (int)row.Id).SequenceEqual([characterIds[5]]), "Profile other select showcase");
            AssertEqual(20037002, Call<QueryPlayerFashionResponse>("QueryPlayerFashionRequest", new QueryPlayerProfileRequest { PlayerId = otherUid }).Code, "Profile friend-only fashions hidden without friendship");
            AssertEqual(20037002, Call<QueryPlayerTitleResponse>("QueryPlayerTitleRequest", new QueryPlayerProfileRequest { PlayerId = otherUid }).Code, "Profile self-only titles hidden");
            AssertEqual(0, Call<QueryPlayerWeaponFashionResponse>("QueryPlayerWeaponFashionRequest", new QueryPlayerProfileRequest { PlayerId = otherUid }).Code, "Profile public weapon coatings visible");
            AssertEqual(20037003, Call<QueryCharacterInfoResponse>("QueryCharacterInfoRequest", new QueryCharacterInfoRequest { PlayerId = otherUid, CharacterId = characterIds[4] }).Code, "Profile other unshown character");
            AssertEqual(0, Call<QueryCharacterInfoResponse>("QueryCharacterInfoRequest", new QueryCharacterInfoRequest { PlayerId = otherUid, CharacterId = characterIds[5] }).Code, "Profile other shown character");

            other.PlayerData.AppearanceSettingInfo.FashionType = 1;
            QueryPlayerFashionResponse fashions = Call<QueryPlayerFashionResponse>("QueryPlayerFashionRequest", new QueryPlayerProfileRequest { PlayerId = otherUid });
            AssertEqual(0, fashions.Code, "Profile public fashions visible");
            AssertEqual(true, fashions.Fashions.SequenceEqual([9001L]), "Profile fashions list");
            AssertEqual(true, fashions.FashionColors[9001].SequenceEqual([3, 4]), "Profile fashion colours keyed by original fashion");

            playerProxy.FindResults = [];
            AssertEqual(20037001, Call<QueryPlayerDetailResponse>("QueryPlayerDetailRequest", new QueryPlayerDetailRequest { PlayerId = unknownUid }).Code, "Profile unknown player");
            Console.WriteLine("Player profile compatibility checks passed.");
        }
        finally
        {
            Player.collection = originalPlayers;
            Character.collection = originalCharacters;
            Guild.collection = originalGuilds;
        }
    }

    private static Character ProfileCharacter(long uid, IEnumerable<int> characterIds)
    {
        Character character = CreateDrawCompatibilityCharacter(uid);
        character.Characters = characterIds.Select(id => new CharacterData { Id = (uint)id, Level = 1, Quality = 1 }).ToList();
        return character;
    }
}

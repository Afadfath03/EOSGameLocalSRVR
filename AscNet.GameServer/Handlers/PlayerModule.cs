using AscNet.Common.MsgPack;
using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Table.V2.client.functional;
using AscNet.Table.V2.share.functional;
using AscNet.Table.V2.share.headportrait;
using MessagePack;
using MongoDB.Driver;

namespace AscNet.GameServer.Handlers
{

    #region MsgPackScheme
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [MessagePackObject(true)]
    public class ChangePlayerMarkRequest
    {
        public long MaskId;
    }

    [MessagePackObject(true)]
    public class ChangeCommunicationResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class TouchBoardMutualRequest
    {
        public int CharacterId;
    }

    [MessagePackObject(true)]
    public class TouchBoardMutualResponse
    {
    }

    [MessagePackObject(true)]
    public class ChangeCommunicationRequest
    {
        public long Id;
    }

    [MessagePackObject(true)]
    public class ChangePlayerBirthdayRequest : Birthday
    {
    }

    [MessagePackObject(true)]
    public class ChangePlayerBirthdayResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class ChangePlayerGenderRequest
    {
        public int Gender;
    }

    [MessagePackObject(true)]
    public class NotifyPlayerGender
    {
        public long Gender;
        public long ChangeGenderTime;
    }

    [MessagePackObject(true)]
    public class ChangePlayerGenderResponse
    {
        public int Code;
        public long Gender;
        public long ChangeGenderTime;
        public long NextCanChangeTime;
        public PlayerData PlayerData;
        public List<RewardGoods> RewardGoodsList = new();
    }

    [MessagePackObject(true)]
    public class ChangePlayerSignRequest
    {
        public string Msg;
    }

    [MessagePackObject(true)]
    public class ChangePlayerSignResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class SetHeadPortraitRequest
    {
        public long Id { get; set; }
    }

    [MessagePackObject(true)]
    public class SetHeadPortraitResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class SetHeadFrameRequest
    {
        public long Id { get; set; }
    }
    [MessagePackObject(true)]
    public class SetHeadFrameResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyHeadTimeout
    {
        public long CurrHeadPortraitId { get; set; }
        public long CurrHeadFrameId { get; set; }
        public List<long> TimeoutIds { get; set; } = new();
    }
    [MessagePackObject(true)]
    public class SetCurrentMedalRequest
    {
        public long Id { get; set; }
    }

    [MessagePackObject(true)]
    public class SetCurrentMedalResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyPlayerCurrMedalId
    {
        public long CurrMedalId { get; set; }
    }

    [MessagePackObject(true)]
    public class SetCurChatBoardRequest
    {
        public long ChatBoardId { get; set; }
    }

    [MessagePackObject(true)]
    public class SetCurChatBoardResponse
    {
        public int Code { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyCurChatBoardId
    {
        public long CurrentChatBoardId { get; set; }
    }

    [MessagePackObject(true)]
    public class NotifyPlayerName
    {
        public string Name;
    }

    [MessagePackObject(true)]
    public class ChangePlayerNameRequest
    {
        public string Name;
    }

    [MessagePackObject(true)]
    public class ChangePlayerNameResponse
    {
        public int Code;
        public long NextCanChangeTime;
    }

    [MessagePackObject(true)]
    public class RemovePlayerDisplayCharIdRequest
    {
        public long CharId;
    }

    [MessagePackObject(true)]
    public class RemovePlayerDisplayCharIdResponse
    {
        public int Code;
        public List<long> DisplayCharIdList;
    }

    [MessagePackObject(true)]
    public class AddPlayerDisplayCharIdRequest
    {
        public long CharId;
    }

    [MessagePackObject(true)]
    public class AddPlayerDisplayCharIdResponse
    {
        public int Code;
        public List<long> DisplayCharIdList;
    }

    [MessagePackObject(true)]
    public class UpdatePlayerDisplayCharIdRequest
    {
        public long NewCharId;
        public long OldCharId;
    }

    [MessagePackObject(true)]
    public class UpdatePlayerDisplayCharIdResponse
    {
        public int Code;
        public List<long> DisplayCharIdList;
    }

    [MessagePackObject(true)]
    public class SetDisplayCharIdFirstRequest
    {
        public long CharId;
    }

    [MessagePackObject(true)]
    public class SetDisplayCharIdFirstResponse
    {
        public int Code;
        public List<long> DisplayCharIdList;
    }

    // Profile ("UiPlayerInfo") wire contract. Every field below is one the EN client Lua reads:
    // XPlayerInfoManager.lua (response envelopes), XUiPlayerInfo.lua / XUiPlayerInfoBase.lua /
    // XUiPanelInfo.lua / XUiPanelCharacterList.lua / XUiPanelFashionPlayerInfo.lua /
    // XUiPanelCollectionScrollPlayerInfo.lua (Detail and list payloads), XUiPlayerInfoCharacterGrid.lua
    // and XUiPanelCharPropertyOtherV2P7.lua (character info), XUiPanelSetting.lua (preview + SetAppearance).
    [MessagePackObject(true)]
    public class QueryPlayerDetailRequest
    {
        public long PlayerId;
    }

    [MessagePackObject(true)]
    public class QueryPlayerDetailResponse
    {
        public int Code;
        public PlayerDetail? Detail;
    }

    [MessagePackObject(true)]
    public class PlayerDetail
    {
        public long Id;
        public string Name;
        public long Level;
        public string Sign;
        public long Likes;
        public long CurrHeadPortraitId;
        public long CurrHeadFrameId;
        public int CurrentWearNameplate;
        public Birthday? Birthday;
        // XExhibitionManager.SetCharacterInfo: the viewed player's claimed GatherReward ids.
        public List<int> GatherIds = new();
        public List<NotifyMedalData.NotifyMedalDataMedalInfo> MedalInfos = new();
        public PlayerDetailAchievement AchievementDetail = new();
        public long AppearanceShowType;
        public AppearanceSettingInfo? AppearanceSettingInfo;
        public PlayerDetailDorm? DormDetail;
        public PlayerDetailGuild? GuildDetail;
        public PlayerDetailAssistCharacter? AssistCharacterDetail;
    }

    [MessagePackObject(true)]
    public class PlayerDetailAchievement
    {
        public int Achievement;
    }

    [MessagePackObject(true)]
    public class PlayerDetailDorm
    {
        public long DormitoryId;
        public string DormitoryName;
    }

    [MessagePackObject(true)]
    public class PlayerDetailGuild
    {
        public long GuildId;
        public string GuildName;
        public int GuildRankLevel;
    }

    [MessagePackObject(true)]
    public class PlayerDetailAssistCharacter
    {
        public long Id;
    }

    [MessagePackObject(true)]
    public class QueryPlayerProfileRequest
    {
        public long PlayerId;
    }

    [MessagePackObject(true)]
    public class QueryPlayerCharacterListResponse
    {
        public int Code;
        public List<CharacterData> CharacterShows = new();
    }

    [MessagePackObject(true)]
    public class QueryPlayerFashionResponse
    {
        public int Code;
        public List<long> Fashions = new();
        // XFashionAgency:IsColorContains reads fashionColors[OriginalFashionId] as a colour-id list.
        public Dictionary<int, List<int>> FashionColors = new();
    }

    [MessagePackObject(true)]
    public class QueryPlayerWeaponFashionResponse
    {
        public int Code;
        public List<int> WeaponFashions = new();
    }

    [MessagePackObject(true)]
    public class QueryPlayerTitleResponse
    {
        public int Code;
        public List<NotifyScoreTitleData.NotifyScoreTitleDataTitleInfo> Titles = new();
        public List<NotifyScoreTitleData.NotifyScoreTitleDataWallInfo> Walls = new();
    }

    [MessagePackObject(true)]
    public class QueryCharacterInfoRequest
    {
        public long PlayerId;
        public long CharacterId;
    }

    [MessagePackObject(true)]
    public class QueryCharacterInfoResponse
    {
        public int Code;
        public CharacterData? CharacterData;
        public List<EquipData> EquipData = new();
        public PartnerData? PartnerData;
        public int WeaponFashionId;
        public List<QueryCharacterInfoAssignChapterRecord> AssignChapterRecords = new();
        // Awareness chapter ids that hold a stationed character (XUiGridEquipOtherV2P7: table.contains).
        public List<int> AwarenessSetPositions = new();
    }

    [MessagePackObject(true)]
    public class QueryCharacterInfoAssignChapterRecord
    {
        public int ChapterId;
        public long CharacterId;
    }

    [MessagePackObject(true)]
    public class SetAppearanceRequest
    {
        public int CharactersAppearanceType;
        // A Lua array; decoded as object so an empty or sparse Lua table (msgpack map) is still accepted.
        public object? Characters;
        public AppearanceSettingInfo? AppearanceSettingInfo;
    }

    [MessagePackObject(true)]
    public class SetAppearanceResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class XCustomComponentData
    {
        public float PositionX;
        public float PositionY;
        public float Scale;
        public float Alpha;
        public bool IsActive;
        public bool IsShowPcTips;
    }

    [MessagePackObject(true)]
    public class XKeyPadPanelCustomData
    {
        public int SchemeId;
        public uint Version;
        public int BallDirection;
        public bool IsShowFps;
        public bool IsShowSignal;
        public bool IsShowQteIcon;
        public int JoystickType;
        public float SafeScreenAreaWidth;
        public float SafeScreenAreaHeight;
        public Dictionary<int, XCustomComponentData> UiData;
    }

    [MessagePackObject(true)]
    public class SyncPlayerKeyPadSettingRequest
    {
        public int CurSchemeId;
        public List<XKeyPadPanelCustomData> PlayerKeyPadSettingList;
    }

    [MessagePackObject(true)]
    public class SyncPlayerKeyPadSettingResponse
    {
        public int Code;
    }

    [MessagePackObject(true)]
    public class RecordPlayerKeyPadSettingRequest
    {
        public int CurSchemeId;
        public XKeyPadPanelCustomData KeyPadCustomData;
    }

    [MessagePackObject(true)]
    public class RecordPlayerKeyPadSettingResponse
    {
        public int Code;
    }
    [MessagePackObject(true)]
    public class RecordPlayerPointRequest
    {
        public int PointId;
        public int PointType;
    }

    [MessagePackObject(true)]
    public class RecordPlayerPointResponse
    {
        public int Code;
    }

#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    #endregion

    internal class PlayerModule
    {
        private static readonly Lazy<HashSet<long>> ValidPlayerMarkIds = new(() =>
            TableReaderV2.Parse<FunctionalOpenTable>()
                .Select(row => (long)row.Id)
                .Concat(TableReaderV2.Parse<SkipFunctionalTable>()
                    .Select(row => (long)row.FunctionalId.GetValueOrDefault()))
                .Where(id => id > 0)
                .ToHashSet());

        [RequestPacketHandler("ChangePlayerMarkRequest")]
        public static void ChangePlayerMarkRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerMarkRequest request = packet.Deserialize<ChangePlayerMarkRequest>();

            if (!ValidPlayerMarkIds.Value.Contains(request.MaskId))
            {
                session.SendResponse(new ChangePlayerMarkResponse { Code = 1 }, packet.Id);
                return;
            }

            session.player.PlayerData.Marks ??= new();

            if (!session.player.PlayerData.Marks.Contains(request.MaskId))
            {
                session.player.PlayerData.Marks.Add(request.MaskId);
                session.player.Save();
            }
            session.SendResponse(new ChangePlayerMarkResponse(), packet.Id);
        }

        [RequestPacketHandler("ChangeCommunicationRequest")]
        public static void ChangeCommunicationRequestHandler(Session session, Packet.Request packet)
        {
            ChangeCommunicationRequest request = packet.Deserialize<ChangeCommunicationRequest>();
            session.player.PlayerData.Communications.Add(request.Id);

            session.SendResponse(new ChangeCommunicationResponse(), packet.Id);
        }

        [RequestPacketHandler("TouchBoardMutualRequest")]
        public static void TouchBoardMutualRequestHandler(Session session, Packet.Request packet)
        {
            TouchBoardMutualRequest request = packet.Deserialize<TouchBoardMutualRequest>();

            session.SendResponse(new TouchBoardMutualResponse(), packet.Id);
            TaskModule.RecordConditionType(session, 13212);
        }

        [RequestPacketHandler("ChangePlayerNameRequest")]
        public static void ChangePlayerNameRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerNameRequest request = packet.Deserialize<ChangePlayerNameRequest>();
            session.player.PlayerData.Name = request.Name;
            session.player.PlayerData.ChangeNameTime = DateTimeOffset.Now.ToUnixTimeSeconds();

            NotifyPlayerName notifyPlayerName = new() { Name = session.player.PlayerData.Name };
            session.SendPush(notifyPlayerName);
            session.SendResponse(new ChangePlayerNameResponse() { NextCanChangeTime = session.player.PlayerData.ChangeNameTime }, packet.Id);
        }

        [RequestPacketHandler("ChangePlayerSignRequest")]
        public static void ChangePlayerSignRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerSignRequest request = packet.Deserialize<ChangePlayerSignRequest>();
            session.player.PlayerData.Sign = request.Msg;

            session.SendResponse(new ChangePlayerSignResponse(), packet.Id);
        }

        [RequestPacketHandler("SetHeadPortraitRequest")]
        public static void SetHeadPortraitRequestHandler(Session session, Packet.Request packet)
        {
            SetHeadPortraitRequest request = packet.Deserialize<SetHeadPortraitRequest>();
            const int invalidRequestCode = 20012001;
            if (!CanEquipHead(session, request.Id, type: 1, DateTimeOffset.Now.ToUnixTimeSeconds()))
            {
                session.SendResponse(new SetHeadPortraitResponse { Code = invalidRequestCode }, packet.Id);
                return;
            }

            if (session.player.PlayerData.CurrHeadPortraitId != request.Id)
            {
                session.player.PlayerData.CurrHeadPortraitId = request.Id;
                session.player.Save();
            }
            session.SendResponse(new SetHeadPortraitResponse(), packet.Id);
        }

        [RequestPacketHandler("SetHeadFrameRequest")]
        public static void SetHeadFrameRequestHandler(Session session, Packet.Request packet)
        {
            SetHeadFrameRequest request = packet.Deserialize<SetHeadFrameRequest>();
            const int invalidRequestCode = 20012001;
            if (!CanEquipHead(session, request.Id, type: 2, DateTimeOffset.Now.ToUnixTimeSeconds()))
            {
                session.SendResponse(new SetHeadFrameResponse { Code = invalidRequestCode }, packet.Id);
                return;
            }

            if (session.player.PlayerData.CurrHeadFrameId != request.Id)
            {
                session.player.PlayerData.CurrHeadFrameId = request.Id;
                session.player.Save();
            }
            session.SendResponse(new SetHeadFrameResponse(), packet.Id);
        }

        /// <summary>
        /// A client may equip an owned, table-typed, currently-valid portrait/frame only.
        /// </summary>
        private static bool CanEquipHead(Session session, long id, int type, long nowUnixSeconds)
        {
            if (id <= 0)
                return false;

            HeadPortraitTable? row = TableReaderV2.Parse<HeadPortraitTable>().Find(candidate => candidate.Id == id);
            if (row is null || row.Type != type)
                return false;

            HeadPortraitList? owned = session.player.HeadPortraits.Find(candidate => candidate.Id == id);
            return owned is not null && IsHeadEntryValid(row, owned, nowUnixSeconds);
        }

        /// <summary>
        /// Mirror of XHeadPortraitManager.IsHeadPortraitValid (Forever=0, Duration=1, FixedTime=2).
        /// AscNet's HeadPortrait table has no FixedTime TimeId window, so FixedTime entries resolve as not-valid.
        /// </summary>
        public static bool IsHeadEntryValid(HeadPortraitTable row, HeadPortraitList owned, long nowUnixSeconds)
        {
            if (row is null || owned is null)
                return false;

            return (row.LimitType ?? 0) switch
            {
                0 => true, // Forever
                1 => nowUnixSeconds - owned.BeginTime < (long)owned.LeftCount * row.Duration.GetValueOrDefault(), // Duration, repeatable via LeftCount
                _ => false
            };
        }

        /// <summary>
        /// Login reconciliation hook for AccountModule. Mutates and persists: keeps expired owned
        /// entries in HeadPortraits (matching the retail NotifyLogin oracle) and repairs any expired
        /// equipped portrait/frame to a valid owned non-expired same-type default (or 0 if none).
        /// Returns the exact NotifyHeadTimeout state to push AFTER NotifyLogin; null when nothing is expired.
        /// </summary>
        public static NotifyHeadTimeout? ReconcileHeadTimeouts(Session session, DateTimeOffset now)
        {
            long nowUnixSeconds = now.ToUnixTimeSeconds();
            List<HeadPortraitTable> rows = TableReaderV2.Parse<HeadPortraitTable>();
            List<long> timeoutIds = session.player.HeadPortraits
                .Where(owned => rows.FirstOrDefault(row => row.Id == owned.Id) is { } row
                    && !IsHeadEntryValid(row, owned, nowUnixSeconds))
                .Select(owned => owned.Id)
                .ToList();

            bool changed = false;
            long portraitId = RepairEquippedHeadId(session, rows, type: 1, session.player.PlayerData.CurrHeadPortraitId, nowUnixSeconds, ref changed);
            long frameId = RepairEquippedHeadId(session, rows, type: 2, session.player.PlayerData.CurrHeadFrameId, nowUnixSeconds, ref changed);

            if (changed)
                session.player.Save();

            if (timeoutIds.Count == 0)
                return null;

            return new NotifyHeadTimeout
            {
                CurrHeadPortraitId = portraitId,
                CurrHeadFrameId = frameId,
                TimeoutIds = timeoutIds
            };
        }

        private static long RepairEquippedHeadId(Session session, List<HeadPortraitTable> rows, int type, long currentId, long nowUnixSeconds, ref bool changed)
        {
            if (currentId > 0
                && rows.FirstOrDefault(row => row.Id == currentId && row.Type == type) is { } currentRow
                && session.player.HeadPortraits.FirstOrDefault(owned => owned.Id == currentId) is { } currentOwned
                && IsHeadEntryValid(currentRow, currentOwned, nowUnixSeconds))
            {
                return currentId;
            }

            long repairedId = session.player.HeadPortraits
                .Where(owned => rows.FirstOrDefault(row => row.Id == owned.Id && row.Type == type) is { } row
                    && IsHeadEntryValid(row, owned, nowUnixSeconds))
                .OrderByDescending(owned => rows.First(row => row.Id == owned.Id).Priority)
                .Select(owned => owned.Id)
                .FirstOrDefault();
            if (repairedId != currentId)
            {
                if (type == 1)
                    session.player.PlayerData.CurrHeadPortraitId = repairedId;
                else
                    session.player.PlayerData.CurrHeadFrameId = repairedId;
                changed = true;
            }
            return repairedId;
        }

        [RequestPacketHandler("SetCurrentMedalRequest")]
        public static void SetCurrentMedalRequestHandler(Session session, Packet.Request packet)
        {
            SetCurrentMedalRequest request = packet.Deserialize<SetCurrentMedalRequest>();
            session.player.PlayerData.CurrMedalId = request.Id;
            session.player.Save();
            session.SendPush(new NotifyPlayerCurrMedalId
            {
                CurrMedalId = session.player.PlayerData.CurrMedalId
            });
            session.SendResponse(new SetCurrentMedalResponse(), packet.Id);
        }

        [RequestPacketHandler("SetCurChatBoardRequest")]
        public static void SetCurChatBoardRequestHandler(Session session, Packet.Request packet)
        {
            SetCurChatBoardRequest request = packet.Deserialize<SetCurChatBoardRequest>();
            session.player.PlayerData.CurrentChatBoardId = request.ChatBoardId;
            session.player.Save();
            session.SendPush(new NotifyCurChatBoardId
            {
                CurrentChatBoardId = session.player.PlayerData.CurrentChatBoardId
            });
            session.SendResponse(new SetCurChatBoardResponse(), packet.Id);
        }

        [RequestPacketHandler("GetPlayerInfoListRequest")]
        public static void GetPlayerInfoListRequestHandler(Session session, Packet.Request packet)
        {
            GetPlayerInfoListRequest request = packet.Deserialize<GetPlayerInfoListRequest>();
            GetPlayerInfoListResponse response = new();
            HashSet<uint> emittedPlayerIds = new();

            foreach (uint id in request.Ids)
            {
                if (!emittedPlayerIds.Add(id))
                    continue;

                Player? player = (long)id == session.player.PlayerData.Id
                    ? session.player
                    : Player.TryFromPlayerId(id);

                response.PlayerInfoList.Add(player is null ? ToFallbackPlayerInfo(id) : ToPlayerInfo(player));
            }

            session.SendResponse(response, packet.Id);
        }

        private static GetPlayerInfoListResponse.GetPlayerInfoListResponsePlayerInfo ToPlayerInfo(Player player)
        {
            return new()
            {
                Id = (uint)player.PlayerData.Id,
                Name = player.PlayerData.Name,
                Level = (int)player.PlayerData.Level,
                FriendExp = 0,
                Sign = player.PlayerData.Sign,
                CurrHeadPortraitId = (uint)player.PlayerData.CurrHeadPortraitId,
                CurrHeadFrameId = (int)player.PlayerData.CurrHeadFrameId,
                LastLoginTime = (uint)player.PlayerData.LastLoginTime,
                IsOnline = false,
                CurrMedalId = (int)player.PlayerData.CurrMedalId,
                IsCancel = false,
                DlcMultiplayerTitle = 0
            };
        }

        private static GetPlayerInfoListResponse.GetPlayerInfoListResponsePlayerInfo ToFallbackPlayerInfo(uint id)
        {
            return new()
            {
                Id = id,
                Name = $"Commandant {id}",
                Level = 1,
                FriendExp = 0,
                Sign = string.Empty,
                CurrHeadPortraitId = 9000003,
                CurrHeadFrameId = 0,
                LastLoginTime = (uint)DateTimeOffset.Now.ToUnixTimeSeconds(),
                IsOnline = false,
                CurrMedalId = 0,
                IsCancel = false,
                DlcMultiplayerTitle = 0
            };
        }

        [RequestPacketHandler("ChangePlayerBirthdayRequest")]
        public static void ChangePlayerBirthdayRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerBirthdayRequest request = packet.Deserialize<ChangePlayerBirthdayRequest>();
            session.player.PlayerData.Birthday = request;
            session.SendPush(new NotifyBirthdayPlot() { IsChange = 1 });

            session.SendResponse(new ChangePlayerBirthdayResponse(), packet.Id);
        }

        [RequestPacketHandler("ChangePlayerGenderRequest")]
        public static void ChangePlayerGenderRequestHandler(Session session, Packet.Request packet)
        {
            ChangePlayerGenderRequest request = packet.Deserialize<ChangePlayerGenderRequest>();
            if (request.Gender is < 1 or > 3)
            {
                // PlayerGenderCfgNotExist
                session.SendResponse(new ChangePlayerGenderResponse() { Code = 20002020 }, packet.Id);
                return;
            }

            bool isFirstGenderSetup = session.player.PlayerData.Gender <= 0 || session.player.PlayerData.ChangeGenderTime <= 0;
            if (!isFirstGenderSetup && session.player.PlayerData.Gender == request.Gender)
            {
                // PlayerGenderIsSame
                session.SendResponse(new ChangePlayerGenderResponse() { Code = 20002021 }, packet.Id);
                return;
            }

            ChangePlayerGenderResponse response = new();
            session.player.PlayerData.Gender = request.Gender;
            session.player.PlayerData.ChangeGenderTime = DateTimeOffset.Now.ToUnixTimeSeconds();
            response.Gender = session.player.PlayerData.Gender;
            response.ChangeGenderTime = session.player.PlayerData.ChangeGenderTime;
            response.NextCanChangeTime = session.player.PlayerData.ChangeGenderTime;
            response.PlayerData = session.player.PlayerData;

            if (isFirstGenderSetup)
            {
                Item rewardItem = session.inventory.Do(Inventory.FreeGem, 50);
                session.SendPush(new NotifyItemDataList()
                {
                    ItemDataList = { rewardItem }
                });
                response.RewardGoodsList.Add(new RewardGoods()
                {
                    RewardType = (int)RewardType.Item,
                    TemplateId = Inventory.FreeGem,
                    Count = 50
                });
                session.inventory.Save();
            }

            session.SendPush(new NotifyPlayerGender()
            {
                Gender = session.player.PlayerData.Gender,
                ChangeGenderTime = session.player.PlayerData.ChangeGenderTime
            });

            session.player.Save();
            session.SendResponse(response, packet.Id);
        }

        [RequestPacketHandler("UpdatePlayerDisplayCharIdRequest")]
        public static void UpdatePlayerDisplayCharIdRequestHandler(Session session, Packet.Request packet)
        {
            UpdatePlayerDisplayCharIdRequest request = packet.Deserialize<UpdatePlayerDisplayCharIdRequest>();
            if (session.player.PlayerData.DisplayCharIdList.Contains(request.OldCharId))
            {
                session.player.PlayerData.DisplayCharIdList[session.player.PlayerData.DisplayCharIdList.IndexOf(request.OldCharId)] = request.NewCharId;
            }

            session.SendResponse(new UpdatePlayerDisplayCharIdResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        [RequestPacketHandler("AddPlayerDisplayCharIdRequest")]
        public static void AddPlayerDisplayCharIdRequestHandler(Session session, Packet.Request packet)
        {
            AddPlayerDisplayCharIdRequest request = packet.Deserialize<AddPlayerDisplayCharIdRequest>();
            session.player.PlayerData.DisplayCharIdList.Add(request.CharId);

            session.SendResponse(new AddPlayerDisplayCharIdResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        [RequestPacketHandler("RemovePlayerDisplayCharIdRequest")]
        public static void RemovePlayerDisplayCharIdRequestHandler(Session session, Packet.Request packet)
        {
            RemovePlayerDisplayCharIdRequest request = packet.Deserialize<RemovePlayerDisplayCharIdRequest>();
            session.player.PlayerData.DisplayCharIdList.Remove(request.CharId);

            session.SendResponse(new RemovePlayerDisplayCharIdResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        [RequestPacketHandler("SetDisplayCharIdFirstRequest")]
        public static void SetDisplayCharIdFirstRequestHandler(Session session, Packet.Request packet)
        {
            SetDisplayCharIdFirstRequest request = packet.Deserialize<SetDisplayCharIdFirstRequest>();
            session.player.PlayerData.DisplayCharIdList.Remove(request.CharId);
            session.player.PlayerData.DisplayCharIdList.Insert(0, request.CharId);

            session.SendResponse(new SetDisplayCharIdFirstResponse() { DisplayCharIdList = session.player.PlayerData.DisplayCharIdList }, packet.Id);
        }

        #region Profile (UiPlayerInfo) and appearance settings

        // CodeText (share/text/CodeText.tsv) identities.
        private const int ParamsErrorCode = 5;                                  // ParamsError
        private const int CharacterNotFoundCode = 20009011;                     // CharacterManagerGetCharacterByIdNotFound
        private const int PersonalInfoQueryPlayerDataFailCode = 20037001;       // PersonalInfoQueryPlayerDataFail
        private const int PersonalInfoWithoutPermissionCode = 20037002;         // PersonalInfoWithoutPermission
        private const int PersonalInfoNoShowCharacterCode = 20037003;           // PersonalInfoNoShowCharacterWithThisId
        private const int PersonalInfoNoCharacterInfoCode = 20037004;           // PersonalInfoNoCharacterInfoWithThisId

        // XPlayerInfoConfigs.CharactersAppearanceType (All = 0, Select = 1).
        private const int CharactersAppearanceAll = 0;
        private const int CharactersAppearanceSelect = 1;

        // XUiAppearanceShowType as documented by XUiPanelSetting.lua InitAppearanceSetting:
        // "1 visible to all, 2 visible to friends, 3 visible to self".
        private const long AppearanceShowToAll = 1;
        private const long AppearanceShowToFriend = 2;
        private const long AppearanceShowToSelf = 3;

        // XUiPanelSetting.lua MAX_CHARACTER: the settings panel exposes five showcase slots.
        private const int MaxShowCharacters = 5;

        // XTaskManager.TaskType.Achievement; XAchievementManager counts finished tasks of this type.
        private const int AchievementTaskType = 4;

        private static readonly Lazy<HashSet<int>> AchievementTaskIds = new(() =>
            TableReaderV2.Parse<AscNet.Table.V2.share.task.TaskTable>()
                .Where(row => row.Type == AchievementTaskType)
                .Select(row => row.Id)
                .ToHashSet());

        internal sealed record ProfileTarget(Player Player, Character? Character, bool IsSelf);

        /// <summary>
        /// Resolves the profile owner without creating documents: the caller's own session, the owner's
        /// live session when online (freshest state), otherwise the persisted player/character documents.
        /// </summary>
        internal static ProfileTarget? ResolveProfileTarget(Session session, long playerId)
        {
            if (playerId == session.player.PlayerData.Id)
                return new ProfileTarget(session.player, session.character, IsSelf: true);
            if (playerId <= 0)
                return null;

            Session? online = Server.Instance.SessionFromUID(playerId);
            if (online?.player is not null && online.character is not null)
                return new ProfileTarget(online.player, online.character, IsSelf: false);

            Player? player = Player.TryFromPlayerId(playerId);
            if (player is null)
                return null;

            // Read-only lookup (Character.FromUid would create a document for an unknown uid).
            Character? character = Character.collection.Find(doc => doc.Uid == playerId).FirstOrDefault();
            return new ProfileTarget(player, character, IsSelf: false);
        }

        /// <summary>
        /// AscNet has no persisted friend relation (no friend list store exists), so no viewer can satisfy
        /// a "visible to friends" setting; such sections are visible to their owner only.
        /// </summary>
        private static bool IsFriend(Session viewer, ProfileTarget target) => false;

        /// <summary>
        /// Mirrors the client's HasPermission checks (XUiPanelCharacterList / XUiPanelFashionPlayerInfo /
        /// XUiPanelCollectionScrollPlayerInfo / XDormManager.HasDormPermission): the owner always sees
        /// everything; others need ToAll, or ToFriend plus friendship. Missing settings fall back to
        /// ToSelf exactly like the client panels do.
        /// </summary>
        internal static bool CanViewProfileSection(Session viewer, ProfileTarget target, Func<AppearanceSettingInfo, long> section)
        {
            if (target.IsSelf)
                return true;
            AppearanceSettingInfo? setting = target.Player.PlayerData.AppearanceSettingInfo;
            long showType = setting is null ? AppearanceShowToSelf : section(setting);
            return showType == AppearanceShowToAll
                || (showType == AppearanceShowToFriend && IsFriend(viewer, target));
        }

        internal static PlayerDetail BuildPlayerDetail(Session viewer, ProfileTarget target, long nowUnixSeconds)
        {
            Player player = target.Player;
            PlayerData data = player.PlayerData;
            HashSet<int> achievementTaskIds = AchievementTaskIds.Value;

            return new PlayerDetail
            {
                Id = data.Id,
                Name = data.Name ?? string.Empty,
                Level = data.Level,
                Sign = data.Sign ?? string.Empty,
                Likes = data.Likes,
                CurrHeadPortraitId = data.CurrHeadPortraitId,
                CurrHeadFrameId = data.CurrHeadFrameId,
                CurrentWearNameplate = target.Character?.GetCurrentWearNameplate(nowUnixSeconds) ?? 0,
                Birthday = data.Birthday,
                GatherIds = (player.GatherRewards ?? []).Distinct().ToList(),
                // XMedalManager.CheckMedalIsExpired: KeepTime > 0 expires at Time + KeepTime.
                MedalInfos = (player.UnlockedMedals ?? [])
                    .Where(medal => medal.KeepTime <= 0 || medal.Time + medal.KeepTime >= nowUnixSeconds)
                    .Select(medal => new NotifyMedalData.NotifyMedalDataMedalInfo
                    {
                        Id = medal.Id,
                        Time = medal.Time,
                        KeepTime = medal.KeepTime
                    })
                    .ToList(),
                AchievementDetail = new PlayerDetailAchievement
                {
                    Achievement = (player.MissionProgress?.ClaimedTaskIds ?? [])
                        .Distinct()
                        .Count(achievementTaskIds.Contains)
                },
                AppearanceShowType = data.AppearanceShowType,
                AppearanceSettingInfo = data.AppearanceSettingInfo,
                DormDetail = CanViewProfileSection(viewer, target, setting => setting.DormitoryType)
                    ? BuildDormDetail(player)
                    : null,
                GuildDetail = BuildGuildDetail(data.Id),
                AssistCharacterDetail = player.AssistCharacterId != 0
                    && target.Character?.Characters?.Any(character => character.Id == (uint)player.AssistCharacterId) == true
                        ? new PlayerDetailAssistCharacter { Id = player.AssistCharacterId }
                        : null
            };
        }

        /// <summary>
        /// The shown dormitory is AppearanceSettingInfo.DormitoryId. When it is unset (0) or not owned,
        /// the owned room with the lowest Dormitory.InitNumber is used, which is the default the client
        /// settings panel adopts (XUiPanelSetting.InitDormitoryDrop).
        /// </summary>
        private static PlayerDetailDorm? BuildDormDetail(Player player)
        {
            List<PlayerDormRoom> rooms = player.Dorm?.Rooms ?? [];
            if (rooms.Count == 0)
                return null;

            long selectedId = player.PlayerData.AppearanceSettingInfo?.DormitoryId ?? 0;
            PlayerDormRoom? room = rooms.FirstOrDefault(candidate => candidate.Id == selectedId);
            if (room is null)
            {
                Dictionary<int, int> initNumbers = TableReaderV2.Parse<AscNet.Table.V2.share.dormitory.DormitoryTable>()
                    .GroupBy(row => row.Id)
                    .ToDictionary(group => group.Key, group => group.First().InitNumber);
                room = rooms
                    .OrderBy(candidate => initNumbers.TryGetValue((int)candidate.Id, out int number) ? number : int.MaxValue)
                    .ThenBy(candidate => candidate.Id)
                    .First();
            }

            return new PlayerDetailDorm { DormitoryId = room.Id, DormitoryName = room.Name ?? string.Empty };
        }

        private static PlayerDetailGuild? BuildGuildDetail(long playerId)
        {
            Guild? guild = GuildModule.FindMembership(playerId);
            return guild is null
                ? null
                : new PlayerDetailGuild { GuildId = guild.Id, GuildName = guild.Name ?? string.Empty, GuildRankLevel = GuildModule.Rank(guild, playerId) };
        }

        [RequestPacketHandler("QueryPlayerDetailRequest")]
        public static void QueryPlayerDetailRequestHandler(Session session, Packet.Request packet)
        {
            QueryPlayerDetailRequest request = packet.Deserialize<QueryPlayerDetailRequest>();
            ProfileTarget? target = ResolveProfileTarget(session, request.PlayerId);
            if (target is null)
            {
                session.SendResponse(new QueryPlayerDetailResponse { Code = PersonalInfoQueryPlayerDataFailCode }, packet.Id);
                return;
            }

            session.SendResponse(new QueryPlayerDetailResponse
            {
                Detail = BuildPlayerDetail(session, target, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            }, packet.Id);
        }

        /// <summary>
        /// Shared resolution + permission gate for the four section queries. Returns 0 on success.
        /// </summary>
        private static int ResolveProfileSection(Session session, long playerId, Func<AppearanceSettingInfo, long> section, out ProfileTarget? target)
        {
            target = ResolveProfileTarget(session, playerId);
            if (target is null)
                return PersonalInfoQueryPlayerDataFailCode;
            return CanViewProfileSection(session, target, section) ? 0 : PersonalInfoWithoutPermissionCode;
        }

        internal static List<CharacterData> BuildCharacterShows(ProfileTarget target)
        {
            List<CharacterData> owned = target.Character?.Characters ?? [];
            if (target.Player.PlayerData.AppearanceShowType != CharactersAppearanceSelect)
                return owned.ToList();

            Dictionary<long, CharacterData> byId = owned
                .GroupBy(character => (long)character.Id)
                .ToDictionary(group => group.Key, group => group.First());
            return (target.Player.PlayerData.ShowCharacters ?? [])
                .Distinct()
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();
        }

        [RequestPacketHandler("QueryPlayerCharacterListRequest")]
        public static void QueryPlayerCharacterListRequestHandler(Session session, Packet.Request packet)
        {
            QueryPlayerProfileRequest request = packet.Deserialize<QueryPlayerProfileRequest>();
            int code = ResolveProfileSection(session, request.PlayerId, setting => setting.CharacterType, out ProfileTarget? target);
            if (code != 0 || target is null)
            {
                session.SendResponse(new QueryPlayerCharacterListResponse { Code = code }, packet.Id);
                return;
            }

            session.SendResponse(new QueryPlayerCharacterListResponse { CharacterShows = BuildCharacterShows(target) }, packet.Id);
        }

        [RequestPacketHandler("QueryPlayerFashionRequest")]
        public static void QueryPlayerFashionRequestHandler(Session session, Packet.Request packet)
        {
            QueryPlayerProfileRequest request = packet.Deserialize<QueryPlayerProfileRequest>();
            int code = ResolveProfileSection(session, request.PlayerId, setting => setting.FashionType, out ProfileTarget? target);
            if (code != 0 || target is null)
            {
                session.SendResponse(new QueryPlayerFashionResponse { Code = code }, packet.Id);
                return;
            }

            Character? character = target.Character;
            session.SendResponse(new QueryPlayerFashionResponse
            {
                Fashions = (character?.Fashions ?? []).Select(fashion => fashion.Id).Distinct().ToList(),
                FashionColors = (character?.FashionColors ?? new Dictionary<int, List<int>>())
                    .Where(entry => entry.Value is { Count: > 0 })
                    .ToDictionary(entry => entry.Key, entry => entry.Value.Distinct().ToList())
            }, packet.Id);
        }

        [RequestPacketHandler("QueryPlayerWeaponFashionRequest")]
        public static void QueryPlayerWeaponFashionRequestHandler(Session session, Packet.Request packet)
        {
            QueryPlayerProfileRequest request = packet.Deserialize<QueryPlayerProfileRequest>();
            int code = ResolveProfileSection(session, request.PlayerId, setting => setting.WeaponFashionType, out ProfileTarget? target);
            if (code != 0 || target is null)
            {
                session.SendResponse(new QueryPlayerWeaponFashionResponse { Code = code }, packet.Id);
                return;
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            session.SendResponse(new QueryPlayerWeaponFashionResponse
            {
                WeaponFashions = (target.Character?.WeaponFashions ?? [])
                    .Where(fashion => fashion.ExpireTime == 0 || fashion.ExpireTime > now)
                    .Select(fashion => fashion.Id)
                    .Distinct()
                    .ToList()
            }, packet.Id);
        }

        [RequestPacketHandler("QueryPlayerTitleRequest")]
        public static void QueryPlayerTitleRequestHandler(Session session, Packet.Request packet)
        {
            QueryPlayerProfileRequest request = packet.Deserialize<QueryPlayerProfileRequest>();
            int code = ResolveProfileSection(session, request.PlayerId, setting => setting.TitleType, out ProfileTarget? target);
            if (code != 0 || target is null)
            {
                session.SendResponse(new QueryPlayerTitleResponse { Code = code }, packet.Id);
                return;
            }

            // Walls stay empty: AscNet persists no collection-wall layouts (no wall store or handler exists),
            // and the client then shows its "CollectionWallEmpty" state.
            session.SendResponse(new QueryPlayerTitleResponse
            {
                Titles = (target.Character?.ScoreTitles ?? []).ToList()
            }, packet.Id);
        }

        [RequestPacketHandler("QueryCharacterInfoRequest")]
        public static void QueryCharacterInfoRequestHandler(Session session, Packet.Request packet)
        {
            QueryCharacterInfoRequest request = packet.Deserialize<QueryCharacterInfoRequest>();
            session.SendResponse(BuildCharacterInfo(session, request, DateTimeOffset.UtcNow.ToUnixTimeSeconds()), packet.Id);
        }

        internal static QueryCharacterInfoResponse BuildCharacterInfo(Session session, QueryCharacterInfoRequest request, long nowUnixSeconds)
        {
            ProfileTarget? target = ResolveProfileTarget(session, request.PlayerId);
            if (target is null)
                return new QueryCharacterInfoResponse { Code = PersonalInfoQueryPlayerDataFailCode };
            if (!CanViewProfileSection(session, target, setting => setting.CharacterType))
                return new QueryCharacterInfoResponse { Code = PersonalInfoWithoutPermissionCode };

            Character? owner = target.Character;
            CharacterData? character = owner?.Characters?.FirstOrDefault(candidate => candidate.Id == request.CharacterId);
            if (owner is null || character is null)
                return new QueryCharacterInfoResponse { Code = PersonalInfoNoCharacterInfoCode };

            // Others may only open characters the owner exposes in the current showcase mode.
            if (!target.IsSelf
                && target.Player.PlayerData.AppearanceShowType == CharactersAppearanceSelect
                && !(target.Player.PlayerData.ShowCharacters ?? []).Contains(request.CharacterId))
            {
                return new QueryCharacterInfoResponse { Code = PersonalInfoNoShowCharacterCode };
            }

            HashSet<int> assignChapterIds = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.assign.AssignChapterTable>()
                .Select(row => row.ChapterId)
                .ToHashSet();
            HashSet<int> awarenessChapterIds = TableReaderV2.Parse<AscNet.Table.V2.share.fuben.awareness.AwarenessChapterTable>()
                .Select(row => row.Id)
                .ToHashSet();
            int characterId = (int)character.Id;

            return new QueryCharacterInfoResponse
            {
                CharacterData = character,
                EquipData = (owner.Equips ?? []).Where(equip => equip.CharacterId == characterId).ToList(),
                PartnerData = (owner.Partners ?? []).FirstOrDefault(partner => partner.CharacterId == characterId),
                // Same selection the PreFight NpcData uses: the unexpired weapon coating applied to this character.
                WeaponFashionId = (owner.WeaponFashions ?? [])
                    .FirstOrDefault(fashion => (fashion.ExpireTime == 0 || fashion.ExpireTime > nowUnixSeconds)
                        && fashion.UseCharacterList.Contains(characterId))
                    ?.Id ?? 0,
                AssignChapterRecords = (target.Player.Assign?.Chapters ?? [])
                    .Where(record => assignChapterIds.Contains(record.ChapterId))
                    .OrderBy(record => record.ChapterId)
                    .Select(record => new QueryCharacterInfoAssignChapterRecord { ChapterId = record.ChapterId, CharacterId = record.CharacterId })
                    .ToList(),
                AwarenessSetPositions = (target.Player.Awareness?.Chapters ?? [])
                    .Where(record => record.CharacterId != 0 && awarenessChapterIds.Contains(record.ChapterId))
                    .Select(record => record.ChapterId)
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList()
            };
        }

        /// <summary>
        /// Reads a Lua integer array. The client encodes a dense table as a msgpack array; an empty or
        /// sparse table may arrive as a map keyed by Lua index, whose values are taken in index order.
        /// </summary>
        internal static bool TryReadLuaIntegerArray(object? value, out List<long> result)
        {
            result = new();
            IEnumerable<object?>? items = value switch
            {
                null => Array.Empty<object?>(),
                object?[] array => array,
                IDictionary<object, object> map => map
                    .Select(entry => (Key: TryToInt64(entry.Key, out long key) ? key : long.MaxValue, entry.Value))
                    .OrderBy(entry => entry.Key)
                    .Select(entry => (object?)entry.Value),
                _ => null
            };
            if (items is null)
                return false;

            foreach (object? item in items)
            {
                if (!TryToInt64(item, out long number))
                    return false;
                result.Add(number);
            }
            return true;
        }

        private static bool TryToInt64(object? value, out long number)
        {
            switch (value)
            {
                case sbyte or byte or short or ushort or int or uint or long:
                    number = Convert.ToInt64(value);
                    return true;
                case ulong unsigned when unsigned <= long.MaxValue:
                    number = (long)unsigned;
                    return true;
                default:
                    number = 0;
                    return false;
            }
        }

        private static bool IsAppearanceShowType(long value) => value is AppearanceShowToAll or AppearanceShowToFriend or AppearanceShowToSelf;

        /// <summary>
        /// Validates a SetAppearanceRequest against the caller's persisted state. Returns 0 when valid.
        /// Validation order is AscNet policy; every code is a retail CodeText key.
        /// </summary>
        internal static int ValidateAppearance(Session session, SetAppearanceRequest request, out List<long> characterIds)
        {
            characterIds = new();
            if (request.CharactersAppearanceType is not (CharactersAppearanceAll or CharactersAppearanceSelect))
                return ParamsErrorCode;

            AppearanceSettingInfo? setting = request.AppearanceSettingInfo;
            if (setting is null
                || !IsAppearanceShowType(setting.TitleType)
                || !IsAppearanceShowType(setting.CharacterType)
                || !IsAppearanceShowType(setting.FashionType)
                || !IsAppearanceShowType(setting.WeaponFashionType)
                || !IsAppearanceShowType(setting.DormitoryType))
            {
                return ParamsErrorCode;
            }

            // DormitoryId is 0 until the dorm system is unlocked (XUiPanelSetting.InitDormitoryDrop); otherwise
            // it must name an owned room.
            if (setting.DormitoryId != 0 && !(session.player.Dorm?.Rooms ?? []).Any(room => room.Id == setting.DormitoryId))
                return ParamsErrorCode;

            if (!TryReadLuaIntegerArray(request.Characters, out List<long> ids)
                || ids.Count > MaxShowCharacters
                || ids.Any(id => id <= 0)
                || ids.Distinct().Count() != ids.Count)
            {
                return ParamsErrorCode;
            }

            HashSet<long> owned = (session.character.Characters ?? []).Select(character => (long)character.Id).ToHashSet();
            if (ids.Any(id => !owned.Contains(id)))
                return CharacterNotFoundCode;

            characterIds = ids;
            return 0;
        }

        [RequestPacketHandler("SetAppearanceRequest")]
        public static void SetAppearanceRequestHandler(Session session, Packet.Request packet)
        {
            SetAppearanceRequest request = packet.Deserialize<SetAppearanceRequest>();
            int code = ValidateAppearance(session, request, out List<long> characterIds);
            if (code != 0)
            {
                session.SendResponse(new SetAppearanceResponse { Code = code }, packet.Id);
                return;
            }

            AppearanceSettingInfo setting = request.AppearanceSettingInfo!;
            PlayerData data = session.player.PlayerData;
            data.AppearanceShowType = request.CharactersAppearanceType;
            data.ShowCharacters = characterIds;
            data.AppearanceSettingInfo = new AppearanceSettingInfo
            {
                TitleType = setting.TitleType,
                CharacterType = setting.CharacterType,
                FashionType = setting.FashionType,
                WeaponFashionType = setting.WeaponFashionType,
                DormitoryType = setting.DormitoryType,
                DormitoryId = setting.DormitoryId
            };
            session.player.Save();

            session.SendResponse(new SetAppearanceResponse(), packet.Id);
        }

        #endregion

        [RequestPacketHandler("RecordPlayerPointRequest")]
        public static void RecordPlayerPointRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<RecordPlayerPointRequest>();
            session.SendResponse(new RecordPlayerPointResponse(), packet.Id);
        }

        [RequestPacketHandler("SyncPlayerKeyPadSettingRequest")]
        public static void SyncPlayerKeyPadSettingRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<SyncPlayerKeyPadSettingRequest>();
            session.SendResponse(new SyncPlayerKeyPadSettingResponse(), packet.Id);
        }

        [RequestPacketHandler("RecordPlayerKeyPadSettingRequest")]
        public static void RecordPlayerKeyPadSettingRequestHandler(Session session, Packet.Request packet)
        {
            _ = packet.Deserialize<RecordPlayerKeyPadSettingRequest>();
            session.SendResponse(new RecordPlayerKeyPadSettingResponse(), packet.Id);
        }
    }
}

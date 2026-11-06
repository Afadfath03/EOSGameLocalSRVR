using AscNet.Common.Database;
using AscNet.Common.MsgPack;
using AscNet.Common.Util;
using AscNet.GameServer.Handlers;
using AscNet.Table.V2.share.draw;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using System.Reflection;

namespace AscNet.Test;

internal partial class Program
{
    private sealed class DrawFixedRandom(double value, bool upper = false) : Random
    {
        public override double NextDouble() => value;
        public override int Next(int maxValue) => upper ? maxValue - 1 : 0;
        public override int Next(int minValue, int maxValue) => upper ? maxValue - 1 : minValue;
    }

    // Queued deterministic rolls; exhaustions fall back to a non-rare tail so a
    // scripted sequence only needs to list the draws it cares about.
    private sealed class DrawSequenceRandom : Random
    {
        private readonly Queue<double> values;
        private readonly int index;

        public DrawSequenceRandom(IEnumerable<double> values, int index = 0)
        {
            this.values = new(values);
            this.index = index;
        }

        public override double NextDouble() => values.Count > 0 ? values.Dequeue() : 1d;
        public override int Next(int maxValue) => Math.Min(index, maxValue - 1);
        public override int Next(int minValue, int maxValue) => Math.Min(minValue + index, maxValue - 1);
    }

    private static bool ThrowsInvalidData(Func<object?> action)
    {
        try
        {
            action();
            return false;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
        {
            return true;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    private static void ValidateDrawRules()
    {
        Type manager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
        object? Call(string name, params object?[] args) => manager.GetMethod(name,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, args);
        DrawInfo Template(int group) => Version47CatalogTemplates().First(x => x.GroupId == group);
        RewardGoods Roll(Player p, DrawInfo d, Random r) => (RewardGoods)Call("RollDraw", p, d, r)!;
        Dictionary<int, int> ranks = TableReaderV2.Parse<AscNet.Table.V2.share.character.quality.CharacterQualityTable>()
            .GroupBy(row => row.CharacterId).ToDictionary(group => group.Key, group => group.Min(row => row.Quality));
        int CharacterMinQuality(int id) => ranks.GetValueOrDefault(id);
        DrawInfo fate = Template(15), theme = Template(11), weapon = Template(4), member = Template(1), cub = Template(22);
        foreach (var (d, rate) in new[] { (fate, .015), (theme, .005), (weapon, .05), (member, .005), (cub, .0582), (Template(16), .05) })
            AssertEqual(true, Math.Abs((double)Call("RareProbability", d)! - rate) < 1e-10, $"client probability group {d.GroupId}");

        // No authoritative Fate threshold weights exist: every path that would need
        // the missing law fails closed instead of running a substitute distribution.
        AssertEqual(true, ThrowsInvalidData(() => Call("GetPityRound", new Player(), fate, new DrawFixedRandom(0))),
            "Fate pity round fails closed without authoritative threshold law");
        AssertEqual(true, ThrowsInvalidData(() => Roll(new Player(), fate, new DrawFixedRandom(0))),
            "Fate roll fails closed without authoritative threshold law");
        AssertEqual(true, ThrowsInvalidData(() => Call("OverallRareProbability", fate)),
            "Fate combined rate requires the missing threshold law");
        AssertEqual(0, ((List<RewardGoods>)Call("DrawDraw", new Player(), fate.Id, 0)!).Count,
            "Fate live draw returns no rewards");
        AssertEqual(true, Call("GetDrawInfoById", fate.Id, new Player()) is null,
            "Fate draw info is not served");
        AssertEqual(0, ((List<DrawInfo>)Call("GetDrawInfosByGroup", 15, new Player())!).Count,
            "Fate group serves no draw infos");
        AssertEqual(false, ((List<DrawGroupInfo>)Call("GetDrawGroupInfos", new Player())!).Any(x => x.Id == 15),
            "Fate group is not advertised");
        AssertEqual(true, (double)Call("OverallRareProbability", theme)! > .005,
            "fixed-pity combined rate still derives");

        // The availability gate must also fail closed when a group has no
        // DrawServerRule row at all; a missing rule is not a lawful fixed pity.
        int rulelessGroup = Enumerable.Range(0, int.MaxValue).First(id =>
            !TableReaderV2.Parse<DrawServerRuleTable>().Any(rule => rule.GroupId == id));
        AssertEqual(false, (bool)Call("HasAvailablePityLaw", new DrawInfo { GroupId = rulelessGroup })!,
            "draw without a server rule fails closed");
        AssertEqual(false, (bool)Call("HasAvailablePityLaw", fate)!,
            "variable-pity Fate draw fails closed");
        AssertEqual(true, (bool)Call("HasAvailablePityLaw", theme)!,
            "fixed-pity draw keeps an available law");

        // The live DrawDraw path runs group-1 member draws through the shared pity
        // engine: first S on pull 40, then every 60, A-or-better every 10.
        Player p = new();
        PlayerDrawPityRound memberRound = (PlayerDrawPityRound)Call("GetPityRound", p, member, new DrawFixedRandom(0))!;
        AssertEqual(40, memberRound.Limit, "member first guarantee limit");
        memberRound.Misses = memberRound.Limit - 1;
        RewardGoods memberS = ((List<RewardGoods>)Call("DrawDraw", p, member.Id, 0)!).Single();
        AssertEqual((int)RewardType.Character, memberS.RewardType, "member first pity returns a character");
        AssertEqual(3, CharacterMinQuality(memberS.TemplateId), "member first pity returns an S");
        AssertEqual(60, memberRound.Limit, "member guarantee switches to 60 after first S");
        AssertEqual(0, memberRound.Misses, "member S resets the round");
        memberRound.Misses = 59;
        memberS = ((List<RewardGoods>)Call("DrawDraw", p, member.Id, 0)!).Single();
        AssertEqual(3, CharacterMinQuality(memberS.TemplateId), "member recurring pity returns an S at 60");
        AssertEqual(60, memberRound.Limit, "member guarantee remains 60");
        memberRound.Misses = 0;
        memberRound.LowerMisses = 9;
        RewardGoods memberLower = ((List<RewardGoods>)Call("DrawDraw", p, member.Id, 0)!).Single();
        AssertEqual(true, CharacterMinQuality(memberLower.TemplateId) >= 2, "member lower pity forces A-or-better");
        AssertEqual(0, memberRound.LowerMisses, "member lower pity resets");
        memberRound.Misses = 0;
        memberRound.LowerMisses = 8;
        RewardGoods memberOrdinary = Roll(p, member, new DrawFixedRandom(.999));
        AssertEqual(true, CharacterMinQuality(memberOrdinary.TemplateId) < 2, "member ordinary roll stays below A");
        AssertEqual(9, memberRound.LowerMisses, "member lower pity does not fire early");
        AssertEqual(1, memberRound.Misses, "member lower-rarity pull advances S pity");

        // Member history replay anchors migration: lifetime 119 keeps the visible
        // partial cycle, while a recorded S anchors both since-hit counters.
        p = new();
        p.DrawState.PityCountByGroup[1] = 119;
        memberRound = (PlayerDrawPityRound)Call("GetPityRound", p, member, new DrawFixedRandom(0))!;
        AssertEqual(59, memberRound.Misses, "member legacy progress keeps the partial cycle");
        AssertEqual(60, memberRound.Limit, "member legacy progress inherits recurring limit");
        AssertEqual(0, memberRound.LowerMisses, "member legacy progress starts lower pity at zero");

        foreach (DrawInfo d in new[] { weapon, Template(12) })
        {
            p = new();
            Roll(p, d, new DrawFixedRandom(.999));
            var round = p.DrawState.PityRounds[d.GroupId];
            round.Misses = round.Limit - 1;
            AssertEqual(false, Roll(p, d, new DrawFixedRandom(.999)).TemplateId == d.ResourceIds[1], "forced off-target");
            AssertEqual(true, round.GuaranteedTarget, "calibration recorded");
            p.DrawState = BsonSerializer.Deserialize<PlayerDrawState>(p.DrawState.ToBson());
            var changed = Version47CatalogTemplates().First(x => x.GroupId == d.GroupId && x.Id != d.Id);
            round = p.DrawState.PityRounds[d.GroupId];
            round.Misses = round.Limit - 1;
            AssertEqual(changed.ResourceIds[1], Roll(p, changed, new DrawFixedRandom(.999)).TemplateId, "calibration follows target switch and reload");
            AssertEqual(false, round.GuaranteedTarget, "calibration consumed");
        }

        foreach ((int drawId, bool permitsOffTarget) in new[] { (4001, true), (7002, true), (7069, false) })
        {
            DrawInfo banner = Version47CatalogTemplates().Single(x => x.Id == drawId);
            int[] rarePool = (int[])Call("RarePool", banner)!;
            p = new();
            var round = (PlayerDrawPityRound)Call("GetPityRound", p, banner, new DrawFixedRandom(0))!;
            round.Misses = round.Limit - 1;
            AssertEqual(banner.ResourceIds[1], Roll(p, banner, new DrawFixedRandom(0)).TemplateId,
                $"banner {drawId} deterministic target");
            p = new();
            round = (PlayerDrawPityRound)Call("GetPityRound", p, banner, new DrawFixedRandom(.999, true))!;
            round.Misses = round.Limit - 1;
            int upperResult = Roll(p, banner, new DrawFixedRandom(.999, true)).TemplateId;
            AssertEqual(permitsOffTarget, upperResult != banner.ResourceIds[1],
                $"banner {drawId} per-banner target rate");
            AssertEqual(true, rarePool.Contains(upperResult), $"banner {drawId} result remains in rare pool");
        }

        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _))
        {
            DrawGroupInfo active = ((List<DrawGroupInfo>)Call("GetDrawGroupInfos", new Player())!).First(x => x.Id != 1);
            long uid = 47_190;
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid),
                CreateDrawCompatibilityInventory(uid, []), "draw-pity-initialization");
            // A failed initialization save must not leave the in-memory round looking
            // persisted; the same session retries the write on the next request.
            playerSaves.ThrowOnReplaceOne = true;
            bool saveFailed = false;
            try
            {
                InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, 47_189,
                    new DrawGetDrawInfoListRequest { GroupId = active.Id });
            }
            catch (InvalidDataException exception) when (exception.GetBaseException() is MongoDB.Driver.MongoException)
            {
                saveFailed = true;
            }
            finally
            {
                playerSaves.ThrowOnReplaceOne = false;
            }
            AssertEqual(true, saveFailed, "failed pity initialization save surfaces");
            AssertEqual(1, playerSaves.ReplaceOneCalls, "failed save still attempted persistence");
            AssertEqual(true, harness.Session.player.DrawState.HasUnsavedPityRounds,
                "failed pity initialization stays marked unsaved");

            // An acknowledged no-match wrote nothing durable: the unchecked Save
            // must keep the round marked unsaved so the retry still persists it.
            playerSaves.ReplaceOneMatchedCount = 0;
            harness.Session.player.Save();
            playerSaves.ReplaceOneMatchedCount = 1;
            AssertEqual(true, harness.Session.player.DrawState.HasUnsavedPityRounds,
                "no-match save does not clear unsaved pity state");

            const int firstPacket = 47_191;
            InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, firstPacket,
                new DrawGetDrawInfoListRequest { GroupId = active.Id });
            DrawGetDrawInfoListResponse first = ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, firstPacket,
                nameof(DrawGetDrawInfoListResponse), "initial durable draw pity");
            Player reloaded = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("Draw info read did not persist newly sampled pity."));
            int firstLimit = first.DrawInfoList.First().MaxBottomTimes;
            PlayerDrawPityRound persisted = reloaded.DrawState.PityRounds[active.Id];
            AssertEqual(firstLimit, persisted.Limit, "catalog response persisted sampled pity");
            AssertEqual(0, persisted.Misses, "persisted pity round starts with no misses");
            AssertEqual(false, persisted.HasObtainedRare, "persisted pity round starts unmet");
            AssertEqual(false, reloaded.DrawState.HasUnsavedPityRounds, "pity dirty flag is not persisted");
            int savedCount = playerSaves.ReplaceOneCalls;
            harness.Session.player = reloaded;
            const int secondPacket = 47_192;
            InvokeRegisteredRequestHandler(nameof(DrawGetDrawInfoListRequest), harness.Session, secondPacket,
                new DrawGetDrawInfoListRequest { GroupId = active.Id });
            DrawGetDrawInfoListResponse second = ReadResponsePayload<DrawGetDrawInfoListResponse>(harness, secondPacket,
                nameof(DrawGetDrawInfoListResponse), "reloaded durable draw pity");
            AssertEqual(firstLimit, second.DrawInfoList.First().MaxBottomTimes, "reload preserves sampled pity");
            AssertEqual(savedCount, playerSaves.ReplaceOneCalls, "already-persisted pity needs no rewrite");

            // Live member draw through the request handler: the first S guarantee
            // lands on pull 40, the response advertises the post-first 60 limit, and
            // the Fate draw rejects at the catalog boundary.
            DrawInfo memberDraw = ((List<DrawInfo>)Call("GetDrawInfosByGroup", 1, new Player())!).First();
            harness.Session.player = CreateDrawCompatibilityPlayer(uid);
            harness.Session.inventory.Items.Add(new Item { Id = memberDraw.UseItemId, Count = memberDraw.UseItemCount * 2 });
            harness.Session.player.DrawState.PityRounds[1] = new PlayerDrawPityRound { Misses = 39, Limit = 40 };
            const int memberPacket = 47_193;
            InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, memberPacket,
                new DrawDrawCardRequest { DrawId = memberDraw.Id, Count = 1 });
            DrawDrawCardResponse memberResponse = ReadResponsePayload<DrawDrawCardResponse>(harness, memberPacket,
                nameof(DrawDrawCardResponse), "member handler first guarantee", maxPacketsToRead: 20);
            AssertEqual(0, memberResponse.Code, "member handler draw succeeds");
            RewardGoods memberGranted = memberResponse.RewardGoodsList.Single();
            AssertEqual(3, CharacterMinQuality(memberGranted.ConvertFrom > 0 ? memberGranted.ConvertFrom : memberGranted.TemplateId),
                "member handler first guarantee grants S");
            AssertEqual(60, memberResponse.ClientDrawInfo!.MaxBottomTimes, "member handler advertises recurring limit after first S");
            AssertEqual(60, memberResponse.ClientDrawInfo!.BottomTimes, "member handler resets remaining pity");

            const int fatePacket = 47_194;
            InvokeRegisteredRequestHandler(nameof(DrawDrawCardRequest), harness.Session, fatePacket,
                new DrawDrawCardRequest { DrawId = fate.Id, Count = 1 });
            DrawDrawCardResponse fateResponse = ReadResponsePayload<DrawDrawCardResponse>(harness, fatePacket,
                nameof(DrawDrawCardResponse), "fate handler rejection");
            AssertEqual(1, fateResponse.Code, "Fate draw request fails closed at the catalog boundary");
        }

        // All currently advertised pools can produce both rare and non-rare outcomes.
        var groups = (List<DrawGroupInfo>)Call("GetDrawGroupInfos", new Player())!;
        foreach (var group in groups.Where(x => x.Id != 1))
        {
            var draws = (List<DrawInfo>)Call("GetDrawInfosByGroup", group.Id, new Player())!;
            foreach (var d in draws)
            {
                Roll(new Player(), d, new DrawFixedRandom(0));
                for (int i = 0; i < 20; i++) Roll(new Player(), d, new DrawFixedRandom((i + .5) / 20));
            }
        }
        ValidateDrawRareRateOverride();
        Console.WriteLine("Draw rules passed; Fate pools fail closed without an authoritative threshold law.");
    }

    // GM /gacharate override (AscNet local policy): unset keeps table rolls; set, it only
    // replaces the base S roll while non-S proportions and pity stay table-driven.
    private static void ValidateDrawRareRateOverride()
    {
        Type manager = RequiredAscNetGameServerType("AscNet.GameServer.Game.DrawManager");
        object? Call(string name, params object?[] args) => manager.GetMethod(name,
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, args);
        DrawInfo Template(int group) => Version47CatalogTemplates().First(x => x.GroupId == group);
        RewardGoods roll(Player p, DrawInfo d, Random r) => (RewardGoods)Call("RollDraw", p, d, r)!;
        Dictionary<int, int> ranks = TableReaderV2.Parse<AscNet.Table.V2.share.character.quality.CharacterQualityTable>()
            .GroupBy(row => row.CharacterId).ToDictionary(group => group.Key, group => group.Min(row => row.Quality));
        int characterQuality(int id) => ranks.GetValueOrDefault(id);
        DrawInfo fate = Template(15), theme = Template(11), weapon = Template(4), member = Template(1), cub = Template(22);
        bool IsS(DrawInfo d, RewardGoods reward) => d.GroupId == 1
            ? reward.RewardType == (int)RewardType.Character && characterQuality(reward.TemplateId) == 3
            : ((int[])Call("RarePool", d)!).Contains(reward.TemplateId);
        Player WithOverride(double? all, params (int DrawId, double Percent)[] banners)
        {
            Player p = new();
            p.DrawState.RareRateOverride = new() { AllPercent = all };
            foreach ((int drawId, double percent) in banners) p.DrawState.RareRateOverride.ByDrawId[drawId] = percent;
            return p;
        }
        DrawInfo[] covered = [theme, weapon, member, cub, fate];

        foreach (DrawInfo d in covered)
        {
            Player unset = new();
            AssertEqual(true, Call("GetRareRateOverridePercent", unset, d.Id) is null, $"group {d.GroupId} override defaults off");
            AssertEqual((double)Call("RareProbability", d)!, (double)Call("EffectiveRareProbability", unset, d)!,
                $"group {d.GroupId} unset override keeps table rate");
            AssertEqual(false, IsS(d, roll(unset, d, new DrawFixedRandom(.999))), $"group {d.GroupId} unset high roll stays non-S");

            Player forced = WithOverride(100);
            for (int i = 0; i < 30; i++)
            {
                RewardGoods reward = roll(forced, d, new DrawFixedRandom((i + .5) / 30));
                AssertEqual(true, IsS(d, reward), $"group {d.GroupId} 100% override yields S on roll {i}");
                AssertEqual(0, forced.DrawState.PityRounds[Rule(d)].Misses, $"group {d.GroupId} forced S resets pity");
            }
        }
        int Rule(DrawInfo d) => TableReaderV2.Parse<DrawServerRuleTable>().Single(x => x.GroupId == d.GroupId).PityGroupId;

        // Specific percentage: theme base is 0.5%, so 5.4% only becomes S under a 5.5% override.
        Player custom = WithOverride(5.5);
        AssertEqual(true, Math.Abs((double)Call("EffectiveRareProbability", custom, theme)! - .055) < 1e-12, "5.5% override is a 0.055 per-pull rate");
        AssertEqual(true, IsS(theme, roll(custom, theme, new DrawFixedRandom(.054))), "roll under 5.5% override is S");
        AssertEqual(false, IsS(theme, roll(new Player(), theme, new DrawFixedRandom(.054))), "same roll under table rate is non-S");
        AssertEqual(false, IsS(theme, roll(custom, theme, new DrawFixedRandom(.056))), "roll above 5.5% override is non-S");

        // Non-S outcomes use the same conditional table weights with or without an override.
        foreach (DrawInfo d in new[] { theme, weapon, cub })
            for (int i = 0; i < 20; i++)
            {
                double category = (i + .5) / 20;
                RewardGoods table = roll(new Player(), d, new DrawSequenceRandom([.5, category]));
                RewardGoods scaled = roll(WithOverride(5.5), d, new DrawSequenceRandom([.5, category]));
                AssertEqual(table.RewardType, scaled.RewardType, $"group {d.GroupId} non-S category {i} keeps table proportion");
                // Weapon/CUB-partner picks use the injected random; other item picks use Random.Shared.
                if (d == weapon && table.RewardType == (int)RewardType.Equip || table.RewardType == (int)RewardType.Partner)
                    AssertEqual(table.TemplateId, scaled.TemplateId, $"group {d.GroupId} non-S outcome {i} unchanged");
            }

        // 0% removes only the base roll; the hard pity guarantee still lands.
        Player none = WithOverride(0);
        AssertEqual(false, IsS(theme, roll(none, theme, new DrawFixedRandom(0))), "0% override skips the base S roll");
        PlayerDrawPityRound round = none.DrawState.PityRounds[Rule(theme)];
        round.Misses = round.Limit - 1;
        AssertEqual(true, IsS(theme, roll(none, theme, new DrawFixedRandom(0))), "0% override keeps hard pity");

        // A banner-specific value wins over the all-banner value.
        Player scoped = WithOverride(100, (theme.Id, 0));
        AssertEqual(0d, (double)Call("GetRareRateOverridePercent", scoped, theme.Id)!, "banner override precedence");
        AssertEqual(false, IsS(theme, roll(scoped, theme, new DrawFixedRandom(.5))), "banner 0% beats all-banner 100%");
        AssertEqual(true, IsS(weapon, roll(scoped, weapon, new DrawFixedRandom(.5))), "other banners keep all-banner 100%");

        // Persistence: survives BSON reload; an unset override writes no element.
        PlayerDrawState reloaded = BsonSerializer.Deserialize<PlayerDrawState>(scoped.DrawState.ToBson());
        AssertEqual(100d, reloaded.RareRateOverride!.AllPercent!.Value, "override all-banner percent persists");
        AssertEqual(0d, reloaded.RareRateOverride.ByDrawId[theme.Id], "override banner percent persists");
        AssertEqual(false, new PlayerDrawState().ToBsonDocument().Contains("gm_rare_rate_override"), "unset override is not persisted");

        // Fate (local fixed-100 pity policy) is reached through the same roll; unknown draws are not.
        AssertEqual(true, Math.Abs((double)Call("GetTableRareRate", fate.Id)! - .015) < 1e-10, "Fate base rate reported");
        AssertEqual(true, Call("GetTableRareRate", int.MaxValue) is null, "unknown draw is not an override target");
        AssertEqual(true, Math.Abs((double)Call("GetTableRareRate", theme.Id)! - .005) < 1e-10, "table rate reported for covered banner");

        // /gacharate command: set, scope, status, persist, reset, and reject bad input.
        using (MongoCollectionOverride mongo = MongoCollectionOverride.InstallForDailySignInCompatibility(out var playerSaves, out _, out _))
        {
            AscNet.GameServer.Commands.CommandFactory.commands.Clear();
            AscNet.GameServer.Commands.CommandFactory.LoadCommands();
            const long uid = 47_290;
            using LoopbackSessionHarness harness = new(CreateDrawCompatibilityCharacter(uid), CreateDrawCompatibilityPlayer(uid),
                CreateDrawCompatibilityInventory(uid, []), "gacharate-command");
            string Run(params string[] args)
            {
                AscNet.GameServer.Commands.Command command = AscNet.GameServer.Commands.CommandFactory.CreateCommand(
                    "gacharate", harness.Session, args) ?? throw new InvalidDataException("gacharate command is not registered");
                try { command.Execute(); }
                catch (AscNet.GameServer.Commands.CommandMessageCallbackException exception) { return exception.Message; }
                throw new InvalidDataException("gacharate command returned no reply");
            }
            Player player() => harness.Session.player;
            AssertEqual(true, Run("status").Contains("off"), "status reports off by default");
            AssertEqual(true, Run("s", "100").Contains("100% on all banners"), "set 100% reply");
            AssertEqual(100d, player().DrawState.RareRateOverride!.AllPercent!.Value, "set 100% stored");
            AssertEqual(true, Run("S", "5.5", theme.Id.ToString()).Contains($"5.5% on draw {theme.Id}"), "banner 5.5% reply");
            Player commandSaved = BsonSerializer.Deserialize<Player>(playerSaves.LastSuccessfulReplacementBson
                ?? throw new InvalidDataException("gacharate did not persist"));
            AssertEqual(5.5, commandSaved.DrawState.RareRateOverride!.ByDrawId[theme.Id], "banner override persisted");
            AssertEqual(100d, commandSaved.DrawState.RareRateOverride.AllPercent!.Value, "all-banner override persisted");
            string status = Run("s", "status");
            AssertEqual(true, status.Contains("all banners 100%") && status.Contains($"draw {theme.Id} 5.5% (table 0.5%)"), "status lists overrides");
            AssertEqual(true, Run("s", "101").Contains("between 0 and 100"), "out-of-range percent rejected");
            AssertEqual(true, Run("s", "50", int.MaxValue.ToString()).Contains("not a supported banner"), "unknown banner rejected");
            AssertEqual(100d, player().DrawState.RareRateOverride!.AllPercent!.Value, "rejected input keeps previous override");
            Run("s", "reset", theme.Id.ToString());
            AssertEqual(false, player().DrawState.RareRateOverride!.ByDrawId.ContainsKey(theme.Id), "banner reset removes only that banner");
            AssertEqual(100d, player().DrawState.RareRateOverride!.AllPercent!.Value, "banner reset keeps all-banner override");
            Run("s", "reset");
            AssertEqual(true, player().DrawState.RareRateOverride is null, "reset clears every override");
            AssertEqual(false, BsonSerializer.Deserialize<BsonDocument>(playerSaves.LastSuccessfulReplacementBson!)["draw_state"]
                .AsBsonDocument.Contains("gm_rare_rate_override"), "reset leaves no persisted override");
            AssertEqual(true, ThrowsArgument(() => AscNet.GameServer.Commands.CommandFactory.CreateCommand("gacharate", harness.Session, ["s", "abc"])),
                "non-numeric percent rejected by argument validation");
        }
        Console.WriteLine("Draw S-rank rate override passed.");
    }

    private static bool ThrowsArgument(Func<object?> action)
    {
        try { action(); return false; }
        catch (TargetInvocationException exception) when (exception.InnerException is ArgumentException) { return true; }
        catch (ArgumentException) { return true; }
    }
}

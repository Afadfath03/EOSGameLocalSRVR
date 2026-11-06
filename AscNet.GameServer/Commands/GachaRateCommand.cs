using System.Globalization;
using System.Text;
using AscNet.Common.Database;
using AscNet.GameServer.Game;

namespace AscNet.GameServer.Commands
{
    // GM/debug override of the per-pull S-rank (top-rarity) rate. AscNet local policy:
    // explicit per-player opt-in, persisted on the player, default off (table rates).
    [CommandName("gacharate")]
    internal sealed class GachaRateCommand : Command
    {
        public GachaRateCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Override your per-pull S-rank (6-star weapon / S CUB) draw rate; other outcomes keep their table proportions. "
            + "/gacharate s <percent 0-100> [drawId|all], /gacharate s reset [drawId], /gacharate status. "
            + "Hard pity still applies; Fate banners stay unavailable.";

        [Argument(0, @"^(s|status)$", "s (S-rank rate) or status", ArgumentFlags.IgnoreCase)]
        string Rank { get; set; } = string.Empty;

        [Argument(1, @"^(status|reset|[0-9]{1,3}(\.[0-9]{1,4})?)$", "Percent 0-100 (e.g. 100 or 5.5), reset, or status", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        string Value { get; set; } = string.Empty;

        [Argument(2, @"^(all|[0-9]+)$", "Draw ID to scope the override to one banner, or all (default)", ArgumentFlags.Optional | ArgumentFlags.IgnoreCase)]
        string Banner { get; set; } = string.Empty;

        public override void Execute()
        {
            if (Rank.Equals("status", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(Value)
                || Value.Equals("status", StringComparison.OrdinalIgnoreCase))
                throw new CommandMessageCallbackException(Status());

            int drawId = 0;
            if (!string.IsNullOrEmpty(Banner) && !Banner.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                drawId = int.Parse(Banner, CultureInfo.InvariantCulture);
                if (DrawManager.GetTableRareRate(drawId) is null)
                    throw new CommandMessageCallbackException($"Draw {drawId} is not a supported banner (unknown, Fate, or without reward configuration).");
            }

            session.player.DrawState ??= new();
            PlayerDrawState state = session.player.DrawState;
            PlayerDrawRareRateOverride? previous = state.RareRateOverride is null ? null : new()
            {
                AllPercent = state.RareRateOverride.AllPercent,
                ByDrawId = new(state.RareRateOverride.ByDrawId)
            };

            string message;
            if (Value.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                if (drawId != 0)
                {
                    state.RareRateOverride?.ByDrawId.Remove(drawId);
                    message = $"S-rank rate override for draw {drawId} removed.";
                }
                else
                {
                    state.RareRateOverride = null;
                    message = "S-rank rate overrides cleared; all banners use their table rates.";
                }
                if (state.RareRateOverride?.IsEmpty == true) state.RareRateOverride = null;
            }
            else
            {
                double percent = double.Parse(Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                if (percent < 0 || percent > 100)
                    throw new CommandMessageCallbackException("S-rank rate must be between 0 and 100 percent.");
                state.RareRateOverride ??= new();
                if (drawId != 0) state.RareRateOverride.ByDrawId[drawId] = percent;
                else state.RareRateOverride.AllPercent = percent;
                string scope = drawId != 0 ? $"draw {drawId}" : "all banners";
                message = $"S-rank rate set to {Format(percent)}% on {scope}; non-S outcomes keep their table proportions "
                    + $"within the remaining {Format(100 - percent)}%. Hard pity still applies.";
            }

            try
            {
                session.player.SaveChecked();
            }
            catch
            {
                state.RareRateOverride = previous;
                throw new CommandMessageCallbackException("Failed to persist the S-rank rate override.");
            }
            throw new CommandMessageCallbackException(message);
        }

        private string Status()
        {
            PlayerDrawRareRateOverride? value = session.player.DrawState?.RareRateOverride;
            if (value is null || value.IsEmpty)
                return "S-rank rate override: off (all banners use their table rates).";
            StringBuilder status = new("S-rank rate override: ");
            status.Append(value.AllPercent is double all ? $"all banners {Format(all)}%" : "all banners table rate");
            foreach ((int drawId, double percent) in value.ByDrawId.OrderBy(x => x.Key))
            {
                double? table = DrawManager.GetTableRareRate(drawId);
                status.Append($"; draw {drawId} {Format(percent)}% (table {(table is double rate ? Format(rate * 100) + "%" : "n/a")})");
            }
            return status.Append('.').ToString();
        }

        private static string Format(double percent) => percent.ToString("0.####", CultureInfo.InvariantCulture);
    }
}

using AscNet.Common;
using AscNet.Common.MsgPack;
using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Table.V2.share.item;
using Newtonsoft.Json;

namespace AscNet.GameServer.Commands
{
    [CommandName("item")]
    internal class ItemCommand : Command
    {
        public ItemCommand(Session session, string[] args, bool validate = true) : base(session, args, validate) { }

        public override string Help => "Use /item add <id|name|all> <amount|max>, /item clear <id|name|all>, /item reset <id|name|all>, or /item find <name>. Names may contain spaces, e.g. /item add simulation score 500.";

        [Argument(0, @"^add$|^clear$|^reset$|^find$", "The operation selected (add, clear, reset, find)", ArgumentFlags.IgnoreCase)]
        string Op { get; set; } = string.Empty;

        [Argument(1, @"^\S+$", "The target item: item id, item name (may span several words), or 'all'", ArgumentFlags.Optional)]
        string Target { get; set; } = string.Empty;

        [Argument(2, @"^\S+$", "For add: the amount, a number or 'max' (limited to the game's max count); for names it is the last word", ArgumentFlags.Optional)]
        string Amount { get; set; } = string.Empty;

        public override void Execute()
        {
            switch (Op.ToLowerInvariant())
            {
                case "add":
                    AddItems();
                    break;
                case "clear":
                    ClearItems();
                    break;
                case "reset":
                    ResetItems();
                    break;
                case "find":
                    FindItems();
                    break;
                default:
                    throw new InvalidOperationException("Invalid operation!");
            }
        }

        private void AddItems()
        {
            NotifyItemDataList notifyItemData = new();

            if (Target == "all")
            {
                if (string.IsNullOrEmpty(Amount))
                    throw new ArgumentException("Specify a target amount: a number or 'max'.");

                bool useMaxCount = Amount.Equals("max", StringComparison.OrdinalIgnoreCase);
                int amount = useMaxCount ? 0 : Miscs.ParseIntOr(Amount);

                foreach (ItemTable itemTable in TableReaderV2.Parse<ItemTable>())
                {
                    Item? existingItem = session.inventory.Items.FirstOrDefault(x => x.Id == itemTable.Id);
                    long currentCount = existingItem?.Count ?? 0;
                    long targetCount = GetBatchAddMaxTargetCount(itemTable);
                    long grantAmount = useMaxCount
                        ? targetCount - currentCount
                        : Math.Min(amount, targetCount - currentCount);
                    notifyItemData.ItemDataList.Add(
                        session.inventory.Do(itemTable.Id, (int)Math.Clamp(grantAmount, int.MinValue, int.MaxValue)));
                }
            }
            else
            {
                if (args.Length < 3)
                    throw new CommandMessageCallbackException("Usage: /item add <id|name> <amount|max>");

                string amountText = args[^1];
                bool useMaxCount = amountText.Equals("max", StringComparison.OrdinalIgnoreCase);
                if (!useMaxCount && !int.TryParse(amountText, out _))
                    throw new CommandMessageCallbackException("Usage: /item add <id|name> <amount|max>. The last word must be the amount.");

                ItemTable item = ResolveItem(args[1..^1]);
                long current = session.inventory.Items.FirstOrDefault(x => x.Id == item.Id)?.Count ?? 0;
                int amount = useMaxCount
                    ? (int)Math.Clamp(Inventory.GetMaxCount(item) - current, 0, int.MaxValue)
                    : Miscs.ParseIntOr(amountText);

                Item updated = session.inventory.Do(item.Id, amount);
                notifyItemData.ItemDataList.Add(updated);
                session.inventory.Save();
                session.SendPush(notifyItemData);
                List<int> sameName = TableReaderV2.Parse<ItemTable>()
                    .Where(row => row.Id != item.Id && string.Equals(row.Name, item.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(row => row.Id)
                    .ToList();
                throw new CommandMessageCallbackException($"{item.Name} ({item.Id}): {current} -> {updated.Count}."
                    + (sameName.Count > 0 ? $" Other items share this name: {string.Join(", ", sameName)}; use the id to pick one." : string.Empty));
            }

            session.inventory.Save();
            session.SendPush(notifyItemData);
        }

        private void FindItems()
        {
            if (args.Length < 2)
                throw new CommandMessageCallbackException("Usage: /item find <name>");
            NameLookup.Result<ItemTable> result = NameLookup.Resolve(
                TableReaderV2.Parse<ItemTable>(), row => row.Id, row => [row.Name], args[1..]);
            List<NameLookup.Candidate<ItemTable>> found = result.Match is null
                ? result.Suggestions.ToList()
                : [result.Match, .. result.Suggestions];
            throw new CommandMessageCallbackException(found.Count == 0
                ? $"No item matches \"{string.Join(' ', args[1..])}\"."
                : "Items: " + string.Join(", ", found.Take(10).Select(candidate => $"{candidate.Name} ({candidate.Id})")));
        }

        // Resolves an item id or a (possibly multi-word) item name; ambiguous or unknown names list candidates.
        private static ItemTable ResolveItem(string[] tokens)
        {
            string query = string.Join(' ', tokens);
            if (tokens.Length == 0)
                throw new CommandMessageCallbackException("Specify an item id or name.");
            NameLookup.Result<ItemTable> result = NameLookup.Resolve(
                TableReaderV2.Parse<ItemTable>().Where(row => Inventory.IsValidClientItemId(row.Id)),
                row => row.Id,
                row => [row.Name],
                tokens);
            if (result.Match is null)
                throw new CommandMessageCallbackException(int.TryParse(query, out _)
                    ? $"Item id {query} does not exist."
                    : NameLookup.FormatSuggestions("item", query, result.Suggestions));
            return result.Match.Row;
        }

        private void ClearItems()
        {
            NotifyItemDataList notifyItemData = new();

            if (Target == "all")
            {
                foreach (Item item in Inventory.FilterClientItems(session.inventory.Items))
                {
                    item.Count = 0;
                    item.RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                    notifyItemData.ItemDataList.Add(item);
                }

                session.inventory.Items.RemoveAll(item => Inventory.IsValidClientItemId(item.Id));
            }
            else
            {
                if (string.IsNullOrEmpty(Target))
                    throw new ArgumentException("Specify an item ID, name, or 'all' to clear.");

                int itemId = ResolveItem(args[1..]).Id;

                Item clearedItem = session.inventory.Do(itemId, int.MinValue);
                notifyItemData.ItemDataList.Add(clearedItem);
                session.inventory.Items.RemoveAll(item => item.Id == itemId);
            }

            session.inventory.Save();
            session.SendPush(notifyItemData);
        }

        private void ResetItems()
        {
            List<ItemConfig> defaultItems =
                JsonConvert.DeserializeObject<List<ItemConfig>>(File.ReadAllText("./Configs/default_items.json")) ?? new();
            Dictionary<int, ItemTable> itemTables = TableReaderV2.Parse<ItemTable>().ToDictionary(x => x.Id);
            Dictionary<int, long> defaultItemMap = defaultItems.ToDictionary(
                x => x.Id,
                x => Math.Min(x.Count, Inventory.GetMaxCount(itemTables.GetValueOrDefault(x.Id))));
            NotifyItemDataList notifyItemData = new();

            if (Target == "all")
            {
                List<Item> clientItems = Inventory.FilterClientItems(session.inventory.Items);
                foreach (Item item in clientItems)
                {
                    item.Count = defaultItemMap.GetValueOrDefault(item.Id, 0);
                    item.RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                    notifyItemData.ItemDataList.Add(item);
                }

                session.inventory.Items.RemoveAll(item => Inventory.IsValidClientItemId(item.Id));

                foreach (ItemConfig defaultItem in defaultItems.Where(item => itemTables.ContainsKey(item.Id)))
                {
                    Item newItem = new()
                    {
                        Id = defaultItem.Id,
                        Count = defaultItemMap[defaultItem.Id],
                        RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                        CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds()
                    };
                    session.inventory.Items.Add(newItem);

                    if (!clientItems.Any(item => item.Id == defaultItem.Id))
                        notifyItemData.ItemDataList.Add(newItem);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(Target))
                    throw new ArgumentException("Specify an item ID, name, or 'all' to reset.");

                int itemId = ResolveItem(args[1..]).Id;

                long targetCount = defaultItemMap.GetValueOrDefault(itemId, 0);
                Item? existingItem = session.inventory.Items.FirstOrDefault(x => x.Id == itemId);
                if (existingItem is null)
                {
                    existingItem = new Item
                    {
                        Id = itemId,
                        Count = targetCount,
                        RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
                        CreateTime = DateTimeOffset.Now.ToUnixTimeSeconds()
                    };
                    session.inventory.Items.Add(existingItem);
                }
                else
                {
                    existingItem.Count = targetCount;
                    existingItem.RefreshTime = DateTimeOffset.Now.ToUnixTimeSeconds();
                }

                notifyItemData.ItemDataList.Add(existingItem);
            }

            session.inventory.Save();
            session.SendPush(notifyItemData);
        }

        private static long GetBatchAddMaxTargetCount(ItemTable itemTable)
        {
            // Gift and selection boxes are rendered as individual entries by the client.
            // Keep one copy during a bulk grant to avoid expanding the inventory excessively.
            if (itemTable.ItemType == (int)ItemType.Gift)
                return 1;

            return Math.Min(Inventory.GetMaxCount(itemTable), Inventory.GlobalItemMaxCount);
        }
    }
}

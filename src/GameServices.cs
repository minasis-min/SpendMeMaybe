using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using Lumina.Excel.Sheets;

namespace SpendMeMaybe;

public sealed class ItemCatalog(IDataManager data)
{
    private Dictionary<uint, ItemInfo>? items;
    private IReadOnlyList<ItemInfo>? tokens;
    public string Error { get; private set; } = "";
    public string TokenError { get; private set; } = "";
    public IReadOnlyCollection<ItemInfo> All { get { Ensure(); return items!.Values; } }
    public ItemInfo? Find(uint id) { Ensure(); return items!.GetValueOrDefault(id); }
    public IReadOnlyList<ItemInfo> CurrentTokens
    {
        get
        {
            Ensure();
            if (tokens != null) return tokens;
            try
            {
                var rows = data.GetExcelSheet<TomestonesItem>();
                var mapping = rows.Select(x => (Role: x.Tomestones.RowId, ItemId: x.Item.RowId)).ToArray();
                var active = CurrentTomestones.Resolve(mapping).ToHashSet();
                var retired = mapping.Where(x => x.ItemId != 0 && !active.Contains(x.ItemId)).Select(x => x.ItemId).ToHashSet();
                var tribal = data.GetExcelSheet<BeastTribe>().Select(x => x.CurrencyItem.RowId).Where(x => x != 0).ToHashSet();
                tokens = items!.Values.Where(x => (x.IsCurrency || active.Contains(x.Id) || tribal.Contains(x.Id)) && !retired.Contains(x.Id))
                    .Select(x => x with { Group = CurrencyGroups.Resolve(x.Id, active.Contains(x.Id), tribal.Contains(x.Id)) })
                    .OrderBy(x => x.Group).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
                if (tokens.Count == 0) TokenError = "通貨リストを取得できません。下の名前検索を利用してください。";
            }
            catch { tokens = []; TokenError = "通貨リストを取得できません。下の名前検索を利用してください。"; }
            return tokens;
        }
    }
    private void Ensure()
    {
        if (items != null) return;
        try
        {
            var sheet = data.GetExcelSheet<Item>(ClientLanguage.Japanese);
            items = new();
            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (row.RowId != 0 && !string.IsNullOrWhiteSpace(name)) items[row.RowId] = new(row.RowId, name, row.Icon, row.CanBeHq, row.IsCollectable,
                    CurrencyKinds.IsWalletCurrency(row.RowId, row.ItemUICategory.RowId, row.FilterGroup, row.StackSize));
            }
        }
        catch { items = new(); Error = "日本語のゲームデータを取得できません。再ロードしてください。"; }
        try
        {
            foreach (var row in data.GetExcelSheet<EventItem>(ClientLanguage.Japanese))
            {
                var info = ItemInfo.EventItem(row.RowId, row.Singular.ExtractText(), row.Icon);
                if (info != null) items[info.Id] = info;
            }
        }
        catch { Error = "「だいじなもの」のゲームデータを取得できません。再ロードしてください。"; }
    }
}
public static class InventoryReader
{
    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    public static unsafe CountReading Read(ItemInfo info)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return new(null, "インベントリ未取得");
            if (info.IsEventItem)
            {
                var keyItems = manager->GetInventoryContainer(InventoryType.KeyItems);
                if (keyItems == null || !keyItems->IsLoaded) return new(null, "だいじなものの読み込み待ち");
                if (keyItems->Size <= 0 || keyItems->Size > 1000) return new(null, "だいじなものの形式を確認できません");
                long count = 0;
                for (var index = 0; index < keyItems->Size; index++)
                {
                    var slot = keyItems->GetInventorySlot(index);
                    if (slot == null) return new(null, "だいじなものの取得が完了していません");
                    // EventItem ids are already full ids; do not normalize them as HQ item ids.
                    if (slot->ItemId == info.Id) count += slot->Quantity;
                }
                return new(count);
            }
            // Avoid treating a partially loaded character inventory as an empty inventory.
            foreach (var type in Bags)
            {
                var bag = manager->GetInventoryContainer(type);
                if (bag == null || !bag->IsLoaded) return new(null, "所持品の読み込み待ち");
                if (bag->Size <= 0 || bag->Size > 200) return new(null, "所持品の形式を確認できません");
            }
            // Collectable-capable rows use explicit slots: include ordinary NQ/HQ only,
            // independent of the native count function's collectability semantics.
            if (info.IsCollectable)
            {
                long total = 0;
                foreach (var type in Bags)
                {
                    var bag = manager->GetInventoryContainer(type);
                    if (bag == null || !bag->IsLoaded) return new(null, "所持品の読み込み待ち");
                    if (bag->Size <= 0 || bag->Size > 200) return new(null, "所持品の形式を確認できません");
                    for (var index = 0; index < bag->Size; index++)
                    {
                        var slot = bag->GetInventorySlot(index);
                        if (slot == null) return new(null, "所持品の取得が完了していません");
                        if ((slot->Flags & InventoryItem.ItemFlags.Collectable) == 0 && slot->GetBaseItemId() == info.Id)
                            total += slot->Quantity;
                    }
                }
                return new(total);
            }
            var nq = manager->GetInventoryItemCount(info.Id, false, false, false);
            // Currency counts are queried only once; they do not have an HQ variant.
            var hq = info.CanBeHq ? manager->GetInventoryItemCount(info.Id, true, false, false) : 0;
            return nq < 0 || hq < 0 ? new(null, "所持数を取得できません") : new((long)nq + hq);
        }
        catch { return new(null, "所持数の取得に失敗しました"); }
    }
}

// UI only submits intent. Execution is drained once on the framework thread, never from rendering/threshold updates.
internal sealed class SlashCommandAction(ICommandManager commands)
{
    private readonly CommandRouter router = new(commands.ProcessCommand, SendToGame);
    public void Execute(string command) => router.Execute(command);
    private static unsafe void SendToGame(string text)
    {
        var module = UIModule.Instance();
        if (module == null) throw new InvalidOperationException("ゲームのコマンド受付が準備できていません。");
        var message = Utf8String.FromString(text);
        try { module->ProcessChatBoxEntry(message); }
        finally { message->Dtor(true); }
    }
}


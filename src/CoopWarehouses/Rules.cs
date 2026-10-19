using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;

namespace CoopWarehouses;

/// <summary>
/// A player's game -> the authority. Item lines are <c>itemKey\tdelta</c>: positive deposits, negative withdraws.
/// Equipment lines are <c>heroId\tset\tslot\toldKey\tnewKey</c> (set 0 battle, 1 civilian; a key is empty for an
/// empty slot): equipping or unequipping done on the same stash screen.
/// </summary>
public sealed class WireRequest
{
    public const string Open = "open", Transfer = "transfer", Gold = "gold";

    public string Op = "";
    public string SettlementId = "";
    public int Scope;
    public List<string> Items = new List<string>();
    public List<string> Equipment = new List<string>();
    public int GoldDelta;
    public int Seq;
}

/// <summary>The authority -> the asking player. Item lines are <c>itemKey\tcount</c> (the warehouse after the request).</summary>
public sealed class WireReply
{
    public string Op = "";
    public string SettlementId = "";
    public int Scope;
    public bool Ok;
    public string Message = "";
    public List<string> Items = new List<string>();
    public long Gold;
    public List<string> Log = new List<string>();
    /// <summary>Designs of the crafted weapons among <see cref="Items"/>, so the player's game can build any it lacks.</summary>
    public List<string> Designs = new List<string>();
    public int Seq;
}

/// <summary>
/// The rules, run only where the warehouses live (single player or the co-op server). Every request is checked against
/// the warehouse and the player's party as they are now, and applied all or nothing.
/// </summary>
public static class Rules
{
    public static WarehouseStore Store = new WarehouseStore();

    public static bool IsWarehouseSettlement(Settlement? s) => s != null && (s.IsTown || s.IsCastle);

    /// <summary>The warehouse owner id for this hero, or why they may not use this scope here.</summary>
    public static bool TryOwner(Hero hero, Settlement settlement, Scope scope, out string ownerId, out string why)
    {
        ownerId = why = "";
        if (!IsWarehouseSettlement(settlement)) { why = "There is no warehouse here."; return false; }
        switch (scope)
        {
            case Scope.Public:
                return true;
            case Scope.Clan:
                if (hero.Clan == null) { why = "You have no clan."; return false; }
                ownerId = hero.Clan.StringId;
                return true;
            case Scope.Kingdom:
                var kingdom = hero.Clan?.Kingdom;
                if (kingdom == null) { why = "You are not in a kingdom."; return false; }
                if (settlement.MapFaction != kingdom) { why = "Only settlements of your kingdom hold its warehouse."; return false; }
                ownerId = kingdom.StringId;
                return true;
            default:
                why = "Unknown warehouse.";
                return false;
        }
    }

    public static WireReply Handle(Hero hero, MobileParty party, WireRequest req)
    {
        var reply = new WireReply { Op = req.Op, SettlementId = req.SettlementId, Scope = req.Scope, Seq = req.Seq };
        var settlement = Settlement.Find(req.SettlementId);
        if (settlement == null) return Fail(reply, "Unknown settlement.");
        if (party.CurrentSettlement != settlement) return Fail(reply, "You must be in " + settlement.Name + ".");
        var scope = (Scope)req.Scope;
        if (!TryOwner(hero, settlement, scope, out var owner, out var why)) return Fail(reply, why);
        var w = Store.Get(settlement.StringId, scope, owner);

        string? problem = req.Op switch
        {
            WireRequest.Open => null,
            WireRequest.Transfer => Transfer(hero, party, w, req.Items, req.Equipment),
            WireRequest.Gold => MoveGold(hero, w, req.GoldDelta),
            _ => "Unknown request.",
        };
        if (problem != null) Fail(reply, problem); else reply.Ok = true;
        Fill(reply, w);
        return reply;
    }

    private static WireReply Fail(WireReply reply, string why)
    {
        reply.Ok = false;
        reply.Message = why;
        return reply;
    }

    private static void Fill(WireReply reply, Warehouse w)
    {
        reply.Items = w.Items.Select(kv => kv.Key + "\t" + kv.Value).ToList();
        reply.Gold = w.Gold;
        reply.Log = w.Log.ToList();
        reply.Designs = Store.DesignsFor(w).ToList();
    }

    /// <summary>
    /// One stash screen's Done: warehouse deposits and withdrawals plus any equipping or unequipping done on the same
    /// screen (from the warehouse, the bag, or into either). Worked out against the party's bag in order: unequipped
    /// items go to the bag, then the warehouse moves, then equipped items leave the bag. Nothing moves unless all of it can.
    /// </summary>
    private static string? Transfer(Hero hero, MobileParty party, Warehouse w, List<string> lines, List<string> equipLines)
    {
        var moves = new List<(string Key, EquipmentElement Element, int Delta)>();
        foreach (var line in lines)
        {
            var p = line.Split('\t');
            if (p.Length != 2 || !int.TryParse(p[1], out var delta) || delta == 0) return "Malformed request.";
            if (!TryElement(p[0], out var element)) return UnknownItem(p[0]);
            if (!Storable(element.Item, out var why)) return why;
            if (moves.Any(m => m.Key == p[0])) return "Malformed request.";
            moves.Add((p[0], element, delta));
        }

        var swaps = new List<(Hero Hero, Equipment Set, EquipmentIndex Slot, EquipmentElement Old, EquipmentElement New)>();
        foreach (var line in equipLines)
        {
            var p = line.Split('\t');
            if (p.Length != 5 || !int.TryParse(p[1], out var set) || set is < 0 or > 1 || !int.TryParse(p[2], out var slot)
                || slot < 0 || slot >= (int)EquipmentIndex.NumEquipmentSetSlots) return "Malformed request.";
            var who = party.MemberRoster.GetTroopRoster().Select(t => t.Character?.HeroObject).FirstOrDefault(h => h?.StringId == p[0]);
            if (who == null || who.Clan != hero.Clan) return "Only heroes of your clan in your party can be equipped here.";
            var equipment = set == 0 ? who.BattleEquipment : who.CivilianEquipment;
            var index = (EquipmentIndex)slot;
            var current = equipment[index];
            if ((current.IsEmpty ? "" : ItemKey(current)) != p[3]) return who.Name + "'s equipment changed meanwhile; nothing was moved.";
            var next = default(EquipmentElement);
            if (p[4].Length > 0)
            {
                if (!TryElement(p[4], out next)) return UnknownItem(p[4]);
                if (!Equipment.IsItemFitsToSlot(index, next.Item)) return Name(next) + " does not fit that slot.";
            }
            if (swaps.Any(s => s.Hero == who && s.Set == equipment && s.Slot == index)) return "Malformed request.";
            swaps.Add((who, equipment, index, current, next));
        }

        // Check: the bag must never go below zero, and the warehouse must hold what is taken.
        var bag = new Dictionary<string, int>(StringComparer.Ordinal);
        int Bag(EquipmentElement e) { var k = ItemKey(e); return bag.TryGetValue(k, out var n) ? n : bag[k] = Have(party, e); }
        void Move(EquipmentElement e, int delta) => bag[ItemKey(e)] = Bag(e) + delta;
        foreach (var s in swaps) if (!s.Old.IsEmpty) Move(s.Old, +1);
        foreach (var m in moves)
        {
            if (m.Delta < 0 && w.Count(m.Key) < -m.Delta)
                return "The warehouse no longer has " + (-m.Delta) + " " + Name(m.Element) + ". Someone may have taken it.";
            if (m.Delta > 0 && Bag(m.Element) < m.Delta) return "You no longer have " + m.Delta + " " + Name(m.Element) + ".";
            Move(m.Element, -m.Delta);
        }
        foreach (var s in swaps)
        {
            if (s.New.IsEmpty) continue;
            if (Bag(s.New) < 1) return "You no longer have " + Name(s.New) + " to equip.";
            Move(s.New, -1);
        }

        // Apply.
        var day = CampaignTime.Now.ToString();
        foreach (var s in swaps)
        {
            if (!s.Old.IsEmpty) party.ItemRoster.AddToCounts(s.Old, 1);
            s.Set.AddEquipmentToSlotWithoutAgent(s.Slot, default);
        }
        foreach (var m in moves)
        {
            party.ItemRoster.AddToCounts(m.Element, -m.Delta);
            w.Add(m.Key, m.Delta);
            if (m.Delta > 0 && Crafted.Describe(m.Element.Item) is { } design) Store.Designs[m.Element.Item.StringId] = design;
            w.Note($"{day}: {hero.Name} {(m.Delta > 0 ? "deposited" : "withdrew")} {Math.Abs(m.Delta)} {Name(m.Element)}");
        }
        foreach (var s in swaps)
        {
            if (s.New.IsEmpty) continue;
            party.ItemRoster.AddToCounts(s.New, -1);
            s.Set.AddEquipmentToSlotWithoutAgent(s.Slot, s.New);
        }
        return null;
    }

    private static string UnknownItem(string key) => key.StartsWith("crafted_item", StringComparison.Ordinal)
        ? "The server does not know this crafted weapon (" + key + ")."
        : "Unknown item " + key + ".";

    private static int Have(MobileParty party, EquipmentElement element)
    {
        var i = party.ItemRoster.FindIndexOfElement(element);
        return i < 0 ? 0 : party.ItemRoster.GetElementNumber(i);
    }

    private static string? MoveGold(Hero hero, Warehouse w, int delta)
    {
        if (delta == 0) return "No amount given.";
        if (delta > 0 && hero.Gold < delta) return "You do not have " + delta + " denars.";
        if (delta < 0 && w.Gold < -delta) return "The warehouse holds only " + w.Gold + " denars.";
        hero.ChangeHeroGold(-delta);
        w.Gold += delta;
        w.Note($"{CampaignTime.Now}: {hero.Name} {(delta > 0 ? "deposited" : "withdrew")} {Math.Abs(delta)} denars");
        return null;
    }

    /// <summary>Kingdom stashes in a settlement that no longer belongs to that kingdom, or of a destroyed kingdom, go to the public one.</summary>
    public static void SweepKingdomStashes()
    {
        var moves = new List<(string Settlement, string Owner, Warehouse W)>();
        foreach (var kv in Store.All)
        {
            if (!WarehouseStore.TryParseKey(kv.Key, out var sid, out var scope, out var owner) || scope != Scope.Kingdom || kv.Value.IsEmpty) continue;
            var settlement = Settlement.Find(sid);
            var kingdom = Kingdom.All.FirstOrDefault(k => k.StringId == owner);
            if (settlement == null) continue;
            if (kingdom == null || kingdom.IsEliminated || settlement.MapFaction != kingdom) moves.Add((sid, owner, kv.Value));
        }
        foreach (var m in moves)
        {
            var target = Store.Get(m.Settlement, Scope.Public, "");
            var what = $"{m.W.Items.Values.Sum()} items and {m.W.Gold} denars";
            m.W.MergeInto(target);
            var line = $"{CampaignTime.Now}: the kingdom warehouse of {m.Owner} was given to the public ({what})";
            m.W.Note(line);
            target.Note(line);
            Log.Info($"{m.Settlement}: {line}");
        }
    }

    // ---- items ----

    public static string ItemKey(EquipmentElement e) => e.Item.StringId + "|" + (e.ItemModifier?.StringId ?? "");

    public static bool TryElement(string key, out EquipmentElement element)
    {
        element = default;
        var bar = key.IndexOf('|');
        if (bar <= 0) return false;
        var item = MBObjectManager.Instance.GetObject<ItemObject>(key.Substring(0, bar));
        if (item == null) return false;
        ItemModifier? modifier = null;
        if (bar < key.Length - 1)
        {
            modifier = MBObjectManager.Instance.GetObject<ItemModifier>(key.Substring(bar + 1));
            if (modifier == null) return false;
        }
        element = new EquipmentElement(item, modifier);
        return true;
    }

    /// <summary>
    /// Crafted weapons are stored with their design (<see cref="Crafted"/>). One marked as player-crafted but with no
    /// readable design could not be rebuilt on another game, so it is refused.
    /// </summary>
    public static bool Storable(ItemObject item, out string why)
    {
        why = "";
        if (item.IsCraftedByPlayer && Crafted.Describe(item) == null)
        {
            why = item.Name + " is a crafted weapon whose design cannot be read; the warehouse cannot take it.";
            return false;
        }
        return true;
    }

    /// <summary>After a load: rebuild any stored crafted weapon this game no longer has.</summary>
    public static void EnsureStoredCraftedWeapons()
    {
        int ok = 0, failed = 0;
        foreach (var design in Store.Designs.Values)
        {
            if (Crafted.Ensure(design, out var why)) ok++;
            else { failed++; Log.Warn("stored crafted weapon not rebuilt: " + why); }
        }
        if (ok + failed > 0) Log.Info($"stored crafted weapons: {ok} ready, {failed} failed");
    }

    public static string Name(EquipmentElement e) => e.GetModifiedItemName().ToString();
}

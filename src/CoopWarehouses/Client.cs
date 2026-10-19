using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Inventory;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace CoopWarehouses;

/// <summary>
/// The player's side: menus, the stash screen and gold prompts. Every change is a request to the authority (this game in
/// single player, the server in co-op) and the screen shows what the authority answered.
/// </summary>
public static class Client
{
    public const string ScopesMenu = "cw_scopes", ScopeMenu = "cw_scope";

    private static Scope _scope;
    private static WireReply? _last;
    private static int _seq;
    private static int _openSeq = -1;

    // The stash screen currently showing a warehouse, and what it held when it opened.
    private static ItemRoster? _stash;
    private static Dictionary<string, int>? _stashAtOpen;
    private static string _stashSettlement = "";
    private static Scope _stashScope;

    // ---- menus ----

    public static void AddMenus(CampaignGameStarter starter)
    {
        foreach (var parent in new[] { "town", "castle" })
            starter.AddGameMenuOption(parent, "cw_visit_" + parent, "{=cw_visit}Visit the warehouses",
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Trade; return Rules.IsWarehouseSettlement(Settlement.CurrentSettlement); },
                _ => GameMenu.SwitchToMenu(ScopesMenu), false, 4);

        starter.AddGameMenu(ScopesMenu,
            "{=cw_scopes}The warehouses of {CW_SETTLEMENT}. The public store is open to anyone; the clan and kingdom stores only to their members.",
            _ => MBTextManager.SetTextVariable("CW_SETTLEMENT", Settlement.CurrentSettlement?.Name ?? new TextObject("")));
        AddScopeOption(starter, Scope.Public, "{=cw_public}Public warehouse", _ => true);
        AddScopeOption(starter, Scope.Clan, "{=cw_clan}Clan warehouse", h => h.Clan != null);
        AddScopeOption(starter, Scope.Kingdom, "{=cw_kingdom}Kingdom warehouse", h => h.Clan?.Kingdom != null);
        starter.AddGameMenuOption(ScopesMenu, "cw_leave", "{=cw_leave}Leave",
            args => { args.optionLeaveType = GameMenuOption.LeaveType.Leave; return true; },
            _ => GameMenu.SwitchToMenu(Settlement.CurrentSettlement?.IsCastle == true ? "castle" : "town"), true);

        starter.AddGameMenu(ScopeMenu, "{=cw_scope}{CW_TITLE}{newline}{newline}Stored gold: {CW_GOLD}{newline}{CW_STATUS}", _ =>
        {
            ShowLast();
            Request(new WireRequest { Op = WireRequest.Open });
        });
        starter.AddGameMenuOption(ScopeMenu, "cw_open", "{=cw_open}Open the storage",
            args => { args.optionLeaveType = GameMenuOption.LeaveType.Trade; return true; },
            _ => { var r = new WireRequest { Op = WireRequest.Open }; _openSeq = Request(r); });
        starter.AddGameMenuOption(ScopeMenu, "cw_deposit", "{=cw_deposit}Deposit gold",
            args => { args.optionLeaveType = GameMenuOption.LeaveType.Submenu; return true; },
            _ => AskGold(deposit: true));
        starter.AddGameMenuOption(ScopeMenu, "cw_withdraw", "{=cw_withdraw}Withdraw gold",
            args => { args.optionLeaveType = GameMenuOption.LeaveType.Submenu; return true; },
            _ => AskGold(deposit: false));
        starter.AddGameMenuOption(ScopeMenu, "cw_log", "{=cw_log}Read the ledger",
            args => { args.optionLeaveType = GameMenuOption.LeaveType.Submenu; return true; },
            _ => ShowLog());
        starter.AddGameMenuOption(ScopeMenu, "cw_back", "{=cw_back}Back",
            args => { args.optionLeaveType = GameMenuOption.LeaveType.Leave; return true; },
            _ => GameMenu.SwitchToMenu(ScopesMenu), true);
    }

    private static void AddScopeOption(CampaignGameStarter starter, Scope scope, string text, Func<Hero, bool> visible)
    {
        starter.AddGameMenuOption(ScopesMenu, "cw_scope_" + (int)scope, text, args =>
        {
            args.optionLeaveType = GameMenuOption.LeaveType.Trade;
            var hero = Hero.MainHero;
            if (hero == null || !visible(hero)) return false;
            if (!Rules.TryOwner(hero, Settlement.CurrentSettlement, scope, out _, out var why))
            {
                args.IsEnabled = false;
                args.Tooltip = new TextObject(why);
            }
            return true;
        }, _ =>
        {
            _scope = scope;
            _last = null;
            GameMenu.SwitchToMenu(ScopeMenu);
        });
    }

    private static string Title(Scope scope) => scope switch
    {
        Scope.Clan => "The clan warehouse of " + Hero.MainHero?.Clan?.Name,
        Scope.Kingdom => "The kingdom warehouse of " + Hero.MainHero?.Clan?.Kingdom?.Name,
        _ => "The public warehouse",
    };

    private static void ShowLast()
    {
        MBTextManager.SetTextVariable("CW_TITLE", Title(_scope));
        MBTextManager.SetTextVariable("CW_GOLD", _last == null ? "..." : _last.Gold.ToString("N0"));
        MBTextManager.SetTextVariable("CW_STATUS", _last == null ? "Asking the warehouse keeper..." : $"{_last.Items.Count} kinds of goods stored.");
    }

    // ---- requests ----

    /// <summary>Sends a request for the current settlement and scope; returns its sequence number.</summary>
    private static int Request(WireRequest req)
    {
        req.SettlementId = Settlement.CurrentSettlement?.StringId ?? "";
        req.Scope = (int)_scope;
        req.Seq = ++_seq;
        if (Side.IsAuthority)
        {
            var hero = Hero.MainHero;
            var party = MobileParty.MainParty;
            if (hero == null || party == null) return req.Seq;
            OnReply(Rules.Handle(hero, party, req));
        }
        else if (Net.Send is { } send) send(req);
        else Message("The warehouse keeper cannot reach the server (not connected).");
        return req.Seq;
    }

    /// <summary>A reply from the authority. On a co-op client this runs on the game thread.</summary>
    public static void OnReply(WireReply reply)
    {
        if (reply.SettlementId != Settlement.CurrentSettlement?.StringId || reply.Scope != (int)_scope) return;
        _last = reply;
        if (!reply.Ok) Message(reply.Message);
        else if (reply.Op == WireRequest.Transfer) Message("The warehouse keeper records your goods.");
        else if (reply.Op == WireRequest.Gold) Message("The warehouse keeper counts out the coin.");
        if (reply.Ok && reply.Op == WireRequest.Open && reply.Seq == _openSeq)
        {
            _openSeq = -1;
            OpenStash(reply);
            return;
        }
        if (Campaign.Current?.CurrentMenuContext?.GameMenu?.StringId == ScopeMenu)
        {
            ShowLast();
            Campaign.Current.CurrentMenuContext.Refresh();
        }
    }

    private static void Message(string text) => InformationManager.DisplayMessage(new InformationMessage(text));

    // ---- gold and ledger ----

    private static void AskGold(bool deposit)
    {
        InformationManager.ShowTextInquiry(new TextInquiryData(
            deposit ? "Deposit gold" : "Withdraw gold",
            deposit ? $"How many denars? You carry {Hero.MainHero?.Gold:N0}." : $"How many denars? The warehouse holds {_last?.Gold.ToString("N0") ?? "?"}.",
            true, true, "OK", "Cancel",
            text =>
            {
                if (int.TryParse(text, out var n) && n > 0)
                    Request(new WireRequest { Op = WireRequest.Gold, GoldDelta = deposit ? n : -n });
            },
            null, false,
            text => int.TryParse(text, out var n) && n > 0 ? new Tuple<bool, string>(true, "") : new Tuple<bool, string>(false, "Enter a whole number above zero.")));
    }

    private static void ShowLog()
    {
        var lines = _last?.Log ?? new List<string>();
        var text = lines.Count == 0 ? "Nothing has been recorded yet." : string.Join("\n", lines.AsEnumerable().Reverse().Take(30));
        InformationManager.ShowInquiry(new InquiryData("The ledger", text, true, false, "Close", "", null, null));
    }

    // ---- the stash screen ----

    private static void OpenStash(WireReply reply)
    {
        // Crafted weapons this game has never seen: build them from the designs the warehouse sent.
        foreach (var design in reply.Designs)
            if (!Crafted.Ensure(design, out var why)) Log.Warn("crafted weapon not built: " + why);
        var roster = new ItemRoster();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in reply.Items)
        {
            var p = line.Split('\t');
            if (p.Length != 2 || !int.TryParse(p[1], out var n) || n <= 0) continue;
            if (!Rules.TryElement(p[0], out var element)) { Log.Warn("unknown stored item " + p[0] + " (a removed mod?); not shown"); continue; }
            roster.AddToCounts(element, n);
            counts[p[0]] = n;
        }
        _stash = roster;
        _stashAtOpen = counts;
        _stashSettlement = reply.SettlementId;
        _stashScope = (Scope)reply.Scope;
        _equipmentAtOpen = Equipped();
        InventoryScreenHelper.OpenScreenAsStash(roster);
    }

    // What every hero in the player's party wore when the screen opened: "heroId\tset\tslot" -> item key ("" = empty).
    private static Dictionary<string, string>? _equipmentAtOpen;

    /// <summary>The battle (set 0) and civilian (set 1) equipment of every hero in the player's party.</summary>
    private static Dictionary<string, string> Equipped()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var party = MobileParty.MainParty;
        if (party == null) return map;
        foreach (var troop in party.MemberRoster.GetTroopRoster())
        {
            var hero = troop.Character?.HeroObject;
            if (hero == null) continue;
            for (var set = 0; set < 2; set++)
            {
                var equipment = set == 0 ? hero.BattleEquipment : hero.CivilianEquipment;
                if (equipment == null) continue;
                for (var slot = 0; slot < (int)EquipmentIndex.NumEquipmentSetSlots; slot++)
                {
                    var e = equipment[(EquipmentIndex)slot];
                    map[hero.StringId + "\t" + set + "\t" + slot] = e.IsEmpty ? "" : Rules.ItemKey(e);
                }
            }
        }
        return map;
    }

    /// <summary>Equipment slots changed on the screen, as <c>heroId\tset\tslot\toldKey\tnewKey</c>.</summary>
    private static List<string> EquipmentChanges()
    {
        var lines = new List<string>();
        if (_equipmentAtOpen == null) return lines;
        foreach (var now in Equipped())
            if (_equipmentAtOpen.TryGetValue(now.Key, out var before) && before != now.Value)
                lines.Add(now.Key + "\t" + before + "\t" + now.Value);
        return lines;
    }

    private static Dictionary<string, int> Count(ItemRoster roster)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in roster)
        {
            if (e.Amount <= 0 || e.EquipmentElement.Item == null) continue;
            var key = Rules.ItemKey(e.EquipmentElement);
            counts[key] = (counts.TryGetValue(key, out var n) ? n : 0) + e.Amount;
        }
        return counts;
    }

    private static readonly AccessTools.FieldRef<InventoryLogic, ItemRoster[]> Rosters =
        AccessTools.FieldRefAccess<InventoryLogic, ItemRoster[]>("_rosters");

    /// <summary>
    /// Ahead of everything else on the inventory screen's Done (including Coop's own prefix, which would send it as a
    /// party trade): when the screen is one of our warehouses, put both sides back as they were, send the difference
    /// to the authority as one transfer, and close the screen. The authority moves the goods in the player's party,
    /// which Coop then syncs back like any server-side change.
    /// </summary>
    [HarmonyPatch(typeof(InventoryLogic), nameof(InventoryLogic.DoneLogic))]
    public static class DonePatch
    {
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(InventoryLogic __instance, ref bool __result)
        {
            if (_stash == null || _stashAtOpen == null) return true;
            try
            {
                var rosters = Rosters(__instance);
                if (rosters == null || rosters.Length == 0 || !ReferenceEquals(rosters[0], _stash)) return true;
                var now = Count(_stash);
                var lines = new List<string>();
                foreach (var key in now.Keys.Union(_stashAtOpen.Keys))
                {
                    var delta = (now.TryGetValue(key, out var a) ? a : 0) - (_stashAtOpen.TryGetValue(key, out var b) ? b : 0);
                    if (delta != 0) lines.Add(key + "\t" + delta);
                }
                // Equipping or unequipping on this screen is undone by Reset too, so it travels with the transfer.
                var equipment = EquipmentChanges();
                __instance.Reset(true);
                _stash = null;
                _stashAtOpen = null;
                _equipmentAtOpen = null;
                if ((lines.Count > 0 || equipment.Count > 0) && _stashSettlement == Settlement.CurrentSettlement?.StringId && _stashScope == _scope)
                    Request(new WireRequest { Op = WireRequest.Transfer, Items = lines, Equipment = equipment });
                __result = true;
                return false;
            }
            catch (Exception ex)
            {
                // Never fall through to the original: the stash roster is a throwaway copy, so goods moved into it
                // would vanish. Undo the screen instead; nothing has moved.
                Log.Warn("warehouse Done failed, nothing moved: " + ex);
                _stash = null;
                try { __instance.Reset(true); } catch { }
                __result = true;
                return false;
            }
        }
    }
}

# Coop Warehouses

Public, clan and kingdom warehouses in every town and castle, for Bannerlord single player and Bannerlord Coop.
Players can store items (including horses, food and trade goods) and gold for each other.

## Rules
- **Public:** anyone can use it. **Clan:** any member of the clan. **Kingdom:** any member of the kingdom, and only in
  that kingdom's own settlements.
- A player who leaves a kingdom loses access straight away (access is checked on every request).
- When a settlement changes owner, or a kingdom is destroyed, that kingdom's stash goes into the settlement's public warehouse.
- Every deposit and withdrawal is written to the warehouse's ledger (last 200 entries).
- Stored gold is out of circulation. AI lords never use warehouses. Player-crafted weapons are refused for now.

## How it works
- `CoopWarehouses` (the submodule) holds the rules, menus, the stash screen and saving. It references nothing of Coop's.
- `CoopWarehouses.Coop` is loaded by hand once Coop is in the process. It carries requests to the server and replies back.
- The server owns the contents. A player's game opens the vanilla stash screen with a copy of the contents. On Done, the
  mod intercepts the screen ahead of Coop and puts both sides back, then sends the difference to the server. The server checks it
  against the warehouse and the player's party as they are now and applies it all or nothing. Coop syncs the party
  change back.
- Saved as one plain string in the campaign save (no saveable types), so removing the mod does not break saves.

Build: `dotnet build -c Release`, then copy `src/CoopWarehouses/_Module` to `Modules/CoopWarehouses` on the server
and every client.

## Spike checklist (not yet verified in game)
- [ ] Single player: deposit and withdraw items and gold; save and load; the ledger.
- [ ] Co-op: the adapter loads and both handlers arm (`Documents/.../Configs/ModLogs/CoopWarehouses-*.log`).
- [ ] Co-op: a deposit leaves the client's party and comes back correctly after the server applies it (no duplicates or losses).
- [ ] Co-op: gold changes made by `Hero.ChangeHeroGold` on the server reach the client.
- [ ] Our Done prefix runs before Coop's (otherwise Coop also sends the screen as a party trade).
- [ ] Two players withdraw the last item at once: one gets it, the other gets "no longer has".
- [ ] Settlement captured: the kingdom stash moves to public.

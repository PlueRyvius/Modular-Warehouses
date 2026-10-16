using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace CoopWarehouses;

public sealed class SubModule : MBSubModuleBase
{
    private const string AdapterFileName = "CoopWarehouses.Coop.dll";
    private bool _adapterTried;
    private float _sinceProbe;

    protected override void OnSubModuleLoad()
    {
        base.OnSubModuleLoad();
        try
        {
            new Harmony("CoopWarehouses").PatchAll(typeof(SubModule).Assembly);
            Log.Info("loaded; " + (Side.IsServer ? "server" : "player's game"));
        }
        catch (Exception ex) { Log.Warn("patches failed: " + ex.GetBaseException().Message); }
    }

    protected override void OnGameStart(Game game, IGameStarter starter)
    {
        base.OnGameStart(game, starter);
        if (game.GameType is Campaign && starter is CampaignGameStarter campaign)
        {
            Rules.Store = new WarehouseStore();
            campaign.AddBehavior(new WarehouseBehavior());
        }
    }

    protected override void OnApplicationTick(float dt)
    {
        base.OnApplicationTick(dt);
        if (_adapterTried) return;
        _sinceProbe += dt;
        if (_sinceProbe < 1f) return;
        _sinceProbe = 0f;
        TryLoadAdapter();
    }

    /// <summary>
    /// Loads the Coop half once Coop's assemblies are in the process. It has to be loaded before a session starts:
    /// Coop builds its message handlers from the loaded assemblies then.
    /// </summary>
    private void TryLoadAdapter()
    {
        bool Has(string name) => AppDomain.CurrentDomain.GetAssemblies().Any(a => !a.IsDynamic && a.GetName().Name == name);
        if (!Has("Common") || !Has("Coop.Core") || !Has("GameInterface")) return;   // single player, or Coop not loaded yet
        _adapterTried = true;
        try
        {
            var dir = Path.GetDirectoryName(typeof(SubModule).Assembly.Location) ?? ".";
            var path = Path.Combine(dir, AdapterFileName);
            if (!File.Exists(path)) { Log.Warn("co-op disabled: " + AdapterFileName + " missing next to the submodule"); return; }
            var asm = Assembly.LoadFrom(path);
            Log.Info($"co-op adapter loaded ({asm.GetTypes().Length} types)");
        }
        catch (Exception ex) { Log.Warn("co-op adapter failed to load: " + ex.GetBaseException().Message); }
    }
}

/// <summary>Menus on every game; storage, saving and the kingdom rules only where the warehouses live.</summary>
public sealed class WarehouseBehavior : CampaignBehaviorBase
{
    public override void RegisterEvents()
    {
        CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, starter => Client.AddMenus(starter));
        CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, (_, _, _, _, _, _) => Sweep());
        CampaignEvents.KingdomDestroyedEvent.AddNonSerializedListener(this, _ => Sweep());
        CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Sweep);
    }

    private static void Sweep()
    {
        if (!Side.IsAuthority) return;
        try { Rules.SweepKingdomStashes(); }
        catch (Exception ex) { Log.Warn("kingdom sweep failed: " + ex.GetBaseException().Message); }
    }

    public override void SyncData(IDataStore dataStore)
    {
        var text = dataStore.IsSaving ? Rules.Store.Serialize() : "";
        dataStore.SyncData("CoopWarehouses_v1", ref text);
        if (dataStore.IsLoading)
        {
            Rules.Store = WarehouseStore.Deserialize(text);
            Log.Info($"loaded {Rules.Store.All.Count()} warehouse(s)");
        }
    }
}

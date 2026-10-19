using System;
using System.Collections.Generic;
using Common;
using Common.Messaging;
using Common.Network;
using CoopWarehouses;
using CoopWarehouses.Messages;
using GameInterface;
using GameInterface.Services.ObjectManager;
using GameInterface.Services.Players;
using LiteNetLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

// Coop discovers server-side handlers by namespace prefix (Coop.Core.Server.*) and builds them from its container when
// a hosting session starts. The namespace is a discovery convention, nothing more.
namespace Coop.Core.Server.Services.CoopWarehouses.Handlers
{
    /// <summary>Server: runs each warehouse request as the player who sent it and answers that player.</summary>
    public sealed class WarehouseServerHandler : IHandler
    {
        private readonly IMessageBroker broker;
        private readonly INetwork network;

        public WarehouseServerHandler(IMessageBroker broker, INetwork network)
        {
            this.broker = broker;
            this.network = network;
            broker.Subscribe<NetworkWarehouseRequest>(Handle);
            Log.Info("server handler armed");
        }

        private void Handle(MessagePayload<NetworkWarehouseRequest> payload)
        {
            if (payload.Who is not NetPeer peer) return;
            var msg = payload.What;
            if (msg.ProtocolVersion != Protocol.Version) { Log.Warn($"request with protocol {msg.ProtocolVersion}, expected {Protocol.Version}; ignored"); return; }
            GameThread.RunSafe(() => Run(peer, msg), false, "CoopWarehouses request");
        }

        private void Run(NetPeer peer, NetworkWarehouseRequest msg)
        {
            var req = new WireRequest
            {
                Op = msg.Op, SettlementId = msg.SettlementId, Scope = msg.Scope, Items = msg.Items ?? new List<string>(), Equipment = msg.Equipment ?? new List<string>(),
                GoldDelta = msg.GoldDelta, Seq = msg.Seq,
            };
            WireReply reply;
            try
            {
                if (!ContainerProvider.TryResolve<IPlayerManager>(out var players) || !players.TryGetPlayer(peer, out var player) || player is null
                    || !ContainerProvider.TryResolve<IObjectManager>(out var objects)
                    || !objects.TryGetObject<Hero>(player.HeroId, out var hero) || hero is null
                    || !objects.TryGetObject<MobileParty>(player.MobilePartyId, out var party) || party is null)
                    reply = new WireReply { Op = req.Op, SettlementId = req.SettlementId, Scope = req.Scope, Seq = req.Seq, Message = "The server does not know you as a player." };
                else
                    reply = Rules.Handle(hero, party, req);
            }
            catch (Exception ex)
            {
                Log.Warn("request failed: " + ex);
                reply = new WireReply { Op = req.Op, SettlementId = req.SettlementId, Scope = req.Scope, Seq = req.Seq, Message = "The warehouse keeper is confused (server error)." };
            }
            network.Send(peer, new NetworkWarehouseReply
            {
                Op = reply.Op, SettlementId = reply.SettlementId, Scope = reply.Scope, Ok = reply.Ok, Message = reply.Message,
                Items = reply.Items, Gold = reply.Gold, Log = reply.Log, Designs = reply.Designs, Seq = reply.Seq, ProtocolVersion = Protocol.Version,
            });
        }

        public void Dispose() => broker.Unsubscribe<NetworkWarehouseRequest>(Handle);
    }
}

// Coop discovers client-side handlers by namespace prefix (Coop.Core.Client.*); see the server handler.
namespace Coop.Core.Client.Services.CoopWarehouses.Handlers
{
    /// <summary>Client: sends this player's warehouse requests to the server and hands the replies to the game side.</summary>
    public sealed class WarehouseClientHandler : IHandler
    {
        private readonly IMessageBroker broker;
        private readonly INetwork network;

        public WarehouseClientHandler(IMessageBroker broker, INetwork network)
        {
            this.broker = broker;
            this.network = network;
            broker.Subscribe<NetworkWarehouseReply>(Handle);
            Net.Send = req => network.SendAll(new NetworkWarehouseRequest
            {
                Op = req.Op, SettlementId = req.SettlementId, Scope = req.Scope, Items = req.Items, Equipment = req.Equipment, GoldDelta = req.GoldDelta,
                Seq = req.Seq, ProtocolVersion = Protocol.Version,
            });
            Log.Info("client handler armed");
        }

        private void Handle(MessagePayload<NetworkWarehouseReply> payload)
        {
            var m = payload.What;
            var reply = new WireReply
            {
                Op = m.Op, SettlementId = m.SettlementId, Scope = m.Scope, Ok = m.Ok, Message = m.Message,
                Items = m.Items ?? new List<string>(), Gold = m.Gold, Log = m.Log ?? new List<string>(), Designs = m.Designs ?? new List<string>(), Seq = m.Seq,
            };
            GameThread.RunSafe(() => global::CoopWarehouses.Client.OnReply(reply), false, "CoopWarehouses reply");
        }

        public void Dispose()
        {
            broker.Unsubscribe<NetworkWarehouseReply>(Handle);
            Net.Send = null;
        }
    }
}

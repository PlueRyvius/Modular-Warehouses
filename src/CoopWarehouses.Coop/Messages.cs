using System.Collections.Generic;
using Common.Messaging;
using ProtoBuf;

namespace CoopWarehouses.Messages;

// WIRE-FORMAT NOTE: Coop derives each message's wire id from a hash of the type's full name, so these names are part
// of the protocol between server and clients. Never rename or move them; add new members with new tags.

/// <summary>Client -> server: open, transfer or gold, for one warehouse.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkWarehouseRequest : ICommand
{
    [ProtoMember(1)] public string Op { get; set; } = "";
    [ProtoMember(2)] public string SettlementId { get; set; } = "";
    [ProtoMember(3)] public int Scope { get; set; }
    [ProtoMember(4)] public List<string>? Items { get; set; }
    [ProtoMember(5)] public int GoldDelta { get; set; }
    [ProtoMember(6)] public int Seq { get; set; }
    [ProtoMember(7)] public int ProtocolVersion { get; set; }
}

/// <summary>Server -> the asking client: the outcome and the warehouse as it now is.</summary>
[ProtoContract(SkipConstructor = true)]
public sealed class NetworkWarehouseReply : IEvent
{
    [ProtoMember(1)] public string Op { get; set; } = "";
    [ProtoMember(2)] public string SettlementId { get; set; } = "";
    [ProtoMember(3)] public int Scope { get; set; }
    [ProtoMember(4)] public bool Ok { get; set; }
    [ProtoMember(5)] public string Message { get; set; } = "";
    [ProtoMember(6)] public List<string>? Items { get; set; }
    [ProtoMember(7)] public long Gold { get; set; }
    [ProtoMember(8)] public List<string>? Log { get; set; }
    [ProtoMember(9)] public int Seq { get; set; }
    [ProtoMember(10)] public int ProtocolVersion { get; set; }
    /// <summary>Crafted weapon designs for the items above (Crafted.Describe lines).</summary>
    [ProtoMember(11)] public List<string>? Designs { get; set; }
}

public static class Protocol
{
    public const int Version = 1;
}

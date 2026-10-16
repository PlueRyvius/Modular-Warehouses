using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CoopWarehouses;

public enum Scope { Public = 0, Clan = 1, Kingdom = 2 }

/// <summary>One stash: items by key (<c>itemId|modifierId</c>), stored gold, and an audit log (newest last).</summary>
public sealed class Warehouse
{
    public const int MaxLog = 200;

    public readonly Dictionary<string, int> Items = new Dictionary<string, int>(StringComparer.Ordinal);
    public long Gold;
    public readonly List<string> Log = new List<string>();

    public int Count(string key) => Items.TryGetValue(key, out var n) ? n : 0;

    public void Add(string key, int delta)
    {
        var n = Count(key) + delta;
        if (n <= 0) Items.Remove(key); else Items[key] = n;
    }

    public void Note(string line)
    {
        Log.Add(line);
        if (Log.Count > MaxLog) Log.RemoveRange(0, Log.Count - MaxLog);
    }

    public bool IsEmpty => Items.Count == 0 && Gold == 0;

    public void MergeInto(Warehouse target)
    {
        foreach (var kv in Items) target.Add(kv.Key, kv.Value);
        target.Gold += Gold;
        Items.Clear();
        Gold = 0;
    }
}

/// <summary>
/// Every warehouse in the campaign, keyed by settlement, scope and owner (clan or kingdom id; empty for public).
/// Only the authoritative game (single player or the co-op server) holds real contents.
/// </summary>
public sealed class WarehouseStore
{
    private readonly Dictionary<string, Warehouse> _all = new Dictionary<string, Warehouse>(StringComparer.Ordinal);

    public static string Key(string settlementId, Scope scope, string ownerId) => settlementId + "|" + (int)scope + "|" + ownerId;

    public static bool TryParseKey(string key, out string settlementId, out Scope scope, out string ownerId)
    {
        settlementId = ownerId = "";
        scope = Scope.Public;
        var parts = key.Split('|');
        if (parts.Length != 3 || !int.TryParse(parts[1], out var s) || s < 0 || s > 2) return false;
        settlementId = parts[0];
        scope = (Scope)s;
        ownerId = parts[2];
        return true;
    }

    public Warehouse Get(string settlementId, Scope scope, string ownerId)
    {
        var key = Key(settlementId, scope, ownerId);
        if (!_all.TryGetValue(key, out var w)) _all[key] = w = new Warehouse();
        return w;
    }

    public IEnumerable<KeyValuePair<string, Warehouse>> All => _all;

    public void DropEmpty()
    {
        foreach (var key in _all.Where(kv => kv.Value.IsEmpty && kv.Value.Log.Count == 0).Select(kv => kv.Key).ToList())
            _all.Remove(key);
    }

    // Save format: plain text, one record per line, tab-separated. Plain strings rather than saveable types, so
    // removing the mod never leaves types in a save that the game cannot read.
    //   W <key> <gold>       starts a warehouse
    //   I <itemKey> <count>  an item stack in the current warehouse
    //   L <text>             an audit line in the current warehouse
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("V\t1\n");
        foreach (var kv in _all)
        {
            if (kv.Value.IsEmpty && kv.Value.Log.Count == 0) continue;
            sb.Append("W\t").Append(kv.Key).Append('\t').Append(kv.Value.Gold).Append('\n');
            foreach (var item in kv.Value.Items) sb.Append("I\t").Append(item.Key).Append('\t').Append(item.Value).Append('\n');
            foreach (var line in kv.Value.Log) sb.Append("L\t").Append(Clean(line)).Append('\n');
        }
        return sb.ToString();
    }

    public static WarehouseStore Deserialize(string? text)
    {
        var store = new WarehouseStore();
        if (string.IsNullOrEmpty(text)) return store;
        Warehouse? current = null;
        foreach (var raw in text!.Split('\n'))
        {
            var p = raw.Split('\t');
            switch (p[0])
            {
                case "W" when p.Length == 3:
                    current = new Warehouse { Gold = long.TryParse(p[2], out var g) ? g : 0 };
                    store._all[p[1]] = current;
                    break;
                case "I" when p.Length == 3 && current != null && int.TryParse(p[2], out var n):
                    current.Add(p[1], n);
                    break;
                case "L" when p.Length >= 2 && current != null:
                    current.Log.Add(p[1]);
                    break;
            }
        }
        return store;
    }

    public static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}

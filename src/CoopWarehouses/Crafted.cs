using System;
using System.Linq;
using HarmonyLib;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace CoopWarehouses;

/// <summary>
/// Player-crafted weapons. Vanilla makes each one per campaign (<c>crafted_item_N</c>) and keeps it in the crafting
/// behaviour's save. Coop only sends an item's id, never its content, so a player's game may never have seen a weapon
/// someone else made. The warehouse keeps each stored weapon's design, sends it with the contents, and rebuilds the
/// weapon from that design wherever it is missing, the same way vanilla rebuilds crafted weapons when a save loads.
/// </summary>
public static class Crafted
{
    private const char Sep = '\u001f';
    private const string Format = "C1";

    private static readonly Action<ItemObject, bool>? SetIsReady =
        AccessTools.PropertySetter(typeof(ItemObject), nameof(ItemObject.IsReady)) is { } m
            ? (Action<ItemObject, bool>)Delegate.CreateDelegate(typeof(Action<ItemObject, bool>), m)
            : null;

    /// <summary>A vanilla-style crafted weapon: crafted by a player and built from a weapon design.</summary>
    public static bool IsCrafted(ItemObject item) => item.IsCraftedByPlayer && item.WeaponDesign != null;

    /// <summary>
    /// The design as one line: format, id, template, culture, name, then one <c>pieceId:scale</c> per piece slot
    /// (Blade, Guard, Handle, Pommel; empty when the slot has no piece). Null when the item is not a crafted weapon.
    /// </summary>
    public static string? Describe(ItemObject item)
    {
        if (!IsCrafted(item)) return null;
        var design = item.WeaponDesign;
        if (design.Template == null || design.UsedPieces == null) return null;
        var pieces = Enumerable.Range(0, 4).Select(i =>
        {
            var e = i < design.UsedPieces.Length ? design.UsedPieces[i] : null;
            return e is { IsValid: true, CraftingPiece: { } p } ? p.StringId + ":" + e.ScalePercentage : "";
        });
        var name = Clean(item.Name?.ToString() ?? "");
        return string.Join(Sep.ToString(), new[] { Format, item.StringId, design.Template.StringId, item.Culture?.StringId ?? "", name }.Concat(pieces));
    }

    public static string IdOf(string description)
    {
        var p = description.Split(Sep);
        return p.Length > 1 ? p[1] : "";
    }

    /// <summary>
    /// Makes sure the described weapon exists here and is ready. Leaves an existing, ready item alone; rebuilds a
    /// not-ready one in place (keeping its identity); otherwise creates and registers it under the same id.
    /// </summary>
    public static bool Ensure(string description, out string why)
    {
        why = "";
        var p = description.Split(Sep);
        if (p.Length != 9 || p[0] != Format) { why = "unreadable design"; return false; }
        string id = p[1], templateId = p[2], cultureId = p[3];
        var existing = MBObjectManager.Instance.GetObject<ItemObject>(id);
        if (existing is { IsReady: true }) return true;

        var template = CraftingTemplate.GetTemplateFromId(templateId);
        if (template == null) { why = $"{id}: unknown crafting template {templateId}"; return false; }
        var pieces = new WeaponDesignElement[4];
        for (var i = 0; i < 4; i++)
        {
            var slot = p[5 + i];
            if (slot.Length == 0) { pieces[i] = WeaponDesignElement.GetInvalidPieceForType((CraftingPiece.PieceTypes)i); continue; }
            var colon = slot.LastIndexOf(':');
            var piece = colon > 0 ? MBObjectManager.Instance.GetObject<CraftingPiece>(slot.Substring(0, colon)) : null;
            if (piece == null || !int.TryParse(slot.Substring(colon + 1), out var scale)) { why = $"{id}: unknown crafting piece {slot}"; return false; }
            pieces[i] = WeaponDesignElement.CreateUsablePiece(piece, scale);
        }
        var name = new TextObject(p[4]);
        var culture = cultureId.Length == 0 ? null : MBObjectManager.Instance.GetObject<BasicCultureObject>(cultureId);
        var design = new WeaponDesign(template, name, pieces, id);

        try
        {
            if (existing != null)
            {
                Crafting.InitializePreCraftedWeaponOnLoad(existing, design, name, culture);
                ItemObject.InitAsPlayerCraftedItem(ref existing);
                SetIsReady?.Invoke(existing, true);
                return true;
            }
            ItemObject? item = null;
            Crafting.GenerateItem(design, name, culture, template.ItemModifierGroup, ref item, id);
            if (item == null) { why = $"{id}: the game could not build the weapon"; return false; }
            ItemObject.InitAsPlayerCraftedItem(ref item);
            SetIsReady?.Invoke(item, true);
            MBObjectManager.Instance.RegisterObject(item);
            // RegisterObject renames on a collision; a renamed copy would not match the id the server uses.
            if (item.StringId != id) { why = $"{id}: registered as {item.StringId}"; return false; }
            return true;
        }
        catch (Exception ex)
        {
            why = $"{id}: {ex.GetBaseException().Message}";
            return false;
        }
    }

    private static string Clean(string s) => new string(s.Where(c => c != Sep && c != '\t' && c != '\n' && c != '\r').ToArray());
}

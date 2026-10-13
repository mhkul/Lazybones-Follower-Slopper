using System.Text.Json;
namespace AutoFollower;
// Private structural evidence. Identifiers are rendered only as report-local aliases.
public sealed record EmittedReferenceDetail(string SourceRecordType, string TargetRecordType, string TargetScope, string SourceRecord, string TargetRecord, string TargetPlugin);
public sealed record PluginValidationFailure(string Schema, string Category, int Count, bool DetailsTruncated, EmittedReferenceDetail[] References)
{
    public const string Format = "Lazybones/emitted-reference-failure-v1";
    public const string Unresolved = "unresolved-emitted-references";
    public const int MaximumDetails = 32;
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "FORM", "NPC_", "ACHR", "REFR", "RACE", "ARMO", "ARMA", "HDPT", "TXST", "WEAP", "AMMO", "PROJ", "STAT", "FLST", "ENCH", "SPEL", "MGEF", "CLFM", "SNDR", "SNCT", "SOPM", "CELL", "WRLD", "RELA", "OTFT", "KYWD", "QUST", "FACT", "PERK", "CLAS", "CSTY", "VTYP", "PACK", "GLOB", "EQUP", "LCTN", "LCRT", "IMGS", "IPCT", "IPDS", "EXPL", "ARTO", "AVIF", "HAZD", "LTEX", "WATR", "MATT", "DOBJ" };
    public static string RecordType(Type type) => type.Name switch
    {
        "Npc" or "NpcBinaryOverlay" or "INpcGetter" => "NPC_",
        "PlacedNpc" or "PlacedNpcBinaryOverlay" or "IPlacedNpcGetter" => "ACHR",
        "IPlacedGetter" or "IPlacedObjectGetter" => "REFR",
        "Race" or "RaceBinaryOverlay" or "IRaceGetter" => "RACE",
        "Armor" or "ArmorBinaryOverlay" or "IArmorGetter" => "ARMO",
        "ArmorAddon" or "ArmorAddonBinaryOverlay" or "IArmorAddonGetter" => "ARMA",
        "HeadPart" or "HeadPartBinaryOverlay" or "IHeadPartGetter" => "HDPT",
        "TextureSet" or "TextureSetBinaryOverlay" or "ITextureSetGetter" => "TXST",
        "Weapon" or "WeaponBinaryOverlay" or "IWeaponGetter" => "WEAP",
        "Ammunition" or "AmmunitionBinaryOverlay" or "IAmmunitionGetter" => "AMMO",
        "Projectile" or "ProjectileBinaryOverlay" or "IProjectileGetter" => "PROJ",
        "Static" or "StaticBinaryOverlay" or "IStaticGetter" => "STAT",
        "FormList" or "FormListBinaryOverlay" or "IFormListGetter" => "FLST",
        "ObjectEffect" or "ObjectEffectBinaryOverlay" or "IObjectEffectGetter" => "ENCH",
        "Spell" or "SpellBinaryOverlay" or "ISpellGetter" => "SPEL",
        "MagicEffect" or "MagicEffectBinaryOverlay" or "IMagicEffectGetter" => "MGEF",
        "ColorRecord" or "ColorRecordBinaryOverlay" or "IColorRecordGetter" => "CLFM",
        "SoundDescriptor" or "SoundDescriptorBinaryOverlay" or "ISoundDescriptorGetter" => "SNDR",
        "SoundCategory" or "SoundCategoryBinaryOverlay" or "ISoundCategoryGetter" => "SNCT",
        "SoundOutputModel" or "SoundOutputModelBinaryOverlay" or "ISoundOutputModelGetter" => "SOPM",
        "Cell" or "CellBinaryOverlay" or "ICellGetter" => "CELL",
        "Worldspace" or "WorldspaceBinaryOverlay" or "IWorldspaceGetter" => "WRLD",
        "Relationship" or "RelationshipBinaryOverlay" or "IRelationshipGetter" => "RELA",
        "Outfit" or "OutfitBinaryOverlay" or "IOutfitGetter" => "OTFT",
        "IKeywordGetter" => "KYWD", "IQuestGetter" => "QUST", "IFactionGetter" => "FACT", "IPerkGetter" => "PERK", "IClassGetter" => "CLAS", "ICombatStyleGetter" => "CSTY", "IVoiceTypeGetter" => "VTYP", "IPackageGetter" => "PACK", "IGlobalGetter" => "GLOB", "IEquipTypeGetter" => "EQUP", "ILocationGetter" => "LCTN", "ILocationReferenceTypeGetter" => "LCRT", "IImageSpaceGetter" => "IMGS", "IImpactGetter" => "IPCT", "IImpactDataSetGetter" => "IPDS", "IExplosionGetter" => "EXPL", "IArtObjectGetter" => "ARTO", "IActorValueInformationGetter" => "AVIF", "IHazardGetter" => "HAZD", "ILandscapeTextureGetter" => "LTEX", "IWaterGetter" => "WATR", "IMaterialTypeGetter" => "MATT", "IDefaultObjectManagerGetter" => "DOBJ", _ => "FORM"
    };
    public static bool TryRead(JsonElement value, out PluginValidationFailure? failure)
    {
        failure = null;
        try
        {
            if (value.ValueKind != JsonValueKind.Object || value.GetProperty("schema").GetString() != Format || value.GetProperty("category").GetString() != Unresolved) return false;
            int count = value.GetProperty("count").GetInt32(); bool truncated = value.GetProperty("detailsTruncated").GetBoolean(); var array = value.GetProperty("references");
            if (count <= 0 || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != Math.Min(count, MaximumDetails) || truncated != (count > MaximumDetails)) return false;
            var references = new List<EmittedReferenceDetail>();
            string Identity(JsonElement row, string key) { string text = row.GetProperty(key).GetString()!; if (string.IsNullOrEmpty(text) || text.Length > 512) throw new FormatException(); return text; }
            foreach (var row in array.EnumerateArray())
            {
                string source = row.GetProperty("sourceRecordType").GetString()!, target = row.GetProperty("targetRecordType").GetString()!, scope = row.GetProperty("targetScope").GetString()!;
                if (!Types.Contains(source) || !Types.Contains(target) || scope is not ("missing-local-record" or "undeclared-master" or "missing-master-record" or "wrong-record-type")) return false;
                references.Add(new(source, target, scope, Identity(row,"sourceRecord"), Identity(row,"targetRecord"), Identity(row,"targetPlugin")));
            }
            failure = new(Format, Unresolved, count, truncated, references.ToArray()); return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return false; }
    }
}

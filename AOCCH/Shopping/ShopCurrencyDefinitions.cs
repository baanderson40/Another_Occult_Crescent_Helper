using System;
using System.Collections.Generic;
using System.Linq;

namespace AOCCH.Shopping;

public sealed class ShopCurrencyPageDefinition
{
    public required int MenuIndex { get; init; }
    public required string MenuLabel { get; init; }
    public required uint CurrencyItemId { get; init; }
    public required string CurrencyName { get; init; }
    public List<ShopCurrencyTabDefinition> Tabs { get; init; } = [];
}

public sealed class ShopCurrencyTabDefinition
{
    public required int TabId { get; init; }
    public required string TabLabel { get; init; }
    public List<ShopCurrencyItemDefinition> Items { get; init; } = [];
}

public sealed class CurrencyShopData
{
    public List<CurrencyShopVendorDefinition> Vendors { get; init; } = [];
    public List<ShopCurrencyPageDefinition> Pages { get; init; } = [];
}

public sealed class CurrencyShopVendorDefinition
{
    public uint DataId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string PreferredAethernet { get; init; } = string.Empty;
}

public sealed class ShopCurrencyItemDefinition
{
    public required uint ItemId { get; init; }
    public required string Name { get; init; }
    public required uint RowIndex { get; init; }
    public required uint Cost { get; init; }
}

public static class ShopCurrencyCatalog
{
    public static IReadOnlyList<ShopCurrencyPageDefinition> Pages { get; } =
    [
        new ShopCurrencyPageDefinition
        {
            MenuIndex = 0,
            MenuLabel = "Enlightenment Silver Piece Exchange (IL 745)",
            CurrencyItemId = 45043,
            CurrencyName = "Enlightenment Silver Piece",
            Tabs =
            [
                new ShopCurrencyTabDefinition
                {
                    TabId = 0,
                    TabLabel = "Weapons",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 1,
                    TabLabel = "Armor",
                    Items =
                    [
                        new() { ItemId = 47758, Name = "Arcanaut's Pelt of Fending", Cost = 4000, RowIndex = 0 },
                        new() { ItemId = 47773, Name = "Arcanaut's Pelt of Maiming", Cost = 4000, RowIndex = 1 },
                        new() { ItemId = 47788, Name = "Arcanaut's Bicorne of Striking", Cost = 4000, RowIndex = 2 },
                        new() { ItemId = 47818, Name = "Arcanaut's Bicorne of Scouting", Cost = 4000, RowIndex = 3 },
                        new() { ItemId = 47803, Name = "Arcanaut's Bicorne of Aiming", Cost = 4000, RowIndex = 4 },
                        new() { ItemId = 47848, Name = "Arcanaut's Sugarloaf Hat of Casting", Cost = 4000, RowIndex = 5 },
                        new() { ItemId = 47833, Name = "Arcanaut's Sugarloaf Hat of Healing", Cost = 4000, RowIndex = 6 },
                        new() { ItemId = 47759, Name = "Arcanaut's Vest of Fending", Cost = 4000, RowIndex = 7 },
                        new() { ItemId = 47774, Name = "Arcanaut's Vest of Maiming", Cost = 4000, RowIndex = 8 },
                        new() { ItemId = 47789, Name = "Arcanaut's Justaucorps of Striking", Cost = 4000, RowIndex = 9 },
                        new() { ItemId = 47819, Name = "Arcanaut's Justaucorps of Scouting", Cost = 4000, RowIndex = 10 },
                        new() { ItemId = 47804, Name = "Arcanaut's Justaucorps of Aiming", Cost = 4000, RowIndex = 11 },
                        new() { ItemId = 47849, Name = "Arcanaut's Robe of Casting", Cost = 4000, RowIndex = 12 },
                        new() { ItemId = 47834, Name = "Arcanaut's Robe of Healing", Cost = 4000, RowIndex = 13 },
                        new() { ItemId = 47760, Name = "Arcanaut's Armlets of Fending", Cost = 4000, RowIndex = 14 },
                        new() { ItemId = 47775, Name = "Arcanaut's Armlets of Maiming", Cost = 4000, RowIndex = 15 },
                        new() { ItemId = 47790, Name = "Arcanaut's Gloves of Striking", Cost = 4000, RowIndex = 16 },
                        new() { ItemId = 47820, Name = "Arcanaut's Gloves of Scouting", Cost = 4000, RowIndex = 17 },
                        new() { ItemId = 47805, Name = "Arcanaut's Gloves of Aiming", Cost = 4000, RowIndex = 18 },
                        new() { ItemId = 47850, Name = "Arcanaut's Wristgloves of Casting", Cost = 4000, RowIndex = 19 },
                        new() { ItemId = 47835, Name = "Arcanaut's Wristgloves of Healing", Cost = 4000, RowIndex = 20 },
                        new() { ItemId = 47761, Name = "Arcanaut's Loincloth of Fending", Cost = 4000, RowIndex = 21 },
                        new() { ItemId = 47776, Name = "Arcanaut's Loincloth of Maiming", Cost = 4000, RowIndex = 22 },
                        new() { ItemId = 47791, Name = "Arcanaut's Slops of Striking", Cost = 4000, RowIndex = 23 },
                        new() { ItemId = 47821, Name = "Arcanaut's Slops of Scouting", Cost = 4000, RowIndex = 24 },
                        new() { ItemId = 47806, Name = "Arcanaut's Slops of Aiming", Cost = 4000, RowIndex = 25 },
                        new() { ItemId = 47851, Name = "Arcanaut's Skirt of Casting", Cost = 4000, RowIndex = 26 },
                        new() { ItemId = 47836, Name = "Arcanaut's Skirt of Healing", Cost = 4000, RowIndex = 27 },
                        new() { ItemId = 47762, Name = "Arcanaut's Feet of Fending", Cost = 4000, RowIndex = 28 },
                        new() { ItemId = 47777, Name = "Arcanaut's Feet of Maiming", Cost = 4000, RowIndex = 29 },
                        new() { ItemId = 47792, Name = "Arcanaut's Boots of Striking", Cost = 4000, RowIndex = 30 },
                        new() { ItemId = 47822, Name = "Arcanaut's Boots of Scouting", Cost = 4000, RowIndex = 31 },
                        new() { ItemId = 47807, Name = "Arcanaut's Boots of Aiming", Cost = 4000, RowIndex = 32 },
                        new() { ItemId = 47852, Name = "Arcanaut's Boots of Casting", Cost = 4000, RowIndex = 33 },
                        new() { ItemId = 47837, Name = "Arcanaut's Boots of Healing", Cost = 4000, RowIndex = 34 },
                    ],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 2,
                    TabLabel = "Accessories",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 3,
                    TabLabel = "Other",
                    Items = [],
                },
            ],
        },
        new ShopCurrencyPageDefinition
        {
            MenuIndex = 1,
            MenuLabel = "Enlightenment Silver Piece Exchange (Battlecraft Items)",
            CurrencyItemId = 45043,
            CurrencyName = "Enlightenment Silver Piece",
            Tabs =
            [
                new ShopCurrencyTabDefinition
                {
                    TabId = 0,
                    TabLabel = "Weapons",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 1,
                    TabLabel = "Armor",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 2,
                    TabLabel = "Accessories",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 3,
                    TabLabel = "Other",
                    Items =
                    [
                        new() { ItemId = 47755, Name = "Time Mage's Soul Shard", Cost = 1000, RowIndex = 0 },
                        new() { ItemId = 47756, Name = "Cannoneer's Soul Shard", Cost = 1000, RowIndex = 1 },
                        new() { ItemId = 48748, Name = "Chemist's Soul Shard", Cost = 1000, RowIndex = 2 },
                        new() { ItemId = 49823, Name = "Mystic Knight's Soul Shard", Cost = 1000, RowIndex = 3 },
                        new() { ItemId = 49825, Name = "Dancer's Soul Shard", Cost = 1000, RowIndex = 4 },
                        new() { ItemId = 47741, Name = "Occult Potion", Cost = 40, RowIndex = 5 },
                        new() { ItemId = 47740, Name = "Occult Coffer", Cost = 40, RowIndex = 6 },
                        new() { ItemId = 47864, Name = "Aetherspun Silver", Cost = 1200, RowIndex = 7 },
                        new() { ItemId = 41759, Name = "Savage Aim Materia XI", Cost = 100, RowIndex = 8 },
                        new() { ItemId = 41772, Name = "Savage Aim Materia XII", Cost = 200, RowIndex = 9 },
                        new() { ItemId = 41760, Name = "Savage Might Materia XI", Cost = 100, RowIndex = 10 },
                        new() { ItemId = 41773, Name = "Savage Might Materia XII", Cost = 200, RowIndex = 11 },
                        new() { ItemId = 41758, Name = "Heavens' Eye Materia XI", Cost = 100, RowIndex = 12 },
                        new() { ItemId = 41771, Name = "Heavens' Eye Materia XII", Cost = 200, RowIndex = 13 },
                        new() { ItemId = 41768, Name = "Quickarm Materia XI", Cost = 100, RowIndex = 14 },
                        new() { ItemId = 41781, Name = "Quickarm Materia XII", Cost = 200, RowIndex = 15 },
                        new() { ItemId = 41769, Name = "Quicktongue Materia XI", Cost = 100, RowIndex = 16 },
                        new() { ItemId = 41782, Name = "Quicktongue Materia XII", Cost = 200, RowIndex = 17 },
                        new() { ItemId = 41761, Name = "Battledance Materia XI", Cost = 100, RowIndex = 18 },
                        new() { ItemId = 41774, Name = "Battledance Materia XII", Cost = 200, RowIndex = 19 },
                        new() { ItemId = 41757, Name = "Piety Materia XI", Cost = 100, RowIndex = 20 },
                        new() { ItemId = 41770, Name = "Piety Materia XII", Cost = 200, RowIndex = 21 },
                    ],
                },
            ],
        },
        new ShopCurrencyPageDefinition
        {
            MenuIndex = 2,
            MenuLabel = "Enlightenment Silver Piece Exchange (Other)",
            CurrencyItemId = 45043,
            CurrencyName = "Enlightenment Silver Piece",
            Tabs =
            [
                new ShopCurrencyTabDefinition
                {
                    TabId = 0,
                    TabLabel = "Weapons",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 1,
                    TabLabel = "Armor",
                    Items =
                    [
                        new() { ItemId = 47891, Name = "Lix Temple Chain", Cost = 1000, RowIndex = 10 },
                        new() { ItemId = 47892, Name = "Lix Chiton", Cost = 1000, RowIndex = 11 },
                        new() { ItemId = 47893, Name = "Lix Fingerless Gloves", Cost = 1000, RowIndex = 12 },
                        new() { ItemId = 47894, Name = "Lix Hose", Cost = 1000, RowIndex = 13 },
                        new() { ItemId = 47895, Name = "Lix Longboots", Cost = 1000, RowIndex = 14 },
                    ],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 2,
                    TabLabel = "Accessories",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 3,
                    TabLabel = "Other",
                    Items =
                    [
                        new() { ItemId = 48230, Name = "South Horn Riding Map", Cost = 3000, RowIndex = 0 },
                        new() { ItemId = 47975, Name = "Ancient Airship Identification Key", Cost = 5000, RowIndex = 1 },
                        new() { ItemId = 47972, Name = "Skallic Uolosapa", Cost = 600, RowIndex = 2 },
                        new() { ItemId = 49822, Name = "La Noscean Shorthair", Cost = 1000, RowIndex = 3 },
                        new() { ItemId = 48090, Name = "Occult Crescent Framer's Kit", Cost = 600, RowIndex = 4 },
                        new() { ItemId = 48206, Name = "Town Theme (Dawntrail) Orchestrion Roll", Cost = 1000, RowIndex = 5 },
                        new() { ItemId = 48207, Name = "A New World (Dawntrail) Orchestrion Roll", Cost = 1000, RowIndex = 6 },
                        new() { ItemId = 48144, Name = "Occult Crescent Map", Cost = 400, RowIndex = 7 },
                        new() { ItemId = 48139, Name = "Crescent Trophy", Cost = 400, RowIndex = 8 },
                        new() { ItemId = 50425, Name = "Mhachi Lamppost", Cost = 400, RowIndex = 9 },
                        new() { ItemId = 48157, Name = "Magicked Prism (Ribbons)", Cost = 1, RowIndex = 15 },
                    ],
                },
            ],
        },
        new ShopCurrencyPageDefinition
        {
            MenuIndex = 3,
            MenuLabel = "Enlightenment Gold Piece Exchange (Battlecraft Items)",
            CurrencyItemId = 45044,
            CurrencyName = "Enlightenment Gold Piece",
            Tabs =
            [
                new ShopCurrencyTabDefinition
                {
                    TabId = 0,
                    TabLabel = "Weapons",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 1,
                    TabLabel = "Armor",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 2,
                    TabLabel = "Accessories",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 3,
                    TabLabel = "Other",
                    Items =
                    [
                        new() { ItemId = 47753, Name = "Samurai's Soul Shard", Cost = 1600, RowIndex = 0 },
                        new() { ItemId = 47754, Name = "Geomancer's Soul Shard", Cost = 1600, RowIndex = 1 },
                        new() { ItemId = 48749, Name = "Thief's Soul Shard", Cost = 1600, RowIndex = 2 },
                        new() { ItemId = 49824, Name = "Gladiator's Soul Shard", Cost = 1600, RowIndex = 3 },
                        new() { ItemId = 47741, Name = "Occult Potion", Cost = 50, RowIndex = 4 },
                        new() { ItemId = 47740, Name = "Occult Coffer", Cost = 50, RowIndex = 5 },
                        new() { ItemId = 47865, Name = "Aetherial Fixative", Cost = 1600, RowIndex = 6 },
                        new() { ItemId = 41759, Name = "Savage Aim Materia XI", Cost = 160, RowIndex = 7 },
                        new() { ItemId = 41772, Name = "Savage Aim Materia XII", Cost = 320, RowIndex = 8 },
                        new() { ItemId = 41760, Name = "Savage Might Materia XI", Cost = 160, RowIndex = 9 },
                        new() { ItemId = 41773, Name = "Savage Might Materia XII", Cost = 320, RowIndex = 10 },
                        new() { ItemId = 41758, Name = "Heavens' Eye Materia XI", Cost = 160, RowIndex = 11 },
                        new() { ItemId = 41771, Name = "Heavens' Eye Materia XII", Cost = 320, RowIndex = 12 },
                        new() { ItemId = 41768, Name = "Quickarm Materia XI", Cost = 160, RowIndex = 13 },
                        new() { ItemId = 41781, Name = "Quickarm Materia XII", Cost = 320, RowIndex = 14 },
                        new() { ItemId = 41769, Name = "Quicktongue Materia XI", Cost = 160, RowIndex = 15 },
                        new() { ItemId = 41782, Name = "Quicktongue Materia XII", Cost = 320, RowIndex = 16 },
                        new() { ItemId = 41761, Name = "Battledance Materia XI", Cost = 160, RowIndex = 17 },
                        new() { ItemId = 41774, Name = "Battledance Materia XII", Cost = 320, RowIndex = 18 },
                        new() { ItemId = 41757, Name = "Piety Materia XI", Cost = 160, RowIndex = 19 },
                        new() { ItemId = 41770, Name = "Piety Materia XII", Cost = 320, RowIndex = 20 },
                    ],
                },
            ],
        },
        new ShopCurrencyPageDefinition
        {
            MenuIndex = 4,
            MenuLabel = "Enlightenment Gold Piece Exchange (Other)",
            CurrencyItemId = 45044,
            CurrencyName = "Enlightenment Gold Piece",
            Tabs =
            [
                new ShopCurrencyTabDefinition
                {
                    TabId = 0,
                    TabLabel = "Weapons",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 1,
                    TabLabel = "Armor",
                    Items =
                    [
                        new() { ItemId = 47896, Name = "Tycoon Hairpin", Cost = 1600, RowIndex = 3 },
                        new() { ItemId = 47897, Name = "Tycoon Leotard", Cost = 1600, RowIndex = 4 },
                        new() { ItemId = 47898, Name = "Tycoon Dress Gloves", Cost = 1600, RowIndex = 5 },
                        new() { ItemId = 47899, Name = "Tycoon Tights", Cost = 1600, RowIndex = 6 },
                        new() { ItemId = 47900, Name = "Tycoon Bootlets", Cost = 1600, RowIndex = 7 },
                        new() { ItemId = 47901, Name = "Scherwiz Hairpin", Cost = 1600, RowIndex = 11 },
                        new() { ItemId = 47902, Name = "Scherwiz Coat", Cost = 1600, RowIndex = 12 },
                        new() { ItemId = 47903, Name = "Scherwiz Vambraces", Cost = 1600, RowIndex = 13 },
                        new() { ItemId = 47904, Name = "Scherwiz Skirt", Cost = 1600, RowIndex = 14 },
                        new() { ItemId = 47905, Name = "Scherwiz Boots", Cost = 1600, RowIndex = 15 },
                    ],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 2,
                    TabLabel = "Accessories",
                    Items = [],
                },
                new ShopCurrencyTabDefinition
                {
                    TabId = 3,
                    TabLabel = "Other",
                    Items =
                    [
                        new() { ItemId = 49821, Name = "Gallant Shepherd", Cost = 1600, RowIndex = 0 },
                        new() { ItemId = 48204, Name = "Garden Relics Orchestrion Roll", Cost = 1600, RowIndex = 1 },
                        new() { ItemId = 48205, Name = "Garden Ruins Orchestrion Roll", Cost = 1600, RowIndex = 2 },
                        new() { ItemId = 48143, Name = "Knowledge Crystal Replica", Cost = 960, RowIndex = 8 },
                        new() { ItemId = 50423, Name = "Occult Compass", Cost = 960, RowIndex = 9 },
                        new() { ItemId = 50424, Name = "Occult Pyramicula", Cost = 960, RowIndex = 10 },
                    ],
                },
            ],
        },
    ];

    public static CurrencyShopData CreateSouthHornData(IReadOnlyList<CurrencyShopVendorDefinition> vendors)
        => new()
        {
            Vendors = vendors.ToList(),
            Pages = Pages.ToList(),
        };
}

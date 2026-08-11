namespace SpinTexture.Core.Services;

internal static class CharacterEquipmentArchiveCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>>
        MixedArchiveEquipmentTextures =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["lon11"] = TextureNames(
                    "11895_2_c.dds",
                    "11895_3_c.dds",
                    "11896_2_c.dds",
                    "11897_5_c.dds",
                    "11898_2_c.dds",
                    "11899_2_c.dds",
                    "11900_b_c.dds",
                    "11901_b_c.dds",
                    "11902_2_c.dds",
                    "sk_env.dds",
                    "temp_n.dds"),
                ["lon12"] = TextureNames("firedragon_weap.dds", "firedragon_weap_n.dds"),
                ["lon07"] = TextureNames(
                    "eq_combine_lon07_2_n.dds",
                    "eq_combine_lon07_n.dds",
                    "eq_combine_weapons01.dds",
                    "eq_combine_weapons02.dds",
                    "it11530_edge_glow_c.dds",
                    "it11531_scrapshield_c.dds",
                    "magi_shield_001.dds"),
                ["lon15"] = TextureNames("it12512_sword_c.dds"),
                ["lon16"] = TextureNames(
                    "sp_fearshard_axe_12ngs.dds",
                    "sp_fearshard_axe_c.dds"),
                ["wallet23"] = TextureNames(
                    "jack_faces_c.dds",
                    "jack_sword_c.dds",
                    "jack_sword_n.dds"),
                ["wallet15"] = TextureNames(
                    "it11620_c.dds",
                    "it11620_n.dds",
                    "it11621_c.dds",
                    "it11621_n.dds"),
                ["wallet17"] = TextureNames(
                    "wallet17_01.dds",
                    "wallet17_01nrml.dds",
                    "wallet17_02.dds",
                    "wallet17_02nrml.dds"),
                ["wallet22"] = TextureNames(
                    "morrel-thule-dagger_dif.dds",
                    "morrel-thule-dagger_nrm.dds",
                    "morrel-thule-lance_dif.dds",
                    "morrel-thule-lance_nrm.dds"),
                ["wallet26"] = TextureNames("blizz_weap01.dds", "blizz_weap01_n.dds"),
                ["wallet27"] = TextureNames(
                    "cupid_bow.dds",
                    "cupid_bow_n.dds",
                    "heartshield.dds",
                    "heartshield_n.dds"),
                ["wallet28"] = TextureNames(
                    "cb_wallplain_c.dds",
                    "cb_wallplain_dark_c.dds",
                    "chainwhip.dds",
                    "chainwhip_n.dds",
                    "clothfist.dds",
                    "erollisi_painting_n.dds",
                    "lute.dds",
                    "obsidian_n.dds",
                    "w28broken_glass.dds",
                    "w28brushed_metal.dds",
                    "w28brushed_metal_n.dds",
                    "w28_woodblood_c.dds"),
                ["wallet29"] = TextureNames(
                    "lute_w29.dds",
                    "lute_w29_n.dds",
                    "ralloszek_axe.dds",
                    "ralloszek_axe_n.dds"),
                ["wallet30"] = TextureNames(
                    "flat_n.dds",
                    "shield_w30.dds",
                    "shield_w30_n.dds",
                    "wood_w30.dds"),
                ["wallet33"] = TextureNames(
                    "dark_warrior_c.dds",
                    "dark_warrior_n.dds",
                    "rule_mess.dds",
                    "rule_mess_n.dds")
            };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>>
        ExcludedWorldTextures =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["global_chr"] = TextureNames("binside.bmp", "boatbot.bmp"),
                // Invisible-man actors use this all-transparent magenta DDS as
                // a renderer control/sentinel, not visible artwork. Re-encoding
                // it exposes the magenta error material on enchanter animations.
                ["ivm_chr"] = TextureNames("ivmhe0001.dds"),
                ["pre1_chr"] = TextureNames("precrate1.bmp", "precrate2.bmp"),
                ["donequip"] = TextureNames(
                    "i_glass01_e.dds",
                    "it10806_potion_red_c.dds",
                    "it10806_potion_red_n.dds",
                    "it10807_potion_blue_c.dds",
                    "it10808_potion_gold_c.dds"),
                ["hotequip"] = TextureNames(
                    "11835_c.dds",
                    "11835_n.dds",
                    "di_treasure_map01.dds",
                    "di_treasure_map01_6n.dds",
                    "hot_paintings_c.dds",
                    "it11880_tomato_c.dds",
                    "it11881_lettuce_c.dds",
                    "it11882_apple_c.dds"),
                ["sodequip"] = TextureNames("discus_c.dds", "discus_n.dds"),
                ["sofequip"] = TextureNames(
                    "black_crystal.dds",
                    "blue_crystal.dds",
                    "clear_crystal.dds",
                    "crystal_n.dds",
                    "di_ocean_rope_single.dds",
                    "di_ocean_rope_single_10n.dds",
                    "env_noswap_cave.dds",
                    "flowers_c.dds",
                    "flowers_n.dds",
                    "green_crystal.dds",
                    "moss_black_c.dds",
                    "moss_brown_c.dds",
                    "moss_green_brown_c.dds",
                    "moss_green_c.dds",
                    "moss_n.dds",
                    "orange_crystal.dds",
                    "orc_doll01_c.dds",
                    "orc_doll01_n.dds",
                    "pouch_coins_c.dds",
                    "pouch_coins_n.dds",
                    "pouch_leather_c.dds",
                    "pouch_leather_n.dds",
                    "prismatic_crystal.dds",
                    "purple_crystal.dds",
                    "red_crystal.dds",
                    "sof_trade_clayflinger_loop_c.dds",
                    "sof_trade_clayflinger_loop_n.dds",
                    "sof_trade_fletcher_goldenarrow_c.dds",
                    "sof_trade_fletcher_goldenarrow_n.dds",
                    "sof_trade_hovering_contraption_c.dds",
                    "sof_trade_hovering_contraption_n.dds",
                    "sof_trade_jeweler_eyeglass_c.dds",
                    "sof_trade_jeweler_eyeglass_n.dds",
                    "sof_trade_pestle_greengold_c.dds",
                    "sof_trade_pestle_greengold_n.dds",
                    "sof_trade_quill_bluefeather_c.dds",
                    "sof_trade_quill_bluefeather_n.dds",
                    "sphere_noise_c.dds",
                    "sphere_noise_n.dds",
                    "stem_leaf_c.dds",
                    "stem_leaf_n.dds",
                    "stone_idol_c.dds",
                    "stone_idol_n.dds",
                    "yellow_crystal.dds"),
                ["tbsequip"] = TextureNames(
                    "combine_coin_c.dds",
                    "combine_coin_n.dds",
                    "pirate_coin_c.dds",
                    "pirate_coin_n.dds",
                    "sunglow_yellow_c.dds"),
                ["tssequip"] = TextureNames(
                    "bb_brpaper_c.dds",
                    "bb_brpaper_n.dds",
                    "bb_paper_c.dds",
                    "bb_paper_n.dds",
                    "bb_rlpaper_c.dds",
                    "bb_rlpaper_n.dds",
                    "black_ash_c.dds",
                    "black_bones_c.dds",
                    "blackb_c.dds",
                    "brown_dirtp_c.dds",
                    "brown_dirtp_n.dds",
                    "brown_leaf_c.dds",
                    "brown_leaf_n.dds",
                    "brown_mushroom_c.dds",
                    "brown_rock_c.dds",
                    "brown_rock_n.dds",
                    "green_leaf_c.dds",
                    "green_leaf_n.dds",
                    "green_mushroom_c.dds",
                    "green_rock_c.dds",
                    "greyrock_c.dds",
                    "greyrock_n.dds",
                    "orange_mushroom_c.dds",
                    "orange_pmpkin_c.dds",
                    "palmfrond_c.dds",
                    "palmfrond_n.dds",
                    "purple_green_brown_book_c.dds",
                    "red_blue_brown_book_c.dds",
                    "red_clay_c.dds",
                    "red_leaf_c.dds",
                    "red_leaf_n.dds",
                    "red_rock_c.dds",
                    "redb_c.dds",
                    "shrub_leaves.dds",
                    "shrub_leaves_n.dds",
                    "white_bones_c.dds",
                    "white_bones_n.dds",
                    "white_mushroom_c.dds"),
                ["undequip"] = TextureNames(
                    "11527brush_c.dds",
                    "boomerang.dds",
                    "boomerang1_n.dds",
                    "it11526_toolbox_c.dds",
                    "it11528_ore_chest_c.dds",
                    "it11528_ore_chest_n.dds",
                    "it11529_stone_block_c.dds",
                    "it11529_stone_block_n.dds"),
                ["oot_chr"] = TextureNames(
                    "aftwall.bmp",
                    "arm2.bmp",
                    "boatside.bmp",
                    "boatside2.bmp",
                    "bow.bmp",
                    "bow1.bmp",
                    "brow.bmp",
                    "cabin.bmp",
                    "cabin1.bmp",
                    "deck.bmp",
                    "dhceiling.bmp",
                    "dhwall.bmp",
                    "eye.bmp",
                    "mast.bmp",
                    "mast1.bmp",
                    "mhole.bmp",
                    "precrate1.bmp",
                    "precrate2.bmp",
                    "sail.bmp",
                    "stern.bmp",
                    "winder.bmp")
            };

    private static readonly string[] CharacterRoleTerms =
    [
        "character",
        "equipment",
        "weapon",
        "armor",
        "robe",
        "helm"
    ];

    private static readonly string[] WorldRoleTerms =
    [
        "furniture",
        "flora",
        "terrain",
        "world object"
    ];

    public static IReadOnlyList<string> Discover(
        string installPath,
        IReadOnlyList<string> allArchives)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        ArgumentNullException.ThrowIfNull(allArchives);

        var root = Path.GetFullPath(installPath);
        var archivePathsByStem = allArchives
            .Select(Path.GetFullPath)
            .GroupBy(
                path => Path.GetFileNameWithoutExtension(path),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var selectedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitlyWorldStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actorTags = ReadActorTags(
            Path.Combine(root, "Resources", "actortaginfo.txt"));

        ReadGlobalLoadCatalog(
            Path.Combine(root, "Resources", "GlobalLoad.txt"),
            selectedStems,
            explicitlyWorldStems);

        foreach (var catalogPath in EnumerateCharacterCatalogs(root))
        {
            ReadCharacterCatalog(catalogPath, selectedStems);
        }

        var hasRaceData = ReadRaceData(Path.Combine(root, "racedata.txt"), selectedStems);
        ReadArmorRaceData(
            Path.Combine(root, "Resources", "NewArmorAdjData.txt"),
            selectedStems);
        ReadArmorLayerCatalogs(
            Path.Combine(root, "Resources", "Layers"),
            selectedStems);
        foreach (var mixedArchiveStem in MixedArchiveEquipmentTextures.Keys)
        {
            if (archivePathsByStem.ContainsKey(mixedArchiveStem))
            {
                selectedStems.Add(mixedArchiveStem);
            }
        }

        foreach (var archivePath in allArchives)
        {
            var stem = Path.GetFileNameWithoutExtension(archivePath);
            if (IsKnownWorldOnlyArchive(archivePath))
            {
                explicitlyWorldStems.Add(stem);
                continue;
            }

            if (actorTags is not null
                && TryGetStandaloneItemId(stem, out var itemId)
                && actorTags.TryGetValue(itemId, out var attachmentTag)
                && attachmentTag >= 0)
            {
                selectedStems.Add(stem);
                continue;
            }

            if ((IsDedicatedCharacterOrEquipmentArchive(archivePath)
                    || (!hasRaceData && IsCompactEqgModelArchive(archivePath)))
                && !explicitlyWorldStems.Contains(stem))
            {
                selectedStems.Add(stem);
            }
        }

        selectedStems.ExceptWith(explicitlyWorldStems);

        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stem in selectedStems)
        {
            AddArchivePaths(stem, archivePathsByStem, selectedPaths);
            AddArchivePaths(stem + "_anims", archivePathsByStem, selectedPaths);
            AddArchivePaths(stem + "_chr", archivePathsByStem, selectedPaths);
            AddArchivePaths("global" + stem + "_chr", archivePathsByStem, selectedPaths);
        }

        selectedPaths.RemoveWhere(path =>
            explicitlyWorldStems.Contains(Path.GetFileNameWithoutExtension(path)));

        return selectedPaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsDedicatedCharacterOrEquipmentArchive(string path)
    {
        if (IsKnownWorldOnlyArchive(path))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(path);

        if (name.Contains("_chr", StringComparison.OrdinalIgnoreCase)
            || name.Contains("equip", StringComparison.OrdinalIgnoreCase)
            || name.Contains("weapon", StringComparison.OrdinalIgnoreCase)
            || name.Contains("armor", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("robe", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("psrobe", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("helm", StringComparison.OrdinalIgnoreCase)
            || name.Contains("shield", StringComparison.OrdinalIgnoreCase)
            || name.Contains("sword", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("axe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsAuditedStandaloneEquipmentArchive(name);
    }

    internal static bool ShouldClampArchiveSampling(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return IsKnownWorldOnlyArchive(path)
            || name.Contains("_chr", StringComparison.OrdinalIgnoreCase)
            || IsDedicatedCharacterOrEquipmentArchive(path)
            || name.StartsWith("global", StringComparison.OrdinalIgnoreCase)
            || HasNumberedPrefix(name, "it")
            || HasNumberedPrefix(name, "wallet")
            || HasNumberedPrefix(name, "lon");
    }

    internal static bool IsTextureEntryAllowed(string archivePath, string textureName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(textureName);

        var archiveName = Path.GetFileNameWithoutExtension(archivePath);
        var textureFileName = Path.GetFileName(textureName);
        if (MixedArchiveEquipmentTextures.TryGetValue(archiveName, out var allowlist)
            && !allowlist.Contains(textureFileName))
        {
            return false;
        }

        if (ExcludedWorldTextures.TryGetValue(archiveName, out var denylist)
            && denylist.Contains(textureFileName))
        {
            return false;
        }

        return !archiveName.Equals("oot_chr", StringComparison.OrdinalIgnoreCase)
            || !textureFileName
                .StartsWith("ghost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCompactEqgModelArchive(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        // Standalone EQG race/model packs conventionally use the actor's compact
        // three-character code. racedata.txt and *_chr.txt provide the primary
        // evidence; this fallback keeps those packs discoverable on older clients.
        return extension.Equals(".eqg", StringComparison.OrdinalIgnoreCase)
            && name.Length == 3
            && name.All(char.IsLetterOrDigit);
    }

    private static bool IsKnownWorldOnlyArchive(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("holeequip", StringComparison.OrdinalIgnoreCase)
            || name.Equals("obequip_lexit", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dest_sphere_shield", StringComparison.OrdinalIgnoreCase)
            || name.Equals("boat_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("box_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("och_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ship1_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ship2_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ship3_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tpn_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("gsp_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("brl_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tbl_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cpt_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("shp_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("btp_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cst", StringComparison.OrdinalIgnoreCase)
            || name.Equals("prt", StringComparison.OrdinalIgnoreCase)
            || name.Equals("rkp", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bnr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("row", StringComparison.OrdinalIgnoreCase)
            || name.Equals("t00", StringComparison.OrdinalIgnoreCase)
            || name.Equals("t10", StringComparison.OrdinalIgnoreCase)
            || name.Equals("shi", StringComparison.OrdinalIgnoreCase)
            || name.Equals("i10", StringComparison.OrdinalIgnoreCase)
            || name.Equals("g05", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bnx", StringComparison.OrdinalIgnoreCase)
            || name.Equals("i21", StringComparison.OrdinalIgnoreCase)
            || name.Equals("b09", StringComparison.OrdinalIgnoreCase)
            || name.Equals("b10", StringComparison.OrdinalIgnoreCase)
            || name.Equals("i25", StringComparison.OrdinalIgnoreCase)
            || name.Equals("i35", StringComparison.OrdinalIgnoreCase)
            || name.Equals("chs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("erudsxing_chr2", StringComparison.OrdinalIgnoreCase)
            || name.Equals("l01_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("l04_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("l06_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("l07_chr", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pre2_chr", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateCharacterCatalogs(string root)
    {
        IEnumerable<string> topLevelCatalogs;
        try
        {
            topLevelCatalogs = Directory.EnumerateFiles(root, "*.txt", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path)
                    .EndsWith("_chr.txt", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            topLevelCatalogs = [];
        }

        foreach (var catalogPath in topLevelCatalogs)
        {
            yield return catalogPath;
        }

        var globalCharacterCatalog = Path.Combine(root, "Resources", "GlobalLoad_chr.txt");
        if (File.Exists(globalCharacterCatalog))
        {
            yield return globalCharacterCatalog;
        }
    }

    private static void ReadCharacterCatalog(string path, ISet<string> selectedStems)
    {
        foreach (var line in TryReadAllLines(path))
        {
            var fields = line.Split(',');
            if (fields.Length < 2)
            {
                continue;
            }

            AddNormalizedStem(fields[1], selectedStems);
        }
    }

    private static void ReadGlobalLoadCatalog(
        string path,
        ISet<string> selectedStems,
        ISet<string> explicitlyWorldStems)
    {
        foreach (var line in TryReadAllLines(path))
        {
            var fields = line.Split(',', 5);
            if (fields.Length < 5)
            {
                continue;
            }

            var stem = NormalizeStem(fields[3]);
            if (stem is null)
            {
                continue;
            }

            var description = fields[4];
            if (WorldRoleTerms.Any(term =>
                    description.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                explicitlyWorldStems.Add(stem);
            }
            else if (CharacterRoleTerms.Any(term =>
                         description.Contains(term, StringComparison.OrdinalIgnoreCase))
                     && !IsAmbiguousMixedArchiveStem(stem))
            {
                selectedStems.Add(stem);
            }
        }
    }

    private static bool ReadRaceData(string path, ISet<string> selectedStems)
    {
        var foundModelCode = false;
        foreach (var line in TryReadAllLines(path))
        {
            var fields = line.Split('^');
            if (fields.Length <= 50)
            {
                continue;
            }

            var modelCode = fields[50].Trim();
            if (modelCode.Length is < 2 or > 16
                || modelCode.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                || !modelCode.All(character =>
                    char.IsLetterOrDigit(character) || character == '_'))
            {
                continue;
            }

            selectedStems.Add(modelCode);
            foundModelCode = true;
        }

        return foundModelCode;
    }

    private static Dictionary<int, int>? ReadActorTags(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var actorTags = new Dictionary<int, int>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var firstSeparator = line.IndexOf('^');
                if (firstSeparator <= 0)
                {
                    continue;
                }

                var secondSeparator = line.IndexOf('^', firstSeparator + 1);
                if (secondSeparator <= firstSeparator + 1
                    || !int.TryParse(line.AsSpan(0, firstSeparator), out var itemId)
                    || !int.TryParse(
                        line.AsSpan(
                            firstSeparator + 1,
                            secondSeparator - firstSeparator - 1),
                        out var attachmentTag))
                {
                    continue;
                }

                actorTags[itemId] = attachmentTag;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return actorTags.Count != 0 ? actorTags : null;
    }

    private static void ReadArmorRaceData(string path, ISet<string> selectedStems)
    {
        foreach (var line in TryReadAllLines(path))
        {
            var separator = line.IndexOf(',');
            if (separator <= 0)
            {
                continue;
            }

            AddNormalizedStem(line[..separator], selectedStems);
        }
    }

    private static void ReadArmorLayerCatalogs(string directory, ISet<string> selectedStems)
    {
        IEnumerable<string> catalogPaths;
        try
        {
            catalogPaths = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetFileNameWithoutExtension(path)
                        .EndsWith("_IT", StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            catalogPaths = [];
        }

        foreach (var catalogPath in catalogPaths)
        {
            var name = Path.GetFileNameWithoutExtension(catalogPath);
            selectedStems.Add(name[..^"_IT".Length]);
        }
    }

    private static IReadOnlyList<string> TryReadAllLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void AddNormalizedStem(string value, ISet<string> selectedStems)
    {
        var stem = NormalizeStem(value);
        if (stem is not null)
        {
            selectedStems.Add(stem);
        }
    }

    private static string? NormalizeStem(string value)
    {
        var fileName = Path.GetFileName(value.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return EverQuestInstall.IsPfsArchiveExtension(Path.GetExtension(fileName))
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
    }

    private static void AddArchivePaths(
        string stem,
        IReadOnlyDictionary<string, string[]> archivePathsByStem,
        ISet<string> selectedPaths)
    {
        if (!archivePathsByStem.TryGetValue(stem, out var paths))
        {
            return;
        }

        foreach (var path in paths)
        {
            selectedPaths.Add(path);
        }
    }

    private static bool HasNumberedPrefix(string name, string prefix) =>
        name.Length > prefix.Length
        && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && char.IsDigit(name[prefix.Length]);

    private static bool TryGetStandaloneItemId(string name, out int itemId)
    {
        itemId = 0;
        return name.StartsWith("it", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(2), out itemId);
    }

    private static bool IsAuditedStandaloneEquipmentArchive(string name)
    {
        // Numeric IT and wallet archives are otherwise mixed with furniture and
        // housing assets. Keep this list intentionally narrow: these ranges were
        // verified as dedicated weapon packs in the supported client.
        if (name.StartsWith("it", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(name.AsSpan(2), out var itemArchiveId))
        {
            return itemArchiveId is >= 13900 and <= 13998
                or >= 100000 and <= 100187;
        }

        return name.Equals("lon02", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bloodiron_green", StringComparison.OrdinalIgnoreCase)
            || name.Equals("bloodiron_red", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lon03", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lon04", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lon05", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lon06", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lon08", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lon10", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lon13", StringComparison.OrdinalIgnoreCase)
            || name.Equals("lon14", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet01", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet02", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet03", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet06", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet08", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet10", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet11", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet12", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet13", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet14", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet16", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet18", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet19", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet20", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet21", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet24", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet25", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet32", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet34", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet35", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet37", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet39", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet40", StringComparison.OrdinalIgnoreCase)
            || name.Equals("wallet41", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAmbiguousMixedArchiveStem(string name)
    {
        if (name.StartsWith("global", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("_chr", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (HasNumberedPrefix(name, "it")
                || HasNumberedPrefix(name, "wallet"))
            && !IsAuditedStandaloneEquipmentArchive(name);
    }

    private static IReadOnlySet<string> TextureNames(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
}

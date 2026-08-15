namespace SpinTexture.Core.Textures;

/// <summary>
/// A per-file art-direction hint for the external diffusion worker. The
/// suffix is appended to the active style prompt; the denoise scale
/// multiplies the configured denoise strength (values below one keep the
/// repaint closer to the reconstructed surface).
/// </summary>
public sealed record DiffusionFileDirective(
    string? PromptSuffix,
    double DenoiseScale)
{
    public bool IsDefault => PromptSuffix is null && DenoiseScale == 1d;
}

/// <summary>
/// Composes deterministic per-texture prompt hints for the diffusion repaint
/// from two safe signals: material vocabulary inferred from the texture's
/// own file name, and a reviewed per-zone flavor for known zone archives.
/// The composition is a pure function of the name and zone stem, so repairs
/// and resumed builds reproduce the identical prompt for every texture.
/// </summary>
public static class DiffusionPromptComposer
{
    /// <summary>
    /// Fluid, fire, and glow surfaces are repainted more conservatively:
    /// many of them are frames of the client's texture animations, and a
    /// restrained denoise keeps consecutive frames coherent so water, lava,
    /// and flame keep flowing instead of shimmering between looks.
    /// </summary>
    public const double CoherentSurfaceDenoiseScale = 0.75;

    private sealed record MaterialRule(string[] Tokens, string Suffix, double DenoiseScale = 1d);

    // Ordered by specificity: the first matching rule wins. Tokens are
    // matched as substrings of the lower-cased file name because classic
    // texture names are terse compounds ("stonewall2", "lavarock") that
    // defeat delimiter tokenization.
    private static readonly MaterialRule[] MaterialRules =
    [
        new(["lava", "magma", "molten"], "glowing molten rock, ember-lit cracks", CoherentSurfaceDenoiseScale),
        new(["water", "wave", "ocean", "river", "pool", "falls"], "painted water surface, clear liquid color", CoherentSurfaceDenoiseScale),
        new(["fire", "flame", "torch", "brazier", "candle"], "painted flame, warm radiant glow", CoherentSurfaceDenoiseScale),
        new(["swirl", "portal", "glow"], "luminous painted energy", CoherentSurfaceDenoiseScale),
        new(["grass", "lawn", "meadow"], "painted grass, organic clumped blades"),
        new(["leaf", "leaves", "tree", "vine", "bush", "fern", "moss", "shrub", "branch", "foliage"], "painted foliage, layered leafy planes"),
        new(["brick", "masonry", "mortar"], "weathered painted brick, defined mortar lines"),
        new(["marble", "granite"], "polished painted stone, veined mineral color"),
        new(["stone", "rock", "boulder", "cobble", "cliff", "cave"], "weathered painted stone, carved chiseled detail"),
        new(["wood", "plank", "bark", "beam", "timber"], "painted wood, visible carved grain"),
        new(["door", "gate", "fence"], "sturdy painted craftsmanship, defined edges"),
        new(["dirt", "mud", "soil", "ground", "path", "gravel"], "painted earthen ground, varied soil tones"),
        new(["sand", "dune", "beach", "desert"], "painted sand, soft wind-swept ripples"),
        new(["snow", "ice", "frost", "frozen", "glacier"], "painted snow and ice, cool crisp highlights"),
        new(["plate", "armor", "cuirass", "breastplate", "pauldron", "gauntlet", "greave", "helm", "shield"], "brilliant polished plate armor, gleaming metallic sheen, sharp specular highlights, painted steel"),
        new(["chain"], "painted chainmail, glinting interlocked steel rings"),
        new(["metal", "iron", "steel", "bronze", "brass", "grate", "anvil"], "burnished painted metal, bright worn edges, glinting highlights"),
        new(["leather", "strap", "buckle"], "painted leather, supple worn grain, warm rich tone"),
        new(["roof", "shingle", "thatch", "slate"], "painted rooftop shingles, layered rows"),
        new(["cloth", "banner", "flag", "rug", "carpet", "tapestry", "curtain", "tent", "sail", "robe", "tunic", "cape", "cloak"], "painted woven cloth, soft fabric folds, rich dyed color"),
        new(["bone", "skull"], "aged painted bone, dry ivory tones"),
        new(["fur", "pelt", "hide"], "painted fur, directional brushed strands"),
        new(["crystal", "gem", "jewel"], "glowing painted crystal, faceted color"),
        new(["web"], "pale painted spider silk strands")
    ];

    // Reviewed zone flavors, keyed by the normalized zone archive stem the
    // painted theme resolver already derives. Unknown archives simply get no
    // zone clause; this is a curated map, not generative lore.
    private static readonly IReadOnlyDictionary<string, string> ZoneFlavors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["neriaka"] = "dark elven gothic architecture, deep violet and obsidian accents",
            ["neriakb"] = "dark elven gothic architecture, deep violet and obsidian accents",
            ["neriakc"] = "dark elven gothic architecture, deep violet and obsidian accents",
            ["nektulos"] = "shadowed pine forest, dark mossy undergrowth",
            ["lavastorm"] = "volcanic wasteland, ember glow on scorched rock",
            ["soldunga"] = "volcanic cavern, heat-cracked stone",
            ["soldungb"] = "volcanic cavern, heat-cracked stone",
            ["befallen"] = "haunted crypt, cold moonlit stone",
            ["unrest"] = "haunted estate, decayed grandeur",
            ["mistmoore"] = "vampiric castle, gothic moonlit masonry",
            ["fearplane"] = "nightmare plane, unsettling saturated color",
            ["hateplane"] = "malevolent plane, ominous crimson and iron",
            ["hateplaneb"] = "malevolent plane, ominous crimson and iron",
            ["guktop"] = "ancient swamp ruins, wet mossy stone",
            ["gukbottom"] = "flooded swamp ruins, algae-slick stone",
            ["innothule"] = "murky swamp, tangled roots and peat",
            ["grobb"] = "troll mud warren, crude swamp timber",
            ["oggok"] = "ogre mud city, massive crude stonework",
            ["najena"] = "torchlit dungeon, dark worked stone",
            ["paineel"] = "sinister arcane city, dark polished marble",
            ["felwithea"] = "graceful high elven architecture, silver and leaf-green elegance",
            ["felwitheb"] = "graceful high elven architecture, silver and leaf-green elegance",
            ["gfaydark"] = "ancient mossy elven forest, dappled emerald light",
            ["kelethin"] = "elven treetop platforms, living wood",
            ["crushbone"] = "crude orc war camp, rough stained timber",
            ["kaladima"] = "dwarven stone halls, precise carved granite",
            ["kaladimb"] = "dwarven stone halls, precise carved granite",
            ["akanon"] = "gnomish clockwork city, brass fittings on stone",
            ["rivervale"] = "cozy storybook halfling village, warm rounded forms",
            ["misty"] = "misty storybook thicket, soft pastoral color",
            ["qeynos"] = "warm cobblestone harbor city, sunlit plaster and timber",
            ["qeynos2"] = "warm cobblestone harbor city, sunlit plaster and timber",
            ["qeytoqrg"] = "pastoral farmland hills, warm country light",
            ["qrg"] = "quiet farming village, rustic timber and thatch",
            ["qey2hh1"] = "windswept golden plains, open sky",
            ["eastkarana"] = "windswept golden plains, open sky",
            ["northkarana"] = "windswept golden plains, open sky",
            ["southkarana"] = "windswept golden plains, open sky",
            ["westkarana"] = "windswept golden plains, open sky",
            ["halas"] = "frozen northern village, snowbound timber lodges",
            ["everfrost"] = "arctic tundra, wind-carved snow and ice",
            ["permafrost"] = "glacial ice caverns, deep blue frost",
            ["freporte"] = "grand human port city, warm imposing sandstone",
            ["freportn"] = "grand human port city, warm imposing sandstone",
            ["freportw"] = "grand human port city, warm imposing sandstone",
            ["highkeep"] = "fortified mountain keep, dressed defensive stone",
            ["highpass"] = "rugged mountain pass, hardy frontier timber",
            ["erudnext"] = "alabaster arcane city, pale enchanted stone",
            ["erudnint"] = "alabaster arcane city, pale enchanted stone",
            ["oasis"] = "sun-bleached desert oasis, ochre sand and palm shade",
            ["nro"] = "sun-bleached desert, wind-scoured sandstone",
            ["sro"] = "sun-bleached desert, wind-scoured sandstone"
        };

    public static DiffusionFileDirective Compose(string sourceFileName, string? zoneArchiveStem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileName);
        var name = Path.GetFileNameWithoutExtension(sourceFileName).ToLowerInvariant();

        string? materialSuffix = null;
        var denoiseScale = 1d;
        foreach (var rule in MaterialRules)
        {
            if (rule.Tokens.Any(token => name.Contains(token, StringComparison.Ordinal)))
            {
                materialSuffix = rule.Suffix;
                denoiseScale = rule.DenoiseScale;
                break;
            }
        }

        string? zoneFlavor = null;
        if (!string.IsNullOrWhiteSpace(zoneArchiveStem))
        {
            ZoneFlavors.TryGetValue(zoneArchiveStem.Trim(), out zoneFlavor);
        }

        var suffix = (materialSuffix, zoneFlavor) switch
        {
            (null, null) => null,
            (not null, null) => materialSuffix,
            (null, not null) => zoneFlavor,
            _ => $"{materialSuffix}, {zoneFlavor}"
        };
        return new DiffusionFileDirective(suffix, denoiseScale);
    }
}

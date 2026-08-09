using Godot;

// ============================================================
// UITheme.cs
//
// Purpose:        Single source of truth for every UI color,
//                 font size, padding value, animation duration,
//                 and panel-style helper used in the project.
//                 Anywhere else in the codebase: read from here,
//                 don't hardcode.
// Layer:          UI
// Collaborators:  Read by every file under Scripts/UI/, Scripts/
//                 Tiles/, Scripts/Systems/Overworld/,
//                 Scripts/Systems/Campus/, Scripts/Systems/
//                 Negotiation/, and CombatUI/CardUi
// See:            README §3 — visual identity is one of the
//                 game's three design pillars
// ============================================================
//
// Visual identity reminders for new contributors:
//   - 3D world (tiles, units, overworld map) — deep purple-black.
//   - UI chrome (panels, campus, modals, negotiation) — dark
//     blue-grey slate. Near-white text on all panel surfaces.
//   - Card faces — warm light parchment (SurfaceLight). Cards
//     should read as physical objects on top of the slate UI.
//   - Accents — violet borders, gold titles, arcane blue mana.

/// <summary>Project-wide theme tokens. Every UI surface, panel, button, label, and animation duration reads from this class. Member-level docs are intentionally omitted for the color/size constants — the names + the section banners below carry the meaning. If you find yourself hardcoding a color or padding value in another file, add a token here instead.</summary>
public static class UITheme
{
    // ════════════════════════════════════════════════════════════
    // CORE PALETTE
    // ════════════════════════════════════════════════════════════

    // ── World backgrounds (3D battlefield, overworld map) ────────
    public static readonly Color WorldDeep = new Color(0.08f, 0.06f, 0.10f, 1f); // #141018
    public static readonly Color WorldBase = new Color(0.12f, 0.09f, 0.16f, 1f); // #1E1728
    public static readonly Color WorldOverlay = new Color(0.00f, 0.00f, 0.00f, 0.65f);

    // ── Slate UI chrome ───────────────────────────────────────────
    public static readonly Color BgDeep = new Color(0.10f, 0.11f, 0.16f, 1f); // #1A1C29 — deepest slate
    public static readonly Color BgBase = new Color(0.13f, 0.15f, 0.22f, 1f); // #212538 — panel bg
    public static readonly Color BgRaised = new Color(0.17f, 0.19f, 0.28f, 1f); // #2B3047 — raised elements
    public static readonly Color BgOverlay = new Color(0.00f, 0.00f, 0.00f, 0.72f);
    public static readonly Color BgCard = new Color(0.20f, 0.22f, 0.32f, 1f); // #333852 — lighter than BgBase, used for cards and campus elements to stand out against panels

    // ── Accent purple ─────────────────────────────────────────────
    public static readonly Color Violet = new Color(0.48f, 0.30f, 0.82f, 1f); // #7A4DD1
    public static readonly Color VioletDim = new Color(0.32f, 0.20f, 0.55f, 1f); // #52338C
    public static readonly Color VioletDark = new Color(0.20f, 0.13f, 0.35f, 1f); // #332159

    // ── Button fills — solid so they pop against slate ────────────
    public static readonly Color ButtonPrimary = new Color(0.36f, 0.22f, 0.65f, 1f); // #5C38A6
    public static readonly Color ButtonPrimaryHover = new Color(0.44f, 0.28f, 0.76f, 1f); // #7048C2
    public static readonly Color ButtonSecondary = new Color(0.18f, 0.20f, 0.30f, 1f); // #2E3350
    public static readonly Color ButtonDanger = new Color(0.55f, 0.14f, 0.14f, 1f); // #8C2424

    // ── Gold ──────────────────────────────────────────────────────
    public static readonly Color Gold = new Color(0.88f, 0.72f, 0.20f, 1f); // #E0B833
    public static readonly Color GoldDim = new Color(0.60f, 0.48f, 0.12f, 1f); // #997A1F
    public static readonly Color GoldDark = new Color(0.35f, 0.28f, 0.06f, 1f); // #59470F

    // ── Magic blue ────────────────────────────────────────────────
    public static readonly Color ArcaneBlue = new Color(0.28f, 0.55f, 0.92f, 1f); // #478CEB
    public static readonly Color ArcaneBlueDim = new Color(0.18f, 0.36f, 0.62f, 1f); // #2E5C9E
    public static readonly Color ArcaneBlueDark = new Color(0.10f, 0.20f, 0.38f, 1f); // #1A3361

    // ── Semantic ──────────────────────────────────────────────────
    public static readonly Color Success = new Color(0.22f, 0.72f, 0.38f, 1f); // #38B861
    public static readonly Color SuccessDim = new Color(0.14f, 0.45f, 0.24f, 1f); // #24733D
    public static readonly Color Danger = new Color(0.85f, 0.25f, 0.25f, 1f); // #D94040
    public static readonly Color DangerDim = new Color(0.60f, 0.18f, 0.18f, 1f); // #992E2E
    public static readonly Color Warning = new Color(0.90f, 0.60f, 0.12f, 1f); // #E6991F
    public static readonly Color WarningDim = new Color(0.60f, 0.40f, 0.08f, 1f); // #996614

    // ── Text on slate surfaces ────────────────────────────────────
    public static readonly Color TextPrimary = new Color(0.92f, 0.90f, 0.96f, 1f); // #EBE6F5 near-white
    public static readonly Color TextSecondary = new Color(0.68f, 0.64f, 0.80f, 1f); // #ADA3CC soft lavender
    public static readonly Color TextDim = new Color(0.48f, 0.45f, 0.58f, 1f); // #7A7394 muted
    public static readonly Color TextOnDark = new Color(0.92f, 0.90f, 0.96f, 1f); // same as primary
    public static readonly Color TextOnLight = new Color(0.12f, 0.10f, 0.18f, 1f); // for card faces

    // ── Neutral ───────────────────────────────────────────────────
    public static readonly Color Neutral = new Color(0.32f, 0.30f, 0.42f, 1f);
    public static readonly Color NeutralDim = new Color(0.22f, 0.20f, 0.30f, 1f);

    // ════════════════════════════════════════════════════════════
    // SURFACE ALIASES
    // ════════════════════════════════════════════════════════════
    // SurfaceLight is reserved for card faces only.
    public static readonly Color SurfaceLight = new Color(0.96f, 0.94f, 0.88f, 1f); // #F5F0E0
    public static readonly Color SurfaceMid = BgBase;
    public static readonly Color SurfaceDark = BgDeep;
    public static readonly Color SurfaceOverlay = BgOverlay;

    // ════════════════════════════════════════════════════════════
    // CARD UI
    // ════════════════════════════════════════════════════════════
    public static readonly Color CardTopActive = new Color(1.00f, 0.96f, 0.78f, 1f);
    public static readonly Color CardBottomActive = new Color(0.82f, 0.80f, 1.00f, 1f);
    public static readonly Color CardDim = new Color(0.55f, 0.52f, 0.60f, 1f);
    // §7c reaction window: halves that cannot respond (not Reflex speed) render
    // darkened + desaturated — deader than CardDim so "locked" ≠ "unhovered".
    public static readonly Color CardReactionLocked = new Color(0.38f, 0.38f, 0.42f, 1f);
    public static readonly Color CardDragGhost = new Color(0.92f, 0.90f, 0.96f, 0.88f);

    // ── Rarity colors ─────────────────────────────────────────────
    public static readonly Color RarityCommon = new Color(0.35f, 0.33f, 0.38f, 1f); // darker grey
    public static readonly Color RarityUncommon = new Color(0.08f, 0.52f, 0.22f, 1f); // darker green  
    public static readonly Color RarityRare = new Color(0.12f, 0.32f, 0.75f, 1f); // darker blue
    public static readonly Color RarityLegendary = new Color(0.65f, 0.42f, 0.05f, 1f); // darker gold

    public static Color GetRarityColor(CardRarity rarity) => rarity switch
    {
        CardRarity.Common => RarityCommon,
        CardRarity.Uncommon => RarityUncommon,
        CardRarity.Rare => RarityRare,
        CardRarity.Legendary => RarityLegendary,
        _ => RarityCommon,
    };

    public static Color RarityColor(string rarity) => rarity switch
    {
        "Common" => RarityCommon,
        "Uncommon" => RarityUncommon,
        "Rare" => RarityRare,
        "Legendary" => RarityLegendary,
        _ => RarityCommon,
    };

    public const int CardBorderWidth = 4;
    public const int CardCornerRadius = 10;

    // ════════════════════════════════════════════════════════════
    // HEALTH BAR / UNIT STATS
    // ════════════════════════════════════════════════════════════
    public static readonly Color HealthGreen = new Color(0.18f, 0.72f, 0.35f, 1f);
    public static readonly Color ManaBlue = ArcaneBlue;
    public static readonly Color ArmorGrey = new Color(0.55f, 0.55f, 0.62f, 1f);
    public static readonly Color ShieldPurple = new Color(0.50f, 0.25f, 0.85f, 0.80f);
    public const float HealthBarWidth = 1.6f;
    public static readonly Color APPipFull = new Color(0.9f, 0.8f, 0.2f);
    public static readonly Color APPipEmpty = new Color(0.25f, 0.25f, 0.25f);

    // ════════════════════════════════════════════════════════════
    // TILE HIGHLIGHTS
    // ════════════════════════════════════════════════════════════
    public static readonly Color TileHover = new Color(1.0f, 0.92f, 0.50f, 1f);
    public static readonly Color TileDragHover = new Color(0.90f, 0.80f, 0.25f, 1f);
    public static readonly Color TileMoveHighlight = new Color(0.30f, 0.65f, 1.00f, 1f);
    public static readonly Color TileDeployHighlight = new Color(0.30f, 1.00f, 0.40f, 1f);
    public static readonly Color TileTargetHighlight = new Color(1.00f, 0.38f, 0.38f, 1f);
    public static readonly Color TileRangeInterior = new Color(0.70f, 0.48f, 1.00f, 0.70f);
    public static readonly Color TileRangeBorder = new Color(0.55f, 0.38f, 0.90f, 0.25f);
    public static readonly Color TileGlyph = new Color(0.65f, 0.25f, 1.00f, 1f);

    // ════════════════════════════════════════════════════════════
    // GLYPH CIPHER (docs/glyph_cipher_spec_v2.md §7)
    // Two layers that must stay separable for a protanopic player.
    // Hue is the SECONDARY channel: the function layer is drawn at
    // 1.88x the identity layer's stroke weight (~3x at tile scale),
    // and it is a hub-and-spokes where the identity layer is
    // arms-and-ticks — so shape carries the split even if colour
    // fails entirely. Measured contrast ink/function: 4.57:1 normal,
    // 3.79:1 protanopic.
    // ════════════════════════════════════════════════════════════
    /// <summary>Identity layer (the encoded Name) on card stock.</summary>
    public static readonly Color CipherInk = new Color(0.102f, 0.086f, 0.078f, 1f);      // #1A1614

    /// <summary>Identity layer over the dark combat board.</summary>
    public static readonly Color CipherInkLight = new Color(0.929f, 0.894f, 0.827f, 1f); // #EDE4D3

    /// <summary>Function layer (hub + spokes). The Enchanter border rose.</summary>
    public static readonly Color CipherFunction = new Color(0.769f, 0.357f, 0.620f, 1f); // #C45B9E

    /// <summary>Default punch colour for the ALLY hub, which is a filled disc with its
    /// centre removed. Callers drawing over a different background should set
    /// GlyphCipherView.PaperColor to match it.</summary>
    public static readonly Color CipherPaper = new Color(0.910f, 0.875f, 0.808f, 1f);    // #E8DFCE

    /// <summary>Backing disc behind the hex-tile decal. Alpha here is the full-strength
    /// value; the LOD profile scales it. Without a backing the sigil is composited
    /// straight onto whatever terrain the tile happens to be — grass, sand, stone, water —
    /// and no single ink alpha is legible across all of them. A controlled backdrop makes
    /// the composite deterministic, and reads as a rune scorched into the ground.</summary>
    public static readonly Color CipherTileBacking = new Color(0.055f, 0.043f, 0.051f, 1f);
    // ════════════════════════════════════════════════════════════
    // V1 DESIGN SPACE (combat_ui_v2 §4 ruling, R20/R21)
    // canvas_items stretch, 1920×1080 base, 1280×720 minimum window.
    // ALL UI code positions in design space — these constants replace
    // per-node screen-fraction exports (the DeckUiManager trap).
    // ════════════════════════════════════════════════════════════

    public const int DesignWidth = 1920;
    public const int DesignHeight = 1080;
    public const int MinWindowWidth = 1280;
    public const int MinWindowHeight = 720;

    // Hand-fan region (design px) — replaces DeckUiManager's dead
    // screen-fraction exports; the fan centers between these reserves.
    // SYMMETRIC reserves so the fan centers on the true screen center
    // (V1 fix: 570/350 read visibly off-center). Bottom-row tiling at 1920:
    // ticker 12–372 · deck 380–476 · fan 484–1436 (center 960) · grave
    // 1444–1540 · End Turn 1728–1908.
    public const float HandReserveLeft = 484f;    // clears bottom-left block + deck button
    public const float HandReserveRight = 484f;   // symmetric; clears grave + End Turn
    public const float HandCardWidth = 200f;
    public const float HandCardHeight = 300f;
    public const float HandBottomInsetFactor = 0.15f;  // card height below screen bottom
    public const float HandArcRadiusFactor = 1.8f;     // arc radius = design height ×
    public const float HandCardGapFactor = 1.8f;       // gap = card width ×
    public const float HandMaxArcDegrees = 25f;

    // ── V3 ability state chips (combat_ui_v2 §8/§12) ──
    public static readonly Color ChargeReady = new Color(1.0f, 0.6f, 0.2f);   // filled — firing this activation
    public static readonly Color ChargeSpent = new Color(0.5f, 0.5f, 0.55f);  // hollow — charging

    // ── V2 roster role markers (combat_ui_v2 §6) ──
    public static readonly Color RoleLine = new Color(0.55f, 0.55f, 0.6f);
    public static readonly Color RoleElite = new Color(0.95f, 0.75f, 0.25f);
    public static readonly Color RoleBoss = new Color(0.9f, 0.3f, 0.5f);

    public static readonly Color TileThreat = new Color(0.85f, 0.25f, 0.20f, 0.45f);
    // Battlefield E4 telegraph - imminent destructive map event (amber, distinct from threat red).
    public static readonly Color TileTelegraph = new Color(0.95f, 0.55f, 0.10f, 0.55f);
    // Battlefield E4 terrain scars - overlay tint after a tile is converted mid-fight.
    public static readonly Color TileScarWater = new Color(0.20f, 0.45f, 0.85f, 0.60f);
    public static readonly Color TileScarChasm = new Color(0.14f, 0.10f, 0.22f, 0.75f);
    public static readonly Color TileScarRubble = new Color(0.45f, 0.38f, 0.30f, 0.50f);
    /// <summary>Locked-but-unrevealed intent footprint — the kind-tier reticle
    /// (you always see WHERE an enemy aims; the reveal tier adds how hard).</summary>
    public static readonly Color TileThreatDim = new Color(0.85f, 0.25f, 0.20f, 0.18f);
    /// <summary>Floating reticle glyph over a threatened tile — revealed (hot) tier.
    /// A marker, not a tint: tile tints get buried under the move-zone overlay
    /// when the locked tile sits inside the player's movement range.</summary>
    public static readonly Color TileThreatReticle = new Color(1.0f, 0.35f, 0.28f, 1f);
    /// <summary>Floating reticle glyph — locked-but-unrevealed (dim) tier.</summary>
    public static readonly Color TileThreatReticleDim = new Color(0.95f, 0.5f, 0.4f, 0.65f);

    // ════════════════════════════════════════════════════════════
    // COMBAT TILE BASE COLORS
    // ════════════════════════════════════════════════════════════
    public static readonly Color CombatTileGrass = new Color(0.35f, 0.60f, 0.35f, 1f);
    public static readonly Color CombatTileForest = new Color(0.18f, 0.42f, 0.20f, 1f);
    public static readonly Color CombatTileStone = new Color(0.45f, 0.45f, 0.50f, 1f);
    public static readonly Color CombatTileWater = new Color(0.20f, 0.42f, 0.80f, 1f);
    public static readonly Color CombatTileLava = new Color(0.85f, 0.28f, 0.08f, 1f);
    public static readonly Color CombatTileArcane = new Color(0.48f, 0.22f, 0.75f, 1f);
    public static readonly Color CombatTileIce = new Color(0.65f, 0.85f, 0.98f, 1f);
    public static readonly Color CombatTileSand = new Color(0.82f, 0.71f, 0.52f, 1f);

    public static readonly Color SpawnTintPlayer = new Color(0.25f, 0.75f, 1.00f, 1f);
    public static readonly Color SpawnTintEnemy = new Color(1.00f, 0.30f, 0.30f, 1f);
    public const float SpawnTintStrength = 0.30f;

    // ════════════════════════════════════════════════════════════
    // CAMPUS LANDMARK STATE TINTS
    // ════════════════════════════════════════════════════════════
    // Applied by CampusGridManager.LoadLandmarks as a lerp over the tile's terrain
    // colour — the same technique as the spawn tints above, not a replacement.
    // Active is deliberately the loudest of the three: it is the only state with a
    // player action available (an unplayed narrative beat). Ruined reads as dormant
    // stone, Restored as settled and finished.
    // See docs/campus_landmarks_3d_v1.md §4.
    public static readonly Color LandmarkTintRuined = new Color(0.42f, 0.38f, 0.36f, 1f);
    public static readonly Color LandmarkTintActive = new Color(1.00f, 0.82f, 0.25f, 1f);
    public static readonly Color LandmarkTintRestored = new Color(0.45f, 0.80f, 0.55f, 1f);
    public const float LandmarkTintStrength = 0.45f;

    // ── Campus building name labels ──────────────────────────────────────
    // A three-colour vocabulary on the campus map, readable before any building meshes
    // exist: GOLD (LandmarkTint* above) = quest site with a restoration arc;
    // CYAN = a building that is a door to a system; GREY = an ordinary building.
    // Deliberately not the same hue family as the landmark tints — the player should be
    // able to tell "this has a story" from "this opens a menu" at a glance.

    /// <summary>Name label for a building whose JSON authors a <c>hostsSystem</c> — clicking
    /// it opens that system. The call to action.</summary>
    public static readonly Color BuildingLabelDoor = new Color(0.72f, 0.92f, 1.00f, 1f);

    /// <summary>Name label for a building that grants passive bonuses but opens nothing.
    /// Muted, because there is nothing to do there.</summary>
    public static readonly Color BuildingLabelPlain = new Color(0.74f, 0.71f, 0.67f, 1f);

    // ════════════════════════════════════════════════════════════
    // ELEMENT COLORS
    // ════════════════════════════════════════════════════════════
    public static readonly Color ElementFire = new Color(0.88f, 0.32f, 0.05f, 1f);
    public static readonly Color ElementIce = new Color(0.35f, 0.68f, 0.90f, 1f);
    public static readonly Color ElementStorm = new Color(0.72f, 0.65f, 0.08f, 1f);
    public static readonly Color ElementEarth = new Color(0.52f, 0.38f, 0.15f, 1f);

    // Imbuement overlay tints. Referenced ONLY by ImbuementOverlay.cs -- these
    // are board colours, not UI colours, and nothing in the card layer reads
    // them. Muted for the painterly pass: the originals pinned channels at
    // 1.00 and 0.08, which is exactly the saturation the style guide rules
    // out, and additive blending then pushed them to white over lit grass.
    //
    // Hue separation is deliberately PRESERVED even though saturation dropped,
    // because which element a tile carries is gameplay state
    // (redesign doc SS2). Lightning moved off pale violet to straw gold to
    // pull it away from Frost and Air, which all read as "pale" once muted.
    //
    // See docs/imbuement_painterly_redesign_v1.md SS3.4.
    public static readonly Color ElementTintFire = new Color(0.86f, 0.44f, 0.22f, 1f);
    public static readonly Color ElementTintFrost = new Color(0.66f, 0.84f, 0.90f, 1f);
    // Storm is AMETHYST now, because its form is an amethyst cluster. Straw gold
    // was chosen when it was bare filaments and the only job was separating it
    // from Frost and Air; the geometry now carries that separation on its own.
    public static readonly Color ElementTintLightning = new Color(0.60f, 0.42f, 0.86f, 1f);
    public static readonly Color ElementTintEarth = new Color(0.64f, 0.50f, 0.30f, 1f);
    public static readonly Color ElementTintWater = new Color(0.36f, 0.56f, 0.74f, 1f);
    public static readonly Color ElementTintAir = new Color(0.78f, 0.86f, 0.80f, 1f);
    // Nudged toward magenta ONLY to stay clear of Storm's new amethyst. Two violet
    // elements on one board is a legibility problem, not a palette preference —
    // which element a tile carries is targetable state. Shape separates them too
    // (floating shards vs. ground clusters), but hue should not be doing nothing.
    public static readonly Color ElementTintArcane = new Color(0.80f, 0.44f, 0.70f, 1f);
    public static readonly Color ElementTintShadow = new Color(0.34f, 0.26f, 0.38f, 1f);

    // ════════════════════════════════════════════════════════════
    // COMBAT UI
    // ════════════════════════════════════════════════════════════
    public static readonly Color EnemyHealthBar = Danger;
    public static readonly Color UnitBarSelected = new Color(0.48f, 0.30f, 0.82f, 0.25f);
    public static readonly Color UnitBarBorder = Violet;
    public static readonly Color StatBarHealth = HealthGreen;
    public static readonly Color StatBarMove = Gold;
    public static readonly Color StatBarMana = ArcaneBlue;
    // V2.2 (combat_ui §8): 2D HP-bar withered-max span; mirrors HealthBarRoot.WitherColor.
    public static readonly Color WitherFill = new Color(0.48f, 0.22f, 0.55f, 1f);
    // Threat overlay tiers (2026-07-13): blood-red darkening ramp. Level 0 = movement-
    // only (reachable, no attack affordable); higher = more attacks landable on that tile.
    public static readonly Color ThreatMoveOnly = new Color(0.55f, 0.30f, 0.28f, 1f); // faint desaturated
    public static readonly Color ThreatTierLow  = new Color(0.82f, 0.22f, 0.20f, 1f); // 1 hit — bright red
    public static readonly Color ThreatTierHigh = new Color(0.36f, 0.02f, 0.05f, 1f); // 3+ hits — blood dark
    public const int ThreatMaxTier = 3;
    // v2.2 behavior-tag chips (combat_ui §6/§7b): one hue per wired tag so the
    // roster telegraphs pack/charge/bulwark without reading tooltips.
    public static readonly Color TagPack    = new Color(0.85f, 0.60f, 0.25f, 1f); // amber — hunts together
    public static readonly Color TagCharge  = new Color(0.80f, 0.32f, 0.16f, 1f); // rust — momentum hit
    public static readonly Color TagBulwark = new Color(0.35f, 0.55f, 0.78f, 1f); // steel — braces
    public static readonly Color TagNeutral = new Color(0.60f, 0.58f, 0.62f, 1f); // scout/immobile/other
    // V2 §6 valence: blight chip accent for corrupted-faction units and
    // factionless monsters (spec-ruled hex #1A3A5C).
    public static readonly Color ValenceBlight = new Color("#1A3A5C");

    public const int CombatStatLabelFontSize = 10;
    public const int EnemyRosterButtonWidth = 90;
    public const int EnemyRosterBarWidth = 80;
    public const int EnemyRosterBarHeight = 14;
    public const int UnitBarStatBarWidth = 80;
    public const int UnitBarStatBarHeight = 8;
    public const int MaxActionLogLines = 6;

    // ════════════════════════════════════════════════════════════
    // OVERWORLD
    // ════════════════════════════════════════════════════════════
    public static readonly Color TerrainGrassland = new Color(0.38f, 0.65f, 0.28f, 1f);
    public static readonly Color TerrainForest = new Color(0.12f, 0.42f, 0.15f, 1f);
    public static readonly Color TerrainRoad = new Color(0.72f, 0.65f, 0.48f, 1f);
    public static readonly Color TerrainRuins = new Color(0.52f, 0.44f, 0.36f, 1f);
    public static readonly Color TerrainMountain = new Color(0.58f, 0.56f, 0.54f, 1f);
    public static readonly Color TerrainSwamp = new Color(0.32f, 0.44f, 0.22f, 1f);
    public static readonly Color TerrainArcaneGround = new Color(0.48f, 0.30f, 0.72f, 1f);
    public static readonly Color TerrainVolcanic = new Color(0.70f, 0.26f, 0.10f, 1f);
    public static readonly Color TerrainWater = new Color(0.18f, 0.44f, 0.78f, 1f);
    public static readonly Color TerrainHills = new Color(0.50f, 0.56f, 0.34f, 1f);
    public static readonly Color TerrainCoast = new Color(0.82f, 0.76f, 0.56f, 1f);
    public static readonly Color TerrainLake = new Color(0.30f, 0.58f, 0.82f, 1f);
    public static readonly Color TerrainDesert = new Color(0.83f, 0.74f, 0.46f); // sandy tan
    public static readonly Color TerrainTundra = new Color(0.62f, 0.66f, 0.60f); // cold grey-green
    public static readonly Color TerrainSnow = new Color(0.90f, 0.93f, 0.96f); // near-white
    public static readonly Color TerrainMarsh = new Color(0.34f, 0.46f, 0.40f); // waterlogged green (keep distinct from Swamp)
    public static readonly Color SettlementCityBorder = new Color(0.95f, 0.80f, 0.30f, 1f); // gold wash
    public static readonly Color SettlementTownBorder = new Color(0.80f, 0.55f, 0.30f, 1f); // bronze wash

    public static readonly Color TerrainWaterShallow = new Color(0.17f, 0.34f, 0.50f); // muted steel shelf
    public static readonly Color TerrainWaterDeep = new Color(0.05f, 0.14f, 0.29f);    // deep navy
    public static int OceanDepthForFullDark = 36;   // wide ramp: deep mottle stays visible, only the true abyss saturates

    public static readonly Color FogHidden = new Color(0.08f, 0.06f, 0.12f, 0.90f);
    public static readonly Color FogSilhouette = new Color(0.08f, 0.06f, 0.12f, 0.48f);
    public static readonly Color FogRevealed = new Color(0f, 0f, 0f, 0f);

    public static readonly Color POICombat = new Color(0.90f, 0.22f, 0.22f, 1f);
    public static readonly Color POIRest = new Color(0.25f, 0.78f, 0.90f, 1f);
    public static readonly Color POIObjective = new Color(1.00f, 0.85f, 0.20f, 1f);
    public static readonly Color POINarrative = new Color(0.70f, 0.40f, 1.00f, 1f);
    public static readonly Color POINegotiation = new Color(0.20f, 0.80f, 0.58f, 1f);
    public static readonly Color POIOutpost = new Color(0.90f, 0.60f, 0.12f, 1f);

    public static readonly Color HexBorderColor = new Color(0.18f, 0.14f, 0.22f, 0.70f);
    public const float HexBorderWidth = 1.5f;

    public static readonly Color PartyTokenFill = new Color(1.00f, 0.85f, 0.20f, 1f);
    public static readonly Color PartyTokenOutline = new Color(0.25f, 0.18f, 0.05f, 1f);
    public const float PartyTokenRadius = 14f;
    public const float PartyTokenOutlineRadius = 17f;
    public const int PartyTokenSegments = 12;

    public static readonly Color MoveHighlightCheap = new Color(0.38f, 0.90f, 0.45f, 0.30f);
    public static readonly Color MoveHighlightModerate = new Color(0.90f, 0.80f, 0.25f, 0.30f);
    public static readonly Color MoveHighlightExpensive = new Color(0.90f, 0.40f, 0.25f, 0.30f);
    public static readonly Color MoveHighlightDefault = new Color(0.70f, 0.55f, 0.90f, 0.30f);

    public static readonly Color OverworldHudBg = new Color(0.10f, 0.11f, 0.16f, 0.88f);
    public static readonly Color OverworldHudBorder = new Color(0.32f, 0.22f, 0.52f, 0.80f);
    public static readonly Color OverworldLowResourceWarning = Danger;
    public static readonly Color OverworldInfoLabelTint = Gold;

    // ── Overworld spellcasting (S2, overworld_spell_system §12) ─────
    public static readonly Color EssenceText = ArcaneBlue;
    public static readonly Color SpellTargetHighlight = new Color(0.28f, 0.55f, 0.92f, 0.35f);
    public static readonly Color MagnitudeSubtle = TextSecondary;
    public static readonly Color MagnitudeOvert = Warning;
    public static readonly Color MagnitudeGrand = Gold;
    public static readonly Color BeaconMark = Gold;


    // ════════════════════════════════════════════════════════════
    // FACTION COLORS
    // ════════════════════════════════════════════════════════════


    private static readonly System.Collections.Generic.Dictionary<string, Color> _factionColorCache = new();

    public static Color FactionColor(string factionId)
    {
        if (string.IsNullOrEmpty(factionId))
            return Neutral;

        if (_factionColorCache.TryGetValue(factionId, out var cached))
            return cached;

        var def = FactionRegistry.Get(factionId);
        Color c = (def != null && !string.IsNullOrEmpty(def.ColorHex))
            ? new Color(def.ColorHex)   // Godot parses "#RRGGBB"
            : Neutral;

        _factionColorCache[factionId] = c;
        return c;
    }

    // Strategic-view discovery tones (read by StrategicView).
    public static readonly Color StrategicUnseen = WorldDeep;                       // unexplored void
    public static readonly Color StrategicCharted = new Color(0.16f, 0.14f, 0.20f, 1f); // dim known-shape
    public static readonly Color StrategicCorruption = new Color(0.78f, 0.12f, 0.20f, 1f); // red wash, blended by level
    public static readonly Color StrategicCorruptionWash = new Color(0.42f, 0.10f, 0.16f, 1f); // dark stain for the political-lens overlay
    public static readonly Color POIConvergence = new Color(0.72f, 0.18f, 0.62f, 1f); // endgame seat: corrupt magenta-violet
    public static readonly Color KingdomBorder = new Color(0.06f, 0.05f, 0.09f, 0.55f); // dark boundary-tile tint // near-black political boundary

    // Kingdom/territory fill colors — tuned to the overworld palette's register
    // (values ~0.55, muted) so blocs read as tinted GROUND beneath the POI/label
    // markers, not as the brightest surface. Hues borrow from the terrain/accent
    // families already in this file so the political layer feels authored alongside
    // the rest. Assigned by kingdom index; wraps past the end.
    public static readonly Color[] KingdomPalette =
    {
        new Color(0.56f, 0.40f, 0.36f), // clay (terracotta family)
        new Color(0.38f, 0.50f, 0.40f), // muted moss (grassland family, dimmed)
        new Color(0.36f, 0.44f, 0.58f), // dusk blue (arcane-blue family, dimmed)
        new Color(0.58f, 0.50f, 0.34f), // ochre (gold-dim family)
        new Color(0.48f, 0.37f, 0.55f), // muted violet (arcane-ground family, dimmed)
        new Color(0.34f, 0.50f, 0.50f), // teal-grey
        new Color(0.60f, 0.42f, 0.32f), // rust-brown (volcanic family, heavily dimmed)
        new Color(0.46f, 0.48f, 0.34f), // olive (hills family)
        new Color(0.42f, 0.40f, 0.56f), // slate indigo
        new Color(0.56f, 0.44f, 0.48f), // dusty mauve
        new Color(0.38f, 0.52f, 0.46f), // verdigris
        new Color(0.52f, 0.48f, 0.40f), // taupe
    };

    public static int StrategicLabelFontSize = 15;

    /// <summary>Ocean colour by depth-from-shore: shallow teal near land grading to
    /// deep navy out to sea.</summary>
    public static Color OceanColor(int depth)
    {
        float t = Mathf.Clamp((float)depth / OceanDepthForFullDark, 0f, 1f);
        return TerrainWaterShallow.Lerp(TerrainWaterDeep, t);
    }


    // ════════════════════════════════════════════════════════════
    // DRUID VISUALS
    // ════════════════════════════════════════════════════════════

    public static readonly Color WildingGreen = new Color(0.42f, 0.62f, 0.20f); // ~#6B9E33

    // Druid living-terrain tile tints (low alpha — terrain still reads underneath)
    public static readonly Color GrowthSapling = new Color(0.45f, 0.70f, 0.30f, 0.30f);
    public static readonly Color GrowthThicket = new Color(0.30f, 0.60f, 0.20f, 0.45f);
    public static readonly Color GrowthOldGrowth = new Color(0.18f, 0.45f, 0.14f, 0.60f);
    public static readonly Color GrowthPip = new Color(0.55f, 0.90f, 0.35f, 1.0f); // floating marker

    // ════════════════════════════════════════════════════════════
    // UNIT VISUALS
    // ════════════════════════════════════════════════════════════
    public static readonly Color[] EnemyUnitColors =
    {
        new Color(0.90f, 0.22f, 0.22f),
        new Color(0.95f, 0.52f, 0.08f),
        new Color(0.75f, 0.18f, 0.88f),
        new Color(0.18f, 0.75f, 0.88f),
        new Color(0.88f, 0.88f, 0.08f),
    };

    public static readonly Color SummonColorPillar = new Color(0.48f, 0.42f, 0.32f, 1f);
    public static readonly Color SummonColorFriendly = new Color(0.25f, 0.55f, 0.90f, 1f);
    public static readonly Color SummonColorEnemy = new Color(0.88f, 0.22f, 0.22f, 1f);

    // ════════════════════════════════════════════════════════════
    // NARRATIVE ENCOUNTER — slate panels
    // ════════════════════════════════════════════════════════════
    public static readonly Color NarrativeBackdrop = BgOverlay;
    public static readonly Color NarrativePanelBg = BgBase;
    public static readonly Color NarrativePanelBorder = Violet;
    public static readonly Color NarrativeResultBg = BgRaised;
    public static readonly Color NarrativeResultBorder = VioletDim;
    public static readonly Color NarrativeTitleColor = Gold;
    public static readonly Color NarrativeBodyColor = TextPrimary;
    public static readonly Color NarrativeResultColor = TextSecondary;

    public const int NarrativeTitleFontSize = 22;
    public const int NarrativeBodyFontSize = 16;
    public const int NarrativeResultFontSize = 15;
    public const int NarrativeChoiceFontSize = 15;
    public const int NarrativePanelCorner = 8;
    public const int NarrativeResultCorner = 4;

    // ════════════════════════════════════════════════════════════
    // NEGOTIATION — slate panels
    // ════════════════════════════════════════════════════════════
    public static readonly Color NegotiationBg = BgDeep;
    public static readonly Color NegotiationResultBg = BgRaised;
    public static readonly Color NegotiationResultBorder = Violet;
    public static readonly Color NegotiationTitleColor = Gold;
    public static readonly Color NegotiationNpcColor = TextSecondary;
    public static readonly Color NegotiationBodyColor = TextPrimary;
    public static readonly Color NegotiationHiddenTerm = TextDim;

    public static readonly Color TensionEmpty = NeutralDim;
    public static readonly Color TensionCordial = Success;
    public static readonly Color TensionStrained = Warning;
    public static readonly Color TensionHostile = Danger;

    public static readonly Color ZoneCordialLabel = Success;
    public static readonly Color ZoneStrainedLabel = Warning;
    public static readonly Color ZoneHostileLabel = Danger;

    public static readonly Color TermFavorPlayer = Success;
    public static readonly Color TermAgainstPlayer = Danger;

    public const int NegotiationTitleFontSize = 26;
    public const int NegotiationNpcFontSize = 18;
    public const int NegotiationBodyFontSize = 15;
    public const int NegotiationHeaderFontSize = 16;
    public const int NegotiationDetailFontSize = 14;
    public const int NegotiationSmallFontSize = 13;
    public const int NegotiationTinyFontSize = 11;
    public const int NegotiationResultFontSize = 18;
    public const int NegotiationActionFontSize = 16;
    public const int NegotiationTensionFontSize = 14;

    // ════════════════════════════════════════════════════════════
    // CAMPUS SCREEN — slate panels, near-white text
    // ════════════════════════════════════════════════════════════
    public static readonly Color CampusBg = WorldBase;
    public static readonly Color CampusTitleBarBg = BgDeep;
    public static readonly Color CampusTitleBarBorder = Violet;

    public static readonly Color CampusTitleColor = Gold;
    public static readonly Color CampusSectionColor = Gold;
    public static readonly Color CampusSubtleText = TextSecondary;
    public static readonly Color CampusStubText = TextDim;
    public static readonly Color CampusSchoolDescText = TextSecondary;
    public static readonly Color CampusNoRunText = TextDim;
    public static readonly Color CampusRunSuccess = Success;
    public static readonly Color CampusRunFail = Danger;
    public static readonly Color CampusSlotSelected = new Color(0.22f, 0.32f, 0.22f, 1f);

    public static readonly Color DebugPanelBg = new Color(0.22f, 0.12f, 0.12f, 1f);
    public static readonly Color DebugPanelBorder = Danger;

    public static readonly Color CompanionCardBg = BgCard;
    public static readonly Color CompanionCardBorderActive = Success;
    public static readonly Color CompanionCardBorderInactive = Neutral;
    public static readonly Color CompanionSubText = TextSecondary;

    public static readonly Color BuildingCardBg = BgCard;
    public static readonly Color BuildingCardBorderBuilt = Violet;
    public static readonly Color BuildingCardBorderEmpty = Neutral;
    public static readonly Color BuildingCategoryText = TextSecondary;
    public static readonly Color BuildingActiveText = Success;
    public static readonly Color BuildingNextText = TextSecondary;
    public static readonly Color BuildingMaxText = Gold;

    // ── Armory ────────────────────────────────────────────────────
    public static readonly Color ArmoryCardBg = BgCard;
    public static readonly Color ArmoryCardBorderEmpty = Neutral;
    public static readonly Color ArmorySlotLabel = TextDim;
    public static readonly Color ArmoryEmptySlot = TextDim;
    public static readonly Color ArmoryStatText = TextSecondary;
    public static readonly Color ArmoryDescText = TextDim;
    public static readonly Color ArmoryPendingBg = new Color(0.14f, 0.22f, 0.14f, 1f);
    public static readonly Color ArmoryPendingBorder = Success;

    // ── Intel / deployment ────────────────────────────────────────
    public static readonly Color IntelHeader = Gold;
    public static readonly Color IntelMuted = TextDim;

    // ── Combat phase ──────────────────────────────────────────────
    public static readonly Color PhasePlayer = Success;
    public static readonly Color PhaseEnemy = Danger;
    public static readonly Color PhaseNeutral = TextSecondary;

    // ── Font sizes ────────────────────────────────────────────────
    public const int CampusTitleFontSize = 30;
    public const int CampusTabFontSize = 16;
    public const int CampusSectionFontSize = 20;
    public const int CampusBodyFontSize = 15;
    public const int CampusSmallFontSize = 13;
    public const int CampusTinyFontSize = 12;
    public const int CampusStubFontSize = 14;
    public const int CampusSchoolFontSize = 13;
    public const int CampusNameFontSize = 15;
    public const int CampusBuildFontSize = 16;
    public const int CampusBuildSmallFontSize = 13;
    public const int CampusBuildTinyFontSize = 12;

    // ── Card library ──────────────────────────────────────────────
    public static readonly Color LibraryTabNormal = BgBase;
    public static readonly Color LibraryTabPressed = new Color(0.28f, 0.20f, 0.48f, 1f);
    public static readonly Color LibraryTabHover = BgRaised;

    public const int LibraryTabFontSize = 12;
    public const int LibraryTabHeight = 28;
    public const float LibraryCardWidth = 200f;
    public const float LibraryCardHeight = 300f;
    public const float LibraryGridSpacing = 12f;

    // ════════════════════════════════════════════════════════════
    // FONT SIZES — shared scale
    // ════════════════════════════════════════════════════════════
    public const int FontSizeSmall = 13;
    public const int FontSizeNormal = 16;
    public const int FontSizeMedium = 20;
    public const int FontSizeLarge = 26;
    public const int FontSizeTitle = 32;

    public const int Label3DUnit = 18;
    public const int Label3DSmall = 24;
    public const int Label3DHealth = 28;
    public const int Label3DGlyph = 64;

    /// <summary>Billboarded point-of-interest label on a hex (HexTile.SetPoiLabel).
    /// The DEFAULT for SetPoiLabel, but nothing on the campus passes it any more — both
    /// landmarks and buildings now label with full names at
    /// <see cref="Label3DPlaceName"/>. Kept as the fallback for a future short-marker
    /// caller; delete it if none appears.
    /// Sized for a two-character marker like the campus landmarks' "BL" / "RF" —
    /// larger than Label3DSmall so it reads at campus camera distance, smaller than
    /// the memorial glyph so the two never compete when a tile carries both.</summary>
    public const int Label3DPoi = 32;

    /// <summary>POI label size for named places on the campus map — buildings AND
    /// landmarks. Both draw a full name ("Gatehouse Yard", "The Uncatalogued Wing") rather
    /// than a marker, so both use this rather than <see cref="Label3DPoi"/>, which is sized
    /// for two characters and would send a long name across three hexes.
    ///
    /// One size for both on purpose: a place on the map should read with the same weight
    /// whether it is a building or a landmark. What separates them is COLOUR — gold for a
    /// landmark with a restoration arc, cyan for a building that is a door, grey for one
    /// that is not.</summary>
    public const int Label3DPlaceName = 22;

    /// <summary>Outline thickness for hex POI labels. Sized to survive the worst case —
    /// pale text on the light-green campus lawn — rather than tuned for the best one.</summary>
    public const int Label3DOutlineSize = 10;

    /// <summary>Near-black outline behind every hex POI label. Almost fully opaque: a
    /// translucent outline stops working over a dark tile, which is exactly where a light
    /// label needs it least and a dim one needs it most.</summary>
    public static readonly Color Label3DOutline = new Color(0.02f, 0.02f, 0.04f, 0.95f);

    /// <summary>Height above the tile for POI labels. Above the memorial indicator (0.85),
    /// and high enough that at a shallow camera pitch the label does not appear to belong to
    /// the tile behind it.</summary>
    public const float Label3DPoiHeight = 1.55f;

    public const int OverworldUIFontSize = 18;
    public const int OverworldCostLabelFontSize = 14;

    public const int AttunementPanelWidth = 252;
    public const int AttunementBarHeight = 12;
    public const int AttunementBarMax = 4;

    public const int FontSizePauseButton = 18;

    // ════════════════════════════════════════════════════════════
    // SPACING / LAYOUT
    // ════════════════════════════════════════════════════════════
    public const int PaddingSmall = 4;
    public const int PaddingNormal = 8;
    public const int PaddingLarge = 16;
    public const int BorderWidth = 2;
    public const int CornerRadius = 5;
    public const int CornerRadiusLg = 8;

    // ════════════════════════════════════════════════════════════
    // ANIMATION DURATIONS (seconds)
    // ════════════════════════════════════════════════════════════
    public const float AnimFast = 0.12f;
    public const float AnimNormal = 0.20f;
    public const float AnimSlow = 0.35f;
    public const float AnimPulse = 0.50f;

    // ════════════════════════════════════════════════════════════
    // HAND / CARD LAYOUT
    // ════════════════════════════════════════════════════════════
    public const float HandArcRadiusScale = 2.3f;
    public const float HandArcCenterYScale = 0.6f;
    public const float HandArcMaxSpanDeg = 30f;
    public const float HandArcMinSpanDeg = 1f;
    public const float HandArcStepPerCard = 5f;
    public const float HandNeighborPushPx = 18f;

    public const float DiscardAnimDropScale = 0.3f;
    public const float DiscardAnimDuration = 0.28f;
    public const float DiscardFadeDuration = 0.22f;
    public const float DiscardEndScale = 0.85f;

    // ════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// Standard slate panel StyleBoxFlat.
    /// bg: pass BgRaised for cards/items, BgBase for container panels.
    /// </summary>
    public static StyleBoxFlat MakePanelStyle(
        Color bg, Color border,
        bool topCorners = true, bool bottomCorners = true)
    {
        var s = new StyleBoxFlat();
        s.BgColor = bg;
        s.BorderColor = border;
        s.SetBorderWidthAll(BorderWidth);
        if (topCorners)
        {
            s.CornerRadiusTopLeft = CornerRadius;
            s.CornerRadiusTopRight = CornerRadius;
        }
        if (bottomCorners)
        {
            s.CornerRadiusBottomLeft = CornerRadius;
            s.CornerRadiusBottomRight = CornerRadius;
        }
        return s;
    }

    public static StyleBoxFlat MakeBadgeStyle(Color bg)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.SetCornerRadiusAll(CornerRadiusLg);
        return s;
    }

    /// <summary>
    /// Apply the standard slate button appearance to any Button node.
    /// Call this on every Button in the project that needs themed styling.
    /// </summary>
    public static void ApplyButtonStyle(Button btn, bool isPrimary = true)
    {
        var normal = new StyleBoxFlat
        {
            BgColor = isPrimary ? ButtonPrimary : ButtonSecondary,
            BorderColor = isPrimary ? Violet : Neutral,
        };
        normal.SetBorderWidthAll(1);
        normal.SetCornerRadiusAll(CornerRadius);
        normal.ContentMarginLeft = normal.ContentMarginRight = PaddingNormal + 4;
        normal.ContentMarginTop = normal.ContentMarginBottom = PaddingSmall + 2;

        var hover = new StyleBoxFlat
        {
            BgColor = isPrimary ? ButtonPrimaryHover : BgRaised,
            BorderColor = isPrimary ? Violet : Neutral,
        };
        hover.SetBorderWidthAll(1);
        hover.SetCornerRadiusAll(CornerRadius);
        hover.ContentMarginLeft = hover.ContentMarginRight = PaddingNormal + 4;
        hover.ContentMarginTop = hover.ContentMarginBottom = PaddingSmall + 2;

        var pressed = new StyleBoxFlat { BgColor = VioletDim, BorderColor = Violet };
        pressed.SetBorderWidthAll(1);
        pressed.SetCornerRadiusAll(CornerRadius);
        pressed.ContentMarginLeft = pressed.ContentMarginRight = PaddingNormal + 4;
        pressed.ContentMarginTop = pressed.ContentMarginBottom = PaddingSmall + 2;

        btn.AddThemeStyleboxOverride("normal", normal);
        btn.AddThemeStyleboxOverride("hover", hover);
        btn.AddThemeStyleboxOverride("pressed", pressed);
        btn.AddThemeStyleboxOverride("focus", normal);
        btn.AddThemeColorOverride("font_color", TextPrimary);
    }
}

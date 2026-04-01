using System;
using System.Collections.Generic;
using System.Linq;

namespace BrokenNes.Windows.WebApi
{
    internal sealed record AuthoredCardDefinition(
        string Domain,
        string Id,
        string ShortName,
        string DisplayName,
        string Description,
        int Rating,
        int Performance,
        string Category,
        string FooterNote
    )
    {
        public CoreCardModel ToCardModel()
        {
            return new CoreCardModel
            {
                Id = Id,
                ShortName = ShortName,
                DisplayName = DisplayName,
                Description = Description,
                Rating = Rating,
                Performance = Performance,
                FooterNote = FooterNote,
                Domain = Domain
            };
        }
    }

    internal static class AuthoredCardCatalog
    {
        private static readonly IReadOnlyDictionary<string, AuthoredCardDefinition> Entries =
            new Dictionary<string, AuthoredCardDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                [BuildKey("BACKGROUND", "Gradient (Default)")] = new(
                    "BACKGROUND",
                    "Gradient (Default)",
                    "GRAD",
                    "Gradient (Default)",
                    "The stock BrokenNes backdrop: ordered dither, steel-gray edges, and a black center well that keeps menus readable.",
                    2,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "None (Black)")] = new(
                    "BACKGROUND",
                    "None (Black)",
                    "NONE",
                    "None (Black)",
                    "A deliberate blackout plate with no atmosphere at all. It hands every bit of contrast to the foreground surface.",
                    1,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "AnimatedWave")] = new(
                    "BACKGROUND",
                    "AnimatedWave",
                    "WAVE",
                    "AnimatedWave",
                    "Layered sine sheets with specular shimmer and cold hue cycling. It reads like lit water without becoming literal scenery.",
                    4,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "AnimatedBubble")] = new(
                    "BACKGROUND",
                    "AnimatedBubble",
                    "BUBL",
                    "AnimatedBubble",
                    "A buoyant field of translucent spheres rising through a dark liquid plane. It feels playful at first glance, but the layered refractions keep it simple.",
                    3,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "BelousovZhabotinsky")] = new(
                    "BACKGROUND",
                    "BelousovZhabotinsky",
                    "BZRX",
                    "BelousovZhabotinsky",
                    "Concentric chemical-wave fronts chasing each other through a reaction dish palette. The surface reads like self-propagating logic rather than a static abstract texture.",
                    5,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "BreathingGradients")] = new(
                    "BACKGROUND",
                    "BreathingGradients",
                    "BRTH",
                    "BreathingGradients",
                    "A slow radial pulse that shifts its center and hue over time. It feels meditative, but the shimmer keeps it from collapsing into wallpaper.",
                    4,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "CalmWaterReflection")] = new(
                    "BACKGROUND",
                    "CalmWaterReflection",
                    "CALM",
                    "CalmWaterReflection",
                    "A horizon-lit mirror pool with barely disturbed reflection bands. It is intentionally restrained, built to make the interface feel suspended over still water.",
                    3,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "CliffordAttractor")] = new(
                    "BACKGROUND",
                    "CliffordAttractor",
                    "CLFD",
                    "CliffordAttractor",
                    "A dense strange-attractor bloom plotted as orbit dust and looped trajectories. It has the feel of a mathematical storm that never fully disperses.",
                    5,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "ComplexDomainColoring")] = new(
                    "BACKGROUND",
                    "ComplexDomainColoring",
                    "CMPL",
                    "ComplexDomainColoring",
                    "Phase hues and magnitude contours folded into a complex-plane cross-section. The card reads like a navigator for invisible structure rather than a rainbow diagnostic.",
                    5,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "DeJongAttractor")] = new(
                    "BACKGROUND",
                    "DeJongAttractor",
                    "DJNG",
                    "DeJongAttractor",
                    "A De Jong orbit cloud rendered as fine particulate drift around an emergent winged form. It looks grown from iteration instead of drawn by hand.",
                    4,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "DriftingClouds")] = new(
                    "BACKGROUND",
                    "DriftingClouds",
                    "CLDS",
                    "DriftingClouds",
                    "Layered cloud bands over a soft horizon gradient. The motion is restrained and wide, more sky system than particle effect.",
                    3,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "FlowingAurora")] = new(
                    "BACKGROUND",
                    "FlowingAurora",
                    "AURA",
                    "FlowingAurora",
                    "Stacked curtain waves in cyan, green, and violet, tuned for very slow travel. It turns the menu plane into an arctic night field.",
                    5,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "FractalFlame")] = new(
                    "BACKGROUND",
                    "FractalFlame",
                    "FLME",
                    "FractalFlame",
                    "Flame-style transforms and spherical folds collapsed into a violet core. It behaves like a cool-burning singularity instead of literal fire.",
                    4,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "GentleRipples")] = new(
                    "BACKGROUND",
                    "GentleRipples",
                    "RIPL",
                    "GentleRipples",
                    "Three offset ripple sources overlap in a deep-water field. The motion is quiet, concentric, and intentionally low-stress.",
                    3,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                [BuildKey("BACKGROUND", "HenonMap")] = new(
                    "BACKGROUND",
                    "HenonMap",
                    "HENO",
                    "HenonMap",
                    "A discrete chaos plot pushed into chartreuse and lime. It reads like a live attractor sketch pinned under glass.",
                    4,
                    0,
                    "Background",
                    "BACKGROUND"
                ),
                    [BuildKey("BACKGROUND", "HopfBifurcation")] = new(
                        "BACKGROUND",
                        "HopfBifurcation",
                        "HOPF",
                        "HopfBifurcation",
                        "A supercritical Hopf field where stable rings emerge from equilibrium. Mint and aquamarine tones keep the mathematics feeling calm instead of clinical.",
                        4,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "IkedaMap")] = new(
                        "BACKGROUND",
                        "IkedaMap",
                        "IKDA",
                        "IkedaMap",
                        "Chaotic laser-map drift rendered in violet and lavender. It feels like a feedback system folding over itself in slow motion.",
                        4,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "JuliaSet")] = new(
                        "BACKGROUND",
                        "JuliaSet",
                        "JULI",
                        "JuliaSet",
                        "An animated Julia fractal with gently shifting constants and emerald escape bands. It reads as living geometry rather than a static math poster.",
                        5,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "LavaLamp")] = new(
                        "BACKGROUND",
                        "LavaLamp",
                        "LAVA",
                        "LavaLamp",
                        "Rising metaball blobs drift through a warm magenta-to-orange field. It feels retro-analog, soft-edged, and slightly hypnotic.",
                        4,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "LogisticMapBifurcation")] = new(
                        "BACKGROUND",
                        "LogisticMapBifurcation",
                        "LOGI",
                        "LogisticMapBifurcation",
                        "A route-to-chaos diagram scanned across parameter space in cobalt blue. It reads like a scientific instrument slowly discovering instability.",
                        5,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "LorenzAttractor")] = new(
                        "BACKGROUND",
                        "LorenzAttractor",
                        "LRNZ",
                        "LorenzAttractor",
                        "A compact Lorenz flow rendered in deep teal and cyan. The background suggests continuous turbulence without ever breaking into noise.",
                        4,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "MandelbrotDrift")] = new(
                        "BACKGROUND",
                        "MandelbrotDrift",
                        "MNDR",
                        "MandelbrotDrift",
                        "A drifting Mandelbrot viewport with coral and amber escape colors around a dark core. It feels like a warm fracture line opening in slow time.",
                        5,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "PerlinNoise")] = new(
                        "BACKGROUND",
                        "PerlinNoise",
                        "PRLN",
                        "PerlinNoise",
                        "Layered soft-noise bands slide through violet and pink gradients. The motion is low-pressure and cloudlike rather than sharply procedural.",
                        3,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "PlasmaFlow")] = new(
                        "BACKGROUND",
                        "PlasmaFlow",
                        "PLSM",
                        "PlasmaFlow",
                        "Classic plasma math pushed through orange-red heat tones and multiple sine fields. It reads like a demo-scene furnace behind smoked glass.",
                        4,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "ReactDiffusion")] = new(
                        "BACKGROUND",
                        "ReactDiffusion",
                        "REAC",
                        "ReactDiffusion",
                        "A Gray-Scott reaction field growing indigo and pale-blue islands through chemical exchange. It feels biological, self-writing, and slightly alien.",
                        5,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "RosslerAttractor")] = new(
                        "BACKGROUND",
                        "RosslerAttractor",
                        "ROSS",
                        "RosslerAttractor",
                        "A Rössler strange attractor washed in jade and emerald phase shifts. The motion is chaotic, but the surface stays smooth and coherent.",
                        4,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "SpiralGalaxy")] = new(
                        "BACKGROUND",
                        "SpiralGalaxy",
                        "GLXY",
                        "SpiralGalaxy",
                        "A slow-turning spiral galaxy with purple arms, radial falloff, and speckled noise. It turns the menu plane into deep space without getting busy.",
                        4,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "StarfieldDrift")] = new(
                        "BACKGROUND",
                        "StarfieldDrift",
                        "STAR",
                        "StarfieldDrift",
                        "A restrained parallax starfield with faint nebula wash and slow twinkle. It feels spacious and calm rather than fast or arcade-like.",
                        3,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                    [BuildKey("BACKGROUND", "VoronoiDrift")] = new(
                        "BACKGROUND",
                        "VoronoiDrift",
                        "VRNO",
                        "VoronoiDrift",
                        "Drifting Voronoi seeds carve warm cell plates that slide against one another. The surface reads like living topology under soft light.",
                        4,
                        0,
                        "Background",
                        "BACKGROUND"
                    ),
                [BuildKey("NULLPROVIDER", "Static")] = new(
                    "NULLPROVIDER",
                    "Static",
                    "STAT",
                    "Static",
                    "Dead-channel snowfall held in place: no drift, no panorama, just raw television hiss locked to the crash field.",
                    1,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Void")] = new(
                    "NULLPROVIDER",
                    "Void",
                    "VOID",
                    "Void",
                    "An absolute blackout crash surface. When the fail state should feel cut, muted, or terminal, this removes every motion cue.",
                    1,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Aurora")] = new(
                    "NULLPROVIDER",
                    "Aurora",
                    "AURA",
                    "Aurora",
                    "Northern ribbon light with slow color bleed and sky-wide motion. It turns a crash screen into a cold atmospheric horizon.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Butterfly")] = new(
                    "NULLPROVIDER",
                    "Butterfly",
                    "BFLY",
                    "Butterfly",
                    "A Lorenz-system trace drawing the classic butterfly wings in accumulating color. The crash field feels like chaos made visible rather than random noise.",
                    5,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Cells")] = new(
                    "NULLPROVIDER",
                    "Cells",
                    "CELL",
                    "Cells",
                    "Drifting Voronoi regions form living membranes with softly shifting boundaries. It reads like a microscope slide for an invented organism.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Chaos")] = new(
                    "NULLPROVIDER",
                    "Chaos",
                    "CHAO",
                    "Chaos",
                    "A glowing Rössler attractor projected into looping streaks and spectral phase color. It makes the null surface feel unstable but still elegantly bounded.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Breath")] = new(
                    "NULLPROVIDER",
                    "Breath",
                    "BRTH",
                    "Breath",
                    "A centered pulse field that inhales and exhales through hue rotation. Good for crashes that should feel organic instead of broken.",
                    3,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Plasma")] = new(
                    "NULLPROVIDER",
                    "Plasma",
                    "PLSM",
                    "Plasma",
                    "Low-resolution rainbow plasma with heavy interpolation blocks. It reads like old demo-scene firepower repurposed as a failure state.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Waves")] = new(
                    "NULLPROVIDER",
                    "Waves",
                    "WAVE",
                    "Waves",
                    "Three sinusoidal color fields crossing on different axes. It keeps the crash surface active without becoming noisy or jagged.",
                    3,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Clouds")] = new(
                    "NULLPROVIDER",
                    "Clouds",
                    "CLDS",
                    "Clouds",
                    "Layered smooth-noise vapor in pale sky tones. It makes the crash field feel airy and suspended rather than violent.",
                    2,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Ember")] = new(
                    "NULLPROVIDER",
                    "Ember",
                    "EMBR",
                    "Ember",
                    "An upward heat map with cooled reds, amber, and ash-dark gaps. It is fire reduced to residual glow and motion.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Fifth Dimension")] = new(
                    "NULLPROVIDER",
                    "Fifth Dimension",
                    "5DIM",
                    "Fifth Dimension",
                    "A rotating penteract projected down through higher dimensions, with color keyed to the fifth-axis depth.",
                    5,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Flow")] = new(
                    "NULLPROVIDER",
                    "Flow",
                    "FLOW",
                    "Flow",
                    "Multi-octave noise routed through hue drift and restrained brightness. It behaves like a live current rather than a simple texture.",
                    3,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Fluid")] = new(
                    "NULLPROVIDER",
                    "Fluid",
                    "FLUD",
                    "Fluid",
                    "Metaballs merge and separate in a dim liquid field. The crash screen becomes organic, elastic, and slightly synthetic.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Growth")] = new(
                    "NULLPROVIDER",
                    "Growth",
                    "GRTH",
                    "Growth",
                    "An L-system tree keeps regenerating through stacked branch rules and hue drift. It feels like procedural life reclaiming a dead frame.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Infinity")] = new(
                    "NULLPROVIDER",
                    "Infinity",
                    "INFI",
                    "Infinity",
                    "A slow Mandelbrot zoom with rotating spectral bands around a dark set interior. It turns the null surface into an endless approach vector.",
                    5,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Julia")] = new(
                    "NULLPROVIDER",
                    "Julia",
                    "JULI",
                    "Julia",
                    "A parameter-shifting Julia fractal with saturated spectral bands and a dark interior. The crash field becomes a slow mathematical trance.",
                    5,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Lattice")] = new(
                    "NULLPROVIDER",
                    "Lattice",
                    "LATT",
                    "Lattice",
                    "A pulsing honeycomb grid whose cells breathe by position and phase. It feels engineered, modular, and alive under the surface.",
                    3,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Mirrors")] = new(
                    "NULLPROVIDER",
                    "Mirrors",
                    "MIRR",
                    "Mirrors",
                    "Rotating kaleidoscope symmetry folds color into repeating wedges. It turns failure output into a ceremonial reflection chamber.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Murmuration")] = new(
                    "NULLPROVIDER",
                    "Murmuration",
                    "MRMR",
                    "Murmuration",
                    "A flocking swarm leaves dim spectral trails as boids align, separate, and regroup. The crash field feels social and self-organizing instead of static.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Oscillations")] = new(
                    "NULLPROVIDER",
                    "Oscillations",
                    "OSCI",
                    "Oscillations",
                    "Layered Lissajous curves sweep harmonic paths across a dark blue stage. It feels mathematical, musical, and continuously resolving.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Ripples")] = new(
                    "NULLPROVIDER",
                    "Ripples",
                    "RIPL",
                    "Ripples",
                    "Three moving wave sources interfere in cool blue water bands. The null surface stays active without becoming harsh or fragmented.",
                    3,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Stars")] = new(
                    "NULLPROVIDER",
                    "Stars",
                    "STAR",
                    "Stars",
                    "A colorful parallax star shower with trailing glow and depth-coded brightness. It turns failure output into a slow-moving night lane.",
                    3,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("NULLPROVIDER", "Swarm")] = new(
                    "NULLPROVIDER",
                    "Swarm",
                    "SWRM",
                    "Swarm",
                    "Particles chase a moving optimum through classic swarm-optimization rules. The crash field feels strategic and kinetic instead of chaotic formlessness.",
                    4,
                    0,
                    "Null Provider",
                    "NULL PROVIDER"
                ),
                [BuildKey("WEBMODULE", "AchievementsRuntime")] = new(
                    "WEBMODULE",
                    "AchievementsRuntime",
                    "ARTM",
                    "Achievements Runtime",
                    "The live RetroAchievements overlay: it watches frame evaluation, stages unlock modals, runs the null-provider intermission, and hands control back to Continue.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "AchievementsTest")] = new(
                    "WEBMODULE",
                    "AchievementsTest",
                    "ATST",
                    "Achievements Test",
                    "A diagnostics widget for initializing, listing, monitoring, and resetting RetroAchievements against the loaded game. It is the bench rig for the achievement stack.",
                    4,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "ApiTest")] = new(
                    "WEBMODULE",
                    "ApiTest",
                    "APIX",
                    "API Test Suite",
                    "A broad Web API exercise surface for memory, CPU, and glitch-harvester endpoints. It exposes the emulator backend as an inspectable lab console.",
                    4,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "AudioTest")] = new(
                    "WEBMODULE",
                    "AudioTest",
                    "AUDO",
                    "Audio Test",
                    "A control room for music playback, SFX auditioning, live volume changes, and crossfade timing. It turns the audio engine into a hands-on mixer surface.",
                    4,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "Continue")] = new(
                    "WEBMODULE",
                    "Continue",
                    "CONT",
                    "Continue",
                    "The campaign hub and deck-builder spine of BrokenNes: ROM selection, core loadout and reward reveals.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "Cores")] = new(
                    "WEBMODULE",
                    "Cores",
                    "CORE",
                    "Cores",
                    "A collection browser for unlocked cards across cores, webmodules, backgrounds, and null providers, with grouping, sorting, and authored card previews all in one view.",
                    4,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "CorruptionSlop")] = new(
                    "WEBMODULE",
                    "CorruptionSlop",
                    "SLOP",
                    "Corruption Slop",
                    "An overlay automation loop that configures RTC, blasts repeatedly, injects random intermissions, and bounces back to the emulator for rapid glitch farming.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "DeckBuilder")] = new(
                    "WEBMODULE",
                    "DeckBuilder",
                    "DECK",
                    "Deck Builder",
                    "A lightweight collection dashboard that turns save data into owned-core counts, achievement stars, and level progress while keeping the presentation in the front-end shell.",
                    4,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "DeckBuilderCrud")] = new(
                    "WEBMODULE",
                    "DeckBuilderCrud",
                    "CRUD",
                    "Database Editor",
                    "The internal content workstation for games, achievements, cards, meta rules, and levels. It is the authoring surface that shapes BrokenNes progression data.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "GlitchHarvester")] = new(
                    "WEBMODULE",
                    "GlitchHarvester",
                    "RTC",
                    "RTC + Glitch Harvester",
                    "A full corruption workflow manager for base states, stash and stockpile handling, and crash behavior.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "HexEditor")] = new(
                    "WEBMODULE",
                    "HexEditor",
                    "HEXR",
                    "Hex Editor",
                    "A high-throughput memory inspector with virtual scrolling, live refresh, inline byte editing, and heatmap tracking for visible cells.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "Home")] = new(
                    "WEBMODULE",
                    "Home",
                    "HOME",
                    "Home",
                    "The front door of BrokenNes: health warning gate, title-screen music, and the primary routing hub into deck building, options, ROM management, and emulator mode.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "ImagineBug")] = new(
                    "WEBMODULE",
                    "ImagineBug",
                    "IMAG",
                    "Target the Beam",
                    "A standalone Imagine overlay for creating a savestate, drawing a scanline target band, and rerunning the beam-selection workflow.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "Options")] = new(
                    "WEBMODULE",
                    "Options",
                    "OPTS",
                    "Options",
                    "The settings surface for audio levels, feature unlock shortcuts, controller configuration, and save-management actions.",
                    4,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "Overlay")] = new(
                    "WEBMODULE",
                    "Overlay",
                    "OVLY",
                    "Overlay",
                    "A transparent presentation layer for rendering a single card over the emulator while brokering menu close and fullscreen requests. It is the card spotlight surface.",
                    4,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "RomManager")] = new(
                    "WEBMODULE",
                    "RomManager",
                    "ROMS",
                    "ROM Manager",
                    "The cartridge library surface for importing, filtering, inspecting, and deleting ROMs.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "Story")] = new(
                    "WEBMODULE",
                    "Story",
                    "STRY",
                    "Story",
                    "A narrated overlay cutscene system that swaps built-in ROM pages, forces the CRT shader, drives subtitles and TTS, and routes the player into the campaign flow.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "TimeJump")] = new(
                    "WEBMODULE",
                    "TimeJump",
                    "TIME",
                    "Time Jump Challenge",
                    "An automated savestate-capture widget that builds a temporal ladder, tracks level progression, and lets the player jump backward through time.",
                    5,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "VoiceTest")] = new(
                    "WEBMODULE",
                    "VoiceTest",
                    "VOIC",
                    "Voice Test",
                    "A speak.js tuning bench for text, speed, pitch, volume, variant, and voice selection. It is the direct control surface for BrokenNes TTS behavior.",
                    3,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("WEBMODULE", "XYTest")] = new(
                    "WEBMODULE",
                    "XYTest",
                    "XYTS",
                    "XY Test",
                    "A focused input harness that polls the webmodule button bridge and visualizes X/Y presses, releases, counts, and event logs in real time.",
                    3,
                    0,
                    "Webmodule",
                    "WEBMODULE CORE"
                ),
                [BuildKey("FEATURE", "Savestates")] = new(
                    "FEATURE",
                    "Savestates",
                    "SAVE",
                    "Savestates",
                    "Temporal checkpoints for the emulation run. This feature turns volatile play into a recoverable timeline with manual capture and restore points.",
                    5,
                    0,
                    "Feature",
                    "SYSTEM FEATURE"
                ),
                [BuildKey("FEATURE", "RTC")] = new(
                    "FEATURE",
                    "RTC",
                    "RTC",
                    "RTC",
                    "Real-time corruption controls that let the runtime stay hot while values mutate underneath it. This is the live-voltage side of the glitch stack.",
                    5,
                    0,
                    "Feature",
                    "SYSTEM FEATURE"
                ),
                [BuildKey("FEATURE", "GH")] = new(
                    "FEATURE",
                    "GH",
                    "GH",
                    "Glitch Harvester",
                    "The harvesting layer for capturing, organizing, and replaying corruption experiments. It gives the RTC workflow a durable collection surface.",
                    5,
                    0,
                    "Feature",
                    "SYSTEM FEATURE"
                ),
                [BuildKey("FEATURE", "Imagine")] = new(
                    "FEATURE",
                    "Imagine",
                    "IMAG",
                    "Imagine",
                    "Target-the-beam style corruption tooling for aiming directly at execution state. It is the precision-strike feature hidden behind the ImagineBug unlock.",
                    5,
                    0,
                    "Feature",
                    "SYSTEM FEATURE"
                ),
                [BuildKey("FEATURE", "Debug")] = new(
                    "FEATURE",
                    "Debug",
                    "DBUG",
                    "Debug",
                    "An internal diagnostics lane for inspecting and stress-testing systems that are normally kept behind progression-safe surfaces.",
                    4,
                    0,
                    "Feature",
                    "SYSTEM FEATURE"
                )
            };

        public static bool TryBuildCardModel(string? domain, string? id, out CoreCardModel? model)
        {
            model = null;
            if (!TryGetDefinition(domain, id, out var definition))
            {
                return false;
            }

            model = definition.ToCardModel();
            return true;
        }

        public static bool TryGetDefinition(string? domain, string? id, out AuthoredCardDefinition definition)
        {
            return Entries.TryGetValue(BuildKey(domain, id), out definition!);
        }

        public static IReadOnlyList<AuthoredCardDefinition> GetAllDefinitions()
        {
            return Entries.Values
                .OrderBy(entry => entry.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string BuildKey(string? domain, string? id)
        {
            return $"{NormalizeDomain(domain)}::{NormalizeId(domain, id)}";
        }

        private static string NormalizeDomain(string? domain)
        {
            return (domain ?? string.Empty)
                .Trim()
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToUpperInvariant();
        }

        private static string NormalizeId(string? domain, string? id)
        {
            var normalizedDomain = NormalizeDomain(domain);
            var trimmed = id?.Trim() ?? string.Empty;

            if (normalizedDomain == "BACKGROUND")
            {
                return NormalizeBackgroundId(trimmed);
            }

            return trimmed;
        }

        private static string NormalizeBackgroundId(string value)
        {
            if (value.Equals("Gradient", StringComparison.OrdinalIgnoreCase)
                || value.Equals("Gradient (Default)", StringComparison.OrdinalIgnoreCase)
                || value.Equals("StaticGradient", StringComparison.OrdinalIgnoreCase))
            {
                return "Gradient (Default)";
            }

            if (value.Equals("Black", StringComparison.OrdinalIgnoreCase)
                || value.Equals("None", StringComparison.OrdinalIgnoreCase)
                || value.Equals("None (Black)", StringComparison.OrdinalIgnoreCase))
            {
                return "None (Black)";
            }

            return value;
        }
    }
}
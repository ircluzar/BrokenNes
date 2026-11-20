using System;
using System.Collections.Generic;

namespace cor64.IO
{
    // [catalog_fix1] Save types for the whole library, keyed by the 3-character id (media letter + 2-letter game id,
    // header bytes 0x3B-0x3D). Facts taken from ares' Nintendo 64 database (mia/medium/nintendo-64.cpp, ISC licence),
    // rewritten as a data table. Used only when SaveDb's own table has no entry for the 2-letter id, so every game it
    // already knew (all certified games) keeps its type. Japan-only differences are in the switch below.
    // COR64_SAVEDB_FULL=0: off (unknown games get None, picked on first access, and the PIF answers EEPROM 4 Kbit).
    public static class SaveDbFull
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("COR64_SAVEDB_FULL") != "0";

        public static bool TryLookup(string gameCode, out CartSaveType type)
        {
            type = CartSaveType.None;
            if (!Enabled || gameCode == null || gameCode.Length < 4) return false;
            string id = gameCode.Substring(0, 3); char region = gameCode[3];
            switch (id) {
                case "N3H": if (region == 'J') { type = CartSaveType.Sram32K; return true; } return false; // Ganbare! Nippon! Olympics 2000
                case "ND3": case "ND4": if (region == 'J') { type = CartSaveType.Eeprom16K; return true; } return false; // Akumajou Dracula (J)
                case "NDK": case "NWT": if (region == 'J') { type = CartSaveType.Eeprom4K; return true; } return false; // Space Dynamites, Wetrix (J)
            }
            return s_Db.TryGetValue(id, out type);
        }

        private static readonly Dictionary<string, CartSaveType> s_Db = new() {
            ["CDZ"] = CartSaveType.Sram96K, // Dezaemon 3D
            ["CFZ"] = CartSaveType.Sram32K, // F-Zero X (J)
            ["CLB"] = CartSaveType.Eeprom4K, // Mario Party (NTSC)
            ["CP2"] = CartSaveType.FlashRam, // Pocket Monsters Stadium 2 (J)
            ["CPS"] = CartSaveType.Sram32K, // Pocket Monsters Stadium (J)
            ["CZL"] = CartSaveType.Sram32K, // Legend of Zelda: Ocarina of Time [Zelda no Densetsu - Toki no Ocarina (J)]
            ["N3D"] = CartSaveType.Eeprom16K, // Doraemon 3: Nobita no Machi SOS!
            ["NA2"] = CartSaveType.Sram32K, // Virtual Pro Wrestling 2
            ["NAB"] = CartSaveType.Eeprom4K, // Air Boarder 64
            ["NAD"] = CartSaveType.Eeprom4K, // Worms Armageddon (U)
            ["NAF"] = CartSaveType.FlashRam, // Doubutsu no Mori
            ["NAG"] = CartSaveType.Eeprom4K, // AeroGauge
            ["NAL"] = CartSaveType.Sram32K, // Super Smash Bros. [Nintendo All-Star! Dairantou Smash Brothers (J)]
            ["NB5"] = CartSaveType.Sram32K, // Biohazard 2 (J)
            ["NB6"] = CartSaveType.Eeprom4K, // Super B-Daman: Battle Phoenix 64
            ["NB7"] = CartSaveType.Eeprom16K, // Banjo-Tooie [Banjo to Kazooie no Daiboken 2 (J)]
            ["NBC"] = CartSaveType.Eeprom4K, // Blast Corps
            ["NBD"] = CartSaveType.Eeprom4K, // Bomberman Hero [Mirian Ojo o Sukue! (J)]
            ["NBH"] = CartSaveType.Eeprom4K, // Body Harvest
            ["NBK"] = CartSaveType.Eeprom4K, // Banjo-Kazooie [Banjo to Kazooie no Daiboken (J)]
            ["NBM"] = CartSaveType.Eeprom4K, // Bomberman 64 [Baku Bomberman (J)]
            ["NBN"] = CartSaveType.Eeprom4K, // Bakuretsu Muteki Bangaioh
            ["NBV"] = CartSaveType.Eeprom4K, // Bomberman 64: The Second Attack! [Baku Bomberman 2 (J)]
            ["NCC"] = CartSaveType.FlashRam, // Command & Conquer
            ["NCG"] = CartSaveType.Eeprom4K, // Choro Q 64 II - Hacha Mecha Grand Prix Race (J)
            ["NCH"] = CartSaveType.Eeprom4K, // Chopper Attack
            ["NCK"] = CartSaveType.FlashRam, // NBA Courtside 2 featuring Kobe Bryant
            ["NCR"] = CartSaveType.Eeprom4K, // Penny Racers [Choro Q 64 (J)]
            ["NCT"] = CartSaveType.Eeprom4K, // Chameleon Twist
            ["NCU"] = CartSaveType.Eeprom4K, // Cruis'n USA
            ["NCV"] = CartSaveType.FlashRam, // Cubivore [English translation/patch of Doubutsu Banchou (J)]
            ["NCW"] = CartSaveType.Eeprom16K, // Cruis'n World
            ["NCX"] = CartSaveType.Eeprom4K, // Custom Robo
            ["NCZ"] = CartSaveType.Eeprom16K, // Custom Robo V2
            ["ND2"] = CartSaveType.Eeprom16K, // Doraemon 2: Nobita to Hikari no Shinden
            ["ND6"] = CartSaveType.Eeprom16K, // Densha de Go! 64
            ["NDA"] = CartSaveType.FlashRam, // Derby Stallion 64
            ["NDO"] = CartSaveType.Eeprom16K, // Donkey Kong 64
            ["NDP"] = CartSaveType.FlashRam, // Dinosaur Planet (Unlicensed)
            ["NDQ"] = CartSaveType.Eeprom4K, // Disney's Donald Duck - Goin' Quackers [Quack Attack (E)]
            ["NDR"] = CartSaveType.Eeprom4K, // Doraemon: Nobita to 3tsu no Seireiseki
            ["NDU"] = CartSaveType.Eeprom4K, // Duck Dodgers starring Daffy Duck
            ["NDY"] = CartSaveType.Eeprom4K, // Diddy Kong Racing
            ["NEA"] = CartSaveType.Eeprom4K, // PGA European Tour
            ["NEP"] = CartSaveType.Eeprom16K, // Star Wars Episode I: Racer
            ["NER"] = CartSaveType.Eeprom4K, // Aero Fighters Assault [Sonic Wings Assault (J)]
            ["NEV"] = CartSaveType.Eeprom16K, // Neon Genesis Evangelion
            ["NF2"] = CartSaveType.Eeprom4K, // F-1 World Grand Prix II
            ["NFG"] = CartSaveType.Eeprom4K, // Fighter Destiny 2
            ["NFH"] = CartSaveType.Eeprom4K, // In-Fisherman Bass Hunter 64
            ["NFU"] = CartSaveType.Eeprom16K, // Conker's Bad Fur Day
            ["NFW"] = CartSaveType.Eeprom4K, // F-1 World Grand Prix
            ["NFX"] = CartSaveType.Eeprom4K, // Star Fox 64 [Lylat Wars (E)]
            ["NFY"] = CartSaveType.Eeprom4K, // Kakutou Denshou: F-Cup Maniax
            ["NFZ"] = CartSaveType.Sram32K, // F-Zero X (U + E)
            ["NG6"] = CartSaveType.Sram32K, // Ganmare Goemon: Dero Dero Douchuu Obake Tenkomori
            ["NGC"] = CartSaveType.Eeprom16K, // GT 64: Championship Edition
            ["NGE"] = CartSaveType.Eeprom4K, // GoldenEye 007
            ["NGL"] = CartSaveType.Eeprom4K, // Getter Love!!
            ["NGP"] = CartSaveType.Sram32K, // Goemon: Mononoke Sugoroku
            ["NGT"] = CartSaveType.Eeprom16K, // City Tour GrandPrix - Zen Nihon GT Senshuken
            ["NGU"] = CartSaveType.Eeprom4K, // Tsumi to Batsu: Hoshi no Keishousha (Sin and Punishment)
            ["NGV"] = CartSaveType.Eeprom4K, // Glover
            ["NHA"] = CartSaveType.Eeprom4K, // Bomberman 64: Arcade Edition (J)
            ["NHF"] = CartSaveType.Eeprom4K, // 64 Hanafuda: Tenshi no Yakusoku
            ["NHP"] = CartSaveType.Eeprom4K, // Heiwa Pachinko World 64
            ["NHY"] = CartSaveType.Sram32K, // Hybrid Heaven (J)
            ["NIB"] = CartSaveType.Sram32K, // Itoi Shigesato no Bass Tsuri No. 1 Kettei Ban!
            ["NIC"] = CartSaveType.Eeprom4K, // Indy Racing 2000
            ["NIJ"] = CartSaveType.Eeprom4K, // Indiana Jones and the Infernal Machine
            ["NIM"] = CartSaveType.Eeprom16K, // Ide Yosuke no Mahjong Juku
            ["NIR"] = CartSaveType.Eeprom4K, // Utchan Nanchan no Hono no Challenger: Denryuu Ira Ira Bou
            ["NJ5"] = CartSaveType.Sram32K, // Jikkyou Powerful Pro Yakyuu 5
            ["NJF"] = CartSaveType.FlashRam, // Jet Force Gemini [Star Twins (J)]
            ["NJG"] = CartSaveType.Sram32K, // Jinsei Game 64
            ["NJK"] = CartSaveType.Eeprom4K, // Viewpoint 2064 (Master)
            ["NJM"] = CartSaveType.Eeprom4K, // Earthworm Jim 3D
            ["NK2"] = CartSaveType.Eeprom4K, // Snowboard Kids 2 [Chou Snobow Kids (J)]
            ["NKA"] = CartSaveType.Eeprom4K, // Fighters Destiny [Fighting Cup (J)]
            ["NKG"] = CartSaveType.Sram32K, // Major League Baseball featuring Ken Griffey Jr.
            ["NKI"] = CartSaveType.Eeprom4K, // Killer Instinct Gold
            ["NKJ"] = CartSaveType.FlashRam, // Ken Griffey Jr.'s Slugfest
            ["NKT"] = CartSaveType.Eeprom4K, // Mario Kart 64
            ["NLB"] = CartSaveType.Eeprom4K, // Mario Party (PAL)
            ["NLL"] = CartSaveType.Eeprom4K, // Last Legion UX
            ["NLR"] = CartSaveType.Eeprom4K, // Lode Runner 3-D
            ["NM6"] = CartSaveType.FlashRam, // Mega Man 64
            ["NM8"] = CartSaveType.Eeprom16K, // Mario Tennis
            ["NMF"] = CartSaveType.Sram32K, // Mario Golf 64
            ["NMG"] = CartSaveType.Eeprom4K, // Monaco Grand Prix [Racing Simulation 2 (G)]
            ["NMI"] = CartSaveType.Eeprom4K, // Mission: Impossible
            ["NML"] = CartSaveType.Eeprom4K, // Mickey's Speedway USA [Mickey no Racing Challenge USA (J)]
            ["NMO"] = CartSaveType.Eeprom4K, // Monopoly
            ["NMQ"] = CartSaveType.FlashRam, // Paper Mario
            ["NMR"] = CartSaveType.Eeprom4K, // Multi-Racing Championship
            ["NMS"] = CartSaveType.Eeprom4K, // Morita Shougi 64
            ["NMU"] = CartSaveType.Eeprom4K, // Big Mountain 2000
            ["NMV"] = CartSaveType.Eeprom16K, // Mario Party 3
            ["NMW"] = CartSaveType.Eeprom4K, // Mario Party 2
            ["NMX"] = CartSaveType.Eeprom16K, // Excitebike 64
            ["NMZ"] = CartSaveType.Eeprom4K, // Zool - Majou Tsukai Densetsu (J)
            ["NN6"] = CartSaveType.Eeprom4K, // Dr. Mario 64
            ["NNA"] = CartSaveType.Eeprom4K, // Star Wars Episode I: Battle for Naboo
            ["NNB"] = CartSaveType.Eeprom16K, // Kobe Bryant in NBA Courtside
            ["NOB"] = CartSaveType.Sram32K, // Ogre Battle 64: Person of Lordly Caliber
            ["NOS"] = CartSaveType.Eeprom4K, // 64 Oozumou
            ["NP2"] = CartSaveType.Eeprom4K, // Chou Kuukan Night Pro Yakyuu King 2 (J)
            ["NP3"] = CartSaveType.FlashRam, // Pokemon Stadium 2 [Pocket Monsters Stadium - Kin Gin (J)]
            ["NP4"] = CartSaveType.Sram32K, // Jikkyou Powerful Pro Yakyuu 4
            ["NP6"] = CartSaveType.Sram32K, // Jikkyou Powerful Pro Yakyuu 6
            ["NPA"] = CartSaveType.Sram32K, // Jikkyou Powerful Pro Yakyuu 2000
            ["NPD"] = CartSaveType.Eeprom16K, // Perfect Dark
            ["NPE"] = CartSaveType.Sram32K, // Jikkyou Powerful Pro Yakyuu Basic Ban 2001
            ["NPF"] = CartSaveType.FlashRam, // Pokemon Snap [Pocket Monsters Snap (J)]
            ["NPG"] = CartSaveType.Eeprom4K, // Hey You, Pikachu! [Pikachu Genki Dechu (J)]
            ["NPM"] = CartSaveType.Sram32K, // Premier Manager 64
            ["NPN"] = CartSaveType.FlashRam, // Pokemon Puzzle League
            ["NPO"] = CartSaveType.FlashRam, // Pokemon Stadium
            ["NPP"] = CartSaveType.Eeprom16K, // Parlor! Pro 64: Pachinko Jikki Simulation Game
            ["NPS"] = CartSaveType.Sram32K, // Jikkyou J.League 1999: Perfect Striker 2
            ["NPT"] = CartSaveType.Eeprom4K, // Puyo Puyon Party
            ["NPW"] = CartSaveType.Eeprom4K, // Pilotwings 64
            ["NPY"] = CartSaveType.Eeprom4K, // Puyo Puyo Sun 64
            ["NR7"] = CartSaveType.Eeprom16K, // Robot Poncots 64: 7tsu no Umi no Caramel
            ["NRA"] = CartSaveType.Eeprom4K, // Rally '99 (J)
            ["NRC"] = CartSaveType.Eeprom4K, // Top Gear Overdrive
            ["NRE"] = CartSaveType.Sram32K, // Resident Evil 2
            ["NRH"] = CartSaveType.FlashRam, // Rockman Dash - Hagane no Boukenshin (J)
            ["NRI"] = CartSaveType.Sram32K, // New Tetris, The
            ["NRS"] = CartSaveType.Eeprom4K, // Star Wars: Rogue Squadron [Shutsugeki! Rogue Chuutai (J)]
            ["NRZ"] = CartSaveType.Eeprom16K, // Ridge Racer 64
            ["NS3"] = CartSaveType.Eeprom4K, // AI Shougi 3
            ["NS4"] = CartSaveType.Sram32K, // Super Robot Taisen 64
            ["NS6"] = CartSaveType.Eeprom4K, // Star Soldier: Vanishing Earth
            ["NSA"] = CartSaveType.Eeprom4K, // Sonic Wings Assault (J)
            ["NSC"] = CartSaveType.Eeprom4K, // Starshot: Space Circus Fever
            ["NSI"] = CartSaveType.Sram32K, // Fushigi no Dungeon: Fuurai no Shiren 2
            ["NSN"] = CartSaveType.Eeprom4K, // Snow Speeder (J)
            ["NSQ"] = CartSaveType.FlashRam, // StarCraft 64
            ["NSS"] = CartSaveType.Eeprom4K, // Super Robot Spirits
            ["NSU"] = CartSaveType.Eeprom4K, // Rocket: Robot on Wheels
            ["NSV"] = CartSaveType.Eeprom4K, // Space Station Silicon Valley
            ["NSW"] = CartSaveType.Eeprom4K, // Star Wars: Shadows of the Empire [Teikoku no Kage (J)]
            ["NT3"] = CartSaveType.Sram32K, // Shin Nihon Pro Wrestling - Toukon Road 2 - The Next Generation (J)
            ["NT6"] = CartSaveType.Eeprom4K, // Tetris 64
            ["NT9"] = CartSaveType.FlashRam, // Tigger's Honey Hunt
            ["NTB"] = CartSaveType.Eeprom4K, // Transformers: Beast Wars Metals 64 (J)
            ["NTC"] = CartSaveType.Eeprom4K, // 64 Trump Collection
            ["NTE"] = CartSaveType.Sram32K, // 1080 Snowboarding
            ["NTJ"] = CartSaveType.Eeprom4K, // Tom & Jerry in Fists of Fury
            ["NTM"] = CartSaveType.Eeprom4K, // Mischief Makers [Yuke Yuke!! Trouble Makers (J)]
            ["NTN"] = CartSaveType.Eeprom4K, // All Star Tennis '99
            ["NTP"] = CartSaveType.Eeprom4K, // Tetrisphere
            ["NTR"] = CartSaveType.Eeprom4K, // Top Gear Rally (J + E)
            ["NTW"] = CartSaveType.Eeprom4K, // 64 de Hakken!! Tamagotchi
            ["NTX"] = CartSaveType.Eeprom4K, // Taz Express
            ["NUB"] = CartSaveType.Eeprom16K, // PD Ultraman Battle Collection 64
            ["NUM"] = CartSaveType.Sram32K, // Nushi Zuri 64: Shiokaze ni Notte
            ["NUT"] = CartSaveType.Sram32K, // Nushi Zuri 64
            ["NVB"] = CartSaveType.Sram32K, // Bass Rush - ECOGEAR PowerWorm Championship (J)
            ["NVL"] = CartSaveType.Eeprom4K, // V-Rally Edition '99
            ["NVP"] = CartSaveType.Sram32K, // Virtual Pro Wrestling 64
            ["NVY"] = CartSaveType.Eeprom4K, // V-Rally Edition '99 (J)
            ["NW2"] = CartSaveType.Sram32K, // WCW-nWo Revenge
            ["NW4"] = CartSaveType.FlashRam, // WWF No Mercy
            ["NWC"] = CartSaveType.Eeprom4K, // Wild Choppers
            ["NWL"] = CartSaveType.Sram32K, // Waialae Country Club: True Golf Classics
            ["NWQ"] = CartSaveType.Eeprom4K, // Rally Challenge 2000
            ["NWU"] = CartSaveType.Eeprom4K, // Worms Armageddon (E)
            ["NWX"] = CartSaveType.Sram32K, // WWF WrestleMania 2000
            ["NXO"] = CartSaveType.Eeprom4K, // Cruis'n Exotica
            ["NYK"] = CartSaveType.Eeprom4K, // Yakouchuu II: Satsujin Kouro
            ["NYS"] = CartSaveType.Eeprom16K, // Yoshi's Story
            ["NYW"] = CartSaveType.Sram32K, // Harvest Moon 64
            ["NZL"] = CartSaveType.Sram32K, // Legend of Zelda: Ocarina of Time (E)
            ["NZS"] = CartSaveType.FlashRam, // Legend of Zelda: Majora's Mask [Zelda no Densetsu - Mujura no Kamen (J)]
        };
    }
}

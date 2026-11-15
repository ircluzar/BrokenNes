# NES pilot list (P4): 55 games

The pilot of [compat-catalog-plan.md](../compat-catalog-plan.md) P4: every tier on a spread of mappers and genres, known-good
(certified in the 2026-09 certification campaign or "same" against Mesen in P0) and known-broken or risky (new P0 mappers, the three
still-missing mappers 13 / 47 / 118, open issues from the P0 status). Paths are on `X:\EMULATION\Nintendo Entertainment System\` and every
title has a catalog record under `T:\BrokenDev\Catalog\records\nes\` (sha1 = the catalog key). Mapper numbers are read from the iNES
headers on X: (2026-11-15).

Mix: mapper 0 x6, 1 x8, 2 x6, 3 x2, 4 x9, 5 x2, 7 x3, 9, 11, 13, 34, 41, 47, 64, 66, 67-header (Transformers), 68, 69, 71, 79, 113, 118,
119, 144, 228, 232. Genres: platformers, shooters, RPGs with battery saves, password games, puzzle, sports, racing, multicarts, a pinball,
a paint program, a point-and-click.

How to run it: T1 on all 55, T2 on every T1 pass, T3 on 5 chosen ones (suggested: Super Mario Bros., Mega Man 2, Zelda II, Contra,
Castlevania III: short, well-known routes with known RAM). What to measure is listed in [tiers.md](tiers.md#pilot-measurements).

Expected outcomes the pilot checks itself against (the false-alarm rate): the three missing mappers must come back **Nothing**; the
certified ones must not come back worse than **OK**; Arkanoid (Vaus paddle) and Super Mario Bros. + Duck Hunt's Duck Hunt (Zapper) need
peripherals the agent loop has no input for: the agent grades what a pad can reach and tags `input`.

| # | Title | Region | Mapper | Genre / feature | Why it is here / expectation | sha1 |
|---|---|---|---|---|---|---|
| 1 | Super Mario Bros. | JU | 0 | platformer | known-good (golden, reference same) | `facee9c577a5262dbe33ac4930bb0b58c8c037f7` |
| 2 | Balloon Fight | JU | 0 | action | Works in db | `0f85a4d325b97d7977177b9c7808f0558239766e` |
| 3 | Excitebike | JU | 0 | racing | Certified 2026-09 | `ba8d9227a0d02b62ee6cb9e691fa5a5134c303a8` |
| 4 | Donkey Kong | JU | 0 | arcade | Works | `d222dbba5bd3716bbf62ca91167c6a9d15c60065` |
| 5 | Galaga | U | 0 | shooter | Works | `87e536b900db2976e6b3bd682a4d7ed88ac19dcd` |
| 6 | Ice Climber | U | 0 | platformer | Works | `22d57ac6066529d199fcd299159d94820042c7d0` |
| 7 | Legend of Zelda, The | U | 1 | adventure, battery save | Certified (Mesen .sav trap) | `be2f5dc8c5ba8ec1a344a71f9fb204750af24fe7` |
| 8 | Metroid | U | 1 | platformer, password | Certified (default-mode enemy phase note) | `fdbfc7871962f72a1ef57e5a7e456164fb93430b` |
| 9 | Mega Man 2 | U | 1 | platformer, password | reference same (P0) | `2ec08f9341003ded125458df8697ca5ef09d2209` |
| 10 | Zelda II - The Adventure of Link | U | 1 | action RPG, battery | Certified; pause-menu sprite-0 fix 08f92f8 | `11333adb723a5975e0ecca3aee8f4747aa8d2d26` |
| 11 | Dragon Warrior | U | 1 | RPG, battery | menus/text | `240e445ab95c3a5afbef63e9239eacc9f9fe1e19` |
| 12 | Final Fantasy | U | 1 | RPG, battery | menus/text | `c9cfbf5455085e198dce039298b083cd6fc88bce` |
| 13 | Tetris | U | 1 | puzzle | Works | `fd9079cb5e8479eb06d93c2ae5175bfce871746a` |
| 14 | Ninja Gaiden | U | 1 | action, cutscenes | raster splits in cutscenes | `15f245161179ab1959b7dc20e82add024d23aa3d` |
| 15 | Blaster Master | U | 1 | action | MMC1 + scroll | `98845d5716525e68ef7417e6fb55be30a4c7130d` |
| 16 | Castlevania | U | 2 | platformer | lag detector misses its wait loop | `3dcb69a8c861c041aeb56c04e39adf6d332eda3a` |
| 17 | Contra | U | 2 | run and gun, Konami code | UNROM | `979494e7869ac7ab4815fdbd1dc99f893f713fbf` |
| 18 | Duck Tales | U | 2 | platformer | UNROM | `6e700e8f72f767918b096f6a5f17f8cd1e2d0866` |
| 19 | Lifeforce | U | 2 | shooter | NMI phase 17/18 cert | `351edb1fdf4bce3bfc56d1eecccfdc6a21bb14f4` |
| 20 | Rygar | U | 2 | action | UNROM | `b76593a8fbd39447cc5a5d85e525427b9eceb6f8` |
| 21 | Gradius | U | 3 | shooter | CNROM | `9282e87c643557682aee674174821bf3a0fe3876` |
| 22 | Arkanoid | U | 3 | paddle game | Vaus controller: pad fallback only - expect stuck | `230fc31d2c2eb20e78711c82574f29f28117eba3` |
| 23 | Super Mario Bros. 3 | U | 4 | platformer | Certified; red-clouds IRQ fix | `bb894d104c796f69ba16587eb66c0275f5c2fc02` |
| 24 | Kirby's Adventure | U | 4 | platformer | cert 46/47 | `118307da6c77a592f0884baad14120301d8d3a1b` |
| 25 | Mega Man 3 | U | 4 | platformer | MMC3 split 1px rows | `6780b3fcc547c013ee45afac6bb30c6fc6d8b46e` |
| 26 | Mega Man 6 | U | 4 | platformer | Certified | `26d33987cff21c651fe3d638f4a739c33a18c240` |
| 27 | M.C. Kids | U | 4 | platformer | Certified | `f8faa774708e108984158fcd9bbc5482ed7165c4` |
| 28 | Startropics | U | 4 | adventure, battery | MMC6 | `74c53fe9ac779f146c59ac01e701c9bf912b3c7b` |
| 29 | Shadowgate | U | 4 | point and click | menus | `e7869c683bd93275cde4536cd2ddf37f230dd8d0` |
| 30 | Low G Man | U | 4 | action | MMC3 IRQ | `93b758fccb9a4d9e69e851172d2647aebabad461` |
| 31 | Little Nemo - The Dream Master | U | 4 | platformer | MMC3 | `0a0c3c8693df47b988c12f720428473800b67640` |
| 32 | Castlevania III - Dracula's Curse | U | 5 | platformer | MMC5, Certified | `23c4dafcc17fa75e8d682931702277659cd59735` |
| 33 | Uncharted Waters | U | 5 | strategy, MMC5 battery | MMC5 ExRAM | `37267833c984f176db4b0bc9d45daba0fff45304` |
| 34 | Battletoads | U | 7 | beat em up | AxROM, sprite-0 sensitive: known hard | `d85c9ff489672534fbf61a15f8fa56fff489a34b` |
| 35 | Marble Madness | U | 7 | action | AxROM, mid-frame CHR | `eaaa0f6d5d94ec692eaf9a61f593674829289914` |
| 36 | Solstice | U | 7 | isometric | AxROM | `db8a165b40542394c6f13914f4722743cd5ab511` |
| 37 | Mike Tyson's Punch-Out!! | U | 9 | boxing, password | MMC2 | `b6f6bd9d78cdd264a117d4b647aee3309993e9a9` |
| 38 | Bible Adventures | U | 11 | unlicensed | mapper 11; black left column at times | `d84c1c88fd0d5b0a65f6d8f28b1ca37a5ad36c91` |
| 39 | Death Race | U | 11 | unlicensed | mapper 144 (CRC fixup) | `80cd18bb63a5b52b1f3ad36c9191845eb29dd807` |
| 40 | Deadly Towers | U | 34 | action RPG | mapper 34 BNROM | `764c9a41db58d9a0f8dd56499cf1ea84c10addf8` |
| 41 | Caltron 6-in-1 | U | 41 | multicart | mapper 41; drifts from Mesen | `c0c1e49cb73067f76aaf0a3858fa718fa99321b7` |
| 42 | Skull & Crossbones | U | 64 | action | mapper 64; split one line late | `085ee66db817a06d97670e8a80a0c41ccad49e96` |
| 43 | Super Mario Bros. + Duck Hunt | U | 66 | multicart, Zapper | mapper 66; pick SMB, Duck Hunt needs Zapper | `36e695ba6cacc8a5098fb67d9c1f8e6828d6c2c6` |
| 44 | Transformers |  | 67 | action | old iNES header -> CNROM rule | `0d5f7ac10c1d116271a1064e4fada35f37882022` |
| 45 | After Burner | U | 68 | shooter | mapper 68 CHR nametables | `56c3d55654d0e837082978505653f6e1e0d52d6f` |
| 46 | Batman - Return of the Joker | U | 69 | action | mapper 69; no 5B audio | `14289a6dfa8b85ef2c06fa81d6ed61ae7c2868d1` |
| 47 | Micro Machines | U | 71 | racing | mapper 71 Camerica | `8a670df6cf4bc9f99f2fb9932633a0164891122f` |
| 48 | Dudes With Attitude | U | 79 | puzzle | mapper 79 | `721514be25e33105853bed9d7e318a5390253bcd` |
| 49 | Deathbots | U | 113 | shooter | mapper 113 (CRC fixup to 79) | `0ae47bd83202a5a2235b0bc16278f56d66038ab5` |
| 50 | Pinbot | U | 119 | pinball | mapper 119 TQROM | `c649505dce1607ed1b5d201003194629c9c3a86c` |
| 51 | Action 52 | U | 228 | multicart | mapper 228; Certified before | `8e5767e4a6d5842684e1538d21948538643e0393` |
| 52 | Quattro Adventure | U | 232 | multicart | mapper 232 | `06990c8573128e5548c5dcd39479fabf67234926` |
| 53 | Alien Syndrome | U | 118 | action | mapper 118 MISSING: expect Nothing | `7955dbe5ec63f71ba591a9237d113aaa90849263` |
| 54 | Super Spike V'Ball + Nintendo World Cup | U | 47 | sports multicart | mapper 47 MISSING: expect Nothing | `4db565c13702aa461ee22a83f6e3e93e89a9f51b` |
| 55 | Videomation | U | 13 | paint program | mapper 13 MISSING: expect Nothing | `3e24edd8c06713b775eaa66f3468f71693a542a9` |

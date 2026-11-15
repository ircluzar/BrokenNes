# AutoTAS tools

Generic code in `lib/`, game knowledge as data in `games/<game>/` (dossier, milestones, cleanup, macros): a later agent
reads a game's folder as intel on how to interact with that game. Do not put game addresses or inputs in `lib/`.

| file | what |
|---|---|
| `lib/bridge.py` | `Bridge(pc="PEARL")` / `Bridge()`: the MCP bridge. Paths are local; on a PC the bridge writes there and copies back, and pushes `load_state` files |
| `lib/chapter.py` | `Chapter(bridge, pkg, id, rom)`: `begin(from_state)`, `step`, `macro`, `play_until(milestones)`, `save_sub_state`, `finish()` (mark + raw state + segment + thumb); `stitch(pkg)`, `verify(pkg, rom)` |
| `lib/package.py` | `Package.create/load/save/put_chapter`: the autotas.json manifest (same schema as `AutoTasPackage.cs`) |
| `lib/game.py` | `game.load("smb3")`: `milestone(ids)`, `macro(name)`, `ram(priority)`, `cleanup` |
| `tests/test_package.py` | offline manifest + game-data test |
| `tests/test_fleet_e2e.py` | two chapters over the bridge on a fleet PC, stitch + verify from power-on |

Fleet: the AutoTAS MCP build is on PEARL at `C:\brokennes\autotas\mcp_fd` (hosted by the kit's dotnet.exe; Application
Control blocked it on the other six PCs on 2026-11-15). Bridge option `--remote-exe` (added to cc_pilot/bridge) selects it.
To redeploy: `dotnet build BrokenDev/mcp/BrokenNes.Mcp.csproj -c Release -o <dir>`, zip, scp, expand on the PC, and test one call.

Catalog: `python BrokenDev/tools/catalog/catalog.py autotas-set --console nes --sha1 <id> --manifest <pkg>/autotas.json --status pilot`.

// The music test players (2026-11-10): each game's real sound driver + every track + the FL TEST slot, built by BrokenDev\tools\music\flp\musictest\build-<id>.ps1 -Flp -Out,
// which writes <safe>.<id>.<ext> and <safe>.<id>.fence.txt. For these targets GameTarget.Engine holds the product's extension. Registered in GameTargets.All after the full games.
namespace BrokenNes2.Music.Flp;

public static class MusicTestTargets
{
    private static GameTarget T(string id, string label, string ext, bool plays) => new(id, label + " music test", ext, plays, $@"BrokenDev\tools\music\flp\musictest\build-{id}.ps1");

    public static readonly GameTarget[] All =
    [
        T("vaults-gb-mt", "Vaults GB", "gbc", true),
        T("vaults-gg-mt", "Vaults GG", "gg", true),
        T("ctw-sms-mt", "CTW SMS", "sms", true),
        T("supervrun-snes-mt", "Super VRUN SNES", "sfc", true),
        T("supervrun-wms-mt", "Super VRUN WideMess", "wms", true),
        T("supervrun-md-mt", "Super VRUN MD", "bin", true),
        T("supervrun-dos-mt", "Super VRUN DOS", "exe", false),
        T("supervrun-95-mt", "Super VRUN 95", "exe", false),
        T("vaults-dos-mt", "Vaults DOS", "exe", false),
        T("vaults-95-mt", "Vaults 95", "exe", false),
    ];

    public static bool Is(GameTarget t) => t.Id.EndsWith("-mt", StringComparison.Ordinal);
}

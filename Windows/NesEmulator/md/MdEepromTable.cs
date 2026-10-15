using System;

namespace NesEmulator.Sega;

/// <summary>How a cartridge's EEPROM is wired to the 68000's bus (which addresses and bits carry SDA and SCL, and which bit reads SDA back). The publishers each did it their own way.</summary>
public enum MdEepromWiring
{
    None,
    /// <summary>Sega's own: a byte at $200001, SDA bit 0 and SCL bit 1 (written together), SDA read back in bit 0.</summary>
    Sega,
    /// <summary>Acclaim's: SCL is bit 0 of the byte at $200000 and SDA bit 0 of the byte at $200001 (read back there).</summary>
    Acclaim,
    /// <summary>Codemasters': SDA bit 0 and SCL bit 1 of the byte at $300000, SDA read back in bit 7 of the byte at $380001.</summary>
    Codemasters,
}

/// <summary>
/// Which games carry which serial EEPROM, by the header's serial number, header checksum and ROM length (several Codemasters carts share a generic serial and a copied checksum, so the size joins the key).
/// Each entry was made by running the game and looking at what it does to the bus: the addresses and bits it uses for the two lines, the device and word bytes it sends the chip (see the track B3b record).
/// Games not in the table run without an EEPROM; a header that declares one for a game that is not here is reported by <see cref="MdCartridge.EepromUnsupported"/>.
/// </summary>
public static class MdEepromTable
{
    private readonly record struct Entry(string Serial, int Checksum, int Length, MdEepromChip Chip, MdEepromWiring Wiring, string Games);

    // Checksum -1 matches any. The serial is the header's, trimmed (as MdCartridge.Serial reads it).
    private static readonly Entry[] Entries =
    {
        new("GM MK-1215 -00", -1, -1, MdEepromChip.X24C01, MdEepromWiring.Sega, "Evander Holyfield's Real Deal Boxing"),
        new("GM MK-1228 -00", -1, -1, MdEepromChip.X24C01, MdEepromWiring.Sega, "Greatest Heavyweights of the Ring"),
        new("GM_00004076-00", -1, -1, MdEepromChip.X24C01, MdEepromWiring.Sega, "Dodge Ball - Kuy Kid (Japan)"),
        new("GM G-4060  -00", -1, -1, MdEepromChip.X24C01, MdEepromWiring.Sega, "Wonder Boy in Monster World / Wonder Boy V (Monster World III)"),
        new("GM T-081276 00", -1, -1, MdEepromChip.C24C02, MdEepromWiring.Acclaim, "NFL Quarterback Club"),
        new("GM T-081586-00", -1, -1, MdEepromChip.C24C02, MdEepromWiring.Acclaim, "NFL Quarterback Club 96"),
        new("GM T-81406 -00", -1, -1, MdEepromChip.C24C08, MdEepromWiring.Acclaim, "NBA Jam Tournament Edition (and the Blockbuster compilation of it): the game addresses four 256-byte pages through device bytes A0, A2, A4, A6"),
        new("GM T-081326 01", -1, -1, MdEepromChip.C24C02, MdEepromWiring.Acclaim, "NBA Jam"),
        new("GM T-81576 -00", -1, -1, MdEepromChip.C24C02, MdEepromWiring.Acclaim, "College Slam"),
        new("GM T-81476 -00", -1, -1, MdEepromChip.C24C02, MdEepromWiring.Acclaim, "Frank Thomas Big Hurt Baseball"),
        new("GM T-120106-50", -1, -1, MdEepromChip.C24C08, MdEepromWiring.Codemasters, "Brian Lara Cricket"),
        new("GM T-120146-50", -1, -1, MdEepromChip.C24C08, MdEepromWiring.Codemasters, "Brian Lara Cricket 96"),
        new("GM 00000000-00", 0x168B, 1048576, MdEepromChip.C24C08, MdEepromWiring.Codemasters, "Micro Machines Military - It's a Blast!"),
    };

    /// <summary>Games whose EEPROM is a three-wire 93C46 (<see cref="MdMicrowire"/>) on bits 2, 1, 0 of the byte at $200001 (CS, SK, DI; DO read back in bit 0): Mega Man: The Wily Wars (Europe) and Rockman Megaworld (Japan).</summary>
    private static readonly (string Serial, string Games)[] MicrowireGames =
    {
        ("GM T-12046 -00", "Mega Man: The Wily Wars"),
        ("GM T-12053 -00", "Rockman Megaworld"),
    };

    public static bool LookupMicrowire(string serial)
    {
        foreach (var e in MicrowireGames) if (e.Serial.Equals(serial, StringComparison.Ordinal)) return true;
        return false;
    }

    public static (MdEepromChip chip, MdEepromWiring wiring)? Lookup(string serial, int headerChecksum, int romLength)
    {
        foreach (var e in Entries)
            if (e.Serial.Equals(serial, StringComparison.Ordinal) && (e.Checksum < 0 || e.Checksum == headerChecksum) && (e.Length < 0 || e.Length == romLength))
                return (e.Chip, e.Wiring);
        return null;
    }
}

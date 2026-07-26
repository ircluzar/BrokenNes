using NesEmulator;

namespace BrokenNes.Workshop.Tas.SelfPlay;

/// <summary>
/// Super Mario Bros. 1 RAM addresses and state snapshot, ported address-for-address from the
/// source project's validated SMB1 profile (TAS/fceux_custom/src/input.cpp:301-324, 629-664).
/// These are the well-known public SMB1 disassembly addresses, independently re-confirmed against
/// that project's own working implementation rather than re-derived from scratch.
/// </summary>
public static class Smb1Addr
{
    public const int GameId = 133;

    public const int GameEngine = 0x000E;
    public const int PlayerState = 0x001D;
    public const int LevelDataAddress = 0x06D5;
    public const int ScreenCounter = 0x0760;
    public const int AreaType = 0x074E;
    public const int AreaId = 0x0750;
    public const int OperMode = 0x0770;
    public const int OperModeTask = 0x0772;
    public const int World = 0x075C;
    public const int Level = 0x075F;
    public const int Coins = 0x075E;
    public const int Status = 0x0756;
    public const int Lives = 0x075A;
    public const int MarioPage = 0x006D;
    public const int MarioX = 0x0086;
    public const int MarioY = 0x00CE;
    public const int Page = 0x0760;
    public const int XScroll = 0x071A;
    public const int LevelDataPointer = 0x071C;
    public const int EventMusicQueue = 0x00FC;
    public const int GameTimerExpiredFlag = 0x0759;
    public const int ScoreBase = 0x07DC;
    public const int TimeBase = 0x07F8;

    public const byte EventMusicDeath = 0x01;
}

/// <summary>One frame's worth of SMB1 game state, read fresh each frame and compared against the
/// prior frame's snapshot for edge-triggered event detection (Smb1EventDetection).</summary>
public sealed class Smb1State
{
    public int World, Level, Coins, Status, Lives, Page, XScroll, ScreenCounter;
    public int MarioScreenX, MarioXPosition, MarioYPosition;
    public int LevelDataPointer, LevelDataAddress;
    public int AreaType, AreaId;
    public int Score, Timer;
    public int GameEngine, PlayerState, OperMode, OperModeTask;
    public int EventMusicQueue;

    /// <summary>Screen-scroll position (page*256+xscroll) - distinct from MarioXPosition (the
    /// sprite's true world position). Used only by the flag-like-progress heuristic, matching the
    /// source's own "xposition" field naming.</summary>
    public int XPosition => Page * 256 + XScroll;

    public static Smb1State Read(NES nes)
    {
        var s = new Smb1State();
        byte Ram(int addr) => nes.PeekMemory("System RAM", addr);

        s.World = Ram(Smb1Addr.World);
        s.Level = Ram(Smb1Addr.Level);
        s.Coins = Ram(Smb1Addr.Coins);
        s.Status = Ram(Smb1Addr.Status);
        s.Lives = Ram(Smb1Addr.Lives);
        s.Page = Ram(Smb1Addr.Page);
        s.XScroll = Ram(Smb1Addr.XScroll);
        s.ScreenCounter = Ram(Smb1Addr.ScreenCounter);
        s.MarioScreenX = Ram(Smb1Addr.MarioX);
        s.MarioXPosition = Ram(Smb1Addr.MarioPage) * 256 + s.MarioScreenX;
        s.MarioYPosition = Ram(Smb1Addr.MarioY);
        s.LevelDataPointer = Ram(Smb1Addr.LevelDataPointer);
        // Mirrors the source's own (harmless, pre-existing) "& 0x7FF" wraparound on the high byte read.
        s.LevelDataAddress = Ram(Smb1Addr.LevelDataAddress) | (Ram((Smb1Addr.LevelDataAddress + 1) & 0x7FF) << 8);
        s.AreaType = Ram(Smb1Addr.AreaType);
        s.AreaId = Ram(Smb1Addr.AreaId);
        s.Score = ReadBcdScore(nes);
        s.Timer = ReadBcdTimer(nes);
        s.GameEngine = Ram(Smb1Addr.GameEngine);
        s.PlayerState = Ram(Smb1Addr.PlayerState);
        s.OperMode = Ram(Smb1Addr.OperMode);
        s.OperModeTask = Ram(Smb1Addr.OperModeTask);
        s.EventMusicQueue = Ram(Smb1Addr.EventMusicQueue);
        return s;
    }

    /// <summary>6 BCD digits starting at ScoreBase, last digit implicitly x10 (SMB1 never stores
    /// the trailing zero) - mirrors SMB1Score() at input.cpp:605.</summary>
    private static int ReadBcdScore(NES nes)
    {
        int score = 0;
        for (int i = 0; i < 6; i++)
        {
            int digit = nes.PeekMemory("System RAM", Smb1Addr.ScoreBase + i) & 0x0F;
            score = score * 10 + digit;
        }
        return score * 10;
    }

    /// <summary>3 BCD digits starting at TimeBase - mirrors SMB1Timer() at input.cpp:617.</summary>
    private static int ReadBcdTimer(NES nes)
    {
        int timer = 0;
        for (int i = 0; i < 3; i++)
        {
            int digit = nes.PeekMemory("System RAM", Smb1Addr.TimeBase + i) & 0x0F;
            timer = timer * 10 + digit;
        }
        return timer;
    }
}

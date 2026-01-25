using System;
using System.Collections.Generic;

namespace BrokenNes.Windows.WebApi
{
    internal class PokeRequest
    {
        public string Domain { get; set; } = "";
        public int Address { get; set; }
        public byte Value { get; set; }
    }

    internal class PokeRangeRequest
    {
        public string Domain { get; set; } = "";
        public int Address { get; set; }
        public int[] Data { get; set; } = Array.Empty<int>();
    }

    internal class DomainSelectionRequest
    {
        public string[] SelectedDomains { get; set; } = Array.Empty<string>();
    }

    internal class IntensityRequest
    {
        public int Intensity { get; set; }
    }

    internal class BlastTypeRequest
    {
        public string BlastType { get; set; } = "";
    }

    internal class AutoCorruptRequest
    {
        public bool Enabled { get; set; }
    }

    internal class CrashBehaviorRequest
    {
        public string Behavior { get; set; } = "";
    }

    internal class StubbornModeRequest
    {
        public bool Enabled { get; set; }
    }

    internal class SetRegistersRequest
    {
        public ushort? PC { get; set; }
        public byte? A { get; set; }
        public byte? X { get; set; }
        public byte? Y { get; set; }
        public byte? P { get; set; }
        public ushort? SP { get; set; }
    }

    internal class AddBaseStateRequest
    {
        public string? Name { get; set; }
    }

    internal class SelectBaseRequest
    {
        public string Id { get; set; } = "";
    }

    internal class LoadBaseRequest
    {
        public string? Id { get; set; }
    }

    internal class CorruptAndStashRequest
    {
        public string? Id { get; set; }
    }

    internal class LoadOnOperationRequest
    {
        public bool Enabled { get; set; }
    }

    internal class RenameRequest
    {
        public string Name { get; set; } = "";
    }

    internal class ImportRequest
    {
        public string Json { get; set; } = "";
    }

    internal class EpochRequest
    {
        public int Epoch { get; set; }
    }

    internal class GenerationParamsRequest
    {
        public int? BytesToGenerate { get; set; }
        public float? Temperature { get; set; }
        public int? TopK { get; set; }
    }

    internal class ApplyPatchRequest
    {
        public ushort Pc { get; set; }
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }

    internal class NavigateRequest
    {
        public string Url { get; set; } = "";
    }

    internal class AchievementIdRequest
    {
        public string Id { get; set; } = "";
    }

    internal class AchievementsInitRequest
    {
        public string? GameTitle { get; set; }
        public int? MaxAchievements { get; set; }
        public bool? LoadAll { get; set; }
    }

    internal class SetShaderRequest
    {
        public string ShaderName { get; set; } = "";
    }

    internal class GameSaveDto
    {
        public int Level { get; set; } = 1;
        public bool LevelCleared { get; set; } = false;
        public List<string>? Achievements { get; set; }
        public bool SavestatesUnlocked { get; set; } = false;
        public bool RtcUnlocked { get; set; } = false;
        public bool GhUnlocked { get; set; } = false;
        public bool ImagineUnlocked { get; set; } = false;
        public bool DebugUnlocked { get; set; } = false;
        public bool SeenStory { get; set; } = false;
        public string[]? OwnedCpuIds { get; set; }
        public string[]? OwnedPpuIds { get; set; }
        public string[]? OwnedApuIds { get; set; }
        public string[]? OwnedClockIds { get; set; }
        public string[]? OwnedShaderIds { get; set; }
        public string? PreferredCpuId { get; set; }
        public string? PreferredPpuId { get; set; }
        public string? PreferredApuId { get; set; }
        public string? PreferredShaderId { get; set; }
        public bool PendingDeckContinue { get; set; } = false;
        public string? PendingDeckContinueRom { get; set; }
        public string? PendingDeckContinueTitle { get; set; }
        public DateTime? PendingDeckContinueAtUtc { get; set; }
        public bool UnderConstructionAcknowledged { get; set; } = false;
        public bool AllCoresUnlockedCongrats { get; set; } = false;
        public Dictionary<string, string>? MasqueradeRomToGameId { get; set; }
    }

    internal class AudioPlayRequest
    {
        public string? Filename { get; set; }
        public bool? Loop { get; set; }
        public int? FadeDurationMs { get; set; }
    }

    internal class AudioVolumeRequest
    {
        public float? MusicVolume { get; set; }
        public float? SfxVolume { get; set; }
    }

    internal class LoadBuiltInRomRequest
    {
        public string? Filename { get; set; }
        public bool PreserveShader { get; set; } = false;
    }
}

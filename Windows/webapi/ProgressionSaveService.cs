using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BrokenNes.Models;

namespace BrokenNes.Windows.WebApi
{
    internal sealed class ProgressionSaveService
    {
        private static readonly string[] DefaultUnlockedWebmodules = new[]
        {
            "Home", "Continue", "DeckBuilder", "Cores", "Options", "Story", "RomManager", "HexEditor"
        };
        private static readonly string[] DefaultUnlockedBackgrounds = new[] { "Gradient (Default)", "None (Black)" };
        private static readonly string[] DefaultUnlockedNullProviders = new[] { "Static", "Void" };

        private readonly string _appDataRoot;
        private readonly string _savePath;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public ProgressionSaveService()
        {
            _appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BrokenNes"
            );
            _savePath = Path.Combine(_appDataRoot, "gamesave.json");
        }

        public async Task<GameSave> LoadAsync()
        {
            try
            {
                if (!File.Exists(_savePath))
                {
                    return CreateDefaultSave();
                }

                var json = await File.ReadAllTextAsync(_savePath).ConfigureAwait(false);
                var save = JsonSerializer.Deserialize<GameSave>(json, _jsonOptions) ?? CreateDefaultSave();
                Normalize(save);
                return save;
            }
            catch
            {
                return CreateDefaultSave();
            }
        }

        public async Task SaveAsync(GameSave save)
        {
            Directory.CreateDirectory(_appDataRoot);
            Normalize(save);
            var json = JsonSerializer.Serialize(save, _jsonOptions);
            await File.WriteAllTextAsync(_savePath, json).ConfigureAwait(false);
        }

        public async Task<GameSave> ResetAsync()
        {
            var save = CreateDefaultSave();
            await SaveAsync(save).ConfigureAwait(false);
            DeleteContinueStateDirectory("ContinueStates");
            DeleteContinueStateDirectory("DeckContinueStates");
            return save;
        }

        public async Task<GameSave> MergeAndSaveAsync(
            GameSave incoming,
            IEnumerable<string>? availableBackgrounds = null,
            IEnumerable<string>? availableNullProviders = null)
        {
            var hasExistingSave = File.Exists(_savePath);
            var existing = hasExistingSave ? await LoadAsync().ConfigureAwait(false) : CreateDefaultSave();
            var merged = MergeState(existing, incoming);

            if (hasExistingSave)
            {
                ApplyAchievementRewards(existing, merged, availableBackgrounds, availableNullProviders);
                ApplyMilestoneRewards(existing, merged);
            }
            else
            {
                BackfillProgressionFromLegacyState(merged);
            }

            DeriveLegacyFeatureFlags(merged);
            await SaveAsync(merged).ConfigureAwait(false);
            return merged;
        }

        public async Task<IReadOnlyList<PendingUnlockBundle>> ClaimPendingAsync()
        {
            var save = await LoadAsync().ConfigureAwait(false);
            return save.PendingUnlocks.Where(bundle => !bundle.Presented).ToList();
        }

        public async Task<int> AcknowledgePendingAsync(IEnumerable<string> rewardIds)
        {
            var requestedIds = new HashSet<string>((rewardIds ?? Array.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
            if (requestedIds.Count == 0)
            {
                return 0;
            }

            var save = await LoadAsync().ConfigureAwait(false);
            var updatedCount = 0;
            foreach (var bundle in save.PendingUnlocks)
            {
                if (!bundle.Presented && requestedIds.Contains(bundle.Id))
                {
                    bundle.Presented = true;
                    updatedCount++;
                }
            }

            if (updatedCount > 0)
            {
                await SaveAsync(save).ConfigureAwait(false);
            }

            return updatedCount;
        }

        public async Task<bool> SetPreferredBackgroundAsync(string backgroundId)
        {
            var save = await LoadAsync().ConfigureAwait(false);
            if (!IsBackgroundUnlocked(save, backgroundId))
            {
                return false;
            }

            save.PreferredBackgroundId = NormalizePreferredValue(backgroundId, save.UnlockedBackgrounds, "Gradient");
            await SaveAsync(save).ConfigureAwait(false);
            return true;
        }

        public async Task<bool> SetPreferredNullProviderAsync(string nullProviderId)
        {
            var save = await LoadAsync().ConfigureAwait(false);
            if (!IsNullProviderUnlocked(save, nullProviderId))
            {
                return false;
            }

            save.PreferredNullProviderId = NormalizePreferredValue(nullProviderId, save.UnlockedNullProviders, "Static");
            await SaveAsync(save).ConfigureAwait(false);
            return true;
        }

        public async Task<GameSave> UnlockEverythingAsync(
            IEnumerable<string>? cpuIds = null,
            IEnumerable<string>? ppuIds = null,
            IEnumerable<string>? apuIds = null,
            IEnumerable<string>? clockIds = null,
            IEnumerable<string>? shaderIds = null,
            IEnumerable<string>? webmoduleIds = null,
            IEnumerable<string>? backgroundIds = null,
            IEnumerable<string>? nullProviderIds = null)
        {
            var save = await LoadAsync().ConfigureAwait(false);

            save.OwnedCpuIds = NormalizeStringList(cpuIds, save.OwnedCpuIds);
            save.OwnedPpuIds = NormalizeStringList(ppuIds, save.OwnedPpuIds);
            save.OwnedApuIds = NormalizeStringList(apuIds, save.OwnedApuIds);
            save.OwnedClockIds = NormalizeStringList(clockIds, save.OwnedClockIds);
            save.OwnedShaderIds = NormalizeStringList(shaderIds, save.OwnedShaderIds);
            save.UnlockedWebmodules = NormalizeStringList(webmoduleIds, save.UnlockedWebmodules);
            save.UnlockedBackgrounds = NormalizeStringList(backgroundIds, save.UnlockedBackgrounds, NormalizeBackgroundId);
            save.UnlockedNullProviders = NormalizeStringList(nullProviderIds, save.UnlockedNullProviders);

            save.SavestatesUnlocked = true;
            save.RtcUnlocked = true;
            save.GhUnlocked = true;
            save.ImagineUnlocked = true;
            save.DebugUnlocked = true;
            save.AllCoresUnlockedCongrats = true;
            save.PendingUnlocks.Clear();

            save.PreferredCpuId = NormalizePreferredValue(save.PreferredCpuId, save.OwnedCpuIds, "FMC");
            save.PreferredPpuId = NormalizePreferredValue(save.PreferredPpuId, save.OwnedPpuIds, "FMC");
            save.PreferredApuId = NormalizePreferredValue(save.PreferredApuId, save.OwnedApuIds, "FMC");
            save.PreferredShaderId = NormalizePreferredValue(save.PreferredShaderId, save.OwnedShaderIds, "PX");
            save.PreferredBackgroundId = NormalizePreferredValue(save.PreferredBackgroundId, save.UnlockedBackgrounds, "Gradient (Default)", NormalizeBackgroundId);
            save.PreferredNullProviderId = NormalizePreferredValue(save.PreferredNullProviderId, save.UnlockedNullProviders, "Static");

            Normalize(save);
            await SaveAsync(save).ConfigureAwait(false);
            return save;
        }

        public bool IsBackgroundUnlocked(GameSave save, string backgroundId)
            => save.UnlockedBackgrounds.Any(value => value.Equals(backgroundId, StringComparison.OrdinalIgnoreCase));

        public bool IsNullProviderUnlocked(GameSave save, string nullProviderId)
            => save.UnlockedNullProviders.Any(value => value.Equals(nullProviderId, StringComparison.OrdinalIgnoreCase));

        public bool IsWebmoduleUnlocked(GameSave save, string moduleId)
            => save.UnlockedWebmodules.Any(value => value.Equals(moduleId, StringComparison.OrdinalIgnoreCase));

        private GameSave CreateDefaultSave()
        {
            var save = new GameSave
            {
                Level = 1,
                LevelCleared = false,
                Achievements = new(),
                SavestatesUnlocked = false,
                RtcUnlocked = false,
                GhUnlocked = false,
                ImagineUnlocked = false,
                DebugUnlocked = false,
                SeenStory = false,
                OwnedCpuIds = new() { "FMC" },
                OwnedPpuIds = new() { "FMC" },
                OwnedApuIds = new() { "FMC" },
                OwnedClockIds = new() { "FMC" },
                OwnedShaderIds = new() { "PX" },
                PreferredCpuId = "FMC",
                PreferredPpuId = "FMC",
                PreferredApuId = "FMC",
                PreferredShaderId = "PX",
                UnlockedWebmodules = DefaultUnlockedWebmodules.ToList(),
                UnlockedBackgrounds = DefaultUnlockedBackgrounds.ToList(),
                UnlockedNullProviders = DefaultUnlockedNullProviders.ToList(),
                PreferredBackgroundId = "Gradient (Default)",
                PreferredNullProviderId = "Static",
                PendingUnlocks = new(),
                ContinueSlots = new(),
                MasqueradeRomToGameId = new()
            };
            Normalize(save);
            return save;
        }

        private void Normalize(GameSave save)
        {
            save.Level = Math.Max(1, save.Level);
            save.Achievements ??= new();
            save.OwnedCpuIds ??= new();
            save.OwnedPpuIds ??= new();
            save.OwnedApuIds ??= new();
            save.OwnedClockIds ??= new();
            save.OwnedShaderIds ??= new();
            save.PreferredCpuId ??= "FMC";
            save.PreferredPpuId ??= "FMC";
            save.PreferredApuId ??= "FMC";
            save.PreferredShaderId ??= "PX";
            save.UnlockedWebmodules = NormalizeStringList(save.UnlockedWebmodules, DefaultUnlockedWebmodules);
            save.UnlockedBackgrounds = NormalizeStringList(save.UnlockedBackgrounds, DefaultUnlockedBackgrounds, NormalizeBackgroundId);
            save.UnlockedNullProviders = NormalizeStringList(save.UnlockedNullProviders, DefaultUnlockedNullProviders);
            save.PreferredBackgroundId = NormalizePreferredValue(save.PreferredBackgroundId, save.UnlockedBackgrounds, "Gradient (Default)", NormalizeBackgroundId);
            save.PreferredNullProviderId = NormalizePreferredValue(save.PreferredNullProviderId, save.UnlockedNullProviders, "Static");
            save.PendingUnlocks ??= new();
            save.ContinueSlots ??= new();
            save.MasqueradeRomToGameId ??= new();
            NormalizePendingUnlocks(save.PendingUnlocks, save.PreferredBackgroundId, save.PreferredNullProviderId);
        }

        private static List<string> NormalizeStringList(IEnumerable<string>? values, IEnumerable<string>? defaults, Func<string, string>? normalizer = null)
        {
            var result = new List<string>();

            static void AppendUnique(List<string> target, IEnumerable<string>? source, Func<string, string>? normalizer)
            {
                if (source == null)
                {
                    return;
                }

                foreach (var value in source)
                {
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    var normalizedValue = normalizer != null ? normalizer(value) : value.Trim();
                    if (string.IsNullOrWhiteSpace(normalizedValue))
                    {
                        continue;
                    }

                    if (!target.Any(existing => existing.Equals(normalizedValue, StringComparison.OrdinalIgnoreCase)))
                    {
                        target.Add(normalizedValue);
                    }
                }
            }

            AppendUnique(result, defaults, normalizer);
            AppendUnique(result, values, normalizer);
            return result;
        }

        private static string NormalizePreferredValue(string? preferredValue, IReadOnlyCollection<string> unlockedValues, string fallback, Func<string, string>? normalizer = null)
        {
            var normalizedPreferredValue = string.IsNullOrWhiteSpace(preferredValue)
                ? null
                : normalizer != null ? normalizer(preferredValue) : preferredValue.Trim();
            var normalizedFallback = normalizer != null ? normalizer(fallback) : fallback;

            if (!string.IsNullOrWhiteSpace(normalizedPreferredValue))
            {
                var match = unlockedValues.FirstOrDefault(value => value.Equals(normalizedPreferredValue, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            var fallbackMatch = unlockedValues.FirstOrDefault(value => value.Equals(normalizedFallback, StringComparison.OrdinalIgnoreCase));
            if (fallbackMatch != null)
            {
                return fallbackMatch;
            }

            return unlockedValues.FirstOrDefault() ?? normalizedFallback;
        }

        private static string NormalizeBackgroundId(string value)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (trimmed.Equals("Gradient", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Gradient (Default)", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("StaticGradient", StringComparison.OrdinalIgnoreCase))
            {
                return "Gradient (Default)";
            }

            if (trimmed.Equals("Black", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("None", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("None (Black)", StringComparison.OrdinalIgnoreCase))
            {
                return "None (Black)";
            }

            return trimmed;
        }

        private static void NormalizePendingUnlocks(List<PendingUnlockBundle> pendingUnlocks, string? preferredBackgroundId, string? preferredNullProviderId)
        {
            foreach (var bundle in pendingUnlocks)
            {
                bundle.Id = string.IsNullOrWhiteSpace(bundle.Id) ? Guid.NewGuid().ToString("N") : bundle.Id;
                bundle.Source ??= string.Empty;
                bundle.Items ??= new();

                foreach (var item in bundle.Items)
                {
                    item.Id = item.Id?.Trim() ?? string.Empty;
                    item.Type = item.Type?.Trim() ?? string.Empty;

                    item.IsEquipped = item.Type.Equals("background", StringComparison.OrdinalIgnoreCase)
                        ? item.Id.Equals(preferredBackgroundId, StringComparison.OrdinalIgnoreCase)
                        : item.Type.Equals("nullProvider", StringComparison.OrdinalIgnoreCase)
                            ? item.Id.Equals(preferredNullProviderId, StringComparison.OrdinalIgnoreCase)
                            : item.IsEquipped;
                }
            }
        }

        private GameSave MergeState(GameSave existing, GameSave incoming)
        {
            NormalizeDictionaries(existing);
            NormalizeDictionaries(incoming);

            var merged = new GameSave
            {
                Level = Math.Max(1, incoming.Level),
                LevelCleared = incoming.LevelCleared,
                Achievements = NormalizeStringList(incoming.Achievements, null),
                SavestatesUnlocked = incoming.SavestatesUnlocked,
                RtcUnlocked = incoming.RtcUnlocked,
                GhUnlocked = incoming.GhUnlocked,
                ImagineUnlocked = incoming.ImagineUnlocked,
                DebugUnlocked = incoming.DebugUnlocked,
                SeenStory = incoming.SeenStory,
                OwnedCpuIds = NormalizeStringList(incoming.OwnedCpuIds, null),
                OwnedPpuIds = NormalizeStringList(incoming.OwnedPpuIds, null),
                OwnedApuIds = NormalizeStringList(incoming.OwnedApuIds, null),
                OwnedClockIds = NormalizeStringList(incoming.OwnedClockIds, null),
                OwnedShaderIds = NormalizeStringList(incoming.OwnedShaderIds, null),
                PreferredCpuId = incoming.PreferredCpuId ?? existing.PreferredCpuId,
                PreferredPpuId = incoming.PreferredPpuId ?? existing.PreferredPpuId,
                PreferredApuId = incoming.PreferredApuId ?? existing.PreferredApuId,
                PreferredShaderId = incoming.PreferredShaderId ?? existing.PreferredShaderId,
                UnlockedWebmodules = NormalizeStringList(incoming.UnlockedWebmodules, null),
                UnlockedBackgrounds = NormalizeStringList(incoming.UnlockedBackgrounds, null, NormalizeBackgroundId),
                UnlockedNullProviders = NormalizeStringList(incoming.UnlockedNullProviders, null),
                PendingUnlocks = MergePendingUnlocks(incoming.PendingUnlocks, null),
                PendingDeckContinue = incoming.PendingDeckContinue,
                PendingDeckContinueRom = string.IsNullOrWhiteSpace(incoming.PendingDeckContinueRom) ? null : incoming.PendingDeckContinueRom,
                PendingDeckContinueTitle = string.IsNullOrWhiteSpace(incoming.PendingDeckContinueTitle) ? null : incoming.PendingDeckContinueTitle,
                PendingDeckContinueAtUtc = incoming.PendingDeckContinueAtUtc,
                ContinueSlots = MergeContinueSlots(null, incoming.ContinueSlots),
                UnderConstructionAcknowledged = incoming.UnderConstructionAcknowledged,
                AllCoresUnlockedCongrats = incoming.AllCoresUnlockedCongrats,
                MasqueradeRomToGameId = MergeStringDictionary(null, incoming.MasqueradeRomToGameId)
            };

            merged.PreferredBackgroundId = NormalizePreferredValue(
                incoming.PreferredBackgroundId ?? existing.PreferredBackgroundId,
                merged.UnlockedBackgrounds,
                "Gradient (Default)",
                NormalizeBackgroundId);
            merged.PreferredNullProviderId = NormalizePreferredValue(
                incoming.PreferredNullProviderId ?? existing.PreferredNullProviderId,
                merged.UnlockedNullProviders,
                "Static");
            Normalize(merged);
            return merged;
        }

        private static void NormalizeDictionaries(GameSave save)
        {
            save.ContinueSlots ??= new();
            save.MasqueradeRomToGameId ??= new();
        }

        private static List<PendingUnlockBundle> MergePendingUnlocks(IEnumerable<PendingUnlockBundle>? existing, IEnumerable<PendingUnlockBundle>? incoming)
        {
            var merged = new List<PendingUnlockBundle>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRange(IEnumerable<PendingUnlockBundle>? source)
            {
                if (source == null)
                {
                    return;
                }

                foreach (var bundle in source)
                {
                    var bundleId = string.IsNullOrWhiteSpace(bundle?.Id) ? Guid.NewGuid().ToString("N") : bundle.Id;
                    if (!seenIds.Add(bundleId))
                    {
                        continue;
                    }

                    merged.Add(new PendingUnlockBundle
                    {
                        Id = bundleId,
                        Source = bundle?.Source ?? string.Empty,
                        AchievementId = bundle?.AchievementId,
                        LevelIndex = bundle?.LevelIndex,
                        CreatedAtUtc = bundle?.CreatedAtUtc == default ? DateTime.UtcNow : bundle.CreatedAtUtc,
                        Presented = bundle?.Presented ?? false,
                        Items = (bundle?.Items ?? new List<UnlockRewardItem>())
                            .Select(item => new UnlockRewardItem
                            {
                                Id = item.Id,
                                Type = item.Type,
                                Title = item.Title,
                                Subtitle = item.Subtitle,
                                Description = item.Description,
                                CanEquip = item.CanEquip,
                                IsEquipped = item.IsEquipped,
                                EquipAction = item.EquipAction
                            })
                            .ToList()
                    });
                }
            }

            AddRange(existing);
            AddRange(incoming);
            return merged;
        }

        private static Dictionary<string, ContinueStateSlot> MergeContinueSlots(
            IDictionary<string, ContinueStateSlot>? existing,
            IDictionary<string, ContinueStateSlot>? incoming)
        {
            var merged = new Dictionary<string, ContinueStateSlot>(StringComparer.OrdinalIgnoreCase);

            void AddRange(IDictionary<string, ContinueStateSlot>? source)
            {
                if (source == null)
                {
                    return;
                }

                foreach (var entry in source)
                {
                    merged[entry.Key] = new ContinueStateSlot
                    {
                        RomKey = entry.Value?.RomKey ?? entry.Key,
                        Title = entry.Value?.Title,
                        UpdatedAtUtc = entry.Value?.UpdatedAtUtc,
                        PreviewImagePath = entry.Value?.PreviewImagePath
                    };
                }
            }

            AddRange(existing);
            AddRange(incoming);
            return merged;
        }

        private static Dictionary<string, string> MergeStringDictionary(
            IDictionary<string, string>? existing,
            IDictionary<string, string>? incoming)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void AddRange(IDictionary<string, string>? source)
            {
                if (source == null)
                {
                    return;
                }

                foreach (var entry in source)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key) || string.IsNullOrWhiteSpace(entry.Value))
                    {
                        continue;
                    }

                    merged[entry.Key] = entry.Value;
                }
            }

            AddRange(existing);
            AddRange(incoming);
            return merged;
        }

        private void BackfillProgressionFromLegacyState(GameSave save)
        {
            if (save.Level > 4 || save.RtcUnlocked || save.GhUnlocked)
            {
                UnlockWebmodule(save, "GlitchHarvester");
            }

            if (save.Level > 8 || save.SavestatesUnlocked)
            {
                UnlockWebmodule(save, "TimeJump");
            }

            if (save.Level > 12)
            {
                UnlockWebmodule(save, "CorruptionSlop");
            }

            if (save.Level > 16 || save.ImagineUnlocked)
            {
                UnlockWebmodule(save, "ImagineBug");
            }
        }

        private void ApplyAchievementRewards(
            GameSave existing,
            GameSave save,
            IEnumerable<string>? availableBackgrounds,
            IEnumerable<string>? availableNullProviders)
        {
            var existingAchievements = new HashSet<string>(existing.Achievements ?? new(), StringComparer.OrdinalIgnoreCase);
            var newlyUnlockedAchievements = (save.Achievements ?? new())
                .Where(id => !string.IsNullOrWhiteSpace(id) && !existingAchievements.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var achievementId in newlyUnlockedAchievements)
            {
                var bundleId = $"achievement:{achievementId}";
                if (save.PendingUnlocks.Any(bundle => bundle.Id.Equals(bundleId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var items = new List<UnlockRewardItem>();
                var backgroundReward = PickDeterministicReward(
                    $"{achievementId}|background",
                    (availableBackgrounds ?? Array.Empty<string>())
                        .Select(NormalizeBackgroundId)
                        .Where(name => !string.IsNullOrWhiteSpace(name)
                            && !name.Equals("---", StringComparison.OrdinalIgnoreCase)
                            && !DefaultUnlockedBackgrounds.Contains(name, StringComparer.OrdinalIgnoreCase)
                            && !save.UnlockedBackgrounds.Any(unlocked => unlocked.Equals(name, StringComparison.OrdinalIgnoreCase))));

                if (!string.IsNullOrWhiteSpace(backgroundReward) && UnlockBackground(save, backgroundReward))
                {
                    items.Add(new UnlockRewardItem
                    {
                        Id = backgroundReward,
                        Type = "background",
                        Title = backgroundReward,
                            Subtitle = "BG",
                            Description = string.Empty,
                        CanEquip = true,
                        IsEquipped = string.Equals(save.PreferredBackgroundId, backgroundReward, StringComparison.OrdinalIgnoreCase),
                        EquipAction = "equip-background"
                    });
                }

                var nullProviderReward = PickDeterministicReward(
                    $"{achievementId}|null-provider",
                    (availableNullProviders ?? Array.Empty<string>())
                        .Where(name => !string.IsNullOrWhiteSpace(name)
                            && !DefaultUnlockedNullProviders.Contains(name, StringComparer.OrdinalIgnoreCase)
                            && !save.UnlockedNullProviders.Any(unlocked => unlocked.Equals(name, StringComparison.OrdinalIgnoreCase))));

                if (!string.IsNullOrWhiteSpace(nullProviderReward) && UnlockNullProvider(save, nullProviderReward))
                {
                    items.Add(new UnlockRewardItem
                    {
                        Id = nullProviderReward,
                        Type = "nullProvider",
                        Title = nullProviderReward,
                            Subtitle = "NULL",
                            Description = string.Empty,
                        CanEquip = true,
                        IsEquipped = string.Equals(save.PreferredNullProviderId, nullProviderReward, StringComparison.OrdinalIgnoreCase),
                        EquipAction = "equip-null-provider"
                    });
                }

                if (items.Count == 0)
                {
                    continue;
                }

                save.PendingUnlocks.Add(new PendingUnlockBundle
                {
                    Id = bundleId,
                    Source = "achievement",
                    AchievementId = achievementId,
                    CreatedAtUtc = DateTime.UtcNow,
                    Presented = false,
                    Items = items
                });
            }
        }

        private void ApplyMilestoneRewards(GameSave existing, GameSave save)
        {
            if (save.Level <= existing.Level)
            {
                return;
            }

            for (var completedLevel = existing.Level; completedLevel < save.Level; completedLevel++)
            {
                var bundleId = $"level:{completedLevel}";
                if (save.PendingUnlocks.Any(bundle => bundle.Id.Equals(bundleId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var items = BuildMilestoneRewardItems(save, completedLevel);
                if (items.Count == 0)
                {
                    continue;
                }

                save.PendingUnlocks.Add(new PendingUnlockBundle
                {
                    Id = bundleId,
                    Source = "level",
                    LevelIndex = completedLevel,
                    CreatedAtUtc = DateTime.UtcNow,
                    Presented = false,
                    Items = items
                });
            }
        }

        private List<UnlockRewardItem> BuildMilestoneRewardItems(GameSave save, int completedLevel)
        {
            var items = new List<UnlockRewardItem>();

            if (completedLevel == 4 && UnlockWebmodule(save, "GlitchHarvester"))
            {
                items.Add(new UnlockRewardItem
                {
                    Id = "GlitchHarvester",
                    Type = "webmodule",
                    Title = "RTC + Glitch Harvester",
                    Subtitle = "Milestone module unlocked",
                    Description = "The corruption workflow stack is now available.",
                    CanEquip = false,
                    EquipAction = "open-webmodule"
                });
            }

            if (completedLevel == 8 && UnlockWebmodule(save, "TimeJump"))
            {
                items.Add(new UnlockRewardItem
                {
                    Id = "TimeJump",
                    Type = "webmodule",
                    Title = "Time Jump",
                    Subtitle = "Milestone module unlocked",
                    Description = "Temporal state tooling is now available.",
                    CanEquip = false,
                    EquipAction = "open-webmodule"
                });
            }

            if (completedLevel == 12 && UnlockWebmodule(save, "CorruptionSlop"))
            {
                items.Add(new UnlockRewardItem
                {
                    Id = "CorruptionSlop",
                    Type = "webmodule",
                    Title = "Corruption Slop",
                    Subtitle = "Milestone module unlocked",
                    Description = "Automated corruption flow is now available.",
                    CanEquip = false,
                    EquipAction = "open-webmodule"
                });
            }

            if (completedLevel == 16 && UnlockWebmodule(save, "ImagineBug"))
            {
                items.Add(new UnlockRewardItem
                {
                    Id = "ImagineBug",
                    Type = "webmodule",
                    Title = "ImagineBug",
                    Subtitle = "Milestone module unlocked",
                    Description = "Late-game advanced corruption tooling is now available.",
                    CanEquip = false,
                    EquipAction = "open-webmodule"
                });
            }

            return items;
        }

        private static void DeriveLegacyFeatureFlags(GameSave save)
        {
            var hasRtcStack = save.UnlockedWebmodules.Any(value => value.Equals("GlitchHarvester", StringComparison.OrdinalIgnoreCase));
            var hasTimeJump = save.UnlockedWebmodules.Any(value => value.Equals("TimeJump", StringComparison.OrdinalIgnoreCase));
            var hasImagineBug = save.UnlockedWebmodules.Any(value => value.Equals("ImagineBug", StringComparison.OrdinalIgnoreCase));

            save.RtcUnlocked = save.RtcUnlocked || hasRtcStack;
            save.GhUnlocked = save.GhUnlocked || hasRtcStack;
            save.SavestatesUnlocked = save.SavestatesUnlocked || hasTimeJump;
            save.ImagineUnlocked = save.ImagineUnlocked || hasImagineBug;
        }

        private static bool UnlockWebmodule(GameSave save, string moduleId)
            => AddIfMissing(save.UnlockedWebmodules, moduleId);

        private static bool UnlockBackground(GameSave save, string backgroundId)
            => AddIfMissing(save.UnlockedBackgrounds, NormalizeBackgroundId(backgroundId));

        private static bool UnlockNullProvider(GameSave save, string nullProviderId)
            => AddIfMissing(save.UnlockedNullProviders, nullProviderId);

        private static bool AddIfMissing(List<string> values, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (values.Any(existing => existing.Equals(value, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            values.Add(value);
            return true;
        }

        private static string? PickDeterministicReward(string seed, IEnumerable<string> candidates)
        {
            var orderedCandidates = candidates
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (orderedCandidates.Count == 0)
            {
                return null;
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            var index = (int)(BitConverter.ToUInt32(hash, 0) % (uint)orderedCandidates.Count);
            return orderedCandidates[index];
        }

        private void DeleteContinueStateDirectory(string directoryName)
        {
            var directoryPath = Path.Combine(_appDataRoot, directoryName);
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch
            {
                // Leave any locked continue artifacts in place rather than failing the save reset.
            }
        }
    }
}
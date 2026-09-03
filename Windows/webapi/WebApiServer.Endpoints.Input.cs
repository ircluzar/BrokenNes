using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>
    /// Process-wide latch that lets the local Web API hold NES controller buttons down on behalf
    /// of an automated test, without taking control away from the real keyboard/gamepad.
    ///
    /// The desktop emulation loop (MainForm.Emulation.cs) polls the physical devices every frame
    /// and then ORs this latch in before calling <c>nes.SetInputs</c>. Consequences of that shape:
    ///   * an empty latch is a no-op, so normal play is completely unaffected when unused;
    ///   * an injected button is additive - it can never mask a key the player is actually holding;
    ///   * the latch persists until explicitly cleared (or until its optional hold expires), which
    ///     is what makes "press and hold Right for 30 frames" expressible over a stateless HTTP API.
    ///
    /// Static rather than an instance member of <see cref="WebApiServer"/> so the emulation thread
    /// can consult it directly on the hot path without another delegate threaded through the
    /// server's constructor, and so its cost when unused is a single volatile read.
    /// </summary>
    public static class ApiInputInjector
    {
        public const int ButtonCount = 8;

        /// <summary>NES button order used everywhere in this codebase (index == bit order).</summary>
        private static readonly string[] ButtonNames =
        {
            "A", "B", "Select", "Start", "Up", "Down", "Left", "Right"
        };

        private const int PlayerCount = 2;

        private static readonly object Gate = new();

        // [player][button]; player index 0 == player 1.
        private static readonly bool[][] Injected =
        {
            new bool[ButtonCount],
            new bool[ButtonCount]
        };

        // UTC ticks at which the corresponding player's latch auto-releases; 0 == hold forever.
        private static readonly long[] ExpiresAtUtcTicks = new long[PlayerCount];

        // Last state actually handed to the NES, published by the emulation loop.
        private static readonly bool[][] Observed =
        {
            new bool[ButtonCount],
            new bool[ButtonCount]
        };

        private static long _lastObservedUtcTicks;
        private static long _observedFrameCount;

        /// <summary>Cheap "is anything latched at all" flag so the hot path can bail immediately.</summary>
        private static int _anyInjected;

        public static bool AnyInjected => Volatile.Read(ref _anyInjected) != 0;

        public static string[] GetButtonNames() => (string[])ButtonNames.Clone();

        /// <summary>
        /// Accepts a button name ("a", "Start", "dpad-right" style variants) or a raw index 0-7.
        /// </summary>
        public static bool TryParseButton(string? token, out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(token)) return false;

            var trimmed = token.Trim();

            if (int.TryParse(trimmed, out var numeric))
            {
                if (numeric < 0 || numeric >= ButtonCount) return false;
                index = numeric;
                return true;
            }

            for (int i = 0; i < ButtonNames.Length; i++)
            {
                if (string.Equals(ButtonNames[i], trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    return true;
                }
            }

            // A few forgiving aliases so callers do not have to guess.
            switch (trimmed.ToLowerInvariant())
            {
                case "sel": index = 2; return true;
                case "u": index = 4; return true;
                case "d": index = 5; return true;
                case "l": index = 6; return true;
                case "r": index = 7; return true;
                default: return false;
            }
        }

        public static bool IsValidPlayer(int player) => player >= 1 && player <= PlayerCount;

        /// <summary>
        /// Latch the given held-button set for a player, replacing whatever was latched before.
        /// An empty set releases everything for that player.
        /// </summary>
        /// <param name="holdMs">
        /// Optional auto-release. Null/0 means "hold until explicitly changed or cleared"; a
        /// positive value releases the latch automatically, which keeps a crashed or abandoned
        /// test from leaving the pad wedged.
        /// </param>
        public static void Set(int player, IReadOnlyCollection<int> buttonIndices, int? holdMs)
        {
            if (!IsValidPlayer(player)) throw new ArgumentOutOfRangeException(nameof(player));

            var slot = player - 1;
            lock (Gate)
            {
                Array.Clear(Injected[slot], 0, ButtonCount);
                foreach (var index in buttonIndices)
                {
                    if (index >= 0 && index < ButtonCount)
                    {
                        Injected[slot][index] = true;
                    }
                }

                ExpiresAtUtcTicks[slot] = holdMs.HasValue && holdMs.Value > 0
                    ? DateTime.UtcNow.AddMilliseconds(holdMs.Value).Ticks
                    : 0;

                RecomputeAnyInjectedLocked();
            }
        }

        public static void Clear(int player)
        {
            if (!IsValidPlayer(player)) throw new ArgumentOutOfRangeException(nameof(player));

            var slot = player - 1;
            lock (Gate)
            {
                Array.Clear(Injected[slot], 0, ButtonCount);
                ExpiresAtUtcTicks[slot] = 0;
                RecomputeAnyInjectedLocked();
            }
        }

        public static void ClearAll()
        {
            lock (Gate)
            {
                for (int slot = 0; slot < PlayerCount; slot++)
                {
                    Array.Clear(Injected[slot], 0, ButtonCount);
                    ExpiresAtUtcTicks[slot] = 0;
                }
                RecomputeAnyInjectedLocked();
            }
        }

        /// <summary>
        /// ORs the latched buttons into the given per-frame input arrays. Either array may be null
        /// (callers that only care about player 1). Called once per emulated frame - it must stay
        /// allocation-free and must do nothing when no injection is active.
        /// </summary>
        public static void Apply(bool[]? player1, bool[]? player2)
        {
            if (Volatile.Read(ref _anyInjected) == 0) return;

            lock (Gate)
            {
                ExpireLocked();

                if (player1 != null)
                {
                    var source = Injected[0];
                    for (int i = 0; i < ButtonCount && i < player1.Length; i++)
                    {
                        if (source[i]) player1[i] = true;
                    }
                }

                if (player2 != null)
                {
                    var source = Injected[1];
                    for (int i = 0; i < ButtonCount && i < player2.Length; i++)
                    {
                        if (source[i]) player2[i] = true;
                    }
                }
            }
        }

        /// <summary>
        /// Records the state actually handed to the NES this frame, so GET /api/input/state can
        /// report what the emulator believes is held rather than only what was requested.
        /// </summary>
        public static void PublishObserved(bool[]? player1, bool[]? player2)
        {
            lock (Gate)
            {
                if (player1 != null)
                {
                    for (int i = 0; i < ButtonCount; i++)
                    {
                        Observed[0][i] = i < player1.Length && player1[i];
                    }
                }

                if (player2 != null)
                {
                    for (int i = 0; i < ButtonCount; i++)
                    {
                        Observed[1][i] = i < player2.Length && player2[i];
                    }
                }

                _lastObservedUtcTicks = DateTime.UtcNow.Ticks;
                _observedFrameCount++;
            }
        }

        /// <summary>
        /// Immutable view of both players for the state endpoint.
        /// </summary>
        public static InjectorSnapshot Snapshot()
        {
            lock (Gate)
            {
                ExpireLocked();

                var players = new PlayerSnapshot[PlayerCount];
                for (int slot = 0; slot < PlayerCount; slot++)
                {
                    players[slot] = new PlayerSnapshot(
                        slot + 1,
                        (bool[])Injected[slot].Clone(),
                        (bool[])Observed[slot].Clone(),
                        ExpiresAtUtcTicks[slot] == 0
                            ? (double?)null
                            : Math.Max(0, (ExpiresAtUtcTicks[slot] - DateTime.UtcNow.Ticks) / (double)TimeSpan.TicksPerMillisecond));
                }

                double? observedAgeMs = _lastObservedUtcTicks == 0
                    ? null
                    : (DateTime.UtcNow.Ticks - _lastObservedUtcTicks) / (double)TimeSpan.TicksPerMillisecond;

                return new InjectorSnapshot(players, observedAgeMs, _observedFrameCount);
            }
        }

        public static string[] NamesOf(bool[] state)
        {
            var names = new List<string>(ButtonCount);
            for (int i = 0; i < ButtonCount && i < state.Length; i++)
            {
                if (state[i]) names.Add(ButtonNames[i]);
            }
            return names.ToArray();
        }

        private static void ExpireLocked()
        {
            var now = DateTime.UtcNow.Ticks;
            bool changed = false;

            for (int slot = 0; slot < PlayerCount; slot++)
            {
                if (ExpiresAtUtcTicks[slot] != 0 && ExpiresAtUtcTicks[slot] <= now)
                {
                    Array.Clear(Injected[slot], 0, ButtonCount);
                    ExpiresAtUtcTicks[slot] = 0;
                    changed = true;
                }
            }

            if (changed)
            {
                RecomputeAnyInjectedLocked();
            }
        }

        private static void RecomputeAnyInjectedLocked()
        {
            bool any = false;
            for (int slot = 0; slot < PlayerCount && !any; slot++)
            {
                for (int i = 0; i < ButtonCount; i++)
                {
                    if (Injected[slot][i]) { any = true; break; }
                }
            }
            Volatile.Write(ref _anyInjected, any ? 1 : 0);
        }

        public sealed record PlayerSnapshot(int Player, bool[] Injected, bool[] Observed, double? HoldRemainingMs);

        public sealed record InjectorSnapshot(PlayerSnapshot[] Players, double? ObservedAgeMs, long ObservedFrameCount);
    }

    /// <summary>
    /// Body of POST /api/input/set-buttons. <c>Buttons</c> accepts names ("Right", "a", "Start")
    /// or raw indices (0-7) in the same array.
    /// </summary>
    public sealed class SetInputButtonsRequest
    {
        public int? Player { get; set; }
        public List<JsonElement>? Buttons { get; set; }
        public int? HoldMs { get; set; }
    }

    /// <summary>
    /// Endpoints for webmodule input events (X/Y buttons) and for API-driven NES input injection.
    /// </summary>
    public partial class WebApiServer
    {
        // Store the last button event to allow polling (simple approach)
        private string? _lastButtonEvent = null;
        private DateTime _lastButtonEventTime = DateTime.MinValue;
        private readonly object _buttonEventLock = new object();

        /// <summary>
        /// Notify the server that a webmodule button was pressed
        /// This is called from MainForm when X/Y buttons are detected
        /// </summary>
        public void NotifyButtonPressed(string buttonName)
        {
            lock (_buttonEventLock)
            {
                _lastButtonEvent = $"pressed:{buttonName}";
                _lastButtonEventTime = DateTime.UtcNow;
            }
            Console.WriteLine($"[WebApi] Webmodule button pressed: {buttonName}");
        }

        /// <summary>
        /// Notify the server that a webmodule button was released
        /// </summary>
        public void NotifyButtonReleased(string buttonName)
        {
            lock (_buttonEventLock)
            {
                _lastButtonEvent = $"released:{buttonName}";
                _lastButtonEventTime = DateTime.UtcNow;
            }
            Console.WriteLine($"[WebApi] Webmodule button released: {buttonName}");
        }

        private void RegisterInputEndpoints(WebApplication app)
        {
            // GET /api/input/button-event - Poll for button events
            // Returns the most recent button event if it occurred within the last 100ms
            app.MapGet("/api/input/button-event", () =>
            {
                try
                {
                    lock (_buttonEventLock)
                    {
                        // Only return events that are recent (within 100ms)
                        var age = DateTime.UtcNow - _lastButtonEventTime;
                        if (age.TotalMilliseconds < 100 && _lastButtonEvent != null)
                        {
                            var parts = _lastButtonEvent.Split(':');
                            if (parts.Length == 2)
                            {
                                var eventType = parts[0]; // "pressed" or "released"
                                var button = parts[1]; // "X" or "Y"

                                // Clear the event after reading
                                _lastButtonEvent = null;

                                return Results.Ok(new
                                {
                                    success = true,
                                    hasEvent = true,
                                    eventType,
                                    button
                                });
                            }
                        }

                        return Results.Ok(new
                        {
                            success = true,
                            hasEvent = false
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] Input event error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/input/set-buttons - Hold a set of NES buttons on behalf of a test.
            //   { "player": 1, "buttons": ["Right","A"] }  -> hold Right+A
            //   { "player": 1, "buttons": [] }             -> release everything
            //   { "buttons": ["B"], "holdMs": 150 }        -> hold B, auto-release after 150ms
            // The set is a LATCH, not a one-shot press: the emulation loop ORs it into the real
            // keyboard/gamepad state every frame until it is changed, cleared or expires.
            app.MapPost("/api/input/set-buttons", (SetInputButtonsRequest? request) =>
            {
                try
                {
                    var player = request?.Player ?? 1;
                    if (!ApiInputInjector.IsValidPlayer(player))
                    {
                        return Results.BadRequest(new
                        {
                            success = false,
                            error = $"Invalid player {player}; expected 1 or 2"
                        });
                    }

                    var indices = new List<int>();
                    var unknown = new List<string>();

                    foreach (var element in request?.Buttons ?? new List<JsonElement>())
                    {
                        string? token = element.ValueKind switch
                        {
                            JsonValueKind.String => element.GetString(),
                            JsonValueKind.Number => element.TryGetInt32(out var n) ? n.ToString() : null,
                            _ => null
                        };

                        if (ApiInputInjector.TryParseButton(token, out var index))
                        {
                            if (!indices.Contains(index)) indices.Add(index);
                        }
                        else
                        {
                            unknown.Add(token ?? element.ToString());
                        }
                    }

                    if (unknown.Count > 0)
                    {
                        // Reject the whole request rather than silently pressing a subset - a test
                        // that quietly loses a button is worse than one that fails loudly.
                        return Results.BadRequest(new
                        {
                            success = false,
                            error = $"Unknown button(s): {string.Join(", ", unknown)}",
                            validButtons = ApiInputInjector.GetButtonNames()
                        });
                    }

                    ApiInputInjector.Set(player, indices, request?.HoldMs);

                    var held = new bool[ApiInputInjector.ButtonCount];
                    foreach (var index in indices) held[index] = true;

                    return Results.Ok(new
                    {
                        success = true,
                        player,
                        buttons = ApiInputInjector.NamesOf(held),
                        holdMs = request?.HoldMs,
                        // Injection is additive: real keyboard/gamepad input still reaches the NES.
                        merge = "or"
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] set-buttons error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // POST /api/input/clear - Release every injected button for both players.
            app.MapPost("/api/input/clear", () =>
            {
                try
                {
                    ApiInputInjector.ClearAll();
                    return Results.Ok(new { success = true, cleared = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] input clear error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });

            // GET /api/input/state - What the emulator currently believes is held.
            //   injected  = what this API is latching
            //   held      = what was actually handed to nes.SetInputs on the most recent frame
            //               (real devices merged with the injection)
            // heldAgeMs tells a caller whether "held" is live or a stale snapshot from before the
            // emulation loop stopped (paused / no ROM).
            app.MapGet("/api/input/state", () =>
            {
                try
                {
                    var snapshot = ApiInputInjector.Snapshot();
                    var nes = _getNes();

                    return Results.Ok(new
                    {
                        success = true,
                        romLoaded = nes != null,
                        heldAgeMs = snapshot.ObservedAgeMs,
                        // The emulation loop publishes every frame, so anything fresher than a few
                        // frames means the loop is genuinely running.
                        emulationActive = snapshot.ObservedAgeMs.HasValue && snapshot.ObservedAgeMs.Value < 250,
                        frameCount = snapshot.ObservedFrameCount,
                        buttonOrder = ApiInputInjector.GetButtonNames(),
                        players = snapshot.Players.Select(p => new
                        {
                            player = p.Player,
                            injected = ApiInputInjector.NamesOf(p.Injected),
                            held = ApiInputInjector.NamesOf(p.Observed),
                            heldMask = p.Observed,
                            holdRemainingMs = p.HoldRemainingMs
                        }).ToArray()
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebApi] input state error: {ex.Message}");
                    return Results.BadRequest(new { success = false, error = ex.Message });
                }
            });
        }
    }
}

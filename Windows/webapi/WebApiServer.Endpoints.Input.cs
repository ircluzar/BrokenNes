using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace BrokenNes.Windows.WebApi
{
    /// <summary>
    /// Endpoints for webmodule input events (X/Y buttons)
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
        }
    }
}

using System;
using NesEmulator;

namespace BrokenNes.Windows
{
    /// <summary>
    /// Helper extensions for NES class to make it easier to work with in WinForms
    /// </summary>
    public static class NesExtensions
    {
        // NES button indices:
        // 0 = A, 1 = B, 2 = Select, 3 = Start, 4 = Up, 5 = Down, 6 = Left, 7 = Right
        
        private static bool[] player1Buttons = new bool[8];
        private static bool[] player2Buttons = new bool[8];
        
        /// <summary>
        /// Set a specific button state for a player
        /// </summary>
        /// <param name="nes">NES instance</param>
        /// <param name="player">Player number (0 or 1)</param>
        /// <param name="buttonIndex">Button index (0-7)</param>
        /// <param name="pressed">True if pressed, false if released</param>
        public static void SetButton(this NES nes, int player, int buttonIndex, bool pressed)
        {
            if (buttonIndex < 0 || buttonIndex >= 8) return;
            
            if (player == 0)
            {
                player1Buttons[buttonIndex] = pressed;
                nes.SetInput(player1Buttons);
            }
            else if (player == 1)
            {
                player2Buttons[buttonIndex] = pressed;
                nes.SetInputs(player1Buttons, player2Buttons);
            }
        }
        
        /// <summary>
        /// Clear all button states for all players
        /// </summary>
        public static void ClearButtons(this NES nes)
        {
            Array.Clear(player1Buttons, 0, player1Buttons.Length);
            Array.Clear(player2Buttons, 0, player2Buttons.Length);
            nes.SetInputs(player1Buttons, player2Buttons);
        }
    }
}

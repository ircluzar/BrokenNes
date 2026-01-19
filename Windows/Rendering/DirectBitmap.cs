using System;
using System.Runtime.InteropServices;

namespace BrokenNes.Windows.Rendering
{
    /// <summary>
    /// A fast bitmap class that provides direct access to pixel data for efficient DirectX texture updates.
    /// This class manages an unmanaged buffer that can be quickly copied to GPU memory.
    /// </summary>
    public class DirectBitmap : IDisposable
    {
        public int[] Bits { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride => Width * 4; // BGRA format = 4 bytes per pixel
        
        private GCHandle bitsHandle;
        private bool disposed = false;

        /// <summary>
        /// Creates a new DirectBitmap with the specified dimensions
        /// </summary>
        /// <param name="width">Width in pixels</param>
        /// <param name="height">Height in pixels</param>
        public DirectBitmap(int width, int height)
        {
            Width = width;
            Height = height;
            Bits = new int[width * height];
            bitsHandle = GCHandle.Alloc(Bits, GCHandleType.Pinned);
        }

        /// <summary>
        /// Gets the pointer to the pixel data
        /// </summary>
        public IntPtr BitsPtr => bitsHandle.AddrOfPinnedObject();

        /// <summary>
        /// Sets a pixel at the specified coordinates
        /// </summary>
        /// <param name="x">X coordinate</param>
        /// <param name="y">Y coordinate</param>
        /// <param name="color">Color as ARGB integer</param>
        public void SetPixel(int x, int y, int color)
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height)
            {
                int index = y * Width + x;
                Bits[index] = color;
            }
        }

        /// <summary>
        /// Gets a pixel at the specified coordinates
        /// </summary>
        /// <param name="x">X coordinate</param>
        /// <param name="y">Y coordinate</param>
        /// <returns>Color as ARGB integer</returns>
        public int GetPixel(int x, int y)
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height)
            {
                int index = y * Width + x;
                return Bits[index];
            }
            return 0;
        }

        /// <summary>
        /// Clears the bitmap to a specific color
        /// </summary>
        /// <param name="color">Color as ARGB integer</param>
        public void Clear(int color = 0)
        {
            for (int i = 0; i < Bits.Length; i++)
            {
                Bits[i] = color;
            }
        }

        /// <summary>
        /// Copies pixel data from a byte array (used for NES emulator output)
        /// Converts from RGBA format (NES output) to BGRA format (Windows/DirectX)
        /// </summary>
        /// <param name="source">Source byte array in RGBA format</param>
        public void CopyFromBytes(byte[] source)
        {
            if (source.Length != Bits.Length * 4)
            {
                throw new ArgumentException($"Source array size mismatch. Expected {Bits.Length * 4} bytes, got {source.Length}");
            }

            // Convert RGBA to BGRA format by swapping R and B channels
            for (int i = 0; i < Bits.Length; i++)
            {
                int srcIndex = i * 4;
                byte r = source[srcIndex + 0];
                byte g = source[srcIndex + 1];
                byte b = source[srcIndex + 2];
                byte a = source[srcIndex + 3];
                
                // Pack as BGRA (little-endian ARGB32 on Windows)
                Bits[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }

        /// <summary>
        /// Copies pixel data from another DirectBitmap
        /// </summary>
        /// <param name="source">Source DirectBitmap</param>
        public void CopyFrom(DirectBitmap source)
        {
            if (source.Width != Width || source.Height != Height)
            {
                throw new ArgumentException("Source bitmap dimensions do not match");
            }

            Array.Copy(source.Bits, Bits, Bits.Length);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                if (bitsHandle.IsAllocated)
                {
                    bitsHandle.Free();
                }
                disposed = true;
            }
        }

        ~DirectBitmap()
        {
            Dispose();
        }
    }
}

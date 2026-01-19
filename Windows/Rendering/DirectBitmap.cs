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
        public unsafe void CopyFromBytes(byte[] source)
        {
            if (source.Length != Bits.Length * 4)
            {
                throw new ArgumentException($"Source array size mismatch. Expected {Bits.Length * 4} bytes, got {source.Length}");
            }

            // Use unsafe pointer arithmetic for fast RGBA→BGRA conversion
            // This is ~3-5x faster than the byte-by-byte loop
            fixed (byte* srcPtr = source)
            fixed (int* dstPtr = Bits)
            {
                byte* src = srcPtr;
                int* dst = dstPtr;
                int count = Bits.Length;
                
                // Process in chunks of 4 for better CPU pipelining
                int chunks = count / 4;
                int remainder = count % 4;
                
                for (int i = 0; i < chunks; i++)
                {
                    // Unroll 4 iterations for better performance
                    byte r0 = src[0], g0 = src[1], b0 = src[2], a0 = src[3];
                    byte r1 = src[4], g1 = src[5], b1 = src[6], a1 = src[7];
                    byte r2 = src[8], g2 = src[9], b2 = src[10], a2 = src[11];
                    byte r3 = src[12], g3 = src[13], b3 = src[14], a3 = src[15];
                    
                    dst[0] = (a0 << 24) | (r0 << 16) | (g0 << 8) | b0;
                    dst[1] = (a1 << 24) | (r1 << 16) | (g1 << 8) | b1;
                    dst[2] = (a2 << 24) | (r2 << 16) | (g2 << 8) | b2;
                    dst[3] = (a3 << 24) | (r3 << 16) | (g3 << 8) | b3;
                    
                    src += 16;
                    dst += 4;
                }
                
                // Handle remainder
                for (int i = 0; i < remainder; i++)
                {
                    byte r = src[0], g = src[1], b = src[2], a = src[3];
                    *dst = (a << 24) | (r << 16) | (g << 8) | b;
                    src += 4;
                    dst++;
                }
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

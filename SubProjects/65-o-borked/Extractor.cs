using System;
using System.IO;

public static class Extractor
{
    public static void ExtractAllNesFiles(string romsDir)
    {
        if (!Directory.Exists(romsDir))
        {
            Console.WriteLine($"Directory not found: {romsDir}");
            return;
        }

        foreach (var nesFile in Directory.GetFiles(romsDir, "*.nes"))
        {
            try
            {
                string fileName = Path.GetFileNameWithoutExtension(nesFile);
                byte[] data = File.ReadAllBytes(nesFile);
                if (data.Length < 16)
                {
                    Console.WriteLine($"File too small to be a valid NES file: {nesFile}");
                    continue;
                }

                // Extract iNES header
                byte[] header = new byte[16];
                Array.Copy(data, 0, header, 0, 16);
                File.WriteAllBytes(Path.Combine(romsDir, fileName + ".ines"), header);

                // Validate iNES magic
                if (!(header[0] == 'N' && header[1] == 'E' && header[2] == 'S' && header[3] == 0x1A))
                {
                    Console.WriteLine($"Invalid iNES header in file: {nesFile}");
                    continue;
                }

                int prgSize = header[4] * 16 * 1024; // PRG ROM size in bytes
                int chrSize = header[5] * 8 * 1024;  // CHR ROM size in bytes
                bool hasTrainer = (header[6] & 0x04) != 0;
                int offset = 16;

                // Extract trainer if present
                if (hasTrainer)
                {
                    if (data.Length < offset + 512)
                    {
                        Console.WriteLine($"File too small for trainer: {nesFile}");
                        continue;
                    }
                    byte[] trainer = new byte[512];
                    Array.Copy(data, offset, trainer, 0, 512);
                    File.WriteAllBytes(Path.Combine(romsDir, fileName + ".trainer"), trainer);
                    offset += 512;
                }

                // Extract PRG ROM
                if (data.Length < offset + prgSize)
                {
                    Console.WriteLine($"File too small for PRG ROM: {nesFile}");
                    continue;
                }
                byte[] prg = new byte[prgSize];
                Array.Copy(data, offset, prg, 0, prgSize);
                File.WriteAllBytes(Path.Combine(romsDir, fileName + ".prg"), prg);
                offset += prgSize;

                // Extract CHR ROM (if present)
                if (chrSize > 0)
                {
                    if (data.Length < offset + chrSize)
                    {
                        Console.WriteLine($"File too small for CHR ROM: {nesFile}");
                        continue;
                    }
                    byte[] chr = new byte[chrSize];
                    Array.Copy(data, offset, chr, 0, chrSize);
                    File.WriteAllBytes(Path.Combine(romsDir, fileName + ".chr"), chr);
                    offset += chrSize;
                }

                // If any extra data remains, dump it as .extra
                if (offset < data.Length)
                {
                    byte[] extra = new byte[data.Length - offset];
                    Array.Copy(data, offset, extra, 0, extra.Length);
                    File.WriteAllBytes(Path.Combine(romsDir, fileName + ".extra"), extra);
                }

                Console.WriteLine($"Extracted: {fileName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing {nesFile}: {ex.Message}");
                // Continue to next file
            }
        }
    }
}

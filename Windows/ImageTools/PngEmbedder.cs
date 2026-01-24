using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;

namespace PngPayloadEmbedding
{
    /// <summary>
    /// Main class for embedding and extracting payloads within PNG images using compression and encryption.
    /// </summary>
    public static class PngPayload
    {
        /// <summary>
        /// Embeds data into a PNG image with optional encryption.
        /// </summary>
        /// <param name="inputImage">The source image to embed data into</param>
        /// <param name="inputData">The data to embed</param>
        /// <param name="encryptionKey">Optional encryption key (uses gold key if null)</param>
        /// <param name="decryptionKey">Optional decryption key to embed in the image</param>
        /// <returns>A new Bitmap with embedded data, or null if data doesn't fit</returns>
        public static Bitmap EmbedData(Bitmap inputImage, byte[] inputData, byte[] encryptionKey = null, byte[] decryptionKey = null)
        {
            return ColorEncoder.EncodeImageData(inputImage, inputData, encryptionKey, decryptionKey);
        }

        /// <summary>
        /// Extracts data from a PNG image.
        /// </summary>
        /// <param name="inputImage">The image containing embedded data</param>
        /// <param name="decryptionKey">Optional decryption key (uses gold key or embedded key if null)</param>
        /// <returns>The extracted data</returns>
        public static byte[] ExtractData(Bitmap inputImage, byte[] decryptionKey = null)
        {
            return ColorEncoder.DecodeImageData(inputImage, decryptionKey);
        }

        /// <summary>
        /// Generates a new RSA key pair for encryption/decryption.
        /// </summary>
        /// <returns>Tuple containing (encryptionKey, decryptionKey)</returns>
        public static (byte[] encryptionKey, byte[] decryptionKey) GenerateKeyPair()
        {
            return CryptRSA.GenerateKeyPair();
        }

        /// <summary>
        /// Enable or disable unofficial (non-gold) key usage.
        /// </summary>
        public static bool AllowUnofficialKeys
        {
            get { return Crypt.AllowUnofficial; }
            set { Crypt.AllowUnofficial = value; }
        }

        /// <summary>
        /// Runs comprehensive tests on all components.
        /// </summary>
        public static void RunTests()
        {
            Compressor.RunTest();
            Crypt.RunTest();
            CryptRSA.RunTest();
            ColorEncoder.RunTest();
        }
    }

    #region ColorEncoder - Steganography Implementation

    internal static class ColorEncoder
    {
        public static void RunTest()
        {
            bool success = true;
            Random rnd = new Random();

            // Test encoding with linear pixels
            {
                List<Color> pixels = new List<Color>();
                for (int i = 0; i < 800; i++)
                {
                    byte color1 = (byte)rnd.Next(256);
                    byte color2 = (byte)rnd.Next(256);
                    byte color3 = (byte)rnd.Next(256);
                    Color pixel = Color.FromArgb(color1, color2, color3);
                    pixels.Add(pixel);
                }

                Color[] inputPixels = pixels.ToArray();
                List<byte> data = new List<byte>();
                for (int i = 0; i < 300; i++)
                {
                    byte byte1 = (byte)rnd.Next(256);
                    data.Add(byte1);
                }

                byte[] inputData = data.ToArray();
                Color[] outputPixels = EncodePixelColors(inputPixels, inputData);
                byte[] extractedData = DecodePixelColors(outputPixels);

                if (inputData.Length != extractedData.Length)
                    success = false;

                for (int i = 0; i < 256; i++)
                {
                    if (inputData[i] != extractedData[i])
                        success = false;
                }
            }

            if (!success)
                throw new Exception("ColorEncoder TEST FAILED");
        }

        public static Bitmap EncodeImageData(Bitmap inputImage, byte[] inputData, byte[] encryptionKey = null, byte[] decryptionKey = null)
        {
            bool embedkey = false;
            if (encryptionKey == null)
            {
                encryptionKey = CryptRSA.encryptionKey;
            }

            if (decryptionKey == null)
            {
                decryptionKey = CryptRSA.decryptionKey;
            }
            else
            {
                embedkey = true;
            }

            int imgWidth = inputImage.Width;
            int imgHeight = inputImage.Height;

            int nbPixels = imgHeight * imgWidth;
            int nbColors = nbPixels * 3;

            var compressedFile = Compressor.Compress(inputData);
            var encryptedFile = Crypt.Encrypt(compressedFile, encryptionKey);

            // Key Header
            int keyHeader = (embedkey ? decryptionKey.Length : 0);
            byte[] keyHeaderBytes = BitConverter.GetBytes(keyHeader);
            BitArray keyHeaderBits = new BitArray(keyHeaderBytes);
            int keyHeaderBitsSize = keyHeaderBits.Length;

            // Key contents
            BitArray keyBits = (embedkey ? new BitArray(decryptionKey) : new BitArray(new byte[0]));
            int keyBitsSize = keyBits.Length;

            // Data Header
            int header = encryptedFile.Length;
            byte[] headerBytes = BitConverter.GetBytes(header);
            BitArray headerBits = new BitArray(headerBytes);
            int headerBitsSize = headerBits.Length;

            // Data contents
            BitArray inputBits = new BitArray(encryptedFile);
            int inputBitsSize = inputBits.Length;

            int payloadBitsSize = keyHeaderBitsSize + keyBitsSize + headerBitsSize + inputBitsSize;

            if (payloadBitsSize > nbColors)
                return null;

            // Fill remainder with noise
            int noiseBitsSize = nbColors - payloadBitsSize;
            BitArray noiseBits = new BitArray(noiseBitsSize);

            Random rnd = new Random();
            for (int i = 0; i < noiseBitsSize; i++)
            {
                var test = rnd.Next(100);
                noiseBits[i] = (test > 69);
            }

            // Fill all bits
            BitArray allBits = new BitArray(nbColors);

            for (int i = 0; i < nbColors; i++)
            {
                if (i < keyHeaderBitsSize)
                {
                    allBits[i] = keyHeaderBits[i];
                }
                else if (embedkey && i < (keyHeaderBitsSize + keyBitsSize))
                {
                    allBits[i] = keyBits[i - keyHeaderBitsSize];
                }
                else if (i < (keyHeaderBitsSize + keyBitsSize + headerBitsSize))
                {
                    allBits[i] = headerBits[i - (keyHeaderBitsSize + keyBitsSize)];
                }
                else if (i < (keyHeaderBitsSize + keyBitsSize + headerBitsSize + inputBitsSize))
                {
                    allBits[i] = inputBits[i - (keyHeaderBitsSize + keyBitsSize + headerBitsSize)];
                }
                else
                {
                    allBits[i] = noiseBits[i - (keyHeaderBitsSize + keyBitsSize + headerBitsSize + inputBitsSize)];
                }
            }

            Color[] pixels = GetImagePixels(inputImage);
            Color[] encodedPixels = EncodePixelColors(pixels, allBits);
            Bitmap outputImage = RemapPixels(inputImage, encodedPixels);

            return outputImage;
        }

        private static Bitmap RemapPixels(Bitmap inputImage, Color[] encodedPixels)
        {
            Bitmap outputImage = (Bitmap)inputImage.Clone();

            var imgHeight = inputImage.Height;
            var imgWidth = inputImage.Width;
            var totalPixels = imgHeight * imgWidth;

            int x = 0;
            int y = 0;

            for (int i = 0; i < totalPixels; i++)
            {
                outputImage.SetPixel(x, y, encodedPixels[i]);

                x++;
                if (x >= imgWidth)
                {
                    x = 0;
                    y++;
                }
            }

            return outputImage;
        }

        private static Color[] GetImagePixels(Bitmap inputImage)
        {
            List<Color> pixels = new List<Color>();
            var imgHeight = inputImage.Height;
            var imgWidth = inputImage.Width;
            var totalPixels = imgHeight * imgWidth;

            int x = 0;
            int y = 0;

            for (int i = 0; i < totalPixels; i++)
            {
                pixels.Add(inputImage.GetPixel(x, y));

                x++;
                if (x >= imgWidth)
                {
                    x = 0;
                    y++;
                }
            }

            return pixels.ToArray();
        }

        public static byte[] DecodeImageData(Bitmap inputImage, byte[] decryptionKey = null)
        {
            if (decryptionKey == null)
            {
                decryptionKey = CryptRSA.decryptionKey;
            }

            Color[] pixels = GetImagePixels(inputImage);
            byte[] pixelData = DecodePixelColors(pixels);

            byte[] keyHeaderData = new byte[4];
            Array.Copy(pixelData, 0, keyHeaderData, 0, 4);
            int keyHeaderSize = BitConverter.ToInt32(keyHeaderData, 0);

            if (keyHeaderSize > 0)
            {
                byte[] keyData = new byte[keyHeaderSize];
                Array.Copy(pixelData, 4, keyData, 0, keyHeaderSize);

                if (!Crypt.AllowUnofficial)
                    throw new Exception("UNOFFICIAL KEY DETECTED - Set AllowUnofficialKeys to true");

                decryptionKey = keyData;
            }

            int headerPos = 4 + keyHeaderSize;
            byte[] headerData = new byte[4];
            Array.Copy(pixelData, headerPos, headerData, 0, 4);

            int headerSize = BitConverter.ToInt32(headerData, 0);

            int dataPos = 4 + keyHeaderSize + 4;
            byte[] encryptedData = new byte[headerSize];
            Array.Copy(pixelData, dataPos, encryptedData, 0, headerSize);

            byte[] decryptedData = Crypt.Decrypt(encryptedData, decryptionKey);
            byte[] decompressedData = Compressor.Decompress(decryptedData);

            return decompressedData;
        }

        public static Color[] EncodePixelColors(Color[] pixels, byte[] encodedBytes)
        {
            return EncodePixelColors(pixels, new BitArray(encodedBytes));
        }

        public static Color[] EncodePixelColors(Color[] pixels, BitArray allBits)
        {
            Color[] output = (Color[])pixels.Clone();

            for (int i = 0; i < pixels.Length; i++)
            {
                int driftPos = (i * 3);

                bool bit1 = allBits[driftPos];
                driftPos++;
                bool bit2 = allBits[driftPos];
                driftPos++;
                bool bit3 = allBits[driftPos];

                Color inputColor = pixels[i];
                Color outputColor = ColorEncoder.GetEncodedPixel(inputColor, bit1, bit2, bit3);
                output[i] = outputColor;
            }

            return output;
        }

        public static byte[] DecodePixelColors(Color[] pixels)
        {
            List<byte> data = new List<byte>();
            for (int i = 0; i < pixels.Length; i++)
            {
                data.Add(pixels[i].R);
                data.Add(pixels[i].G);
                data.Add(pixels[i].B);
            }

            return DecodeBytes(data.ToArray());
        }

        public static byte[] DecodeBytes(byte[] encodedData)
        {
            int remainder = encodedData.Length % 8;
            int outputSize = (encodedData.Length - remainder) / 8;
            byte[] output = new byte[outputSize];

            for (int i = 0; i < outputSize; i++)
            {
                byte[] subByte = new byte[8];
                Array.Copy(encodedData, (i * 8), subByte, 0, 8);

                byte sub = DecodeByte(subByte);
                output[i] = sub;
            }

            return output;
        }

        public static byte DecodeByte(byte[] subByte)
        {
            BitArray ba = new BitArray(8);

            for (int i = 0; i < 8; i++)
            {
                ba[i] = (subByte[i] % 2) == 1;
            }

            byte[] bytes = new byte[1];
            ba.CopyTo(bytes, 0);
            return bytes[0];
        }

        public static Color GetEncodedPixel(Color inputColor, bool bit1, bool bit2, bool bit3)
        {
            byte R = FlatByteColor(inputColor.R, bit1);
            byte G = FlatByteColor(inputColor.G, bit2);
            byte B = FlatByteColor(inputColor.B, bit3);

            return Color.FromArgb(R, G, B);
        }

        public static byte FlatByteColor(byte color, bool bit)
        {
            if (color % 2 == 0) // even
            {
                if (bit)
                {
                    if (color == 0)
                        return 1;

                    return (byte)(color - 1);
                }
                else
                {
                    return color;
                }
            }
            else // odd
            {
                if (bit)
                {
                    return color;
                }
                else
                {
                    return (byte)(color - 1);
                }
            }
        }
    }

    #endregion

    #region Compression

    internal static class Compressor
    {
        public static byte[] Compress(byte[] data)
        {
            MemoryStream output = new MemoryStream();
            using (DeflateStream dstream = new DeflateStream(output, CompressionLevel.Optimal))
            {
                dstream.Write(data, 0, data.Length);
            }
            return output.ToArray();
        }

        public static void RunTest()
        {
            bool success = true;

            List<byte> data = new List<byte>();
            for (int i = 0; i < 256; i++)
                data.Add((byte)i);

            byte[] input = data.ToArray();

            var compressed = Compress(input);
            var output = Decompress(compressed);

            if (input.Length != output.Length)
                success = false;

            for (int i = 0; i < 256; i++)
            {
                if (input[i] != output[i])
                    success = false;
            }

            if (!success)
                throw new Exception("Compressor TEST FAILED");
        }

        public static byte[] Decompress(byte[] data)
        {
            MemoryStream input = new MemoryStream(data);
            MemoryStream output = new MemoryStream();
            using (DeflateStream dstream = new DeflateStream(input, CompressionMode.Decompress))
            {
                dstream.CopyTo(output);
            }
            return output.ToArray();
        }

        public static byte[] Serialize(object obj)
        {
            throw new NotImplementedException("Generic serialization is removed due to BinaryFormatter obsolescence.");
        }

        public static T Deserialize<T>(byte[] input)
        {
             throw new NotImplementedException("Generic deserialization is removed due to BinaryFormatter obsolescence.");
        }
    }

    #endregion

    #region Encryption

    internal static class Crypt
    {
        public static bool AllowUnofficial { get; set; } = false;

        public static void RunTest()
        {
            var keys = CryptRSA.GenerateKeyPair();
            bool success = true;

            List<byte> data = new List<byte>();
            for (int i = 0; i < 256; i++)
                data.Add((byte)i);

            byte[] dataInput = data.ToArray();
            byte[] encryptedData = Encrypt(dataInput, keys.encryptionKey);
            byte[] dataOutput = Decrypt(encryptedData, keys.decryptionKey);

            if (dataInput.Length != dataOutput.Length)
                success = false;

            for (int i = 0; i < 256; i++)
            {
                if (dataInput[i] != dataOutput[i])
                    success = false;
            }

            if (!success)
                throw new Exception("Crypt TEST FAILED");
        }

        public static byte[] Encrypt(byte[] inputbyteArray, byte[] encryptionKey)
        {
            try
            {
                var encryptedStack = CryptStack.GetNewStackFromBytes(inputbyteArray);
                if (encryptionKey != null)
                {
                    encryptedStack.Encrypt(encryptionKey);
                }
                byte[] result = encryptedStack.ToBytes();
                return result;
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message, ex.InnerException);
            }
        }

        public static byte[] Decrypt(byte[] inputbyteArray, byte[] decryptionKey)
        {
            try
            {
                var decryptedStack = CryptStack.FromBytes(inputbyteArray);
                if (decryptionKey != null)
                {
                    decryptedStack.Decrypt(decryptionKey);
                }
                byte[] result = decryptedStack.GetInnerBytesFromStack();
                return result;
            }
            catch (Exception ae)
            {
                throw new Exception(ae.Message, ae.InnerException);
            }
        }
    }

    #endregion

    #region RSA Encryption

    internal static class CryptRSA
    {
        public static byte[] goldkey = GetDefaultDecryptionKey();
        public static byte[] decryptionKey = goldkey;
        public static byte[] encryptionKey = GetDefaultEncryptionKey();

        private static byte[] GetDefaultDecryptionKey()
        {
            // Gold Decryption Key
            // .NET 10 Migration: BinaryFormatter blob removed. 
            // Original key cannot be recovered without old formatter.
            return null; 
        }

        private static byte[] GetDefaultEncryptionKey()
        {
            if (File.Exists("gold_encrypt.key"))
                return File.ReadAllBytes("gold_encrypt.key");

            return null;
        }

        public static void RunTest()
        {
            bool success = true;

            // Short data encryption test
            {
                var keys = GenerateKeyPair();

                List<byte> data = new List<byte>();
                for (int i = 0; i < 100; i++)
                    data.Add((byte)i);

                byte[] dataInput = data.ToArray();
                byte[] encryptedData = RSAEncrypt(dataInput, keys.encryptionKey, false);
                byte[] dataOutput = RSADecrypt(encryptedData, keys.decryptionKey, false);

                if (dataInput.Length != dataOutput.Length)
                    success = false;

                for (int i = 0; i < 100; i++)
                {
                    if (dataInput[i] != dataOutput[i])
                        success = false;
                }
            }

            // Large data encryption test
            {
                var keys = GenerateKeyPair();

                List<byte> data = new List<byte>();
                for (int i = 0; i < 20069; i++)
                    data.Add((byte)i);

                byte[] dataInput = data.ToArray();

                var encryptedStack = CryptStack.GetNewStackFromBytes(dataInput);
                encryptedStack.Encrypt(keys.encryptionKey);

                byte[] encryptedStackData = encryptedStack.ToBytes();

                var decryptedStack = CryptStack.FromBytes(encryptedStackData);
                decryptedStack.Decrypt(keys.decryptionKey);
                byte[] dataOutput = decryptedStack.GetInnerBytesFromStack();

                if (dataInput.Length != dataOutput.Length)
                    success = false;

                for (int i = 0; i < 20069; i++)
                {
                    if (dataInput[i] != dataOutput[i])
                        success = false;
                }
            }

            if (!success)
                throw new Exception("CryptRSA TEST FAILED");
        }

        public static (byte[] encryptionKey, byte[] decryptionKey) GenerateKeyPair()
        {
            byte[] SerializedEncryptionKey;
            byte[] SerializedDecryptionKey;

            using (RSACryptoServiceProvider RSA = new RSACryptoServiceProvider())
            {
                SerializedEncryptionKey = CompatExportRSAPublicKey(RSA);
                SerializedDecryptionKey = CompatExportRSAPrivateKey(RSA);

                return (SerializedEncryptionKey, SerializedDecryptionKey);
            }
        }

        public static byte[] CompatExportRSAPublicKey(RSACryptoServiceProvider RSA)
        {
            var sparam = new RSAParametersSerializable(RSA.ExportParameters(false));
            return sparam.ToBytes();
        }

        public static byte[] CompatExportRSAPrivateKey(RSACryptoServiceProvider RSA)
        {
            var sparam = new RSAParametersSerializable(RSA.ExportParameters(true));
            return sparam.ToBytes();
        }

        public static void CompatImportRSAPublicKey(RSACryptoServiceProvider RSA, byte[] key)
        {
            RSAParametersSerializable sparam = RSAParametersSerializable.FromBytes(key);
            RSA.ImportParameters(sparam.RSAParameters);
        }

        public static void CompatImportRSAPrivateKey(RSACryptoServiceProvider RSA, byte[] key)
        {
            RSAParametersSerializable sparam = RSAParametersSerializable.FromBytes(key);
            RSA.ImportParameters(sparam.RSAParameters);
        }

        public static byte[] RSAEncrypt(byte[] DataToEncrypt, byte[] encryptKey, bool DoOAEPPadding)
        {
            try
            {
                byte[] encryptedData;

                using (RSACryptoServiceProvider RSA = new RSACryptoServiceProvider())
                {
                    CompatImportRSAPublicKey(RSA, encryptKey);
                    encryptedData = RSA.Encrypt(DataToEncrypt, DoOAEPPadding);
                }
                return encryptedData;
            }
            catch (CryptographicException e)
            {
                Console.WriteLine(e.Message);
                return null;
            }
        }

        public static byte[] RSADecrypt(byte[] DataToDecrypt, byte[] decryptKey, bool DoOAEPPadding)
        {
            try
            {
                byte[] decryptedData;

                using (RSACryptoServiceProvider RSA = new RSACryptoServiceProvider())
                {
                    CompatImportRSAPrivateKey(RSA, decryptKey);
                    decryptedData = RSA.Decrypt(DataToDecrypt, DoOAEPPadding);
                }
                return decryptedData;
            }
            catch (CryptographicException e)
            {
                Console.WriteLine(e.ToString());
                return null;
            }
        }
    }

    #endregion

    #region CryptStack - Large Data Encryption

    internal class CryptStack
    {
        private byte[][] stack { get; set; } = null;

        public byte[] ToBytes()
        {
            using (var memoryStream = new MemoryStream())
            using (var writer = new BinaryWriter(memoryStream))
            {
                if (stack == null)
                {
                    writer.Write(0);
                }
                else
                {
                    writer.Write(stack.Length);
                    foreach (var layer in stack)
                    {
                        if (layer == null)
                        {
                            writer.Write(-1);
                        }
                        else
                        {
                            writer.Write(layer.Length);
                            writer.Write(layer);
                        }
                    }
                }
                return memoryStream.ToArray();
            }
        }

        public static CryptStack FromBytes(byte[] bytes)
        {
            var result = new CryptStack();
            using (var memoryStream = new MemoryStream(bytes))
            using (var reader = new BinaryReader(memoryStream))
            {
                int layerCount = reader.ReadInt32();
                result.stack = new byte[layerCount][];
                for (int i = 0; i < layerCount; i++)
                {
                    int layerLength = reader.ReadInt32();
                    if (layerLength == -1)
                    {
                        result.stack[i] = null;
                    }
                    else
                    {
                        result.stack[i] = reader.ReadBytes(layerLength);
                    }
                }
            }
            return result;
        }

        public static CryptStack GetNewStackFromBytes(byte[] input)
        {
            CryptStack newStack = new CryptStack();

            int bytesPerLayer = 100;
            double neededLayers = Math.Floor(Convert.ToDouble(input.Length) / Convert.ToDouble(bytesPerLayer));

            int remainderLayerSize = input.Length % bytesPerLayer;
            bool hasSmallLastLayer = remainderLayerSize != 0;
            if (hasSmallLastLayer)
                neededLayers++;

            int layerCount = Convert.ToInt32(neededLayers);
            int copiedByteCount = 0;

            newStack.stack = new byte[layerCount][];
            for (int i = 0; i < layerCount; i++)
            {
                byte[] layer;
                int copysize = 100;

                if (hasSmallLastLayer && i == (layerCount - 1))
                {
                    copysize = remainderLayerSize;
                }

                layer = new byte[copysize];
                Array.Copy(input, copiedByteCount, layer, 0, copysize);
                newStack.stack[i] = layer;

                copiedByteCount += copysize;
            }

            return newStack;
        }

        public byte[] GetInnerBytesFromStack()
        {
            try
            {
                byte[] output = null;

                int endSize = 0;
                for (int i = 0; i < stack.Length; i++)
                {
                    byte[] layer = stack[i];

                    if (layer == null)
                        return null;

                    endSize += layer.Length;
                }

                output = new byte[endSize];

                int readBytes = 0;
                for (int i = 0; i < stack.Length; i++)
                {
                    byte[] layer = stack[i];

                    Array.Copy(layer, 0, output, readBytes, layer.Length);

                    readBytes += layer.Length;
                }

                return output;
            }
            catch
            {
                return null;
            }
        }

        public void Encrypt(byte[] encryptionKey)
        {
            try
            {
                int stackHeight = stack.Length;

                for (int i = 0; i < stackHeight; i++)
                {
                    byte[] layer = stack[i];
                    var encrypted = CryptRSA.RSAEncrypt(layer, encryptionKey, false);
                    stack[i] = encrypted;
                }
            }
            catch
            {
                throw;
            }
        }

        public void Decrypt(byte[] decryptionKey)
        {
            try
            {
                int stackHeight = stack.Length;

                for (int i = 0; i < stackHeight; i++)
                {
                    byte[] layer = stack[i];
                    var decrypted = CryptRSA.RSADecrypt(layer, decryptionKey, false);
                    stack[i] = decrypted;
                }
            }
            catch
            {
                throw;
            }
        }
    }

    #endregion

    #region RSAParametersSerializable - RSA Key Serialization

    internal class RSAParametersSerializable
    {
        private RSAParameters _rsaParameters;

        public RSAParameters RSAParameters
        {
            get { return _rsaParameters; }
        }

        public RSAParametersSerializable(RSAParameters rsaParameters)
        {
            _rsaParameters = rsaParameters;
        }

        private RSAParametersSerializable()
        {
        }

        public byte[] D { get { return _rsaParameters.D; } set { _rsaParameters.D = value; } }
        public byte[] DP { get { return _rsaParameters.DP; } set { _rsaParameters.DP = value; } }
        public byte[] DQ { get { return _rsaParameters.DQ; } set { _rsaParameters.DQ = value; } }
        public byte[] Exponent { get { return _rsaParameters.Exponent; } set { _rsaParameters.Exponent = value; } }
        public byte[] InverseQ { get { return _rsaParameters.InverseQ; } set { _rsaParameters.InverseQ = value; } }
        public byte[] Modulus { get { return _rsaParameters.Modulus; } set { _rsaParameters.Modulus = value; } }
        public byte[] P { get { return _rsaParameters.P; } set { _rsaParameters.P = value; } }
        public byte[] Q { get { return _rsaParameters.Q; } set { _rsaParameters.Q = value; } }

        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                WriteByteArray(writer, D);
                WriteByteArray(writer, DP);
                WriteByteArray(writer, DQ);
                WriteByteArray(writer, Exponent);
                WriteByteArray(writer, InverseQ);
                WriteByteArray(writer, Modulus);
                WriteByteArray(writer, P);
                WriteByteArray(writer, Q);
                return ms.ToArray();
            }
        }

        public static RSAParametersSerializable FromBytes(byte[] key)
        {
            var result = new RSAParametersSerializable();
            result._rsaParameters = new RSAParameters();
            using (var ms = new MemoryStream(key))
            using (var reader = new BinaryReader(ms))
            {
                result.D = ReadByteArray(reader);
                result.DP = ReadByteArray(reader);
                result.DQ = ReadByteArray(reader);
                result.Exponent = ReadByteArray(reader);
                result.InverseQ = ReadByteArray(reader);
                result.Modulus = ReadByteArray(reader);
                result.P = ReadByteArray(reader);
                result.Q = ReadByteArray(reader);
            }
            return result;
        }

        private void WriteByteArray(BinaryWriter writer, byte[] data)
        {
            if (data == null)
            {
                writer.Write(-1);
            }
            else
            {
                writer.Write(data.Length);
                writer.Write(data);
            }
        }

        private static byte[] ReadByteArray(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length == -1) return null;
            return reader.ReadBytes(length);
        }
    }

    #endregion
}

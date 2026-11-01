using System;

namespace NesEmulator.Gb;

// =====================================================================================================================
//  gnuboy's Game Boy sound engine, ported to C#.
//
//  Source: gnuboy 1.0.3 as revived by rofl0r ("latest version of original laguna source", tree reports 1.0.4),
//  https://github.com/rofl0r/gnuboy at commit 4c2f6c441eb3b364e3c8e211ec39241718d5e189: sound.c, sound.h, pcm.h,
//  noise.h (the noise7 / noise15 tables), and the clocking from cpu.c (sound_advance: cpu.snd counts 2.097152 MHz
//  units, i.e. T-cycles / 2) and the sys/*/ audio defaults (44,100 Hz requested, stereo, unsigned 8-bit).
//  Copyright (C) 2000-2001 Laguna and Gilgamesh, and the gnuboy contributors (see docs/CREDITS of the source).
//  License: GPL-2.0 (COPYING of the source). This is a documented exception to the repository's license policy - see
//  THIRD_PARTY_NOTICES.md section 2 and docs/projects/foreign-apu-project.md.
//
//  A quick port (the user's call: quick and dirty first, polish later). The bugs and shortcuts of the original are kept
//  on purpose; they are the sound:
//   - one sample per RATE = 2^21 / 44100 = 47 units (integer division), so the real output rate is 44,620 Hz, not 44,100
//   - phase accumulators, no band limiting; every channel's length, envelope and sweep is timed per output sample
//     (counters in 2^21 Hz units), not by the 512 Hz frame sequencer
//   - pulse and noise are unipolar (0..vol), the wave channel is bipolar (nibble - 8); 8-bit output, hard clipped
//   - NR50 is applied swapped (left takes bits 0-2, right bits 4-6) and with no +1 (a master volume of 0 is silence)
//   - triggering the wave channel scrambles wave RAM as the CPU reads it (byte i = 0x13 ^ byte i+1; the last one
//     XORs the LCDC register, here a constant 0x91); the channel keeps playing its private copy, and writes to wave
//     RAM while the channel is on are dropped
//   - NR31 length on write is (256 - x) << 13 but on sound_dirty (state reload) << 20
//   - register reads return the raw written byte (no read-back masks); NR52's status bits refresh only after a mix
//   - very high pulse/wave periods (RATE > d << 4 / << 3) are muted; noise frequency clamps at 1 << 18
//   - sweep only steps the frequency on its period (no overflow check at trigger, no "negate used" quirk)
// =====================================================================================================================

/// <summary>gnuboy's sound: give it the GB sound register writes and the elapsed time, take 44.62 kHz stereo bytes.</summary>
public sealed class GnubSound
{
    /// <summary>The rate gnuboy is asked for (pcm.hz); the one it actually makes is <see cref="ActualRate"/>.</summary>
    public const int RequestedHz = 44100;
    /// <summary>snd.rate: 2.097152 MHz units per output sample.</summary>
    public const int RATE = (1 << 21) / RequestedHz;
    public const int ActualRate = (1 << 21) / RATE;     // 44,620 (44,620.25)
    private const byte Lcdc = 0x91;                     // ram.hi[0x40] as s3_init reads it

    private static readonly byte[] DmgWave = { 0xac, 0xdd, 0xda, 0x48, 0x36, 0x02, 0xcf, 0x16, 0x2c, 0x04, 0xe5, 0x2c, 0xac, 0xdd, 0xda, 0x48 };
    private static readonly byte[] CgbWave = { 0x00, 0xff, 0x00, 0xff, 0x00, 0xff, 0x00, 0xff, 0x00, 0xff, 0x00, 0xff, 0x00, 0xff, 0x00, 0xff };
    private static readonly sbyte[,] SqWave =
    {
        { 0, 0, -1, 0, 0, 0, 0, 0 },
        { 0, -1, -1, 0, 0, 0, 0, 0 },
        { -1, -1, -1, -1, 0, 0, 0, 0 },
        { -1, 0, 0, -1, -1, -1, -1, -1 },
    };
    private static readonly int[] FreqTab = { (1 << 14) * 2, 1 << 14, (1 << 14) / 2, (1 << 14) / 3, (1 << 14) / 4, (1 << 14) / 5, (1 << 14) / 6, (1 << 14) / 7 };

    // noise.h
    private static readonly byte[] Noise7 = Convert.FromHexString("fbe7ae1ba62b05e3b64a4272d119aa03");
    private static readonly byte[] Noise15 = Convert.FromHexString(
        "fffbffe7ffaffe1ffbbfe67faafe01fbfbe7e7afae1e1bbba6662aab0005ffe3ffb7fe4ffa5fe23fb37e54fa05e3e3b7b64e4a5a42227332d5510019ffabfe07" +
        "fbefe79faebe187baee619abaa0603ebf787ceef599c2ab7004dfe53fa17e38fb6de493a496248b24c5256120b93c697688c8cd4d50501e1fbbbe667aaae001b" +
        "ffa7fe2ffb1fe5bfa27e32fb51e41ba7a62e2b1b05a5e223b336554a0043fe77facfe15fb83e6f7a9ce0b5bc427672cad1411879aeea1983aaf601cbfb47e46f" +
        "a69e28bb0c65d6a30835cf435c7436c7496c48964c8a54c20573e0d7bd0e71dadb212539216938896cc8954c8054fe05fbe3e7b7ae4e1a5ba226332b550401e7" +
        "fbafe61fabbe067beae781aefa19e3abb6064bea47826ef299d0ab1c05b7e24fb25e523a136394b68448e64daa520213f397d68f08ddcd3351541807afee1f9b" +
        "bea6782aef019dfab3e057be0e7bdae721ad3a116398b6ac48164f8a5ec2397368d48d04d1e51ba1a63a2b6304b5e443a6762acb0145f863eeb7984eae581a2f" +
        "a31e35bb426472a6d0291f09bdca7342d47106d9e92b8906c9e94b8846ce695a8820cf3d5d7030df5d3c317758cc2d57100d9fd2bf107d9ef2b9d06b1e85b8e2" +
        "6db29250921c93b496448a64c2a57020df3d3d7170d8dd2d311159982aaf001dffb3fe57fa0fe3dfb73e4d7a50e21db3b256520a13c397768cc8d54d0051fe1b" +
        "fba7e62fab1e05bbe267b2ae501a1fa3be367b4ae441a67a2ae301b5fa43e277b2ce515a1823af361d4bb0465e6a3a8360f4bdc47366d4a90409e7cbaf461c6b" +
        "b68648ea4d8252f211d39b16a58822cf315d58302f5f1c3db7724cd255120193fa97e08fbcde753ac16178b8ec6d96928890cc9d54b0045fe63fab7e04fbe5e7" +
        "a3ae361b4ba446266b2a8500e1fdbbf267d2af101d9fb2be507a1ee3b9b66a4a8240f27dd2f311d59b02a5f023df373d4d7050de1d3bb16658aa2c0317f58fc2" +
        "df713cd97528c10d79d0eb1d85b2e251b21a53a216338b56c40967c8af4c1c57b60e4bda47226d329150981cafb41e47ba6e629ab0a05c3e377b4ce455a6022b" +
        "f307d5ef039df6b3c8574e0c5bd6270b2dc5136194ba8460e6bda8720ed3d917298d0ad1c11b79a4ea258322f531c15b7824ef259d22b130595e283b0f65dca3" +
        "343547406c7e96f889eccb95468068fe8df8d3ed17918e9ad8a12c3917698c8ad4c10579e0ebbd8672ead1811af9a1ea3b8366f4a9c40b67c4af641ca7b42e47" +
        "1a6da29230935c94348744ec6596a28830cf5d5c30375f4c3c57760ccbd547006dfe93f897ec8f94de8538e16db8926c929490849ce4b5a44226732ad50101f9" +
        "fbebe787aeee199baaa6002bff07fdeff39fd6bf087dcef359d42b0705ede393b696488a4cc2557200d3fd17f18fdadf213d397168d88d2cd1151981aafa01e3" +
        "fbb7e64faa5e023bf367d4af041de7b3ae561a0ba3c6376b4c8454e605abe207b3ee579a0ea3d8372f4d1c51b61a4ba246326b528410e79daeb21853ae161b8b" +
        "a6c6296b0885cce355b40247f26fd29f10bd9c72b6d0491e49ba4a6242b27052de113b9966a8a80c0fd7df0f3ddd7330d55d0031ff5bfc27f72fcd1f51bc1a77" +
        "a2ce315b58242f271d2db11259922a930095fc83f4f7c5cf635cb43447466c6a968088fccdf553c0177f8cfed5f903e9f78bcec7596c28970c8dd4d30515e183" +
        "baf661cabb40647ea6f829ef0b9dc6b368548e04dbe527a12e391b69a48a24c3257520c13d7970e8dd8d32d1511819afaa1e03bbf667caaf401c7fb6fe49fa4b" +
        "e247b26e529a10a39c36b7484c4e565a0a23c337754cc0557e00fbfde7f3afd61f0bbdc6736ad48104f9e5eba38636eb49844ae641aa7a02e3f1b7da4f225d32" +
        "315358142f871eedb9926a928090fc9df4b3c457660cabd40707edef939e96b8886cce9558802cff15fd83f2f7d1cf1b5da43227532c1517818efad9e12bb906" +
        "69ea8b80c6fd69f08bdcc7356d40907c9ef4b9c46b6684a8e40da7d22f131d95b28250f21dd3b316558a02c3f177d8cf2d5d10319f5abc20773ecd7950e81d8f" +
        "b2de513a1963a8b60c4bd6470a6dc2937094dc8534e145b8626eb29850ae1c1bb7a64e2a5b0225f323d537014df853ee179b8ea6d8292f091dc9b34a54420673" +
        "ead7810ef9d9eb2b8506e1e9bb8a66c2a97008dfcd3f517c18f7adce135b94268728ed0d91d29b10a59c22b7304d5e503a1f63bcb6744ac6416a7882ecf195da" +
        "8320f53dc17378d4ed0591e29bb0a65c2a37034df453c6176b8c86d4e90589e2cbb146586a2e8318f5adc2137394d68508e1cdbb526412a7902e9f18bdac7216" +
        "d38916c9894ac8414e785aee219b3aa56020bf3c7d76f0c9dd4b30455e603abf607cbef479c6eb69848ae4c1a57a20e33db57240d27d12f191da9b20a53c2177" +
        "38cd6d50901c9fb4be447a66e2a9b00a5fc23f737cd4f505c1e37bb4e645aa6202b3f057de0f3bdd6730ad5c10379f4ebc58762ecb1945a8620eb3d8572e0d1b" +
        "d1a71a2da312359342947086dce9358942c8714ed8592e291b09a5ca2343347546c0697e88f8cded5390169f88becc7956e8098fcadf413c7976e8c98d4ad041" +
        "1e79baea6182baf061debb38656ea0983caf741cc7b56e409a7ca2f431c75b6c2497248d24d1251921a93a0963c8b74c4c56560a0bc3c7776ccc95548004ffe5" +
        "ffa3fe37fb4fe45fa63e2b7b04e5e5a3a236334b54440667eaaf801effb9fe6bfa87e0efbd9e72bad0611eb9b86a6e8298f0addc1337954e8058fe2dfb13e597" +
        "a28e30db5d243127592c2917098dcad341147986eae9818afac1e17bb8e66daa920093fc97f48fc4df653ca17438c76d6c90949c84b4e445a6622ab30055fe03" +
        "fbf7e7cfaf5e1c3bb7664caa540207f3efd79f0ebdd8732ed51901a9fa0be3c7b76e4c9a54a20433e757ac0e17db8f26dd29310959c82b4f045de633ab56040b" +
        "e7c7af6e1c9bb4a6442a6702adf013df973e8d78d0ed1d91b29a50a21c33b7564c0a57c20f73dcd7350d41d07b1ee5b9a26a328350f41dc7b36e549a04a3e437" +
        "a74e2c5b16258b22c5316158b82c6f169d88b2cc5156180bafc61f6bbc8674eac58162f8b1ec5b96268b28c50d61d0bb1c65b6a248324f525c1237934e945886" +
        "2ceb158582e2f1b1da5b222533215538016ff89fecbf947e86f8e9ed8b92c69168988cacd4150781eefb99e6aba8060febdf873eed7990ea9d80b2fc51f61bcb" +
        "a7462c6b168588e2cdb15258122f931e95b8826cf295d0831cf5b5c2437274d2c5116198baac6016bf887ecef959e82b8f06dde9338956c8094fc85f4e3c5b76" +
        "24cb254520613eb97868ee8d98d2ad10119f9abea0783eef799ceab58042fe71fadbe127b92e691a89a0ca3d437074dec5396168b88c6cd6950881ccfb55e403" +
        "a7f62fcb1f45bc6276b2c8514e185bae261b2ba50621eb3b8566e0a9bc0a77c2cf715cd8352f411c79b6ea49824af241d27b12e591a29a30a35c3437474c6c56" +
        "96088bccc7556c0097fc8ff4dfc53f617cb8f46dc69368948c84d4e505a1e23bb36654aa0403e7f7afce1f5bbc26772acd0151f81befa79e2ebb1865aea21833" +
        "af561c0bb7c64f6a5c8234f345d46306b5e8438e76dac9214938496e489a4ca254320753ec17978e8ed8d92d29110999caab40047fe6ffa9fe0bfbc7e76fac9e" +
        "14bb8466e6a9a80a0fc3df773ccd7550c01d7fb0fe5dfa33e357b40e47da6f229d30b15c58362f4b1c45b6624ab240527e12fb91e69ba8a60c2bd7070dedd393" +
        "16958882ccf155d8032ff51fc1bf7a7ce2f5b1c25b7224d3251521813af961e8bb8c66d6a90809cfcb5f443c6776acc8154f805efe39fb6be487a4ee259b22a5" +
        "30215f383d6f709cdcb5344146786aee8198faade013bf967e8af8c1ed7b90e69da8b20c53d6170b8dc6d369148984cae541a07a3ee379b4ea458262f2b1d05b" +
        "1e25bb226532a150381f6fbc9e74bac46166b8a86c0e97d88f2cdd1531815af821ef3b9d66b0a85c0e37db4f245d26312b590429e70badc6136b948684e8e58d" +
        "a2d2311359942a8700edfd93f297d08f1cddb53241527812ef919e9ab8a06c3e97788cecd5950281f0fbdde733ad56100b9fc6bf687c8ef4d9c52b6104b9e46b" +
        "a68628eb0d85d2e311b59a42a27032df513c1977a8ce0d5bd0271f2dbd127192da9120993ca97408c7cd6f509c1cb7b44e465a6a228330f55dc0337f54fc05f7" +
        "e3cfb75e4c3a57620cb3d457060debd38716ed8992ca9140987caef419c7ab6e049be4a7a42e271b2da51221933a956080bcfc75f6c3c97748cc4d56500a1fc3" +
        "bf767ccaf541c07b7ee4f9a5ea238336f549c04b7e44fa65e2a3b0365f4a3c437674cac5416078beec7996ea8980cafd41f07bdee739ad6a10839cf6b5c8434e" +
        "745ac6216b38856ce095bc8274f2c5d16318b5ac4216738ad6c10979c8eb4d8452e611ab9a06a3e8378f4edc5936294b0845ce635ab420473e6d7a90e09dbcb2" +
        "7452c6116b9886ace8158f82def139d96b28850ce1d5bb0265f2a3d0371f4dbc527612cb91469868ae8c18d7ad0e11db9b26a528210f39dd6b30855ce035bf42" +
        "7c72f6d1c91b49a44a26432a7502c1f17bd8e72dad1211939a96a0883ccf755cc0357f40fc7df6f3c9d74b0c45d6630ab5c0437e74fac5e163b8b66c4a96408a" +
        "7cc2f571c0db7d24f125d92329350941c87b4ee459a62a2b0305f5e3c3b7764cca55420073fed7f90fe9df8b3ec57960e8bd8c72d6d10919c9ab4a0443e677aa" +
        "ce015bf827ef2f9d1eb1b85a6e229b30a55c20373f4d7c50f61dcbb346546a0683e8f78dced3591429870aedc1937a94e085bce275b2c2517218d3ad16118b9a" +
        "c6a168388f6cdc95348144f865eea39836af481c4fb65e4a3a436274b2c4516618abac0617eb8f86dee939896ac8814cf855ee039bf6a7c82f4f1c5db6324b52" +
        "44126792ae90189facbe147b86e6e9a98a0ac3c17778cced5590029ff0bfdc7f36fd49f04bde473a6d6290b09c5cb6344b46446a6682a8f00ddfd33f157d80f2" +
        "fdd1f31bd5a7022df313d597028df0d3dd17318d5ad0211f39bd6a7082dcf135d94328750ec1d97b28e50da1d23b136594a28430e75dac3217538c16d7890ec9" +
        "d94b28450e61dabb20653ea17838ef6d9c92b490449e64baa46026bf287d0ef1d9db2b250521e13bb96668aa8c00d7fd0ff1dfdb3f257d20f13dd97328d50d01" +
        "d1fb1be5a7a22e331b55a40227f32fd51f01bdfa73e2d7b10e59da2b230535e143b8766eca9940a87c0ef7d9cf2b5d0431e75bac26172b8d06d1e91b89a6ca29" +
        "430875cec3597428c70d6dd0931c95b48244f265d2a310359f42bc7076dec9394968488e4cda55220133f957e80f8fdedf393d697088dccd355140187faefe19" +
        "fbabe607abee079beea7982eaf181dafb21e53ba16638ab6c0497e48fa4de253b216538a16c38976c8c94d48504e1e5bba26632ab50041fe7bfae7e1afba1e63" +
        "bab6604abe407a7ee2f9b1ea5b8226f329d50b01c5fb63e4b7a44e265b2a250321f53bc16778acec1597828ef0d9dd2b310559e02bbf067deaf381d6fb09e5cb" +
        "a346346b468468e68da8d20d13d1971a8da0d23d137194da8520e13db97268d28d10d19d1ab1a05a3e237b34e545a0623eb37854ee059be2a7b02e5f1a3da372" +
        "34d345146186bae8618ebad8612eb91869ae8a18c3ad7610cb9d46b0685e8e38db6d2491249924a9240927c92f491c49b64a4a42427272d2d1111999aaaa0003");

    /// <summary>struct sndchan</summary>
    public struct Chan
    {
        public int on;
        public uint pos;
        public int cnt, encnt, swcnt;
        public int len, enlen, swlen;
        public int swfreq;
        public int freq;
        public int envol, endir;
    }

    public readonly Chan[] Ch = new Chan[4];
    /// <summary>snd.wave: the channel's private copy of wave RAM.</summary>
    public readonly byte[] Wave = new byte[16];
    /// <summary>ram.hi[0x00..0x40]: the sound registers ($10-$3F) as gnuboy keeps them (index = register low byte).</summary>
    public readonly byte[] Hi = new byte[0x41];
    /// <summary>cpu.snd: 2.097152 MHz units not yet mixed.</summary>
    public int Snd;
    public bool Cgb;

    /// <summary>Called with each mixed sample (l, r in -128..127, before the +128 of gnuboy's unsigned 8-bit buffer).</summary>
    public Action<int, int>? Output;

    private const int NR10 = 0x10, NR11 = 0x11, NR12 = 0x12, NR13 = 0x13, NR14 = 0x14, NR21 = 0x16, NR22 = 0x17, NR23 = 0x18, NR24 = 0x19,
        NR30 = 0x1A, NR31 = 0x1B, NR32 = 0x1C, NR33 = 0x1D, NR34 = 0x1E, NR41 = 0x20, NR42 = 0x21, NR43 = 0x22, NR44 = 0x23,
        NR50 = 0x24, NR51 = 0x25, NR52 = 0x26;

    public GnubSound(bool cgb) { Cgb = cgb; Hi[0x40] = Lcdc; }

    private void s1_freq_d(int d)
    {
        if (RATE > (d << 4)) Ch[0].freq = 0;
        else Ch[0].freq = (RATE << 17) / d;
    }

    private void s1_freq() => s1_freq_d(2048 - (((Hi[NR14] & 7) << 8) + Hi[NR13]));

    private void s2_freq()
    {
        int d = 2048 - (((Hi[NR24] & 7) << 8) + Hi[NR23]);
        if (RATE > (d << 4)) Ch[1].freq = 0;
        else Ch[1].freq = (RATE << 17) / d;
    }

    private void s3_freq()
    {
        int d = 2048 - (((Hi[NR34] & 7) << 8) + Hi[NR33]);
        if (RATE > (d << 3)) Ch[2].freq = 0;
        else Ch[2].freq = (RATE << 21) / d;
    }

    private void s4_freq()
    {
        Ch[3].freq = (FreqTab[Hi[NR43] & 7] >> (Hi[NR43] >> 4)) * RATE;
        if ((Ch[3].freq >> 18) != 0) Ch[3].freq = 1 << 18;
    }

    private static void Env(ref Chan c, byte nrx2)
    {
        c.envol = nrx2 >> 4;
        c.endir = (nrx2 >> 3) & 1;
        c.endir |= c.endir - 1;
        c.enlen = (nrx2 & 7) << 15;
    }

    public void sound_dirty()
    {
        Ch[0].swlen = ((Hi[NR10] >> 4) & 7) << 14;
        Ch[0].len = (64 - (Hi[NR11] & 63)) << 13;
        Env(ref Ch[0], Hi[NR12]);
        s1_freq();
        Ch[1].len = (64 - (Hi[NR21] & 63)) << 13;
        Env(ref Ch[1], Hi[NR22]);
        s2_freq();
        Ch[2].len = (256 - Hi[NR31]) << 20;
        s3_freq();
        Ch[3].len = (64 - (Hi[NR41] & 63)) << 13;
        Env(ref Ch[3], Hi[NR42]);
        s4_freq();
    }

    public void sound_off()
    {
        for (int i = 0; i < 4; i++) Ch[i] = default;
        Hi[NR10] = 0x80; Hi[NR11] = 0xBF; Hi[NR12] = 0xF3; Hi[NR14] = 0xBF;
        Hi[NR21] = 0x3F; Hi[NR22] = 0x00; Hi[NR24] = 0xBF;
        Hi[NR30] = 0x7F; Hi[NR31] = 0xFF; Hi[NR32] = 0x9F; Hi[NR34] = 0xBF;
        Hi[NR41] = 0xFF; Hi[NR42] = 0x00; Hi[NR43] = 0x00; Hi[NR44] = 0xBF;
        Hi[NR50] = 0x77; Hi[NR51] = 0xF3; Hi[NR52] = 0x70;
        sound_dirty();
    }

    public void sound_reset()
    {
        for (int i = 0; i < 4; i++) Ch[i] = default;
        Array.Clear(Hi); Hi[0x40] = Lcdc;
        Snd = 0;
        Array.Copy(Cgb ? CgbWave : DmgWave, Wave, 16);
        Array.Copy(Wave, 0, Hi, 0x30, 16);
        sound_off();
        Hi[NR52] = 0xF1;
    }

    /// <summary>sound_advance: cpu.snd += units (2.097152 MHz); then mixes what is due, as gnuboy does before every register access.</summary>
    public void Advance(int units) { Snd += units; sound_mix(); }

    private static void EnvStep(ref Chan c)
    {
        if (c.enlen != 0 && (c.encnt += RATE) >= c.enlen)
        {
            c.encnt -= c.enlen;
            c.envol += c.endir;
            if (c.envol < 0) c.envol = 0;
            if (c.envol > 15) c.envol = 15;
        }
    }

    public void sound_mix()
    {
        int s, l, r, f, n;

        if (Snd < RATE) return;

        for (; Snd >= RATE; Snd -= RATE)
        {
            l = r = 0;
            ref Chan S1 = ref Ch[0];
            ref Chan S2 = ref Ch[1];
            ref Chan S3 = ref Ch[2];
            ref Chan S4 = ref Ch[3];

            if (S1.on != 0)
            {
                s = SqWave[Hi[NR11] >> 6, (S1.pos >> 18) & 7] & S1.envol;
                S1.pos += (uint)S1.freq;
                if ((Hi[NR14] & 64) != 0 && ((S1.cnt += RATE) >= S1.len))
                    S1.on = 0;
                EnvStep(ref S1);
                if (S1.swlen != 0 && (S1.swcnt += RATE) >= S1.swlen)
                {
                    S1.swcnt -= S1.swlen;
                    f = S1.swfreq;
                    n = Hi[NR10] & 7;
                    if ((Hi[NR10] & 8) != 0) f -= f >> n;
                    else f += f >> n;
                    if (f > 2047)
                        S1.on = 0;
                    else
                    {
                        S1.swfreq = f;
                        Hi[NR13] = (byte)f;
                        Hi[NR14] = (byte)((Hi[NR14] & 0xF8) | (f >> 8));
                        s1_freq_d(2048 - f);
                    }
                }
                s <<= 2;
                if ((Hi[NR51] & 1) != 0) r += s;
                if ((Hi[NR51] & 16) != 0) l += s;
            }

            if (S2.on != 0)
            {
                s = SqWave[Hi[NR21] >> 6, (S2.pos >> 18) & 7] & S2.envol;
                S2.pos += (uint)S2.freq;
                if ((Hi[NR24] & 64) != 0 && ((S2.cnt += RATE) >= S2.len))
                    S2.on = 0;
                EnvStep(ref S2);
                s <<= 2;
                if ((Hi[NR51] & 2) != 0) r += s;
                if ((Hi[NR51] & 32) != 0) l += s;
            }

            if (S3.on != 0)
            {
                s = Wave[(S3.pos >> 22) & 15];
                if ((S3.pos & (1 << 21)) != 0) s &= 15;
                else s >>= 4;
                s -= 8;
                S3.pos += (uint)S3.freq;
                if ((Hi[NR34] & 64) != 0 && ((S3.cnt += RATE) >= S3.len))
                    S3.on = 0;
                if ((Hi[NR32] & 96) != 0) s <<= 3 - ((Hi[NR32] >> 5) & 3);
                else s = 0;
                if ((Hi[NR51] & 4) != 0) r += s;
                if ((Hi[NR51] & 64) != 0) l += s;
            }

            if (S4.on != 0)
            {
                if ((Hi[NR43] & 8) != 0) s = 1 & (Noise7[(S4.pos >> 20) & 15] >> (int)(7 - ((S4.pos >> 17) & 7)));
                else s = 1 & (Noise15[(S4.pos >> 20) & 4095] >> (int)(7 - ((S4.pos >> 17) & 7)));
                s = (-s) & S4.envol;
                S4.pos += (uint)S4.freq;
                if ((Hi[NR44] & 64) != 0 && ((S4.cnt += RATE) >= S4.len))
                    S4.on = 0;
                EnvStep(ref S4);
                s += s << 1;
                if ((Hi[NR51] & 8) != 0) r += s;
                if ((Hi[NR51] & 128) != 0) l += s;
            }

            l *= Hi[NR50] & 0x07;
            r *= (Hi[NR50] & 0x70) >> 4;
            l >>= 4;
            r >>= 4;

            if (l > 127) l = 127;
            else if (l < -128) l = -128;
            if (r > 127) r = 127;
            else if (r < -128) r = -128;

            Output?.Invoke(l, r);
        }
        Hi[NR52] = (byte)((Hi[NR52] & 0xf0) | Ch[0].on | (Ch[1].on << 1) | (Ch[2].on << 2) | (Ch[3].on << 3));
    }

    public byte sound_read(int r)
    {
        sound_mix();
        return Hi[r];
    }

    private void s1_init()
    {
        ref Chan S1 = ref Ch[0];
        S1.swcnt = 0;
        S1.swfreq = ((Hi[NR14] & 7) << 8) + Hi[NR13];
        Env(ref S1, Hi[NR12]);
        if (S1.on == 0) S1.pos = 0;
        S1.on = 1;
        S1.cnt = 0;
        S1.encnt = 0;
    }

    private void s2_init()
    {
        ref Chan S2 = ref Ch[1];
        Env(ref S2, Hi[NR22]);
        if (S2.on == 0) S2.pos = 0;
        S2.on = 1;
        S2.cnt = 0;
        S2.encnt = 0;
    }

    private void s3_init()
    {
        ref Chan S3 = ref Ch[2];
        if (S3.on == 0) S3.pos = 0;
        S3.cnt = 0;
        S3.on = Hi[NR30] >> 7;
        if (S3.on != 0) for (int i = 0; i < 16; i++)
            Hi[i + 0x30] = (byte)(0x13 ^ Hi[i + 0x31]);
    }

    private void s4_init()
    {
        ref Chan S4 = ref Ch[3];
        Env(ref S4, Hi[NR42]);
        S4.on = 1;
        S4.pos = 0;
        S4.cnt = 0;
        S4.encnt = 0;
    }

    public void sound_write(int r, byte b)
    {
        if ((Hi[NR52] & 128) == 0 && r != NR52) return;
        if ((r & 0xF0) == 0x30)
        {
            if (Ch[2].on != 0) sound_mix();
            if (Ch[2].on == 0)
                Wave[r - 0x30] = Hi[r] = b;
            return;
        }
        sound_mix();
        switch (r)
        {
            case NR10:
                Hi[NR10] = b;
                Ch[0].swlen = ((Hi[NR10] >> 4) & 7) << 14;
                Ch[0].swfreq = ((Hi[NR14] & 7) << 8) + Hi[NR13];
                break;
            case NR11:
                Hi[NR11] = b;
                Ch[0].len = (64 - (Hi[NR11] & 63)) << 13;
                break;
            case NR12:
                Hi[NR12] = b;
                Env(ref Ch[0], b);
                break;
            case NR13:
                Hi[NR13] = b;
                s1_freq();
                break;
            case NR14:
                Hi[NR14] = b;
                s1_freq();
                if ((b & 128) != 0) s1_init();
                break;
            case NR21:
                Hi[NR21] = b;
                Ch[1].len = (64 - (Hi[NR21] & 63)) << 13;
                break;
            case NR22:
                Hi[NR22] = b;
                Env(ref Ch[1], b);
                break;
            case NR23:
                Hi[NR23] = b;
                s2_freq();
                break;
            case NR24:
                Hi[NR24] = b;
                s2_freq();
                if ((b & 128) != 0) s2_init();
                break;
            case NR30:
                Hi[NR30] = b;
                if ((b & 128) == 0) Ch[2].on = 0;
                break;
            case NR31:
                Hi[NR31] = b;
                Ch[2].len = (256 - Hi[NR31]) << 13;
                break;
            case NR32:
                Hi[NR32] = b;
                break;
            case NR33:
                Hi[NR33] = b;
                s3_freq();
                break;
            case NR34:
                Hi[NR34] = b;
                s3_freq();
                if ((b & 128) != 0) s3_init();
                break;
            case NR41:
                Hi[NR41] = b;
                Ch[3].len = (64 - (Hi[NR41] & 63)) << 13;
                break;
            case NR42:
                Hi[NR42] = b;
                Env(ref Ch[3], b);
                break;
            case NR43:
                Hi[NR43] = b;
                s4_freq();
                break;
            case NR44:
                Hi[NR44] = b;
                if ((b & 128) != 0) s4_init();
                break;
            case NR50:
                Hi[NR50] = b;
                break;
            case NR51:
                Hi[NR51] = b;
                break;
            case NR52:
                Hi[NR52] = b;
                if ((Hi[NR52] & 128) == 0)
                    sound_off();
                break;
            default:
                return;
        }
    }
}

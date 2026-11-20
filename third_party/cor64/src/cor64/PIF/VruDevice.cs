namespace cor64.PIF
{
    // [catalog_fix3] COR64_VRU: a Voice Recognition Unit (NUS-020) on a controller port that never hears a word. It answers
    // the device id (0x0001) and the VRU commands so games pass their "VRU connected" check: 0x09 read result (37 bytes),
    // 0x0A write word data (23/1), 0x0B read status (3/3), 0x0C write config (7/1), 0x0D write init (3/1). Write replies
    // are the joybus data CRC of the payload. Command set and reply layout from the public n64brew joybus notes.
    public sealed class VruDevice
    {
        private readonly bool m_Japan;
        private byte m_Status;     // byte 2 of the 0x00 status reply
        private byte m_Voice;      // 0 ready, 1 listening

        public VruDevice(bool japan) { m_Japan = japan; m_Voice = (byte)(japan ? 0 : 1); }

        public bool Comm(byte[] input, int send, int recv, byte[] output)
        {
            switch (input[0]) {
                case 0x00: case 0xFF:
                    if (recv < 3) return false;
                    output[0] = 0x00; output[1] = 0x01; output[2] = m_Status;
                    return true;
                case 0x01:
                    return recv >= 4; // no buttons
                case 0x09: // read recognition result: nothing recognised
                    if (recv < 37) return false;
                    output[0] = 0x00; output[1] = 0x80; output[2] = 0x00; output[3] = 0x0F;
                    output[34] = 0x40; output[35] = 0x00;
                    output[36] = JoybusPorts.DataCrcN(output, 0, 36);
                    m_Voice = 1;
                    return true;
                case 0x0A: // write word data
                    if (send < 23 || recv < 1) return false;
                    output[0] = JoybusPorts.DataCrcN(input, 3, 20);
                    m_Status = 0;
                    return true;
                case 0x0B: // read status
                    if (send < 3 || recv < 3) return false;
                    output[0] = m_Voice; output[1] = 0;
                    output[2] = JoybusPorts.DataCrcN(output, 0, 2);
                    m_Status = 1;
                    return true;
                case 0x0C: // write config
                    if (send < 7 || recv < 1) return false;
                    output[0] = JoybusPorts.DataCrcN(input, 3, 4);
                    if (output[0] == 0x4E) m_Voice = 1; // microphone on: listening
                    m_Status = 0;
                    return true;
                case 0x0D: // write init
                    if (send < 3 || recv < 1) return false;
                    output[0] = 0;
                    return true;
            }
            return false;
        }
    }
}

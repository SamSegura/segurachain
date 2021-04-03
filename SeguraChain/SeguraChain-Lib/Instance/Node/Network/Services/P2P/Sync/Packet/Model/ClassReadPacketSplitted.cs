using System.Collections.Generic;

namespace SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.Model
{
    /// <summary>
    /// Store packet data and set complete status once the packet separator is received.
    /// </summary>
    public class ClassReadPacketSplitted
    {
        public List<byte> Packet;
        public bool Complete;
        public ClassReadPacketSplitted()
        {
            Packet = new List<byte>();
        }
    }
}

using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Packet;

namespace SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet
{
    public class ClassPeerPacketSendObject
    {
        public ClassPeerEnumPacketSend PacketOrder;
        public string PacketContent; // The serialized packet encrypted.
        public string PacketHash;
        public string PacketSignature; // The signature of the packet hash.
        public string PacketPeerUniqueId;
        public string PublicKey;

        /// <summary>
        /// The peer unique id is mandatory.
        /// </summary>
        /// <param name="packetPeerUniqueId"></param>
        public ClassPeerPacketSendObject(string packetPeerUniqueId, string publicKey)
        {
            PacketPeerUniqueId = packetPeerUniqueId;
            PublicKey = publicKey;
        }
    }

    public class ClassPeerPacketRecvObject
    {
        public ClassPeerEnumPacketResponse PacketOrder;
        public string PacketContent; // The serialized packet encrypted.
        public string PacketHash;
        public string PacketSignature; // The signature of the packet hash.
        public string PacketPeerUniqueId;
        public string PublicKey;

        /// <summary>
        /// The peer unique id is mandatory.
        /// </summary>
        /// <param name="packetPeerUniqueId"></param>
        public ClassPeerPacketRecvObject(string packetPeerUniqueId, string publicKey)
        {
            PacketPeerUniqueId = packetPeerUniqueId;
            PublicKey = publicKey;
        }
    }
}

using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Packet;
using SeguraChain_Lib.Utility;
using System;

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
        public long PeerLastTimestampSignatureWhitelist;

  
        /// <summary>
        /// The peer unique id is mandatory.
        /// </summary>
        /// <param name="packetPeerUniqueId"></param>
        public ClassPeerPacketSendObject(string packetPeerUniqueId, string publicKey, long lastTimestampSignatureWhitelist)
        {
            PacketPeerUniqueId = packetPeerUniqueId;
            PublicKey = publicKey;
            PeerLastTimestampSignatureWhitelist = lastTimestampSignatureWhitelist;
        }

        public ClassPeerPacketSendObject(byte[] packetData, out bool status)
        {
            status = false;
            try
            {
                string[] splitPacketData = packetData.GetStringFromByteArrayAscii().Split(new[] { "#" }, StringSplitOptions.None);

                PacketOrder = (ClassPeerEnumPacketSend)int.Parse(splitPacketData[0]);
                PacketContent = splitPacketData[1];
                PacketHash = splitPacketData[2];
                PacketSignature = splitPacketData[3];
                PacketPeerUniqueId = splitPacketData[4];
                PublicKey = splitPacketData[5];
                PeerLastTimestampSignatureWhitelist = long.Parse(splitPacketData[6]);
                status = true;
            }
            catch 
            {
               // Ignored.
            }
        }

        public byte[] GetPacketData()
        {

            return ClassUtility.GetByteArrayFromStringAscii((int)PacketOrder + "#" +
                PacketContent + "#" +
                PacketHash + "#" +
                PacketSignature + "#" +
                PacketPeerUniqueId + "#" +
                PublicKey + "#" +
                PeerLastTimestampSignatureWhitelist);
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
        public long PeerLastTimestampSignatureWhitelist;

        /// <summary>
        /// The peer unique id is mandatory.
        /// </summary>
        /// <param name="packetPeerUniqueId"></param>
        public ClassPeerPacketRecvObject(string packetPeerUniqueId, string publicKey, long lastTimestampSignatureWhitelist)
        {
            PacketPeerUniqueId = packetPeerUniqueId;
            PublicKey = publicKey;
            PeerLastTimestampSignatureWhitelist = lastTimestampSignatureWhitelist;
        }

        public ClassPeerPacketRecvObject(byte[] packetData, out bool status)
        {
            status = false;

            try
            {
                string[] splitPacketData = packetData.GetStringFromByteArrayAscii().Split(new[] { "#" }, StringSplitOptions.None);

                PacketOrder = (ClassPeerEnumPacketResponse)int.Parse(splitPacketData[0]);
                PacketContent = splitPacketData[1];
                PacketHash = splitPacketData[2];
                PacketSignature = splitPacketData[3];
                PacketPeerUniqueId = splitPacketData[4];
                PublicKey = splitPacketData[5];
                PeerLastTimestampSignatureWhitelist = long.Parse(splitPacketData[6]);
                status = true;

            }
            catch
            {
                // Ignored.
            }
        }

        public byte[] GetPacketData()
        {
            return ClassUtility.GetByteArrayFromStringAscii((int)PacketOrder + "#" +
                PacketContent + "#" +
                PacketHash + "#" +
                PacketSignature + "#" +
                PacketPeerUniqueId + "#" +
                PublicKey + "#" +
                PeerLastTimestampSignatureWhitelist);
        }
    }
}

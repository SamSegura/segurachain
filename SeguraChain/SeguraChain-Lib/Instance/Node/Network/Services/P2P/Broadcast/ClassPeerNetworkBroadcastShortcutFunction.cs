using SeguraChain_Lib.Algorithm;
using SeguraChain_Lib.Blockchain.Wallet.Function;
using SeguraChain_Lib.Instance.Node.Network.Database;
using SeguraChain_Lib.Instance.Node.Network.Database.Manager;
using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ClientSync.ClientConnect.Object;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Utility;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SeguraChain_Lib.Instance.Node.Network.Services.P2P.Broadcast
{
    public class ClassPeerNetworkBroadcastShortcutFunction
    {
        public static async Task<R> SendBroadcastPacket<T, R>(ClassPeerNetworkClientSyncObject peerNetworkClientSyncObject, ClassPeerEnumPacketSend packetType, T packetToSend, string peerIpTarget, string peerUniqueIdTarget, ClassPeerNetworkSettingObject peerNetworkSetting, ClassPeerEnumPacketResponse packetTypeExpected, CancellationTokenSource cancellation)
        {
            ClassPeerPacketSendObject packetSendObject = new ClassPeerPacketSendObject(peerNetworkSetting.PeerUniqueId,
            ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget][peerUniqueIdTarget].PeerInternPublicKey,
            ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget][peerUniqueIdTarget].PeerClientLastTimestampPeerPacketSignatureWhitelist)
            {
                PacketOrder = packetType,
                PacketContent = ClassUtility.SerializeData(packetToSend)
            };

            packetSendObject = ClassPeerNetworkBroadcastFunction.BuildSignedPeerSendPacketObject(packetSendObject, peerIpTarget, peerUniqueIdTarget, true, cancellation);

            if (packetSendObject == null)
                return default(R);

            if (!await peerNetworkClientSyncObject.TrySendPacketToPeerTarget(packetSendObject.GetPacketData(), cancellation, packetTypeExpected, false, false))
                return default(R);

            if (peerNetworkClientSyncObject.PeerPacketReceived == null)
                return default(R);

            if (peerNetworkClientSyncObject.PeerPacketReceived.PacketOrder != packetTypeExpected)
                return default(R);

            bool peerPacketSignatureValid = ClassPeerCheckManager.CheckPeerClientWhitelistStatus(peerIpTarget, peerUniqueIdTarget, peerNetworkSetting) ? true : ClassWalletUtility.WalletCheckSignature(peerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget][peerUniqueIdTarget].PeerClientPublicKey);

            if (!peerPacketSignatureValid)
                return default(R);


            Tuple<byte[], bool> packetTupleDecrypted = ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget][peerUniqueIdTarget].GetInternCryptoStreamObject.DecryptDataProcess(Convert.FromBase64String(peerNetworkClientSyncObject.PeerPacketReceived.PacketContent));
            if (packetTupleDecrypted.Item1 == null || !packetTupleDecrypted.Item2)
            {
                if (ClassAes.DecryptionProcess(Convert.FromBase64String(peerNetworkClientSyncObject.PeerPacketReceived.PacketContent), ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget][peerUniqueIdTarget].PeerInternPacketEncryptionKey, ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget][peerUniqueIdTarget].PeerInternPacketEncryptionKeyIv, out byte[] packetDecrypted))
                    packetTupleDecrypted = new Tuple<byte[], bool>(packetDecrypted, true);
            }

            if (packetTupleDecrypted.Item1 == null || !packetTupleDecrypted.Item2)
                return default(R);

            if (!ClassUtility.TryDeserialize(packetTupleDecrypted.Item1.GetStringFromByteArrayAscii(), out R peerPacketReceived))
                return default(R);

            if (EqualityComparer<R>.Default.Equals(peerPacketReceived, default(R)))
                return default(R);


            return peerPacketReceived;
        }
    }
}

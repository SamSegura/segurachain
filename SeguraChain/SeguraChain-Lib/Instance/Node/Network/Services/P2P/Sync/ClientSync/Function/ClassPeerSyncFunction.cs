using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SeguraChain_Lib.Algorithm;
using SeguraChain_Lib.Blockchain.Block.Function;
using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using SeguraChain_Lib.Blockchain.Database;
using SeguraChain_Lib.Blockchain.Setting;
using SeguraChain_Lib.Blockchain.Sovereign.Database;
using SeguraChain_Lib.Blockchain.Sovereign.Enum;
using SeguraChain_Lib.Blockchain.Transaction.Enum;
using SeguraChain_Lib.Instance.Node.Network.Database;
using SeguraChain_Lib.Instance.Node.Network.Database.Manager;
using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ClientSync.ClientConnect.Object;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Response;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ClientSync.Function
{
    public class ClassPeerSyncFunction
    {
        #region Mandatory functions to handle a packet received.

        /// <summary>
        /// Check a packet signature.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="peerNetworkSetting"></param>
        /// <param name="packetHash"></param>
        /// <param name="packetSignature"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public bool CheckPacketSignature(string peerIp, string peerUniqueId, ClassPeerNetworkSettingObject peerNetworkSetting, string packetContent, ClassPeerEnumPacketResponse packetOrder, string packetHash, string packetSignature, CancellationTokenSource cancellation)
        {
            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                bool peerIgnorePacketSignature = ClassPeerCheckManager.CheckPeerClientWhitelistStatus(peerIp, peerUniqueId, peerNetworkSetting);

                bool peerPacketSignatureValid = true;

                if (!peerIgnorePacketSignature)
                {
                    if (ClassUtility.GenerateSha3512FromString(packetContent) == packetHash)
                    {
                        peerPacketSignatureValid = ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].GetClientCryptoStreamObject.CheckSignatureProcess(packetHash, packetSignature, ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientPublicKey);
                    }
                    else
                    {
                        return false;
                    }
                }

                return peerPacketSignatureValid;
            }
            return false;
        }

        /// <summary>
        /// Try to decrypt a packet content.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="packetContent"></param>
        /// <param name="cancellation"></param>
        /// <param name="packetDecrypted"></param>
        /// <returns></returns>
        public bool TryDecryptPacketPeerContent(string peerIp, int peerPort, string peerUniqueId, string packetContent, CancellationTokenSource cancellation, out byte[] packetDecrypted)
        {
            packetDecrypted = null; // Default.

            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].GetInternCryptoStreamObject != null)
                {
                    Task<Tuple<byte[], bool>> taskPacketDecrypt = ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].GetInternCryptoStreamObject.DecryptDataProcess(Convert.FromBase64String(packetContent), cancellation);

                    taskPacketDecrypt.Wait(cancellation.Token);

                    if (taskPacketDecrypt.IsCompleted)
                    {
                        if (taskPacketDecrypt.Result != null)
                        {
                            if (taskPacketDecrypt.Result.Item2 && taskPacketDecrypt.Result.Item1 != null)
                            {
                                if (taskPacketDecrypt.Result.Item1.Length == 0)
                                {
                                    return false;
                                }
                                packetDecrypted = taskPacketDecrypt.Result.Item1;

                                return true;
                            }

                            return false;
                        }

                        ClassLog.WriteLine(peerIp + ":" + peerPort + " send a packet who can't be decrypted by internal keys sent.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_SYNC, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);
                        return false;
                    }

                    ClassLog.WriteLine(peerIp + ":" + peerPort + " send a packet who can't be decrypted propertly, an exception or a cancellation is probably the problem.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_SYNC, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Deserialize packet content.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="packetContent"></param>
        /// <param name="packetResult"></param>
        /// <returns></returns>
        public bool DeserializePacketContent<T>(string packetContent, out T packetResult)
        {
            if (ClassUtility.TryDeserialize(packetContent, out packetResult, ObjectCreationHandling.Reuse))
            {
                if (packetResult != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Check a peer unique id.
        /// </summary>
        /// <param name="peerUniqueId"></param>
        /// <returns></returns>
        private bool CheckPeerUniqueId(string peerUniqueId)
        {
            // Empty peer unique id.
            if (peerUniqueId.IsNullOrEmpty())
            {
                return false;
            }

            // Invalid peer unique id.
            if (!ClassUtility.CheckHexStringFormat(peerUniqueId))
            {
                return false;
            }

            // Invalid peer unique id length.
            if (peerUniqueId.Length != BlockchainSetting.PeerUniqueIdHashLength)
            {
                return false;
            }

            return true;
        }

        #endregion

        #region Get and check packet peer auth keys.

        /// <summary>
        /// Try get a peer auth keys packet from a packet received.
        /// </summary>
        /// <param name="peerNetworkClientSyncObject"></param>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="peerPacketSendPeerAuthKeys"></param>
        /// <returns></returns>
        public bool TryGetPacketPeerAuthKeys(ClassPeerNetworkClientSyncObject peerNetworkClientSyncObject, string peerIp, int peerPort, string peerUniqueId, ClassPeerNetworkSettingObject peerNetworkSettingObject, out ClassPeerPacketSendPeerAuthKeys peerPacketSendPeerAuthKeys)
        {
            peerPacketSendPeerAuthKeys = null; // Default.

            if (!CheckPeerUniqueId(peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId))
            {
                return false;
            }

            if (!DeserializePacketContent(peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, out peerPacketSendPeerAuthKeys))
            {
                return false;
            }

            if (!CheckPacketPeerAuthKeys(peerPacketSendPeerAuthKeys, peerNetworkSettingObject))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check a packet peer auth keys data.
        /// </summary>
        /// <param name="peerPacketSendPeerAuthKeys"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <returns></returns>
        private bool CheckPacketPeerAuthKeys(ClassPeerPacketSendPeerAuthKeys peerPacketSendPeerAuthKeys, ClassPeerNetworkSettingObject peerNetworkSettingObject)
        {
            if (peerPacketSendPeerAuthKeys == null)
            {
                return false;
            }

            if (!ClassUtility.CheckPacketTimestamp(peerPacketSendPeerAuthKeys.PacketTimestamp, peerNetworkSettingObject.PeerMaxTimestampDelayPacket, peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
            {
                return false;
            }

            return true;
        }

        #endregion

        #region Get and check packet peer list.

        /// <summary>
        /// Try get a packet peer list from a packet received.
        /// </summary>
        /// <param name="peerNetworkClientSyncObject"></param>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="cancellation"></param>
        /// <param name="packetPeerList"></param>
        /// <returns></returns>
        public bool TryGetPacketPeerList(ClassPeerNetworkClientSyncObject peerNetworkClientSyncObject, string peerIp, int peerPort, ClassPeerNetworkSettingObject peerNetworkSettingObject, CancellationTokenSource cancellation, out ClassPeerPacketSendPeerList packetPeerList)
        {
            packetPeerList = null; // Default.

            bool checkPacketSignature = CheckPacketSignature(peerIp, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkSettingObject, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, peerNetworkClientSyncObject.PeerPacketReceived.PacketOrder, peerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, cancellation);

            if (!checkPacketSignature)
            {
                return false;
            }

            if (!TryDecryptPacketPeerContent(peerIp, peerPort, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, cancellation, out byte[] packetDecrypted))
            {
                return false;
            }

            if (packetDecrypted.Length == 0)
            {
                return false;
            }

            if (!DeserializePacketContent(packetDecrypted.GetStringFromByteArrayAscii(), out packetPeerList))
            {
                return false;
            }

            if (!CheckPeerPacketList(packetPeerList, peerNetworkSettingObject))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check packet peer list data.
        /// </summary>
        /// <param name="packetPeerList"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <returns></returns>
        private bool CheckPeerPacketList(ClassPeerPacketSendPeerList packetPeerList, ClassPeerNetworkSettingObject peerNetworkSettingObject)
        {
            if (packetPeerList == null)
            {
                return false;
            }

            if (!ClassUtility.CheckPacketTimestamp(packetPeerList.PacketTimestamp, peerNetworkSettingObject.PeerMaxTimestampDelayPacket, peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
            {
                return false;
            }

            if (packetPeerList.PeerIpList == null || packetPeerList.PeerPortList == null || packetPeerList.PeerUniqueIdList == null)
            {
                return false;
            }

            int countPeerIp = packetPeerList.PeerIpList.Count;

            if (countPeerIp > 0)
            {

                int countPeerPort = packetPeerList.PeerPortList.Count;
                int countPeerUniqueId = packetPeerList.PeerUniqueIdList.Count;

                if (!(countPeerIp == countPeerPort && countPeerIp == countPeerUniqueId))
                {
                    return false;
                }

                foreach (int peerPort in packetPeerList.PeerPortList)
                {
                    if (peerPort < BlockchainSetting.PeerMinPort || peerPort > BlockchainSetting.PeerMaxPort)
                    {
                        return false;
                    }
                }

                foreach (string peerIp in packetPeerList.PeerIpList)
                {
                    if (peerIp.IsNullOrEmpty())
                    {
                        return false;
                    }


                    if (!IPAddress.TryParse(peerIp, out _))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        #endregion

        #region Get and check packet network informations.

        /// <summary>
        /// Try get a packet network information from a packet received.
        /// </summary>
        /// <param name="peerNetworkClientSyncObject"></param>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="cancellation"></param>
        /// <param name="peerPacketNetworkInformation"></param>
        /// 
        /// <returns></returns>
        public bool TryGetPacketNetworkInformation(ClassPeerNetworkClientSyncObject peerNetworkClientSyncObject, string peerIp, int peerPort, ClassPeerNetworkSettingObject peerNetworkSettingObject, CancellationTokenSource cancellation, out ClassPeerPacketSendNetworkInformation peerPacketNetworkInformation)
        {
            peerPacketNetworkInformation = null; // Default.

            bool checkPacketSignature = CheckPacketSignature(peerIp, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkSettingObject, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, peerNetworkClientSyncObject.PeerPacketReceived.PacketOrder, peerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, cancellation);

            if (!checkPacketSignature)
            {
                return false;
            }

            if (!TryDecryptPacketPeerContent(peerIp, peerPort, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, cancellation, out byte[] packetDecrypted))
            {
                return false;
            }

            if (!DeserializePacketContent(packetDecrypted.GetStringFromByteArrayAscii(), out peerPacketNetworkInformation))
            {
                return false;
            }


            if (!CheckPacketNetworkInformation(peerPacketNetworkInformation, peerNetworkSettingObject))
            {

                return false;
            }

            return true;
        }

        /// <summary>
        /// Check packet network information data.
        /// </summary>
        /// <param name="peerPacketNetworkInformation"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <returns></returns>
        private bool CheckPacketNetworkInformation(ClassPeerPacketSendNetworkInformation peerPacketNetworkInformation, ClassPeerNetworkSettingObject peerNetworkSettingObject)
        {
            if (peerPacketNetworkInformation == null)
            {
                return false;
            }

            if (!ClassUtility.CheckPacketTimestamp(peerPacketNetworkInformation.PacketTimestamp, peerNetworkSettingObject.PeerMaxTimestampDelayPacket, peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
            {
                return false;
            }

            if (peerPacketNetworkInformation.CurrentBlockHeight < BlockchainSetting.GenesisBlockHeight)
            {
                return false;
            }

            if (peerPacketNetworkInformation.CurrentBlockHash.IsNullOrEmpty())
            {
                return false;
            }

            if (peerPacketNetworkInformation.CurrentBlockHash.Length != BlockchainSetting.BlockHashHexSize)
            {
                return false;
            }

            if (peerPacketNetworkInformation.CurrentBlockDifficulty < BlockchainSetting.MiningMinDifficulty)
            {
                return false;
            }

            if (peerPacketNetworkInformation.LastBlockHeightUnlocked > peerPacketNetworkInformation.CurrentBlockHeight)
            {
                return false;
            }

            if (!ClassBlockUtility.GetBlockTemplateFromBlockHash(peerPacketNetworkInformation.CurrentBlockHash, out ClassBlockTemplateObject blockTemplateObject))
            {
                return false;
            }

            if (blockTemplateObject == null)
            {
                return false;
            }




            return true;
        }

        #endregion

        #region Get and check packet sovereign update list.

        /// <summary>
        /// Try get a packet sovereign update list received.
        /// </summary>
        /// <param name="peerNetworkClientSyncObject"></param>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="cancellation"></param>
        /// <param name="packetPeerSovereignUpdateList"></param>
        /// 
        /// <returns></returns>
        public bool TryGetPacketSovereignUpdateList(ClassPeerNetworkClientSyncObject peerNetworkClientSyncObject, string peerIp, int peerPort, ClassPeerNetworkSettingObject peerNetworkSettingObject, CancellationTokenSource cancellation, out ClassPeerPacketSendListSovereignUpdate packetPeerSovereignUpdateList)
        {
            packetPeerSovereignUpdateList = null; // Default.

            bool checkPacketSignature = CheckPacketSignature(peerIp, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkSettingObject, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, peerNetworkClientSyncObject.PeerPacketReceived.PacketOrder, peerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, cancellation);

            if (!checkPacketSignature)
            {
                return false;
            }

            if (!TryDecryptPacketPeerContent(peerIp, peerPort, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, cancellation, out byte[] packetDecrypted))
            {
                return false;
            }

            if (!DeserializePacketContent(packetDecrypted.GetStringFromByteArrayAscii(), out packetPeerSovereignUpdateList))
            {
                return false;
            }

            if (!CheckPacketSovereignUpdateList(packetPeerSovereignUpdateList, peerNetworkSettingObject))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check packet sovereign update list data.
        /// </summary>
        /// <param name="packetPeerSendSovereignUpdateList"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <returns></returns>
        private bool CheckPacketSovereignUpdateList(ClassPeerPacketSendListSovereignUpdate packetPeerSendSovereignUpdateList, ClassPeerNetworkSettingObject peerNetworkSettingObject)
        {
            if (packetPeerSendSovereignUpdateList == null)
            {
                return false;
            }

            if (!ClassUtility.CheckPacketTimestamp(packetPeerSendSovereignUpdateList.PacketTimestamp, peerNetworkSettingObject.PeerMaxTimestampDelayPacket, peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
            {
                return false;
            }

            if (packetPeerSendSovereignUpdateList.SovereignUpdateHashList == null)
            {
                return false;
            }

            if (packetPeerSendSovereignUpdateList.SovereignUpdateHashList.Count > 0)
            {
                foreach (var sovereignUpdateHash in packetPeerSendSovereignUpdateList.SovereignUpdateHashList)
                {
                    if (sovereignUpdateHash.IsNullOrEmpty())
                    {
                        return false;
                    }
                    if (!ClassUtility.CheckHexStringFormat(sovereignUpdateHash))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        #endregion

        #region Get and check packet sovereign update data.

        /// <summary>
        /// Try get a packet sovereign update data received.
        /// </summary>
        /// <param name="peerNetworkClientSyncObject"></param>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="cancellation"></param>
        /// <param name="packetSovereignUpdateData"></param>
        /// 
        /// <returns></returns>
        public bool TryGetPacketSovereignUpdateData(ClassPeerNetworkClientSyncObject peerNetworkClientSyncObject, string peerIp, int peerPort, ClassPeerNetworkSettingObject peerNetworkSettingObject, CancellationTokenSource cancellation, out ClassPeerPacketSendSovereignUpdateFromHash packetSovereignUpdateData)
        {
            packetSovereignUpdateData = null; // Default.

            bool checkPacketSignature = CheckPacketSignature(peerIp, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkSettingObject, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, peerNetworkClientSyncObject.PeerPacketReceived.PacketOrder, peerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, cancellation);

            if (!checkPacketSignature)
            {
                return false;
            }

            if (!TryDecryptPacketPeerContent(peerIp, peerPort, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, cancellation, out byte[] packetDecrypted))
            {
                return false;
            }

            if (!DeserializePacketContent(packetDecrypted.GetStringFromByteArrayAscii(), out packetSovereignUpdateData))
            {
                return false;
            }

            if (!CheckPacketSovereignUpdateData(packetSovereignUpdateData, peerNetworkSettingObject))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check packet sovereign update data.
        /// </summary>
        /// <param name="packetSovereignUpdateData"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <returns></returns>
        private bool CheckPacketSovereignUpdateData(ClassPeerPacketSendSovereignUpdateFromHash packetSovereignUpdateData, ClassPeerNetworkSettingObject peerNetworkSettingObject)
        {
            if (packetSovereignUpdateData?.SovereignUpdateObject == null)
            {
                return false;
            }

            if (packetSovereignUpdateData.SovereignUpdateObject.SovereignUpdateContent.Description.IsNullOrEmpty())
            {
                return false;
            }

            if (packetSovereignUpdateData.SovereignUpdateObject.SovereignUpdateContent.PossibleContent1.IsNullOrEmpty())
            {
                return false;
            }

            if (packetSovereignUpdateData.SovereignUpdateObject.SovereignUpdateContent.PossibleContent2.IsNullOrEmpty())
            {
                return false;
            }

            if (packetSovereignUpdateData.SovereignUpdateObject.SovereignUpdateContent == null)
            {
                return false;
            }

            if (packetSovereignUpdateData.SovereignUpdateObject.SovereignUpdateDevWalletAddress.IsNullOrEmpty())
            {
                return false;
            }

            if (packetSovereignUpdateData.SovereignUpdateObject.SovereignUpdateHash.IsNullOrEmpty())
            {
                return false;
            }

            if (packetSovereignUpdateData.SovereignUpdateObject.SovereignUpdateSignature.IsNullOrEmpty())
            {
                return false;
            }

            if (!ClassUtility.CheckPacketTimestamp(packetSovereignUpdateData.PacketTimestamp, peerNetworkSettingObject.PeerMaxTimestampDelayPacket, peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
            {
                return false;
            }

            if (ClassSovereignUpdateDatabase.CheckSovereignUpdateObject(packetSovereignUpdateData.SovereignUpdateObject, out _) != ClassSovereignEnumUpdateCheckStatus.VALID_SOVEREIGN_UPDATE)
            {
                return false;
            }

            return true;
        }

        #endregion

        #region Get and check packet block informations data.

        /// <summary>
        /// Try get a block information data from a packet received.
        /// </summary>
        /// <param name="peerNetworkClientSyncObject"></param>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="blockHeightTarget"></param>
        /// <param name="blockHash"></param>
        /// <param name="blockFinalTransactionHash"></param>
        /// <param name="cancellation"></param>
        /// <param name="packetSendBlockHeightInformation"></param>
        /// 
        /// <returns></returns>
        public bool TryGetPacketBlockInformationData(ClassPeerNetworkClientSyncObject peerNetworkClientSyncObject, string peerIp, int peerPort, ClassPeerNetworkSettingObject peerNetworkSettingObject, long blockHeightTarget, string blockHash, string blockFinalTransactionHash, CancellationTokenSource cancellation, out ClassPeerPacketSendBlockHeightInformation packetSendBlockHeightInformation)
        {
            packetSendBlockHeightInformation = null; // Default.

            bool checkPacketSignature = CheckPacketSignature(peerIp, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkSettingObject, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, peerNetworkClientSyncObject.PeerPacketReceived.PacketOrder, peerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, cancellation);

            if (!checkPacketSignature)
            {
                return false;
            }

            if (!TryDecryptPacketPeerContent(peerIp, peerPort, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, cancellation, out byte[] packetDecrypted))
            {
                return false;
            }

            if (!DeserializePacketContent(packetDecrypted.GetStringFromByteArrayAscii(), out packetSendBlockHeightInformation))
            {
                return false;
            }

            if (!CheckPacketBlockInformationData(packetSendBlockHeightInformation, peerNetworkSettingObject, blockHeightTarget, blockHash, blockFinalTransactionHash))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check block information data.
        /// </summary>
        /// <param name="packetSendBlockHeightInformation"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="blockHeightTarget"></param>
        /// <param name="blockHash"></param>
        /// <param name="blockFinalTransactionHash"></param>
        /// <returns></returns>
        private bool CheckPacketBlockInformationData(ClassPeerPacketSendBlockHeightInformation packetSendBlockHeightInformation, ClassPeerNetworkSettingObject peerNetworkSettingObject, long blockHeightTarget, string blockHash, string blockFinalTransactionHash)
        {
            if (packetSendBlockHeightInformation == null)
            {
                return false;
            }

            if (!ClassUtility.CheckPacketTimestamp(packetSendBlockHeightInformation.PacketTimestamp, peerNetworkSettingObject.PeerMaxTimestampDelayPacket, peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
            {
                return false;
            }

            if (packetSendBlockHeightInformation.BlockHeight != blockHeightTarget ||
                packetSendBlockHeightInformation.BlockHash != blockHash ||
                packetSendBlockHeightInformation.BlockFinalTransactionHash != blockFinalTransactionHash)
            {
                return false;
            }

            return true;
        }

        #endregion

        #region Get and check packet block data.

        /// <summary>
        /// Try get a packet block data received.
        /// </summary>
        /// <param name="peerNetworkClientSyncObject"></param>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="blockHeightTarget"></param>
        /// <param name="refuseLockedBlock"></param>
        /// <param name="cancellation"></param>
        /// <param name="packetSendBlockData"></param>
        /// 
        /// <returns></returns>
        public bool TryGetPacketBlockData(ClassPeerNetworkClientSyncObject peerNetworkClientSyncObject, string peerIp, int peerPort, ClassPeerNetworkSettingObject peerNetworkSettingObject, long blockHeightTarget, bool refuseLockedBlock, CancellationTokenSource cancellation, out ClassPeerPacketSendBlockData packetSendBlockData)
        {
            packetSendBlockData = null; // Default.

            bool checkPacketSignature = CheckPacketSignature(peerIp, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkSettingObject, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, peerNetworkClientSyncObject.PeerPacketReceived.PacketOrder, peerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, cancellation);

            if (!checkPacketSignature)
            {
                return false;
            }

            if (!TryDecryptPacketPeerContent(peerIp, peerPort, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, cancellation, out byte[] packetDecrypted))
            {
                return false;
            }

            if (!DeserializePacketContent(packetDecrypted.GetStringFromByteArrayAscii(), out packetSendBlockData))
            {
                return false;
            }

            Task<bool> result = CheckPacketBlockData(packetSendBlockData, blockHeightTarget, refuseLockedBlock, peerNetworkSettingObject, cancellation);
            result.Wait(cancellation.Token);

            if (!result.IsCompleted)
            {
                return false;
            }

            if (!result.Result)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check packet block data.
        /// </summary>
        /// <param name="packetSendBlockData"></param>
        /// <param name="blockHeightTarget"></param>
        /// <param name="refuseLockedBlock"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> CheckPacketBlockData(ClassPeerPacketSendBlockData packetSendBlockData, long blockHeightTarget, bool refuseLockedBlock, ClassPeerNetworkSettingObject peerNetworkSettingObject, CancellationTokenSource cancellation)
        {

            if (packetSendBlockData?.BlockData?.BlockHash == null)
            {
                return false;
            }

            if (!ClassUtility.CheckPacketTimestamp(packetSendBlockData.PacketTimestamp, peerNetworkSettingObject.PeerMaxTimestampDelayPacket, peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
            {
                return false;
            }

            if (packetSendBlockData.BlockData == null)
            {
                return false;
            }

            // Reset block data just in case.
            packetSendBlockData.BlockData.BlockTransactionFullyConfirmed = false;
            packetSendBlockData.BlockData.BlockUnlockValid = false;
            packetSendBlockData.BlockData.BlockNetworkAmountConfirmations = 0;
            packetSendBlockData.BlockData.BlockSlowNetworkAmountConfirmations = 0;
            packetSendBlockData.BlockData.BlockLastHeightTransactionConfirmationDone = 0;
            packetSendBlockData.BlockData.BlockTotalTaskTransactionConfirmationDone = 0;
            packetSendBlockData.BlockData.BlockTransactionConfirmationCheckTaskDone = false;
            packetSendBlockData.BlockData.BlockTotalTaskTransactionConfirmationDone = 0;
            packetSendBlockData.BlockData.TotalCoinConfirmed = 0;
            packetSendBlockData.BlockData.TotalCoinPending = 0;
            packetSendBlockData.BlockData.TotalFee = 0;
            packetSendBlockData.BlockData.TotalTransactionConfirmed = 0;

            if (!await ClassBlockUtility.CheckBlockDataObject(packetSendBlockData.BlockData, blockHeightTarget, refuseLockedBlock, cancellation))
            {
                return false;
            }

            return true;
        }


        #endregion

        #region Get and check packet block transaction data.

        /// <summary>
        /// Try get a packet block transaction data received.
        /// </summary>
        /// <param name="peerNetworkClientSyncObject"></param>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="blockHeightTarget"></param>
        /// <param name="cancellation"></param>
        /// <param name="packetSendBlockTransactionData"></param>
        /// 
        /// <returns></returns>
        public bool TryGetPacketBlockTransactionData(ClassPeerNetworkClientSyncObject peerNetworkClientSyncObject, string peerIp, int peerPort, ClassPeerNetworkSettingObject peerNetworkSettingObject, long blockHeightTarget, CancellationTokenSource cancellation, out ClassPeerPacketSendBlockTransactionData packetSendBlockTransactionData)
        {
            packetSendBlockTransactionData = null; // Default.

            bool checkPacketSignature = CheckPacketSignature(peerIp, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkSettingObject, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, peerNetworkClientSyncObject.PeerPacketReceived.PacketOrder, peerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, cancellation);

            if (!checkPacketSignature)
            {
                return false;
            }

            if (!TryDecryptPacketPeerContent(peerIp, peerPort, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, cancellation, out byte[] packetDecrypted))
            {
                return false;
            }

            if (!DeserializePacketContent(packetDecrypted.GetStringFromByteArrayAscii(), out packetSendBlockTransactionData))
            {
                return false;
            }


            if (!ClassUtility.CheckPacketTimestamp(packetSendBlockTransactionData.PacketTimestamp, peerNetworkSettingObject.PeerMaxTimestampDelayPacket, peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
            {
                return false;
            }

            Task<bool> checkBlockTransactionData = CheckPacketBlockTransactionData(packetSendBlockTransactionData, blockHeightTarget, null, cancellation);
            checkBlockTransactionData.Wait(cancellation.Token);

            if (!checkBlockTransactionData.IsCompleted)
            {
                return false;
            }
            if (!checkBlockTransactionData.Result)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Try get a packet block transaction data received.
        /// </summary>
        /// <param name="peerNetworkClientSyncObject"></param>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="listWalletAndPublicKeys"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="blockHeightTarget"></param>
        /// <param name="cancellation"></param>
        /// <param name="packetSendBlockTransactionDataByRange"></param>
        /// 
        /// <returns></returns>
        public bool TryGetPacketBlockTransactionDataByRange(ClassPeerNetworkClientSyncObject peerNetworkClientSyncObject, string peerIp, int peerPort, Dictionary<string, string> listWalletAndPublicKeys, ClassPeerNetworkSettingObject peerNetworkSettingObject, long blockHeightTarget, CancellationTokenSource cancellation, out ClassPeerPacketSendBlockTransactionDataByRange packetSendBlockTransactionDataByRange)
        {
            packetSendBlockTransactionDataByRange = null; // Default.

            bool checkPacketSignature = CheckPacketSignature(peerIp, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkSettingObject, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, peerNetworkClientSyncObject.PeerPacketReceived.PacketOrder, peerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, cancellation);

            if (!checkPacketSignature)
            {
                return false;
            }

            if (!TryDecryptPacketPeerContent(peerIp, peerPort, peerNetworkClientSyncObject.PeerPacketReceived.PacketPeerUniqueId, peerNetworkClientSyncObject.PeerPacketReceived.PacketContent, cancellation, out byte[] packetDecrypted))
            {
                return false;
            }

            if (!DeserializePacketContent(packetDecrypted.GetStringFromByteArrayAscii(), out packetSendBlockTransactionDataByRange))
            {
                return false;
            }

            if (packetSendBlockTransactionDataByRange.ListTransactionObject == null)
            {
                return false;
            }

            if (packetSendBlockTransactionDataByRange.ListTransactionObject.Count == 0)
            {
                return false;
            }


            if (!ClassUtility.CheckPacketTimestamp(packetSendBlockTransactionDataByRange.PacketTimestamp, peerNetworkSettingObject.PeerMaxTimestampDelayPacket, peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
            {
                return false;
            }

            Task<bool> checkBlockTransactionData = ClassPeerPacketSendBlockTransactionDataByRange(packetSendBlockTransactionDataByRange, blockHeightTarget, listWalletAndPublicKeys, cancellation);
            checkBlockTransactionData.Wait(cancellation.Token);

            if (!checkBlockTransactionData.IsCompleted)
            {
                return false;
            }
            if (!checkBlockTransactionData.Result)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check packet block transaction data by range.
        /// </summary>
        /// <param name="packetSendBlockTransactionDataByRange"></param>
        /// <param name="blockHeightTarget"></param>
        /// <param name="listWalletAndPublicKeys"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> ClassPeerPacketSendBlockTransactionDataByRange(ClassPeerPacketSendBlockTransactionDataByRange packetSendBlockTransactionDataByRange, long blockHeightTarget, Dictionary<string, string> listWalletAndPublicKeys, CancellationTokenSource cancellation)
        {
            bool result = true;


            foreach (string transactionHash in packetSendBlockTransactionDataByRange.ListTransactionObject.Keys)
            {
                if (!await CheckPacketBlockTransactionData(new ClassPeerPacketSendBlockTransactionData()
                {
                    BlockHeight = packetSendBlockTransactionDataByRange.BlockHeight,
                    TransactionObject = packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash],
                    PacketTimestamp = packetSendBlockTransactionDataByRange.PacketTimestamp,
                    PacketNumericHash = packetSendBlockTransactionDataByRange.PacketNumericHash,
                    PacketNumericSignature = packetSendBlockTransactionDataByRange.PacketNumericSignature
                }, blockHeightTarget, listWalletAndPublicKeys, cancellation))
                {
                    result = false;
                    break;
                }

                if (listWalletAndPublicKeys != null)
                {
                    switch (packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash].TransactionType)
                    {
                        case ClassTransactionEnumType.NORMAL_TRANSACTION:
                            lock (listWalletAndPublicKeys)
                            {
                                if (!listWalletAndPublicKeys.ContainsKey(packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash].WalletAddressSender))
                                {
                                    listWalletAndPublicKeys.Add(packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash].WalletAddressSender, packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash].WalletPublicKeySender);
                                }
                            }
                            break;
                        case ClassTransactionEnumType.TRANSFER_TRANSACTION:
                            lock (listWalletAndPublicKeys)
                            {
                                if (!listWalletAndPublicKeys.ContainsKey(packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash].WalletAddressSender))
                                {
                                    listWalletAndPublicKeys.Add(packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash].WalletAddressSender, packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash].WalletPublicKeySender);
                                }
                                if (!listWalletAndPublicKeys.ContainsKey(packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash].WalletAddressReceiver))
                                {
                                    listWalletAndPublicKeys.Add(packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash].WalletAddressReceiver, packetSendBlockTransactionDataByRange.ListTransactionObject[transactionHash].WalletPublicKeyReceiver);
                                }
                            }
                            break;
                    }
                }
            }

            listWalletAndPublicKeys.Clear();

            return result;
        }

        /// <summary>
        /// Check packet block transaction data.
        /// </summary>
        /// <param name="packetSendBlockTransactionData"></param>
        /// <param name="blockHeightTarget"></param>
        /// <param name="listWalletAndPublicKeys"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> CheckPacketBlockTransactionData(ClassPeerPacketSendBlockTransactionData packetSendBlockTransactionData, long blockHeightTarget, Dictionary<string, string> listWalletAndPublicKeys, CancellationTokenSource cancellation)
        {
            if (packetSendBlockTransactionData == null)
            {
                return false;
            }

            if (packetSendBlockTransactionData.BlockHeight != blockHeightTarget)
            {
                return false;
            }

            if (packetSendBlockTransactionData.TransactionObject == null)
            {
                return false;
            }

            if (packetSendBlockTransactionData.TransactionObject.BlockHeightTransaction != blockHeightTarget)
            {
                return false;
            }

            ClassTransactionEnumStatus checkTxResult = await ClassBlockchainDatabase.BlockchainMemoryManagement.CheckTransaction(packetSendBlockTransactionData.TransactionObject, null, false, listWalletAndPublicKeys, cancellation, false);

            if (checkTxResult != ClassTransactionEnumStatus.VALID_TRANSACTION)
            {
#if DEBUG
                Debug.WriteLine(packetSendBlockTransactionData.TransactionObject.TransactionHash + " synced from peers is invalid. Check result: " + checkTxResult);
#endif
                return false;
            }

            return true;
        }

        #endregion
    }
}

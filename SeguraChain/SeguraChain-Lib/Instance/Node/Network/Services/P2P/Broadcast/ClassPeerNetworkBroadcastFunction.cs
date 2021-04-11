using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SeguraChain_Lib.Algorithm;
using SeguraChain_Lib.Blockchain.Block.Enum;
using SeguraChain_Lib.Blockchain.Database;
using SeguraChain_Lib.Blockchain.Mining.Function;
using SeguraChain_Lib.Blockchain.Mining.Object;
using SeguraChain_Lib.Blockchain.Setting;
using SeguraChain_Lib.Blockchain.Transaction.Enum;
using SeguraChain_Lib.Blockchain.Transaction.Object;
using SeguraChain_Lib.Blockchain.Wallet.Function;
using SeguraChain_Lib.Instance.Node.Network.Database;
using SeguraChain_Lib.Instance.Node.Network.Database.Manager;
using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Packet;
using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Status;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ClientSync.ClientConnect.Object;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Request;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Response;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Response.Enum;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Instance.Node.Network.Services.P2P.Broadcast
{
    public class ClassPeerNetworkBroadcastFunction
    {
        #region General utility.

        private static Dictionary<int, ClassPeerTargetObject> peerListBroadcastMiningShareTarget = new Dictionary<int, ClassPeerTargetObject>();

        /// <summary>
        /// Generate a list of random alive peers excepting the peer server and another peer.
        /// </summary>
        /// <param name="peerServerIp"></param>
        /// <param name="peerOpenNatServerIp"></param>
        /// <param name="peerToExcept"></param>
        /// <param name="previousListPeerSelected"></param>
        /// <param name="peerNetworkSetting"></param>
        /// <param name="peerFirewallSettingObject"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public static Dictionary<int, ClassPeerTargetObject> GetRandomListPeerTargetAlive(string peerServerIp, string peerOpenNatServerIp, string peerToExcept, Dictionary<int, ClassPeerTargetObject> previousListPeerSelected, ClassPeerNetworkSettingObject peerNetworkSetting, ClassPeerFirewallSettingObject peerFirewallSettingObject, CancellationTokenSource cancellation)
        {
            if (previousListPeerSelected == null)
            {
                previousListPeerSelected = new Dictionary<int, ClassPeerTargetObject>();
            }

            Dictionary<int, ClassPeerTargetObject> newListSelected = new Dictionary<int, ClassPeerTargetObject>();

            if (previousListPeerSelected.Count > 0)
            {
                foreach (int peerIndex in previousListPeerSelected.Keys.ToArray())
                {
                    cancellation?.Token.ThrowIfCancellationRequested();
                    bool removePeerConnection = false;
                    string peerIp = previousListPeerSelected[peerIndex].PeerIpTarget;
                    if (peerIp != peerToExcept && peerIp != peerServerIp && peerIp != peerOpenNatServerIp)
                    {
                        string peerUniqueId = previousListPeerSelected[peerIndex].PeerUniqueIdTarget;

                        if (!ClassPeerCheckManager.CheckPeerClientStatus(peerIp, peerUniqueId, false, peerNetworkSetting, peerFirewallSettingObject))
                        {
                            removePeerConnection = true;
                        }
                    }
                    else
                    {
                        removePeerConnection = true;
                    }

                    if (removePeerConnection)
                    {
                        try
                        {
                            if (previousListPeerSelected[peerIndex].PeerNetworkClientSyncObject != null)
                            {
                                previousListPeerSelected[peerIndex].PeerNetworkClientSyncObject.DisconnectFromTarget();
                                previousListPeerSelected[peerIndex].PeerNetworkClientSyncObject.Dispose();
                            }
                        }
                        catch
                        {
                            // Ignored.
                        }
                        previousListPeerSelected.Remove(peerIndex);
                    }
                    else
                    {
                        try
                        {
                            if (previousListPeerSelected[peerIndex].PeerNetworkClientSyncObject != null)
                            {
                                if (!previousListPeerSelected[peerIndex].PeerNetworkClientSyncObject.PeerConnectStatus ||
                                !previousListPeerSelected[peerIndex].PeerNetworkClientSyncObject.PeerPacketReceivedStatus)
                                {
                                    try
                                    {
                                        previousListPeerSelected[peerIndex].PeerNetworkClientSyncObject.DisconnectFromTarget();

                                        previousListPeerSelected[peerIndex].PeerNetworkClientSyncObject.Dispose();
                                    }
                                    catch
                                    {
                                        // Ignored.
                                    }
                                    previousListPeerSelected.Remove(peerIndex);
                                }

                            }
                        }
                        catch
                        {
                            // Ignored.
                        }
                    }

                }
            }

            if (ClassPeerDatabase.DictionaryPeerDataObject.Count > 0)
            {
                Dictionary<string, string> listPublicPeer = new Dictionary<string, string>(); // Peer ip | Peer unique id.

                foreach (string peerIp in ClassPeerDatabase.DictionaryPeerDataObject.Keys.ToArray())
                {
                    cancellation?.Token.ThrowIfCancellationRequested();
                    if (peerIp != peerToExcept && peerIp != peerServerIp && peerIp != peerOpenNatServerIp)
                    {
                        if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp].Count > 0 && peerIp != peerToExcept)
                        {
                            foreach (string peerUniqueId in ClassPeerDatabase.DictionaryPeerDataObject[peerIp].Keys.ToArray())
                            {
                                cancellation?.Token.ThrowIfCancellationRequested();
                                if (!peerUniqueId.IsNullOrEmpty())
                                {
                                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerIsPublic && !ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].OnUpdateAuthKeys)
                                    {
                                        if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerStatus == ClassPeerEnumStatus.PEER_ALIVE)
                                        {
                                            if (ClassPeerCheckManager.CheckPeerClientStatus(peerIp, peerUniqueId, false, peerNetworkSetting, peerFirewallSettingObject))
                                            {
                                                if (!listPublicPeer.ContainsKey(peerIp))
                                                {
                                                    if (previousListPeerSelected.Count(x => x.Value.PeerUniqueIdTarget == peerUniqueId) == 0)
                                                    {
                                                        listPublicPeer.Add(peerIp, peerUniqueId);
                                                    }
                                                }
                                                else
                                                {
                                                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastValidPacket > ClassPeerDatabase.DictionaryPeerDataObject[peerIp][listPublicPeer[peerIp]].PeerLastValidPacket)
                                                    {
                                                        listPublicPeer[peerIp] = peerUniqueId;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (listPublicPeer.Count >= BlockchainSetting.MaxPeerPerSyncTask)
                    {
                        break;
                    }
                }


                int indexPeer = 0;
                if (previousListPeerSelected.Count > 0)
                {
                    foreach (var peerIndex in previousListPeerSelected.Keys)
                    {
                        cancellation?.Token.ThrowIfCancellationRequested();
                        while (newListSelected.ContainsKey(indexPeer))
                        {
                            indexPeer++;
                        }
                        newListSelected.Add(indexPeer, previousListPeerSelected[peerIndex]);
                        indexPeer++;
                    }
                }

                if (listPublicPeer.Count > 0)
                {

                    foreach (var peer in listPublicPeer)
                    {
                        cancellation?.Token.ThrowIfCancellationRequested();
                        while (newListSelected.ContainsKey(indexPeer))
                        {
                            indexPeer++;
                        }
                        newListSelected.Add(indexPeer, new ClassPeerTargetObject()
                        {
                            PeerNetworkClientSyncObject = new ClassPeerNetworkClientSyncObject(peer.Key, ClassPeerDatabase.DictionaryPeerDataObject[peer.Key][peer.Value].PeerPort, peer.Value, cancellation, peerNetworkSetting, peerFirewallSettingObject)
                        });
                        indexPeer++;
                    }
                }
            }

            return newListSelected;
        }

        #endregion

        #region Mining Broadcast.

        /// <summary>
        /// Broadcast mining share accepted to other peers.
        /// </summary>
        /// <param name="peerOpenNatServerIp"></param>
        /// <param name="peerToExcept"></param>
        /// <param name="miningPowShareObject"></param>
        /// <param name="peerServerIp"></param>
        /// <param name="peerNetworkSetting"></param>
        /// <param name="peerFirewallSettingObject"></param>
        public static async Task BroadcastMiningShareAsync(string peerServerIp, string peerOpenNatServerIp, string peerToExcept, ClassMiningPoWaCShareObject miningPowShareObject, ClassPeerNetworkSettingObject peerNetworkSetting, ClassPeerFirewallSettingObject peerFirewallSettingObject)
        {
            CancellationTokenSource cancellation = new CancellationTokenSource();

            foreach (var peerTargetObject in GetRandomListPeerTargetAlive(peerServerIp, peerOpenNatServerIp, peerToExcept, peerListBroadcastMiningShareTarget, peerNetworkSetting, peerFirewallSettingObject, cancellation).Values)
            {
                try
                {
                    await Task.Factory.StartNew(async () =>
                    {

                        ClassPeerPacketSendObject packetSendObject = new ClassPeerPacketSendObject(peerNetworkSetting.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[peerTargetObject.PeerIpTarget][peerTargetObject.PeerUniqueIdTarget].PeerInternPublicKey)
                        {
                            PacketOrder = ClassPeerEnumPacketSend.ASK_MINING_SHARE_VOTE,
                            PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendAskMiningShareVote()
                            {
                                BlockHeight = miningPowShareObject.BlockHeight,
                                MiningPowShareObject = miningPowShareObject,
                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond(),
                            })
                        };

                        packetSendObject = await BuildSignedPeerSendPacketObject(packetSendObject, peerTargetObject.PeerIpTarget, peerTargetObject.PeerUniqueIdTarget, cancellation);

                        if (packetSendObject != null)
                        {
                            await peerTargetObject.PeerNetworkClientSyncObject.TrySendPacketToPeerTarget(JsonConvert.SerializeObject(packetSendObject), cancellation, ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE, false, true);
                        }

                        peerTargetObject.PeerNetworkClientSyncObject.DisconnectFromTarget();
                        peerTargetObject.PeerNetworkClientSyncObject.Dispose();


                    }, TaskCreationOptions.RunContinuationsAsynchronously).ConfigureAwait(false);
                }
                catch
                {
                    // Ignored, catch the exception once the task is cancelled.
                }
            }
        }

        /// <summary>
        /// Ask to other peers validation of the mining pow share.
        /// </summary>
        /// <param name="peerOpenNatServerIp"></param>
        /// <param name="peerToExcept"></param>
        /// <param name="blockHeight"></param>
        /// <param name="miningPowShareObject"></param>
        /// <param name="peerNetworkSetting"></param>
        /// <param name="peerFirewallSettingObject"></param>
        /// <param name="cancellation"></param>
        /// <param name="peerServerIp"></param>
        /// <param name="askOnlyFewAgreements"></param>
        /// <returns></returns>
        public static async Task<Tuple<ClassBlockEnumMiningShareVoteStatus, bool>> AskBlockMiningShareVoteToPeerListsAsync(string peerServerIp, string peerOpenNatServerIp, string peerToExcept, long blockHeight, ClassMiningPoWaCShareObject miningPowShareObject, ClassPeerNetworkSettingObject peerNetworkSetting, ClassPeerFirewallSettingObject peerFirewallSettingObject, CancellationTokenSource cancellation, bool askOnlyFewAgreements)
        {
            ClassBlockEnumMiningShareVoteStatus voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_NOCONSENSUS;

            #region If the block is already unlocked before to start broadcasting and ask votes.

            if (ClassBlockchainDatabase.BlockchainMemoryManagement[blockHeight, cancellation].BlockStatus == ClassBlockEnumStatus.UNLOCKED)
            {

                // That's can happen sometimes when the broadcast of the share to other nodes is very fast and return back the data of the block unlocked to the synced data before to retrieve back every votes done.
                if (ClassMiningPoWaCUtility.ComparePoWaCShare(ClassBlockchainDatabase.BlockchainMemoryManagement[blockHeight, cancellation].BlockMiningPowShareUnlockObject, miningPowShareObject))
                {
                    //ClassLog.WriteLine("Votes from peers ignored, the block seems to be found by the share provided and already available on sync.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Green);
                    voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ACCEPTED;
                }
                else
                {
                    //ClassLog.WriteLine("Votes from peers ignored, the block seems to be found by another share or another miner and already available on sync.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                    voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ALREADY_FOUND;
                }
                return new Tuple<ClassBlockEnumMiningShareVoteStatus, bool>(voteResultStatus, true);
            }

            #endregion

            var peerListTarget = GetRandomListPeerTargetAlive(peerServerIp, peerOpenNatServerIp, peerToExcept, null, peerNetworkSetting, peerFirewallSettingObject, cancellation);


            Dictionary<bool, float> dictionaryMiningShareVoteNormPeer = new Dictionary<bool, float> { { false, 0 }, { true, 0 } };
            Dictionary<bool, float> dictionaryMiningShareVoteSeedPeer = new Dictionary<bool, float> { { false, 0 }, { true, 0 } };
            HashSet<string> listOfRankedPeerPublicKeySaved = new HashSet<string>();

            CancellationTokenSource cancellationTokenSourceMiningShareVote = new CancellationTokenSource();

            long taskTimestampStart = ClassUtility.GetCurrentTimestampInMillisecond();
            int totalTaskDone = 0;
            int totalResponseOk = 0;
            int totalAgree = 0;

            #region Ask mining share votes check result to peers.

            foreach (var peerKey in peerListTarget.Keys)
            {
                try
                {

                    await Task.Factory.StartNew(async () =>
                    {
                        bool invalidPacket = false;
                        string peerIpTarget = peerListTarget[peerKey].PeerIpTarget;
                        string peerUniqueIdTarget = peerListTarget[peerKey].PeerUniqueIdTarget;
                        try
                        {


                                ClassPeerPacketSendObject packetSendObject = new ClassPeerPacketSendObject(peerNetworkSetting.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget][peerUniqueIdTarget].PeerInternPublicKey)
                                {
                                    PacketOrder = ClassPeerEnumPacketSend.ASK_MINING_SHARE_VOTE,
                                    PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendAskMiningShareVote()
                                    {
                                        BlockHeight = miningPowShareObject.BlockHeight,
                                        MiningPowShareObject = miningPowShareObject,
                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond(),
                                    })
                                };

                                packetSendObject = await BuildSignedPeerSendPacketObject(packetSendObject, peerIpTarget, peerUniqueIdTarget, cancellationTokenSourceMiningShareVote);

                                if (packetSendObject != null)
                                {
                                    if (await peerListTarget[peerKey].PeerNetworkClientSyncObject.TrySendPacketToPeerTarget(JsonConvert.SerializeObject(packetSendObject), cancellationTokenSourceMiningShareVote, ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE, false, false))
                                    {
                                        if (peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived != null)
                                        {
                                            if (peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived.PacketOrder == ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE)
                                            {

                                                bool peerIgnorePacketSignature = ClassPeerCheckManager.CheckPeerClientWhitelistStatus(peerIpTarget, peerUniqueIdTarget, peerNetworkSetting);

                                                bool peerPacketSignatureValid = true;
                                                if (!peerIgnorePacketSignature)
                                                {
                                                    peerPacketSignatureValid = ClassWalletUtility.WalletCheckSignature(peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, ClassPeerDatabase.DictionaryPeerDataObject[peerListTarget[peerKey].PeerIpTarget][peerListTarget[peerKey].PeerUniqueIdTarget].PeerClientPublicKey);
                                                }

                                                if (peerPacketSignatureValid)
                                                {
                                                    Tuple<byte[], bool> packetTupleDecrypted = null;
                                                    if (ClassAes.DecryptionProcess(Convert.FromBase64String(peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived.PacketContent), ClassPeerDatabase.DictionaryPeerDataObject[peerListTarget[peerKey].PeerIpTarget][peerListTarget[peerKey].PeerUniqueIdTarget].PeerInternPacketEncryptionKey, ClassPeerDatabase.DictionaryPeerDataObject[peerListTarget[peerKey].PeerIpTarget][peerListTarget[peerKey].PeerUniqueIdTarget].PeerInternPacketEncryptionKeyIv, out byte[] packetDecrypted))
                                                    {
                                                        if (packetDecrypted == null)
                                                        {
                                                            invalidPacket = true;
                                                        }
                                                        else
                                                        {
                                                            packetTupleDecrypted = new Tuple<byte[], bool>(packetDecrypted, true);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        packetTupleDecrypted = await ClassPeerDatabase.DictionaryPeerDataObject[peerListTarget[peerKey].PeerIpTarget][peerListTarget[peerKey].PeerUniqueIdTarget].GetInternCryptoStreamObject.DecryptDataProcess(Convert.FromBase64String(peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived.PacketContent), cancellationTokenSourceMiningShareVote);
                                                    }

                                                    if (packetTupleDecrypted == null)
                                                    {
                                                        invalidPacket = true;
                                                    }
                                                    else
                                                    {
                                                        if (!packetTupleDecrypted.Item2 || packetTupleDecrypted.Item1 == null)
                                                        {
                                                            invalidPacket = true;
                                                        }
                                                    }

                                                    if (!invalidPacket)
                                                    {
                                                        if (ClassUtility.TryDeserialize(packetTupleDecrypted.Item1.GetStringFromByteArrayAscii(), out ClassPeerPacketSendMiningShareVote peerPacketSendMiningShareVote))
                                                        {
                                                            if (peerPacketSendMiningShareVote != null)
                                                            {
                                                                if (ClassUtility.CheckPacketTimestamp(peerPacketSendMiningShareVote.PacketTimestamp, peerNetworkSetting.PeerMaxTimestampDelayPacket, peerNetworkSetting.PeerMaxEarlierPacketDelay))
                                                                {
                                                                    ClassPeerCheckManager.InputPeerClientValidPacket(peerIpTarget, peerUniqueIdTarget);

                                                                    if (peerPacketSendMiningShareVote.BlockHeight == miningPowShareObject.BlockHeight)
                                                                    {
                                                                        bool ignoreVote = false;
                                                                        bool voteStatus = false;
                                                                        switch (peerPacketSendMiningShareVote.VoteStatus)
                                                                        {
                                                                            case ClassPeerPacketMiningShareVoteEnum.ACCEPTED:
                                                                                voteStatus = true;
                                                                                totalAgree++;
                                                                                break;
                                                                            // Already set to false.
                                                                            case ClassPeerPacketMiningShareVoteEnum.REFUSED:
                                                                                break;
                                                                            case ClassPeerPacketMiningShareVoteEnum.NOT_SYNCED:
                                                                                ignoreVote = true;
                                                                                break;
                                                                            default:
                                                                                ignoreVote = true;
                                                                                break;
                                                                        }
                                                                        if (!ignoreVote)
                                                                        {
                                                                            bool peerRanked = false;

                                                                            if (peerNetworkSetting.PeerEnableSovereignPeerVote)
                                                                            {
                                                                                if (ClassPeerCheckManager.PeerHasSeedRank(peerIpTarget, peerUniqueIdTarget, out string numericPublicKeyOut, out _))
                                                                                {
                                                                                    if (!listOfRankedPeerPublicKeySaved.Contains(numericPublicKeyOut))
                                                                                    {
                                                                                        if (ClassPeerCheckManager.CheckPeerSeedNumericPacketSignature(JsonConvert.SerializeObject(new ClassPeerPacketSendMiningShareVote()
                                                                                        {
                                                                                            BlockHeight = peerPacketSendMiningShareVote.BlockHeight,
                                                                                            VoteStatus = peerPacketSendMiningShareVote.VoteStatus,
                                                                                            PacketTimestamp = peerPacketSendMiningShareVote.PacketTimestamp
                                                                                        }),
                                                                                        peerPacketSendMiningShareVote.PacketNumericHash,
                                                                                        peerPacketSendMiningShareVote.PacketNumericSignature,
                                                                                        numericPublicKeyOut,
                                                                                        cancellationTokenSourceMiningShareVote))
                                                                                        {
                                                                                            // Do not allow multiple seed votes from the same numeric public key.
                                                                                            if (!listOfRankedPeerPublicKeySaved.Contains(numericPublicKeyOut))
                                                                                            {
                                                                                                if (listOfRankedPeerPublicKeySaved.Add(numericPublicKeyOut))
                                                                                                {
                                                                                                    peerRanked = true;
                                                                                                }
                                                                                            }
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }


                                                                            switch (voteStatus)
                                                                            {
                                                                                case true:
                                                                                    if (peerRanked)
                                                                                    {
                                                                                        dictionaryMiningShareVoteSeedPeer[true]++;
                                                                                    }
                                                                                    else
                                                                                    {
                                                                                        dictionaryMiningShareVoteNormPeer[true]++;
                                                                                    }
                                                                                    break;
                                                                                case false:
                                                                                    if (peerRanked)
                                                                                    {
                                                                                        dictionaryMiningShareVoteSeedPeer[false]++;
                                                                                    }
                                                                                    else
                                                                                    {
                                                                                        dictionaryMiningShareVoteNormPeer[false]++;
                                                                                    }
                                                                                    break;
                                                                            }

                                                                        }
                                                                        totalResponseOk++;
                                                                    }
                                                                }
                                                            }
                                                            else
                                                            {
                                                                invalidPacket = true;
                                                            }
                                                        }
                                                        else
                                                        {
                                                            invalidPacket = true;
                                                        }
                                                    }
                                                }
                                                else
                                                {
                                                    invalidPacket = true;
                                                }
                                            }
                                            else
                                            {
                                                invalidPacket = true;
                                            }
                                        }
                                    }
                                }

                            peerListTarget[peerKey].PeerNetworkClientSyncObject.DisconnectFromTarget();
                            peerListTarget[peerKey].PeerNetworkClientSyncObject.Dispose();
                        }
                        catch
                        {
                            // Ignored.
                        }

                        totalTaskDone++;

                        if (invalidPacket)
                        {
                            ClassPeerCheckManager.InputPeerClientInvalidPacket(peerIpTarget, peerUniqueIdTarget, peerNetworkSetting, peerFirewallSettingObject);
                        }

                    }, cancellationTokenSourceMiningShareVote.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);

                }
                catch
                {
                    // Ignored catch the exception once the taks is cancelled.
                }
            }

            #endregion

            #region Wait every tasks of votes are done or cancelled.

            while (totalTaskDone < peerListTarget.Count)
            {

                // If the block is already unlocked pending to wait votes from other peers.
                if (ClassBlockchainDatabase.BlockchainMemoryManagement[blockHeight, cancellation].BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                {
                    // That's can happen sometimes when the broadcast of the share to other nodes is very fast and return back the data of the block unlocked to the synced data before to retrieve back every votes done.
                    if (ClassMiningPoWaCUtility.ComparePoWaCShare(ClassBlockchainDatabase.BlockchainMemoryManagement[blockHeight, cancellation].BlockMiningPowShareUnlockObject, miningPowShareObject))
                    {
                        ClassLog.WriteLine("Votes from peers ignored, the block seems to be found by the share provided and already available on sync.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Green);
                        voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ACCEPTED;
                    }
                    else
                    {
                        ClassLog.WriteLine("Votes from peers ignored, the block seems to be found by another share or another miner and already available on sync.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                        voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ALREADY_FOUND;
                    }
                    return new Tuple<ClassBlockEnumMiningShareVoteStatus, bool>(voteResultStatus, true);
                }


                if (totalResponseOk == peerListTarget.Count)
                {
                    break;
                }

                if (askOnlyFewAgreements)
                {
                    if (totalAgree > 0)
                    {
                        break;
                    }
                }

                // Max delay of waiting.
                if (taskTimestampStart + (peerNetworkSetting.PeerMaxDelayConnection * 1000) < ClassUtility.GetCurrentTimestampInMillisecond())
                {
                    // Timeout reach.
                    break;
                }

                try
                {
                    await Task.Delay(100, cancellation.Token);
                }
                catch
                {
                    break;
                }
            }

            #endregion

            #region Cancel the task of votes.

            if (!askOnlyFewAgreements)
            {
                try
                {
                    if (!cancellationTokenSourceMiningShareVote.IsCancellationRequested)
                    {
                        cancellationTokenSourceMiningShareVote.Cancel();
                    }
                }
                catch
                {
                    // Ignored.  
                }
            }

            #endregion

            // Clean up.
            listOfRankedPeerPublicKeySaved.Clear();

            #region Clean up contact peers.

            if (!askOnlyFewAgreements)
            {
                foreach (int peerKey in peerListTarget.Keys)
                {
                    try
                    {
                        peerListTarget[peerKey].PeerNetworkClientSyncObject.DisconnectFromTarget();
                        peerListTarget[peerKey].PeerNetworkClientSyncObject.Dispose();
                    }
                    catch
                    {
                        // Ignored.
                    }
                }

                peerListTarget.Clear();
            }

            #endregion

            // If the block is already unlocked pending to wait votes from other peers.
            if (ClassBlockchainDatabase.BlockchainMemoryManagement[blockHeight, cancellation].BlockStatus == ClassBlockEnumStatus.UNLOCKED)
            {

                // That's can happen sometimes when the broadcast of the share to other nodes is very fast and return back the data of the block unlocked to the synced data before to retrieve back every votes done.
                if (ClassMiningPoWaCUtility.ComparePoWaCShare(ClassBlockchainDatabase.BlockchainMemoryManagement[blockHeight, cancellation].BlockMiningPowShareUnlockObject, miningPowShareObject))
                {
                    ClassLog.WriteLine("Votes from peers ignored, the block seems to be found by the share provided and already available on sync.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Green);
                    voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ACCEPTED;
                }
                else
                {
                    ClassLog.WriteLine("Votes from peers ignored, the block seems to be found by another share or another miner and already available on sync.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                    voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ALREADY_FOUND;
                }
                return new Tuple<ClassBlockEnumMiningShareVoteStatus, bool>(voteResultStatus, true);
            }


            if (!askOnlyFewAgreements)
            {
                #region Check the amount of responses received.

                if (peerNetworkSetting.PeerMinAvailablePeerSync > BlockchainSetting.PeerMinAvailablePeerSync)
                {
                    if (totalResponseOk < peerNetworkSetting.PeerMinAvailablePeerSync)
                    {
                        ClassLog.WriteLine("Error on calculating peer(s) vote(s). Not enough responses received from peer, cancel vote and return no consensus.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                        return new Tuple<ClassBlockEnumMiningShareVoteStatus, bool>(ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_NOCONSENSUS, true);
                    }
                }
                else
                {
                    if (totalResponseOk < BlockchainSetting.PeerMinAvailablePeerSync)
                    {
                        ClassLog.WriteLine("Error on calculating peer(s) vote(s). Not enough responses received from peer, cancel vote and return no consensus.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                        return new Tuple<ClassBlockEnumMiningShareVoteStatus, bool>(ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_NOCONSENSUS, true);
                    }
                }

                #endregion

                #region Calculate votes.

                try
                {

                    float totalSeedVotes = dictionaryMiningShareVoteSeedPeer[false] + dictionaryMiningShareVoteSeedPeer[true];
                    float totalNormVotes = dictionaryMiningShareVoteNormPeer[false] + dictionaryMiningShareVoteNormPeer[true];

                    #region Check the amount of votes received.

                    if (totalSeedVotes + totalNormVotes < peerNetworkSetting.PeerMinAvailablePeerSync)
                    {
                        ClassLog.WriteLine("Error on calculating peer(s) vote(s). Not enough responses received from peer, cancel vote and return no consensus.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                        return new Tuple<ClassBlockEnumMiningShareVoteStatus, bool>(ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_NOCONSENSUS, true);
                    }

                    #endregion

                    float percentSeedAgree = 0;
                    float percentSeedDenied = 0;
                    float seedCountAgree = 0;
                    float seedCountDenied = 0;
                    bool seedVoteResult = false;

                    float percentNormAgree = 0;
                    float percentNormDenied = 0;
                    float normCountAgree = 0;
                    float normCountDenied = 0;
                    bool normVoteResult = false;

                    if (totalSeedVotes > 0)
                    {
                        seedCountAgree = dictionaryMiningShareVoteSeedPeer[true];
                        seedCountDenied = dictionaryMiningShareVoteSeedPeer[false];

                        if (dictionaryMiningShareVoteSeedPeer[true] > 0)
                        {
                            percentSeedAgree = (dictionaryMiningShareVoteSeedPeer[true] / totalSeedVotes) * 100f;
                        }
                        if (dictionaryMiningShareVoteSeedPeer[false] > 0)
                        {
                            percentSeedDenied = (dictionaryMiningShareVoteSeedPeer[false] / totalSeedVotes) * 100f;
                        }

                        seedVoteResult = percentSeedAgree > percentSeedDenied;
                    }

                    if (totalNormVotes > 0)
                    {
                        normCountAgree = dictionaryMiningShareVoteNormPeer[true];
                        normCountDenied = dictionaryMiningShareVoteNormPeer[false];

                        if (dictionaryMiningShareVoteNormPeer[true] > 0)
                        {
                            percentNormAgree = (dictionaryMiningShareVoteNormPeer[true] / totalNormVotes) * 100f;
                        }
                        if (dictionaryMiningShareVoteNormPeer[false] > 0)
                        {
                            percentNormDenied = (dictionaryMiningShareVoteNormPeer[false] / totalNormVotes) * 100f;
                        }

                        normVoteResult = percentNormAgree > percentNormDenied;
                    }

                    // Clean up.
                    dictionaryMiningShareVoteNormPeer.Clear();
                    dictionaryMiningShareVoteSeedPeer.Clear();


                    switch (seedVoteResult)
                    {
                        case true:
                            switch (normVoteResult)
                            {
                                // Both types agreed together.
                                case true:
                                    ClassLog.WriteLine("Mining Share on block height: " + blockHeight + " accepted by seeds and peers. " +
                                                       "Seed Peer Accept: " + percentSeedAgree + "% (A: " + seedCountAgree + "/ D: " + seedCountDenied + ") | " +
                                                       "Normal Peer Accept: " + percentNormAgree + "% (A: " + normCountAgree + "/ D: " + normCountDenied + ")", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Cyan);

                                    voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ACCEPTED;
                                    break;
                                // Compare percent of agreements of seed peers vs percent of denied of normal peers.
                                case false:
                                    if (percentSeedAgree > percentNormDenied)
                                    {
                                        ClassLog.WriteLine("Mining Share on block height: " + blockHeight + " accepted by seeds in majority. " +
                                                           "Seed Peer Accept: " + percentSeedAgree + "% (A: " + seedCountAgree + "/ D: " + seedCountDenied + ") | " +
                                                           "Normal Peer Denied: " + percentNormDenied + "% (A: " + normCountAgree + "/ D: " + normCountDenied + ")", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Cyan);
                                        voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ACCEPTED;
                                    }
                                    else if (percentSeedAgree < percentNormDenied)
                                    {
                                        ClassLog.WriteLine("Mining Share on block height: " + blockHeight + " refused by peers in majority. " +
                                                           "Seed Peer Accept: " + percentSeedAgree + "% (A: " + seedCountAgree + "/ D: " + seedCountDenied + ") | " +
                                                           "Normal Peer Denied: " + percentNormDenied + "% (A: " + normCountAgree + "/ D: " + normCountDenied + ")", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Magenta);
                                        voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_REFUSED;
                                    }
                                    break;
                            }
                            break;
                        case false:
                            switch (normVoteResult)
                            {
                                // Compare percent of agreements of normal peers vs percent of denied of seeds.
                                case true:
                                    if (percentNormAgree > percentSeedDenied)
                                    {
                                        ClassLog.WriteLine("Mining Share on block height: " + blockHeight + "  accepted by peers in majority. " +
                                                           "Seed Peer Denied: " + percentSeedDenied + "% (A: " + seedCountAgree + "/ D: " + seedCountDenied + ") | " +
                                                           "Normal Peer Accept: " + percentNormAgree + "% (A: " + normCountAgree + "/ D: " + normCountDenied + ")", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Cyan);

                                        voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ACCEPTED;
                                    }
                                    else if (percentNormAgree < percentSeedDenied)
                                    {
                                        ClassLog.WriteLine("Mining Share on block height: " + blockHeight + "  refused by seed in majority. " +
                                                           "Seed Peer Denied: " + percentSeedDenied + "% (A: " + seedCountAgree + "/ D: " + seedCountDenied + ") | " +
                                                           "Normal Peer Accept: " + percentNormAgree + "% (A: " + normCountAgree + "/ D: " + normCountDenied + ")", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Magenta);

                                        voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_REFUSED;
                                    }
                                    break;
                                // Both types denied together.
                                case false:
                                    ClassLog.WriteLine("Mining Share on block height: " + blockHeight + " refused by seeds and peers. " +
                                                       "Seed Peer Denied: " + percentSeedDenied + "% (A: " + seedCountAgree + "/ D: " + seedCountDenied + ") | " +
                                                       "Normal Peer Denied: " + percentNormDenied + "% (A: " + normCountAgree + "/ D: " + normCountDenied + ")", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Cyan);

                                    voteResultStatus = ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_REFUSED;
                                    break;
                            }
                            break;
                    }


                }
                catch (Exception error)
                {
                    ClassLog.WriteLine("Error on calculating peer(s) vote(s). Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                }

                #endregion
            }
            else
            {
                if (totalAgree > 0)
                {
                    return new Tuple<ClassBlockEnumMiningShareVoteStatus, bool>(ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ACCEPTED, false);
                }
            }
            // Exception or no consensus found or ignored vote result.
            return new Tuple<ClassBlockEnumMiningShareVoteStatus, bool>(voteResultStatus, false);
        }

        #endregion

        #region MemPool tx's broadcast.

        /// <summary>
        /// Ask other peers if the tx's to push into mem pool is valid.
        /// </summary>
        /// <param name="peerToExcept"></param>
        /// <param name="transactionObject"></param>
        /// <param name="peerNetworkSetting"></param>
        /// <param name="peerFirewallSettingObject"></param>
        /// <param name="cancellation"></param>
        /// <param name="peerServerIp"></param>
        /// <param name="peerOpenNatServerIp"></param>
        /// <param name="onlyBroadcast"></param>
        /// <param name="onlySomeConfirmations"></param>
        /// <returns></returns>
        public static async Task<ClassTransactionEnumStatus> AskMemPoolTxVoteToPeerListsAsync(string peerServerIp, string peerOpenNatServerIp, string peerToExcept, ClassTransactionObject transactionObject, ClassPeerNetworkSettingObject peerNetworkSetting, ClassPeerFirewallSettingObject peerFirewallSettingObject, CancellationTokenSource cancellation, bool onlySomeConfirmations)
        {
            ClassTransactionEnumStatus internalTransactionCheckStatus = ClassTransactionEnumStatus.EMPTY_TRANSACTION;

            var peerListTarget = GetRandomListPeerTargetAlive(peerServerIp, peerOpenNatServerIp, peerToExcept, null, peerNetworkSetting, peerFirewallSettingObject, cancellation);

            Dictionary<bool, float> dictionaryMemPoolTxVoteNormPeer = new Dictionary<bool, float> { { false, 0 }, { true, 0 } };
            Dictionary<bool, float> dictionaryMemPoolTxVoteSeedPeer = new Dictionary<bool, float> { { false, 0 }, { true, 0 } };
            HashSet<string> listOfRankedPeerPublicKeySaved = new HashSet<string>();
            long taskTimestampStart = ClassUtility.GetCurrentTimestampInMillisecond();
            int totalTaskDone = 0;
            int totalResponseOk = 0;
            int totalAgree = 0;

            CancellationTokenSource cancellationTokenSourceMemPoolTxVote = new CancellationTokenSource();

            foreach (var peerKey in peerListTarget.Keys)
            {
                try
                {
                    await Task.Factory.StartNew(async () =>
                    {
                        bool invalidPacket = false;

                        string peerIpTarget = peerListTarget[peerKey].PeerIpTarget;
                        string peerUniqueIdTarget = peerListTarget[peerKey].PeerUniqueIdTarget;

                        try
                        {
                            ClassPeerPacketSendObject packetSendObject = new ClassPeerPacketSendObject(peerNetworkSetting.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget][peerUniqueIdTarget].PeerInternPublicKey)
                            {
                                PacketOrder = ClassPeerEnumPacketSend.ASK_MEM_POOL_TRANSACTION_VOTE,
                                PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendAskMemPoolTransactionVote()
                                {
                                    TransactionObject = transactionObject,
                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond(),
                                })
                            };

                            packetSendObject = await BuildSignedPeerSendPacketObject(packetSendObject, peerIpTarget, peerUniqueIdTarget, cancellationTokenSourceMemPoolTxVote);

                            if (packetSendObject != null)
                            {
                                if (await peerListTarget[peerKey].PeerNetworkClientSyncObject.TrySendPacketToPeerTarget(JsonConvert.SerializeObject(packetSendObject), cancellationTokenSourceMemPoolTxVote, ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_VOTE, false, false))
                                {
                                    if (peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived != null)
                                    {

                                        if (peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived.PacketOrder == ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_VOTE)
                                        {

                                            bool peerIgnorePacketSignature = ClassPeerCheckManager.CheckPeerClientWhitelistStatus(peerIpTarget, peerUniqueIdTarget, peerNetworkSetting);

                                            bool peerPacketSignatureValid = true;
                                            if (!peerIgnorePacketSignature)
                                            {
                                                peerPacketSignatureValid = ClassPeerDatabase.DictionaryPeerDataObject[peerListTarget[peerKey].PeerIpTarget][peerListTarget[peerKey].PeerUniqueIdTarget].GetClientCryptoStreamObject.CheckSignatureProcess(peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived.PacketHash, peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived.PacketSignature, ClassPeerDatabase.DictionaryPeerDataObject[peerListTarget[peerKey].PeerIpTarget][peerListTarget[peerKey].PeerUniqueIdTarget].PeerClientPublicKey);
                                            }

                                            if (peerPacketSignatureValid)
                                            {
                                                Tuple<byte[], bool> packetTupleDecrypted = null;
                                                if (ClassAes.DecryptionProcess(Convert.FromBase64String(peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived.PacketContent), ClassPeerDatabase.DictionaryPeerDataObject[peerListTarget[peerKey].PeerIpTarget][peerListTarget[peerKey].PeerUniqueIdTarget].PeerInternPacketEncryptionKey, ClassPeerDatabase.DictionaryPeerDataObject[peerListTarget[peerKey].PeerIpTarget][peerListTarget[peerKey].PeerUniqueIdTarget].PeerInternPacketEncryptionKeyIv, out byte[] packetDecrypted))
                                                {
                                                    if (packetDecrypted == null)
                                                    {
                                                        invalidPacket = true;
                                                    }
                                                    else
                                                    {
                                                        packetTupleDecrypted = new Tuple<byte[], bool>(packetDecrypted, true);
                                                    }
                                                }
                                                else
                                                {
                                                    packetTupleDecrypted = await ClassPeerDatabase.DictionaryPeerDataObject[peerListTarget[peerKey].PeerIpTarget][peerListTarget[peerKey].PeerUniqueIdTarget].GetInternCryptoStreamObject.DecryptDataProcess(Convert.FromBase64String(peerListTarget[peerKey].PeerNetworkClientSyncObject.PeerPacketReceived.PacketContent), cancellationTokenSourceMemPoolTxVote);
                                                }

                                                if (packetTupleDecrypted == null)
                                                {
                                                    invalidPacket = true;
                                                }
                                                else
                                                {
                                                    if (!packetTupleDecrypted.Item2 || packetTupleDecrypted.Item1 == null)
                                                    {
                                                        invalidPacket = true;
                                                    }
                                                }

                                                if (!invalidPacket)
                                                {
                                                    if (ClassUtility.TryDeserialize(packetTupleDecrypted.Item1.GetStringFromByteArrayAscii(), out ClassPeerPacketSendMemPoolTransactionVote packetSendMemPoolTransactionVote))
                                                    {
                                                        if (packetSendMemPoolTransactionVote != null)
                                                        {
                                                            if (ClassUtility.CheckPacketTimestamp(packetSendMemPoolTransactionVote.PacketTimestamp, peerNetworkSetting.PeerMaxTimestampDelayPacket, peerNetworkSetting.PeerMaxEarlierPacketDelay))
                                                            {
                                                                if (packetSendMemPoolTransactionVote.TransactionHash == transactionObject.TransactionHash)
                                                                {
                                                                    ClassPeerCheckManager.InputPeerClientValidPacket(peerIpTarget, peerUniqueIdTarget);

                                                                    bool peerRanked = false;

                                                                    if (peerNetworkSetting.PeerEnableSovereignPeerVote)
                                                                    {
                                                                        if (ClassPeerCheckManager.PeerHasSeedRank(peerIpTarget, peerUniqueIdTarget, out string numericPublicKeyOut, out _))
                                                                        {
                                                                            if (!listOfRankedPeerPublicKeySaved.Contains(numericPublicKeyOut))
                                                                            {
                                                                                if (ClassPeerCheckManager.CheckPeerSeedNumericPacketSignature(JsonConvert.SerializeObject(new ClassPeerPacketSendMemPoolTransactionVote()
                                                                                {
                                                                                    TransactionHash = packetSendMemPoolTransactionVote.TransactionHash,
                                                                                    TransactionStatus = packetSendMemPoolTransactionVote.TransactionStatus,
                                                                                    PacketTimestamp = packetSendMemPoolTransactionVote.PacketTimestamp
                                                                                }),
                                                                                packetSendMemPoolTransactionVote.PacketNumericHash,
                                                                                packetSendMemPoolTransactionVote.PacketNumericSignature,
                                                                                numericPublicKeyOut,
                                                                                cancellationTokenSourceMemPoolTxVote))
                                                                                {
                                                                                    // Do not allow multiple seed votes from the same numeric public key.
                                                                                    if (!listOfRankedPeerPublicKeySaved.Contains(numericPublicKeyOut))
                                                                                    {
                                                                                        if (listOfRankedPeerPublicKeySaved.Add(numericPublicKeyOut))
                                                                                        {
                                                                                            peerRanked = true;
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }

                                                                    switch (packetSendMemPoolTransactionVote.TransactionStatus)
                                                                    {
                                                                        case ClassTransactionEnumStatus.VALID_TRANSACTION:
                                                                            {
                                                                                totalAgree++;
                                                                                if (peerRanked)
                                                                                {
                                                                                    dictionaryMemPoolTxVoteSeedPeer[true]++;
                                                                                }
                                                                                else
                                                                                {
                                                                                    dictionaryMemPoolTxVoteNormPeer[true]++;
                                                                                }
                                                                            }
                                                                            break;
                                                                        default:
                                                                            {
                                                                                if (peerRanked)
                                                                                {
                                                                                    dictionaryMemPoolTxVoteSeedPeer[false]++;
                                                                                }
                                                                                else
                                                                                {
                                                                                    dictionaryMemPoolTxVoteNormPeer[false]++;
                                                                                }
                                                                            }
                                                                            break;
                                                                    }

                                                                    totalResponseOk++;
                                                                }
                                                                else
                                                                {
                                                                    invalidPacket = true;
                                                                }
                                                            }
                                                            else
                                                            {
                                                                invalidPacket = true;
                                                            }
                                                        }
                                                        else
                                                        {
                                                            invalidPacket = true;
                                                        }
                                                    }
                                                    else
                                                    {
                                                        invalidPacket = true;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                invalidPacket = true;
                                            }
                                        }
                                        else
                                        {
                                            invalidPacket = true;
                                        }
                                    }
                                }
                            }

                        }
                        catch
                        {
                                // Ignored.
                            }

                        if (invalidPacket)
                        {
                            ClassPeerCheckManager.InputPeerClientInvalidPacket(peerIpTarget, peerUniqueIdTarget, peerNetworkSetting, peerFirewallSettingObject);
                        }

                        totalTaskDone++;

                    }, cancellationTokenSourceMemPoolTxVote.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);

                }
                catch
                {
                    // Ignored, catch the exception once the task is cancelled.
                }
            }

            while (totalTaskDone < peerListTarget.Count)
            {
                if (totalResponseOk >= peerListTarget.Count)
                {
                    break;
                }

                if (taskTimestampStart + (peerNetworkSetting.PeerMaxDelayAwaitResponse * 1000) < ClassUtility.GetCurrentTimestampInMillisecond())
                {
                    // Timeout reach.
                    break;
                }

                if (onlySomeConfirmations)
                {
                    if (totalAgree >= 1)
                    {
                        break;
                    }
                }

                try
                {
                    await Task.Delay(100, cancellationTokenSourceMemPoolTxVote.Token);
                }
                catch
                {
                    break;
                }
            }

            if (!onlySomeConfirmations)
            {
                try
                {
                    if (!cancellationTokenSourceMemPoolTxVote.IsCancellationRequested)
                    {
                        cancellationTokenSourceMemPoolTxVote.Cancel();
                        cancellationTokenSourceMemPoolTxVote.Dispose();
                    }
                }
                catch
                {
                    // Ignored, cancelling tasks.
                }

                #region Clean up contact peers.

                foreach (int peerKey in peerListTarget.Keys)
                {
                    try
                    {
                        peerListTarget[peerKey].PeerNetworkClientSyncObject.Dispose();
                    }
                    catch
                    {
                        // Ignored.
                    }
                }


                #endregion

                #region Calculate votes.


                float totalSeedVotes = dictionaryMemPoolTxVoteSeedPeer[false] + dictionaryMemPoolTxVoteSeedPeer[true];
                float percentSeedAgree = 0;
                float percentSeedDenied = 0;
                bool seedVoteResult = false;

                float totalNormVotes = dictionaryMemPoolTxVoteNormPeer[false] + dictionaryMemPoolTxVoteNormPeer[true];
                float percentNormAgree = 0;
                float percentNormDenied = 0;
                bool normVoteResult = false;

                if (totalSeedVotes > 0)
                {
                    if (dictionaryMemPoolTxVoteSeedPeer[true] > 0)
                    {
                        percentSeedAgree = (dictionaryMemPoolTxVoteSeedPeer[true] / totalSeedVotes) * 100f;
                    }
                    if (dictionaryMemPoolTxVoteSeedPeer[false] > 0)
                    {
                        percentSeedDenied = (dictionaryMemPoolTxVoteSeedPeer[false] / totalSeedVotes) * 100f;
                    }

                    seedVoteResult = percentSeedAgree > percentSeedDenied;
                }

                if (totalNormVotes > 0)
                {
                    if (dictionaryMemPoolTxVoteNormPeer[true] > 0)
                    {
                        percentNormAgree = (dictionaryMemPoolTxVoteNormPeer[true] / totalNormVotes) * 100f;
                    }
                    if (dictionaryMemPoolTxVoteNormPeer[false] > 0)
                    {
                        percentNormDenied = (dictionaryMemPoolTxVoteNormPeer[false] / totalNormVotes) * 100f;
                    }

                    normVoteResult = percentNormAgree > percentNormDenied;
                }


                // Clean up.
                dictionaryMemPoolTxVoteNormPeer.Clear();
                dictionaryMemPoolTxVoteSeedPeer.Clear();
                listOfRankedPeerPublicKeySaved.Clear();

                switch (seedVoteResult)
                {
                    case true:
                        {
                            // Total equality.
                            if (normVoteResult)
                            {
                                return ClassTransactionEnumStatus.VALID_TRANSACTION;
                            }

                            if (percentSeedAgree > percentNormDenied)
                            {
                                return ClassTransactionEnumStatus.VALID_TRANSACTION;
                            }

                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_FROM_VOTE;
                        }
                    case false:
                        {
                            // Total equality.
                            if (!normVoteResult)
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_FROM_VOTE;
                            }

                            if (percentNormAgree > percentSeedDenied)
                            {
                                return ClassTransactionEnumStatus.VALID_TRANSACTION;
                            }
                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_FROM_VOTE;
                        }
                }


                #endregion


            }

            if (onlySomeConfirmations)
            {
                if (totalAgree >= 1)
                {
                    return ClassTransactionEnumStatus.VALID_TRANSACTION;
                }
            }

            return internalTransactionCheckStatus;

        }

        #endregion

        #region Static peer packet signing/encryption function.

        /// <summary>
        /// Build the packet content encrypted with peer auth keys and the internal private key assigned to the peer target for sign the packet.
        /// </summary>
        /// <param name="sendObject"></param>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="cancellation"></param>
        /// 
        /// <returns></returns>
        public static async Task<ClassPeerPacketSendObject> BuildSignedPeerSendPacketObject(ClassPeerPacketSendObject sendObject, string peerIp, string peerUniqueId, CancellationTokenSource cancellation)
        {
            if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp].ContainsKey(peerUniqueId))
            {
                byte[] packetContentEncrypted;

                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].GetInternCryptoStreamObject != null)
                {
                    if (cancellation == null)
                    {
                        if (!ClassAes.EncryptionProcess(ClassUtility.GetByteArrayFromStringAscii(sendObject.PacketContent), ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPacketEncryptionKey, ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPacketEncryptionKeyIv, out packetContentEncrypted))
                        {
                            return null;
                        }
                    }
                    else
                    {
                        packetContentEncrypted = await ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].GetInternCryptoStreamObject.EncryptDataProcess(ClassUtility.GetByteArrayFromStringAscii(sendObject.PacketContent), cancellation);

                        if (packetContentEncrypted == null)
                        {
                            if (!ClassAes.EncryptionProcess(ClassUtility.GetByteArrayFromStringAscii(sendObject.PacketContent), ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPacketEncryptionKey, ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPacketEncryptionKeyIv, out packetContentEncrypted))
                            {
                                return null;
                            }
                        }
                    }
                }
                else
                {
                    if (!ClassAes.EncryptionProcess(ClassUtility.GetByteArrayFromStringAscii(sendObject.PacketContent), ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPacketEncryptionKey, ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPacketEncryptionKeyIv, out packetContentEncrypted))
                    {
                        return null;
                    }
                }


                sendObject.PacketContent = Convert.ToBase64String(packetContentEncrypted);
                sendObject.PacketHash = ClassUtility.GenerateSha3512FromString(sendObject.PacketContent + sendObject.PacketOrder);
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp].ContainsKey(peerUniqueId))
                {
                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].GetClientCryptoStreamObject != null && cancellation != null)
                    {
                        sendObject.PacketSignature = ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].GetClientCryptoStreamObject.DoSignatureProcess(sendObject.PacketHash, ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPrivateKey);
                    }
                    else
                    {
                        sendObject.PacketSignature = ClassWalletUtility.WalletGenerateSignature(ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPrivateKey, sendObject.PacketHash);
                    }
                }
            }
            return sendObject;
        }

        #endregion

        #region Check Peer numeric keys signatures on packets

        /// <summary>
        /// Check the peer seed numeric packet signature, compare with the list of peer listed by sovereign updates.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="objectData"></param>
        /// <param name="packetNumericHash"></param>
        /// <param name="packetNumericSignature"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="cancellation"></param>
        /// <param name="numericPublicKeyOut"></param>
        /// <returns></returns>
        public static bool CheckPeerSeedNumericPacketSignature<T>(string peerIp, string peerUniqueId, T objectData, string packetNumericHash, string packetNumericSignature, ClassPeerNetworkSettingObject peerNetworkSettingObject, CancellationTokenSource cancellation, out string numericPublicKeyOut)
        {
            // Default value.
            numericPublicKeyOut = string.Empty;

            if (peerNetworkSettingObject.PeerEnableSovereignPeerVote)
            {
                if (!packetNumericHash.IsNullOrEmpty() && !packetNumericSignature.IsNullOrEmpty())
                {
                    if (ClassPeerCheckManager.PeerHasSeedRank(peerIp, peerUniqueId, out numericPublicKeyOut, out _))
                    {
                        if (ClassPeerCheckManager.CheckPeerSeedNumericPacketSignature(JsonConvert.SerializeObject(objectData), packetNumericHash, packetNumericSignature, numericPublicKeyOut, cancellation))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        #endregion
    }
}

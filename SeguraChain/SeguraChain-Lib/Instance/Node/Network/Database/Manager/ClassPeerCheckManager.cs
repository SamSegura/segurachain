using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SeguraChain_Lib.Algorithm;
using SeguraChain_Lib.Blockchain.Sovereign.Database;
using SeguraChain_Lib.Blockchain.Wallet.Function;
using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Status;
using SeguraChain_Lib.Instance.Node.Network.Services.Firewall.Manager;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Instance.Node.Network.Database.Manager
{
    /// <summary>
    /// A static class for check peer tcp connection server or client.
    /// </summary>
    public class ClassPeerCheckManager
    {
        #region Check peer state.

        /// <summary>
        /// Check whole peer status, remove dead peers.
        /// </summary>
        /// <param name="cancellation"></param>
        /// <param name="peerNetworkSetting"></param>
        /// <returns></returns>
        public static void CheckWholePeerStatus(CancellationTokenSource cancellation, ClassPeerNetworkSettingObject peerNetworkSetting)
        {
            if (ClassPeerDatabase.DictionaryPeerDataObject.Count > 0)
            {
                long currentTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                foreach (string peerIp in ClassPeerDatabase.DictionaryPeerDataObject.Keys.ToArray())
                {
                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp].Count > 0)
                    {
                        foreach (string peerUniqueId in ClassPeerDatabase.DictionaryPeerDataObject[peerIp].Keys.ToArray())
                        {
                            if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerIsPublic)
                            {
                                if (!CheckPeerClientStatus(peerIp, peerUniqueId, false, peerNetworkSetting, out _))
                                {
                                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastPacketReceivedTimestamp + peerNetworkSetting.PeerDelayDeleteDeadPeer <= currentTimestamp &&
                                        ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastValidPacket + peerNetworkSetting.PeerMaxDelayKeepAliveStats <= currentTimestamp &&
                                        (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalInvalidPacket >= peerNetworkSetting.PeerMaxInvalidPacket ||
                                        ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalAttemptConnection >= peerNetworkSetting.PeerMaxAttemptConnection))
                                    {
                                        long deadTime = currentTimestamp - ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastPacketReceivedTimestamp;

                                        if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
                                        {
                                            if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp].TryRemove(peerUniqueId, out _))
                                            {
#if DEBUG
                                                Debug.WriteLine("Peer IP: " + peerIp + " | Peer Unique ID: " + peerUniqueId + " removed from the listing of peers, this one is dead since " + deadTime + " second(s).");
#endif
                                                ClassLog.WriteLine("Peer IP: " + peerIp + " | Peer Unique ID: " + peerUniqueId + " removed from the listing of peers, this one is dead since " + deadTime + " second(s).", ClassEnumLogLevelType.LOG_LEVEL_PEER_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Check if the peer client status is good. (Invalid packets amount, ban delay and more).
        /// </summary>
        /// <returns></returns>
        public static bool CheckPeerClientStatus(string peerIp, string peerUniqueId, bool isIncomingConnection, ClassPeerNetworkSettingObject peerNetworkSettingObject, out bool newPeer)
        {
            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                newPeer = false;
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerStatus == ClassPeerEnumStatus.PEER_BANNED || ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerStatus == ClassPeerEnumStatus.PEER_DEAD)
                {

                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerStatus == ClassPeerEnumStatus.PEER_BANNED)
                    {
                        if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerBanDate + peerNetworkSettingObject.PeerBanDelay >= ClassUtility.GetCurrentTimestampInSecond())
                        {
                            return false;
                        }
                        CleanPeerState(peerIp, peerUniqueId, true);
                    }
                    else if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerStatus == ClassPeerEnumStatus.PEER_DEAD)
                    {
                        if (!isIncomingConnection)
                        {
                            if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastDeadTimestamp + peerNetworkSettingObject.PeerDeadDelay >= ClassUtility.GetCurrentTimestampInSecond())
                            {
                                return false;
                            }
                            CleanPeerState(peerIp, peerUniqueId, true);
                        }
                    }
                }
                else
                {
                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastPacketReceivedTimestamp + peerNetworkSettingObject.PeerDelayDeleteDeadPeer <= ClassUtility.GetCurrentTimestampInSecond())
                    {
                        return false;
                    }
                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastValidPacket + peerNetworkSettingObject.PeerMaxDelayKeepAliveStats <= ClassUtility.GetCurrentTimestampInSecond())
                    {
                        CleanPeerState(peerIp, peerUniqueId, false);
                    }
                }
            }
            else
            {
                newPeer = true;
            }

            return true;
        }

        /// <summary>
        /// Check if the peer client status is enough fine for not check packet signature.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <returns></returns>
        public static bool CheckPeerClientWhitelistStatus(string peerIp, string peerUniqueId, ClassPeerNetworkSettingObject peerNetworkSettingObject)
        {
            if (peerUniqueId.IsNullOrEmpty())
            {
                peerUniqueId = string.Empty;
            }
            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastValidPacket + peerNetworkSettingObject.PeerMaxDelayKeepAliveStats >= ClassUtility.GetCurrentTimestampInSecond())
                {
                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalValidPacket >= peerNetworkSettingObject.PeerMinValidPacket)
                    {
                        ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalPassedPeerPacketSignature++;
                        if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalPassedPeerPacketSignature >= peerNetworkSettingObject.PeerMaxWhiteListPacket)
                        {
                            ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalValidPacket = 0;
                        }
                        return true;
                    }
                }
                else
                {
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalPassedPeerPacketSignature = 0;
                }
            }

            return false;
        }

        /// <summary>
        /// Check if the peer client has been initialized.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <returns></returns>
        public static bool CheckPeerClientInitializationStatus(string peerIp, string peerUniqueId)
        {
            if (ClassPeerDatabase.DictionaryPeerDataObject.ContainsKey(peerIp))
            {
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp].ContainsKey(peerUniqueId))
                {
                    if (!ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerIp.IsNullOrEmpty())
                    {
                        if (!ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerUniqueId.IsNullOrEmpty())
                        {
                            if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerPort != 0)
                            {
                                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPacketEncryptionKey != null)
                                {
                                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPacketEncryptionKeyIv != null)
                                    {
                                        if (!ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPublicKey.IsNullOrEmpty())
                                        {
                                            if (!ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerInternPrivateKey.IsNullOrEmpty())
                                            {
                                                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientPacketEncryptionKey != null)
                                                {
                                                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientPacketEncryptionKeyIv != null)
                                                    {
                                                        if (!ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientPublicKey.IsNullOrEmpty())
                                                        {
                                                            return true;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return false;
        }

        #endregion

        #region Update peer stats.

        /// <summary>
        /// Input invalid packet to a peer.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="peerFirewallSettingObject"></param>
        public static void InputPeerClientInvalidPacket(string peerIp, string peerUniqueId, ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject peerFirewallSettingObject)
        {
            bool exist = false;
            long currentTimestamp = ClassUtility.GetCurrentTimestampInSecond();
            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                exist = true;
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastBadStatePacket + peerNetworkSettingObject.PeerMaxDelayKeepAliveStats <= currentTimestamp)
                {
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalInvalidPacket = 0;
                }
                UpdatePeerLastBadStatePacket(peerIp, peerUniqueId, peerFirewallSettingObject);
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalInvalidPacket++;

                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalInvalidPacket >= peerNetworkSettingObject.PeerMaxInvalidPacket)
                {
                    SetPeerBanState(peerIp, peerUniqueId, peerNetworkSettingObject, peerFirewallSettingObject);
                }
            }
            if (!exist)
            {
                if (peerFirewallSettingObject.PeerEnableFirewallLink)
                {
                    ClassPeerFirewallManager.InsertApiInvalidPacket(peerIp);
                }
            }
        }

        /// <summary>
        /// Input the amount of no packet provided per connection opened.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="peerFirewallSettingObject"></param>
        public static void InputPeerClientNoPacketConnectionOpened(string peerIp, string peerUniqueId, ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject peerFirewallSettingObject)
        {

            bool exist = false;
            long currentTimestamp = ClassUtility.GetCurrentTimestampInSecond();


            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                exist = true;
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastBadStatePacket + peerNetworkSettingObject.PeerMaxDelayKeepAliveStats <= currentTimestamp)
                {
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalNoPacketConnectionAttempt = 0;
                }

                UpdatePeerLastBadStatePacket(peerIp, peerUniqueId, peerFirewallSettingObject);

                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalNoPacketConnectionAttempt++;
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalNoPacketConnectionAttempt >= peerNetworkSettingObject.PeerMaxNoPacketPerConnectionOpened)
                {
                    SetPeerDeadState(peerIp, peerUniqueId, peerNetworkSettingObject, peerFirewallSettingObject);
                }

            }

            if (!exist)
            {
                if (peerFirewallSettingObject.PeerEnableFirewallLink)
                {
                    ClassPeerFirewallManager.InsertApiInvalidPacket(peerIp);
                }
            }
        }

        /// <summary>
        /// Increment attempt to connect to a peer.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="peerFirewallSettingObject"></param>
        public static void InputPeerClientAttemptConnect(string peerIp, string peerUniqueId, ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject peerFirewallSettingObject)
        {
            bool exist = false;
            long currentTimestamp = ClassUtility.GetCurrentTimestampInSecond();

            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                exist = true;
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastBadStatePacket + peerNetworkSettingObject.PeerMaxDelayKeepAliveStats <= currentTimestamp)
                {
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalAttemptConnection = 0;
                }

                UpdatePeerLastBadStatePacket(peerIp, peerUniqueId, peerFirewallSettingObject);

                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalAttemptConnection++;
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalAttemptConnection >= peerNetworkSettingObject.PeerMaxAttemptConnection)
                {
                    SetPeerDeadState(peerIp, peerUniqueId, peerNetworkSettingObject, peerFirewallSettingObject);
                }

            }
            if (!exist)
            {
                if (peerFirewallSettingObject.PeerEnableFirewallLink)
                {
                    ClassPeerFirewallManager.InsertApiInvalidPacket(peerIp);
                }
            }
        }

        /// <summary>
        /// Increment valid packet counter to a peer.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        public static void InputPeerClientValidPacket(string peerIp, string peerUniqueId)
        {
            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastValidPacket = ClassUtility.GetCurrentTimestampInSecond();
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalValidPacket++;
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalNoPacketConnectionAttempt = 0;
            }
        }

        /// <summary>
        /// Update the peer last packet received
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        public static void UpdatePeerClientLastPacketReceived(string peerIp, string peerUniqueId)
        {
            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastPacketReceivedTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalNoPacketConnectionAttempt = 0;
            }
        }

        #endregion

        #region Manage peer states.

        /// <summary>
        /// CloseDiskCache peer state.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="unBanOrUnDead"></param>
        public static void CleanPeerState(string peerIp, string peerUniqueId, bool unBanOrUnDead)
        {
            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerStatus = ClassPeerEnumStatus.PEER_ALIVE;
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalAttemptConnection = 0;
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalInvalidPacket = 0;
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastBadStatePacket = 0;
                if (unBanOrUnDead)
                {
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerBanDate = 0;
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastDeadTimestamp = 0;
                }
                if (!unBanOrUnDead)
                {
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalPassedPeerPacketSignature = 0;
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalNoPacketConnectionAttempt = 0;
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalAttemptConnection = 0;
                }
                ClassLog.WriteLine("Peer: " + peerIp + " | Unique ID: " + peerUniqueId + " state has been cleaned.", ClassEnumLogLevelType.LOG_LEVEL_PEER_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY, false, ConsoleColor.Magenta);
            }
        }

        /// <summary>
        /// Ban a peer.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="peerFirewallSettingObject"></param>
        public static void SetPeerBanState(string peerIp, string peerUniqueId, ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject peerFirewallSettingObject)
        {
            bool exist = false;
            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                exist = true;
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerStatus != ClassPeerEnumStatus.PEER_BANNED)
                {
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerStatus = ClassPeerEnumStatus.PEER_BANNED;
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalInvalidPacket = peerNetworkSettingObject.PeerMaxInvalidPacket;
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerBanDate = ClassUtility.GetCurrentTimestampInSecond();
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalValidPacket = 0;
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalPassedPeerPacketSignature = 0;

                    ClassLog.WriteLine("Peer: " + peerIp + " | Unique ID: " + peerUniqueId + " state has been set to banned temporaly.", ClassEnumLogLevelType.LOG_LEVEL_PEER_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                }
            }
            if (!exist)
            {
                if (peerFirewallSettingObject.PeerEnableFirewallLink)
                {
                    ClassPeerFirewallManager.InsertApiInvalidPacket(peerIp);
                }
            }


        }

        /// <summary>
        /// Set a peer dead.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="peerFirewallSettingObject"></param>
        public static void SetPeerDeadState(string peerIp, string peerUniqueId, ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject peerFirewallSettingObject)
        {

            bool exist = false;
            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                exist = true;
                if (ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerStatus != ClassPeerEnumStatus.PEER_DEAD)
                {
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerStatus = ClassPeerEnumStatus.PEER_DEAD;
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalAttemptConnection = 0;
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerTotalNoPacketConnectionAttempt = 0;
                    ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastDeadTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                    ClassLog.WriteLine("Peer: " + peerIp + " | Unique ID: " + peerUniqueId + " state has been set to dead temporaly.", ClassEnumLogLevelType.LOG_LEVEL_PEER_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                }

            }
            if (!exist)
            {
                if (peerFirewallSettingObject.PeerEnableFirewallLink)
                {
                    ClassPeerFirewallManager.InsertApiInvalidPacket(peerIp);
                }
            }
        }

        /// <summary>
        /// Check if the peer have the Seed Rank.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="numericPublicKeyOut"></param>
        /// <param name="timestampRankDelay"></param>
        /// <returns></returns>
        public static bool PeerHasSeedRank(string peerIp, string peerUniqueId, out string numericPublicKeyOut, out long timestampRankDelay)
        {
            if (ClassPeerDatabase.GetPeerNumericPublicKey(peerIp, peerUniqueId, out numericPublicKeyOut))
            {
                if (!numericPublicKeyOut.IsNullOrEmpty())
                {
                    if (ClassSovereignUpdateDatabase.CheckIfNumericPublicKeyPeerIsRanked(numericPublicKeyOut, out timestampRankDelay))
                    {
                        return true;
                    }
                }
            }

            timestampRankDelay = 0;
            return false;
        }

        /// <summary>
        /// Check the peer packet numeric signature sent from a peer with the seed rank.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="packetNumericHash"></param>
        /// <param name="packetNumericSignature"></param>
        /// <param name="peerNumericPublicKey"></param>
        /// <returns></returns>
        public static bool CheckPeerSeedNumericPacketSignature(string data, string packetNumericHash, string packetNumericSignature, string peerNumericPublicKey, CancellationTokenSource cancellation)
        {
            if (!packetNumericHash.IsNullOrEmpty() && !packetNumericSignature.IsNullOrEmpty() && !peerNumericPublicKey.IsNullOrEmpty())
            {
                if (ClassSha.MakeBigShaHashFromBigData(ClassUtility.GetByteArrayFromStringAscii(data), cancellation) == packetNumericHash)
                {
                    if (ClassWalletUtility.WalletCheckSignature(packetNumericHash, packetNumericSignature, peerNumericPublicKey))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion

        #region Update peer states timestamp.

        /// <summary>
        /// Update last timestamp of packet received.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="peerFirewallSettingObject"></param>
        /// 
        public static void UpdatePeerLastBadStatePacket(string peerIp, string peerUniqueId, ClassPeerFirewallSettingObject peerFirewallSettingObject)
        {
            bool exist = false;
            if (ClassPeerDatabase.ContainsPeer(peerIp, peerUniqueId))
            {
                exist = true;
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerLastBadStatePacket = ClassUtility.GetCurrentTimestampInSecond();
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalValidPacket = 0;
                ClassPeerDatabase.DictionaryPeerDataObject[peerIp][peerUniqueId].PeerClientTotalPassedPeerPacketSignature = 0;

            }

            if (!exist)
            {
                if (peerFirewallSettingObject.PeerEnableFirewallLink)
                {
                    ClassPeerFirewallManager.InsertApiInvalidPacket(peerIp);
                }
            }
        }

        #endregion
    }
}

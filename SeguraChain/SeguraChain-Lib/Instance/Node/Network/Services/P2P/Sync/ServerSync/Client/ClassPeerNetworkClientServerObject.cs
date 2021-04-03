using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SeguraChain_Lib.Algorithm;
using SeguraChain_Lib.Blockchain.Block.Enum;
using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using SeguraChain_Lib.Blockchain.Database;
using SeguraChain_Lib.Blockchain.MemPool.Database;
using SeguraChain_Lib.Blockchain.Mining.Enum;
using SeguraChain_Lib.Blockchain.Mining.Function;
using SeguraChain_Lib.Blockchain.Setting;
using SeguraChain_Lib.Blockchain.Sovereign.Database;
using SeguraChain_Lib.Blockchain.Stats.Function;
using SeguraChain_Lib.Blockchain.Transaction.Enum;
using SeguraChain_Lib.Blockchain.Transaction.Object;
using SeguraChain_Lib.Blockchain.Transaction.Utility;
using SeguraChain_Lib.Blockchain.Wallet.Function;
using SeguraChain_Lib.Instance.Node.Network.Database;
using SeguraChain_Lib.Instance.Node.Network.Database.Manager;
using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Packet;
using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Status;
using SeguraChain_Lib.Instance.Node.Network.Services.Firewall.Manager;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Broadcast;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.Model;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Request;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Response;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Response.Enum;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ServerSync.Client.Enum;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Other.Object.List;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ServerSync.Client
{
    /// <summary>
    /// Object dedicated to peer client tcp received on listening.
    /// </summary>
    public class ClassPeerNetworkClientServerObject : IDisposable
    {
        private TcpClient _tcpClientPeer;
        public CancellationTokenSource CancellationTokenHandlePeerConnection;
        private CancellationTokenSource _cancellationTokenClientCheckConnectionPeer;
        private CancellationTokenSource _cancellationTokenAccessData;
        private CancellationTokenSource _cancellationTokenListenPeerPacket;

        private ClassPeerNetworkSettingObject _peerNetworkSettingObject;
        private ClassPeerFirewallSettingObject _peerFirewallSettingObject;
        private string _peerClientIp;
        private string _peerServerOpenNatIp;
        private string _peerUniqueId;


        /// <summary>
        /// Network status and data.
        /// </summary>
        public bool ClientPeerConnectionStatus;
        public long ClientPeerLastPacketReceived;
        private bool _clientPeerPacketReceivedStatus;
        private bool _clientAskDisconnection;
        private bool _onSendingPacketResponse;
        private bool _clientPeerClosed;
        private List<ClassReadPacketSplitted> _listPacketReceived;

        /// <summary>
        /// About MemPool broadcast mode.
        /// </summary>
        private bool _enableMemPoolBroadcastClientMode;
        private bool _onSendingMemPoolTransaction;
        private bool _onWaitingMemPoolTransactionConfirmationReceived;
        private Dictionary<long, int> _listMemPoolBroadcastBlockHeight;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="tcpClientPeer">The tcp client object.</param>
        /// <param name="cancellationTokenHandlePeerConnection">The cancellation token who permit to cancel the handling of the incoming connection.</param>
        /// <param name="peerClientIp">The peer client IP.</param>
        /// <param name="peerServerOpenNatIp">The public ip of the server.</param>
        /// <param name="peerNetworkSettingObject"></param>
        /// <param name="peerFirewallSettingObject"></param>
        public ClassPeerNetworkClientServerObject(TcpClient tcpClientPeer, CancellationTokenSource cancellationTokenHandlePeerConnection, string peerClientIp, string peerServerOpenNatIp, ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject peerFirewallSettingObject)
        {
            ClientPeerConnectionStatus = true;
            ClientPeerLastPacketReceived = ClassUtility.GetCurrentTimestampInSecond();
            CancellationTokenHandlePeerConnection = cancellationTokenHandlePeerConnection;
            _tcpClientPeer = tcpClientPeer;
            _peerNetworkSettingObject = peerNetworkSettingObject;
            _peerFirewallSettingObject = peerFirewallSettingObject;
            _peerClientIp = peerClientIp;
            _peerServerOpenNatIp = peerServerOpenNatIp;
            _clientPeerPacketReceivedStatus = false;
            _cancellationTokenClientCheckConnectionPeer = CancellationTokenSource.CreateLinkedTokenSource(CancellationTokenHandlePeerConnection.Token);
            _cancellationTokenListenPeerPacket = CancellationTokenSource.CreateLinkedTokenSource(CancellationTokenHandlePeerConnection.Token);
            _cancellationTokenAccessData = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenListenPeerPacket.Token, _cancellationTokenClientCheckConnectionPeer.Token);
            _listPacketReceived = new List<ClassReadPacketSplitted>();
            _listMemPoolBroadcastBlockHeight = new Dictionary<long, int>();
        }

        #region Dispose functions

        private bool _disposed;

        ~ClassPeerNetworkClientServerObject()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        // Protected implementation of Dispose pattern.
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                ClosePeerClient(false);
            }
            _disposed = true;
        }
        #endregion

        #region Manage Peer Client connection.


        /// <summary>
        /// Check peer client connection.
        /// </summary>
        private async Task CheckPeerClientAsync()
        {
            while (ClientPeerConnectionStatus)
            {
                try
                {

                    if (!ClientPeerConnectionStatus)
                    {
                        break;
                    }

                    if (!_onSendingPacketResponse)
                    {
                        // If any packet are received after the delay, the function close the peer client connection to listen.
                        if (ClientPeerLastPacketReceived + _peerNetworkSettingObject.PeerMaxDelayConnection < ClassUtility.GetCurrentTimestampInSecond())
                        {
                            // On this case, insert invalid attempt of connection.
                            if (!_clientPeerPacketReceivedStatus)
                            {
                                ClassPeerCheckManager.InputPeerClientNoPacketConnectionOpened(_peerClientIp, _peerUniqueId, _peerNetworkSettingObject, _peerFirewallSettingObject);
                            }
                            break;
                        }
                    }

                    if (!ClassUtility.SocketIsConnected(_tcpClientPeer))
                    {
                        break;
                    }

                    if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                    {
                        if (!ClassPeerFirewallManager.CheckClientIpStatus(_peerClientIp))
                        {
                            break;
                        }
                    }

                    try
                    {
                        await Task.Delay(1000, _cancellationTokenClientCheckConnectionPeer.Token);
                    }
                    catch
                    {
                        break;
                    }
                    if (_cancellationTokenClientCheckConnectionPeer.Token.IsCancellationRequested)
                    {
                        break;
                    }
                }
                catch
                {
                    break;
                }
            }
            ClosePeerClient(true);
        }

        /// <summary>
        /// Close peer client connection received.
        /// </summary>
        public void ClosePeerClient(bool fromCheckConnection)
        {

            // Clean up.
            _listPacketReceived.Clear();
            _listMemPoolBroadcastBlockHeight.Clear();

            if (!_clientPeerClosed)
            {
                ClientPeerConnectionStatus = false;

                try
                {
                    if (_cancellationTokenListenPeerPacket != null)
                    {
                        if (!_cancellationTokenListenPeerPacket.IsCancellationRequested)
                        {
                            _cancellationTokenListenPeerPacket.Cancel();
                        }
                    }
                }
                catch
                {
                    // Ignored.
                }

                if (!fromCheckConnection)
                {
                    try
                    {
                        if (_cancellationTokenClientCheckConnectionPeer != null)
                        {
                            if (!_cancellationTokenClientCheckConnectionPeer.IsCancellationRequested)
                            {
                                _cancellationTokenClientCheckConnectionPeer.Cancel();
                            }
                        }
                    }
                    catch
                    {
                        // Ignored.
                    }
                }


                try
                {
                    if (_tcpClientPeer?.Client != null)
                    {
                        if (_tcpClientPeer.Client.Connected)
                        {
                            try
                            {
                                _tcpClientPeer.Client.Shutdown(SocketShutdown.Both);
                            }
                            finally
                            {
                                if (_tcpClientPeer?.Client != null)
                                {
                                    if (_tcpClientPeer.Client.Connected)
                                    {
                                        _tcpClientPeer.Client.Close();
                                    }
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Ignored.
                }
                _clientPeerClosed = true;

            }

        }

        #endregion

        #region Listen peer client packets received.

        /// <summary>
        /// Listen packet received from peer.
        /// </summary>
        /// <returns></returns>
        public async Task HandlePeerClient()
        {
            try
            {
                // Launch a task for check the peer connection.
                await Task.Factory.StartNew(CheckPeerClientAsync, _cancellationTokenClientCheckConnectionPeer.Token).ConfigureAwait(false);
            }
            catch
            {
                ClosePeerClient(false);
            }


            try
            {
                await Task.Factory.StartNew(async () =>
                {
                    byte[] packetBufferOnReceive = new byte[_peerNetworkSettingObject.PeerMaxPacketBufferSize];
                    _listPacketReceived.Add(new ClassReadPacketSplitted());
                    try
                    {
                        using (NetworkStream networkStream = new NetworkStream(_tcpClientPeer.Client))
                        {

                            while (ClientPeerConnectionStatus && !_clientAskDisconnection)
                            {
                                try
                                {


                                    
                                    int packetLength = await networkStream.ReadAsync(packetBufferOnReceive, 0, packetBufferOnReceive.Length, _cancellationTokenListenPeerPacket.Token);

                                    if (_clientAskDisconnection || !ClientPeerConnectionStatus)
                                    {
                                        ClientPeerConnectionStatus = false;
                                        break;
                                    }

                                    if (packetLength > 0)
                                    {
                                        ClientPeerLastPacketReceived = ClassUtility.GetCurrentTimestampInSecond();

                                        bool containSeperator = false;

                                        foreach (byte dataByte in packetBufferOnReceive)
                                        {
                                            char character = (char)dataByte;
                                            if (character != '\0')
                                            {

                                                if (ClassUtility.CharIsABase64Character(character))
                                                {
                                                    _listPacketReceived[_listPacketReceived.Count - 1].Packet.Add(dataByte);
                                                }

                                                if (character == ClassPeerPacketSetting.PacketPeerSplitSeperator)
                                                {
                                                    _listPacketReceived[_listPacketReceived.Count - 1].Complete = true;
                                                    _listPacketReceived.Add(new ClassReadPacketSplitted());
                                                    containSeperator = true;

                                                }
                                            }
                                        }

                                        Array.Clear(packetBufferOnReceive, 0, packetBufferOnReceive.Length);

                                        if (_listPacketReceived.Count > 0 && containSeperator)
                                        {

                                            foreach (var packetSplitted in _listPacketReceived)
                                            {
                                                if (packetSplitted.Complete)
                                                {
                                                    string packet = packetSplitted.Packet.ToArray().GetStringFromByteArrayAscii();

                                                    if (!packet.IsNullOrEmpty())
                                                    {
                                                        bool failed = false;

                                                        byte[] base64Packet = null;

                                                        try
                                                        {
                                                            base64Packet = Convert.FromBase64String(packet);
                                                        }
                                                        catch
                                                        {
                                                            failed = true;
                                                        }

                                                        if (!failed && base64Packet.Length > 0)
                                                        {
                                                            _onSendingPacketResponse = true;

                                                            switch (await HandlePacket(base64Packet))
                                                            {
                                                                case ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_TYPE_PACKET:
                                                                case ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET:
                                                                    {
                                                                        ClassPeerCheckManager.InputPeerClientInvalidPacket(_peerClientIp, _peerUniqueId, _peerNetworkSettingObject, _peerFirewallSettingObject);
                                                                        ClientPeerConnectionStatus = false;
                                                                    }
                                                                    break;
                                                                case ClassPeerNetworkClientServerHandlePacketEnumStatus.EXCEPTION_PACKET:
                                                                case ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET:
                                                                    {
                                                                        ClassPeerCheckManager.InputPeerClientAttemptConnect(_peerClientIp, _peerUniqueId, _peerNetworkSettingObject, _peerFirewallSettingObject);
                                                                        ClientPeerConnectionStatus = false;
                                                                    }
                                                                    break;
                                                                case ClassPeerNetworkClientServerHandlePacketEnumStatus.VALID_PACKET:
                                                                    {
                                                                        ClassPeerCheckManager.InputPeerClientValidPacket(_peerClientIp, _peerUniqueId);
                                                                        if (_clientAskDisconnection)
                                                                        {
                                                                            ClientPeerConnectionStatus = false;
                                                                        }
                                                                    }
                                                                    break;

                                                            }

                                                            if (base64Packet.Length > 0)
                                                            {
                                                                Array.Clear(base64Packet, 0, base64Packet.Length);
                                                            }

                                                            _onSendingPacketResponse = false;
                                                        }

                                                    }
                                                }
                                            }

                                            _listPacketReceived.RemoveAll(x => x.Complete);
                                            containSeperator = false;
                                        }

                                    }

                                    if (_clientAskDisconnection || !ClientPeerConnectionStatus)
                                    {
                                        ClientPeerConnectionStatus = false;
                                        break;
                                    }


                                }
                                catch
                                {
                                    ClientPeerConnectionStatus = false;
                                    break;
                                }
                            }

                        }
                    }
                    catch (SocketException)
                    {
                        ClientPeerConnectionStatus = false;
                    }
                    catch (OperationCanceledException)
                    {
                        ClientPeerConnectionStatus = false;
                    }
                }, _cancellationTokenListenPeerPacket.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Clean up.
                _listPacketReceived.Clear();
                ClientPeerConnectionStatus = false;
            }
        }

        #endregion

        #region Handle peer packet received.

        /// <summary>
        /// Handle decrypted packet. (Usually used for send auth keys for register a new peer).
        /// </summary>
        /// <param name="packet">Packet received to handle.</param>
        /// <returns>Return the status of the handle of the packet.</returns>
        private async Task<ClassPeerNetworkClientServerHandlePacketEnumStatus> HandlePacket(byte[] packet)
        {

            try
            {
                if (!ClassUtility.TryDeserialize(packet.GetStringFromByteArrayAscii(), out ClassPeerPacketSendObject packetSendObject, ObjectCreationHandling.Reuse))
                {
                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_TYPE_PACKET;
                }

                if (packetSendObject == null)
                {
                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_TYPE_PACKET;
                }

                _clientPeerPacketReceivedStatus = true;


                _peerUniqueId = packetSendObject.PacketPeerUniqueId;

                bool peerExist = false;

                if (!_peerUniqueId.IsNullOrEmpty())
                {
                    if (ClassPeerDatabase.ContainsPeer(_peerClientIp, _peerUniqueId))
                    {
                        peerExist = true;
                        ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerLastPacketReceivedTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                    }
                }

                if (!peerExist)
                {
                    if (packetSendObject.PacketOrder != ClassPeerEnumPacketSend.ASK_PEER_AUTH_KEYS)
                    {
                        await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                        {
                            PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                            PacketContent = string.Empty,
                        }, false);

                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                    }
                }

                #region Check packet signature if necessary.

                bool peerIgnorePacketSignature = packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_PEER_AUTH_KEYS || packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_KEEP_ALIVE || packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_MEM_POOL_BROADCAST_MODE;
                bool peerPacketSignatureValid = true;

                if (!peerIgnorePacketSignature)
                {
                    peerIgnorePacketSignature = ClassPeerCheckManager.CheckPeerClientWhitelistStatus(_peerClientIp, _peerUniqueId, _peerNetworkSettingObject);

                    if (!peerIgnorePacketSignature)
                    {
                        peerPacketSignatureValid = CheckContentPacketSignaturePeer(packetSendObject);
                    }

                    if (!peerPacketSignatureValid)
                    {
                        await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                        {
                            PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_SIGNATURE,
                            PacketContent = string.Empty,
                        }, false);

                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                    }
                }

                #endregion

                #region Packet type if the broadcast client mode is enabled.

                if (_enableMemPoolBroadcastClientMode)
                {
                    bool invalidPacketBroadcast = true;


                    if (packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_KEEP_ALIVE ||
                        packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_DISCONNECT_REQUEST ||
                        packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_MEM_POOl_BLOCK_HEIGHT_LIST_BROADCAST_MODE ||
                        packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_MEM_POOl_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE ||
                        packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_MEM_POOL_TRANSACTION_VOTE ||
                        packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_MEM_POOL_TRANSACTION_BROADCAST_CONFIRMATION_RECEIVED)
                    {
                        invalidPacketBroadcast = false;
                    }

                    if (invalidPacketBroadcast)
                    {
                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_TYPE_PACKET;
                    }
                }
                else
                {
                    if (packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_MEM_POOl_BLOCK_HEIGHT_LIST_BROADCAST_MODE ||
                        packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_MEM_POOl_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE ||
                        packetSendObject.PacketOrder == ClassPeerEnumPacketSend.ASK_MEM_POOL_TRANSACTION_BROADCAST_CONFIRMATION_RECEIVED)
                    {
                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_TYPE_PACKET;
                    }
                }

                #endregion

                switch (packetSendObject.PacketOrder)
                {
                    case ClassPeerEnumPacketSend.ASK_PEER_AUTH_KEYS: // ! This packet type is not encrypted because we exchange unique encryption keys, public key, numeric public key to the node who ask them. !
                        {
                            ClassPeerPacketSendAskPeerAuthKeys packetSendPeerAuthKeysObject = JsonConvert.DeserializeObject<ClassPeerPacketSendAskPeerAuthKeys>(packetSendObject.PacketContent);

                            if (ClassUtility.CheckPacketTimestamp(packetSendPeerAuthKeysObject.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                            {
                                bool forceUpdate = !peerExist;
                                if (peerExist)
                                {
                                    long timestamp = ClassUtility.GetCurrentTimestampInSecond();
                                    if (ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerLastValidPacket == 0 ||
                                        ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerStatus != ClassPeerEnumStatus.PEER_ALIVE ||
                                        !ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerIsPublic)
                                    {
                                        forceUpdate = true;
                                    }
                                }
                                else
                                {
                                    forceUpdate = true;
                                }

                                await ClassPeerKeysManager.UpdatePeerKeysReceivedNetworkServer(_peerClientIp, _peerUniqueId, packetSendPeerAuthKeysObject, _cancellationTokenAccessData);

                                if (await ClassPeerKeysManager.UpdatePeerInternalKeys(_peerClientIp, packetSendPeerAuthKeysObject.PeerPort, _peerUniqueId, _cancellationTokenAccessData, _peerNetworkSettingObject, forceUpdate))
                                {
                                    if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.SEND_PEER_AUTH_KEYS,
                                        PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendPeerAuthKeys()
                                        {
                                            AesEncryptionIv = ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerInternPacketEncryptionKeyIv,
                                            AesEncryptionKey = ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerInternPacketEncryptionKey,
                                            PublicKey = ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerInternPublicKey,
                                            NumericPublicKey = _peerNetworkSettingObject.PeerNumericPublicKey,
                                            PeerPort = _peerNetworkSettingObject.ListenPort,
                                            PeerApiPort = _peerNetworkSettingObject.ListenApiPort,
                                            PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                        })
                                    }, false))
                                    {
                                        ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                    }
                                }
                                else
                                {
                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_SIGNATURE,
                                        PacketContent = string.Empty,
                                    }, false);
                                }
                            }
                            else
                            {
                                ClassLog.WriteLine("Packet from peer: " + _peerClientIp + " expired.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_HIGH_PRIORITY);

                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_TIMESTAMP,
                                    PacketContent = string.Empty,
                                }, false);


                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_PEER_LIST:
                        {
                            ClassPeerPacketSendAskPeerList packetSendAskPeerList = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskPeerList>(packetSendObject.PacketContent);

                            if (packetSendAskPeerList != null)
                            {
                                if (ClassUtility.CheckPacketTimestamp(packetSendAskPeerList.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    Dictionary<string, Tuple<int, string>> listPeerInfo = ClassPeerDatabase.GetPeerListInfo(_peerClientIp);

                                    if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.SEND_PEER_LIST,
                                        PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendPeerList()
                                        {
                                            PeerIpList = new List<string>(listPeerInfo.Keys),
                                            PeerPortList = new List<int>(listPeerInfo.Values.Select(x => x.Item1)),
                                            PeerUniqueIdList = new List<string>(listPeerInfo.Values.Select(x => x.Item2)),
                                            PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                        })
                                    }, true))
                                    {
                                        ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                    }
                                }
                                else
                                {
                                    ClassLog.WriteLine("Packet from peer: " + _peerClientIp + " timestamp expired.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_HIGH_PRIORITY);
                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_TIMESTAMP,
                                        PacketContent = string.Empty,
                                    }, false);

                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                ClassLog.WriteLine("Packet from peer: " + _peerClientIp + " can't be decrypted or the can't be deseralized because empty or invalid format.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_HIGH_PRIORITY);

                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_ENCRYPTION,
                                    PacketContent = string.Empty,
                                }, false);

                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_LIST_SOVEREIGN_UPDATE:
                        {
                            ClassPeerPacketSendAskListSovereignUpdate packetSendAskListSovereignUpdate = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskListSovereignUpdate>(packetSendObject.PacketContent);

                            if (packetSendAskListSovereignUpdate != null)
                            {
                                if (ClassUtility.CheckPacketTimestamp(packetSendAskListSovereignUpdate.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    ClassPeerPacketSendListSovereignUpdate packetContent = new ClassPeerPacketSendListSovereignUpdate()
                                    {
                                        SovereignUpdateHashList = ClassSovereignUpdateDatabase.GetSovereignUpdateListHash().ToList(),
                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                    };

                                    SignPacketWithNumericPrivateKey(packetContent, out string numericHash, out string numericSignature);
                                    packetContent.PacketNumericHash = numericHash;
                                    packetContent.PacketNumericSignature = numericSignature;

                                    if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.SEND_LIST_SOVEREIGN_UPDATE,
                                        PacketContent = JsonConvert.SerializeObject(packetContent)
                                    }, true))
                                    {
                                        ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                    }
                                }
                                else
                                {
                                    ClassLog.WriteLine("Packet content decrypted and deserialized from peer: " + _peerClientIp + " is empty.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                        PacketContent = string.Empty,
                                    }, false);

                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                ClassLog.WriteLine("Packet from peer: " + _peerClientIp + " can't be decrypted or the can't be deseralized because empty or invalid format.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_HIGH_PRIORITY);

                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_ENCRYPTION,
                                    PacketContent = string.Empty,
                                }, false);

                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_SOVEREIGN_UPDATE_FROM_HASH:
                        {
                            ClassPeerPacketSendAskSovereignUpdateFromHash packetSendAskSovereignUpdateFromHash = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskSovereignUpdateFromHash>(packetSendObject.PacketContent);

                            if (packetSendAskSovereignUpdateFromHash != null)
                            {
                                if (ClassUtility.CheckPacketTimestamp(packetSendAskSovereignUpdateFromHash.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    if (!packetSendAskSovereignUpdateFromHash.SovereignUpdateHash.IsNullOrEmpty())
                                    {
                                        if (ClassSovereignUpdateDatabase.DictionarySovereignUpdateObject.ContainsKey(packetSendAskSovereignUpdateFromHash.SovereignUpdateHash))
                                        {


                                            // Build numeric signature.
                                            SignPacketWithNumericPrivateKey(ClassSovereignUpdateDatabase.DictionarySovereignUpdateObject[packetSendAskSovereignUpdateFromHash.SovereignUpdateHash], out string hashNumeric, out string signatureNumeric);

                                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                            {
                                                PacketOrder = ClassPeerEnumPacketResponse.SEND_SOVEREIGN_UPDATE_FROM_HASH,
                                                PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendSovereignUpdateFromHash()
                                                {
                                                    SovereignUpdateObject = ClassSovereignUpdateDatabase.DictionarySovereignUpdateObject[packetSendAskSovereignUpdateFromHash.SovereignUpdateHash],
                                                    PacketNumericHash = hashNumeric,
                                                    PacketNumericSignature = signatureNumeric,
                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                })
                                            }, true))
                                            {
                                                ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                            }
                                        }
                                        else
                                        {
                                            ClassLog.WriteLine("Sovereign Update Hash received from peer: " + _peerClientIp + " not exist on registered updates. Hash received: " + packetSendAskSovereignUpdateFromHash.SovereignUpdateHash, ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                            await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                            {
                                                PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                                PacketContent = string.Empty,
                                            }, true);

                                            return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                        }
                                    }
                                    else
                                    {
                                        ClassLog.WriteLine("Sovereign Update Hash received from peer: " + _peerClientIp + " is empty.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                        await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                        {
                                            PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                            PacketContent = string.Empty,
                                        }, false);

                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                    }
                                }
                                else
                                {
                                    ClassLog.WriteLine("Packet content decrypted and deserialized from peer: " + _peerClientIp + " is empty.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_TIMESTAMP,
                                        PacketContent = string.Empty,
                                    }, true);

                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                ClassLog.WriteLine("Packet from peer: " + _peerClientIp + " can't be decrypted or the can't be deseralized because empty or invalid format.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_HIGH_PRIORITY);

                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_ENCRYPTION,
                                    PacketContent = string.Empty,
                                }, false);

                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_NETWORK_INFORMATION:
                        {
                            ClassPeerPacketSendAskNetworkInformation packetSendAskNetworkInformation = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskNetworkInformation>(packetSendObject.PacketContent);

                            if (packetSendAskNetworkInformation != null)
                            {
                                if (ClassUtility.CheckPacketTimestamp(packetSendAskNetworkInformation.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    long lastBlockHeight = ClassBlockchainStats.GetLastBlockHeight();

                                    if (lastBlockHeight >= BlockchainSetting.GenesisBlockHeight)
                                    {
                                        long lastBlockHeightUnlocked = ClassBlockchainStats.GetLastBlockHeightUnlocked(_cancellationTokenAccessData);

                                        ClassBlockObject blockObject = await ClassBlockchainStats.GetBlockInformationData(lastBlockHeight, _cancellationTokenAccessData);

                                        if (blockObject != null)
                                        {
                                            ClassPeerPacketSendNetworkInformation packetSendNetworkInformation = new ClassPeerPacketSendNetworkInformation()
                                            {
                                                CurrentBlockHeight = lastBlockHeight,
                                                LastBlockHeightUnlocked = lastBlockHeightUnlocked,
                                                CurrentBlockDifficulty = blockObject.BlockDifficulty,
                                                CurrentBlockHash = blockObject.BlockHash,
                                                TimestampBlockCreate = blockObject.TimestampCreate,
                                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                            };

                                            SignPacketWithNumericPrivateKey(packetSendNetworkInformation, out string hashNumeric, out string signatureNumeric);

                                            packetSendNetworkInformation.PacketNumericHash = hashNumeric;
                                            packetSendNetworkInformation.PacketNumericSignature = signatureNumeric;

                                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                            {
                                                PacketOrder = ClassPeerEnumPacketResponse.SEND_NETWORK_INFORMATION,
                                                PacketContent = JsonConvert.SerializeObject(packetSendNetworkInformation)
                                            }, true))
                                            {
                                                ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                            }
                                        }
                                    }
                                    // No block synced on the node.
                                    else
                                    {
                                        ClassPeerPacketSendNetworkInformation packetSendNetworkInformation = new ClassPeerPacketSendNetworkInformation()
                                        {
                                            CurrentBlockHeight = lastBlockHeight,
                                            LastBlockHeightUnlocked = 0,
                                            CurrentBlockDifficulty = 0,
                                            CurrentBlockHash = string.Empty,
                                            TimestampBlockCreate = 0,
                                            PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                        };

                                        SignPacketWithNumericPrivateKey(packetSendNetworkInformation, out string hashNumeric, out string signatureNumeric);

                                        packetSendNetworkInformation.PacketNumericHash = hashNumeric;
                                        packetSendNetworkInformation.PacketNumericSignature = signatureNumeric;

                                        if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                        {
                                            PacketOrder = ClassPeerEnumPacketResponse.SEND_NETWORK_INFORMATION,
                                            PacketContent = JsonConvert.SerializeObject(packetSendNetworkInformation)
                                        }, true))
                                        {
                                            ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                            return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                        }
                                    }
                                }
                                else
                                {
                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_TIMESTAMP,
                                        PacketContent = string.Empty,
                                    }, false);

                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                ClassLog.WriteLine("Packet from peer: " + _peerClientIp + " can't be decrypted or the can't be deseralized because empty or invalid format.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_HIGH_PRIORITY);

                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_ENCRYPTION,
                                    PacketContent = string.Empty,
                                }, false);

                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_BLOCK_DATA:
                        {
                            ClassPeerPacketSendAskBlockData packetSendAskBlockData = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskBlockData>(packetSendObject.PacketContent);

                            if (packetSendAskBlockData != null)
                            {
                                if (ClassUtility.CheckPacketTimestamp(packetSendAskBlockData.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    if (ClassBlockchainStats.ContainsBlockHeight(packetSendAskBlockData.BlockHeight))
                                    {
                                        ClassBlockObject blockObject = await ClassBlockchainStats.GetBlockInformationData(packetSendAskBlockData.BlockHeight, _cancellationTokenAccessData);

                                        if (blockObject != null)
                                        {
                                            // Do a deep copy of the block information to provide, to avoid the GC link to the object retrieved.
                                            // Initialize back from 0 confirmations done on it, this is already done from the peer client who sync the block by security.
                                            // This initialization back from the beginning, is essential, 
                                            // because your node who provide the block data, need to be the same between other nodes contacted by the peer who contact yours.
                                            blockObject.DeepCloneBlockObject(false, out ClassBlockObject blockObjectCopy);
                                            blockObjectCopy.BlockTransactionFullyConfirmed = false;
                                            blockObjectCopy.BlockUnlockValid = false;
                                            blockObjectCopy.BlockNetworkAmountConfirmations = 0;
                                            blockObjectCopy.BlockSlowNetworkAmountConfirmations = 0;
                                            blockObjectCopy.BlockLastHeightTransactionConfirmationDone = 0;
                                            blockObjectCopy.BlockTotalTaskTransactionConfirmationDone = 0;
                                            blockObjectCopy.BlockTransactionConfirmationCheckTaskDone = false;
                                            blockObjectCopy.BlockTotalTaskTransactionConfirmationDone = 0;
                                            blockObjectCopy.BlockTransactionCountInSync = blockObject.TotalTransaction;
                                            blockObjectCopy.TotalCoinConfirmed = 0;
                                            blockObjectCopy.TotalCoinPending = 0;
                                            blockObjectCopy.TotalFee = 0;
                                            blockObjectCopy.TotalTransactionConfirmed = 0;
                                            blockObjectCopy.TotalTransaction = blockObject.TotalTransaction;

                                            ClassPeerPacketSendBlockData packetSendBlockData = new ClassPeerPacketSendBlockData()
                                            {
                                                BlockData = blockObjectCopy,
                                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                            };

                                            if (blockObject.BlockHeight > BlockchainSetting.GenesisBlockHeight)
                                            {
                                                if (blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                                                {
                                                    packetSendBlockData.BlockData.TimestampFound = blockObject.BlockMiningPowShareUnlockObject.Timestamp;
                                                }
                                            }

                                            SignPacketWithNumericPrivateKey(packetSendBlockData, out string hashNumeric, out string signatureNumeric);

                                            packetSendBlockData.PacketNumericHash = hashNumeric;
                                            packetSendBlockData.PacketNumericSignature = signatureNumeric;

                                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                            {
                                                PacketOrder = ClassPeerEnumPacketResponse.SEND_BLOCK_DATA,
                                                PacketContent = JsonConvert.SerializeObject(packetSendBlockData)
                                            }, true))
                                            {
                                                ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                            }
                                        }
                                        else
                                        {
                                            await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                            {
                                                PacketOrder = ClassPeerEnumPacketResponse.NOT_YET_SYNCED,
                                                PacketContent = string.Empty,
                                            }, false);
                                            return ClassPeerNetworkClientServerHandlePacketEnumStatus.VALID_PACKET;
                                        }
                                    }
                                    else
                                    {
                                        await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                        {
                                            PacketOrder = ClassPeerEnumPacketResponse.NOT_YET_SYNCED,
                                            PacketContent = string.Empty,
                                        }, false);
                                        //
                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.VALID_PACKET;
                                    }
                                }
                                else
                                {
                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_TIMESTAMP,
                                        PacketContent = string.Empty,
                                    }, false);

                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                ClassLog.WriteLine("Packet from peer: " + _peerClientIp + " can't be decrypted or the can't be deseralized because empty or invalid format.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_HIGH_PRIORITY);

                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_ENCRYPTION,
                                    PacketContent = string.Empty,
                                }, false);

                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_BLOCK_HEIGHT_INFORMATION:
                        {
                            ClassPeerPacketSendAskBlockHeightInformation packetSendAskBlockHeightInformation = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskBlockHeightInformation>(packetSendObject.PacketContent);

                            if (packetSendAskBlockHeightInformation != null)
                            {
                                if (ClassUtility.CheckPacketTimestamp(packetSendAskBlockHeightInformation.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    if (ClassBlockchainStats.ContainsBlockHeight(packetSendAskBlockHeightInformation.BlockHeight))
                                    {


                                        ClassBlockObject blockObject = await ClassBlockchainStats.GetBlockInformationData(packetSendAskBlockHeightInformation.BlockHeight, _cancellationTokenAccessData);

                                        if (blockObject != null)
                                        {

                                            ClassPeerPacketSendBlockHeightInformation packetSendBlockHeightInformation = new ClassPeerPacketSendBlockHeightInformation()
                                            {
                                                BlockHeight = packetSendAskBlockHeightInformation.BlockHeight,
                                                BlockFinalTransactionHash = blockObject.BlockFinalHashTransaction,
                                                BlockHash = blockObject.BlockHash,
                                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                            };


                                            SignPacketWithNumericPrivateKey(packetSendBlockHeightInformation, out string hashNumeric, out string signatureNumeric);

                                            packetSendBlockHeightInformation.PacketNumericHash = hashNumeric;
                                            packetSendBlockHeightInformation.PacketNumericSignature = signatureNumeric;

                                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                            {
                                                PacketOrder = ClassPeerEnumPacketResponse.SEND_BLOCK_HEIGHT_INFORMATION,
                                                PacketContent = JsonConvert.SerializeObject(packetSendBlockHeightInformation)
                                            }, true))
                                            {
                                                ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                            }
                                        }
                                        else
                                        {
                                            await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                            {
                                                PacketOrder = ClassPeerEnumPacketResponse.NOT_YET_SYNCED,
                                                PacketContent = string.Empty,
                                            }, false);
                                            //
                                            return ClassPeerNetworkClientServerHandlePacketEnumStatus.VALID_PACKET;
                                        }
                                    }
                                    else
                                    {
                                        await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                        {
                                            PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                            PacketContent = string.Empty,
                                        }, false);

                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                    }
                                }
                                else
                                {
                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_TIMESTAMP,
                                        PacketContent = string.Empty,
                                    }, false);

                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                    PacketContent = string.Empty,
                                }, false);

                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_BLOCK_TRANSACTION_DATA:
                        {
                            ClassPeerPacketSendAskBlockTransactionData packetSendAskBlockTransactionData = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskBlockTransactionData>(packetSendObject.PacketContent);

                            if (packetSendAskBlockTransactionData != null)
                            {
                                if (ClassUtility.CheckPacketTimestamp(packetSendAskBlockTransactionData.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    if (ClassBlockchainStats.ContainsBlockHeight(packetSendAskBlockTransactionData.BlockHeight))
                                    {
                                        int blockTransactionCount = await ClassBlockchainStats.GetBlockTransactionCount(packetSendAskBlockTransactionData.BlockHeight, _cancellationTokenAccessData);

                                        if (blockTransactionCount > packetSendAskBlockTransactionData.TransactionId)
                                        {
                                            SortedList<string, ClassBlockTransaction> transactionList = await ClassBlockchainStats.GetTransactionListFromBlockHeightTarget(packetSendAskBlockTransactionData.BlockHeight, true, _cancellationTokenAccessData);

                                            if (transactionList.Count > packetSendAskBlockTransactionData.TransactionId)
                                            {


                                                ClassPeerPacketSendBlockTransactionData packetSendBlockTransactionData = new ClassPeerPacketSendBlockTransactionData()
                                                {
                                                    BlockHeight = packetSendAskBlockTransactionData.BlockHeight,
                                                    TransactionObject = transactionList.ElementAt(packetSendAskBlockTransactionData.TransactionId).Value.TransactionObject,
                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                };


                                                SignPacketWithNumericPrivateKey(packetSendBlockTransactionData, out string hashNumeric, out string signatureNumeric);

                                                packetSendBlockTransactionData.PacketNumericHash = hashNumeric;
                                                packetSendBlockTransactionData.PacketNumericSignature = signatureNumeric;

                                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                {
                                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_BLOCK_TRANSACTION_DATA,
                                                    PacketContent = JsonConvert.SerializeObject(packetSendBlockTransactionData)
                                                }, true))
                                                {
                                                    ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                    transactionList.Clear();
                                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                }
                                            }

                                            transactionList.Clear();
                                        }
                                        else
                                        {
                                            await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                            {
                                                PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                                PacketContent = string.Empty,
                                            }, false);
                                            return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                        }
                                    }
                                    else
                                    {
                                        await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                        {
                                            PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                            PacketContent = string.Empty,
                                        }, false);
                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                    }
                                }
                                else
                                {
                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_TIMESTAMP,
                                        PacketContent = string.Empty,
                                    }, false);
                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                    PacketContent = string.Empty,
                                }, false);
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_BLOCK_TRANSACTION_DATA_BY_RANGE:
                        {
                            ClassPeerPacketSendAskBlockTransactionDataByRange packetSendAskBlockTransactionDataByRange = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskBlockTransactionDataByRange>(packetSendObject.PacketContent);

                            if (packetSendAskBlockTransactionDataByRange != null)
                            {
                                if (ClassUtility.CheckPacketTimestamp(packetSendAskBlockTransactionDataByRange.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    if (ClassBlockchainStats.ContainsBlockHeight(packetSendAskBlockTransactionDataByRange.BlockHeight))
                                    {
                                        if (packetSendAskBlockTransactionDataByRange.TransactionIdStartRange >= 0 &&
                                            packetSendAskBlockTransactionDataByRange.TransactionIdEndRange >= 0 &&
                                            packetSendAskBlockTransactionDataByRange.TransactionIdStartRange < packetSendAskBlockTransactionDataByRange.TransactionIdEndRange)
                                        {

                                            int blockTransactionCount = await ClassBlockchainStats.GetBlockTransactionCount(packetSendAskBlockTransactionDataByRange.BlockHeight, _cancellationTokenAccessData);

                                            if (blockTransactionCount > packetSendAskBlockTransactionDataByRange.TransactionIdStartRange &&
                                                blockTransactionCount >= packetSendAskBlockTransactionDataByRange.TransactionIdEndRange)
                                            {
                                                SortedList<string, ClassBlockTransaction> transactionList = await ClassBlockchainStats.GetTransactionListFromBlockHeightTarget(packetSendAskBlockTransactionDataByRange.BlockHeight, true, _cancellationTokenAccessData);

                                                if (transactionList.Count > packetSendAskBlockTransactionDataByRange.TransactionIdStartRange &&
                                                    transactionList.Count >= packetSendAskBlockTransactionDataByRange.TransactionIdEndRange)
                                                {


                                                    #region Generate the list of transaction asked by range.

                                                    SortedDictionary<string, ClassTransactionObject> transactionListRangeToSend = new SortedDictionary<string, ClassTransactionObject>();

                                                    foreach (var transactionPair in transactionList.Skip(packetSendAskBlockTransactionDataByRange.TransactionIdStartRange).Take(packetSendAskBlockTransactionDataByRange.TransactionIdEndRange - packetSendAskBlockTransactionDataByRange.TransactionIdStartRange))
                                                    {
                                                        transactionListRangeToSend.Add(transactionPair.Key, transactionPair.Value.TransactionObject);
                                                    }

                                                    #endregion

                                                    ClassPeerPacketSendBlockTransactionDataByRange packetSendBlockTransactionData = new ClassPeerPacketSendBlockTransactionDataByRange()
                                                    {
                                                        BlockHeight = packetSendAskBlockTransactionDataByRange.BlockHeight,
                                                        ListTransactionObject = transactionListRangeToSend,
                                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                    };


                                                    SignPacketWithNumericPrivateKey(packetSendBlockTransactionData, out string hashNumeric, out string signatureNumeric);

                                                    packetSendBlockTransactionData.PacketNumericHash = hashNumeric;
                                                    packetSendBlockTransactionData.PacketNumericSignature = signatureNumeric;

                                                    bool sendError = false;

                                                    if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                    {
                                                        PacketOrder = ClassPeerEnumPacketResponse.SEND_BLOCK_TRANSACTION_DATA_BY_RANGE,
                                                        PacketContent = JsonConvert.SerializeObject(packetSendBlockTransactionData)
                                                    }, true))
                                                    {
                                                        ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                        sendError = true;
                                                    }


                                                    // Clean up.
                                                    transactionList.Clear();
                                                    transactionListRangeToSend.Clear();

                                                    if (sendError)
                                                    {
                                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                    }
                                                }

                                                // Clean up.
                                                transactionList.Clear();
                                            }
                                            else
                                            {
                                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                {
                                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                                    PacketContent = string.Empty,
                                                }, false);
                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                            }
                                        }
                                        else
                                        {
                                            await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                            {
                                                PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                                PacketContent = string.Empty,
                                            }, false);
                                            return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                        }
                                    }
                                    else
                                    {
                                        await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                        {
                                            PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                            PacketContent = string.Empty,
                                        }, false);
                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                    }
                                }
                                else
                                {
                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_TIMESTAMP,
                                        PacketContent = string.Empty,
                                    }, false);
                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                    PacketContent = string.Empty,
                                }, false);
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }

                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_MINING_SHARE_VOTE:
                        {
                            if (ClassBlockchainDatabase.BlockchainMemoryManagement.GetLastBlockHeight >= BlockchainSetting.GenesisBlockHeight)
                            {
                                ClassPeerPacketSendAskMiningShareVote packetSendAskMemPoolMiningShareVote = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskMiningShareVote>(packetSendObject.PacketContent);

                                if (packetSendAskMemPoolMiningShareVote != null)
                                {
                                    if (ClassUtility.CheckPacketTimestamp(packetSendAskMemPoolMiningShareVote.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay) && ClassUtility.CheckPacketTimestamp(packetSendAskMemPoolMiningShareVote.MiningPowShareObject.Timestamp, BlockchainSetting.BlockMiningUnlockShareTimestampMaxDelay, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                    {
                                        if (packetSendAskMemPoolMiningShareVote.MiningPowShareObject != null)
                                        {
                                            // Do not allow to target genesis block and invalid height.
                                            if (packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight <= BlockchainSetting.GenesisBlockHeight)
                                            {
                                                // Just in case we increment the amount of invalid packet.
                                                ClassPeerPacketSendMiningShareVote packetSendMiningShareVote = new ClassPeerPacketSendMiningShareVote()
                                                {
                                                    BlockHeight = packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight,
                                                    VoteStatus = ClassPeerPacketMiningShareVoteEnum.REFUSED,
                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                };

                                                SignPacketWithNumericPrivateKey(packetSendMiningShareVote, out string hashNumeric, out string numericSignature);
                                                packetSendMiningShareVote.PacketNumericHash = hashNumeric;
                                                packetSendMiningShareVote.PacketNumericSignature = numericSignature;

                                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                {
                                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                    PacketContent = JsonConvert.SerializeObject(packetSendMiningShareVote)
                                                }, true))
                                                {
                                                    ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                }
                                            }
                                            else
                                            {
                                                long lastBlockHeight = ClassBlockchainStats.GetLastBlockHeight();

                                                if (packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight > lastBlockHeight)
                                                {
                                                    if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                    {
                                                        PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                        PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendMiningShareVote()
                                                        {
                                                            BlockHeight = lastBlockHeight,
                                                            VoteStatus = ClassPeerPacketMiningShareVoteEnum.NOT_SYNCED,
                                                            PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                        })
                                                    }, true))
                                                    {
                                                        ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;

                                                    }
                                                }
                                                else
                                                {
                                                    ClassBlockObject previousBlockObjectInformation = await ClassBlockchainStats.GetBlockInformationData(packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight - 1, _cancellationTokenAccessData);
                                                    int previousBlockTransactionCount = previousBlockObjectInformation.TotalTransaction;

                                                    if (packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight == lastBlockHeight)
                                                    {
                                                        ClassBlockObject blockObjectInformation = await ClassBlockchainStats.GetBlockInformationData(packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight, _cancellationTokenAccessData);

                                                        if (blockObjectInformation.BlockStatus == ClassBlockEnumStatus.LOCKED)
                                                        {
                                                            // Check the share at first.
                                                            ClassMiningPoWaCEnumStatus miningShareCheckStatus = ClassMiningPoWaCUtility.CheckPoWaCShare(BlockchainSetting.CurrentMiningPoWaCSettingObject(packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight),
                                                                                                     packetSendAskMemPoolMiningShareVote.MiningPowShareObject,
                                                                                                     lastBlockHeight,
                                                                                                     blockObjectInformation.BlockHash,
                                                            blockObjectInformation.BlockDifficulty,
                                                            previousBlockTransactionCount,
                                                            previousBlockObjectInformation.BlockFinalHashTransaction, out BigInteger jobDifficulty, out _);

                                                            // Ensure equality and validity.
                                                            bool shareIsValid = miningShareCheckStatus == ClassMiningPoWaCEnumStatus.VALID_UNLOCK_BLOCK_SHARE && packetSendAskMemPoolMiningShareVote.MiningPowShareObject.PoWaCShareDifficulty == jobDifficulty;

                                                            if (shareIsValid)
                                                            {
                                                                switch (await ClassBlockchainDatabase.UnlockCurrentBlockAsync(packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight, packetSendAskMemPoolMiningShareVote.MiningPowShareObject, false, _peerNetworkSettingObject.ListenIp, _peerServerOpenNatIp, true, false, _peerNetworkSettingObject, _peerFirewallSettingObject, _cancellationTokenAccessData))
                                                                {
                                                                    case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ACCEPTED:
                                                                        {

                                                                            ClassPeerPacketSendMiningShareVote packetSendMiningShareVote = new ClassPeerPacketSendMiningShareVote()
                                                                            {
                                                                                BlockHeight = packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight,
                                                                                VoteStatus = ClassPeerPacketMiningShareVoteEnum.ACCEPTED,
                                                                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                            };

                                                                            SignPacketWithNumericPrivateKey(packetSendMiningShareVote, out string hashNumeric, out string numericSignature);
                                                                            packetSendMiningShareVote.PacketNumericHash = hashNumeric;
                                                                            packetSendMiningShareVote.PacketNumericSignature = numericSignature;


                                                                            bool resultSend = true;
                                                                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                            {
                                                                                PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                                PacketContent = JsonConvert.SerializeObject(packetSendMiningShareVote)
                                                                            }, true))
                                                                            {
                                                                                ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                                resultSend = false;
                                                                            }

                                                                            await Task.Factory.StartNew(async () =>
                                                                            {

                                                                                var miningVoteResult = await ClassBlockchainDatabase.UnlockCurrentBlockAsync(packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight, packetSendAskMemPoolMiningShareVote.MiningPowShareObject, false, _peerNetworkSettingObject.ListenIp, _peerServerOpenNatIp, false, false, _peerNetworkSettingObject, _peerFirewallSettingObject, null);
                                                                                if (miningVoteResult == ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ACCEPTED)
                                                                                {
                                                                                    await ClassPeerNetworkBroadcastFunction.BroadcastMiningShareAsync(_peerNetworkSettingObject.ListenIp, _peerServerOpenNatIp, _peerClientIp, packetSendAskMemPoolMiningShareVote.MiningPowShareObject, _peerNetworkSettingObject, _peerFirewallSettingObject);
                                                                                }

                                                                            }, CancellationToken.None, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);

                                                                            if (resultSend)
                                                                            {
                                                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.VALID_PACKET;
                                                                            }
                                                                            else
                                                                            {
                                                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                                            }
                                                                        }
                                                                    case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ALREADY_FOUND:
                                                                        {
                                                                            if (ClassMiningPoWaCUtility.ComparePoWaCShare(blockObjectInformation.BlockMiningPowShareUnlockObject, packetSendAskMemPoolMiningShareVote.MiningPowShareObject))
                                                                            {

                                                                                ClassPeerPacketSendMiningShareVote packetSendMiningShareVote = new ClassPeerPacketSendMiningShareVote()
                                                                                {
                                                                                    BlockHeight = packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight,
                                                                                    VoteStatus = ClassPeerPacketMiningShareVoteEnum.ACCEPTED,
                                                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                                };

                                                                                SignPacketWithNumericPrivateKey(packetSendMiningShareVote, out string hashNumeric, out string numericSignature);
                                                                                packetSendMiningShareVote.PacketNumericHash = hashNumeric;
                                                                                packetSendMiningShareVote.PacketNumericSignature = numericSignature;


                                                                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                                {
                                                                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                                    PacketContent = JsonConvert.SerializeObject(packetSendMiningShareVote)
                                                                                }, true))
                                                                                {
                                                                                    ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                                                }
                                                                            }
                                                                            else
                                                                            {

                                                                                ClassPeerPacketSendMiningShareVote packetSendMiningShareVote = new ClassPeerPacketSendMiningShareVote()
                                                                                {
                                                                                    BlockHeight = packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight,
                                                                                    VoteStatus = ClassPeerPacketMiningShareVoteEnum.REFUSED,
                                                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                                };

                                                                                SignPacketWithNumericPrivateKey(packetSendMiningShareVote, out string hashNumeric, out string numericSignature);
                                                                                packetSendMiningShareVote.PacketNumericHash = hashNumeric;
                                                                                packetSendMiningShareVote.PacketNumericSignature = numericSignature;

                                                                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                                {
                                                                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                                    PacketContent = JsonConvert.SerializeObject(packetSendMiningShareVote)
                                                                                }, true))
                                                                                {
                                                                                    ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                                                }
                                                                            }
                                                                        }
                                                                        break;
                                                                    case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_NOCONSENSUS:
                                                                        {
                                                                            if (ClassMiningPoWaCUtility.ComparePoWaCShare(blockObjectInformation.BlockMiningPowShareUnlockObject, packetSendAskMemPoolMiningShareVote.MiningPowShareObject))
                                                                            {

                                                                                ClassPeerPacketSendMiningShareVote packetSendMiningShareVote = new ClassPeerPacketSendMiningShareVote()
                                                                                {
                                                                                    BlockHeight = packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight,
                                                                                    VoteStatus = ClassPeerPacketMiningShareVoteEnum.ACCEPTED,
                                                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                                };

                                                                                SignPacketWithNumericPrivateKey(packetSendMiningShareVote, out string hashNumeric, out string numericSignature);
                                                                                packetSendMiningShareVote.PacketNumericHash = hashNumeric;
                                                                                packetSendMiningShareVote.PacketNumericSignature = numericSignature;


                                                                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                                {
                                                                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                                    PacketContent = JsonConvert.SerializeObject(packetSendMiningShareVote)
                                                                                }, true))
                                                                                {
                                                                                    ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                                                }
                                                                            }
                                                                            else
                                                                            {
                                                                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                                {
                                                                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                                    PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendMiningShareVote()
                                                                                    {
                                                                                        BlockHeight = lastBlockHeight,
                                                                                        VoteStatus = ClassPeerPacketMiningShareVoteEnum.REFUSED,
                                                                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                                    })
                                                                                }, true))
                                                                                {
                                                                                    ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                                                }
                                                                            }
                                                                        }
                                                                        break;
                                                                    case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_NOT_SYNCED:
                                                                        {
                                                                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                            {
                                                                                PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                                PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendMiningShareVote()
                                                                                {
                                                                                    BlockHeight = lastBlockHeight,
                                                                                    VoteStatus = ClassPeerPacketMiningShareVoteEnum.NOT_SYNCED,
                                                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                                })
                                                                            }, true))
                                                                            {
                                                                                ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                                            }
                                                                        }
                                                                        break;
                                                                    case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_INVALID_TIMESTAMP:
                                                                    case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_REFUSED:
                                                                        {
                                                                            // By default assume the share is invalid or found by someone else.
                                                                            ClassPeerPacketMiningShareVoteEnum voteStatus = ClassPeerPacketMiningShareVoteEnum.REFUSED;

                                                                            // This is the same winner, probably a returned broadcasted packet of the same share from another peer who have accept the share.
                                                                            if (blockObjectInformation.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                                                                            {
                                                                                if (ClassMiningPoWaCUtility.ComparePoWaCShare(blockObjectInformation.BlockMiningPowShareUnlockObject, packetSendAskMemPoolMiningShareVote.MiningPowShareObject))
                                                                                {
                                                                                    voteStatus = ClassPeerPacketMiningShareVoteEnum.ACCEPTED;
                                                                                }
                                                                            }

                                                                            ClassPeerPacketSendMiningShareVote packetSendMiningShareVote = new ClassPeerPacketSendMiningShareVote()
                                                                            {
                                                                                BlockHeight = packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight,
                                                                                VoteStatus = voteStatus,
                                                                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                            };

                                                                            SignPacketWithNumericPrivateKey(packetSendMiningShareVote, out string hashNumeric, out string numericSignature);
                                                                            packetSendMiningShareVote.PacketNumericHash = hashNumeric;
                                                                            packetSendMiningShareVote.PacketNumericSignature = numericSignature;


                                                                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                            {
                                                                                PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                                PacketContent = JsonConvert.SerializeObject(packetSendMiningShareVote)
                                                                            }, true))
                                                                            {
                                                                                ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;

                                                                            }
                                                                        }
                                                                        break;
                                                                }
                                                            }
                                                            else
                                                            {
                                                                ClassPeerPacketSendMiningShareVote packetSendMiningShareVote = new ClassPeerPacketSendMiningShareVote()
                                                                {
                                                                    BlockHeight = packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight,
                                                                    VoteStatus = ClassPeerPacketMiningShareVoteEnum.REFUSED,
                                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                };

                                                                SignPacketWithNumericPrivateKey(packetSendMiningShareVote, out string hashNumeric, out string numericSignature);
                                                                packetSendMiningShareVote.PacketNumericHash = hashNumeric;
                                                                packetSendMiningShareVote.PacketNumericSignature = numericSignature;

                                                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                {
                                                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                    PacketContent = JsonConvert.SerializeObject(packetSendMiningShareVote)
                                                                }, true))
                                                                {
                                                                    ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                                }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            // By default, assume the share is invalid or found by someone else.
                                                            ClassPeerPacketMiningShareVoteEnum voteStatus = ClassPeerPacketMiningShareVoteEnum.REFUSED;

                                                            // This is the same winner, probably a returned broadcasted packet of the same share from another peer who have accept the share.
                                                            if (ClassMiningPoWaCUtility.ComparePoWaCShare(blockObjectInformation.BlockMiningPowShareUnlockObject, packetSendAskMemPoolMiningShareVote.MiningPowShareObject))
                                                            {
                                                                voteStatus = ClassPeerPacketMiningShareVoteEnum.ACCEPTED;
                                                            }

                                                            ClassPeerPacketSendMiningShareVote packetSendMiningShareVote = new ClassPeerPacketSendMiningShareVote()
                                                            {
                                                                BlockHeight = packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight,
                                                                VoteStatus = voteStatus,
                                                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                            };

                                                            SignPacketWithNumericPrivateKey(packetSendMiningShareVote, out string hashNumeric, out string numericSignature);
                                                            packetSendMiningShareVote.PacketNumericHash = hashNumeric;
                                                            packetSendMiningShareVote.PacketNumericSignature = numericSignature;


                                                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                            {
                                                                PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                PacketContent = JsonConvert.SerializeObject(packetSendMiningShareVote)
                                                            }, true))
                                                            {
                                                                ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                            }
                                                        }
                                                    }
                                                    // If behind.
                                                    else
                                                    {
                                                        if (ClassBlockchainStats.ContainsBlockHeight(packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight))
                                                        {
                                                            ClassBlockObject blockObjectInformation = await ClassBlockchainStats.GetBlockInformationData(packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight, _cancellationTokenAccessData);

                                                            // If the share is the same of the block height target by the share, return the same reponse.
                                                            if (ClassMiningPoWaCUtility.ComparePoWaCShare(blockObjectInformation.BlockMiningPowShareUnlockObject, packetSendAskMemPoolMiningShareVote.MiningPowShareObject))
                                                            {

                                                                ClassPeerPacketSendMiningShareVote packetSendMiningShareVote = new ClassPeerPacketSendMiningShareVote()
                                                                {
                                                                    BlockHeight = packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight,
                                                                    VoteStatus = ClassPeerPacketMiningShareVoteEnum.ACCEPTED,
                                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                };

                                                                SignPacketWithNumericPrivateKey(packetSendMiningShareVote, out string hashNumeric, out string numericSignature);
                                                                packetSendMiningShareVote.PacketNumericHash = hashNumeric;
                                                                packetSendMiningShareVote.PacketNumericSignature = numericSignature;

                                                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                {
                                                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                    PacketContent = JsonConvert.SerializeObject(packetSendMiningShareVote)
                                                                }, true))
                                                                {
                                                                    ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                                }
                                                            }
                                                            // If not even if the block is already found, return false.
                                                            else
                                                            {
                                                                ClassPeerPacketSendMiningShareVote packetSendMiningShareVote = new ClassPeerPacketSendMiningShareVote()
                                                                {
                                                                    BlockHeight = packetSendAskMemPoolMiningShareVote.MiningPowShareObject.BlockHeight,
                                                                    VoteStatus = ClassPeerPacketMiningShareVoteEnum.REFUSED,
                                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                };

                                                                SignPacketWithNumericPrivateKey(packetSendMiningShareVote, out string hashNumeric, out string numericSignature);
                                                                packetSendMiningShareVote.PacketNumericHash = hashNumeric;
                                                                packetSendMiningShareVote.PacketNumericSignature = numericSignature;

                                                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                {
                                                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                    PacketContent = JsonConvert.SerializeObject(packetSendMiningShareVote)
                                                                }, true))
                                                                {
                                                                    ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                                }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                            {
                                                                PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                                                PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendMiningShareVote()
                                                                {
                                                                    BlockHeight = lastBlockHeight,
                                                                    VoteStatus = ClassPeerPacketMiningShareVoteEnum.NOT_SYNCED,
                                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                })
                                                            }, true))
                                                            {
                                                                ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                            {
                                                PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET,
                                                PacketContent = string.Empty,
                                            }, false);

                                            return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                        }
                                    }
                                    else
                                    {
                                        await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                        {
                                            PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_TIMESTAMP,
                                            PacketContent = string.Empty,
                                        }, false);

                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                    }
                                }
                                else
                                {

                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_ENCRYPTION,
                                        PacketContent = string.Empty,
                                    }, false);

                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MINING_SHARE_VOTE,
                                    PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendMiningShareVote()
                                    {
                                        BlockHeight = 0,
                                        VoteStatus = ClassPeerPacketMiningShareVoteEnum.NOT_SYNCED,
                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                    })
                                }, true))
                                {
                                    ClassLog.WriteLine("Packet response to send to peer: " + _peerClientIp + " failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                }
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_MEM_POOL_TRANSACTION_VOTE:
                        {
                            ClassPeerPacketSendAskMemPoolTransactionVote packetSendAskMemPoolTransactionVote = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskMemPoolTransactionVote>(packetSendObject.PacketContent);

                            if (packetSendAskMemPoolTransactionVote != null)
                            {
                                if (ClassUtility.CheckPacketTimestamp(packetSendAskMemPoolTransactionVote.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    if (packetSendAskMemPoolTransactionVote.TransactionObject != null)
                                    {
                                        bool alreadyExist = await ClassMemPoolDatabase.CheckTxHashExist(packetSendAskMemPoolTransactionVote.TransactionObject.TransactionHash, _cancellationTokenAccessData);
                                        ClassTransactionEnumStatus transactionStatus = ClassTransactionEnumStatus.EMPTY_TRANSACTION; // Default.
                                        if (!alreadyExist)
                                        {
                                            transactionStatus = await ClassTransactionUtility.CheckTransactionWithBlockchainData(packetSendAskMemPoolTransactionVote.TransactionObject, true, true, null, 0, null, true, _cancellationTokenAccessData);

                                            if (transactionStatus != ClassTransactionEnumStatus.VALID_TRANSACTION)
                                            {
                                                ClassPeerCheckManager.InputPeerClientInvalidPacket(_peerClientIp, _peerUniqueId, _peerNetworkSettingObject, _peerFirewallSettingObject);
                                            }
                                        }
                                        else
                                        {
                                            ClassTransactionObject memPoolTransactionObject = await ClassMemPoolDatabase.GetMemPoolTxFromTransactionHash(packetSendAskMemPoolTransactionVote.TransactionObject.TransactionHash, _cancellationTokenAccessData);

                                            if (memPoolTransactionObject != null)
                                            {
                                                alreadyExist = true;
                                                if (!ClassTransactionUtility.CompareTransactionObject(memPoolTransactionObject, packetSendAskMemPoolTransactionVote.TransactionObject))
                                                {
                                                    transactionStatus = ClassTransactionEnumStatus.DUPLICATE_TRANSACTION_HASH;
                                                    ClassPeerCheckManager.InputPeerClientInvalidPacket(_peerClientIp, _peerUniqueId, _peerNetworkSettingObject, _peerFirewallSettingObject);
                                                }
                                                else
                                                {
                                                    transactionStatus = ClassTransactionEnumStatus.VALID_TRANSACTION;
                                                }
                                            }
                                            else
                                            {
                                                transactionStatus = ClassTransactionEnumStatus.EMPTY_TRANSACTION;
                                                ClassPeerCheckManager.InputPeerClientInvalidPacket(_peerClientIp, _peerUniqueId, _peerNetworkSettingObject, _peerFirewallSettingObject);
                                            }
                                        }

                                        if (transactionStatus == ClassTransactionEnumStatus.VALID_TRANSACTION
                                            && !alreadyExist)
                                        {
                                            if (!_enableMemPoolBroadcastClientMode)
                                            {
                                                await Task.Factory.StartNew(async () =>
                                                {
                                                    await ClassMemPoolDatabase.InsertTxToMemPoolAsync(packetSendAskMemPoolTransactionVote.TransactionObject, new CancellationTokenSource());
                                                }).ConfigureAwait(false);
                                            }
                                            else
                                            {
                                                await ClassMemPoolDatabase.InsertTxToMemPoolAsync(packetSendAskMemPoolTransactionVote.TransactionObject, _cancellationTokenAccessData);
                                            }
                                        }


                                        ClassPeerPacketSendMemPoolTransactionVote packetSendMemPoolTransactionVote = new ClassPeerPacketSendMemPoolTransactionVote()
                                        {
                                            TransactionHash = packetSendAskMemPoolTransactionVote.TransactionObject.TransactionHash,
                                            TransactionStatus = transactionStatus,
                                            PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                        };

                                        SignPacketWithNumericPrivateKey(packetSendMemPoolTransactionVote, out string hashNumeric, out string numericSignature);
                                        packetSendMemPoolTransactionVote.PacketNumericHash = hashNumeric;
                                        packetSendMemPoolTransactionVote.PacketNumericSignature = numericSignature;

                                        if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                        {
                                            PacketOrder = ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_VOTE,
                                            PacketContent = JsonConvert.SerializeObject(packetSendMemPoolTransactionVote),
                                        }, true))
                                        {
                                            return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                        }
                                    }
                                    else
                                    {
                                        ClassPeerPacketSendMemPoolTransactionVote packetSendMemPoolTransactionVote = new ClassPeerPacketSendMemPoolTransactionVote()
                                        {
                                            TransactionHash = string.Empty,
                                            TransactionStatus = ClassTransactionEnumStatus.EMPTY_TRANSACTION,
                                            PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                        };

                                        SignPacketWithNumericPrivateKey(packetSendMemPoolTransactionVote, out string hashNumeric, out string numericSignature);
                                        packetSendMemPoolTransactionVote.PacketNumericHash = hashNumeric;
                                        packetSendMemPoolTransactionVote.PacketNumericSignature = numericSignature;

                                        await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                        {
                                            PacketOrder = ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_VOTE,
                                            PacketContent = JsonConvert.SerializeObject(packetSendMemPoolTransactionVote),
                                        }, false);
                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                    }
                                }
                                else
                                {

                                    await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_TIMESTAMP,
                                        PacketContent = string.Empty,
                                    }, false);

                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.INVALID_PEER_PACKET_ENCRYPTION,
                                    PacketContent = string.Empty,
                                }, false);

                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_DISCONNECT_REQUEST: // Ask to disconnect propertly from the peer.
                        {
                            _clientAskDisconnection = true;

                            if (packetSendObject.PacketContent != _peerNetworkSettingObject.ListenIp && packetSendObject.PacketContent != _peerServerOpenNatIp)
                            {
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_TYPE_PACKET;
                            }

                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                            {
                                PacketOrder = ClassPeerEnumPacketResponse.SEND_DISCONNECT_CONFIRMATION,
                                PacketContent = packetSendObject.PacketContent,
                            }, false))
                            {
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_KEEP_ALIVE:
                        {
                            if (!ClassUtility.TryDeserialize(packetSendObject.PacketContent, out ClassPeerPacketAskKeepAlive packetAskKeepAlive, ObjectCreationHandling.Reuse))
                            {
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }

                            if (packetAskKeepAlive != null)
                            {
                                if (!ClassUtility.CheckPacketTimestamp(packetAskKeepAlive.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_MEM_POOL_BROADCAST_MODE: // Ask broadcast mode. No packet data required.
                        {
                            if (!_enableMemPoolBroadcastClientMode)
                            {
                                _enableMemPoolBroadcastClientMode = true;
                            }
                            // Disable broadcast mode.
                            else
                            {
                                _enableMemPoolBroadcastClientMode = false;
                            }

                            ClassPeerPacketSendBroadcastMemPoolResponse packetSendBroadcastMemPoolResponse = new ClassPeerPacketSendBroadcastMemPoolResponse()
                            {
                                Status = _enableMemPoolBroadcastClientMode,
                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                            };

                            // Do not encrypt packet.
                            if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                            {
                                PacketOrder = ClassPeerEnumPacketResponse.SEND_MEM_POOL_BROADCAST_RESPONSE,
                                PacketContent = JsonConvert.SerializeObject(packetSendBroadcastMemPoolResponse),
                            }, false))
                            {
                                _enableMemPoolBroadcastClientMode = false;
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_MEM_POOl_BLOCK_HEIGHT_LIST_BROADCAST_MODE:
                        {
                            ClassPeerPacketSendAskMemPoolBlockHeightList packetMemPoolAskBlockHeightList = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskMemPoolBlockHeightList>(packetSendObject.PacketContent);

                            if (packetMemPoolAskBlockHeightList != null)
                            {
                                if (!ClassUtility.CheckPacketTimestamp(packetMemPoolAskBlockHeightList.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }

                                SortedList<long, int> listMemPoolBlockHeightAndCount = new SortedList<long, int>(); // Block height | Tx count.

                                List<long> listMemPoolBlockHeight = await ClassMemPoolDatabase.GetMemPoolListBlockHeight(_cancellationTokenAccessData);
                                if (listMemPoolBlockHeight.Count > 0)
                                {
                                    foreach (long blockHeight in listMemPoolBlockHeight)
                                    {
                                        int txCount = await ClassMemPoolDatabase.GetCountMemPoolTxFromBlockHeight(blockHeight, _cancellationTokenAccessData);

                                        if (txCount > 0)
                                        {
                                            listMemPoolBlockHeightAndCount.Add(blockHeight, txCount);
                                        }
                                    }
                                }

                                ClassPeerPacketSendMemPoolBlockHeightList packetSendMemPoolBlockHeightList = new ClassPeerPacketSendMemPoolBlockHeightList()
                                {
                                    MemPoolBlockHeightListAndCount = listMemPoolBlockHeightAndCount,
                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                };

                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                {
                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MEM_POOL_BLOCK_HEIGHT_LIST_BROADCAST_MODE,
                                    PacketContent = JsonConvert.SerializeObject(packetSendMemPoolBlockHeightList),
                                }, true))
                                {
                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                }
                            }
                            else
                            {
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_MEM_POOL_TRANSACTION_BROADCAST_CONFIRMATION_RECEIVED:
                        {

                            if (!ClassUtility.TryDeserialize(packetSendObject.PacketContent, out ClassPeerPacketAskMemPoolTransactionBroadcastConfirmationReceived packetAskMemPoolBroadcastTransactionConfirmationReceived, ObjectCreationHandling.Reuse))
                            {
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }

                            if (packetAskMemPoolBroadcastTransactionConfirmationReceived != null)
                            {
                                if (!ClassUtility.CheckPacketTimestamp(packetAskMemPoolBroadcastTransactionConfirmationReceived.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }

                                if (_onWaitingMemPoolTransactionConfirmationReceived)
                                {
                                    _onWaitingMemPoolTransactionConfirmationReceived = false;
                                }
                                else
                                {
                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }
                            }
                            else
                            {
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }

                        }
                        break;
                    case ClassPeerEnumPacketSend.ASK_MEM_POOl_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE:
                        {
                            ClassPeerPacketSendAskMemPoolTransactionList packetMemPoolAskMemPoolTransactionList = await DecryptDeserializePacketContentPeer<ClassPeerPacketSendAskMemPoolTransactionList>(packetSendObject.PacketContent);

                            if (packetMemPoolAskMemPoolTransactionList != null)
                            {
                                if (!ClassUtility.CheckPacketTimestamp(packetMemPoolAskMemPoolTransactionList.PacketTimestamp, _peerNetworkSettingObject.PeerMaxTimestampDelayPacket, _peerNetworkSettingObject.PeerMaxEarlierPacketDelay))
                                {
                                    return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                                }

                                bool doBroadcast = false;

                                if (!_onSendingMemPoolTransaction)
                                {
                                    int countMemPoolTx = await ClassMemPoolDatabase.GetCountMemPoolTxFromBlockHeight(packetMemPoolAskMemPoolTransactionList.BlockHeight, _cancellationTokenAccessData);

                                    if (countMemPoolTx > 0)
                                    {
                                        if (countMemPoolTx > packetMemPoolAskMemPoolTransactionList.TotalTransactionProgress)
                                        {
                                            _onSendingMemPoolTransaction = true;
                                            doBroadcast = true;

                                            try
                                            {
                                                // Enable a task of broadcasting transactions from the MemPool, await after each sending a confirmation. 
                                                await Task.Factory.StartNew(async () =>
                                                {
                                                    int countMemPoolTxSent = 0;
                                                    bool exceptionOnSending = false;

                                                    using (DisposableList<ClassTransactionObject> listTransaction = new DisposableList<ClassTransactionObject>(false, 0, await ClassMemPoolDatabase.GetMemPoolTxObjectFromBlockHeight(packetMemPoolAskMemPoolTransactionList.BlockHeight, _cancellationTokenAccessData)))
                                                    {
                                                        List<ClassTransactionObject> listToSend = new List<ClassTransactionObject>();

                                                        int currentProgress = 0;

                                                        if (_listMemPoolBroadcastBlockHeight.ContainsKey(packetMemPoolAskMemPoolTransactionList.BlockHeight))
                                                        {
                                                            currentProgress = _listMemPoolBroadcastBlockHeight[packetMemPoolAskMemPoolTransactionList.BlockHeight];
                                                        }
                                                        else
                                                        {
                                                            _listMemPoolBroadcastBlockHeight.Add(packetMemPoolAskMemPoolTransactionList.BlockHeight, 0);
                                                        }

                                                        if (currentProgress < packetMemPoolAskMemPoolTransactionList.TotalTransactionProgress)
                                                        {
                                                            currentProgress = packetMemPoolAskMemPoolTransactionList.TotalTransactionProgress;
                                                        }

                                                        foreach (ClassTransactionObject transactionObject in listTransaction.GetAll.Skip(currentProgress))
                                                        {
                                                            if (transactionObject != null)
                                                            {
                                                                if (transactionObject.TransactionType == ClassTransactionEnumType.NORMAL_TRANSACTION || transactionObject.TransactionType == ClassTransactionEnumType.TRANSFER_TRANSACTION)
                                                                {
                                                                    listToSend.Add(transactionObject);

                                                                    if (listToSend.Count >= _peerNetworkSettingObject.PeerMaxRangeTransactionToSyncPerRequest)
                                                                    {
                                                                        ClassPeerPacketSendMemPoolTransaction packetSendMemPoolTransaction = new ClassPeerPacketSendMemPoolTransaction()
                                                                        {
                                                                            ListTransactionObject = listToSend,
                                                                            PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                        };

                                                                        _onWaitingMemPoolTransactionConfirmationReceived = true;

                                                                        if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                        {
                                                                            PacketOrder = ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE,
                                                                            PacketContent = JsonConvert.SerializeObject(packetSendMemPoolTransaction),
                                                                        }, true))
                                                                        {
                                                                            exceptionOnSending = true;
                                                                            ClientPeerConnectionStatus = false;
                                                                            break;
                                                                        }

                                                                        if (!exceptionOnSending)
                                                                        {
                                                                            long timestampStartWaitingResponse = ClassUtility.GetCurrentTimestampInMillisecond();
                                                                            while (_onWaitingMemPoolTransactionConfirmationReceived)
                                                                            {
                                                                                if (timestampStartWaitingResponse + (_peerNetworkSettingObject.PeerMaxDelayAwaitResponse * 1000) < ClassUtility.GetCurrentTimestampInMillisecond())
                                                                                {
                                                                                    break;
                                                                                }

                                                                                try
                                                                                {
                                                                                    await Task.Delay(100, _cancellationTokenAccessData.Token);
                                                                                }
                                                                                catch
                                                                                {
                                                                                    break;
                                                                                }
                                                                            }

                                                                            if (!_onWaitingMemPoolTransactionConfirmationReceived)
                                                                            {
                                                                                countMemPoolTxSent += listToSend.Count;
                                                                            }
                                                                            else
                                                                            {
                                                                                exceptionOnSending = true;
                                                                                break;
                                                                            }
                                                                        }

                                                                        listToSend.Clear();
                                                                    }
                                                                }
                                                            }
                                                        }

                                                        if (!exceptionOnSending)
                                                        {
                                                            if (listToSend.Count > 0)
                                                            {
                                                                ClassPeerPacketSendMemPoolTransaction packetSendMemPoolTransaction = new ClassPeerPacketSendMemPoolTransaction()
                                                                {
                                                                    ListTransactionObject = listToSend,
                                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                };

                                                                _onWaitingMemPoolTransactionConfirmationReceived = true;

                                                                if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                                {
                                                                    PacketOrder = ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE,
                                                                    PacketContent = JsonConvert.SerializeObject(packetSendMemPoolTransaction),
                                                                }, true))
                                                                {
                                                                    exceptionOnSending = true;
                                                                    ClientPeerConnectionStatus = false;
                                                                }

                                                                if (!exceptionOnSending)
                                                                {
                                                                    long timestampStartWaitingResponse = ClassUtility.GetCurrentTimestampInMillisecond();
                                                                    while (_onWaitingMemPoolTransactionConfirmationReceived)
                                                                    {
                                                                        if (timestampStartWaitingResponse + (_peerNetworkSettingObject.PeerMaxDelayAwaitResponse * 1000) < ClassUtility.GetCurrentTimestampInMillisecond())
                                                                        {
                                                                            break;
                                                                        }

                                                                        try
                                                                        {
                                                                            await Task.Delay(100, _cancellationTokenAccessData.Token);
                                                                        }
                                                                        catch
                                                                        {
                                                                            break;
                                                                        }
                                                                    }

                                                                    if (!_onWaitingMemPoolTransactionConfirmationReceived)
                                                                    {
                                                                        countMemPoolTxSent += listToSend.Count;
                                                                    }
                                                                    else
                                                                    {
                                                                        exceptionOnSending = true;
                                                                    }
                                                                }
                                                            }
                                                        }

                                                        listToSend.Clear();
                                                    }

                                                    if (exceptionOnSending)
                                                    {
                                                        ClientPeerConnectionStatus = false;
                                                    }
                                                    else
                                                    {
                                                        // End broadcast transaction. Packet not encrypted, no content.
                                                        if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                                        {
                                                            PacketOrder = ClassPeerEnumPacketResponse.SEND_MEM_POOL_END_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE,
                                                            PacketContent = string.Empty,
                                                        }, false))
                                                        {
                                                            ClientPeerConnectionStatus = false;
                                                        }
                                                        else
                                                        {
                                                            _listMemPoolBroadcastBlockHeight[packetMemPoolAskMemPoolTransactionList.BlockHeight] += countMemPoolTxSent;
                                                        }
                                                    }
                                                    _onSendingMemPoolTransaction = false;

                                                }, _cancellationTokenAccessData.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                                            }
                                            catch
                                            {
                                                _onSendingMemPoolTransaction = false;
                                                // Ignored, catch the exception once broadcast task is cancelled.
                                            }

                                            return ClassPeerNetworkClientServerHandlePacketEnumStatus.VALID_PACKET;
                                        }
                                    }
                                }

                                if (!doBroadcast && !_onSendingMemPoolTransaction)
                                {
                                    // End broadcast transaction. Packet not encrypted, no content.
                                    if (!await SendPacketToPeer(new ClassPeerPacketRecvObject(_peerNetworkSettingObject.PeerUniqueId)
                                    {
                                        PacketOrder = ClassPeerEnumPacketResponse.SEND_MEM_POOL_END_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE,
                                        PacketContent = string.Empty,
                                    }, false))
                                    {
                                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.SEND_EXCEPTION_PACKET;
                                    }
                                }
                            }
                            else
                            {
                                return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_PACKET;
                            }
                        }
                        break;
                    default:
                        ClassLog.WriteLine("Invalid packet type received from: " + _peerClientIp + " | Content: " + packet, ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                        return ClassPeerNetworkClientServerHandlePacketEnumStatus.INVALID_TYPE_PACKET;
                }
            }
            catch
            {
                ClassLog.WriteLine("Invalid packet received from: " + _peerClientIp + " | Content: " + packet, ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                return ClassPeerNetworkClientServerHandlePacketEnumStatus.EXCEPTION_PACKET;
            }

            return ClassPeerNetworkClientServerHandlePacketEnumStatus.VALID_PACKET;
        }

        #endregion

        #region Send peer packet response.

        /// <summary>
        /// Send a packet to a peer.
        /// </summary>
        /// <param name="packetSendObject">The packet object to send.</param>
        /// <param name="encrypted">Indicate if the packet require encryption.</param>
        /// <returns>Return the status of the sending of the packet.</returns>
        private async Task<bool> SendPacketToPeer(ClassPeerPacketRecvObject packetSendObject, bool encrypted)
        {
            bool packetSendStatus = true;

            try
            {
                if (encrypted)
                {
                    byte[] packetContentEncrypted;
                    if (ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].GetClientCryptoStreamObject != null)
                    {
                        packetContentEncrypted = await ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].GetClientCryptoStreamObject.EncryptDataProcess(ClassUtility.GetByteArrayFromStringAscii(packetSendObject.PacketContent), _cancellationTokenAccessData);
                    }
                    else
                    {
                        if (!ClassAes.EncryptionProcess(ClassUtility.GetByteArrayFromStringAscii(packetSendObject.PacketContent), ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerClientPacketEncryptionKey, ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerClientPacketEncryptionKeyIv, out packetContentEncrypted))
                        {
                            _onSendingPacketResponse = false;
                            return false;
                        }
                    }

                    if (packetContentEncrypted?.Length > 0)
                    {
                        packetSendObject.PacketContent = Convert.ToBase64String(packetContentEncrypted);
                        packetSendObject.PacketHash = ClassSha.MakeBigShaHashFromBigData(ClassUtility.GetByteArrayFromStringAscii(packetSendObject.PacketContent), _cancellationTokenAccessData);

                        if (ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].GetClientCryptoStreamObject != null)
                        {
                            packetSendObject.PacketSignature = ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].GetClientCryptoStreamObject.DoSignatureProcess(packetSendObject.PacketHash, ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerInternPrivateKey);
                        }
                        else
                        {
                            packetSendObject.PacketSignature = ClassWalletUtility.WalletGenerateSignature(ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerInternPrivateKey, packetSendObject.PacketHash);
                        }

                        byte[] packetBytesToSend = ClassUtility.GetByteArrayFromStringAscii(Convert.ToBase64String(ClassUtility.GetByteArrayFromStringAscii(JsonConvert.SerializeObject(packetSendObject))) + ClassPeerPacketSetting.PacketPeerSplitSeperator);

                        // Clean up.
                        Array.Clear(packetContentEncrypted, 0, packetContentEncrypted.Length);

                        using (NetworkStream networkStream = new NetworkStream(_tcpClientPeer.Client))
                        {
                            if (!await networkStream.TrySendSplittedPacket(packetBytesToSend, _cancellationTokenAccessData, _peerNetworkSettingObject.PeerMaxPacketSplitedSendSize))
                            {
                                _onSendingPacketResponse = false;
                                packetSendStatus = false;
                            }
                        }
                    }
                }
                else
                {
                    packetSendObject.PacketHash = ClassSha.MakeBigShaHashFromBigData(ClassUtility.GetByteArrayFromStringAscii(packetSendObject.PacketContent), _cancellationTokenAccessData);
                    if (ClassPeerDatabase.DictionaryPeerDataObject.ContainsKey(_peerClientIp))
                    {
                        if (ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp].ContainsKey(_peerUniqueId))
                        {
                            packetSendObject.PacketSignature = ClassWalletUtility.WalletGenerateSignature(ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerInternPrivateKey, packetSendObject.PacketHash);
                        }
                        else
                        {
                            _onSendingPacketResponse = false;
                            return false;
                        }
                    }
                    else
                    {
                        _onSendingPacketResponse = false;
                        return false;
                    }

                    byte[] packetBytesToSend = ClassUtility.GetByteArrayFromStringAscii(Convert.ToBase64String(ClassUtility.GetByteArrayFromStringAscii(JsonConvert.SerializeObject(packetSendObject))) + ClassPeerPacketSetting.PacketPeerSplitSeperator);

                    using (NetworkStream networkStream = new NetworkStream(_tcpClientPeer.Client))
                    {
                        if (!await networkStream.TrySendSplittedPacket(packetBytesToSend, _cancellationTokenAccessData, _peerNetworkSettingObject.PeerMaxPacketSplitedSendSize))
                        {
                            _onSendingPacketResponse = false;
                            packetSendStatus = false;
                        }
                    }

                }
            }
            catch
            {
                _onSendingPacketResponse = false;
                return false;
            }

            _onSendingPacketResponse = false;
            return packetSendStatus;
        }


        #endregion

        #region Sign packet with the private key signature of the peer.

        /// <summary>
        /// Sign packet with the numeric private key of the peer.
        /// </summary>
        /// <typeparam name="T">Type of the content to handle.</typeparam>
        /// <param name="content">The content to serialise and to hash.</param>
        /// <param name="numericHash">The hash generated returned.</param>
        /// <param name="numericSignature">The signature generated returned.</param>
        private void SignPacketWithNumericPrivateKey<T>(T content, out string numericHash, out string numericSignature)
        {
            numericHash = ClassSha.MakeBigShaHashFromBigData(ClassUtility.GetByteArrayFromStringAscii(JsonConvert.SerializeObject(content)), _cancellationTokenAccessData);
            numericSignature = ClassWalletUtility.WalletGenerateSignature(_peerNetworkSettingObject.PeerNumericPrivateKey, numericHash);
        }

        #endregion

        #region Check/Decrypt packet from peer.

        /// <summary>
        /// Decrypt and deserialize packet content received from a peer.
        /// </summary>
        /// <typeparam name="T">Type of the content to return.</typeparam>
        /// <param name="packetContentCrypted">The packet content to decrypt.</param>
        /// <returns></returns>
        private async Task<T> DecryptDeserializePacketContentPeer<T>(string packetContentCrypted)
        {
            T packetContentResult = default;


            byte[] decryptedPacket = await DecryptContentPacketPeer(packetContentCrypted);

            if (decryptedPacket != null)
            {

                packetContentResult = JsonConvert.DeserializeObject<T>(decryptedPacket.GetStringFromByteArrayAscii());

                // Clean up.
                Array.Clear(decryptedPacket, 0, decryptedPacket.Length);
            }

            return packetContentResult;
        }

        /// <summary>
        /// Check peer packet content signature and hash.
        /// </summary>
        /// <param name="packetSendObject">The packet object received to check.</param>
        /// <returns>Return if the signature provided is valid.</returns>
        private bool CheckContentPacketSignaturePeer(ClassPeerPacketSendObject packetSendObject)
        {
            bool exist = false;
            if (ClassPeerDatabase.ContainsPeer(_peerClientIp, _peerUniqueId))
            {
                exist = true;
                if (ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerClientPublicKey.IsNullOrEmpty() || packetSendObject.PacketContent.IsNullOrEmpty() || packetSendObject.PacketHash.IsNullOrEmpty())
                {
                    ClassLog.WriteLine("Packet received to check from peer " + _peerClientIp + " is invalid. The public key of the peer, or the date sent are empty.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_HIGH_PRIORITY);

                    return false;
                }

                if (ClassPeerCheckManager.CheckPeerClientWhitelistStatus(_peerClientIp, _peerUniqueId, _peerNetworkSettingObject))
                {
                    return true;
                }
                if (ClassSha.MakeBigShaHashFromBigData(ClassUtility.GetByteArrayFromStringAscii(packetSendObject.PacketContent + packetSendObject.PacketOrder), _cancellationTokenAccessData) == packetSendObject.PacketHash)
                {
                    if (ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].GetClientCryptoStreamObject.CheckSignatureProcess(packetSendObject.PacketHash, packetSendObject.PacketSignature, ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerClientPublicKey))
                    {
                        ClassLog.WriteLine("Signature of packet received from peer " + _peerClientIp + " is valid. Hash: " + packetSendObject.PacketHash + " Signature: " + packetSendObject.PacketSignature, ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);

                        return true;
                    }
                    ClassLog.WriteLine("Signature of packet received from peer " + _peerClientIp + " is invalid. Public Key of the peer: " + ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerClientPublicKey, ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_HIGH_PRIORITY);
                }
                else
                {
                    ClassLog.WriteLine("Hash generated from packet content of peer " + _peerClientIp + " is different of the packet hash received.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                }

            }
            if (!exist)
            {
                if (ClassSha.MakeBigShaHashFromBigData(ClassUtility.GetByteArrayFromStringAscii(packetSendObject.PacketContent + packetSendObject.PacketOrder), _cancellationTokenAccessData) == packetSendObject.PacketHash)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Decrypt encrypted packet content received from a peer.
        /// </summary>
        /// <param name="content">The content encrypted.</param>
        /// <returns>Indicate if the decryption has been done successfully.</returns>
        private async Task<byte[]> DecryptContentPacketPeer(string content)
        {
            if (ClassPeerDatabase.DictionaryPeerDataObject.ContainsKey(_peerClientIp))
            {
                if (ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp].ContainsKey(_peerUniqueId))
                {
                    if (ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].GetClientCryptoStreamObject != null)
                    {
                        var contentDecryptedTuple = await ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].GetClientCryptoStreamObject.DecryptDataProcess(Convert.FromBase64String(content), _cancellationTokenAccessData);

                        if (contentDecryptedTuple != null)
                        {
                            if (contentDecryptedTuple.Item2 && contentDecryptedTuple.Item1 != null)
                            {
                                return contentDecryptedTuple.Item1;
                            }
                        }
                    }
                    else
                    {
                        if (ClassAes.DecryptionProcess(Convert.FromBase64String(content), ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerClientPacketEncryptionKey, ClassPeerDatabase.DictionaryPeerDataObject[_peerClientIp][_peerUniqueId].PeerClientPacketEncryptionKeyIv, out byte[] contentDecrypted))
                        {
                            return contentDecrypted;
                        }
                    }
                }
            }
            //ClassLog.WriteLine("Can't decrypt content packet from peer " + _peerClientIp + " | Content to decrypt: " + content, ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_LOWEST_PRIORITY);
            return null;
        }

        #endregion
    }
}

using Newtonsoft.Json;
using SeguraChain_Lib.Blockchain.Database;
using SeguraChain_Lib.Blockchain.MemPool.Database;
using SeguraChain_Lib.Blockchain.Transaction.Enum;
using SeguraChain_Lib.Blockchain.Transaction.Utility;
using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ClientSync.Function;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Request;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Response;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Instance.Node.Network.Database;
using SeguraChain_Lib.Blockchain.Transaction.Object;
using SeguraChain_Lib.Instance.Node.Network.Database.Manager;
using SeguraChain_Lib.Blockchain.Setting;
using System.Diagnostics;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.Model;

namespace SeguraChain_Lib.Instance.Node.Network.Services.P2P.Broadcast
{
    public class ClassPeerNetworkBroadcastInstanceMemPool
    {
        private Dictionary<string, Dictionary<string, ClassPeerNetworkClientBroadcastMemPool>> _listPeerNetworkClientBroadcastMemPoolReceiver; // Peer IP | [Peer Unique ID | Client broadcast object]
        private Dictionary<string, Dictionary<string, ClassPeerNetworkClientBroadcastMemPool>> _listPeerNetworkClientBroadcastMemPoolSender; // Peer IP | [Peer Unique ID | Client broadcast object]
        private ClassPeerNetworkSettingObject _peerNetworkSettingObject;
        private ClassPeerFirewallSettingObject _peerFirewallSettingObject;
        private CancellationTokenSource _cancellation;
        private string _peerOpenNatIp;
        private bool _peerNetworkBroadcastInstanceMemPoolStatus;

        /// <summary>
        /// Constructor.
        /// </summary>
        public ClassPeerNetworkBroadcastInstanceMemPool(ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject peerFirewallSettingObject, string peerOpenNatIp)
        {
            _listPeerNetworkClientBroadcastMemPoolReceiver = new Dictionary<string, Dictionary<string, ClassPeerNetworkClientBroadcastMemPool>>();
            _listPeerNetworkClientBroadcastMemPoolSender = new Dictionary<string, Dictionary<string, ClassPeerNetworkClientBroadcastMemPool>>();
            _peerNetworkSettingObject = peerNetworkSettingObject;
            _peerFirewallSettingObject = peerFirewallSettingObject;
            _peerOpenNatIp = peerOpenNatIp;
        }

        /// <summary>
        /// Run the network broadcast mempool instance.
        /// </summary>
        public void RunNetworkBroadcastMemPoolInstanceTask()
        {
            _cancellation = new CancellationTokenSource();
            _peerNetworkBroadcastInstanceMemPoolStatus = true;

            try
            {
                Task.Factory.StartNew(async () =>
                {
                    while (_peerNetworkBroadcastInstanceMemPoolStatus)
                    {

                        #region Generate sender/receiver broadcast instances by targetting public peers.

                        if (ClassPeerDatabase.DictionaryPeerDataObject.Count > 0)
                        {
                            foreach (string peerIpTarget in ClassPeerDatabase.DictionaryPeerDataObject.Keys.ToArray())
                            {
                                _cancellation.Token.ThrowIfCancellationRequested();

                                if (!peerIpTarget.IsNullOrEmpty())
                                {
                                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget].Count > 0)
                                    {
                                        foreach (string peerUniqueIdTarget in ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget].Keys.ToArray())
                                        {
                                            _cancellation.Token.ThrowIfCancellationRequested();

                                            bool success = false;

                                            if (!peerUniqueIdTarget.IsNullOrEmpty())
                                            {
                                                if (ClassPeerCheckManager.CheckPeerClientStatus(peerIpTarget, peerUniqueIdTarget, false, _peerNetworkSettingObject, _peerFirewallSettingObject))
                                                {
                                                    if (ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget][peerUniqueIdTarget].PeerIsPublic)
                                                    {
                                                        int peerPortTarget = ClassPeerDatabase.DictionaryPeerDataObject[peerIpTarget][peerUniqueIdTarget].PeerPort;


                                                        // Build receiver instance.
                                                        if (!_listPeerNetworkClientBroadcastMemPoolReceiver.ContainsKey(peerIpTarget))
                                                        {
                                                            _listPeerNetworkClientBroadcastMemPoolReceiver.Add(peerIpTarget, new Dictionary<string, ClassPeerNetworkClientBroadcastMemPool>());
                                                        }

                                                        if (!_listPeerNetworkClientBroadcastMemPoolReceiver[peerIpTarget].ContainsKey(peerUniqueIdTarget))
                                                        {
                                                            _listPeerNetworkClientBroadcastMemPoolReceiver[peerIpTarget].Add(peerUniqueIdTarget, new ClassPeerNetworkClientBroadcastMemPool(peerIpTarget,
                                                                                                                                                   peerUniqueIdTarget,
                                                                                                                                                   peerPortTarget,
                                                                                                                                                   false,
                                                                                                                                                   _peerNetworkSettingObject,
                                                                                                                                                   _peerFirewallSettingObject));

                                                            if (!await RunPeerNetworkClientBroadcastMemPool(peerIpTarget, peerUniqueIdTarget, false))
                                                            {
                                                                _listPeerNetworkClientBroadcastMemPoolReceiver[peerIpTarget][peerUniqueIdTarget].StopTaskAndDisconnect();
                                                                _listPeerNetworkClientBroadcastMemPoolReceiver[peerIpTarget].Remove(peerUniqueIdTarget);
                                                            }
                                                            else
                                                            {
                                                                success = true;
                                                            }
                                                        }

                                                        // Build sender instance if the node is in public node.
                                                        if (success && _peerNetworkSettingObject.PublicPeer)
                                                        {
                                                            if (!_listPeerNetworkClientBroadcastMemPoolSender.ContainsKey(peerIpTarget))
                                                            {
                                                                _listPeerNetworkClientBroadcastMemPoolSender.Add(peerIpTarget, new Dictionary<string, ClassPeerNetworkClientBroadcastMemPool>());
                                                            }

                                                            if (!_listPeerNetworkClientBroadcastMemPoolSender[peerIpTarget].ContainsKey(peerUniqueIdTarget))
                                                            {
                                                                _listPeerNetworkClientBroadcastMemPoolSender[peerIpTarget].Add(peerUniqueIdTarget, new ClassPeerNetworkClientBroadcastMemPool(peerIpTarget,
                                                                                                                                                       peerUniqueIdTarget,
                                                                                                                                                       peerPortTarget,
                                                                                                                                                       true,
                                                                                                                                                       _peerNetworkSettingObject,
                                                                                                                                                       _peerFirewallSettingObject));

                                                                if (!await RunPeerNetworkClientBroadcastMemPool(peerIpTarget, peerUniqueIdTarget, true))
                                                                {
                                                                    _listPeerNetworkClientBroadcastMemPoolSender[peerIpTarget][peerUniqueIdTarget].StopTaskAndDisconnect();
                                                                    _listPeerNetworkClientBroadcastMemPoolSender[peerIpTarget].Remove(peerUniqueIdTarget);
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

                        #endregion

                        #region Check Client MemPool broadcast launched.

                        // Check receiver mode instances.
                        if (_listPeerNetworkClientBroadcastMemPoolReceiver.Count > 0)
                        {
                            foreach (string peerIp in _listPeerNetworkClientBroadcastMemPoolReceiver.Keys.ToArray())
                            {
                                _cancellation.Token.ThrowIfCancellationRequested();

                                if (_listPeerNetworkClientBroadcastMemPoolReceiver[peerIp].Count > 0)
                                {
                                    foreach (string peerUniqueId in _listPeerNetworkClientBroadcastMemPoolReceiver[peerIp].Keys.ToArray())
                                    {
                                        _cancellation.Token.ThrowIfCancellationRequested();

                                        if (!_listPeerNetworkClientBroadcastMemPoolReceiver[peerIp][peerUniqueId].IsAlive)
                                        {
                                            _listPeerNetworkClientBroadcastMemPoolReceiver[peerIp][peerUniqueId].StopTaskAndDisconnect();

                                            if (!await RunPeerNetworkClientBroadcastMemPool(peerIp, peerUniqueId, false))
                                            {
                                                _listPeerNetworkClientBroadcastMemPoolReceiver[peerIp][peerUniqueId].Dispose();
                                                _listPeerNetworkClientBroadcastMemPoolReceiver[peerIp].Remove(peerUniqueId);
                                            }
                                        }
                                    }
                                }

                                if (_listPeerNetworkClientBroadcastMemPoolReceiver[peerIp].Count == 0)
                                {
                                    _listPeerNetworkClientBroadcastMemPoolReceiver.Remove(peerIp);
                                }
                            }
                        }

                        // Check sender mode instances if the node is in public mode.
                        if (_peerNetworkSettingObject.PublicPeer)
                        {
                            if (_listPeerNetworkClientBroadcastMemPoolSender.Count > 0)
                            {
                                foreach (string peerIp in _listPeerNetworkClientBroadcastMemPoolSender.Keys.ToArray())
                                {
                                    _cancellation.Token.ThrowIfCancellationRequested();

                                    if (_listPeerNetworkClientBroadcastMemPoolSender[peerIp].Count > 0)
                                    {
                                        foreach (string peerUniqueId in _listPeerNetworkClientBroadcastMemPoolSender[peerIp].Keys.ToArray())
                                        {
                                            _cancellation.Token.ThrowIfCancellationRequested();

                                            if (!_listPeerNetworkClientBroadcastMemPoolSender[peerIp][peerUniqueId].IsAlive)
                                            {
                                                _listPeerNetworkClientBroadcastMemPoolSender[peerIp][peerUniqueId].StopTaskAndDisconnect();

                                                if (!await RunPeerNetworkClientBroadcastMemPool(peerIp, peerUniqueId, true))
                                                {
                                                    _listPeerNetworkClientBroadcastMemPoolSender[peerIp][peerUniqueId].Dispose();
                                                    _listPeerNetworkClientBroadcastMemPoolSender[peerIp].Remove(peerUniqueId);
                                                }
                                            }
                                        }
                                    }

                                    if (_listPeerNetworkClientBroadcastMemPoolSender[peerIp].Count == 0)
                                    {
                                        _listPeerNetworkClientBroadcastMemPoolSender.Remove(peerIp);
                                    }
                                }
                            }
                        }

                        #endregion

                        await Task.Delay(1000, _cancellation.Token);
                    }
                }, _cancellation.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Ignored catch the exception once the task is cancelled.
            }
        }

        /// <summary>
        /// Run a peer network client broadcast mempool.
        /// </summary>
        /// <param name="peerIpTarget"></param>
        /// <param name="peerUniqueIdTarget"></param>
        /// <returns></returns>
        private async Task<bool> RunPeerNetworkClientBroadcastMemPool(string peerIpTarget, string peerUniqueIdTarget, bool onSendingMode)
        {
            if (!onSendingMode)
            {
                if (await _listPeerNetworkClientBroadcastMemPoolReceiver[peerIpTarget][peerUniqueIdTarget].TryConnect(_cancellation))
                {

                    if (await _listPeerNetworkClientBroadcastMemPoolReceiver[peerIpTarget][peerUniqueIdTarget].TryAskBroadcastMode())
                    {
                        _listPeerNetworkClientBroadcastMemPoolReceiver[peerIpTarget][peerUniqueIdTarget].RunBroadcastTransactionTask();

                        return true;
                    }

                }
            }
            else
            {
                if (await _listPeerNetworkClientBroadcastMemPoolSender[peerIpTarget][peerUniqueIdTarget].TryConnect(_cancellation))
                { 
                        if (await _listPeerNetworkClientBroadcastMemPoolSender[peerIpTarget][peerUniqueIdTarget].TryAskBroadcastMode())
                        {
                            _listPeerNetworkClientBroadcastMemPoolSender[peerIpTarget][peerUniqueIdTarget].RunBroadcastTransactionTask();

                            return true;
                        }
                    
                }
            }

            return false;
        }

        /// <summary>
        /// Stop the network broadcast mempool instance.
        /// </summary>
        public void StopNetworkBroadcastMemPoolInstance()
        {
            _peerNetworkBroadcastInstanceMemPoolStatus = false;

            if (!_cancellation.IsCancellationRequested)
            {
                _cancellation.Cancel();
            }

            #region Clean client broadcast instances.

            // Clean receiver mode instances.
            if (_listPeerNetworkClientBroadcastMemPoolReceiver.Count > 0)
            {
                foreach (string peerIp in _listPeerNetworkClientBroadcastMemPoolReceiver.Keys.ToArray())
                {
                    try
                    {
                        if (_listPeerNetworkClientBroadcastMemPoolReceiver[peerIp].Count > 0)
                        {
                            foreach (string peerUniqueId in _listPeerNetworkClientBroadcastMemPoolReceiver[peerIp].Keys.ToArray())
                            {
                                try
                                {
                                    _listPeerNetworkClientBroadcastMemPoolReceiver[peerIp][peerUniqueId].StopTaskAndDisconnect();
                                }
                                catch
                                {
                                    // Ignored.
                                }
                            }

                            try
                            {
                                _listPeerNetworkClientBroadcastMemPoolReceiver[peerIp].Clear();
                            }
                            catch
                            {
                                // Ignored.
                            }
                        }
                    }
                    catch
                    {
                        // Ignored.
                    }
                }

                _listPeerNetworkClientBroadcastMemPoolReceiver.Clear();
            }

            // Clean sender mode instances.
            if (_listPeerNetworkClientBroadcastMemPoolSender.Count > 0)
            {
                foreach (string peerIp in _listPeerNetworkClientBroadcastMemPoolSender.Keys.ToArray())
                {
                    try
                    {
                        if (_listPeerNetworkClientBroadcastMemPoolSender[peerIp].Count > 0)
                        {
                            foreach (string peerUniqueId in _listPeerNetworkClientBroadcastMemPoolSender[peerIp].Keys.ToArray())
                            {
                                try
                                {
                                    _listPeerNetworkClientBroadcastMemPoolSender[peerIp][peerUniqueId].StopTaskAndDisconnect();
                                }
                                catch
                                {
                                    // Ignored.
                                }
                            }

                            try
                            {
                                _listPeerNetworkClientBroadcastMemPoolSender[peerIp].Clear();
                            }
                            catch
                            {
                                // Ignored.
                            }
                        }
                    }
                    catch
                    {
                        // Ignored.
                    }
                }

                _listPeerNetworkClientBroadcastMemPoolSender.Clear();
            }

            #endregion
        }

        /// <summary>
        /// Peer broadcast client mempool transaction object.
        /// </summary>
        internal class ClassPeerNetworkClientBroadcastMemPool : ClassPeerSyncFunction, IDisposable
        {
            public bool IsAlive;
            private TcpClient _peerTcpClient;
            private string _peerIpTarget;
            private string _peerUniqueIdTarget;
            private int _peerPortTarget;
            private ClassPeerNetworkSettingObject _peerNetworkSettingObject;
            private ClassPeerFirewallSettingObject _peerFirewallSettingObject;
            private CancellationTokenSource _peerCancellationToken;
            private CancellationTokenSource _peerCheckConnectionCancellationToken;
            private Dictionary<long, HashSet<string>> _memPoolListBlockHeightTransactionReceived;
            private Dictionary<long, HashSet<string>> _memPoolListBlockHeightTransactionSend;
            private List<ClassReadPacketSplitted> _listPacketReceived;
            private MemoryStream _memoryStream;
            private bool _onSendBroadcastMode;

            /// <summary>
            /// Constructor.
            /// </summary>
            /// <param name="peerIpTarget"></param>
            /// <param name="peerUniqueIdTarget"></param>
            /// <param name="peerPortTarget"></param>
            /// <param name="peerNetworkSettingObject"></param>
            /// <param name="peerFirewallSettingObject"></param>
            public ClassPeerNetworkClientBroadcastMemPool(string peerIpTarget, string peerUniqueIdTarget, int peerPortTarget, bool onSendBroadcastMode, ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject peerFirewallSettingObject)
            {
                _peerIpTarget = peerIpTarget;
                _peerUniqueIdTarget = peerUniqueIdTarget;
                _peerPortTarget = peerPortTarget;
                _onSendBroadcastMode = onSendBroadcastMode;
                _peerNetworkSettingObject = peerNetworkSettingObject;
                _peerFirewallSettingObject = peerFirewallSettingObject;
                _memPoolListBlockHeightTransactionReceived = new Dictionary<long, HashSet<string>>();
                _memPoolListBlockHeightTransactionSend = new Dictionary<long, HashSet<string>>();
                _listPacketReceived = new List<ClassReadPacketSplitted>();
                _memoryStream = new MemoryStream();
            }

            #region Dispose functions

            private bool _disposed;

            ~ClassPeerNetworkClientBroadcastMemPool()
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
                    StopTaskAndDisconnect();
                }
                _disposed = true;
            }
            #endregion

            #region Initialize/Stop broadcast client.

            /// <summary>
            /// Try to connect to the peer target.
            /// </summary>
            /// <returns></returns>
            public async Task<bool> TryConnect(CancellationTokenSource mainCancellation)
            {
                if (_peerCheckConnectionCancellationToken != null)
                {
                    if (!_peerCheckConnectionCancellationToken.IsCancellationRequested)
                    {
                        _peerCheckConnectionCancellationToken.Cancel();
                        _peerCheckConnectionCancellationToken.Dispose();
                    }
                }
                _peerCheckConnectionCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(mainCancellation.Token);
                StopTaskAndDisconnect();

                IsAlive = true;
                _peerCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(mainCancellation.Token);
                _peerTcpClient = new TcpClient();

                bool connectStatus = false;
                long timestampStart = ClassUtility.GetCurrentTimestampInMillisecond();
                using (CancellationTokenSource cancellationConnect = CancellationTokenSource.CreateLinkedTokenSource(_peerCancellationToken.Token))
                {

                    try
                    {
                        await Task.Factory.StartNew(async () =>
                        {
                            while (!connectStatus)
                            {
                                try
                                {
                                    await _peerTcpClient.ConnectAsync(_peerIpTarget, _peerPortTarget);
                                    connectStatus = true;
                                    break;
                                }
                                catch
                                {
                                    // Ignored, until the task is not finish or until the time to spend is not reach.
                                }
                                try
                                {
                                    await Task.Delay(10, _peerCancellationToken.Token);
                                }
                                catch
                                {
                                    break;
                                }
                            }

                        }, cancellationConnect.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignored, catch the exception once the task is cancelled.
                    }

                    while (!connectStatus)
                    {
                        if (timestampStart + (_peerNetworkSettingObject.PeerMaxDelayToConnectToTarget * 1000) < ClassUtility.GetCurrentTimestampInMillisecond())
                        {
                            break;
                        }

                        if (_peerCancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        try
                        {
                            await Task.Delay(10, _peerCancellationToken.Token);
                        }
                        catch
                        {
                            break;
                        }
                    }

                    cancellationConnect.Cancel();
                }

                if (!connectStatus)
                {
                    IsAlive = false;
                }

                return connectStatus;
            }

            /// <summary>
            /// Try to ask the broadcast mode.
            /// </summary>
            /// <returns></returns>
            public async Task<bool> TryAskBroadcastMode()
            {
                ClassPeerPacketSendObject packetSendObject = new ClassPeerPacketSendObject(_peerNetworkSettingObject.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[_peerIpTarget][_peerUniqueIdTarget].PeerInternPublicKey)
                {
                    PacketOrder = ClassPeerEnumPacketSend.ASK_MEM_POOL_BROADCAST_MODE,
                };

                if (!await TrySendPacketToPeer(JsonConvert.SerializeObject(packetSendObject)))
                {
                    IsAlive = false;
                    return false;
                }

                bool broadcastResponsePacketStatus = false;
                bool failed = false;
                long timestampStart = ClassUtility.GetCurrentTimestampInMillisecond();
                MemoryStream memoryStream = new MemoryStream();

                using (CancellationTokenSource cancellationReceiveBroadcastResponsePacket = CancellationTokenSource.CreateLinkedTokenSource(_peerCancellationToken.Token))
                {

                    // Wait broadcast response.
                    try
                    {
                        await Task.Factory.StartNew(async () =>
                        {
                            try
                            {
                                bool containSeperator = false;

                                using (NetworkStream networkStream = new NetworkStream(_peerTcpClient.Client))
                                {
                                    while (!broadcastResponsePacketStatus)
                                    {
                                        byte[] packetReceivedBuffer = new byte[_peerNetworkSettingObject.PeerMaxPacketBufferSize];

                                        int packetLength = await networkStream.ReadAsync(packetReceivedBuffer, 0, packetReceivedBuffer.Length, cancellationReceiveBroadcastResponsePacket.Token);

                                        if (packetLength > 0)
                                        {
                                            foreach (byte dataByte in packetReceivedBuffer)
                                            {
                                                cancellationReceiveBroadcastResponsePacket.Token.ThrowIfCancellationRequested();

                                                char character = (char)dataByte;
                                                if (character != '\0')
                                                {
                                                    if (character != ClassPeerPacketSetting.PacketPeerSplitSeperator)
                                                    {
                                                        if (ClassUtility.CharIsABase64Character(character))
                                                        {
                                                            memoryStream.WriteByte(dataByte);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        containSeperator = true;
                                                        break;
                                                    }
                                                }
                                            }
                                        }

                                        if (containSeperator)
                                        {
                                            if (ClassUtility.TryDeserialize(Convert.FromBase64String(memoryStream.ToArray().GetStringFromByteArrayAscii()).GetStringFromByteArrayAscii(), out ClassPeerPacketRecvObject peerPacketRecvObject, ObjectCreationHandling.Reuse))
                                            {
                                                if (peerPacketRecvObject == null)
                                                {
                                                    failed = true;
                                                    break;
                                                }

                                                if (peerPacketRecvObject.PacketContent.IsNullOrEmpty())
                                                {
                                                    failed = true;
                                                    break;
                                                }

                                                if (peerPacketRecvObject.PacketOrder != ClassPeerEnumPacketResponse.SEND_MEM_POOL_BROADCAST_RESPONSE)
                                                {
                                                    failed = true;
                                                    break;
                                                }

                                                if (!ClassUtility.TryDeserialize(peerPacketRecvObject.PacketContent, out ClassPeerPacketSendBroadcastMemPoolResponse packetSendBroadcastMemPoolResponse, ObjectCreationHandling.Reuse))
                                                {
                                                    failed = true;
                                                    break;
                                                }

                                                if (packetSendBroadcastMemPoolResponse == null)
                                                {
                                                    failed = true;
                                                    break;
                                                }

                                                if (!packetSendBroadcastMemPoolResponse.Status)
                                                {
                                                    failed = true;
                                                    break;
                                                }

                                                broadcastResponsePacketStatus = true;
                                            }
                                            else
                                            {
                                                failed = true;
                                                break;
                                            }

                                            // Clean up.
                                            memoryStream.Clear();
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Ignored.
                            }
                        }, cancellationReceiveBroadcastResponsePacket.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignored, catch the exception once the task is cancelled.
                    }

                    while (!broadcastResponsePacketStatus)
                    {
                        if (timestampStart + (_peerNetworkSettingObject.PeerMaxDelayAwaitResponse * 1000) < ClassUtility.GetCurrentTimestampInMillisecond())
                        {
                            break;
                        }

                        if (_peerCancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        if (failed)
                        {
                            break;
                        }

                        try
                        {
                            await Task.Delay(10, _peerCancellationToken.Token);
                        }
                        catch
                        {
                            break;
                        }
                    }

                    cancellationReceiveBroadcastResponsePacket.Cancel();
                }
                memoryStream.Clear();

                if (!broadcastResponsePacketStatus)
                {
                    IsAlive = false;
                }

                return broadcastResponsePacketStatus;
            }

            /// <summary>
            /// Stop tasks and disconnect.
            /// </summary>
            public void StopTaskAndDisconnect()
            {
                IsAlive = false;

                // Stop tasks.
                if (_peerCancellationToken != null)
                {
                    if (!_peerCancellationToken.IsCancellationRequested)
                    {
                        _peerCancellationToken.Cancel();
                        _peerCancellationToken.Dispose();
                    }
                }

                try
                {
                    if (_peerTcpClient?.Client != null)
                    {
                        if (_peerTcpClient.Client.Connected)
                        {
                            try
                            {
                                _peerTcpClient.Client.Shutdown(SocketShutdown.Both);
                            }
                            finally
                            {
                                if (_peerTcpClient?.Client != null)
                                {
                                    if (_peerTcpClient.Client.Connected)
                                    {
                                        _peerTcpClient.Client.Close();
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

                // Clean up.
                _memoryStream.Clear();
                _listPacketReceived.Clear();
                _memPoolListBlockHeightTransactionReceived.Clear();
                _memPoolListBlockHeightTransactionSend.Clear();
            }

            #endregion

            #region Broadcast tasks.

            /// <summary>
            /// Run the broadcast mempool client transaction tasks.
            /// </summary>
            public void RunBroadcastTransactionTask()
            {
                IsAlive = true;

                EnableCheckConnect();

                try
                {
                    EnableKeepAliveTask();
                    if (!_onSendBroadcastMode)
                    {
                        EnableReceiveBroadcastTransactionTask();
                    }
                    else
                    {
                        EnableSendBroadcastTransactionTask();
                    }
                }
                catch
                {
                    // Ignored, catch the exception once the task is cancelled.
                }
            }

            /// <summary>
            /// Enable the keep alive task.
            /// </summary>
            private void EnableKeepAliveTask()
            {
                Task.Factory.StartNew(async () =>
                {
                    try
                    {
                        while (IsAlive)
                        {
                            try
                            {
                                ClassPeerPacketSendObject packetSendObject = new ClassPeerPacketSendObject(_peerNetworkSettingObject.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[_peerIpTarget][_peerUniqueIdTarget].PeerInternPublicKey)
                                {
                                    PacketOrder = ClassPeerEnumPacketSend.ASK_KEEP_ALIVE,
                                    PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketAskKeepAlive()
                                    {
                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                    }),
                                };

                                if (!await TrySendPacketToPeer(JsonConvert.SerializeObject(packetSendObject)))
                                {
                                    IsAlive = false;
                                    break;
                                }

                                await Task.Delay(1000, _peerCancellationToken.Token);
                            }
                            catch
                            {
                                IsAlive = false;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        IsAlive = false;
                    }
                }, _peerCancellationToken.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
            }

            /// <summary>
            /// Enable the received broadcast transaction task.
            /// </summary>
            private void EnableReceiveBroadcastTransactionTask()
            {
                Task.Factory.StartNew(async () =>
                {
                    try
                    {
                        while (IsAlive)
                        {
                            try
                            {
                                long lastBlockHeightUnlocked = ClassBlockchainDatabase.BlockchainMemoryManagement.GetLastBlockHeightUnlocked(_peerCancellationToken);

                                // Clean passed blocks mined.
                                foreach (long blockHeight in _memPoolListBlockHeightTransactionReceived.Keys.ToArray())
                                {
                                    if (blockHeight <= lastBlockHeightUnlocked)
                                    {
                                        _memPoolListBlockHeightTransactionReceived[blockHeight].Clear();
                                        _memPoolListBlockHeightTransactionReceived.Remove(blockHeight);
                                    }
                                }


                                #region First ask the mem pool block height list and their transaction counts.

                                ClassPeerPacketSendObject packetSendObject = new ClassPeerPacketSendObject(_peerNetworkSettingObject.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[_peerIpTarget][_peerUniqueIdTarget].PeerInternPublicKey)
                                {
                                    PacketOrder = ClassPeerEnumPacketSend.ASK_MEM_POOl_BLOCK_HEIGHT_LIST_BROADCAST_MODE,
                                    PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendAskMemPoolBlockHeightList()
                                    {
                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                    }),
                                };

                                packetSendObject = await ClassPeerNetworkBroadcastFunction.BuildSignedPeerSendPacketObject(packetSendObject, _peerIpTarget, _peerUniqueIdTarget, _peerCancellationToken);

                                if (!await TrySendPacketToPeer(JsonConvert.SerializeObject(packetSendObject)))
                                {
                                    IsAlive = false;
                                    break;
                                }

                                ClassPeerPacketRecvObject peerPacketRecvMemPoolBlockHeightList = await TryReceiveMemPoolBlockHeightListPacket();

                                ClassTranslatePacket<ClassPeerPacketSendMemPoolBlockHeightList> peerPacketMemPoolBlockHeightListTranslated = TranslatePacketReceived<ClassPeerPacketSendMemPoolBlockHeightList>(peerPacketRecvMemPoolBlockHeightList, ClassPeerEnumPacketResponse.SEND_MEM_POOL_BLOCK_HEIGHT_LIST_BROADCAST_MODE);

                                if (!peerPacketMemPoolBlockHeightListTranslated.Status)
                                {
                                    IsAlive = false;
                                    break;
                                }


                                #endregion

                                #region Check the packet of mempool block height lists.

                                if (peerPacketMemPoolBlockHeightListTranslated.PacketTranslated.MemPoolBlockHeightListAndCount == null)
                                {
                                    IsAlive = false;
                                    break;
                                }

                                #endregion

                                #region Then sync transaction by height.

                                if (peerPacketMemPoolBlockHeightListTranslated.PacketTranslated.MemPoolBlockHeightListAndCount.Count > 0)
                                {
                                    bool failed = false;

                                    foreach (long blockHeight in peerPacketMemPoolBlockHeightListTranslated.PacketTranslated.MemPoolBlockHeightListAndCount.Keys.OrderBy(x => x))
                                    {
                                        _peerCancellationToken.Token.ThrowIfCancellationRequested();

                                        if (ClassBlockchainDatabase.BlockchainMemoryManagement.GetLastBlockHeight < blockHeight)
                                        {
                                            if (peerPacketMemPoolBlockHeightListTranslated.PacketTranslated.MemPoolBlockHeightListAndCount[blockHeight] > 0)
                                            {
                                                long lastBlockHeightUnlockedConfirmed = await ClassBlockchainDatabase.BlockchainMemoryManagement.GetLastBlockHeightTransactionConfirmationDone(_peerCancellationToken);

                                                if (ClassBlockchainDatabase.BlockchainMemoryManagement.GetLastBlockHeight + BlockchainSetting.TransactionMandatoryMinBlockHeightStartConfirmation >= (blockHeight-1))
                                                {
                                                    int countAlreadySynced = 0;

                                                    if (_memPoolListBlockHeightTransactionReceived.ContainsKey(blockHeight))
                                                    {
                                                        countAlreadySynced = _memPoolListBlockHeightTransactionReceived[blockHeight].Count;
                                                    }
                                                    else
                                                    {
                                                        _memPoolListBlockHeightTransactionReceived.Add(blockHeight, new HashSet<string>());
                                                    }

                                                    if (peerPacketMemPoolBlockHeightListTranslated.PacketTranslated.MemPoolBlockHeightListAndCount[blockHeight] != countAlreadySynced)
                                                    {
                                                        // Ensure to be compatible with most recent transactions sent.

                                                        packetSendObject = new ClassPeerPacketSendObject(_peerNetworkSettingObject.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[_peerIpTarget][_peerUniqueIdTarget].PeerInternPublicKey)
                                                        {
                                                            PacketOrder = ClassPeerEnumPacketSend.ASK_MEM_POOl_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE,
                                                            PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendAskMemPoolTransactionList()
                                                            {
                                                                BlockHeight = blockHeight,
                                                                TotalTransactionProgress = 0,
                                                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                            }),
                                                        };

                                                        packetSendObject = await ClassPeerNetworkBroadcastFunction.BuildSignedPeerSendPacketObject(packetSendObject, _peerIpTarget, _peerUniqueIdTarget, _peerCancellationToken);

                                                        if (!await TrySendPacketToPeer(JsonConvert.SerializeObject(packetSendObject)))
                                                        {
                                                            IsAlive = false;
                                                            failed = true;
                                                            break;
                                                        }

                                                        if (!await TryReceiveMemPoolTransactionPacket(blockHeight, peerPacketMemPoolBlockHeightListTranslated.PacketTranslated.MemPoolBlockHeightListAndCount[blockHeight]))
                                                        {
                                                            IsAlive = false;
                                                            failed = true;
                                                            break;
                                                        }


                                                    }
                                                }
                                                else
                                                {
                                                    break;
                                                }
                                            }
                                        }

                                        if (failed)
                                        {
                                            IsAlive = false;
                                            break;
                                        }
                                    }
                                }

                                #endregion

                            }
                            catch
                            {
                                IsAlive = false;
                                break;
                            }

                            await Task.Delay(1000, _peerCancellationToken.Token);
                        }
                    }
                    catch
                    {
                        IsAlive = false;
                    }

                }, _peerCancellationToken.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
            }

            /// <summary>
            /// Enable the send broadcast transaction task.
            /// </summary>
            private void EnableSendBroadcastTransactionTask()
            {
                Task.Factory.StartNew(async () =>
                {
                    try
                    {
                        while (IsAlive)
                        {
                            bool cancelSendingBroadcast = false;
                            try
                            {
                                List<long> listMemPoolBlockHeight = await ClassMemPoolDatabase.GetMemPoolListBlockHeight(_peerCancellationToken);

                                if (listMemPoolBlockHeight.Count > 0)
                                {
                                    foreach (long blockHeight in listMemPoolBlockHeight)
                                    {
                                        _peerCancellationToken.Token.ThrowIfCancellationRequested();

                                        int countMemPoolTransaction = await ClassMemPoolDatabase.GetCountMemPoolTxFromBlockHeight(blockHeight, _peerCancellationToken);

                                        if (countMemPoolTransaction > 0)
                                        {
                                            if (!_memPoolListBlockHeightTransactionSend.ContainsKey(blockHeight))
                                            {
                                                _memPoolListBlockHeightTransactionSend.Add(blockHeight, new HashSet<string>());
                                            }

                                            List<ClassTransactionObject> listTransactionObject = await ClassMemPoolDatabase.GetMemPoolTxObjectFromBlockHeight(blockHeight, _peerCancellationToken);

                                            foreach (ClassTransactionObject transactionObject in listTransactionObject)
                                            {
                                                _peerCancellationToken.Token.ThrowIfCancellationRequested();

                                                if (transactionObject.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION &&
                                                    transactionObject.TransactionType != ClassTransactionEnumType.DEV_FEE_TRANSACTION)
                                                {
                                                    if (!_memPoolListBlockHeightTransactionSend[blockHeight].Contains(transactionObject.TransactionHash))
                                                    {
                                                        ClassPeerPacketSendObject packetSendObject = new ClassPeerPacketSendObject(_peerNetworkSettingObject.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[_peerIpTarget][_peerUniqueIdTarget].PeerInternPublicKey)
                                                        {
                                                            PacketOrder = ClassPeerEnumPacketSend.ASK_MEM_POOL_TRANSACTION_VOTE,
                                                            PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketSendAskMemPoolTransactionVote()
                                                            {
                                                                TransactionObject = transactionObject,
                                                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond(),
                                                            })
                                                        };

                                                        packetSendObject = await ClassPeerNetworkBroadcastFunction.BuildSignedPeerSendPacketObject(packetSendObject, _peerIpTarget, _peerUniqueIdTarget, _peerCancellationToken);

                                                        if (!await TrySendPacketToPeer(JsonConvert.SerializeObject(packetSendObject)))
                                                        {
                                                            IsAlive = false;
                                                            break;
                                                        }

                                                        if (await TryReceiveMemPoolTransactionVotePacket())
                                                        {
                                                            _memPoolListBlockHeightTransactionSend[blockHeight].Add(transactionObject.TransactionHash);
                                                        }
                                                        else
                                                        {
                                                            cancelSendingBroadcast = true;
                                                            IsAlive = false;
                                                            break;
                                                        }
                                                    }
                                                }
                                            }

                                            // Clean up.
                                            listTransactionObject.Clear();

                                        }

                                        if (cancelSendingBroadcast)
                                        {
                                            break;
                                        }
                                    }
                                }

                                // Clean up.
                                listMemPoolBlockHeight.Clear();
                            }
                            catch
                            {
                                IsAlive = false;
                                break;
                            }

                            await Task.Delay(1000, _peerCancellationToken.Token);
                        }
                    }
                    catch
                    {
                        IsAlive = false;
                    }
                }, _peerCancellationToken.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
            }

            /// <summary>
            /// Enable the check connect task.
            /// </summary>
            private void EnableCheckConnect()
            {
                try
                {
                    Task.Factory.StartNew(async () =>
                    {
                        while (IsAlive)
                        {
                            try
                            {
                                if (!ClassUtility.SocketIsConnected(_peerTcpClient))
                                {
                                    StopTaskAndDisconnect();
                                    IsAlive = false;
                                    break;
                                }

                                await Task.Delay(1000, _peerCheckConnectionCancellationToken.Token);
                            }
                            catch
                            {
                                StopTaskAndDisconnect();
                                IsAlive = false;
                                break;
                            }
                        }
                    }, _peerCheckConnectionCancellationToken.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                }
                catch
                {
                    // Ignored, catch the exception once the task is cancelled.
                }
            }

            #endregion

            #region Handle packet response

            /// <summary>
            /// Try receive packet.
            /// </summary>
            /// <param name="networkStream"></param>
            /// <returns></returns>
            private async Task<ClassPeerPacketRecvObject> TryReceiveMemPoolBlockHeightListPacket()
            {
                bool failed = false;
                bool packetReceived = false;
                bool taskComplete = false;
                ClassPeerPacketRecvObject peerPacketRecvObject = null;

                long timestampStart = ClassUtility.GetCurrentTimestampInMillisecond();

                using (CancellationTokenSource cancellationReceiveBlockListPacket = CancellationTokenSource.CreateLinkedTokenSource(_peerCancellationToken.Token))
                {
                    try
                    {
                        await Task.Factory.StartNew(async () =>
                        {
                            bool containSeperator = false;

                            try
                            {
                                using (NetworkStream networkStream = new NetworkStream(_peerTcpClient.Client))
                                {
                                    while (!packetReceived && IsAlive && !taskComplete)
                                    {
                                        byte[] packetBuffer = new byte[_peerNetworkSettingObject.PeerMaxPacketBufferSize];

                                        int packetLength = await networkStream.ReadAsync(packetBuffer, 0, packetBuffer.Length, cancellationReceiveBlockListPacket.Token);

                                        if (ReadBytesPacket(packetBuffer, true, cancellationReceiveBlockListPacket))
                                        {
                                            containSeperator = true;
                                        }

                                        // Clean up.
                                        Array.Clear(packetBuffer, 0, packetBuffer.Length);

                                        if (containSeperator)
                                        {
                                            packetReceived = true;

                                            bool exceptionBase64 = false;
                                            byte[] base64Packet = null;

                                            try
                                            {
                                                base64Packet = Convert.FromBase64String(_memoryStream.ToArray().GetStringFromByteArrayAscii());
                                            }
                                            catch
                                            {
                                                exceptionBase64 = true;
                                            }


                                            // Clean up.
                                            _memoryStream.Clear();

                                            if (!exceptionBase64)
                                            {
                                                if (ClassUtility.TryDeserialize(base64Packet.GetStringFromByteArrayAscii(), out peerPacketRecvObject, ObjectCreationHandling.Reuse))
                                                {
                                                    if (peerPacketRecvObject == null)
                                                    {
                                                        failed = true;
                                                    }

                                                    if (peerPacketRecvObject.PacketContent.IsNullOrEmpty())
                                                    {
                                                        failed = true;
                                                    }

                                                    if (peerPacketRecvObject.PacketOrder != ClassPeerEnumPacketResponse.SEND_MEM_POOL_BLOCK_HEIGHT_LIST_BROADCAST_MODE)
                                                    {
                                                        failed = true;
                                                    }
                                                }
                                                else
                                                {
                                                    failed = true;
                                                }
                                            }


                                            taskComplete = true;
                                            break;
                                        }



                                        if (packetLength == 0)
                                        {
                                            break;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Ignored, the socket can be closed.
                            }

                            taskComplete = true;
                        }, cancellationReceiveBlockListPacket.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignored, catch the exception once the task is cancelled.
                    }

                    while (!taskComplete)
                    {
                        if (timestampStart + (_peerNetworkSettingObject.PeerMaxDelayAwaitResponse * 1000) < ClassUtility.GetCurrentTimestampInMillisecond())
                        {
                            break;
                        }

                        if (!IsAlive)
                        {
                            break;
                        }

                        await Task.Delay(10, _peerCancellationToken.Token);
                    }

                    cancellationReceiveBlockListPacket.Cancel();
                }

                // Clean up.
                _memoryStream.Clear();

                if (failed)
                {
                    return null;
                }

                return peerPacketRecvObject;
            }

            /// <summary>
            /// Try to receive every transactions sent by the peer target.
            /// </summary>
            /// <param name="networkStream"></param>
            /// <returns></returns>
            private async Task<bool> TryReceiveMemPoolTransactionPacket(long blockHeight, int maxBlockTransaction)
            {
                bool receiveStatus = true;
                bool endBroadcast = false;
                long timestampStart = ClassUtility.GetCurrentTimestampInMillisecond();
                Dictionary<string, string> listWalletAddressAndPublicKeyCache = new Dictionary<string, string>();
                List<ClassTransactionObject> listTransactionObject = new List<ClassTransactionObject>();
                _listPacketReceived.Clear();
                _listPacketReceived.Add(new ClassReadPacketSplitted());

                using (CancellationTokenSource cancellationReceiveTransactionPacket = CancellationTokenSource.CreateLinkedTokenSource(_peerCancellationToken.Token))
                {
                    try
                    {
                        await Task.Factory.StartNew(async () =>
                        {
                        try
                        {
                            using (NetworkStream networkStream = new NetworkStream(_peerTcpClient.Client))
                            {
                                while (!endBroadcast && receiveStatus && IsAlive)
                                {
                                    bool containSeperator = false;

                                    byte[] packetBuffer = new byte[_peerNetworkSettingObject.PeerMaxPacketBufferSize];

                                    int packetLength = await networkStream.ReadAsync(packetBuffer, 0, packetBuffer.Length, cancellationReceiveTransactionPacket.Token);


                                    foreach (byte dataByte in packetBuffer)
                                    {
                                        cancellationReceiveTransactionPacket.Token.ThrowIfCancellationRequested();

                                        char character = (char)dataByte;
                                        if (character != '\0')
                                        {
                                            if (ClassUtility.CharIsABase64Character(character))
                                            {
                                                _listPacketReceived[_listPacketReceived.Count - 1].Packet.Add(dataByte);
                                            }

                                            if (character == ClassPeerPacketSetting.PacketPeerSplitSeperator)
                                            {
                                                containSeperator = true;
                                                _listPacketReceived[_listPacketReceived.Count - 1].Complete = true;



                                                bool exceptionBase64 = false;
                                                byte[] base64Data = null;

                                                try
                                                {
                                                    base64Data = Convert.FromBase64String(_listPacketReceived[_listPacketReceived.Count - 1].Packet.ToArray().GetStringFromByteArrayAscii());
                                                }
                                                catch
                                                {
                                                    exceptionBase64 = true;
                                                }

                                                _listPacketReceived[_listPacketReceived.Count - 1].Packet.Clear();

                                                if (!exceptionBase64)
                                                {
                                                    try
                                                    {

                                                        if (ClassUtility.TryDeserialize(base64Data.GetStringFromByteArrayAscii(), out ClassPeerPacketRecvObject peerPacketRecvObject, ObjectCreationHandling.Reuse))
                                                        {
                                                            if (peerPacketRecvObject == null)
                                                            {
                                                                receiveStatus = false;
                                                                break;
                                                            }

                                                            if (peerPacketRecvObject.PacketOrder == ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE ||
                                                                peerPacketRecvObject.PacketOrder == ClassPeerEnumPacketResponse.SEND_MEM_POOL_END_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE)
                                                            {
                                                                // Receive transaction.
                                                                if (peerPacketRecvObject.PacketOrder == ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE)
                                                                {

                                                                    bool sendReceiveConfirmation = false;

                                                                    ClassTranslatePacket<ClassPeerPacketSendMemPoolTransaction> packetTranslated = TranslatePacketReceived<ClassPeerPacketSendMemPoolTransaction>(peerPacketRecvObject, ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE);

                                                                    if (packetTranslated.Status)
                                                                    {
                                                                        if (packetTranslated.PacketTranslated.ListTransactionObject.Count > 0)
                                                                        {
                                                                            foreach (ClassTransactionObject transactionObject in packetTranslated.PacketTranslated.ListTransactionObject)
                                                                            {
                                                                                if (transactionObject != null)
                                                                                {
                                                                                    if (transactionObject.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION &&
                                                                                        transactionObject.TransactionType != ClassTransactionEnumType.DEV_FEE_TRANSACTION)
                                                                                    {
                                                                                        if (!_memPoolListBlockHeightTransactionReceived[blockHeight].Contains(transactionObject.TransactionHash))
                                                                                        {
                                                                                            bool canInsert = false;

                                                                                            if (transactionObject.BlockHeightTransaction > ClassBlockchainDatabase.BlockchainMemoryManagement.GetLastBlockHeight)
                                                                                            {

                                                                                                ClassTransactionEnumStatus checkTxResult = await ClassTransactionUtility.CheckTransactionWithBlockchainData(transactionObject, true, true, null, 0, listWalletAddressAndPublicKeyCache, true, _peerCancellationToken);

                                                                                                bool invalid = true;

                                                                                                if (checkTxResult == ClassTransactionEnumStatus.VALID_TRANSACTION || checkTxResult == ClassTransactionEnumStatus.DUPLICATE_TRANSACTION_HASH)
                                                                                                {
                                                                                                    canInsert = true;
                                                                                                    invalid = false;
                                                                                                }

                                                                                                if (invalid)
                                                                                                {
                                                                                                    Debug.WriteLine("Invalid tx's received from MemPool broadcast mode: " + checkTxResult);
                                                                                                    IsAlive = false;
                                                                                                    endBroadcast = true;
                                                                                                    break;
                                                                                                }
                                                                                            }

                                                                                                if (!sendReceiveConfirmation)
                                                                                                {
                                                                                                    // Send the confirmation of receive.
                                                                                                    if (!await TrySendPacketToPeer(JsonConvert.SerializeObject(new ClassPeerPacketSendObject(_peerNetworkSettingObject.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[_peerIpTarget][_peerUniqueIdTarget].PeerInternPublicKey)
                                                                                                    {
                                                                                                        PacketOrder = ClassPeerEnumPacketSend.ASK_MEM_POOL_TRANSACTION_BROADCAST_CONFIRMATION_RECEIVED,
                                                                                                        PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketAskMemPoolTransactionBroadcastConfirmationReceived()
                                                                                                        {
                                                                                                            PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                                                        })
                                                                                                    })))
                                                                                                    {
                                                                                                        IsAlive = false;
                                                                                                        endBroadcast = true;
                                                                                                        break;
                                                                                                    }

                                                                                                    sendReceiveConfirmation = true;
                                                                                                }

                                                                                                if (!listWalletAddressAndPublicKeyCache.ContainsKey(transactionObject.WalletAddressSender))
                                                                                                {
                                                                                                    listWalletAddressAndPublicKeyCache.Add(transactionObject.WalletAddressSender, transactionObject.WalletPublicKeySender);
                                                                                                }

                                                                                                if (transactionObject.TransactionType == ClassTransactionEnumType.TRANSFER_TRANSACTION)
                                                                                                {
                                                                                                    listWalletAddressAndPublicKeyCache.Add(transactionObject.WalletAddressReceiver, transactionObject.WalletPublicKeyReceiver);
                                                                                                }

                                                                                                if (canInsert)
                                                                                                {
                                                                                                    listTransactionObject.Add(transactionObject);
                                                                                                }
                                                                                            }

                                                                                            if (listTransactionObject.Count >= maxBlockTransaction)
                                                                                            {
                                                                                                endBroadcast = true;
                                                                                                break;
                                                                                            }

                                                                                            timestampStart = ClassUtility.GetCurrentTimestampInMillisecond();
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                    // End broadcast transaction.
                                                                    else if (peerPacketRecvObject.PacketOrder == ClassPeerEnumPacketResponse.SEND_MEM_POOL_END_TRANSACTION_BY_BLOCK_HEIGHT_BROADCAST_MODE)
                                                                    {
                                                                        endBroadcast = true;
                                                                        break;
                                                                    }
                                                                }
                                                                else
                                                                {
                                                                    receiveStatus = false;
                                                                    break;
                                                                }
                                                            }
                                                        }
                                                        catch
                                                        {
                                                            // Ignore formating error.
                                                        }
                                                    }


                                                    _listPacketReceived.Add(new ClassReadPacketSplitted());

                                                }
                                            }
                                        }

                                        // Clean up.
                                        Array.Clear(packetBuffer, 0, packetBuffer.Length);

                                        if (containSeperator)
                                        {
                                            // Clean up.
                                            _listPacketReceived.RemoveAll(x => x.Complete);
                                            containSeperator = false;
                                        }

                                        if (packetLength == 0)
                                        {
                                            break;
                                        }

                                        if (endBroadcast)
                                        {
                                            break;
                                        }
                                    }
                                }
                            }
                            catch
                            {
                                // Ignored, the socket can be closed.
                            }
                        }, cancellationReceiveTransactionPacket.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignored, catch the exception once the task is complete.
                    }

                    while (!endBroadcast)
                    {
                        if (timestampStart + (_peerNetworkSettingObject.PeerMaxDelayAwaitResponse * 1000) < ClassUtility.GetCurrentTimestampInMillisecond())
                        {
                            break;
                        }

                        if (!IsAlive)
                        {
                            break;
                        }

                        if (endBroadcast)
                        {
                            break;
                        }

                        await Task.Delay(10, _peerCancellationToken.Token);
                    }

                    cancellationReceiveTransactionPacket.Cancel();
                }

                if (listTransactionObject.Count > 0)
                {
                    foreach (ClassTransactionObject transactionObject in listTransactionObject)
                    {
                        if (!_memPoolListBlockHeightTransactionReceived[blockHeight].Contains(transactionObject.TransactionHash))
                        {

                            if (await ClassMemPoolDatabase.InsertTxToMemPoolAsync(transactionObject, _peerCancellationToken))
                            {
                                ClassLog.WriteLine("[Client Broadcast] - TX Hash " + transactionObject.TransactionHash + " received from peer: " + _peerIpTarget, ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_SYNC, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);
                            }

                            _memPoolListBlockHeightTransactionReceived[blockHeight].Add(transactionObject.TransactionHash);
                        }
                    }
                }

                // Clean up.
                _listPacketReceived.Clear();
                listWalletAddressAndPublicKeyCache.Clear();
                listTransactionObject.Clear();

                return receiveStatus;
            }

            /// <summary>
            /// Try to receive a transaction vote packet received from the peer target.
            /// </summary>
            /// <returns></returns>
            private async Task<bool> TryReceiveMemPoolTransactionVotePacket()
            {
                bool voteStatus = false;
                bool taskDone = false;
                long timestampStart = ClassUtility.GetCurrentTimestampInMillisecond();

                using (CancellationTokenSource cancellationReceiveMemPoolTransactionVote = CancellationTokenSource.CreateLinkedTokenSource(_peerCancellationToken.Token))
                {
                    try
                    {
                        await Task.Factory.StartNew(async () =>
                        {
                            try
                            {
                                bool containSeperator = false;

                                using (NetworkStream networkStream = new NetworkStream(_peerTcpClient.Client))
                                {
                                    while (IsAlive && !taskDone && !voteStatus)
                                    {
                                        byte[] packetBuffer = new byte[_peerNetworkSettingObject.PeerMaxPacketBufferSize];

                                        int packetLength = await networkStream.ReadAsync(packetBuffer, 0, packetBuffer.Length, cancellationReceiveMemPoolTransactionVote.Token);

                                        if (ReadBytesPacket(packetBuffer, true, cancellationReceiveMemPoolTransactionVote))
                                        {
                                            containSeperator = true;
                                        }

                                        // Clean up.
                                        Array.Clear(packetBuffer, 0, packetBuffer.Length);

                                        if (containSeperator)
                                        {
                                            bool exceptionBase64 = false;
                                            byte[] base64Data = null;

                                            try
                                            {
                                                base64Data = Convert.FromBase64String(_memoryStream.ToArray().GetStringFromByteArrayAscii());
                                            }
                                            catch
                                            {
                                                exceptionBase64 = true;
                                            }


                                            // Clean up.
                                            _memoryStream.Clear();

                                            if (!exceptionBase64)
                                            {
                                                try
                                                {
                                                    if (ClassUtility.TryDeserialize(base64Data.GetStringFromByteArrayAscii(), out ClassPeerPacketRecvObject peerPacketRecvObject, ObjectCreationHandling.Reuse))
                                                    {

                                                        if (peerPacketRecvObject != null)
                                                        {
                                                            if (peerPacketRecvObject.PacketOrder == ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_VOTE)
                                                            {

                                                                ClassTranslatePacket<ClassPeerPacketSendMemPoolTransactionVote> packetTranslated = TranslatePacketReceived<ClassPeerPacketSendMemPoolTransactionVote>(peerPacketRecvObject, ClassPeerEnumPacketResponse.SEND_MEM_POOL_TRANSACTION_VOTE);

                                                                if (packetTranslated.Status)
                                                                {
                                                                    if (packetTranslated.PacketTranslated.TransactionStatus == ClassTransactionEnumStatus.VALID_TRANSACTION)
                                                                    {
                                                                        voteStatus = true;
                                                                    }
                                                                }
                                                                else
                                                                {
                                                                    IsAlive = false;
                                                                }
                                                                taskDone = true;
                                                                break;
                                                            }
                                                            else
                                                            {
                                                                IsAlive = false;
                                                                break;
                                                            }
                                                        }
                                                        else
                                                        {
                                                            IsAlive = false;
                                                            break;
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
                                }
                            }
                            catch
                            {
                                // Ignored.
                            }

                            taskDone = true;
                        }, cancellationReceiveMemPoolTransactionVote.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignored, catch the exception once the task is cancelled.
                    }

                    while (!taskDone)
                    {
                        if (timestampStart + (_peerNetworkSettingObject.PeerMaxDelayAwaitResponse * 1000) < ClassUtility.GetCurrentTimestampInMillisecond())
                        {
                            break;
                        }

                        if (voteStatus)
                        {
                            break;
                        }

                        if (!IsAlive)
                        {
                            break;
                        }

                        await Task.Delay(10, _peerCancellationToken.Token);
                    }

                    cancellationReceiveMemPoolTransactionVote.Cancel();
                }

                // Clean up.
                _memoryStream.Clear();

                return voteStatus;
            }

            #endregion

            #region Packet management.

            /// <summary>
            /// Try to send a packet to the peer target.
            /// </summary>
            /// <param name="packetData"></param>
            /// <returns></returns>
            private async Task<bool> TrySendPacketToPeer(string packetData)
            {
                try
                {
                    using (NetworkStream networkStream = new NetworkStream(_peerTcpClient.Client))
                    {
                        if (!await networkStream.TrySendSplittedPacket(ClassUtility.GetByteArrayFromStringAscii(Convert.ToBase64String(ClassUtility.GetByteArrayFromStringAscii(packetData)) + ClassPeerPacketSetting.PacketPeerSplitSeperator), _peerCancellationToken, _peerNetworkSettingObject.PeerMaxPacketSplitedSendSize))
                        {
                            return false;
                        }
                    }
                }
                catch
                {
                    return false;
                }

                return true;
            }

            /// <summary>
            /// Read bytes packet received and write them to the memory stream.
            /// </summary>
            /// <param name="packetBuffer"></param>
            /// <param name="cancellation"></param>
            /// <returns></returns>
            private bool ReadBytesPacket(byte[] packetBuffer, bool breakOnSeperator, CancellationTokenSource cancellation)
            {
                bool containSeperator = false;

                foreach (byte dataByte in packetBuffer)
                {
                    cancellation.Token.ThrowIfCancellationRequested();

                    char character = (char)dataByte;
                    if (character != '\0')
                    {
                        if (breakOnSeperator)
                        {
                            if (character == ClassPeerPacketSetting.PacketPeerSplitSeperator)
                            {
                                containSeperator = true;

                                break;
                            }

                            if (ClassUtility.CharIsABase64Character(character))
                            {
                                _memoryStream.WriteByte(dataByte);
                            }
                        }
                        else
                        {
                            if (character == ClassPeerPacketSetting.PacketPeerSplitSeperator)
                            {
                                containSeperator = true;
                            }
                            if (ClassUtility.CharIsABase64Character(character) || character == ClassPeerPacketSetting.PacketPeerSplitSeperator)
                            {
                                _memoryStream.WriteByte(dataByte);
                            }
                        }
                    }
                }

                return containSeperator;
            }

            /// <summary>
            /// Translate the packet received.
            /// </summary>
            /// <typeparam name="T"></typeparam>
            /// <param name="peerPacketRecvObject"></param>
            /// <param name="packetResponseExpected"></param>
            /// <returns></returns>
            private ClassTranslatePacket<T> TranslatePacketReceived<T>(ClassPeerPacketRecvObject peerPacketRecvObject, ClassPeerEnumPacketResponse packetResponseExpected)
            {
                ClassTranslatePacket<T> translatedPacket = new ClassTranslatePacket<T>();

                if (peerPacketRecvObject != null)
                {
                    if (!peerPacketRecvObject.PacketContent.IsNullOrEmpty())
                    {


                        bool peerIgnorePacketSignature = ClassPeerCheckManager.CheckPeerClientWhitelistStatus(_peerIpTarget, _peerUniqueIdTarget, _peerNetworkSettingObject);
                        bool packetSignatureStatus = true;

                        if (!peerIgnorePacketSignature)
                        {
                            packetSignatureStatus = CheckPacketSignature(_peerIpTarget,
                                                            _peerUniqueIdTarget,
                                                            _peerNetworkSettingObject,
                                                            peerPacketRecvObject.PacketContent,
                                                            packetResponseExpected,
                                                            peerPacketRecvObject.PacketHash,
                                                            peerPacketRecvObject.PacketSignature,
                                                            _peerCancellationToken);
                        }

                        if (packetSignatureStatus)
                        {
                            if (TryDecryptPacketPeerContent(_peerIpTarget, _peerPortTarget, _peerUniqueIdTarget, peerPacketRecvObject.PacketContent, _peerCancellationToken, out byte[] packetDecrypted))
                            {
                                if (packetDecrypted != null)
                                {
                                    if (packetDecrypted.Length > 0)
                                    {
                                        if (DeserializePacketContent(packetDecrypted.GetStringFromByteArrayAscii(), out translatedPacket.PacketTranslated))
                                        {
                                            if (!translatedPacket.PacketTranslated.Equals(default(T)))
                                            {
                                                translatedPacket.Status = true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                return translatedPacket;
            }

            #endregion

            #region Internal objects.

            /// <summary>
            /// Store the packet data translated after every checks passed.
            /// </summary>
            /// <typeparam name="T"></typeparam>
            internal class ClassTranslatePacket<T>
            {
                public T PacketTranslated;
                public bool Status;
            }

            #endregion
        }
    }
}

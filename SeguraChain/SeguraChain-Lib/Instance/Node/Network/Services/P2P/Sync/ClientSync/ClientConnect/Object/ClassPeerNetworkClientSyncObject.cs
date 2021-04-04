using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SeguraChain_Lib.Instance.Node.Network.Database;
using SeguraChain_Lib.Instance.Node.Network.Database.Manager;
using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Request;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ClientSync.ClientConnect.Object
{
    public class ClassPeerNetworkClientSyncObject : IDisposable
    {
        /// <summary>
        /// Tcp info and tcp client object.
        /// </summary>
        private TcpClient _peerTcpClient;
        public bool PeerConnectStatus;
        private bool _peerDisconnected;

        /// <summary>
        /// Peer informations.
        /// </summary>
        public string PeerIpTarget;
        public int PeerPortTarget;
        public string PeerUniqueIdTarget;

        /// <summary>
        /// Packet received.
        /// </summary>
        public ClassPeerPacketRecvObject PeerPacketReceived;
        private MemoryStream _memoryStreamPacketDataReceived;
        private long _lastPacketReceivedTimestamp;

        /// <summary>
        /// Peer task status.
        /// </summary>
        public bool PeerTaskStatus;
        private bool _peerTaskKeepAliveStatus;
        private CancellationTokenSource _peerCancellationTokenMain;
        private CancellationTokenSource _peerCancellationTokenDoConnection;
        private CancellationTokenSource _peerCancellationTokenTaskWaitPeerPacketResponse;
        private CancellationTokenSource _peerCancellationTokenTaskListenPeerPacketResponse;
        private CancellationTokenSource _peerCancellationTokenTaskSendPeerPacketKeepAlive;
        private SemaphoreSlim _semaphorePeer;

        /// <summary>
        /// Network settings.
        /// </summary>
        private ClassPeerNetworkSettingObject _peerNetworkSetting;
        private ClassPeerFirewallSettingObject _peerFirewallSettingObject;

        /// <summary>
        /// Specifications of the connection opened.
        /// </summary>
        private ClassPeerEnumPacketResponse _packetResponseExpected;
        private bool _keepAlive;

        #region Dispose functions

        private bool _disposed;


        ~ClassPeerNetworkClientSyncObject()
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
                PeerTaskStatus = false;
                CancelTaskWaitPeerPacketResponse();
                DisconnectFromTarget();
            }
            _disposed = true;
        }
        #endregion

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="peerIpTarget"></param>
        /// <param name="peerPort"></param>
        /// <param name="peerUniqueId"></param>
        /// <param name="peerNetworkSetting"></param>
        /// <param name="peerFirewallSettingObject"></param>
        public ClassPeerNetworkClientSyncObject(string peerIpTarget, int peerPort, string peerUniqueId, CancellationTokenSource peerCancellationTokenMain, ClassPeerNetworkSettingObject peerNetworkSetting, ClassPeerFirewallSettingObject peerFirewallSettingObject)
        {
            PeerIpTarget = peerIpTarget;
            PeerPortTarget = peerPort;
            PeerUniqueIdTarget = peerUniqueId;
            _peerNetworkSetting = peerNetworkSetting;
            _peerFirewallSettingObject = peerFirewallSettingObject;
            _memoryStreamPacketDataReceived = new MemoryStream();
            _peerCancellationTokenMain = peerCancellationTokenMain;
            _semaphorePeer = new SemaphoreSlim(1, 1);
        }

        /// <summary>
        /// Attempt to send a packet to a peer target.
        /// </summary>
        /// <param name="packet"></param>
        /// <param name="cancellation"></param>
        /// <param name="packetResponseExpected"></param>
        /// <param name="keepAlive"></param>
        /// <param name="broadcast"></param>
        /// <returns></returns>
        public async Task<bool> TrySendPacketToPeerTarget(string packet, CancellationTokenSource cancellation, ClassPeerEnumPacketResponse packetResponseExpected, bool keepAlive, bool broadcast)
        {
            bool result = false;
            bool semaphoreUsed = false;
            try
            {
                try
                {
                    await _semaphorePeer.WaitAsync(cancellation.Token);
                    semaphoreUsed = true;

                    _packetResponseExpected = packetResponseExpected;
                    _keepAlive = keepAlive;

                    #region Check the current connection status opened to the target.

                    if (PeerConnectStatus)
                    {
                        if (!CheckConnection())
                        {
                            DisconnectFromTarget();
                        }
                    }
                    else
                    {
                        DisconnectFromTarget();
                    }

                    #endregion

                    #region Clean up and cancel previous task.

                    CleanUpTask(cancellation);

                    #endregion

                    bool cancel = false;

                    #region Reconnect to the target ip if the connection is not opened or dead.

                    if (!PeerConnectStatus)
                    {
                        if (!await DoConnection(cancellation))
                        {
                            DisconnectFromTarget();
                            cancel = true;
                        }
                        else
                        {
                            _peerDisconnected = false;
                        }
                    }

                    #endregion

                    if (!cancel)
                    {
                        #region Send packet and wait packet response.


                        if (!await SendPeerPacket(packet, cancellation))
                        {
                            ClassPeerCheckManager.InputPeerClientNoPacketConnectionOpened(PeerIpTarget, PeerUniqueIdTarget, _peerNetworkSetting, _peerFirewallSettingObject);
                            DisconnectFromTarget();
                        }
                        else
                        {
                            if (!broadcast)
                            {
                                if (await WaitPacketExpected(cancellation))
                                {
                                    result = true;
                                }
                            }
                            else
                            {
                                result = true;
                            }
                        }

                        #endregion
                    }
                }
                catch
                {
                    // Ignored.
                }
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphorePeer.Release();
                }
            }
            CleanPacketDataReceived();
            return result;
        }

        #region Initialize connection functions.

        /// <summary>
        /// Clean the list of packets received.
        /// </summary>
        public void CleanPacketDataReceived()
        {
            _memoryStreamPacketDataReceived.Clear();
        }

        /// <summary>
        /// Clean up the task.
        /// </summary>
        private void CleanUpTask(CancellationTokenSource cancellation)
        {
            PeerPacketReceived = null;
            CancelTaskDoConnection();
            if (!PeerConnectStatus)
            {
                CancelTaskPeerPacketKeepAlive();
            }
            CancelTaskWaitPeerPacketResponse();
            CancelTaskListenPeerPacketResponse();
            CleanPacketDataReceived();
            _peerCancellationTokenTaskWaitPeerPacketResponse = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _peerCancellationTokenMain.Token);
        }

        /// <summary>
        /// Check the connection.
        /// </summary>
        private bool CheckConnection()
        {
            if (_peerTcpClient != null)
            {
                if (PeerConnectStatus)
                {
                    try
                    {
                        if (!ClassUtility.SocketIsConnected(_peerTcpClient))
                        {
                            PeerConnectStatus = false;
                        }
                    }
                    catch
                    {
                        PeerConnectStatus = false;
                    }
                }
            }
            else
            {
                PeerConnectStatus = false;
            }
            return PeerConnectStatus;
        }

        /// <summary>
        /// Do connection.
        /// </summary>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> DoConnection(CancellationTokenSource cancellation)
        {
            _peerTcpClient = new TcpClient();
            bool successConnect = false;
            bool taskDone = false;
            bool failed = false;
            CancelTaskDoConnection();
            _peerCancellationTokenDoConnection = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _peerCancellationTokenMain.Token);

            try
            {
                await Task.Factory.StartNew(async () =>
                {
                    while (!successConnect)
                    {
                        if (_peerCancellationTokenDoConnection.IsCancellationRequested)
                        {
                            break;
                        }

                        if (_peerTcpClient == null)
                        {
                            failed = true;
                            break;
                        }
                        if (_peerTcpClient.Client == null)
                        {
                            failed = true;
                            break;
                        }
                        try
                        {
                            await _peerTcpClient.ConnectAsync(PeerIpTarget, PeerPortTarget);
                            successConnect = true;
                            break;
                        }
                        catch
                        {
                            // Ignored.  
                        }

                        try
                        {
                            await Task.Delay(1000, cancellation.Token);
                        }
                        catch
                        {
                            break;
                        }
                    }

                    taskDone = true;

                }, _peerCancellationTokenDoConnection.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Ignored, catch the exception once the task is cancelled.
            }

            long timestampStartConnect = ClassUtility.GetCurrentTimestampInMillisecond();

            while (!successConnect)
            {
                if (timestampStartConnect + (_peerNetworkSetting.PeerMaxDelayToConnectToTarget * 1000) < ClassUtility.GetCurrentTimestampInMillisecond())
                {
                    break;
                }

                if (cancellation.IsCancellationRequested)
                {
                    break;
                }

                if (successConnect)
                {
                    break;
                }

                if (taskDone)
                {
                    break;
                }

                if (failed)
                {
                    break;
                }

                try
                {
                    await Task.Delay(10, cancellation.Token);
                }
                catch
                {
                    break;
                }
            }

            CancelTaskDoConnection();

            if (successConnect)
            {
                PeerConnectStatus = true;
                _peerDisconnected = false;
                return true;
            }

            DisconnectFromTarget();
            CancelTaskWaitPeerPacketResponse();
            PeerConnectStatus = false;
            ClassLog.WriteLine("Failed to connect to peer " + PeerIpTarget, ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_SYNC, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_LOWEST_PRIORITY);
            ClassPeerCheckManager.InputPeerClientAttemptConnect(PeerIpTarget, PeerUniqueIdTarget, _peerNetworkSetting, _peerFirewallSettingObject);
            return false;
        }


        /// <summary>
        /// Wait the packet expected.
        /// </summary>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> WaitPacketExpected(CancellationTokenSource cancellation)
        {

            TaskWaitPeerPacketResponse(cancellation);

            bool result = true;

            _lastPacketReceivedTimestamp = ClassUtility.GetCurrentTimestampInMillisecond();
            long timeSpendOnWaiting = 0;

            while (_lastPacketReceivedTimestamp + (_peerNetworkSetting.PeerMaxDelayAwaitResponse * 1000) >= ClassUtility.GetCurrentTimestampInMillisecond())
            {
                if (cancellation.IsCancellationRequested)
                {
                    break;
                }

                if (!PeerTaskStatus || !PeerConnectStatus)
                {
                    break;
                }

                if (PeerPacketReceived != null)
                {
                    PeerTaskStatus = false;
                    break;
                }

                if (timeSpendOnWaiting >= 1000)
                {
                    timeSpendOnWaiting = 0;

                    if (!ClassUtility.SocketIsConnected(_peerTcpClient))
                    {
                        PeerConnectStatus = false;
                        PeerTaskStatus = false;
                        break;
                    }
                }

                try
                {
                    await Task.Delay(100, cancellation.Token);
                    timeSpendOnWaiting += 100;
                }
                catch
                {
                    break;
                }
            }

            CancelTaskListenPeerPacketResponse();
            CancelTaskWaitPeerPacketResponse();
            CleanPacketDataReceived();

            if (PeerPacketReceived == null)
            {
                ClassLog.WriteLine("Peer " + PeerIpTarget + " don't send a response to the packet sent.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_SYNC, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_LOWEST_PRIORITY, true);
                DisconnectFromTarget();
                result = false;
            }
            else
            {
                if (!_keepAlive)
                {
                    DisconnectFromTarget();
                }
                else
                {
                    // Enable keep alive.
                    TaskEnablePeerPacketKeepAlive();
                }
            }


            return result;
        }

        #endregion

        #region Wait packet to receive functions.

        /// <summary>
        /// Task in waiting a packet of response sent by the peer target.
        /// </summary>
        private void TaskWaitPeerPacketResponse(CancellationTokenSource cancellation)
        {
            PeerPacketReceived = null;
            CancelTaskListenPeerPacketResponse();
            _peerCancellationTokenTaskListenPeerPacketResponse = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _peerCancellationTokenMain.Token);

            PeerTaskStatus = true;
            try
            {
                Task.Factory.StartNew(async () =>
                {
                    bool peerTargetExist = false;

                    try
                    {
                        byte[] packetBufferOnReceive = new byte[_peerNetworkSetting.PeerMaxPacketBufferSize];

                        using (NetworkStream networkStream = new NetworkStream(_peerTcpClient.Client))
                        {
                            while (PeerTaskStatus && PeerConnectStatus)
                            {
                                if (IsCancelledOrDisconnected())
                                {
                                    break;
                                }

                                int packetLength = await networkStream.ReadAsync(packetBufferOnReceive, 0, packetBufferOnReceive.Length, _peerCancellationTokenTaskListenPeerPacketResponse.Token);

                                if (packetLength > 0)
                                {
                                    if (!peerTargetExist)
                                    {
                                        peerTargetExist = ClassPeerDatabase.ContainsPeer(PeerIpTarget, PeerUniqueIdTarget);
                                    }
                                    if (peerTargetExist)
                                    {
                                        ClassPeerDatabase.DictionaryPeerDataObject[PeerIpTarget][PeerUniqueIdTarget].PeerLastPacketReceivedTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                                    }

                                    _lastPacketReceivedTimestamp = ClassUtility.GetCurrentTimestampInMillisecond();

                                    bool containSeperator = false;

                                    foreach (byte dataByte in packetBufferOnReceive)
                                    {
                                        if (IsCancelledOrDisconnected())
                                        {
                                            break;
                                        }

                                        char character = (char)dataByte;

                                        if (character != '\0')
                                        {
                                            if (character == ClassPeerPacketSetting.PacketPeerSplitSeperator)
                                            {
                                                containSeperator = true;
                                                break;
                                            }
                                            else
                                            {
                                                if (ClassUtility.CharIsABase64Character(character))
                                                {
                                                    _memoryStreamPacketDataReceived.WriteByte(dataByte);
                                                }
                                            }
                                        }
                                    }

                                    // Clean up.
                                    Array.Clear(packetBufferOnReceive, 0, packetBufferOnReceive.Length);

                                    if (containSeperator)
                                    {

                                        byte[] base64Packet = null;
                                        bool failed = false;

                                        try
                                        {
                                            base64Packet = Convert.FromBase64String(_memoryStreamPacketDataReceived.ToArray().GetStringFromByteArrayAscii());
                                        }
                                        catch
                                        {
                                            failed = true;
                                        }

                                        CleanPacketDataReceived();

                                        if (!failed)
                                        {
                                            if (ClassUtility.TryDeserialize(base64Packet.GetStringFromByteArrayAscii(), out ClassPeerPacketRecvObject peerPacketReceived, ObjectCreationHandling.Reuse))
                                            {
                                                if (peerPacketReceived != null)
                                                {
                                                    if (peerPacketReceived.PacketOrder != _packetResponseExpected)
                                                    {
                                                        PeerPacketReceived = null;
                                                        ClassPeerCheckManager.InputPeerClientInvalidPacket(PeerIpTarget, PeerUniqueIdTarget, _peerNetworkSetting, _peerFirewallSettingObject);
                                                    }
                                                    else
                                                    {
                                                        PeerPacketReceived = peerPacketReceived;
                                                    }
                                                }
                                                else
                                                {
                                                    PeerPacketReceived = null;
                                                    ClassPeerCheckManager.InputPeerClientInvalidPacket(PeerIpTarget, PeerUniqueIdTarget, _peerNetworkSetting, _peerFirewallSettingObject);
                                                }
                                            }

                                            if (base64Packet.Length > 0)
                                            {
                                                Array.Clear(base64Packet, 0, base64Packet.Length);
                                            }

                                            CancelTaskListenPeerPacketResponse();
                                            PeerTaskStatus = false;
                                            break;
                                        }
                                        else
                                        {
                                            CancelTaskListenPeerPacketResponse();
                                            PeerConnectStatus = false;
                                            PeerTaskStatus = false;
                                            break;
                                        }
                                    }

                                    // If above the max data to receive.
                                    if (_memoryStreamPacketDataReceived.Length >= ClassPeerPacketSetting.PacketMaxLengthReceive)
                                    {
                                        CleanPacketDataReceived();
                                    }
                                }
                                else
                                {
                                    PeerTaskStatus = false;
                                    break;
                                }
                                if (IsCancelledOrDisconnected())
                                {
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {
                        PeerTaskStatus = false;
                        if (!CheckConnection())
                        {
                            PeerConnectStatus = false;
                        }
                    }

                }, _peerCancellationTokenTaskWaitPeerPacketResponse.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Ignored, catch the exception once the task is cancelled.
            }
        }

        /// <summary>
        /// Cancel the token of the task who listen packet in receive from a peer target.
        /// </summary>
        public void CancelTaskWaitPeerPacketResponse()
        {
            try
            {
                if (_peerCancellationTokenTaskWaitPeerPacketResponse != null)
                {
                    if (!_peerCancellationTokenTaskWaitPeerPacketResponse.IsCancellationRequested)
                    {
                        _peerCancellationTokenTaskWaitPeerPacketResponse.Cancel();
                    }
                }
            }
            catch
            {
                // Ignored.
            }
        }

        /// <summary>
        /// Cancel the token dedicated to the networkstream who listen peer packets.
        /// </summary>
        private void CancelTaskListenPeerPacketResponse()
        {
            try
            {

                if (_peerCancellationTokenTaskListenPeerPacketResponse != null)
                {
                    if (!_peerCancellationTokenTaskListenPeerPacketResponse.IsCancellationRequested)
                    {
                        _peerCancellationTokenTaskListenPeerPacketResponse.Cancel();
                        _peerCancellationTokenTaskListenPeerPacketResponse.Dispose();
                    }
                }
            }
            catch
            {
                // Ignored.
            }
        }

        #endregion

        #region Enable Keep alive functions.

        /// <summary>
        /// Enable a task who send a packet of keep alive to the peer target.
        /// </summary>
        private void TaskEnablePeerPacketKeepAlive()
        {

            if (!_peerTaskKeepAliveStatus)
            {
                CancelTaskPeerPacketKeepAlive();
                _peerCancellationTokenTaskSendPeerPacketKeepAlive = CancellationTokenSource.CreateLinkedTokenSource(_peerCancellationTokenMain.Token);

                try
                {
                    Task.Factory.StartNew(async () =>
                    {
                        _peerTaskKeepAliveStatus = true;

                        while (PeerConnectStatus && _peerTaskKeepAliveStatus)
                        {
                            try
                            {
                                ClassPeerPacketSendObject sendObject = new ClassPeerPacketSendObject(_peerNetworkSetting.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[PeerIpTarget][PeerUniqueIdTarget].PeerInternPublicKey)
                                {
                                    PacketOrder = ClassPeerEnumPacketSend.ASK_KEEP_ALIVE,
                                    PacketContent = JsonConvert.SerializeObject(new ClassPeerPacketAskKeepAlive()
                                    {
                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                    }),
                                };

                                if (!await SendPeerPacket(JsonConvert.SerializeObject(sendObject), _peerCancellationTokenTaskSendPeerPacketKeepAlive))
                                {
                                    PeerConnectStatus = false;
                                    _peerTaskKeepAliveStatus = false;
                                    break;
                                }

                                await Task.Delay(5000, _peerCancellationTokenTaskSendPeerPacketKeepAlive.Token);
                            }
                            catch (SocketException)
                            {
                                _peerTaskKeepAliveStatus = false;
                                if (!CheckConnection())
                                {
                                    PeerConnectStatus = false;
                                }
                                break;
                            }
                            catch (TaskCanceledException)
                            {
                                _peerTaskKeepAliveStatus = false;
                                break;
                            }
                        }

                    }, _peerCancellationTokenTaskSendPeerPacketKeepAlive.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                }
                catch
                {
                    // Ignored, catch the exception once the task is cancelled.
                }
            }
        }

        /// <summary>
        /// Cancel the token of the task who send packet of keep alive to the part target.
        /// </summary>
        private void CancelTaskPeerPacketKeepAlive()
        {
            _peerTaskKeepAliveStatus = false;

            if (_peerCancellationTokenTaskSendPeerPacketKeepAlive != null)
            {
                if (!_peerCancellationTokenTaskSendPeerPacketKeepAlive.IsCancellationRequested)
                {
                    _peerCancellationTokenTaskSendPeerPacketKeepAlive.Cancel();
                    _peerCancellationTokenTaskSendPeerPacketKeepAlive.Dispose();
                }
            }
        }

        private void CancelTaskDoConnection()
        {
            if (_peerCancellationTokenDoConnection != null)
            {
                if (!_peerCancellationTokenDoConnection.IsCancellationRequested)
                {
                    _peerCancellationTokenDoConnection.Cancel();
                    _peerCancellationTokenDoConnection.Dispose();
                }
            }
        }

        #endregion

        #region Manage TCP Connection.

        /// <summary>
        /// Send a packet to the peer target.
        /// </summary>
        /// <param name="packet"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> SendPeerPacket(string packet, CancellationTokenSource cancellation)
        {
            try
            {
                if (_peerTcpClient.Connected)
                {
                    using (NetworkStream networkStream = new NetworkStream(_peerTcpClient.Client))
                    {
                        byte[] packetBytesToSend = ClassUtility.GetByteArrayFromStringAscii(Convert.ToBase64String(ClassUtility.GetByteArrayFromStringAscii(packet)) + ClassPeerPacketSetting.PacketPeerSplitSeperator);

                        if (!await networkStream.TrySendSplittedPacket(packetBytesToSend, cancellation, _peerNetworkSetting.PeerMaxPacketSplitedSendSize))
                        {
                            PeerConnectStatus = false;
                            return false;
                        }
                    }
                }
            }
            catch (Exception error)
            {
                ClassLog.WriteLine("Can't send packet to peer: " + PeerIpTarget + " | Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_SYNC, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_LOWEST_PRIORITY);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Disconnect from target.
        /// </summary>
        public void DisconnectFromTarget()
        {
            if (!_peerDisconnected || PeerConnectStatus)
            {
                PeerConnectStatus = false;
                CancelTaskDoConnection();
                CancelTaskPeerPacketKeepAlive();
                CancelTaskListenPeerPacketResponse();

                try
                {
                    if (_peerTcpClient != null)
                    {
                        _peerTcpClient.Close();
                    }
                }
                catch
                {
                    // Ignored.
                }

                _peerDisconnected = true;

                // Ensure to clean up malformed packet data.
                CleanPacketDataReceived();
            }
        }

        /// <summary>
        /// Indicate if the task of client sync is cancelled or connected.
        /// </summary>
        /// <returns></returns>
        private bool IsCancelledOrDisconnected()
        {
            if (!PeerTaskStatus || !PeerConnectStatus || _peerCancellationTokenTaskListenPeerPacketResponse.IsCancellationRequested || _peerCancellationTokenTaskWaitPeerPacketResponse.IsCancellationRequested)
            {
                return true;
            }

            return false;
        }

        #endregion
    }
}

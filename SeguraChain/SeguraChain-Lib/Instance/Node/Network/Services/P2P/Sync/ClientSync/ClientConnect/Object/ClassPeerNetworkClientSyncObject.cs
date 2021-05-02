using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SeguraChain_Lib.Instance.Node.Network.Database;
using SeguraChain_Lib.Instance.Node.Network.Database.Manager;
using SeguraChain_Lib.Instance.Node.Network.Enum.P2P.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.Model;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.Packet.SubPacket.Request;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Other.Object.List;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ClientSync.ClientConnect.Object
{
    public class ClassPeerNetworkClientSyncObject : IDisposable
    {
        /// <summary>
        /// Tcp info and tcp client object.
        /// </summary>
        private Socket _peerSocketClient;
        public bool PeerConnectStatus;

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
        public bool PeerPacketReceivedStatus;
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
                CleanUpTask();
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
            _peerCancellationTokenMain = peerCancellationTokenMain;
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
        public async Task<bool> TrySendPacketToPeerTarget(byte[] packet, CancellationTokenSource cancellation, ClassPeerEnumPacketResponse packetResponseExpected, bool keepAlive, bool broadcast)
        {
            bool result = false;

            try
            {

                #region Clean up and cancel previous task.

                CleanUpTask();

                #endregion

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


                #region Reconnect to the target ip if the connection is not opened or dead.

                if (!PeerConnectStatus)
                {
                    if (!DoConnection(cancellation))
                    {
                        DisconnectFromTarget();
                        return false;
                    }
                }

                #endregion

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
            catch (Exception error)
            {
                if (error is OperationCanceledException || error is TaskCanceledException)
                {
                    Dispose();
                }
            }


            return result;
        }

        #region Initialize connection functions.

        /// <summary>
        /// Clean up the task.
        /// </summary>
        private void CleanUpTask()
        {
            PeerPacketReceivedStatus = false;
            CancelTaskDoConnection();
            if (!PeerConnectStatus)
            {
                CancelTaskPeerPacketKeepAlive();
            }
            CancelTaskWaitPeerPacketResponse();
            CancelTaskListenPeerPacketResponse();
        }

        /// <summary>
        /// Check the connection.
        /// </summary>
        private bool CheckConnection()
        {
            if (_peerSocketClient != null)
            {
                try
                {
                    PeerConnectStatus = ClassUtility.SocketIsConnected(_peerSocketClient);
                }
                catch
                {
                    PeerConnectStatus = false;
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
        private bool DoConnection(CancellationTokenSource cancellation)
        {
            bool successConnect = false;
            CancelTaskDoConnection();
            _peerCancellationTokenDoConnection = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _peerCancellationTokenMain.Token);
            _peerSocketClient = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            try
            {

                Task taskConnect = _peerSocketClient.ConnectAsync(PeerIpTarget, PeerPortTarget);
                taskConnect.Wait(_peerNetworkSetting.PeerMaxDelayToConnectToTarget * 1000, _peerCancellationTokenDoConnection.Token);

#if NET5_0_OR_GREATER
                if (taskConnect.IsCompletedSuccessfully)
                {
                    if (CheckConnection())
                    {
                        successConnect = true;
                    }
                }
#else
                 if (taskConnect.IsCompleted)
                 {
                    if (CheckConnection())
                    {
                        successConnect = true;
                    }
                 }
#endif
            }
            catch
            {
                successConnect = false; 
            }


            if (successConnect)
            {
                PeerConnectStatus = true;
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

                    if (!ClassUtility.SocketIsConnected(_peerSocketClient))
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
            CancelTaskWaitPeerPacketResponse();
            CancelTaskListenPeerPacketResponse();
            _peerCancellationTokenTaskWaitPeerPacketResponse = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _peerCancellationTokenMain.Token);
            _peerCancellationTokenTaskListenPeerPacketResponse = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, _peerCancellationTokenMain.Token);

            PeerTaskStatus = true;
            try
            {
                Task.Factory.StartNew(async () =>
                {
                    bool peerTargetExist = false;

                    using (DisposableList<ClassReadPacketSplitted> listPacketReceived = new DisposableList<ClassReadPacketSplitted>())
                    {
                        listPacketReceived.Clear();
                        listPacketReceived.Add(new ClassReadPacketSplitted());

                        try
                        {
                            byte[] packetBufferOnReceive = new byte[_peerNetworkSetting.PeerMaxPacketBufferSize];

                            using (NetworkStream networkStream = new NetworkStream(_peerSocketClient))
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

                                        foreach (byte dataByte in packetBufferOnReceive.SkipWhile(x => x == 0).ToArray())
                                        {
                                            if (IsCancelledOrDisconnected()) break;


                                            char character = (char)dataByte;

                                            if (character != '\0')
                                            {

                                                if (character == ClassPeerPacketSetting.PacketPeerSplitSeperator)
                                                {
                                                    containSeperator = true;
                                                    listPacketReceived[listPacketReceived.Count - 1].Complete = true;
                                                    break;
                                                }
                                                else
                                                {
                                                    if (ClassUtility.CharIsABase64Character(character))
                                                    {
                                                        listPacketReceived[listPacketReceived.Count - 1].Packet.Add(dataByte);
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
                                                base64Packet = Convert.FromBase64String(listPacketReceived[listPacketReceived.Count - 1].Packet.ToArray().GetStringFromByteArrayAscii());
                                            }
                                            catch
                                            {
                                                failed = true;
                                            }

                                            if (!failed)
                                            {
                                                if (ClassUtility.TryDeserializePacket(base64Packet, out ClassPeerPacketRecvObject peerPacketReceived))
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
                                                            if (peerTargetExist)
                                                                ClassPeerDatabase.DictionaryPeerDataObject[PeerIpTarget][PeerUniqueIdTarget].PeerTimestampSignatureWhitelist = peerPacketReceived.PeerLastTimestampSignatureWhitelist;

                                                            PeerPacketReceived = peerPacketReceived;
                                                            PeerPacketReceivedStatus = true;
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

                                                PeerTaskStatus = false;
                                                break;
                                            }
                                            else
                                            {
                                                PeerTaskStatus = false;
                                                break;
                                            }
                                        }

                                        // If above the max data to receive.
                                        if (listPacketReceived.GetAll.Sum(x => x.Packet.Count) >= ClassPeerPacketSetting.PacketMaxLengthReceive)
                                        {
                                            listPacketReceived.Clear();
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
                                ClassPeerPacketSendObject sendObject = new ClassPeerPacketSendObject(_peerNetworkSetting.PeerUniqueId, ClassPeerDatabase.DictionaryPeerDataObject[PeerIpTarget][PeerUniqueIdTarget].PeerInternPublicKey, ClassPeerDatabase.DictionaryPeerDataObject[PeerIpTarget][PeerUniqueIdTarget].PeerClientLastTimestampPeerPacketSignatureWhitelist)
                                {
                                    PacketOrder = ClassPeerEnumPacketSend.ASK_KEEP_ALIVE,
                                    PacketContent = ClassUtility.SerializeData(new ClassPeerPacketAskKeepAlive()
                                    {
                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                    }),
                                };

                                if (!await SendPeerPacket(ClassUtility.SerializePacketData(sendObject), _peerCancellationTokenTaskSendPeerPacketKeepAlive))
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
        private async Task<bool> SendPeerPacket(byte[] packet, CancellationTokenSource cancellation)
        {
            try
            {
                if (_peerSocketClient.Connected)
                {
                    using (NetworkStream networkStream = new NetworkStream(_peerSocketClient))
                    {
                        byte[] packetBytesToSend = ClassUtility.GetByteArrayFromStringAscii(Convert.ToBase64String(packet) + ClassPeerPacketSetting.PacketPeerSplitSeperator);

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

            PeerConnectStatus = false;
            CancelTaskDoConnection();
            CancelTaskPeerPacketKeepAlive();
            CancelTaskListenPeerPacketResponse();

            try
            {
                if (_peerSocketClient != null)
                {
                    _peerSocketClient.Close();
                }
            }
            catch
            {
                // Ignored.
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

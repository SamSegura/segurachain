using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SeguraChain_Lib.Instance.Node.Network.Services.Firewall.Manager;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ServerSync.Client;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ServerSync.Object;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ServerSync.Service.Enum;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Instance.Node.Network.Services.P2P.Sync.ServerSync.Service
{
    public class ClassPeerNetworkSyncServerObject : IDisposable
    {
        public bool NetworkPeerServerStatus;
        private TcpListener _tcpListenerPeer;
        private CancellationTokenSource _cancellationTokenSourcePeerServer;
        private ConcurrentDictionary<string, ClassPeerIncomingConnectionObject> _listPeerIncomingConnectionObject;
        public string PeerIpOpenNatServer;
        private ClassPeerNetworkSettingObject _peerNetworkSettingObject;
        private ClassPeerFirewallSettingObject _firewallSettingObject;


        #region Dispose functions

        private bool _disposed;

        ~ClassPeerNetworkSyncServerObject()
        {
            Dispose(true);
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
                StopPeerServer();
            }
            _disposed = true;
        }
        #endregion

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="peerIpOpenNatServer">The Public IP of the node, permit to ignore it on sync.</param>
        /// <param name="peerNetworkSettingObject">The network setting object.</param>
        /// <param name="firewallSettingObject">The firewall setting object.</param>
        public ClassPeerNetworkSyncServerObject(string peerIpOpenNatServer, ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject firewallSettingObject)
        {
            PeerIpOpenNatServer = peerIpOpenNatServer;
            _peerNetworkSettingObject = peerNetworkSettingObject;
            _firewallSettingObject = firewallSettingObject;
        }

        #region Peer Server management functions.

        /// <summary>
        /// Start the peer server listening task.
        /// </summary>
        /// <returns>Return if the binding and the execution of listening incoming connection work.</returns>
        public bool StartPeerServer()
        {

            if (_listPeerIncomingConnectionObject == null)
            {
                _listPeerIncomingConnectionObject = new ConcurrentDictionary<string, ClassPeerIncomingConnectionObject>();
            }
            else
            {
                CleanUpAllIncomingConnection();
            }

            try
            {
                _tcpListenerPeer = new TcpListener(IPAddress.Parse(_peerNetworkSettingObject.ListenIp), _peerNetworkSettingObject.ListenPort);
                _tcpListenerPeer.Start();
            }
            catch (Exception error)
            {
                ClassLog.WriteLine("Error on initialize TCP Listener of the Peer. | Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);
                return false;
            }

            NetworkPeerServerStatus = true;
            _cancellationTokenSourcePeerServer = new CancellationTokenSource();
            try
            {
                Task.Factory.StartNew(async () =>
                {
                    while (NetworkPeerServerStatus)
                    {
                        try
                        {

                            await _tcpListenerPeer.AcceptTcpClientAsync().ContinueWith(async clientTask =>
                            {
                                try
                                {
                                    var clientPeerTcp = await clientTask;

                                    if (clientPeerTcp != null)
                                    {
                                        await Task.Factory.StartNew(async () =>
                                        {
                                            string clientIp = ((IPEndPoint)(clientPeerTcp.Client.RemoteEndPoint)).Address.ToString();

                                            switch (await HandleIncomingConnection(clientIp, clientPeerTcp, PeerIpOpenNatServer))
                                            {
                                                case ClassPeerNetworkServerHandleConnectionEnum.TOO_MUCH_ACTIVE_CONNECTION_CLIENT:
                                                case ClassPeerNetworkServerHandleConnectionEnum.BAD_CLIENT_STATUS:
                                                    if (_firewallSettingObject.PeerEnableFirewallLink)
                                                    {
                                                        ClassPeerFirewallManager.InsertInvalidPacket(clientIp);
                                                    }
                                                    CloseTcpClient(clientPeerTcp);
                                                    break;
                                                case ClassPeerNetworkServerHandleConnectionEnum.HANDLE_CLIENT_EXCEPTION:
                                                case ClassPeerNetworkServerHandleConnectionEnum.INSERT_CLIENT_IP_EXCEPTION:
                                                    CloseTcpClient(clientPeerTcp);
                                                    break;
                                            }
                                        }, _cancellationTokenSourcePeerServer.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                                    }
                                }
                                catch
                                {
                                    // Ignored catch the exception once the task is cancelled.
                                }
                            }, _cancellationTokenSourcePeerServer.Token);
                        }
                        catch
                        {
                            // Ignored, catch the exception once the task is cancelled.
                        }
                    }
                }, _cancellationTokenSourcePeerServer.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Ignored, catch the exception once the task is cancelled.
            }
            return true;
        }


        /// <summary>
        /// Stop the peer server listening.
        /// </summary>
        public void StopPeerServer()
        {
            if (NetworkPeerServerStatus)
            {
                NetworkPeerServerStatus = false;
                try
                {
                    if (_cancellationTokenSourcePeerServer != null)
                    {
                        if (!_cancellationTokenSourcePeerServer.IsCancellationRequested)
                        {
                            _cancellationTokenSourcePeerServer.Cancel();
                        }
                    }
                }
                catch
                {
                    // Ignored.
                }
                try
                {
                    _tcpListenerPeer.Stop();
                }
                catch
                {
                    // Ignored.
                }

                CleanUpAllIncomingConnection();
            }
        }

        /// <summary>
        /// Handle incoming connection.
        /// </summary>
        /// <param name="clientIp">The ip of the incoming connection.</param>
        /// <param name="clientPeerTcp">The tcp client object of the incoming connection.</param>
        /// <param name="peerIpOpenNatServer">The public ip of the server.</param>
        /// <returns>Return the handling status of the incoming connection.</returns>
        private async Task<ClassPeerNetworkServerHandleConnectionEnum> HandleIncomingConnection(string clientIp, TcpClient clientPeerTcp, string peerIpOpenNatServer)
        {
            try
            {

                if (_firewallSettingObject.PeerEnableFirewallLink)
                {
                    if (!ClassPeerFirewallManager.CheckClientIpStatus(clientIp))
                    {
                        return ClassPeerNetworkServerHandleConnectionEnum.BAD_CLIENT_STATUS;
                    }
                }

                if (GetTotalActiveConnection(clientIp) < _peerNetworkSettingObject.PeerMaxNodeConnectionPerIp)
                {
                    bool checkPeerClientStatus;

                    // Do not check the client ip if this is the same connection.
                    if (_peerNetworkSettingObject.ListenIp == clientIp)
                    {
                        checkPeerClientStatus = true;
                    }
                    // Check the status of the incoming IP connection of a Peer Client to allow or not it.
                    else
                    {
                        if (_firewallSettingObject.PeerEnableFirewallLink)
                        {
                            if (!ClassPeerFirewallManager.CheckClientIpStatus(clientIp))
                            {
                                checkPeerClientStatus = false;
                            }
                            else
                            {
                                checkPeerClientStatus = true;
                            }
                        }
                        else
                        {
                            checkPeerClientStatus = true;
                        }
                    }

                    if (!checkPeerClientStatus)
                    {
                        return ClassPeerNetworkServerHandleConnectionEnum.BAD_CLIENT_STATUS;
                    }

                    if (_listPeerIncomingConnectionObject.Count < int.MaxValue - 1)
                    {
                        // If it's a new incoming connection, create a new index of incoming connection.
                        if (!_listPeerIncomingConnectionObject.ContainsKey(clientIp))
                        {
                            if (!_listPeerIncomingConnectionObject.TryAdd(clientIp, new ClassPeerIncomingConnectionObject()))
                            {
                                if (!_listPeerIncomingConnectionObject.ContainsKey(clientIp))
                                {
                                    return ClassPeerNetworkServerHandleConnectionEnum.INSERT_CLIENT_IP_EXCEPTION;
                                }
                            }
                        }

                        #region Ensure to not have too much incoming connection from the same ip.

                        CancellationTokenSource cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSourcePeerServer.Token);

                        long randomId = ClassUtility.GetRandomBetweenLong(1, long.MaxValue - 1);

                        while (_listPeerIncomingConnectionObject[clientIp].ListPeerClientObject.ContainsKey(randomId))
                        {
                            randomId = ClassUtility.GetRandomBetweenLong(1, long.MaxValue - 1);
                        }


                        if (GetTotalActiveConnection(clientIp) < _peerNetworkSettingObject.PeerMaxNodeConnectionPerIp)
                        {
                            if (!_listPeerIncomingConnectionObject[clientIp].ListPeerClientObject.TryAdd(randomId, new ClassPeerNetworkClientServerObject(clientPeerTcp, cancellationToken, clientIp, peerIpOpenNatServer, _peerNetworkSettingObject, _firewallSettingObject)))
                            {
                                return ClassPeerNetworkServerHandleConnectionEnum.HANDLE_CLIENT_EXCEPTION;
                            }
                        }
                        else
                        {

                            if (CleanUpInactiveConnectionFromClientIpTarget(clientIp) > 0)
                            {
                                if (!_listPeerIncomingConnectionObject[clientIp].ListPeerClientObject.TryAdd(randomId, new ClassPeerNetworkClientServerObject(clientPeerTcp, cancellationToken, clientIp, peerIpOpenNatServer, _peerNetworkSettingObject, _firewallSettingObject)))
                                {
                                    return ClassPeerNetworkServerHandleConnectionEnum.HANDLE_CLIENT_EXCEPTION;
                                }
                            }
                            else
                            {
                                if (GetTotalActiveConnection(clientIp) > _peerNetworkSettingObject.PeerMaxNodeConnectionPerIp)
                                {
                                    return ClassPeerNetworkServerHandleConnectionEnum.TOO_MUCH_ACTIVE_CONNECTION_CLIENT;
                                }
                            }
                        }

                        #endregion


                        bool useSemaphore = false;
                        bool failed = true;

                        try
                        {
                            long timestampStartAwait = ClassUtility.GetCurrentTimestampInMillisecond();

                            #region Wait the semaphore access availability.

                            while (timestampStartAwait + _peerNetworkSettingObject.PeerMaxSemaphoreConnectAwaitDelay >= ClassUtility.GetCurrentTimestampInMillisecond())
                            {
                                if (await _listPeerIncomingConnectionObject[clientIp].SemaphoreHandleConnection.WaitAsync(10, _cancellationTokenSourcePeerServer.Token))
                                {
                                    useSemaphore = true;
                                    break;
                                }
                            }

                            #endregion

                            if (useSemaphore)
                            {
                                #region Handle the incoming connection to the P2P server.
                                await _listPeerIncomingConnectionObject[clientIp].ListPeerClientObject[randomId].HandlePeerClient();
                                #endregion

                                _listPeerIncomingConnectionObject[clientIp].SemaphoreHandleConnection.Release();
                                useSemaphore = false;

                                failed = false;
                            }
                            else CloseTcpClient(clientPeerTcp);
                        }
                        finally
                        {
                            if (useSemaphore)
                                _listPeerIncomingConnectionObject[clientIp].SemaphoreHandleConnection.Release();

                            if (failed)
                            {
                                CloseTcpClient(clientPeerTcp);
                                _listPeerIncomingConnectionObject[clientIp].ListPeerClientObject[randomId].Dispose();
                            }
                        }

                    }
                    else
                    {
                        return ClassPeerNetworkServerHandleConnectionEnum.INSERT_CLIENT_IP_EXCEPTION;
                    }

                }
                else
                {
                    return ClassPeerNetworkServerHandleConnectionEnum.TOO_MUCH_ACTIVE_CONNECTION_CLIENT;
                }
            }
            catch
            {
                return ClassPeerNetworkServerHandleConnectionEnum.HANDLE_CLIENT_EXCEPTION;
            }
            return ClassPeerNetworkServerHandleConnectionEnum.VALID_HANDLE;
        }

        /// <summary>
        /// Close an incoming tcp client connection.
        /// </summary>
        /// <param name="tcpClient"></param>
        private void CloseTcpClient(TcpClient tcpClient)
        {
            try
            {
                if (tcpClient?.Client != null)
                {
                    if (tcpClient.Client.Connected)
                    {
                        try
                        {
                            tcpClient.Client.Shutdown(SocketShutdown.Both);
                        }
                        finally
                        {
                            if (tcpClient?.Client != null)
                            {
                                if (tcpClient.Client.Connected)
                                {
                                    tcpClient.Client.Close();
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
        }

        #endregion

        #region Stats connections functions.

        /// <summary>
        /// Get the total active connection amount of a client ip.
        /// </summary>
        /// <param name="clientIp">The client IP.</param>
        /// <returns>Return the amount of total connection of a client IP.</returns>
        private int GetTotalActiveConnection(string clientIp)
        {
            try
            {
                if (_listPeerIncomingConnectionObject.Count > 0)
                {
                    if (_listPeerIncomingConnectionObject.ContainsKey(clientIp))
                    {
                        if (_listPeerIncomingConnectionObject[clientIp].ListPeerClientObject.Count > 0)
                        {
                            return _listPeerIncomingConnectionObject[clientIp].ListPeerClientObject.Count(x => x.Value.ClientPeerConnectionStatus);
                        }
                    }
                }
            }
            catch
            {
                // Ignored.
            }
            return 0;
        }

        /// <summary>
        /// Get the total of all active connections of all client ip's.
        /// </summary>
        /// <returns>Return the amount of total active connections.</returns>
        public long GetAllTotalActiveConnection()
        {
            long totalActiveConnection = 0;

            if (_listPeerIncomingConnectionObject.Count > 0)
            {
                List<string> listClientIpKeys = new List<string>(_listPeerIncomingConnectionObject.Keys);

                foreach (var clientIpKey in listClientIpKeys)
                {

                    if (_listPeerIncomingConnectionObject[clientIpKey].ListPeerClientObject.Count > 0)
                    {
                        try
                        {
                            totalActiveConnection += _listPeerIncomingConnectionObject[clientIpKey].ListPeerClientObject.Count(x => x.Value.ClientPeerConnectionStatus);
                        }
                        catch
                        {
                            // ignored.
                        }
                    }
                
                   
                }

                // Clean up.
                listClientIpKeys.Clear();
            }

            return totalActiveConnection;
        }

        /// <summary>
        /// Clean up inactive connection from client ip target.
        /// </summary>
        /// <param name="clientIp">The client IP.</param>
        public int CleanUpInactiveConnectionFromClientIpTarget(string clientIp)
        {
            int totalRemoved = 0;
            if (_listPeerIncomingConnectionObject.Count > 0)
            {
                if (_listPeerIncomingConnectionObject.ContainsKey(clientIp))
                {
                    if (_listPeerIncomingConnectionObject[clientIp].ListPeerClientObject.Count > 0)
                    {
                        _listPeerIncomingConnectionObject[clientIp].OnCleanUp = true;
                        try
                        {

                            foreach (var key in _listPeerIncomingConnectionObject[clientIp].ListPeerClientObject.Keys.ToArray())
                            {
                                bool remove = false;
                                if (_listPeerIncomingConnectionObject[clientIp].ListPeerClientObject[key] == null)
                                {
                                    remove = true;
                                }
                                else
                                {
                                    if (!_listPeerIncomingConnectionObject[clientIp].ListPeerClientObject[key].ClientPeerConnectionStatus && _listPeerIncomingConnectionObject[clientIp].ListPeerClientObject[key].ClientPeerLastPacketReceived + _peerNetworkSettingObject.PeerMaxDelayConnection < ClassUtility.GetCurrentTimestampInSecond())
                                    {
                                        _listPeerIncomingConnectionObject[clientIp].ListPeerClientObject[key].ClosePeerClient(false);
                                        remove = true;
                                    }
                                }

                                if (remove)
                                {

                                    if (_listPeerIncomingConnectionObject[clientIp].ListPeerClientObject.TryRemove(key, out _))
                                    {
                                        totalRemoved++;
                                    }
                                }
                            }

                        }
                        catch
                        {
                            // Ignored.
                        }
                        _listPeerIncomingConnectionObject[clientIp].OnCleanUp = false;
                    }
                }
            }
            return totalRemoved;
        }

        /// <summary>
        /// Clean up all incoming closed connection.
        /// </summary>
        /// <param name="totalIp">The number of ip's returned from the amount of closed connections cleaned.</param>
        /// <returns>Return the amount of total connection closed.</returns>
        public long CleanUpAllIncomingClosedConnection(out int totalIp)
        {
            long totalConnectionClosed = 0;
            totalIp = 0;
            if (_listPeerIncomingConnectionObject.Count > 0)
            {

                List<string> peerIncomingConnectionKeyList = new List<string>(_listPeerIncomingConnectionObject.Keys);

                foreach (var peerIncomingConnectionKey in peerIncomingConnectionKeyList)
                {

                    if (_listPeerIncomingConnectionObject[peerIncomingConnectionKey] != null)
                    {
                        if (_listPeerIncomingConnectionObject[peerIncomingConnectionKey].ListPeerClientObject.Count > 0)
                        {

                            long totalClosed = CleanUpInactiveConnectionFromClientIpTarget(peerIncomingConnectionKey);

                            if (totalClosed > 0)
                            {
                                totalIp++;
                            }

                            totalConnectionClosed += totalClosed;

                        }
                    }

                }

                // Clean up.
                peerIncomingConnectionKeyList.Clear();

            }
            return totalConnectionClosed;
        }

        /// <summary>
        /// Clean up all incoming connection.
        /// </summary>
        /// <returns>Return the amount of incoming connection closed.</returns>
        public long CleanUpAllIncomingConnection()
        {
            long totalConnectionClosed = 0;

            if (_listPeerIncomingConnectionObject.Count > 0)
            {

                List<string> peerIncomingConnectionKeyList = new List<string>(_listPeerIncomingConnectionObject.Keys);

                foreach (var peerIncomingConnectionKey in peerIncomingConnectionKeyList)
                {
                    if (_listPeerIncomingConnectionObject[peerIncomingConnectionKey] != null)
                    {
                        if (_listPeerIncomingConnectionObject[peerIncomingConnectionKey].ListPeerClientObject.Count > 0)
                        {
                            _listPeerIncomingConnectionObject[peerIncomingConnectionKey].OnCleanUp = true;

                            foreach (var key in _listPeerIncomingConnectionObject[peerIncomingConnectionKey].ListPeerClientObject.Keys.ToArray())
                            {
                                try
                                {

                                    if (_listPeerIncomingConnectionObject[peerIncomingConnectionKey].ListPeerClientObject[key].ClientPeerConnectionStatus)
                                    {
                                        totalConnectionClosed++;
                                    }
                                    _listPeerIncomingConnectionObject[peerIncomingConnectionKey].ListPeerClientObject[key].ClosePeerClient(false);
                                }
                                catch
                                {
                                    // Ignored.
                                }
                            }

                            _listPeerIncomingConnectionObject[peerIncomingConnectionKey].OnCleanUp = false;
                        }
                    }
                }

                // Clean up.
                peerIncomingConnectionKeyList.Clear();
                _listPeerIncomingConnectionObject.Clear();

            }

            return totalConnectionClosed;
        }

        #endregion
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Client;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Server.Enum;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Server.Object;
using SeguraChain_Lib.Instance.Node.Network.Services.Firewall.Manager;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Instance.Node.Network.Services.API.Server.Service
{
    public class ClassPeerApiServerServiceObject : IDisposable
    {
        public bool NetworkPeerApiServerStatus;
        private TcpListener _tcpListenerPeerApi;
        private CancellationTokenSource _cancellationTokenSourcePeerApiServer;
        private ConcurrentDictionary<string, ClassPeerApiIncomingConnectionObject> _listApiIncomingConnectionObject;

        private ClassPeerNetworkSettingObject _peerNetworkSettingObject;
        private ClassPeerFirewallSettingObject _firewallSettingObject;
        private string _peerIpOpenNatServer;


        #region Dispose functions
        private bool _disposed;


        ~ClassPeerApiServerServiceObject()
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
                StopPeerApiServer();
            }
            _disposed = true;
        }
        #endregion


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="peerIpOpenNatServer">The OpenNAT IP Server.</param>
        /// <param name="peerNetworkSettingObject">The network setting object.</param>
        /// <param name="firewallSettingObject">The firewall setting object.</param>
        public ClassPeerApiServerServiceObject(string peerIpOpenNatServer, ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject firewallSettingObject)
        {
            _peerIpOpenNatServer = peerIpOpenNatServer;
            _peerNetworkSettingObject = peerNetworkSettingObject;
            _firewallSettingObject = firewallSettingObject;
        }

        #region Peer API Server management functions.

        /// <summary>
        /// Start the API Server of the Peer.
        /// </summary>
        /// <returns></returns>
        public bool StartPeerApiServer()
        {

            if (_listApiIncomingConnectionObject == null)
            {
                _listApiIncomingConnectionObject = new ConcurrentDictionary<string, ClassPeerApiIncomingConnectionObject>();
            }
            else
            {
               CleanUpAllIncomingConnection();
            }

            try
            {
                _tcpListenerPeerApi = new TcpListener(IPAddress.Parse(_peerNetworkSettingObject.ListenApiIp), _peerNetworkSettingObject.ListenApiPort);
                _tcpListenerPeerApi.Start();
            }
            catch (Exception error)
            {
                ClassLog.WriteLine("Error on initialize TCP Listener of the Peer. | Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);
                return false;
            }

            NetworkPeerApiServerStatus = true;
            _cancellationTokenSourcePeerApiServer = new CancellationTokenSource();

            try
            {
                Task.Factory.StartNew(async () =>
                {
                    while (NetworkPeerApiServerStatus)
                    {
                        try
                        {
                            await _tcpListenerPeerApi.AcceptTcpClientAsync().ContinueWith(async clientTask =>
                            {
                                try
                                {
                                    TcpClient clientApiTcp = await clientTask;



                                    string clientIp = ((IPEndPoint)(clientApiTcp.Client.RemoteEndPoint)).Address.ToString();

                                    switch (HandleIncomingConnection(clientIp, clientApiTcp))
                                    {
                                        case ClassPeerApiHandleIncomingConnectionEnum.HANDLE_CLIENT_EXCEPTION:
                                            CloseTcpClient(clientApiTcp);
                                            break;
                                        case ClassPeerApiHandleIncomingConnectionEnum.INSERT_CLIENT_IP_EXCEPTION:
                                        case ClassPeerApiHandleIncomingConnectionEnum.TOO_MUCH_ACTIVE_CONNECTION_CLIENT:
                                            if (_firewallSettingObject.PeerEnableFirewallLink)
                                            {
                                                ClassPeerFirewallManager.InsertInvalidPacket(clientIp);
                                            }
                                            CloseTcpClient(clientApiTcp);

                                            break;
                                    }
                                }
                                catch
                                {
                                    // Ignored catch the exception once the task is cancelled.
                                }
                            }, _cancellationTokenSourcePeerApiServer.Token);
                        }
                        catch
                        {
                            // Ignored, catch the exception once the task is cancelled.
                        }
                    }
                }, _cancellationTokenSourcePeerApiServer.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Ignored, catch the exception once the task is cancelled.
            }
            return true;
        }
    
        /// <summary>
        /// Stop the API Server of the Peer.
        /// </summary>
        public void StopPeerApiServer()
        {
            if (NetworkPeerApiServerStatus)
            {
                NetworkPeerApiServerStatus = false;
                try
                {
                    if (_cancellationTokenSourcePeerApiServer != null)
                    {
                        if (!_cancellationTokenSourcePeerApiServer.IsCancellationRequested)
                        {
                            _cancellationTokenSourcePeerApiServer.Cancel();
                        }
                    }
                }
                catch
                {
                    // Ignored.
                }
                try
                {
                    _tcpListenerPeerApi.Stop();
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
        /// <param name="clientIp"></param>
        /// <param name="clientApiTcp"></param>
        private ClassPeerApiHandleIncomingConnectionEnum HandleIncomingConnection(string clientIp, TcpClient clientApiTcp)
        {
            try
            {
                if (GetTotalActiveConnection(clientIp) < _peerNetworkSettingObject.PeerMaxApiConnectionPerIp)
                {
                    // Check the client ip status.
                    var clientIpStatus = !_firewallSettingObject.PeerEnableFirewallLink || ClassPeerFirewallManager.CheckClientIpStatus(clientIp);

                    if (!clientIpStatus)
                    {
                        return ClassPeerApiHandleIncomingConnectionEnum.BAD_CLIENT_STATUS;
                    }

                    if (_listApiIncomingConnectionObject.Count < int.MaxValue - 1)
                    {
                        // If it's a new incoming connection, create a new index of incoming connection.
                        if (!_listApiIncomingConnectionObject.ContainsKey(clientIp))
                        {
                            if (!_listApiIncomingConnectionObject.TryAdd(clientIp, new ClassPeerApiIncomingConnectionObject()))
                            {
                                if (!_listApiIncomingConnectionObject.ContainsKey(clientIp))
                                {
                                    return ClassPeerApiHandleIncomingConnectionEnum.INSERT_CLIENT_IP_EXCEPTION;
                                }
                            }
                        }

                        #region Ensure to not have too much incoming connection from the same ip.

                        CancellationTokenSource cancellationTokenSourceClientPeer = new CancellationTokenSource();

                        string randomWordHashed = ClassUtility.GenerateSha3512FromString(ClassUtility.GetRandomWord(32));

                        while(_listApiIncomingConnectionObject[clientIp].ListeApiClientObject.ContainsKey(randomWordHashed))
                        {
                            randomWordHashed = ClassUtility.GenerateSha3512FromString(ClassUtility.GetRandomWord(32));
                        }

                        if (_listApiIncomingConnectionObject[clientIp].ListeApiClientObject.Count < _peerNetworkSettingObject.PeerMaxApiConnectionPerIp)
                        {

                            if (!_listApiIncomingConnectionObject[clientIp].ListeApiClientObject.TryAdd(randomWordHashed, new ClassPeerApiClientObject(clientApiTcp, clientIp, _peerNetworkSettingObject, _firewallSettingObject, _peerIpOpenNatServer, cancellationTokenSourceClientPeer)))
                            {
                                return ClassPeerApiHandleIncomingConnectionEnum.HANDLE_CLIENT_EXCEPTION;
                            }
                        }
                        else
                        {
                            if (CleanUpInactiveConnectionFromClientIpTarget(clientIp) > 0 || _listApiIncomingConnectionObject[clientIp].ListeApiClientObject.Count < _peerNetworkSettingObject.PeerMaxApiConnectionPerIp)
                            {
                                if (!_listApiIncomingConnectionObject[clientIp].ListeApiClientObject.TryAdd(randomWordHashed, new ClassPeerApiClientObject(clientApiTcp, clientIp, _peerNetworkSettingObject, _firewallSettingObject, _peerIpOpenNatServer, cancellationTokenSourceClientPeer)))
                                {
                                    return ClassPeerApiHandleIncomingConnectionEnum.HANDLE_CLIENT_EXCEPTION;
                                }
                            }
                            else
                            {
                                ClassLog.WriteLine("Too much listed connection IP registered. Incoming connection from IP: " + clientIp + " closed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                return ClassPeerApiHandleIncomingConnectionEnum.TOO_MUCH_ACTIVE_CONNECTION_CLIENT;
                            }

                        }

                        #endregion


                        // Handle the incoming connection.
                        Task.Factory.StartNew(async () =>
                        {
                            bool tokenCancelled = false;
                            while (_listApiIncomingConnectionObject[clientIp].OnCleanUp)
                            {
                                if (_cancellationTokenSourcePeerApiServer.IsCancellationRequested)
                                {
                                    tokenCancelled = true;
                                    break;
                                }
                                await Task.Delay(100, cancellationTokenSourceClientPeer.Token);
                            }

                            if (!tokenCancelled)
                            {
                                try
                                {
                                    bool available = false;
                                    long timestampStartAwait = ClassUtility.GetCurrentTimestampInMillisecond();

                                    while (timestampStartAwait + _peerNetworkSettingObject.PeerMaxSemaphoreConnectAwaitDelay > ClassUtility.GetCurrentTimestampInMillisecond())
                                    {
                                        if (await _listApiIncomingConnectionObject[clientIp].SemaphoreHandleConnection.WaitAsync(100, _cancellationTokenSourcePeerApiServer.Token))
                                        {
                                            available = true;
                                            break;
                                        }

                                        try
                                        {
                                            await Task.Delay(10, _cancellationTokenSourcePeerApiServer.Token);
                                        }
                                        catch
                                        {
                                            break;
                                        }
                                    }

                                    if (available)
                                    {
                                        _listApiIncomingConnectionObject[clientIp].SemaphoreHandleConnection.Release();

                                        #region Handle the incoming connection to the api.

                                        try
                                        {
                                            await _listApiIncomingConnectionObject[clientIp].ListeApiClientObject[randomWordHashed].HandleApiClientConnection();
                                        }
                                        catch (Exception error)
                                        {
                                            ClassLog.WriteLine("Error on task of handling incoming api connection object. Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_PEER_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                            CloseTcpClient(clientApiTcp);
                                        }

                                        #endregion
                                    }
                                    // Busy
                                    else
                                    {
                                        #region Force to close the incoming connection and dispose the new object created who handle it on this case.

                                        try
                                        {
                                            _listApiIncomingConnectionObject[clientIp].ListeApiClientObject[randomWordHashed].ClientConnectionStatus = false;
                                            if (_listApiIncomingConnectionObject[clientIp].ListeApiClientObject[randomWordHashed].CancellationTokenApiClient != null)
                                            {
                                                if (!_listApiIncomingConnectionObject[clientIp].ListeApiClientObject[randomWordHashed].CancellationTokenApiClient.IsCancellationRequested)
                                                {
                                                    _listApiIncomingConnectionObject[clientIp].ListeApiClientObject[randomWordHashed].CancellationTokenApiClient.Cancel();
                                                }
                                            }
                                        }
                                        catch
                                        {
                                            // Ignored.
                                        }

                                        CloseTcpClient(clientApiTcp);
                                        _listApiIncomingConnectionObject[clientIp].ListeApiClientObject[randomWordHashed].Dispose();

                                        #endregion
                                    }
                                }
                                catch
                                {
                                    // Ignored, just in case the semaphore is released if he is used.
                                    if (_listApiIncomingConnectionObject[clientIp].SemaphoreHandleConnection.CurrentCount == 0)
                                    {
                                        _listApiIncomingConnectionObject[clientIp].SemaphoreHandleConnection.Release();
                                    }
                                    CloseTcpClient(clientApiTcp);
                                    _listApiIncomingConnectionObject[clientIp].ListeApiClientObject[randomWordHashed].Dispose();
                                }
                            }
                            // Long clean up busy.
                            else
                            {
                                _listApiIncomingConnectionObject[clientIp].ListeApiClientObject[randomWordHashed].Dispose();
                                CloseTcpClient(clientApiTcp);
                            }


                        }, _cancellationTokenSourcePeerApiServer.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                    }
                    else
                    {
                        return ClassPeerApiHandleIncomingConnectionEnum.INSERT_CLIENT_IP_EXCEPTION;
                    }

                }
                else
                {
                    return ClassPeerApiHandleIncomingConnectionEnum.TOO_MUCH_ACTIVE_CONNECTION_CLIENT;
                }
            }
            catch
            {
                // Ignored, catch the exception once cancelled.
                return ClassPeerApiHandleIncomingConnectionEnum.HANDLE_CLIENT_EXCEPTION;
            }

            return ClassPeerApiHandleIncomingConnectionEnum.VALID_HANDLE;
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
        /// Get the total active connection amount.
        /// </summary>
        /// <returns></returns>
        private int GetTotalActiveConnection(string clientIp)
        {
            try
            {
                if (_listApiIncomingConnectionObject.Count > 0)
                {
                    if (_listApiIncomingConnectionObject.ContainsKey(clientIp))
                    {
                        if (_listApiIncomingConnectionObject[clientIp].ListeApiClientObject.Count > 0)
                        {

                            return _listApiIncomingConnectionObject[clientIp].ListeApiClientObject.Count(x => x.Value.ClientConnectionStatus);

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
        /// <returns></returns>
        public long GetAllTotalActiveConnection()
        {
            long totalActiveConnection = 0;

            if (_listApiIncomingConnectionObject.Count > 0)
            {
                List<string> listClientIpKeys = new List<string>(_listApiIncomingConnectionObject.Keys);

                foreach (var clientIpKey in listClientIpKeys)
                {
                    try
                    {
                        if (_listApiIncomingConnectionObject[clientIpKey].ListeApiClientObject.Count > 0)
                        {
                            totalActiveConnection += _listApiIncomingConnectionObject[clientIpKey].ListeApiClientObject.Count(x => x.Value.ClientConnectionStatus);
                        }
                    }
                    catch
                    {
                        // Ignored.
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
        /// <param name="clientIp"></param>
        private long CleanUpInactiveConnectionFromClientIpTarget(string clientIp)
        {
            long totalClosed = 0;

            if (_listApiIncomingConnectionObject.Count > 0)
            {
                if (_listApiIncomingConnectionObject.ContainsKey(clientIp))
                {
                    if (_listApiIncomingConnectionObject[clientIp].ListeApiClientObject.Count > 0)
                    {
                        _listApiIncomingConnectionObject[clientIp].OnCleanUp = true;
                        try
                        {

                            foreach (var key in _listApiIncomingConnectionObject[clientIp].ListeApiClientObject.Keys.ToArray())
                            {
                                bool remove = false;
                                if (_listApiIncomingConnectionObject[clientIp].ListeApiClientObject[key] == null)
                                {
                                    remove = true;
                                }
                                else
                                {
                                    if (!_listApiIncomingConnectionObject[clientIp].ListeApiClientObject[key].ClientConnectionStatus)
                                    {
                                        remove = true;
                                    }

                                    if (_listApiIncomingConnectionObject[clientIp].ListeApiClientObject[key].CancellationTokenApiClient.IsCancellationRequested)
                                    {
                                        remove = true;
                                    }
                                }

                                if (remove)
                                {
                                    if(_listApiIncomingConnectionObject[clientIp].ListeApiClientObject.TryRemove(key, out _))
                                    {
                                        totalClosed++;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // ignored.
                        }
                        _listApiIncomingConnectionObject[clientIp].OnCleanUp = false;
                    }
                }
            }

            return totalClosed;
        }

        /// <summary>
        /// Clean up all incoming closed connection.
        /// </summary>
        /// <returns></returns>
        public long CleanUpAllIncomingClosedConnection(out int totalIp)
        {
            long totalConnectionClosed = 0;
            totalIp = 0;
            if (_listApiIncomingConnectionObject.Count > 0)
            {

                List<string> peerIncomingConnectionKeyList = new List<string>(_listApiIncomingConnectionObject.Keys);

                foreach (var peerIncomingConnectionKey in peerIncomingConnectionKeyList)
                {
                    if (_listApiIncomingConnectionObject[peerIncomingConnectionKey] != null)
                    {
                        if (_listApiIncomingConnectionObject[peerIncomingConnectionKey].ListeApiClientObject.Count > 0)
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
        public long CleanUpAllIncomingConnection()
        {
            long totalConnectionClosed = 0;
            if (_listApiIncomingConnectionObject.Count > 0)
            {

                List<string> peerIncomingConnectionKeyList = new List<string>( _listApiIncomingConnectionObject.Keys);

                foreach (var peerIncomingConnectionKey in peerIncomingConnectionKeyList)
                {
                    if (_listApiIncomingConnectionObject[peerIncomingConnectionKey] != null)
                    {
                        if (_listApiIncomingConnectionObject[peerIncomingConnectionKey].ListeApiClientObject.Count > 0)
                        {
                            _listApiIncomingConnectionObject[peerIncomingConnectionKey].OnCleanUp = true;

                            foreach (var key in _listApiIncomingConnectionObject[peerIncomingConnectionKey].ListeApiClientObject.Keys.ToArray())
                            {
                                try
                                {

                                    if (_listApiIncomingConnectionObject[peerIncomingConnectionKey].ListeApiClientObject[key].ClientConnectionStatus)
                                    {
                                        totalConnectionClosed++;
                                    }
                                    _listApiIncomingConnectionObject[peerIncomingConnectionKey].ListeApiClientObject[key].CloseApiClientConnection();
                                }
                                catch
                                {
                                    // Ignored.
                                }
                            }


                            _listApiIncomingConnectionObject[peerIncomingConnectionKey].OnCleanUp = false;

                        }
                    }
                }

                // Clean up.
                peerIncomingConnectionKeyList.Clear();
                _listApiIncomingConnectionObject.Clear();

            }
            return totalConnectionClosed;
        }

        #endregion

    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SeguraChain_Lib.Blockchain.Block.Enum;
using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using SeguraChain_Lib.Blockchain.Database;
using SeguraChain_Lib.Blockchain.MemPool.Database;
using SeguraChain_Lib.Blockchain.Mining.Enum;
using SeguraChain_Lib.Blockchain.Mining.Function;
using SeguraChain_Lib.Blockchain.Setting;
using SeguraChain_Lib.Blockchain.Stats.Function;
using SeguraChain_Lib.Blockchain.Transaction.Enum;
using SeguraChain_Lib.Blockchain.Transaction.Object;
using SeguraChain_Lib.Blockchain.Transaction.Utility;
using SeguraChain_Lib.Instance.Node.Network.Database;
using SeguraChain_Lib.Instance.Node.Network.Enum.API.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Client.Enum;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Packet.SubPacket.Request;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Packet.SubPacket.Response;
using SeguraChain_Lib.Instance.Node.Network.Services.Firewall.Manager;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Broadcast;
using SeguraChain_Lib.Instance.Node.Setting.Object;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Instance.Node.Network.Services.API.Client
{
    public class ClassPeerApiClientObject : IDisposable
    {
        private readonly ClassPeerNetworkSettingObject _peerNetworkSettingObject;
        private readonly ClassPeerFirewallSettingObject _peerFirewallSettingObject;
        private readonly string _apiServerOpenNatIp;
        private TcpClient _clientTcpClient;
        private readonly string _clientIp;
        public CancellationTokenSource CancellationTokenApiClient;
        public bool ClientConnectionStatus;
        private long _clientConnectTimestamp;
        private bool _validPostRequest;


        #region Dispose functions

        private bool _disposed;

        ~ClassPeerApiClientObject()
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
            if (_disposed && !ClientConnectionStatus)
                return;

            if (disposing)
                ClientConnectionStatus = false;

            _disposed = true;
        }
        #endregion

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="clientTcpClient"></param>
        /// <param name="clientIp"></param>
        /// <param name="peerFirewallSettingObject"></param>
        /// <param name="apiServerOpenNatIp"></param>
        /// <param name="cancellationTokenApiClient"></param>
        /// <param name="peerNetworkSettingObject"></param>
        public ClassPeerApiClientObject(TcpClient clientTcpClient, string clientIp, ClassPeerNetworkSettingObject peerNetworkSettingObject, ClassPeerFirewallSettingObject peerFirewallSettingObject, string apiServerOpenNatIp, CancellationTokenSource cancellationTokenApiClient)
        {
            _apiServerOpenNatIp = apiServerOpenNatIp;
            _clientTcpClient = clientTcpClient;
            _peerNetworkSettingObject = peerNetworkSettingObject;
            _peerFirewallSettingObject = peerFirewallSettingObject;
            _clientIp = clientIp;
            CancellationTokenApiClient = cancellationTokenApiClient;
            ClientConnectionStatus = true;
            _clientConnectTimestamp = ClassUtility.GetCurrentTimestampInSecond();
        }

        #region Manage Client API connection

        /// <summary>
        /// Handle a new incoming connection from a client to the API.
        /// </summary>
        /// <returns></returns>
        public async Task HandleApiClientConnection()
        {

            await Task.Factory.StartNew(CheckApiClientConnection).ConfigureAwait(false);

            while (ClientConnectionStatus)
            {
                try
                {

                    CancellationTokenApiClient.Token.ThrowIfCancellationRequested();

                    using (NetworkStream networkStream = new NetworkStream(_clientTcpClient.Client))
                    {

                        if (networkStream.DataAvailable)
                        {
                            byte[] packetBuffer = new byte[BlockchainSetting.PeerMaxPacketBufferSize];


                            int packetLength = await networkStream.ReadAsync(packetBuffer, 0, packetBuffer.Length, CancellationTokenApiClient.Token);

                            if (packetLength > 0)
                            {

                                string packetReceived = packetBuffer.GetStringFromByteArrayAscii();

                                if (!packetReceived.IsNullOrEmpty(out _))
                                {
                                    #region Take in count the common POST HTTP request syntax of data.

                                    // The method to handle HTTP POST request is very not great, but it's work. Like that we don't have to program a webserver and handle only this kind of request.
                                    if (!packetReceived.Contains(ClassPeerApiEnumHttpPostRequestSyntax.HttpPostRequestType))
                                    {

                                        if (_validPostRequest)
                                        {
                                            if (packetReceived.Contains(ClassPeerApiEnumHttpPostRequestSyntax.PostDataPosition1))
                                            {
                                                packetReceived = packetReceived.Split(new[] { ClassPeerApiEnumHttpPostRequestSyntax.PostDataPosition1 }, StringSplitOptions.None)[0];
                                            }

                                            if (!await HandleApiClientPostPacket(packetReceived))
                                            {
                                                if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                                                {
                                                    ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                                                }
                                            }
                                            ClientConnectionStatus = false;
                                            // Close the connection after to have receive the packet of the incoming connection, handle it and sent a response.
                                            break;
                                        }

                                    }
                                    else
                                    {

                                        if (packetReceived.Contains(ClassPeerApiEnumHttpPostRequestSyntax.PostDataPosition2))
                                        {
                                            _validPostRequest = true;

                                            int indexPacket = packetReceived.IndexOf(ClassPeerApiEnumHttpPostRequestSyntax.PostDataTargetIndexOf, 0, StringComparison.Ordinal);

                                            packetReceived = packetReceived.Substring(indexPacket);

                                            if (!await HandleApiClientPostPacket(packetReceived))
                                            {
                                                if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                                                {
                                                    ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                                                }
                                            }
                                            ClientConnectionStatus = false;
                                            // Close the connection after to have receive the packet of the incoming connection, handle it and sent a response.
                                            break;
                                        }
                                    }

                                    #endregion

                                    #region Take in count the common GET HTTP request.

                                    if (!_validPostRequest)
                                    {
                                        if (packetReceived.Contains("GET"))
                                        {

                                            packetReceived = packetReceived.GetStringBetweenTwoStrings("GET /", "HTTP");
                                            packetReceived = packetReceived.Replace("%7C", "|"); // Translate special character | 
                                            packetReceived = packetReceived.Replace(" ", ""); // Remove empty,space characters

                                            if (!await HandleApiClientGetPacket(packetReceived))
                                            {
                                                if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                                                {
                                                    ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                                                }
                                            }
                                        }
                                        ClientConnectionStatus = false;
                                        // Close the connection after to have receive the packet of the incoming connection, handle it and sent a response.
                                        break;
                                    }


                                    Debug.WriteLine("Invalid request" + packetReceived);

                                    #endregion
                                }

                                if (packetBuffer.Length > 0)
                                    Array.Clear(packetBuffer, 0, packetBuffer.Length);
                            }
                        }
                    }
                }
                catch
                {
                    ClientConnectionStatus = false;
                    break;
                }
            }

            CloseApiClientConnection();
        }

        /// <summary>
        /// Check the API Client Connection
        /// </summary>
        /// <returns></returns>
        private async Task CheckApiClientConnection()
        {
            while (ClientConnectionStatus)
            {
                try
                {
                    // Disconnected or the task has been stopped.
                    if (!ClientConnectionStatus)
                    {
                        break;
                    }

                    // If the API Firewall Link is enabled.
                    if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                    {
                        // Banned.
                        if (!ClassPeerFirewallManager.CheckClientIpStatus(_clientIp))
                        {
                            break;
                        }
                    }

                    // Timeout.
                    if (_clientConnectTimestamp + BlockchainSetting.PeerApiMaxConnectionDelay < ClassUtility.GetCurrentTimestampInSecond())
                    {
                        break;
                    }

                    if (!ClassUtility.TcpClientIsConnected(_clientTcpClient))
                    {
                        break;
                    }
                }
                catch
                {
                    break;
                }

                await Task.Delay(1000);
            }

            CloseApiClientConnection();
        }

        /// <summary>
        /// Close the API client connection.
        /// </summary>
        public void CloseApiClientConnection()
        {
            try
            {
                if (CancellationTokenApiClient != null)
                {
                    if (!CancellationTokenApiClient.IsCancellationRequested)
                    {
                        CancellationTokenApiClient.Cancel();
                        CancellationTokenApiClient.Dispose();
                    }
                }
            }
            catch
            {
                // Ignored.
            }


            ClientConnectionStatus = false;

            try
            {
                if (_clientTcpClient?.Client != null)
                {
                    if (_clientTcpClient.Client.Connected)
                    {
                        try
                        {
                            _clientTcpClient.Client.Shutdown(SocketShutdown.Both);
                        }
                        finally
                        {
                            if (_clientTcpClient?.Client != null)
                            {
                                if (_clientTcpClient.Client.Connected)
                                {
                                    _clientTcpClient.Client.Close();
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

        #region Manage Client API Packets

        /// <summary>
        /// Handle API POST Packet sent by a client.
        /// </summary>
        /// <param name="packetReceived"></param>
        private async Task<bool> HandleApiClientPostPacket(string packetReceived)
        {
            try
            {
                ClassPeerApiEnumTypeResponse typeResponse = ClassPeerApiEnumTypeResponse.OK;

                ClassApiPeerPacketObjectSend apiPeerPacketObjectSend = TryDeserializedPacketContent<ClassApiPeerPacketObjectSend>(packetReceived);

                if (apiPeerPacketObjectSend != null)
                {
                    if (!apiPeerPacketObjectSend.PacketContentObjectSerialized.IsNullOrEmpty(out _))
                    {

                        switch (apiPeerPacketObjectSend.PacketType)
                        {
                            case ClassPeerApiEnumPacketSend.ASK_BLOCK_METADATA_BY_ID:
                                {

                                    ClassApiPeerPacketAskBlockMetadataByBlockHeight apiPeerPacketAskBlockMetadataByBlockHeight = TryDeserializedPacketContent<ClassApiPeerPacketAskBlockMetadataByBlockHeight>(apiPeerPacketObjectSend.PacketContentObjectSerialized);

                                    if (apiPeerPacketAskBlockMetadataByBlockHeight != null)
                                    {
                                        if (ClassUtility.CheckPacketTimestamp(apiPeerPacketAskBlockMetadataByBlockHeight.PacketTimestamp, BlockchainSetting.PeerApiMaxPacketDelay, BlockchainSetting.PeerApiMaxEarlierPacketDelay))
                                        {
                                            if (ClassBlockchainStats.ContainsBlockHeight(apiPeerPacketAskBlockMetadataByBlockHeight.BlockHeight))
                                            {

                                                ClassBlockObject blockObject = await ClassBlockchainStats.GetBlockInformationData(apiPeerPacketAskBlockMetadataByBlockHeight.BlockHeight, CancellationTokenApiClient);

                                                if (!await SendApiResponse(BuildPacketResponse(new ClassApiPeerPacketSendBlockMetadataByBlockHeight()
                                                {
                                                    BlockDifficulty = blockObject.BlockDifficulty,
                                                    BlockHash = blockObject.BlockHash,
                                                    BlockHeight = blockObject.BlockHeight,
                                                    BlockTotalTransactions = blockObject.TotalTransaction,
                                                    BlockMiningPowShareUnlockObject = blockObject.BlockMiningPowShareUnlockObject,
                                                    BlockWalletAddressWinner = blockObject.BlockWalletAddressWinner,
                                                    TimestampCreate = blockObject.TimestampCreate,
                                                    TimestampFound = blockObject.TimestampFound,
                                                    BlockStatus = blockObject.BlockStatus,
                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()

                                                }, ClassPeerApiEnumPacketResponse.SEND_BLOCK_METADATA_BY_ID)))
                                                {
                                                    // Can't send packet.
                                                    return false;
                                                }
                                            }
                                            else
                                            {
                                                typeResponse = ClassPeerApiEnumTypeResponse.INVALID_BLOCK_ID;
                                            }
                                        }
                                        else // Invalid Packet Timestamp.
                                        {
                                            typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET_TIMESTAMP;
                                        }
                                    }
                                    else // Invalid Packet.
                                    {
                                        typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET;
                                    }

                                }
                                break;
                            case ClassPeerApiEnumPacketSend.ASK_BLOCK_TRANSACTION_BY_ID:
                                {
                                    ClassApiPeerPacketAskBlockTransactionById apiPeerPacketAskBlockTransactionById = TryDeserializedPacketContent<ClassApiPeerPacketAskBlockTransactionById>(apiPeerPacketObjectSend.PacketContentObjectSerialized);

                                    if (apiPeerPacketAskBlockTransactionById != null)
                                    {
                                        if (ClassUtility.CheckPacketTimestamp(apiPeerPacketAskBlockTransactionById.PacketTimestamp, BlockchainSetting.PeerApiMaxPacketDelay, BlockchainSetting.PeerApiMaxEarlierPacketDelay))
                                        {
                                            if (ClassBlockchainStats.ContainsBlockHeight(apiPeerPacketAskBlockTransactionById.BlockHeight))
                                            {
                                                int totalTransaction = await ClassBlockchainStats.GetBlockTransactionCount(apiPeerPacketAskBlockTransactionById.BlockHeight, CancellationTokenApiClient);


                                                if (apiPeerPacketAskBlockTransactionById.TransactionIndex < totalTransaction)
                                                {
                                                    var listTransaction = await ClassBlockchainStats.GetTransactionListFromBlockHeightTarget(apiPeerPacketAskBlockTransactionById.BlockHeight, false, CancellationTokenApiClient);

                                                    if (apiPeerPacketAskBlockTransactionById.TransactionIndex < listTransaction.Count)
                                                    {
                                                        var transactionObjectPair = listTransaction.ElementAt(apiPeerPacketAskBlockTransactionById.TransactionIndex);
                                                        if (transactionObjectPair.Value != null)
                                                        {
                                                            ClassBlockTransaction transactionObject = transactionObjectPair.Value;
                                                            if (!await SendApiResponse(BuildPacketResponse(new ClassApiPeerPacketSendBlockTransactionById()
                                                            {
                                                                BlockId = apiPeerPacketAskBlockTransactionById.BlockHeight,
                                                                BlockTransaction = transactionObject,
                                                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                            }, ClassPeerApiEnumPacketResponse.SEND_BLOCK_TRANSACTION_BY_ID)))
                                                            {
                                                                // Clean up.
                                                                listTransaction.Clear();
                                                                // Can't send packet.
                                                                return false;
                                                            }
                                                        }
                                                    }

                                                    // Clean up.
                                                    listTransaction.Clear();
                                                }
                                                else
                                                {
                                                    typeResponse = ClassPeerApiEnumTypeResponse.INVALID_BLOCK_TRANSACTION_ID;
                                                }
                                            }
                                            else // Invalid Packet Timestamp.
                                            {
                                                typeResponse = ClassPeerApiEnumTypeResponse.INVALID_BLOCK_ID;
                                            }
                                        }
                                        else // Invalid Packet Timestamp.
                                        {
                                            typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET_TIMESTAMP;
                                        }
                                    }
                                    else // Invalid Packet.
                                    {
                                        typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET;
                                    }
                                }
                                break;
                            case ClassPeerApiEnumPacketSend.ASK_WALLET_BALANCE_BY_ADDRESS:
                                {
                                    ClassApiPeerPacketAskWalletBalanceByAddress apiPeerPacketAskWalletBalance = TryDeserializedPacketContent<ClassApiPeerPacketAskWalletBalanceByAddress>(apiPeerPacketObjectSend.PacketContentObjectSerialized);

                                    if (apiPeerPacketAskWalletBalance != null)
                                    {
                                        if (ClassUtility.CheckPacketTimestamp(apiPeerPacketAskWalletBalance.PacketTimestamp, BlockchainSetting.PeerApiMaxPacketDelay, BlockchainSetting.PeerApiMaxEarlierPacketDelay))
                                        {
                                            if (ClassBase58.DecodeWithCheckSum(apiPeerPacketAskWalletBalance.WalletAddress, true) != null)
                                            {
                                                ClassApiPeerPacketSendWalletBalanceByAddress apiPeerPacketSendWalletBalance = new ClassApiPeerPacketSendWalletBalanceByAddress()
                                                {
                                                    WalletAddress = apiPeerPacketAskWalletBalance.WalletAddress,
                                                    WalletBalance = 0,
                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                };

                                                if (ClassBlockchainDatabase.BlockchainMemoryManagement.BlockchainWalletIndexMemoryCacheObject.ContainsKey(apiPeerPacketAskWalletBalance.WalletAddress, CancellationTokenApiClient, out _))
                                                {
                                                    var resultBalance = await ClassBlockchainStats.GetWalletBalanceFromTransactionAsync(apiPeerPacketAskWalletBalance.WalletAddress, ClassBlockchainStats.GetBlockchainNetworkStatsObject.LastBlockHeightTransactionConfirmationDone, true, false, false, true, CancellationTokenApiClient);
                                                    apiPeerPacketSendWalletBalance.WalletBalance = resultBalance.WalletBalance;
                                                }

                                                if (!await SendApiResponse(BuildPacketResponse(apiPeerPacketSendWalletBalance, ClassPeerApiEnumPacketResponse.SEND_WALLET_BALANCE_BY_ADDRESS)))
                                                {
                                                    // Can't send packet.
                                                    return false;
                                                }
                                            }
                                            else // Invalid Wallet Address Format.
                                            {
                                                typeResponse = ClassPeerApiEnumTypeResponse.INVALID_WALLET_ADDRESS;
                                            }
                                        }
                                        else // Invalid Packet Timestamp.
                                        {
                                            typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET_TIMESTAMP;
                                        }
                                    }
                                    else // Invalid Packet.
                                    {
                                        typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET;
                                    }
                                }
                                break;
                            case ClassPeerApiEnumPacketSend.ASK_WALLET_TRANSACTION_MEMPOOL_HASH_LIST:

                                break;
                            case ClassPeerApiEnumPacketSend.ASK_WALLET_TRANSACTION_HASH_LIST:
                                {
                                    ClassApiPeerPacketAskWalletTransactionHashList apiPeerPacketAskWalletTransactionHashList = TryDeserializedPacketContent<ClassApiPeerPacketAskWalletTransactionHashList>(apiPeerPacketObjectSend.PacketContentObjectSerialized);

                                    if (apiPeerPacketAskWalletTransactionHashList != null)
                                    {
                                        if (ClassUtility.CheckPacketTimestamp(apiPeerPacketAskWalletTransactionHashList.PacketTimestamp, BlockchainSetting.PeerApiMaxPacketDelay, BlockchainSetting.PeerApiMaxEarlierPacketDelay))
                                        {
                                            if (ClassBase58.DecodeWithCheckSum(apiPeerPacketAskWalletTransactionHashList.WalletAddress, true) != null)
                                            {
                                                ClassApiPeerPacketSendWalletTransactionHashList apiPeerPacketSendWalletTransactionHashList = new ClassApiPeerPacketSendWalletTransactionHashList()
                                                {
                                                    WalletAddress = apiPeerPacketAskWalletTransactionHashList.WalletAddress,
                                                    WalletTransactionHashList = new List<string>(),
                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                };

                                                if (ClassBlockchainDatabase.BlockchainMemoryManagement.BlockchainWalletIndexMemoryCacheObject.ContainsKey(apiPeerPacketAskWalletTransactionHashList.WalletAddress, CancellationTokenApiClient, out _))
                                                {
                                                    /*
                                                    long walletCountTransaction = await ClassBlockchainDatabase.BlockchainMemoryManagement.DictionaryWalletTransactionIndexObject[apiPeerPacketAskWalletTransactionHashList.WalletAddress].GetCountWalletTransaction(CancellationTokenApiClient);
                                                    if (walletCountTransaction > 0)
                                                    {
                                                        if (apiPeerPacketAskWalletTransactionHashList.EndRange != 0)
                                                        {
                                                            if (apiPeerPacketAskWalletTransactionHashList.EndRange + apiPeerPacketAskWalletTransactionHashList.StartRange < walletCountTransaction)
                                                            {
                                                                if (walletCountTransaction > apiPeerPacketAskWalletTransactionHashList.EndRange && walletCountTransaction > apiPeerPacketAskWalletTransactionHashList.StartRange)
                                                                {
                                                                    apiPeerPacketSendWalletTransactionHashList.WalletTransactionHashList = (await ClassBlockchainDatabase.BlockchainMemoryManagement.DictionaryWalletTransactionIndexObject[apiPeerPacketAskWalletTransactionHashList.WalletAddress].GetTransactionHashList(CancellationTokenApiClient)).GetList.GetRange(apiPeerPacketAskWalletTransactionHashList.StartRange, apiPeerPacketAskWalletTransactionHashList.EndRange);
                                                                }
                                                            }
                                                        }
                                                        // Send the whole list of transaction hash.
                                                        else
                                                        {
                                                            apiPeerPacketSendWalletTransactionHashList.WalletTransactionHashList = (await ClassBlockchainDatabase.BlockchainMemoryManagement.DictionaryWalletTransactionIndexObject[apiPeerPacketAskWalletTransactionHashList.WalletAddress].GetTransactionHashList(CancellationTokenApiClient)).GetList;
                                                        }
                                                    }*/
                                                }

                                                if (!await SendApiResponse(BuildPacketResponse(apiPeerPacketSendWalletTransactionHashList, ClassPeerApiEnumPacketResponse.SEND_WALLET_TRANSACTION_HASH_LIST)))
                                                {
                                                    // Can't send packet.
                                                    return false;
                                                }
                                            }
                                            else // Invalid Wallet Address Format.
                                            {
                                                typeResponse = ClassPeerApiEnumTypeResponse.INVALID_WALLET_ADDRESS;
                                            }
                                        }
                                        else // Invalid Packet Timestamp.
                                        {
                                            typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET_TIMESTAMP;
                                        }
                                    }
                                    else // Invalid Packet.
                                    {
                                        typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET;
                                    }
                                }
                                break;
                            case ClassPeerApiEnumPacketSend.ASK_WALLET_TRANSACTION_BY_HASH:
                                {
                                    ClassApiPeerPacketAskWalletTransactionByHash apiPeerPacketAskWalletTransactionByHash = TryDeserializedPacketContent<ClassApiPeerPacketAskWalletTransactionByHash>(apiPeerPacketObjectSend.PacketContentObjectSerialized);

                                    if (apiPeerPacketAskWalletTransactionByHash != null)
                                    {
                                        if (ClassUtility.CheckPacketTimestamp(apiPeerPacketAskWalletTransactionByHash.PacketTimestamp, BlockchainSetting.PeerApiMaxPacketDelay, BlockchainSetting.PeerApiMaxEarlierPacketDelay))
                                        {
                                            if (ClassBase58.DecodeWithCheckSum(apiPeerPacketAskWalletTransactionByHash.WalletAddress, true) != null)
                                            {
                                                if (ClassBlockchainDatabase.BlockchainMemoryManagement.BlockchainWalletIndexMemoryCacheObject.ContainsKey(apiPeerPacketAskWalletTransactionByHash.WalletAddress, CancellationTokenApiClient, out _))
                                                {
                                                    string transactionHash = apiPeerPacketAskWalletTransactionByHash.TransactionHash;
                                                    long blockHeight = ClassTransactionUtility.GetBlockHeightFromTransactionHash(transactionHash);

                                                    if (ClassBlockchainStats.ContainsBlockHeight(blockHeight))
                                                    {
                                                        if (await ClassBlockchainDatabase.BlockchainMemoryManagement.CheckIfTransactionHashAlreadyExist(transactionHash, blockHeight, CancellationTokenApiClient))
                                                        {
                                                            ClassBlockTransaction blockTransaction = await ClassBlockchainDatabase.BlockchainMemoryManagement.GetBlockTransactionFromSpecificTransactionHashAndHeight(transactionHash, blockHeight, true, true, CancellationTokenApiClient);

                                                            if (blockTransaction != null)
                                                            {
                                                                if (!await SendApiResponse(BuildPacketResponse(new ClassApiPeerPacketSendWalletTransactionByHash()
                                                                {
                                                                    WalletAddress = apiPeerPacketAskWalletTransactionByHash.WalletAddress,
                                                                    BlockId = blockHeight,
                                                                    TransactionHash = blockTransaction.TransactionObject.TransactionHash,
                                                                    BlockTransaction = blockTransaction,
                                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                                }, ClassPeerApiEnumPacketResponse.SEND_WALLET_TRANSACTION_BY_HASH)))
                                                                {
                                                                    // Can't send packet.
                                                                    return false;
                                                                }
                                                            }
                                                            else // Invalid Transaction Hash.
                                                            {
                                                                typeResponse = ClassPeerApiEnumTypeResponse.INVALID_WALLET_TRANSACTION_HASH;
                                                            }
                                                        }
                                                        else // Invalid Transaction Hash.
                                                        {
                                                            typeResponse = ClassPeerApiEnumTypeResponse.INVALID_WALLET_TRANSACTION_HASH;
                                                        }
                                                    }
                                                    else // Invalid Block ID.
                                                    {
                                                        typeResponse = ClassPeerApiEnumTypeResponse.INVALID_BLOCK_ID;
                                                    }
                                                }
                                                else // Wallet Address not registered.
                                                {
                                                    typeResponse = ClassPeerApiEnumTypeResponse.WALLET_ADDRESS_NOT_REGISTERED;
                                                }
                                            }
                                            else // Invalid Wallet Address Format.
                                            {
                                                typeResponse = ClassPeerApiEnumTypeResponse.INVALID_WALLET_ADDRESS;
                                            }
                                        }
                                        else // Invalid Packet Timestamp.
                                        {
                                            typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET_TIMESTAMP;
                                        }
                                    }
                                    else // Invalid Packet.
                                    {
                                        typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET;
                                    }
                                }
                                break;
                            case ClassPeerApiEnumPacketSend.PUSH_WALLET_TRANSACTION:
                                {
                                    ClassApiPeerPacketPushWalletTransaction apiPeerPacketPushWalletTransaction = TryDeserializedPacketContent<ClassApiPeerPacketPushWalletTransaction>(apiPeerPacketObjectSend.PacketContentObjectSerialized);

                                    if (apiPeerPacketPushWalletTransaction != null)
                                    {
                                        if (ClassUtility.CheckPacketTimestamp(apiPeerPacketPushWalletTransaction.PacketTimestamp, BlockchainSetting.PeerApiMaxPacketDelay, BlockchainSetting.PeerApiMaxEarlierPacketDelay))
                                        {
                                            if (apiPeerPacketPushWalletTransaction.TransactionObject != null)
                                            {
                                                if (await ClassBlockchainStats.CheckTransaction(apiPeerPacketPushWalletTransaction.TransactionObject, null, false, null, CancellationTokenApiClient, true) == ClassTransactionEnumStatus.VALID_TRANSACTION)
                                                {

                                                    var transactionStatus = await ClassPeerNetworkBroadcastFunction.AskMemPoolTxVoteToPeerListsAsync(_peerNetworkSettingObject.ListenIp, _apiServerOpenNatIp, _clientIp, new List<ClassTransactionObject>() { apiPeerPacketPushWalletTransaction.TransactionObject }, _peerNetworkSettingObject, _peerFirewallSettingObject, CancellationTokenApiClient, true);

                                                    bool isValid = false;

                                                    if (transactionStatus.ContainsKey(apiPeerPacketPushWalletTransaction.TransactionObject.TransactionHash))
                                                        if (transactionStatus[apiPeerPacketPushWalletTransaction.TransactionObject.TransactionHash] == ClassTransactionEnumStatus.VALID_TRANSACTION) 
                                                            isValid = true;
                                                    

                                                    if (isValid)
                                                    {

                                                        ClassBlockTransactionInsertEnumStatus blockTransactionInsertStatus = ClassBlockTransactionInsertEnumStatus.BLOCK_TRANSACTION_INVALID;

                                                        if (!await ClassMemPoolDatabase.CheckTxHashExist(apiPeerPacketPushWalletTransaction.TransactionObject.TransactionHash, CancellationTokenApiClient))
                                                        {
                                                             blockTransactionInsertStatus = await ClassBlockchainDatabase.InsertTransactionToMemPool(apiPeerPacketPushWalletTransaction.TransactionObject, true, false, true, CancellationTokenApiClient);
                                                        }
                                                        else
                                                        {
                                                            blockTransactionInsertStatus = ClassBlockTransactionInsertEnumStatus.BLOCK_TRANSACTION_HASH_ALREADY_EXIST;
                                                        }

                                                        if (blockTransactionInsertStatus == ClassBlockTransactionInsertEnumStatus.BLOCK_TRANSACTION_INSERTED)
                                                        {
                                                            if (!await SendApiResponse(BuildPacketResponse(new ClassApiPeerPacketSendPushWalletTransactionResponse()
                                                            {
                                                                BlockId = apiPeerPacketPushWalletTransaction.TransactionObject.BlockHeightTransaction,
                                                                TransactionHash = apiPeerPacketPushWalletTransaction.TransactionObject.TransactionHash,
                                                                PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                            }, ClassPeerApiEnumPacketResponse.SEND_REPLY_WALLET_TRANSACTION_PUSHED)))
                                                            {
                                                                // Can't send packet.
                                                                return false;
                                                            }
                                                        }
                                                        else
                                                        {
                                                            switch (blockTransactionInsertStatus)
                                                            {
                                                                case ClassBlockTransactionInsertEnumStatus.BLOCK_HEIGHT_NOT_EXIST:
                                                                    typeResponse = ClassPeerApiEnumTypeResponse.INVALID_BLOCK_ID;
                                                                    break;
                                                                case ClassBlockTransactionInsertEnumStatus.BLOCK_TRANSACTION_HASH_ALREADY_EXIST:
                                                                    typeResponse = ClassPeerApiEnumTypeResponse.INVALID_WALLET_TRANSACTION_HASH;
                                                                    break;
                                                                case ClassBlockTransactionInsertEnumStatus.MAX_BLOCK_TRANSACTION_PER_BLOCK_HEIGHT_TARGET_REACH:
                                                                    typeResponse = ClassPeerApiEnumTypeResponse.MAX_BLOCK_TRANSACTION_REACH;
                                                                    break;
                                                            }
                                                        }

                                                    }
                                                    else
                                                    {
                                                        typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PUSH_TRANSACTION;
                                                    }
                                                }
                                                else
                                                {
                                                    typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PUSH_TRANSACTION;
                                                }
                                            }
                                            else // Invalid transaction object.
                                            {
                                                typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PUSH_TRANSACTION;
                                            }
                                        }
                                        else // Invalid Packet Timestamp.
                                        {
                                            typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET_TIMESTAMP;
                                        }
                                    }
                                    else // Invalid Packet.
                                    {
                                        typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET;
                                    }
                                }
                                break;
                            case ClassPeerApiEnumPacketSend.PUSH_MINING_SHARE:
                                {
                                    ClassApiPeerPacketSendMiningShare apiPeerPacketSendMiningShare = TryDeserializedPacketContent<ClassApiPeerPacketSendMiningShare>(apiPeerPacketObjectSend.PacketContentObjectSerialized);

                                    if (apiPeerPacketSendMiningShare != null)
                                    {
                                        if (ClassUtility.CheckPacketTimestamp(apiPeerPacketSendMiningShare.PacketTimestamp, BlockchainSetting.PeerApiMaxPacketDelay, BlockchainSetting.PeerApiMaxEarlierPacketDelay))
                                        {
                                            long lastBlockHeight = ClassBlockchainStats.GetLastBlockHeight();

                                            if (lastBlockHeight > BlockchainSetting.GenesisBlockHeight)
                                            {
                                                long previousBlockHeight = lastBlockHeight - 1;

                                                ClassBlockObject previousBlockObjectInformations = await ClassBlockchainDatabase.BlockchainMemoryManagement.GetBlockInformationDataStrategy(previousBlockHeight, CancellationTokenApiClient);

                                                if (ClassBlockchainDatabase.BlockchainMemoryManagement[lastBlockHeight, CancellationTokenApiClient].BlockStatus == ClassBlockEnumStatus.LOCKED)
                                                {
                                                    ClassMiningPoWaCEnumStatus miningPowShareStatus = ClassMiningPoWaCUtility.CheckPoWaCShare(BlockchainSetting.CurrentMiningPoWaCSettingObject(lastBlockHeight), apiPeerPacketSendMiningShare.MiningPowShareObject, ClassBlockchainDatabase.BlockchainMemoryManagement[lastBlockHeight, CancellationTokenApiClient].BlockHeight, ClassBlockchainDatabase.BlockchainMemoryManagement[lastBlockHeight, CancellationTokenApiClient].BlockHash, ClassBlockchainDatabase.BlockchainMemoryManagement[lastBlockHeight, CancellationTokenApiClient].BlockDifficulty, previousBlockObjectInformations.TotalTransaction, previousBlockObjectInformations.BlockFinalHashTransaction, out _, out _);

                                                    switch (miningPowShareStatus)
                                                    {
                                                        // Unlock the current block if everything is okay with other peers.
                                                        case ClassMiningPoWaCEnumStatus.VALID_UNLOCK_BLOCK_SHARE:
                                                            ClassBlockEnumMiningShareVoteStatus miningShareVoteStatus = await ClassBlockchainDatabase.UnlockCurrentBlockAsync(lastBlockHeight, apiPeerPacketSendMiningShare.MiningPowShareObject, true, _peerNetworkSettingObject.ListenIp, _apiServerOpenNatIp, false, false, _peerNetworkSettingObject, _peerFirewallSettingObject, CancellationTokenApiClient);

                                                            switch (miningShareVoteStatus)
                                                            {
                                                                case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ACCEPTED:
                                                                    miningPowShareStatus = ClassMiningPoWaCEnumStatus.VALID_UNLOCK_BLOCK_SHARE;
                                                                    await Task.Factory.StartNew(() => ClassPeerNetworkBroadcastFunction.BroadcastMiningShareAsync(_peerNetworkSettingObject.ListenIp, _apiServerOpenNatIp, _clientIp, apiPeerPacketSendMiningShare.MiningPowShareObject, _peerNetworkSettingObject, _peerFirewallSettingObject)).ConfigureAwait(false);
                                                                    break;
                                                                case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_ALREADY_FOUND:
                                                                    // That's can happen sometimes when the broadcast of the share to other nodes is very fast and return back the data of the block unlocked to the synced data before to retrieve back every votes done.
                                                                    if (ClassMiningPoWaCUtility.ComparePoWaCShare(ClassBlockchainDatabase.BlockchainMemoryManagement[lastBlockHeight, CancellationTokenApiClient].BlockMiningPowShareUnlockObject, apiPeerPacketSendMiningShare.MiningPowShareObject))
                                                                    {
                                                                        miningPowShareStatus = ClassMiningPoWaCEnumStatus.VALID_UNLOCK_BLOCK_SHARE;
                                                                    }
                                                                    else
                                                                    {
                                                                        miningPowShareStatus = ClassMiningPoWaCEnumStatus.BLOCK_ALREADY_FOUND;
                                                                    }
                                                                    break;
                                                                case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_NOCONSENSUS:
                                                                    // That's can happen sometimes when the broadcast of the share to other nodes is very fast and return back the data of the block unlocked to the synced data before to retrieve back every votes done.
                                                                    if (ClassMiningPoWaCUtility.ComparePoWaCShare(ClassBlockchainDatabase.BlockchainMemoryManagement[lastBlockHeight, CancellationTokenApiClient].BlockMiningPowShareUnlockObject, apiPeerPacketSendMiningShare.MiningPowShareObject))
                                                                    {
                                                                        miningPowShareStatus = ClassMiningPoWaCEnumStatus.VALID_UNLOCK_BLOCK_SHARE;
                                                                    }
                                                                    else
                                                                    {
                                                                        miningPowShareStatus = ClassMiningPoWaCEnumStatus.BLOCK_ALREADY_FOUND;
                                                                    }
                                                                    break;
                                                                case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_INVALID_TIMESTAMP:
                                                                case ClassBlockEnumMiningShareVoteStatus.MINING_SHARE_VOTE_REFUSED:
                                                                    miningPowShareStatus = ClassMiningPoWaCEnumStatus.INVALID_SHARE_DATA;
                                                                    break;
                                                            }

                                                            break;
                                                        case ClassMiningPoWaCEnumStatus.VALID_SHARE:
                                                            miningPowShareStatus = ClassMiningPoWaCEnumStatus.VALID_SHARE;
                                                            break;
                                                        default:
                                                            typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PUSH_MINING_SHARE;
                                                            break;
                                                    }

                                                    if (!await SendApiResponse(BuildPacketResponse(new ClassApiPeerPacketSendMiningShareResponse()
                                                    {
                                                        MiningPoWShareStatus = miningPowShareStatus,
                                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                    }, ClassPeerApiEnumPacketResponse.SEND_MINING_SHARE_RESPONSE)))
                                                    {
                                                        // Can't send packet.
                                                        return false;
                                                    }
                                                }
                                                else
                                                {
                                                    if (!await SendApiResponse(BuildPacketResponse(new ClassApiPeerPacketSendMiningShareResponse()
                                                    {
                                                        MiningPoWShareStatus = ClassMiningPoWaCEnumStatus.BLOCK_ALREADY_FOUND,
                                                        PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                    }, ClassPeerApiEnumPacketResponse.SEND_MINING_SHARE_RESPONSE)))
                                                    {
                                                        // Can't send packet.
                                                        return false;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                if (!await SendApiResponse(BuildPacketResponse(new ClassApiPeerPacketSendMiningShareResponse()
                                                {
                                                    MiningPoWShareStatus = ClassMiningPoWaCEnumStatus.INVALID_BLOCK_HEIGHT,
                                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                                }, ClassPeerApiEnumPacketResponse.SEND_MINING_SHARE_RESPONSE)))
                                                {
                                                    // Can't send packet.
                                                    return false;
                                                }
                                            }
                                        }
                                    }
                                }
                                break;
                            // Invalid Packet
                            default:
                                {
                                    ClassLog.WriteLine("Unknown packet type received: " + apiPeerPacketObjectSend.PacketType, ClassEnumLogLevelType.LOG_LEVEL_API_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                                    typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET;
                                }
                                break;
                        }
                    }
                }
                else // Invalid Packet.
                {
                    typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET;
                }

                switch (typeResponse)
                {
                    // Invalid Packet.
                    case ClassPeerApiEnumTypeResponse.INVALID_PACKET:
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_PACKET)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        return false;
                    // Invalid Packet Timestamp.
                    case ClassPeerApiEnumTypeResponse.INVALID_PACKET_TIMESTAMP:

                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_PACKET_TIMESTAMP)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        return false;
                    case ClassPeerApiEnumTypeResponse.INVALID_BLOCK_ID:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_BLOCK_ID)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.INVALID_BLOCK_TRANSACTION_ID:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_BLOCK_TRANSACTION_ID)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.INVALID_WALLET_ADDRESS:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_WALLET_ADDRESS)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.INVALID_WALLET_TRANSACTION_HASH:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_WALLET_TRANSACTION_HASH)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.WALLET_ADDRESS_NOT_REGISTERED:
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.WALLET_ADDRESS_NOT_REGISTERED)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.INVALID_PUSH_TRANSACTION:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_PUSH_TRANSACTION)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.MAX_BLOCK_TRANSACTION_REACH:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.MAX_BLOCK_TRANSACTION_REACH)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.INVALID_PUSH_MINING_SHARE:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        break;

                }
            }
            catch (Exception error)
            {
                ClassLog.WriteLine("Error to handle API Packet received. Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_API_SERVER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MEDIUM_PRIORITY);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Handle API GET Packet sent by a client.
        /// </summary>
        /// <param name="packetReceived"></param>
        private async Task<bool> HandleApiClientGetPacket(string packetReceived)
        {
            try
            {
                ClassPeerApiEnumTypeResponse typeResponse = ClassPeerApiEnumTypeResponse.OK;

                switch (packetReceived)
                {
                    case ClassPeerApiEnumGetRequest.GetBlockTemplate:
                        {
                            long currentBlockHeight = ClassBlockchainStats.GetLastBlockHeight();
                            if (currentBlockHeight > 0)
                            {
                                if (!await SendApiResponse(BuildPacketResponse(new ClassApiPeerPacketSendNetworkStats()
                                {
                                    CurrentBlockHeight = currentBlockHeight,
                                    CurrentBlockDifficulty = ClassBlockchainDatabase.BlockchainMemoryManagement[currentBlockHeight, CancellationTokenApiClient].BlockDifficulty,
                                    CurrentBlockHash = ClassBlockchainDatabase.BlockchainMemoryManagement[currentBlockHeight, CancellationTokenApiClient].BlockHash,
                                    LastTimestampBlockFound = ClassBlockchainDatabase.BlockchainMemoryManagement[currentBlockHeight, CancellationTokenApiClient].TimestampCreate,
                                    CurrentMiningPoWaCSetting = BlockchainSetting.CurrentMiningPoWaCSettingObject(currentBlockHeight),
                                    PacketTimestamp = ClassUtility.GetCurrentTimestampInSecond()
                                }, ClassPeerApiEnumPacketResponse.SEND_NETWORK_STATS)))
                                {
                                    // Can't send packet.
                                    return false;
                                }
                            }
                            else
                            {
                                typeResponse = ClassPeerApiEnumTypeResponse.OK;
                            }
                        }
                        break;
                    default:
                        typeResponse = ClassPeerApiEnumTypeResponse.INVALID_PACKET;
                        break;
                }

                switch (typeResponse)
                {
                    // Invalid Packet.
                    case ClassPeerApiEnumTypeResponse.INVALID_PACKET:
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_PACKET)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        return false;
                    // Invalid Packet Timestamp.
                    case ClassPeerApiEnumTypeResponse.INVALID_PACKET_TIMESTAMP:

                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_PACKET_TIMESTAMP)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        return false;
                    case ClassPeerApiEnumTypeResponse.INVALID_BLOCK_ID:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_BLOCK_ID)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.INVALID_BLOCK_TRANSACTION_ID:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_BLOCK_TRANSACTION_ID)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.INVALID_WALLET_ADDRESS:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_WALLET_ADDRESS)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.INVALID_WALLET_TRANSACTION_HASH:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_WALLET_TRANSACTION_HASH)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.WALLET_ADDRESS_NOT_REGISTERED:
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.WALLET_ADDRESS_NOT_REGISTERED)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.INVALID_PUSH_TRANSACTION:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.INVALID_PUSH_TRANSACTION)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.MAX_BLOCK_TRANSACTION_REACH:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        if (await SendApiResponse(BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse.MAX_BLOCK_TRANSACTION_REACH)))
                        {
                            // Can't send packet.
                            return false;
                        }
                        break;
                    case ClassPeerApiEnumTypeResponse.INVALID_PUSH_MINING_SHARE:
                        if (_peerFirewallSettingObject.PeerEnableFirewallLink)
                        {
                            ClassPeerFirewallManager.InsertInvalidPacket(_clientIp);
                        }
                        break;

                }
            }
            catch
            {

            }

            return false;
        }

        /// <summary>
        /// Send an API Response to the client.
        /// </summary>
        /// <param name="packetToSend"></param>
        /// <returns></returns>
        private async Task<bool> SendApiResponse(string packetToSend)
        {
            try
            {
                using (NetworkStream networkStream = new NetworkStream(_clientTcpClient.Client))
                {

                    StringBuilder builder = new StringBuilder();

                    builder.AppendLine(@"HTTP/1.1 200 OK");
                    builder.AppendLine(@"Content-Type: text/plain");
                    builder.AppendLine(@"Content-Length: " + packetToSend.Length);
                    builder.AppendLine(@"Access-Control-Allow-Origin: *");
                    builder.AppendLine(@"");
                    builder.AppendLine(@"" + packetToSend);


                    byte[] dataToSent = ClassUtility.GetByteArrayFromStringAscii(builder.ToString());

                    // Clean up.
                    builder.Clear();


                    await networkStream.WriteAsync(dataToSent, 0, dataToSent.Length);
                    await networkStream.FlushAsync();

                    // Clean up.
                    Array.Clear(dataToSent, 0, dataToSent.Length);
                }
            }
            catch
            {
                return false;
            }
            return true;
        }

        #endregion

        #region Other functions used for packets received to handle.

        /// <summary>
        /// Try to deserialize the packet content object received.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="packetContentObject"></param>
        /// <returns></returns>
        private T TryDeserializedPacketContent<T>(string packetContentObject)
        {
            if (ClassUtility.TryDeserialize(packetContentObject, out T apiPeerPacketDeserialized))
            {
                if (apiPeerPacketDeserialized != null)
                {
                    return apiPeerPacketDeserialized;
                }
            }
            return default;
        }

        /// <summary>
        /// Serialise packet response to send.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="packetResponse"></param>
        /// <param name="packetResponseType"></param>
        /// <returns></returns>
        private string BuildPacketResponse<T>(T packetResponse, ClassPeerApiEnumPacketResponse packetResponseType)
        {
            return ClassUtility.SerializeData(new ClassApiPeerPacketObjetReceive()
            {
                PacketType = packetResponseType,
                PacketObjectSerialized = ClassUtility.SerializeData(packetResponse)
            });
        }

        /// <summary>
        /// Serialise packet response type to send.
        /// </summary>
        /// <param name="packetResponseType"></param>
        /// <returns></returns>
        private string BuildPacketResponseStatus(ClassPeerApiEnumPacketResponse packetResponseType)
        {
            return ClassUtility.SerializeData(new ClassApiPeerPacketObjetReceive()
            {
                PacketType = packetResponseType,
                PacketObjectSerialized = null
            });
        }

        #endregion
    }
}

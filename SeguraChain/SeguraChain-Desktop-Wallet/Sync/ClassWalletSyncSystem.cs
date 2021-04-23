using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SeguraChain_Desktop_Wallet.Common;
using SeguraChain_Desktop_Wallet.MainForm.Object;
using SeguraChain_Desktop_Wallet.Settings.Object;
using SeguraChain_Desktop_Wallet.Sync.Object;
using SeguraChain_Desktop_Wallet.Settings.Enum;
using SeguraChain_Desktop_Wallet.Wallet.Object;
using SeguraChain_Lib.Blockchain.Block.Enum;
using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using SeguraChain_Lib.Blockchain.Database;
using SeguraChain_Lib.Blockchain.MemPool.Database;
using SeguraChain_Lib.Blockchain.Setting;
using SeguraChain_Lib.Blockchain.Stats.Function;
using SeguraChain_Lib.Blockchain.Stats.Object;
using SeguraChain_Lib.Blockchain.Transaction.Enum;
using SeguraChain_Lib.Blockchain.Transaction.Object;
using SeguraChain_Lib.Blockchain.Transaction.Utility;
using SeguraChain_Lib.Blockchain.Wallet.Function;
using SeguraChain_Lib.Instance.Node;
using SeguraChain_Lib.Instance.Node.Network.Services.P2P.Broadcast;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Desktop_Wallet.Sync
{
    public class ClassWalletSyncSystem
    {
        /// <summary>
        /// Internal sync mode node instance.
        /// </summary>
        private ClassNodeInstance _nodeInstance;
        private CancellationTokenSource _cancellationSyncCache;

        #region Sync cache system.

        /// <summary>
        /// Store wallets sync caches.
        /// </summary>
        public ConcurrentDictionary<string, ClassSyncCacheObject> DatabaseSyncCache { get; private set; }


        /// <summary>
        /// Load the sync database cache.
        /// </summary>
        /// <param name="walletSettingObject"></param>
        /// <returns></returns>
        public bool LoadSyncDatabaseCache(ClassWalletSettingObject walletSettingObject)
        {
            bool result = true;
            DatabaseSyncCache = new ConcurrentDictionary<string, ClassSyncCacheObject>();
            _cancellationSyncCache = new CancellationTokenSource();

            try
            {
                if (!Directory.Exists(walletSettingObject.WalletSyncCacheDirectoryPath))
                {
                    Directory.CreateDirectory(walletSettingObject.WalletSyncCacheDirectoryPath);
                }

                if (!File.Exists(walletSettingObject.WalletSyncCacheFilePath))
                {
                    File.Create(walletSettingObject.WalletSyncCacheFilePath).Close();
                }
                else
                {
                    using (StreamReader reader = new StreamReader(walletSettingObject.WalletSyncCacheFilePath))
                    {
                        string line;

                        while ((line = reader.ReadLine()) != null)
                        {
                            if (ClassUtility.TryDeserialize(line, out ClassSyncCacheBlockTransactionObject blockTransactionSyncCacheObject))
                            {
                                if (blockTransactionSyncCacheObject != null)
                                {
                                    string walletAddress;

                                    if (blockTransactionSyncCacheObject.IsSender)
                                    {
                                        walletAddress = blockTransactionSyncCacheObject.BlockTransaction.TransactionObject.WalletAddressSender;
                                    }
                                    else
                                    {
                                        walletAddress = blockTransactionSyncCacheObject.BlockTransaction.TransactionObject.WalletAddressReceiver;
                                    }

                                    InsertOrUpdateBlockTransactionToSyncCache(walletAddress, blockTransactionSyncCacheObject.BlockTransaction, blockTransactionSyncCacheObject.IsMemPool, _cancellationSyncCache);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                result = false;
            }

            return result;
        }

        /// <summary>
        /// Save the sync database cache.
        /// </summary>
        /// <param name="walletSettingObject"></param>
        public void SaveSyncDatabaseCache(ClassWalletSettingObject walletSettingObject)
        {
            if (!Directory.Exists(walletSettingObject.WalletSyncCacheDirectoryPath))
            {
                Directory.CreateDirectory(walletSettingObject.WalletSyncCacheDirectoryPath);
            }

            if (!File.Exists(walletSettingObject.WalletSyncCacheFilePath))
            {
                File.Create(walletSettingObject.WalletSyncCacheFilePath).Close();
            }

            using (StreamWriter writer = new StreamWriter(walletSettingObject.WalletSyncCacheFilePath))
            {
                HashSet<string> listTransactionHashSaved = new HashSet<string>();

                CancellationTokenSource cancellation = new CancellationTokenSource();
                foreach (string walletAddress in DatabaseSyncCache.Keys)
                {
                    if (DatabaseSyncCache[walletAddress].CountBlockHeight > 0)
                    {
                        foreach (long blockHeight in DatabaseSyncCache[walletAddress].BlockHeightKeys(cancellation))
                        {
                            if (DatabaseSyncCache[walletAddress].CountBlockTransactionFromBlockHeight(blockHeight, cancellation) > 0)
                            {
                                foreach (var blockTransactionPair in DatabaseSyncCache[walletAddress].GetBlockTransactionFromBlockHeight(blockHeight, cancellation))
                                {
                                    if (!listTransactionHashSaved.Contains(blockTransactionPair.Key))
                                    {
                                        writer.WriteLine(JsonConvert.SerializeObject(blockTransactionPair.Value));
                                        listTransactionHashSaved.Add(blockTransactionPair.Key);
                                    }
                                }
                            }
                        }
                    }
                }

                //Clean up.
                listTransactionHashSaved.Clear();
                DatabaseSyncCache.Clear();
            }
        }

        /// <summary>
        /// Enable the task who update the sync cache.
        /// </summary>
        public void EnableTaskUpdateSyncCache()
        {
            if (_cancellationSyncCache.IsCancellationRequested)
            {
                _cancellationSyncCache = new CancellationTokenSource();
            }

            try
            {
                Task.Factory.StartNew(async () =>
                {

                    while (ClassDesktopWalletCommonData.DesktopWalletStarted)
                    {
                        if (DatabaseSyncCache.Count > 0)
                        {
                            
                            foreach (string walletAddress in DatabaseSyncCache.Keys.ToArray())
                            {
                                _cancellationSyncCache.Token.ThrowIfCancellationRequested();

                                bool exception = false;

                                foreach(long blockHeight in DatabaseSyncCache[walletAddress].BlockHeightKeys(_cancellationSyncCache))
                                {
                                    foreach(var transactionHash in DatabaseSyncCache[walletAddress].GetListBlockTransactionHashFromBlockHeight(blockHeight, _cancellationSyncCache))
                                    {
                                        try
                                        {
                                            var blockTransaction = await GetTransactionObjectFromSync(walletAddress, transactionHash, blockHeight, true, _cancellationSyncCache);

                                            if (blockTransaction != null)
                                            {
                                                if (blockTransaction.Item2 != null)
                                                {
                                                    DatabaseSyncCache[walletAddress].UpdateBlockTransaction(blockTransaction.Item2, blockTransaction.Item1);
                                                }
                                            }
                                        }
                                        catch (Exception error)
                                        {
#if DEBUG
                                            Debug.WriteLine("Error on updating sync cache of wallet address: " + walletAddress + " | Exception: " + error.Message);
#endif
                                            exception = true;
                                            break;
                                        }
                                    }

                                    if (exception) break;
                                    
                                }

                                string walletFilename = ClassDesktopWalletCommonData.WalletDatabase.GetWalletFileNameFromWalletAddress(walletAddress);

                                if (!walletFilename.IsNullOrEmpty())
                                {
                                    if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData.ContainsKey(walletFilename))
                                    {
                                        foreach(long blockHeight in ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFilename].WalletTransactionList.Keys.ToArray())
                                        {
                                            foreach(string transactionHash in ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFilename].WalletTransactionList[blockHeight].ToArray())
                                            {
                                                if (!DatabaseSyncCache[walletAddress].ContainsBlockTransactionFromTransactionHashAndBlockHeight(blockHeight, transactionHash, _cancellationSyncCache))
                                                {
                                                    var blockTransaction = await GetTransactionObjectFromSync(walletAddress, transactionHash, blockHeight, true, _cancellationSyncCache);

                                                    if (blockTransaction != null)
                                                    {
                                                        if (blockTransaction.Item2 != null)
                                                        {
                                                            DatabaseSyncCache[walletAddress].InsertBlockTransaction(new ClassSyncCacheBlockTransactionObject()
                                                            {
                                                                BlockTransaction = blockTransaction.Item2,
                                                                IsMemPool = blockTransaction.Item1
                                                            }, _cancellationSyncCache);
                                                        }
                                                    }
                                                }
                                            }
                                        }

                                        foreach (string transactionHash in ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFilename].WalletMemPoolTransactionList.ToArray())
                                        {
                                            long blockHeight = ClassTransactionUtility.GetBlockHeightFromTransactionHash(transactionHash);
                                            if (!DatabaseSyncCache[walletAddress].ContainsBlockTransactionFromTransactionHashAndBlockHeight(blockHeight, transactionHash, _cancellationSyncCache))
                                            {
                                                var blockTransaction = await GetTransactionObjectFromSync(walletAddress, transactionHash, blockHeight, true, _cancellationSyncCache);

                                                if (blockTransaction != null)
                                                {
                                                    if (blockTransaction.Item2 != null)
                                                    {
                                                        DatabaseSyncCache[walletAddress].InsertBlockTransaction(new ClassSyncCacheBlockTransactionObject()
                                                        {
                                                            BlockTransaction = blockTransaction.Item2,
                                                            IsMemPool = blockTransaction.Item1
                                                        }, _cancellationSyncCache);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }


                        }
                        await Task.Delay(ClassWalletDefaultSetting.DefaultWalletUpdateSyncCacheInterval);
                    }
                }, _cancellationSyncCache.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Ignored, catch the exception once the task is cancelled.
            }
        }

        /// <summary>
        /// Stop the task who update the sync cache.
        /// </summary>
        public void StopTaskUpdateSyncCache()
        {
            if (_cancellationSyncCache != null)
            {
                if (!_cancellationSyncCache.IsCancellationRequested)
                {
                    _cancellationSyncCache.Cancel();
                }
            }
        }

        /// <summary>
        /// Clean sync cache of a wallet address target.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public void CleanSyncCacheOfWalletAddressTarget(string walletAddress, CancellationTokenSource cancellation)
        {
            StopTaskUpdateSyncCache();

            if (DatabaseSyncCache.ContainsKey(walletAddress))
            {
                DatabaseSyncCache[walletAddress].Clear(cancellation);
                DatabaseSyncCache.TryRemove(walletAddress, out _);
            }
        }

        /// <summary>
        /// Insert or a update block transaction to the sync cache.
        /// </summary>
        /// <param name="blockTransaction"></param>
        /// <param name="isMemPool"></param>
        /// <param name="cancellation"></param>
        public void InsertOrUpdateBlockTransactionToSyncCache(string walletAddress, ClassBlockTransaction blockTransaction, bool isMemPool, CancellationTokenSource cancellation)
        {

            bool init = false;


            bool isSender = blockTransaction.TransactionObject.WalletAddressSender == walletAddress;

            #region Update sender cache.

            if (!DatabaseSyncCache.ContainsKey(walletAddress))
            {
                if (ClassWalletUtility.CheckWalletAddress(walletAddress))
                {
                    init = DatabaseSyncCache.TryAdd(walletAddress, new ClassSyncCacheObject());
                }
            }
            else
            {
                init = true;
            }

            if (init)
            {
                bool insertBlockHeight = true;

                if (!DatabaseSyncCache[walletAddress].ContainsBlockHeight(blockTransaction.TransactionObject.BlockHeightTransaction, cancellation))
                {
                    insertBlockHeight = DatabaseSyncCache[walletAddress].InsertBlockHeight(blockTransaction.TransactionObject.BlockHeightTransaction, cancellation);

                }

                while (!insertBlockHeight)
                {
                    cancellation?.Token.ThrowIfCancellationRequested();

                    if (!DatabaseSyncCache[walletAddress].ContainsBlockHeight(blockTransaction.TransactionObject.BlockHeightTransaction, cancellation))
                    {
                        insertBlockHeight = DatabaseSyncCache[walletAddress].InsertBlockHeight(blockTransaction.TransactionObject.BlockHeightTransaction, cancellation);
                    }
                    else
                    {
                        insertBlockHeight = true;
                    }
                }

                if (insertBlockHeight)
                {
                    if (!DatabaseSyncCache[walletAddress].ContainsBlockTransactionFromTransactionHashAndBlockHeight(blockTransaction.TransactionObject.BlockHeightTransaction, blockTransaction.TransactionObject.TransactionHash, cancellation))
                    {
                        DatabaseSyncCache[walletAddress].InsertBlockTransaction(new ClassSyncCacheBlockTransactionObject()
                        {
                            BlockTransaction = blockTransaction,
                            IsMemPool = isMemPool,
                            IsSender = isSender
                        }, cancellation);
                    }
                    else
                    {
                        DatabaseSyncCache[walletAddress].UpdateBlockTransaction(blockTransaction, isMemPool);
                    }
                }
            }

            #endregion

        }

        /// <summary>
        /// Get a block transaction from the sync cache.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="isMemPool"></param>
        /// <returns></returns>
        public ClassBlockTransaction GetBlockTransactionFromSyncCache(string walletAddress, string transactionHash, long blockHeight, CancellationTokenSource cancellation, out bool isMemPool)
        {
            ClassBlockTransaction blockTransaction = null;
            isMemPool = false;

            if (DatabaseSyncCache.Count > 0)
            {
                if (DatabaseSyncCache.ContainsKey(walletAddress))
                {
                    if (DatabaseSyncCache[walletAddress].ContainsBlockHeight(blockHeight, cancellation))
                    {
                        if (DatabaseSyncCache[walletAddress].ContainsBlockTransactionFromTransactionHashAndBlockHeight(blockHeight, transactionHash, cancellation))
                        {
                            var syncBlockTransactionCached = DatabaseSyncCache[walletAddress].GetSyncBlockTransactionCached(blockHeight, transactionHash);
                            isMemPool = syncBlockTransactionCached.IsMemPool;
                            blockTransaction = syncBlockTransactionCached.BlockTransaction;
                        }
                    }
                }
            }

            return blockTransaction;
        }

        #endregion

        #region Main sync functions.

        /// <summary>
        /// Initialize and start the sync system.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> StartSync()
        {
            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {

                        _nodeInstance = new ClassNodeInstance
                        {
                            PeerSettingObject = ClassDesktopWalletCommonData.WalletSettingObject.WalletInternalSyncNodeSetting
                        };

                        return _nodeInstance.NodeStart(true);
                    }
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        if (_nodeInstance != null)
                        {
                            await _nodeInstance.NodeStop(false, true);
                        }
                    }

                    break;


            }
            return false;
        }

        /// <summary>
        /// Close the sync system.
        /// </summary>
        public async Task CloseSync()
        {
            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        if (_nodeInstance != null)
                        {
                            await _nodeInstance.NodeStop(false, true);
                        }
                    }
                    break;
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {

                    }
                    break;
            }
        }

        /// <summary>
        /// Update the wallet sync of a wallet file target.
        /// </summary>
        /// <param name="walletFileName"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> UpdateWalletSync(string walletFileName, CancellationTokenSource cancellation)
        {
            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        return await UpdateWalletSyncFromInternalSyncModeAsync(walletFileName, cancellation);
                    }
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        // To do.
                    }
                    break;
            }
            return false;
        }

        /// <summary>
        /// Get the wallet balance from data synced.
        /// </summary>
        /// <param name="walletFileName"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public ClassWalletBalanceObject GetWalletBalanceFromSyncedData(string walletFileName, CancellationTokenSource cancellation)
        {
            BigInteger availableBalance = 0;
            BigInteger pendingBalance = 0;

            if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData.ContainsKey(walletFileName))
            {
                string walletAddress = ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletAddress;

                bool containAddress;
                if (!DatabaseSyncCache.ContainsKey(walletAddress))
                {
                    containAddress = DatabaseSyncCache.TryAdd(walletAddress, new ClassSyncCacheObject());
                }
                else
                {
                    containAddress = true;
                }

                if (containAddress)
                {
                    if (DatabaseSyncCache[walletAddress].CountBlockHeight > 0)
                    {
                        foreach (long blockHeight in DatabaseSyncCache[walletAddress].BlockHeightKeys(cancellation))
                        {
                            if (DatabaseSyncCache[walletAddress].CountBlockTransactionFromBlockHeight(blockHeight, cancellation) > 0)
                            {
                                foreach (var transaction in DatabaseSyncCache[walletAddress].GetBlockTransactionFromBlockHeight(blockHeight, cancellation))
                                {
                                    if (transaction.Value?.BlockTransaction != null)
                                    {
                                        var blockTransaction = transaction.Value.BlockTransaction;
                                        if (blockTransaction.TransactionStatus)
                                        {
                                            if (blockTransaction.TransactionObject.WalletAddressSender == walletAddress || blockTransaction.TransactionObject.WalletAddressReceiver == walletAddress)
                                            {
                                                long totalConfirmationToReach = blockTransaction.TransactionObject.BlockHeightTransactionConfirmationTarget - blockTransaction.TransactionObject.BlockHeightTransaction;
                                                // Confirmed.
                                                if (!transaction.Value.IsMemPool && blockTransaction.TransactionTotalConfirmation >= totalConfirmationToReach && blockTransaction.TransactionTotalConfirmation >= BlockchainSetting.TransactionMandatoryMinBlockTransactionConfirmations)
                                                {
                                                    if (blockTransaction.TransactionObject.WalletAddressSender == walletAddress)
                                                    {
                                                        availableBalance -= (blockTransaction.TransactionObject.Amount + blockTransaction.TransactionObject.Fee);
                                                    }
                                                    else if (blockTransaction.TransactionObject.WalletAddressReceiver == walletAddress)
                                                    {
                                                        availableBalance += blockTransaction.TransactionObject.Amount;
                                                    }
                                                }
                                                // Pending.
                                                else
                                                {
                                                    if (blockTransaction.TransactionObject.WalletAddressSender == walletAddress)
                                                    {
                                                        pendingBalance -= (blockTransaction.TransactionObject.Amount + blockTransaction.TransactionObject.Fee);
                                                    }
                                                    else if (blockTransaction.TransactionObject.WalletAddressReceiver == walletAddress)
                                                    {
                                                        pendingBalance += blockTransaction.TransactionObject.Amount;
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

            return new ClassWalletBalanceObject()
            {
                WalletAvailableBalance = ClassTransactionUtility.GetFormattedAmountFromBigInteger(availableBalance),
                WalletPendingBalance = ClassTransactionUtility.GetFormattedAmountFromBigInteger(pendingBalance),
                WalletTotalBalance = ClassTransactionUtility.GetFormattedAmountFromBigInteger(availableBalance + pendingBalance)
            };
        }

        /// <summary>
        /// Get the wallet balance from data synced.
        /// </summary>
        /// <param name="walletFileName"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public BigInteger GetWalletAvailableBalanceFromSyncedData(string walletFileName, CancellationTokenSource cancellation)
        {
            BigInteger availableBalance = 0;

            if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData.ContainsKey(walletFileName))
            {
                string walletAddress = ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletAddress;


                bool containAddress;
                if (!DatabaseSyncCache.ContainsKey(walletAddress))
                {
                    containAddress = DatabaseSyncCache.TryAdd(walletAddress, new ClassSyncCacheObject());
                }
                else
                {
                    containAddress = true;
                }

                if (containAddress)
                {
                    if (DatabaseSyncCache[walletAddress].CountBlockHeight > 0)
                    {
                        foreach (long blockHeight in DatabaseSyncCache[walletAddress].BlockHeightKeys(cancellation))
                        {
                            if (DatabaseSyncCache[walletAddress].CountBlockTransactionFromBlockHeight(blockHeight, cancellation) > 0)
                            {
                                foreach (var transaction in DatabaseSyncCache[walletAddress].GetBlockTransactionFromBlockHeight(blockHeight, cancellation))
                                {
                                    if (transaction.Value?.BlockTransaction != null)
                                    {
                                        var blockTransaction = transaction.Value.BlockTransaction;
                                        if (blockTransaction.TransactionStatus)
                                        {
                                            if (blockTransaction.TransactionObject.WalletAddressSender == walletAddress || blockTransaction.TransactionObject.WalletAddressReceiver == walletAddress)
                                            {
                                                long totalConfirmationToReach = blockTransaction.TransactionObject.BlockHeightTransactionConfirmationTarget - blockTransaction.TransactionObject.BlockHeightTransaction;
                                                // Confirmed.
                                                if (!transaction.Value.IsMemPool && blockTransaction.TransactionTotalConfirmation >= totalConfirmationToReach && blockTransaction.TransactionTotalConfirmation >= BlockchainSetting.TransactionMandatoryMinBlockTransactionConfirmations)
                                                {
                                                    if (blockTransaction.TransactionObject.WalletAddressReceiver == walletAddress)
                                                    {
                                                        availableBalance += (blockTransaction.TransactionObject.Amount);
                                                    }
                                                    if (blockTransaction.TransactionObject.WalletAddressSender == walletAddress)
                                                    {
                                                        availableBalance -= (blockTransaction.TransactionObject.Amount + blockTransaction.TransactionObject.Fee);
                                                    }
                                                }
                                                // Pending.
                                                else
                                                {
                                                    if (blockTransaction.TransactionObject.WalletAddressSender == walletAddress)
                                                    {
                                                        availableBalance -= (blockTransaction.TransactionObject.Amount + blockTransaction.TransactionObject.Fee);
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

            return availableBalance;
        }

        /// <summary>
        /// Retrieve back a transaction object from data synced by his hash and his block height if possible.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="fromSyncCacheUpdate"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<Tuple<bool, ClassBlockTransaction>> GetTransactionObjectFromSync(string walletAddress, string transactionHash, long blockHeight, bool fromSyncCacheUpdate, CancellationTokenSource cancellation)
        {
            ClassBlockTransaction blockTransaction = null;
            bool isMemPool = false;
            bool wasEmpty = false;

            if (blockHeight < BlockchainSetting.GenesisBlockHeight)
            {
                blockHeight = ClassTransactionUtility.GetBlockHeightFromTransactionHash(transactionHash);
            }

            // Retrieve from the sync cache.
            if (!fromSyncCacheUpdate)
            {
                blockTransaction = GetBlockTransactionFromSyncCache(walletAddress, transactionHash, blockHeight, cancellation, out isMemPool);

                wasEmpty = blockTransaction == null;
            }

            if (blockTransaction == null)
            {
                switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
                {
                    case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                        {
                            blockTransaction = await GetWalletBlockTransactionFromTransactionHashFromInternalSyncMode(transactionHash, blockHeight, !fromSyncCacheUpdate, cancellation);
                            if (blockTransaction != null)
                            {
                                isMemPool = false;
                            }
                        }
                        break;
                    case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                        {
                            // To do.
                        }
                        break;
                }
            }

            // Update sync cache.
            if (!fromSyncCacheUpdate || wasEmpty)
            {
                if (blockTransaction != null)
                {
                    InsertOrUpdateBlockTransactionToSyncCache(walletAddress, blockTransaction, isMemPool, cancellation);
                }
            }


            return new Tuple<bool, ClassBlockTransaction>(isMemPool, blockTransaction);
        }

        /// <summary>
        /// Retrieve back a mem pool transaction object from the synced data by his hash.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<ClassTransactionObject> GetMemPoolTransactionObjectFromSync(string walletAddress, string transactionHash, bool fromSyncCache, CancellationTokenSource cancellation)
        {
            if (!fromSyncCache)
            {
                if (DatabaseSyncCache.ContainsKey(walletAddress))
                {
                    long blockHeightTransaction = ClassTransactionUtility.GetBlockHeightFromTransactionHash(transactionHash);

                    if (DatabaseSyncCache[walletAddress].ContainsBlockHeight(blockHeightTransaction, cancellation))
                    {
                        if (DatabaseSyncCache[walletAddress].ContainsBlockTransactionFromTransactionHashAndBlockHeight(blockHeightTransaction, transactionHash, cancellation))
                        {
                            return DatabaseSyncCache[walletAddress].GetSyncBlockTransactionCached(blockHeightTransaction, transactionHash).BlockTransaction.TransactionObject;
                        }
                    }
                }
            }

            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        return await GetWalletMemPoolTransactionFromTransactionHashFromInternalSyncMode(transactionHash, cancellation);
                    }
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        // To do.
                    }
                    break;
            }
            return null;
        }

        /// <summary>
        /// Get the last block height from data synced.
        /// </summary>
        /// <returns></returns>
        public long GetLastBlockHeightUnlockedSynced(CancellationTokenSource cancellation)
        {
            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        return ClassBlockchainStats.GetLastBlockHeightUnlocked(cancellation);
                    }
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        // To do.
                    }
                    break;
            }
            return 0;
        }

        /// <summary>
        /// Get the last block height from data synced.
        /// </summary>
        /// <returns></returns>
        public long GetLastBlockHeightSynced()
        {
            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        return ClassBlockchainStats.GetLastBlockHeight();
                    }
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        // To do.
                    }
                    break;
            }
            return 0;
        }

        /// <summary>
        /// Get the last block height confirmation done.
        /// </summary>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<long> GetLastBlockHeightUnlockedConfirmationDone(CancellationTokenSource cancellation)
        {
            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        return await ClassBlockchainDatabase.BlockchainMemoryManagement.GetLastBlockHeightTransactionConfirmationDone(cancellation);
                    }
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        // To do.
                    }
                    break;
            }
            return 0;
        }

        /// <summary>
        /// Get the last blockchain network stats object.
        /// </summary>
        /// <returns></returns>
        public ClassBlockchainNetworkStatsObject GetBlockchainNetworkStatsObject()
        {
            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        return ClassBlockchainStats.GetBlockchainNetworkStatsObject;
                    }
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        // To do.
                    }
                    break;
            }

            return null;
        }

        /// <summary>
        /// Get the total amount of mempool transaction(s)
        /// </summary>
        /// <returns></returns>
        public long GetTotalMemPoolTransactionFromSync()
        {
            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        return ClassMemPoolDatabase.GetCountMemPoolTx;
                    }
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        // To do.
                    }
                    break;
            }
            return 0;
        }

        /// <summary>
        /// Get the timestamp create of a block height target.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<long> GetBlockTimestampCreateFromBlockHeight(long blockHeight, CancellationTokenSource cancellation)
        {
            long timestamp = 0;
            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        if (ClassBlockchainDatabase.BlockchainMemoryManagement.ContainsKey(blockHeight))
                        {
                            var blockInformationsData = await ClassBlockchainDatabase.BlockchainMemoryManagement.GetBlockInformationDataStrategy(blockHeight, cancellation);
                            if (blockInformationsData != null)
                            {
                                timestamp = blockInformationsData.TimestampCreate;
                            }
                        }
                    }
                    break;
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        // To do.
                    }
                    break;
            }

            return timestamp;
        }

        /// <summary>
        /// Generate the block height start transaction confirmation from the sync.
        /// </summary>
        /// <param name="lastBlockHeightUnlocked"></param>
        /// <param name="lastBlockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<long> GenerateBlockHeightStartTransactionConfirmationFromSync(long lastBlockHeightUnlocked, long lastBlockHeight, CancellationTokenSource cancellation)
        {
            long blockHeightStart = 0;

            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        blockHeightStart = await ClassTransactionUtility.GenerateBlockHeightStartTransactionConfirmation(lastBlockHeightUnlocked, lastBlockHeight, cancellation);
                    }
                    break;
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        // To do.
                    }
                    break;
            }

            return blockHeightStart;
        }

        /// <summary>
        /// Get fee cost confirmation from the whole activity of the blockchain of the synced data.
        /// </summary>
        /// <param name="lastBlockHeightUnlocked"></param>
        /// <param name="blockHeightConfirmationStart"></param>
        /// <param name="blockHeightConfirmationTarget"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<Tuple<BigInteger, bool>> GetFeeCostConfirmationFromWholeActivityBlockchainFromSync(long lastBlockHeightUnlocked, long blockHeightConfirmationStart, long blockHeightConfirmationTarget, CancellationTokenSource cancellation)
        {
            Tuple<BigInteger, bool> calculationFeeCostConfirmation = new Tuple<BigInteger, bool>(0, false);

            switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
            {
                case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                    {
                        calculationFeeCostConfirmation = await ClassTransactionUtility.GetFeeCostFromWholeBlockchainTransactionActivity(lastBlockHeightUnlocked, blockHeightConfirmationStart, blockHeightConfirmationTarget, cancellation);
                    }
                    break;
                case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                    {
                        // To do.
                    }
                    break;
            }

            return calculationFeeCostConfirmation;
        }

        #endregion

        #region Sync wallet functions in internal mode.

        /// <summary>
        /// Update the wallet sync data in internal sync mode.
        /// </summary>
        /// <param name="walletFileName"></param>
        /// <param name="cancellation"></param>
        private async Task<bool> UpdateWalletSyncFromInternalSyncModeAsync(string walletFileName, CancellationTokenSource cancellation)
        {
            bool changeDone = false;

            if (ClassBlockchainDatabase.BlockchainMemoryManagement.GetLastBlockHeight >= BlockchainSetting.GenesisBlockHeight)
            {
                string walletAddress = ClassDesktopWalletCommonData.WalletDatabase.GetWalletAddressFromWalletFileName(walletFileName);

                long lastBlockHeightUnlocked = ClassBlockchainStats.GetLastBlockHeightUnlocked(cancellation);

                long blockHeightStart = ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletLastBlockHeightSynced;

                bool cancelled = false;

                using (var disposableListTransaction = await ClassMemPoolDatabase.GetMemPoolAllTxFromWalletAddressTargetAsync(walletAddress, cancellation))
                {
                    foreach (ClassTransactionObject memPoolTransactionObject in disposableListTransaction.GetAll)
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        if (memPoolTransactionObject != null)
                        {
                            if (memPoolTransactionObject.WalletAddressReceiver == walletAddress || memPoolTransactionObject.WalletAddressSender == walletAddress)
                            {
                                bool alreadyConfirmed = false;
                                if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTransactionList.ContainsKey(memPoolTransactionObject.BlockHeightTransaction))
                                {
                                    if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTransactionList[memPoolTransactionObject.BlockHeightTransaction].Contains(memPoolTransactionObject.TransactionHash))
                                    {
                                        alreadyConfirmed = true;

                                    }
                                }
                                if (!alreadyConfirmed)
                                {
                                    if (!ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletMemPoolTransactionList.Contains(memPoolTransactionObject.TransactionHash))
                                    {
                                        if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletMemPoolTransactionList.Add(memPoolTransactionObject.TransactionHash))
                                        {
                                            ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTotalMemPoolTransaction++;
                                            changeDone = true;
                                            InsertOrUpdateBlockTransactionToSyncCache(walletAddress, new ClassBlockTransaction()
                                            {
                                                TransactionObject = memPoolTransactionObject,
                                                TransactionStatus = true,
                                                TransactionBlockHeightInsert = memPoolTransactionObject.BlockHeightTransaction,
                                                TransactionBlockHeightTarget = memPoolTransactionObject.BlockHeightTransactionConfirmationTarget
                                            }, true, cancellation);
                                            ClassLog.WriteLine(memPoolTransactionObject.TransactionHash + " tx hash of wallet address: " + walletAddress + " from mempool has been synced successfully.", ClassEnumLogLevelType.LOG_LEVEL_WALLET, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
                                        }
                                    }
                                }
                                else
                                {
                                    if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletMemPoolTransactionList.Contains(memPoolTransactionObject.TransactionHash))
                                    {
                                        ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletMemPoolTransactionList.Remove(memPoolTransactionObject.TransactionHash);
                                        changeDone = true;
                                    }
                                }
                            }
                        }
                    }
                }

                if (!cancelled)
                {
                    if (blockHeightStart <= lastBlockHeightUnlocked)
                    {
                        for (long blockHeight = blockHeightStart; blockHeight <= lastBlockHeightUnlocked; blockHeight++)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();

                            if (blockHeight >= BlockchainSetting.GenesisBlockHeight && blockHeight <= lastBlockHeightUnlocked)
                            {
                                ClassBlockObject blockObject = await ClassBlockchainDatabase.BlockchainMemoryManagement.GetBlockDataStrategy(blockHeight, false, cancellation);

                                lock (blockObject)
                                {
                                    if (blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                                    {
                                        if (blockObject?.BlockTransactions != null)
                                        {
                                            lock (blockObject.BlockTransactions)
                                            {
                                                if (blockObject.BlockTransactions.Count > 0)
                                                {
                                                    foreach (var transactionPair in blockObject.BlockTransactions)
                                                    {
                                                        cancellation.Token.ThrowIfCancellationRequested();

                                                        if (transactionPair.Value.TransactionObject.WalletAddressReceiver == walletAddress || transactionPair.Value.TransactionObject.WalletAddressSender == walletAddress)
                                                        {
                                                            if (!ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTransactionList.ContainsKey(blockHeight))
                                                            {
                                                                ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTransactionList.Add(blockHeight, new HashSet<string>());
                                                            }

                                                            if (!ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTransactionList[blockHeight].Contains(transactionPair.Key))
                                                            {
                                                                if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTransactionList[blockHeight].Add(transactionPair.Key))
                                                                {
                                                                    changeDone = true;
                                                                    ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTotalTransaction++;
                                                                    InsertOrUpdateBlockTransactionToSyncCache(walletAddress, transactionPair.Value, false, cancellation);
                                                                }
                                                                else
                                                                {
                                                                    cancelled = true;
#if DEBUG
                                                                    Debug.WriteLine("Transaction hash: " + transactionPair.Key + " from the block height: " + blockHeight + "can't be inserted into the wallet file data: " + walletFileName);
#endif
                                                                    ClassLog.WriteLine("Transaction hash: " + transactionPair.Key + " from the block height: " + blockHeight + "can't be inserted into the wallet file data: " + walletFileName, ClassEnumLogLevelType.LOG_LEVEL_WALLET, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
                                                                    break;
                                                                }
                                                            }

                                                            if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletMemPoolTransactionList.Contains(transactionPair.Key))
                                                            {
                                                                ClassLog.WriteLine(transactionPair.Key + " tx hash of block height: " + blockHeight + " seems to has been accepted by the network to be proceed, remove it from the mempool.", ClassEnumLogLevelType.LOG_LEVEL_WALLET, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);

                                                                if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletMemPoolTransactionList.Remove(transactionPair.Key))
                                                                {
                                                                    ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTotalMemPoolTransaction--;
                                                                    changeDone = true;
                                                                }
                                                                else
                                                                {
                                                                    cancelled = true;
                                                                    break;
                                                                }
                                                            }
                                                        }

                                                    }

                                                    ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletLastBlockHeightSynced = blockObject.BlockHeight;
                                                }
                                                else
                                                {
                                                    cancelled = true;
                                                    break;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            cancelled = true;
#if DEBUG
                                            Debug.WriteLine("The block height " + blockHeight + " is empty for the wallet file data: " + walletFileName);
#endif
                                            ClassLog.WriteLine("The block height " + blockHeight + " is empty for the wallet file data: " + walletFileName, ClassEnumLogLevelType.LOG_LEVEL_WALLET, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
                                            break;
                                        }

                                        if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTransactionList.ContainsKey(blockHeight))
                                        {
                                            if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTransactionList[blockHeight].Count > 0)
                                            {
                                                foreach (string txHash in ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTransactionList[blockHeight])
                                                {
                                                    cancellation.Token.ThrowIfCancellationRequested();


                                                    if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletMemPoolTransactionList.Contains(txHash))
                                                    {
                                                        ClassLog.WriteLine(txHash + " tx hash of block height: " + blockHeight + " seems to has been accepted by the network to be proceed, remove it from the mempool.", ClassEnumLogLevelType.LOG_LEVEL_WALLET, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);

                                                        if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletMemPoolTransactionList.Remove(txHash))
                                                        {
                                                            changeDone = true;
                                                            ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletTotalMemPoolTransaction--;
                                                        }
                                                        else
                                                        {
                                                            cancelled = true;
                                                            break;
                                                        }
                                                    }
                                                }

                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (changeDone && !cancelled && !cancellation.IsCancellationRequested)
                        {
                            ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletLastBlockHeightSynced = lastBlockHeightUnlocked;
                        }


                        if (cancelled)
                        {
                            changeDone = false;
                            ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletEnableRescan = true;
                        }

                    }
                    else if (blockHeightStart > lastBlockHeightUnlocked)
                    {

                        ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileName].WalletEnableRescan = true;
#if DEBUG
                        Debug.WriteLine("Warning the wallet file data: " + walletFileName + " last block height sync progress is above the current one synced: " + blockHeightStart + "/" + lastBlockHeightUnlocked);
#endif
                        ClassLog.WriteLine("Warning the wallet file data: " + walletFileName + " last block height sync progress is above the current one synced: " + blockHeightStart + "/" + lastBlockHeightUnlocked, ClassEnumLogLevelType.LOG_LEVEL_WALLET, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);


                    }
                }

            }

            return changeDone;
        }

        /// <summary>
        /// Retrieve back a block transaction from his hash and his block height in internal sync mode.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="useTransactionCache"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<ClassBlockTransaction> GetWalletBlockTransactionFromTransactionHashFromInternalSyncMode(string transactionHash, long blockHeight, bool useTransactionCache, CancellationTokenSource cancellation)
        {
            return await ClassBlockchainDatabase.BlockchainMemoryManagement.GetBlockTransactionFromSpecificTransactionHashAndHeight(transactionHash, blockHeight, useTransactionCache, cancellation);
        }

        /// <summary>
        /// Retrieve back a transaction by his hash from the mem pool.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<ClassTransactionObject> GetWalletMemPoolTransactionFromTransactionHashFromInternalSyncMode(string transactionHash, CancellationTokenSource cancellation)
        {
            if (await ClassMemPoolDatabase.CheckTxHashExist(transactionHash, cancellation))
            {
                return await ClassMemPoolDatabase.GetMemPoolTxFromTransactionHash(transactionHash, cancellation);
            }
            return null;
        }

        #endregion

        #region Sync wallet functions in external mode.


        #endregion

        #region Related of build & send transaction functions.

        /// <summary>
        /// Build and send a transaction.
        /// </summary>
        /// <param name="walletFileOpened"></param>
        /// <param name="walletAddressTarget"></param>
        /// <param name="amountToSend"></param>
        /// <param name="feeToPay"></param>
        /// <param name="paymentId"></param>
        /// <param name="totalConfirmationTarget"></param>
        /// <param name="walletPrivateKeySender"></param>
        /// <param name="transactionAmountSourceList"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> BuildAndSendTransaction(string walletFileOpened, string walletAddressTarget, decimal amountToSend, decimal feeToPay, long paymentId, int totalConfirmationTarget, string walletPrivateKeySender, Dictionary<string, ClassTransactionHashSourceObject> transactionAmountSourceList, CancellationTokenSource cancellation)
        {
            bool sendTransactionStatus = false;

            try
            {
                long lastBlockHeight = GetLastBlockHeightSynced();
                long lastBlockHeightUnlocked = GetLastBlockHeightUnlockedSynced(cancellation);
                long blockHeightTransaction = await GenerateBlockHeightStartTransactionConfirmationFromSync(lastBlockHeightUnlocked, lastBlockHeight, cancellation);
                long lastBlockHeightTimestampCreate = await GetBlockTimestampCreateFromBlockHeight(lastBlockHeight, cancellation);

                string walletAddressSender = ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileOpened].WalletAddress;
                string walletPublicKeySender = ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileOpened].WalletPublicKey;


                ClassTransactionObject transactionObject = ClassTransactionUtility.BuildTransaction(blockHeightTransaction,
                                                        blockHeightTransaction + totalConfirmationTarget,
                                                        walletAddressSender,
                                                        walletPublicKeySender,
                                                        string.Empty,
                                                        (BigInteger)(amountToSend * BlockchainSetting.CoinDecimal),
                                                        (BigInteger)(feeToPay * BlockchainSetting.CoinDecimal),
                                                        walletAddressTarget,
                                                        ClassUtility.GetCurrentTimestampInSecond(),
                                                        ClassTransactionEnumType.NORMAL_TRANSACTION,
                                                        paymentId,
                                                        string.Empty,
                                                        string.Empty,
                                                        walletPrivateKeySender,
                                                        string.Empty, transactionAmountSourceList, lastBlockHeightTimestampCreate, cancellation);

                if (ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileOpened].WalletEncrypted)
                {
                    walletPrivateKeySender.Clear();
                }

                if (transactionObject != null)
                {
                    bool insertedInMemPool = true;

                    switch (ClassDesktopWalletCommonData.WalletSettingObject.WalletSyncMode)
                    {
                        case ClassWalletSettingEnumSyncMode.INTERNAL_PEER_SYNC_MODE:
                            {
                                string nodeLocalIp = _nodeInstance.PeerSettingObject.PeerNetworkSettingObject.ListenIp;
                                string openNatIp = _nodeInstance.PeerOpenNatPublicIp;

                                ClassTransactionEnumStatus sendTransactionResult = await ClassPeerNetworkBroadcastFunction.AskMemPoolTxVoteToPeerListsAsync(nodeLocalIp, openNatIp, openNatIp, transactionObject, _nodeInstance.PeerSettingObject.PeerNetworkSettingObject, _nodeInstance.PeerSettingObject.PeerFirewallSettingObject, cancellation, true);

#if DEBUG
                                Debug.WriteLine("Send transaction request Tx response status: " + System.Enum.GetName(typeof(ClassTransactionEnumStatus), sendTransactionResult));
#endif
                                ClassLog.WriteLine("Send transaction request  Tx response status: " + System.Enum.GetName(typeof(ClassTransactionEnumStatus), sendTransactionResult), ClassEnumLogLevelType.LOG_LEVEL_WALLET, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);

                                if (sendTransactionResult == ClassTransactionEnumStatus.VALID_TRANSACTION)
                                {
                                    sendTransactionStatus = true;

                                    insertedInMemPool = await ClassMemPoolDatabase.InsertTxToMemPoolAsync(transactionObject, cancellation);
                                }
                            }
                            break;
                        case ClassWalletSettingEnumSyncMode.EXTERNAL_PEER_SYNC_MODE:
                            {

                            }
                            break;
                    }

                    if (sendTransactionStatus && insertedInMemPool)
                    {
                        InsertOrUpdateBlockTransactionToSyncCache(walletAddressSender, new ClassBlockTransaction()
                        {
                            TransactionObject = transactionObject,
                            TransactionStatus = true,
                            TransactionBlockHeightInsert = transactionObject.BlockHeightTransaction,
                            TransactionBlockHeightTarget = transactionObject.BlockHeightTransactionConfirmationTarget
                        }, true, cancellation);
                    }

                }
            }
            catch (Exception error)
            {
#if DEBUG
                Debug.WriteLine("Error on sending a transaction. Exception: " + error.Message);
#endif
                ClassLog.WriteLine("Error on sending a transaction. Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_WALLET, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
            }
            return sendTransactionStatus;
        }

        /// <summary>
        /// Calculate the fee cost size of the transaction virtually before to send it.
        /// </summary>
        /// <param name="walletFileOpened"></param>
        /// <param name="sendAmount"></param>
        /// <param name="totalConfirmationTarget"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<ClassSendTransactionFeeCostCalculationResultObject> GetTransactionFeeCostVirtuallyFromSync(string walletFileOpened, decimal sendAmount, int totalConfirmationTarget, CancellationTokenSource cancellation)
        {
            ClassSendTransactionFeeCostCalculationResultObject sendTransactionFeeCostCalculationResult = new ClassSendTransactionFeeCostCalculationResultObject();

            BigInteger amountToSpend = (BigInteger)(sendAmount * BlockchainSetting.CoinDecimal);

            BigInteger amountSpend = 0;
            Dictionary<string, ClassWalletSyncAmountSpendObject> listUnspend = new Dictionary<string, ClassWalletSyncAmountSpendObject>();

            string walletAddress = ClassDesktopWalletCommonData.WalletDatabase.DictionaryWalletData[walletFileOpened].WalletAddress;

            if (DatabaseSyncCache.ContainsKey(walletAddress))
            {
                bool syncCacheLocked = false;
                try
                {
                    if (Monitor.TryEnter(DatabaseSyncCache[walletAddress]))
                    {
                        syncCacheLocked = true;

                        if (DatabaseSyncCache[walletAddress].CountBlockHeight > 0)
                        {
                            #region Generate list unspend.

                            // Calculate rest of amounts available to use from spent transactions.
                            foreach (long blockHeight in DatabaseSyncCache[walletAddress].BlockHeightKeys(cancellation))
                            {
                                cancellation?.Token.ThrowIfCancellationRequested();

                                if (DatabaseSyncCache[walletAddress].CountBlockTransactionFromBlockHeight(blockHeight, cancellation) > 0)
                                {
                                    foreach (var transactionPair in DatabaseSyncCache[walletAddress].GetBlockTransactionFromBlockHeight(blockHeight, cancellation))
                                    {
                                        cancellation?.Token.ThrowIfCancellationRequested();

                                        if (transactionPair.Value.BlockTransaction != null)
                                        {
                                            if (transactionPair.Value.BlockTransaction.TransactionStatus)
                                            {
                                                if (transactionPair.Value.BlockTransaction.TransactionObject.WalletAddressSender == walletAddress)
                                                {
                                                    foreach (var txAmountSpent in transactionPair.Value.BlockTransaction.TransactionObject.AmountTransactionSource)
                                                    {
                                                        cancellation?.Token.ThrowIfCancellationRequested();

                                                        long blockTxAmountSpend = ClassTransactionUtility.GetBlockHeightFromTransactionHash(txAmountSpent.Key);

                                                        ClassBlockTransaction blockTransaction = DatabaseSyncCache[walletAddress].GetSyncBlockTransactionCached(blockTxAmountSpend, txAmountSpent.Key).BlockTransaction;

                                                        if (blockTransaction.TransactionObject.WalletAddressReceiver == walletAddress)
                                                        {
                                                            if (!listUnspend.ContainsKey(txAmountSpent.Key))
                                                            {
                                                                listUnspend.Add(txAmountSpent.Key, new ClassWalletSyncAmountSpendObject()
                                                                {
                                                                    TxAmount = blockTransaction.TransactionObject.Amount,
                                                                    AmountSpend = 0
                                                                });
                                                            }

                                                            if (listUnspend.ContainsKey(txAmountSpent.Key))
                                                            {
                                                                if (!listUnspend[txAmountSpent.Key].Spend)
                                                                {
                                                                    if (txAmountSpent.Value.Amount > 0)
                                                                    {

                                                                        listUnspend[txAmountSpent.Key].AmountSpend += txAmountSpent.Value.Amount;

                                                                        if (listUnspend[txAmountSpent.Key].TxAmount <= listUnspend[txAmountSpent.Key].AmountSpend)
                                                                        {
                                                                            listUnspend[txAmountSpent.Key].Spend = true;

#if DEBUG
                                                                            if (listUnspend[txAmountSpent.Key].TxAmount < listUnspend[txAmountSpent.Key].AmountSpend)
                                                                            {
                                                                                Debug.WriteLine("Warning, the spending of the tx hash: " + txAmountSpent.Key + " is above the amount. Spend " + listUnspend[txAmountSpent.Key].AmountSpend + "/" + listUnspend[txAmountSpent.Key].TxAmount);
                                                                            }
#endif
                                                                        }

                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                                else if (transactionPair.Value.BlockTransaction.TransactionObject.WalletAddressReceiver == walletAddress)
                                                {
                                                    if (!listUnspend.ContainsKey(transactionPair.Key))
                                                    {
                                                        listUnspend.Add(transactionPair.Key, new ClassWalletSyncAmountSpendObject()
                                                        {
                                                            TxAmount = DatabaseSyncCache[walletAddress].GetSyncBlockTransactionCached(blockHeight, transactionPair.Key).BlockTransaction.TransactionObject.Amount,
                                                            AmountSpend = 0
                                                        });
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            #endregion

                            #region Generate transaction hash source list from the amount to spend.

                            if (listUnspend.Count > 0)
                            {
                                foreach (string transactionHash in listUnspend.Keys)
                                {
                                    cancellation?.Token.ThrowIfCancellationRequested();

                                    if (!listUnspend[transactionHash].Spend)
                                    {
                                        BigInteger totalRest = listUnspend[transactionHash].TxAmount - listUnspend[transactionHash].AmountSpend;
                                        if (totalRest + amountSpend > amountToSpend)
                                        {
                                            BigInteger spendRest = amountToSpend - amountSpend;

                                            if (spendRest > 0)
                                            {
                                                if (totalRest >= spendRest)
                                                {
                                                    amountSpend += spendRest;

                                                    sendTransactionFeeCostCalculationResult.TransactionAmountSourceList.Add(transactionHash, new ClassTransactionHashSourceObject()
                                                    {
                                                        Amount = spendRest,
                                                    });
                                                }
                                            }
                                        }
                                        else
                                        {
                                            amountSpend += totalRest;
                                            sendTransactionFeeCostCalculationResult.TransactionAmountSourceList.Add(transactionHash, new ClassTransactionHashSourceObject()
                                            {
                                                Amount = totalRest,
                                            });
                                        }
                                    }

                                    if (amountSpend == amountToSpend)
                                    {
                                        break;
                                    }
                                }
                            }

                            #endregion
                        }
#if DEBUG
                        else
                        {
                            Debug.WriteLine("No transaction synced on the cache for the wallet address: " + walletAddress);
                        }
#endif
                    }
                }
                finally
                {
                    if (syncCacheLocked)
                    {
                        Monitor.Exit(DatabaseSyncCache[walletAddress]);
                    }
                }
            }
#if DEBUG
            else
            {
                Debug.WriteLine("No transaction synced on the cache for the wallet address: " + walletAddress);
            }
#endif

            if (amountToSpend == amountSpend)
            {
                sendTransactionFeeCostCalculationResult.Failed = false;

                sendTransactionFeeCostCalculationResult.TotalFeeCost = ClassTransactionUtility.GetBlockTransactionVirtualMemorySizeOnSending(sendTransactionFeeCostCalculationResult.TransactionAmountSourceList, amountToSpend);
                sendTransactionFeeCostCalculationResult.FeeSizeCost += sendTransactionFeeCostCalculationResult.TotalFeeCost;

                long lastBlockHeight = GetLastBlockHeightSynced();

                long lastBlockHeightUnlocked = GetLastBlockHeightUnlockedSynced(cancellation);
                long blockHeightConfirmationStart = await GenerateBlockHeightStartTransactionConfirmationFromSync(lastBlockHeightUnlocked, lastBlockHeight, cancellation);
                long blockHeightConfirmationTarget = blockHeightConfirmationStart + totalConfirmationTarget;
                Tuple<BigInteger, bool> calculationFeeCostConfirmation = await GetFeeCostConfirmationFromWholeActivityBlockchainFromSync(lastBlockHeightUnlocked, blockHeightConfirmationStart, blockHeightConfirmationTarget, cancellation);

                if (calculationFeeCostConfirmation.Item2)
                {
                    sendTransactionFeeCostCalculationResult.TotalFeeCost += calculationFeeCostConfirmation.Item1;
                    sendTransactionFeeCostCalculationResult.FeeConfirmationCost = calculationFeeCostConfirmation.Item1;

                    sendTransactionFeeCostCalculationResult.TransactionAmountSourceList.Clear();
                    amountToSpend = ((BigInteger)(sendAmount * BlockchainSetting.CoinDecimal)) + sendTransactionFeeCostCalculationResult.TotalFeeCost;
                    amountSpend = 0;

                    #region Regenerate amount hash transaction source list with fees calculated.

                    if (listUnspend.Count > 0)
                    {
                        foreach (string transactionHash in listUnspend.Keys)
                        {
                            if (!listUnspend[transactionHash].Spend)
                            {
                                BigInteger totalRest = listUnspend[transactionHash].TxAmount - listUnspend[transactionHash].AmountSpend;
                                if (totalRest + amountSpend > amountToSpend)
                                {
                                    BigInteger spendRest = amountToSpend - amountSpend;

                                    if (spendRest > 0)
                                    {
                                        if (totalRest >= spendRest)
                                        {
                                            amountSpend += spendRest;

                                            sendTransactionFeeCostCalculationResult.TransactionAmountSourceList.Add(transactionHash, new ClassTransactionHashSourceObject()
                                            {
                                                Amount = spendRest,
                                            });
                                        }
                                    }
                                }
                                else
                                {
                                    amountSpend += totalRest;
                                    sendTransactionFeeCostCalculationResult.TransactionAmountSourceList.Add(transactionHash, new ClassTransactionHashSourceObject()
                                    {
                                        Amount = totalRest,
                                    });
                                }
                            }

                            if (amountSpend == amountToSpend)
                            {
                                break;
                            }
                        }
                    }

                    #endregion

                    if (amountSpend > amountToSpend)
                    {
                        sendTransactionFeeCostCalculationResult.Failed = true;
                    }
                }
                else
                {
                    sendTransactionFeeCostCalculationResult.Failed = true;
                }
            }

            // Clean up.
            listUnspend.Clear();

            return sendTransactionFeeCostCalculationResult;
        }

        #endregion
    }
}

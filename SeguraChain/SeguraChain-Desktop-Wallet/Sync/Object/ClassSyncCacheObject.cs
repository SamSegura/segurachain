using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using SeguraChain_Lib.Utility;
using System.Numerics;
using System.Threading.Tasks;
using SeguraChain_Lib.Other.Object.List;
using SeguraChain_Lib.Blockchain.Transaction.Utility;

namespace SeguraChain_Desktop_Wallet.Sync.Object
{
    public class ClassSyncCacheObject
    {
        /// <summary>
        /// Handle multithreading access.
        /// </summary>
        private SemaphoreSlim _semaphoreDictionaryAccess;

        /// <summary>
        /// Store the cache.
        /// </summary>
        private Dictionary<long, Dictionary<string, ClassSyncCacheBlockTransactionObject>> _syncCacheDatabase;

        public BigInteger AvailableBalance;
        public BigInteger PendingBalance;
        public BigInteger TotalBalance;

        /// <summary>
        /// Get the total amount of transactions cached.
        /// </summary>
        public long TotalTransactions
        {
            get
            {
                long totalTransactions = 0;

                using (var listBlockHeight = BlockHeightKeys)
                    foreach (long blockHeight in listBlockHeight.GetList)
                        totalTransactions += _syncCacheDatabase[blockHeight].Count;

                return totalTransactions;
            }
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        public ClassSyncCacheObject()
        {
            _syncCacheDatabase = new Dictionary<long, Dictionary<string, ClassSyncCacheBlockTransactionObject>>();
            _semaphoreDictionaryAccess = new SemaphoreSlim(1, 1);
        }

        /// <summary>
        /// Get the amount of block height.
        /// </summary>
        public int CountBlockHeight => _syncCacheDatabase.Count;


        /// <summary>
        /// Get the list of block heights.
        /// </summary>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public DisposableList<long> BlockHeightKeys => new DisposableList<long>(true, 0, _syncCacheDatabase.Keys.ToList());


        /// <summary>
        /// Check if a block height is stored.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <returns></returns>
        public bool ContainsBlockHeight(long blockHeight) => _syncCacheDatabase.ContainsKey(blockHeight);

        /// <summary>
        /// Insert a block height to the cache.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> InsertBlockHeight(long blockHeight, CancellationTokenSource cancellation)
        {
            bool result = false;
            bool semaphoreUsed = false;
            try
            {
                await _semaphoreDictionaryAccess.WaitAsync(cancellation.Token);
                semaphoreUsed = true;

                result = _syncCacheDatabase.ContainsKey(blockHeight);

                if (!result)
                {
                    _syncCacheDatabase.Add(blockHeight, new Dictionary<string, ClassSyncCacheBlockTransactionObject>());
                    result = true;
                }
            }
            finally
            {
                if (semaphoreUsed)
                    _semaphoreDictionaryAccess.Release();
            }
            return result;
        }

        /// <summary>
        /// Count the amount of block transaction stored at a specific block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<int> CountBlockTransactionFromBlockHeight(long blockHeight, CancellationTokenSource cancellation)
        {
            int count = 0;

            bool semaphoreUsed = false;
            try
            {
                await _semaphoreDictionaryAccess.WaitAsync(cancellation.Token);
                semaphoreUsed = true;

                if (_syncCacheDatabase.ContainsKey(blockHeight))
                    count = _syncCacheDatabase[blockHeight].Count;

            }
            finally
            {
                if (semaphoreUsed)
                    _semaphoreDictionaryAccess.Release();
            }

            return count;
        }

        /// <summary>
        /// Check if a block transaction is stored at a specific block height with a transaction hash provided.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="transactionHash"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> ContainsBlockTransactionFromTransactionHashAndBlockHeight(long blockHeight, string transactionHash, CancellationTokenSource cancellation)
        {
            bool result = false;
            bool semaphoreUsed = false;
            try
            {
                await _semaphoreDictionaryAccess.WaitAsync(cancellation.Token);
                semaphoreUsed = true;

                if (_syncCacheDatabase.ContainsKey(blockHeight))
                    result = _syncCacheDatabase[blockHeight].ContainsKey(transactionHash);
            }
            finally
            {
                if (semaphoreUsed)
                    _semaphoreDictionaryAccess.Release();
            }

            return result;
        }

        /// <summary>
        /// Insert a block transaction to the cache.
        /// </summary>
        /// <param name="syncCacheBlockTransactionObject"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> InsertBlockTransaction(ClassSyncCacheBlockTransactionObject syncCacheBlockTransactionObject, CancellationTokenSource cancellation)
        {
            bool result = false;
            bool semaphoreUsed = false;
            try
            {
                await _semaphoreDictionaryAccess.WaitAsync(cancellation.Token);
                semaphoreUsed = true;

                bool insertBlockHeight = true;

                if (!_syncCacheDatabase.ContainsKey(syncCacheBlockTransactionObject.BlockTransaction.TransactionObject.BlockHeightTransaction))
                {
                    _syncCacheDatabase.Add(syncCacheBlockTransactionObject.BlockTransaction.TransactionObject.BlockHeightTransaction, new Dictionary<string, ClassSyncCacheBlockTransactionObject>());
                    insertBlockHeight = true;
                }

                if (insertBlockHeight)
                {
                    if (!_syncCacheDatabase[syncCacheBlockTransactionObject.BlockTransaction.TransactionObject.BlockHeightTransaction].ContainsKey(syncCacheBlockTransactionObject.BlockTransaction.TransactionObject.TransactionHash))
                    {
                        _syncCacheDatabase[syncCacheBlockTransactionObject.BlockTransaction.TransactionObject.BlockHeightTransaction].Add(syncCacheBlockTransactionObject.BlockTransaction.TransactionObject.TransactionHash, syncCacheBlockTransactionObject);
                        result = true;
                    }
                    else
                    {
                        _syncCacheDatabase[syncCacheBlockTransactionObject.BlockTransaction.TransactionObject.BlockHeightTransaction][syncCacheBlockTransactionObject.BlockTransaction.TransactionObject.TransactionHash] = syncCacheBlockTransactionObject;
                        result = true;
                    }
                }
            }
            finally
            {
                if (semaphoreUsed)
                    _semaphoreDictionaryAccess.Release();
            }

            return result;
        }

        /// <summary>
        /// Update a block transaction stored into the cache.
        /// </summary>
        /// <param name="blockTransaction"></param>
        /// <param name="isMemPool"></param>
        public void UpdateBlockTransaction(ClassBlockTransaction blockTransaction, bool isMemPool)
        {
            _syncCacheDatabase[blockTransaction.TransactionObject.BlockHeightTransaction][blockTransaction.TransactionObject.TransactionHash].BlockTransaction = blockTransaction;
            _syncCacheDatabase[blockTransaction.TransactionObject.BlockHeightTransaction][blockTransaction.TransactionObject.TransactionHash].IsMemPool = isMemPool;
        }

        /// <summary>
        /// Clear the cache.
        /// </summary>
        /// <param name="cancellation"></param>
        public async Task Clear(CancellationTokenSource cancellation)
        {
            bool semaphoreUsed = false;
            try
            {
                await _semaphoreDictionaryAccess.WaitAsync(cancellation.Token);
                semaphoreUsed = true;

                _syncCacheDatabase.Clear();
            }
            finally
            {
                if (semaphoreUsed)
                    _semaphoreDictionaryAccess.Release();
            }
        }

        /// <summary>
        /// Return every block transactions stored at a specific block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<DisposableDictionary<string, ClassSyncCacheBlockTransactionObject>> GetBlockTransactionFromBlockHeight(long blockHeight, CancellationTokenSource cancellation)
        {
            DisposableDictionary<string, ClassSyncCacheBlockTransactionObject> listBlockTransaction = new DisposableDictionary<string, ClassSyncCacheBlockTransactionObject>();

            bool semaphoreUsed = false;

            try
            {
                await _semaphoreDictionaryAccess.WaitAsync(cancellation.Token);
                semaphoreUsed = true;

                if (_syncCacheDatabase.ContainsKey(blockHeight))
                    listBlockTransaction.GetList = new Dictionary<string, ClassSyncCacheBlockTransactionObject>(_syncCacheDatabase[blockHeight]);
            }
            finally
            {
                if (semaphoreUsed)
                    _semaphoreDictionaryAccess.Release();
            }

            return listBlockTransaction;
        }

        /// <summary>
        /// Return a list of block transaction at a specific block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<List<string>> GetListBlockTransactionHashFromBlockHeight(long blockHeight, CancellationTokenSource cancellation)
        {
            List<string> listBlockTransactionHash = new List<string>();
            bool semaphoreUsed = false;

            try
            {
                await _semaphoreDictionaryAccess.WaitAsync(cancellation.Token);
                semaphoreUsed = true;

                if (_syncCacheDatabase.ContainsKey(blockHeight))
                    listBlockTransactionHash = _syncCacheDatabase[blockHeight].Keys.ToList();
            }
            finally
            {
                if (semaphoreUsed)
                    _semaphoreDictionaryAccess.Release();
            }

            return listBlockTransactionHash;
        }

        /// <summary>
        /// Return a block transaction cached at a specific block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="transactionHash"></param>
        /// <returns></returns>
        public ClassSyncCacheBlockTransactionObject GetSyncBlockTransactionCached(long blockHeight, string transactionHash)
        {
            if (_syncCacheDatabase.ContainsKey(blockHeight))
            {
                if (_syncCacheDatabase[blockHeight].ContainsKey(transactionHash))
                {
                    if (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionStatus && _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionTotalConfirmation > 0)
                        _syncCacheDatabase[blockHeight][transactionHash].IsMemPool = false;

                    return _syncCacheDatabase[blockHeight][transactionHash];
                }
            }
            return null;
        }

        /// <summary>
        /// Remove a synced block transaction cached.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="transactionHash"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task RemoveSyncedBlockTransactionCached(long blockHeight, string transactionHash, CancellationTokenSource cancellation)
        {
            bool semaphoreUsed = false;

            try
            {
                await _semaphoreDictionaryAccess.WaitAsync(cancellation.Token);
                semaphoreUsed = true;
                if (_syncCacheDatabase.ContainsKey(blockHeight))
                {
                    if (_syncCacheDatabase[blockHeight].ContainsKey(transactionHash))
                        _syncCacheDatabase[blockHeight].Remove(transactionHash);

                    if (_syncCacheDatabase[blockHeight].Count == 0)
                        _syncCacheDatabase.Remove(blockHeight);
                }
            }
            finally
            {
                if (semaphoreUsed)
                    _semaphoreDictionaryAccess.Release();
            }
        }


        public async Task<DisposableDictionary<long, Dictionary<string, ClassSyncCacheBlockTransactionObject>>> GetAllBlockTransactionCached(CancellationTokenSource cancellation)
        {
            DisposableDictionary<long, Dictionary<string, ClassSyncCacheBlockTransactionObject>> listBlockTransactionSynced = new DisposableDictionary<long, Dictionary<string, ClassSyncCacheBlockTransactionObject>>();
            bool semaphoreUsed = false;

            try
            {
                await _semaphoreDictionaryAccess.WaitAsync(cancellation.Token);
                semaphoreUsed = true;

                using (var listBlockHeight = BlockHeightKeys)
                {
                    foreach (long blockHeight in listBlockHeight.GetList)
                    {
                        if (cancellation.IsCancellationRequested)
                            break;

                        listBlockTransactionSynced.Add(blockHeight, new Dictionary<string, ClassSyncCacheBlockTransactionObject>());
                        listBlockTransactionSynced[blockHeight] = _syncCacheDatabase[blockHeight].ToDictionary(x => x.Key, x => x.Value);

                    }
                }
            }
            finally
            {
                if (semaphoreUsed)
                    _semaphoreDictionaryAccess.Release();
            }

            return listBlockTransactionSynced;
        }

        /// <summary>
        /// Update wallet balances from sync cached data.
        /// </summary>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task UpdateWalletBalance(CancellationTokenSource cancellation)
        {
            bool semaphoreUsed = false;
            try
            {

                await _semaphoreDictionaryAccess.WaitAsync(cancellation.Token);
                semaphoreUsed = true;

                BigInteger availableBalance = 0;
                BigInteger pendingBalance = 0;
                BigInteger totalBalance = 0;

                foreach(long blockHeight in _syncCacheDatabase.Keys.ToArray())
                {
                    if (cancellation.IsCancellationRequested)
                        break;

                    foreach (string transactionHash in _syncCacheDatabase[blockHeight].Keys.ToArray())
                    {
                        if (cancellation.IsCancellationRequested)
                            break;

                        if (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionStatus)
                        {
                            if (!_syncCacheDatabase[blockHeight][transactionHash].IsMemPool)
                            {
                                if (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionTotalConfirmation
                                    >= (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionBlockHeightTarget - _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionBlockHeightInsert))
                                {
                                    if (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.WalletAddressReceiver == _syncCacheDatabase[blockHeight][transactionHash].WalletAddressOwner)
                                    {
                                        availableBalance += _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount;
                                        totalBalance += _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount;
                                    }
                                    else
                                    {
                                        availableBalance -= (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount + _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Fee);
                                        totalBalance -= (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount + _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Fee);
                                    }
                                }
                                else
                                {
                                    if (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.WalletAddressReceiver == _syncCacheDatabase[blockHeight][transactionHash].WalletAddressOwner)
                                    {
                                        pendingBalance += _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount;
                                        totalBalance += _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount;
                                    }
                                    else
                                    {
                                        pendingBalance -= (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount + _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Fee);
                                        totalBalance -= (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount + _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Fee);
                                    }
                                }
                            }
                            else
                            {
                                if (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.WalletAddressSender == _syncCacheDatabase[blockHeight][transactionHash].WalletAddressOwner)
                                {
                                    availableBalance -= (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount + _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Fee);
                                    totalBalance -= (_syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount + _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Fee);
                                }
                                else
                                {
                                    pendingBalance += _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount;
                                    totalBalance += _syncCacheDatabase[blockHeight][transactionHash].BlockTransaction.TransactionObject.Amount;
                                }

                            }
                        }
                    }
                }

                AvailableBalance = availableBalance;
                PendingBalance = pendingBalance;
                TotalBalance = totalBalance;
            }
            finally
            {
                if (semaphoreUsed)
                    _semaphoreDictionaryAccess.Release();
            }
        }
    }
}

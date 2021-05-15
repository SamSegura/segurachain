using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using SeguraChain_Lib.Utility;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;

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

        /// <summary>
        /// Get the total amount of transactions cached.
        /// </summary>
        public long TotalTransactions
        {
            get
            {
                long totalTransactions = 0;

                if (_syncCacheDatabase.Count > 0)
                {
                    foreach (long blockHeight in _syncCacheDatabase.Keys)
                    {
                        totalTransactions += _syncCacheDatabase[blockHeight].Count;
                    }
                }

                return totalTransactions;
            }
        }

        /// <summary>
        /// Constructor.
        /// </summary>
        public ClassSyncCacheObject()
        {
            _syncCacheDatabase = new Dictionary<long, Dictionary<string, ClassSyncCacheBlockTransactionObject>>();
            _semaphoreDictionaryAccess = new SemaphoreSlim(1, ClassUtility.GetMaxAvailableProcessorCount());
        }

        /// <summary>
        /// Get the amount of block height.
        /// </summary>
        public int CountBlockHeight
        {
            get
            {
                return _syncCacheDatabase.Count;
            }
        }

        /// <summary>
        /// Get the list of block heights.
        /// </summary>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public List<long> BlockHeightKeys(CancellationTokenSource cancellation)
        {
            List<long> listBlockHeight = new List<long>();
            bool semaphoreUsed = false;

            try
            {
                _semaphoreDictionaryAccess.Wait(cancellation.Token);
                semaphoreUsed = true;

                listBlockHeight = _syncCacheDatabase.Keys.ToList();
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreDictionaryAccess.Release();
                }
            }

            return listBlockHeight;
        }

        /// <summary>
        /// Check if a block height is stored.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public bool ContainsBlockHeight(long blockHeight, CancellationTokenSource cancellation)
        {
            bool result = false;
            bool semaphoreUsed = false;
            try
            {

                _semaphoreDictionaryAccess.Wait(cancellation.Token);
                semaphoreUsed = true;

                result = _syncCacheDatabase.ContainsKey(blockHeight);

            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreDictionaryAccess.Release();
                }
            }
            return result;
        }

        /// <summary>
        /// Insert a block height to the cache.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public bool InsertBlockHeight(long blockHeight, CancellationTokenSource cancellation)
        {
            bool result = false;
            bool semaphoreUsed = false;
            try
            {
                _semaphoreDictionaryAccess.Wait(cancellation.Token);
                semaphoreUsed = true;

                if (!_syncCacheDatabase.ContainsKey(blockHeight))
                {
                    _syncCacheDatabase.Add(blockHeight, new Dictionary<string, ClassSyncCacheBlockTransactionObject>());
                    result = true;
                }
                else
                {
                    result = true;
                }
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreDictionaryAccess.Release();
                }
            }
            return result;
        }

        /// <summary>
        /// Count the amount of block transaction stored at a specific block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public int CountBlockTransactionFromBlockHeight(long blockHeight, CancellationTokenSource cancellation)
        {
            int count = 0;

            bool semaphoreUsed = false;
            try
            {
                _semaphoreDictionaryAccess.Wait(cancellation.Token);
                semaphoreUsed = true;

                if (_syncCacheDatabase.ContainsKey(blockHeight))
                {
                    count = _syncCacheDatabase[blockHeight].Count;
                }

            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreDictionaryAccess.Release();
                }
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
        public bool ContainsBlockTransactionFromTransactionHashAndBlockHeight(long blockHeight, string transactionHash, CancellationTokenSource cancellation)
        {
            bool result = false;
            bool semaphoreUsed = false;
            try
            {
                _semaphoreDictionaryAccess.Wait(cancellation.Token);
                semaphoreUsed = true;

                if (_syncCacheDatabase.ContainsKey(blockHeight))
                {
                    result = _syncCacheDatabase[blockHeight].ContainsKey(transactionHash);
                }
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreDictionaryAccess.Release();
                }
            }

            return result;
        }

        /// <summary>
        /// Insert a block transaction to the cache.
        /// </summary>
        /// <param name="syncCacheBlockTransactionObject"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public bool InsertBlockTransaction(ClassSyncCacheBlockTransactionObject syncCacheBlockTransactionObject, CancellationTokenSource cancellation)
        {
            bool result = false;
            bool semaphoreUsed = false;
            try
            {
                _semaphoreDictionaryAccess.Wait(cancellation.Token);
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
                {
                    _semaphoreDictionaryAccess.Release();
                }
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
        public void Clear(CancellationTokenSource cancellation)
        {
            bool semaphoreUsed = false;
            try
            {
                _semaphoreDictionaryAccess.Wait(cancellation.Token);
                semaphoreUsed = true;

                _syncCacheDatabase.Clear();
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreDictionaryAccess.Release();
                }
            }
        }

        /// <summary>
        /// Return every block transactions stored at a specific block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public IEnumerable<KeyValuePair<string, ClassSyncCacheBlockTransactionObject>> GetBlockTransactionFromBlockHeight(long blockHeight, CancellationTokenSource cancellation)
        {
            bool semaphoreUsed = false;

            try
            {
                _semaphoreDictionaryAccess.Wait(cancellation.Token);
                semaphoreUsed = true;

                if (_syncCacheDatabase.ContainsKey(blockHeight))
                {
                    foreach (var syncCacheBlockTransactionPair in _syncCacheDatabase[blockHeight])
                    {
                        yield return syncCacheBlockTransactionPair;
                    }
                }
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreDictionaryAccess.Release();
                }
            }
        }

        /// <summary>
        /// Return a list of block transaction at a specific block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public List<string> GetListBlockTransactionHashFromBlockHeight(long blockHeight, CancellationTokenSource cancellation)
        {
            List<string> listBlockTransactionHash = new List<string>();
            bool semaphoreUsed = false;

            try
            {
                _semaphoreDictionaryAccess.Wait(cancellation.Token);
                semaphoreUsed = true;

                if (_syncCacheDatabase.ContainsKey(blockHeight))
                {
                    listBlockTransactionHash = _syncCacheDatabase[blockHeight].Keys.ToList();
                }
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreDictionaryAccess.Release();
                }
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
                    return _syncCacheDatabase[blockHeight][transactionHash];
                }
            }
            return null;
        }

        public void RemoveSyncBlockTransactionCached(long blockHeight, string transactionHash)
        {
            if (_syncCacheDatabase.ContainsKey(blockHeight))
                if (_syncCacheDatabase[blockHeight].ContainsKey(transactionHash))
                    _syncCacheDatabase[blockHeight].Remove(transactionHash);
        }
    }
}

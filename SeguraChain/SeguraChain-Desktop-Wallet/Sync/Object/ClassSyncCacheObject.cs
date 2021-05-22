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
                    foreach (long blockHeight in _syncCacheDatabase.Keys)
                    {
                        lock (_syncCacheDatabase[blockHeight])
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
        public List<long> BlockHeightKeys()
        {
            return _syncCacheDatabase.Keys.ToList();
        }

        /// <summary>
        /// Check if a block height is stored.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// 
        /// <returns></returns>
        public bool ContainsBlockHeight(long blockHeight)
        {
            return _syncCacheDatabase.ContainsKey(blockHeight);
        }

        /// <summary>
        /// Insert a block height to the cache.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// 
        /// <returns></returns>
        public bool InsertBlockHeight(long blockHeight)
        {
            if (!_syncCacheDatabase.ContainsKey(blockHeight))
            {
                _syncCacheDatabase.Add(blockHeight, new Dictionary<string, ClassSyncCacheBlockTransactionObject>());
                return true;
            }
            else
                return true;
        }

        /// <summary>
        /// Count the amount of block transaction stored at a specific block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// 
        /// <returns></returns>
        public int CountBlockTransactionFromBlockHeight(long blockHeight)
        {
            if (_syncCacheDatabase.ContainsKey(blockHeight))
                return _syncCacheDatabase[blockHeight].Count;

            return 0;
        }

        /// <summary>
        /// Check if a block transaction is stored at a specific block height with a transaction hash provided.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="transactionHash"></param>
        /// 
        /// <returns></returns>
        public bool ContainsBlockTransactionFromTransactionHashAndBlockHeight(long blockHeight, string transactionHash)
        {
            if (_syncCacheDatabase.ContainsKey(blockHeight))
                return _syncCacheDatabase[blockHeight].ContainsKey(transactionHash);

            return false;
        }

        /// <summary>
        /// Insert a block transaction to the cache.
        /// </summary>
        /// <param name="syncCacheBlockTransactionObject"></param>
        /// 
        /// <returns></returns>
        public bool InsertBlockTransaction(ClassSyncCacheBlockTransactionObject syncCacheBlockTransactionObject)
        {
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
                    return true;
                }
                else
                {
                    _syncCacheDatabase[syncCacheBlockTransactionObject.BlockTransaction.TransactionObject.BlockHeightTransaction][syncCacheBlockTransactionObject.BlockTransaction.TransactionObject.TransactionHash] = syncCacheBlockTransactionObject;
                    return true;
                }
            }
            return false;
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
        public void Clear() => _syncCacheDatabase.Clear();

        /// <summary>
        /// Return every block transactions stored at a specific block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// 
        /// <returns></returns>
        public IEnumerable<KeyValuePair<string, ClassSyncCacheBlockTransactionObject>> GetBlockTransactionFromBlockHeight(long blockHeight)
        {
            if (_syncCacheDatabase.ContainsKey(blockHeight))
            {
                lock (_syncCacheDatabase[blockHeight])
                {
                    foreach (var syncCacheBlockTransactionPair in _syncCacheDatabase[blockHeight])
                        yield return syncCacheBlockTransactionPair;
                }
            }
        }

        /// <summary>
        /// Return a list of block transaction at a specific block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// 
        /// <returns></returns>
        public List<string> GetListBlockTransactionHashFromBlockHeight(long blockHeight)
        {
            if (_syncCacheDatabase.ContainsKey(blockHeight))
                return _syncCacheDatabase[blockHeight].Keys.ToList();

            return new List<string>();
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
                if (_syncCacheDatabase[blockHeight].ContainsKey(transactionHash))
                    return _syncCacheDatabase[blockHeight][transactionHash];
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
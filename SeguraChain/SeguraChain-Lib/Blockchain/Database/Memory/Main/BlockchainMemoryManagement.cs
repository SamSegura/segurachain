using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SeguraChain_Lib.Blockchain.Block.Enum;
using SeguraChain_Lib.Blockchain.Block.Function;
using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using SeguraChain_Lib.Blockchain.Block.Object.Task.TransactionConfirmation;
using SeguraChain_Lib.Blockchain.Checkpoint.Enum;
using SeguraChain_Lib.Blockchain.Database.DatabaseSetting;
using SeguraChain_Lib.Blockchain.Database.Memory.Cache.Object.Systems.IO.Disk.Object;
using SeguraChain_Lib.Blockchain.Database.Memory.Cache.Object.Systems.IO.Main;
using SeguraChain_Lib.Blockchain.Database.Memory.Main.Enum;
using SeguraChain_Lib.Blockchain.Database.Memory.Main.Object;
using SeguraChain_Lib.Blockchain.MemPool.Database;
using SeguraChain_Lib.Blockchain.Mining.Enum;
using SeguraChain_Lib.Blockchain.Mining.Function;
using SeguraChain_Lib.Blockchain.Mining.Object;
using SeguraChain_Lib.Blockchain.Setting;
using SeguraChain_Lib.Blockchain.Stats.Object;
using SeguraChain_Lib.Blockchain.Transaction.Enum;
using SeguraChain_Lib.Blockchain.Transaction.Object;
using SeguraChain_Lib.Blockchain.Transaction.Utility;
using SeguraChain_Lib.Blockchain.Wallet.Function;
using SeguraChain_Lib.Blockchain.Wallet.Object.Blockchain;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Other.Object.List;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Blockchain.Database.Memory.Main
{
    /// <summary>
    /// Persistant Dictionary class, using IO Cache disk in attempt to reduce memory usage (RAM)
    /// and also to save data updated, and recover them if a crash happen.
    /// </summary>
    public class BlockchainMemoryManagement
    {
        /// <summary>
        /// Block transaction cache system in front of IO cache files/network.
        /// </summary>
        private HashSet<string> _listWalletAddressReservedForBlockTransactionCache;
        private long _totalBlockTransactionCacheCount;
        private long _totalBlockTransactionMemorySize;

        /// <summary>
        /// IO Cache disk system object.
        /// </summary>
        private ClassCacheIoSystem _cacheIoSystem;

        /// <summary>
        /// Database.
        /// </summary>
        private Dictionary<long, BlockchainMemoryObject> _dictionaryBlockObjectMemory;
        public BlockchainWalletIndexMemoryManagement BlockchainWalletIndexMemoryCacheObject; // Contains block height/transactions linked to a wallet address, usually used for provide an accurate sync of data.

        /// <summary>
        /// Management of multithreading access.
        /// </summary>
        private SemaphoreSlim _semaphoreSlimUpdateTransactionConfirmations;
        private SemaphoreSlim _semaphoreSlimMemoryAccess;
        private SemaphoreSlim _semaphoreSlimGetWalletBalance;
        private SemaphoreSlim _semaphoreSlimCacheBlockTransactionAccess;

        /// <summary>
        /// Management of memory.
        /// </summary>
        private CancellationTokenSource _cancellationTokenMemoryManagement;

        /// <summary>
        /// Cache status.
        /// </summary>
        private bool _cacheStatus;
        private bool _pauseMemoryManagement;

        /// <summary>
        /// Cache settings.
        /// </summary>
        private ClassBlockchainDatabaseSetting _blockchainDatabaseSetting;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="blockchainDatabaseSetting">Database setting.</param>
        public BlockchainMemoryManagement(ClassBlockchainDatabaseSetting blockchainDatabaseSetting)
        {
            _blockchainDatabaseSetting = blockchainDatabaseSetting;
            _cacheStatus = _blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase;

            // Cache status.
            _pauseMemoryManagement = false;

            _dictionaryBlockObjectMemory = new Dictionary<long, BlockchainMemoryObject>();
            _listWalletAddressReservedForBlockTransactionCache = new HashSet<string>();
            BlockchainWalletIndexMemoryCacheObject = new BlockchainWalletIndexMemoryManagement(blockchainDatabaseSetting);

            // Protect against multithreading access.
            _semaphoreSlimMemoryAccess = new SemaphoreSlim(1, 1);
            _semaphoreSlimGetWalletBalance = new SemaphoreSlim(1, 1);
            _semaphoreSlimUpdateTransactionConfirmations = new SemaphoreSlim(1, 1);
            _semaphoreSlimCacheBlockTransactionAccess = new SemaphoreSlim(1, ClassUtility.GetMaxAvailableProcessorCount());

            // Cancellation token of memory management.
            _cancellationTokenMemoryManagement = new CancellationTokenSource();
        }

        #region Manage cache functions.

        /// <summary>
        /// Load the blockchain cache.
        /// </summary>
        public async Task<bool> LoadBlockchainCache()
        {
            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {

                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            _cacheIoSystem = new ClassCacheIoSystem(_blockchainDatabaseSetting);

                            Tuple<bool, HashSet<long>> result = await _cacheIoSystem.InitializeCacheIoSystem();

                            if (result.Item2.Count > 0)
                            {
                                foreach (long blockHeight in result.Item2)
                                {
                                    _dictionaryBlockObjectMemory.Add(blockHeight, new BlockchainMemoryObject()
                                    {
                                        ObjectCacheType = CacheBlockMemoryEnumState.IN_PERSISTENT_CACHE,
                                        ObjectIndexed = true,
                                        CacheUpdated = true
                                    });
                                }
                            }
                        }
                        break;
                }
            }

            return true;
        }

        /// <summary>
        /// Close the cache.
        /// </summary>
        /// <returns></returns>
        public async Task CloseCache()
        {
            switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
            {
                case CacheEnumName.IO_CACHE:
                    await _cacheIoSystem.CleanCacheIoSystem();
                    break;
            }
        }

        /// <summary>
        /// Attempt to force to purge the cache.
        /// </summary>
        /// <returns></returns>
        public async Task ForcePurgeCache()
        {
            await ForcePurgeMemoryDataCache();
        }

        #endregion


        #region Original dictionary functions.

        /// <summary>
        /// Emulate dictionary key index. Permit to get/set a value.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public ClassBlockObject this[long blockHeight, CancellationTokenSource cancellation]
        {
            get
            {

                if (ContainsKey(blockHeight))
                {

                    if (!CheckIfBlockHeightOutOfActiveMemory(blockHeight))
                    {
                        if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                        {
                            AddOrUpdateBlockMirrorObject(_dictionaryBlockObjectMemory[blockHeight].Content);
                        }
                        return _dictionaryBlockObjectMemory[blockHeight].Content;

                    }

                    return GetObjectByKeyFromMemoryOrCacheAsync(blockHeight, cancellation).Result;

                }

                if (BlockHeightIsCached(blockHeight, cancellation).Result)
                {
                    return GetObjectByKeyFromMemoryOrCacheAsync(blockHeight, cancellation).Result;
                }
#if DEBUG
                Debug.WriteLine("Blockchain database - The block height: " + blockHeight + " is missing.");
#endif

                ClassLog.WriteLine("Blockchain database - The block height: " + blockHeight + " is missing.", ClassEnumLogLevelType.LOG_LEVEL_MEMORY_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);

                return null;
            }
            set
            {
                if (value != null)
                {

                    try
                    {


                        if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
                        {

                            if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                            {
                                _dictionaryBlockObjectMemory[blockHeight].Content = value;
                                _dictionaryBlockObjectMemory[blockHeight].CacheUpdated = false;
                                _dictionaryBlockObjectMemory[blockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;
                            }
                            else
                            {
                                bool updated = DirectUpdateFromSetter(value, cancellation);

                                if (!updated)
                                {
                                    _dictionaryBlockObjectMemory[blockHeight].Content = value;
                                    _dictionaryBlockObjectMemory[blockHeight].CacheUpdated = false;
                                    _dictionaryBlockObjectMemory[blockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;
                                }

                            }
                        }
                        else
                        {
                            _dictionaryBlockObjectMemory[blockHeight].Content = value;
                            _dictionaryBlockObjectMemory[blockHeight].CacheUpdated = false;
                            _dictionaryBlockObjectMemory[blockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;
                        }

                        if (value.BlockStatus != ClassBlockEnumStatus.LOCKED)
                        {
                            AddOrUpdateBlockMirrorObject(value);
                        }

                    }
                    catch
                    {
                        // Ignored.
                    }
                }
            }
        }

        /// <summary>
        /// Update data from setter directly.
        /// </summary>
        /// <param name="value"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private bool DirectUpdateFromSetter(ClassBlockObject value, CancellationTokenSource cancellation)
        {
            return InsertOrUpdateBlockObjectToCache(value, true, true, cancellation).Result;
        }

        /// <summary>
        /// Check if the key exist.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public bool ContainsKey(long key)
        {
            return _dictionaryBlockObjectMemory.ContainsKey(key);
        }

        /// <summary>
        /// Get the list of keys of elements stored.
        /// </summary>
        public List<long> ListBlockHeight => new List<long>(_dictionaryBlockObjectMemory.Keys.ToArray().OrderBy(x => x));

        /// <summary>
        /// Get the last key stored.
        /// </summary>
        public long GetLastBlockHeight => Count;

        /// <summary>
        /// Get the amount of elements.
        /// </summary>
        public int Count => _dictionaryBlockObjectMemory.Count;

        /// <summary>
        /// Insert an element to the active memory.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="value"></param>
        /// <param name="useSemaphore"></param>
        /// <param name="insertEnumTypeStatus"></param>
        /// <param name="insertPolicy"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> Add(long blockHeight, ClassBlockObject value, bool useSemaphore, CacheBlockMemoryInsertEnumType insertEnumTypeStatus, CacheBlockMemoryEnumInsertPolicy insertPolicy, CancellationTokenSource cancellation)
        {
            bool result = false;
            bool semaphoreUsed = false;
            try
            {

                if (useSemaphore)
                {
                    if (cancellation != null)
                    {
                        await _semaphoreSlimMemoryAccess.WaitAsync(cancellation.Token);
                    }
                    else
                    {
                        await _semaphoreSlimMemoryAccess.WaitAsync();
                    }
                    semaphoreUsed = true;
                }

                // Never put locked blocks into the cache. Keep always alive the genesis block height in the active memory, this one is always low.
                if (value.BlockStatus == ClassBlockEnumStatus.LOCKED || value.BlockHeight == BlockchainSetting.GenesisBlockHeight || !_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
                {
                    insertEnumTypeStatus = CacheBlockMemoryInsertEnumType.INSERT_IN_ACTIVE_MEMORY_OBJECT;
                }


                switch (insertEnumTypeStatus)
                {
                    case CacheBlockMemoryInsertEnumType.INSERT_IN_ACTIVE_MEMORY_OBJECT:
                        {
                            if (ContainsKey(blockHeight))
                            {
                                _dictionaryBlockObjectMemory[blockHeight].Content = value;
                                _dictionaryBlockObjectMemory[blockHeight].CacheUpdated = false;
                                _dictionaryBlockObjectMemory[blockHeight].Content.BlockLastChangeTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                                _dictionaryBlockObjectMemory[blockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;
                            }
                            else
                            {

                                while (!TryAdd(blockHeight, new BlockchainMemoryObject()
                                {
                                    Content = value,
                                    CacheUpdated = false,
                                    ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY
                                }))
                                {
                                    cancellation?.Token.ThrowIfCancellationRequested();

                                    if (ContainsKey(blockHeight))
                                    {
                                        _dictionaryBlockObjectMemory[blockHeight].Content = value;
                                        _dictionaryBlockObjectMemory[blockHeight].CacheUpdated = false;
                                        _dictionaryBlockObjectMemory[blockHeight].Content.BlockLastChangeTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                                        _dictionaryBlockObjectMemory[blockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;
                                        break;
                                    }
                                }
                            }
                            result = true;
                        }
                        break;
                    case CacheBlockMemoryInsertEnumType.INSERT_IN_PERSISTENT_CACHE_OBJECT:
                        {
                            if (_cacheStatus && _blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
                            {
                                result = await AddOrUpdateMemoryDataToCache(value, true, cancellation);
                            }

                            if (result)
                            {
                                if (!_dictionaryBlockObjectMemory.ContainsKey(blockHeight))
                                {
                                    while (!TryAdd(blockHeight, new BlockchainMemoryObject()
                                    {
                                        Content = null,
                                        CacheUpdated = true,
                                        ObjectIndexed = true,
                                        ObjectCacheType = CacheBlockMemoryEnumState.IN_PERSISTENT_CACHE
                                    }))
                                    {
                                        cancellation?.Token.ThrowIfCancellationRequested();

                                        if (_dictionaryBlockObjectMemory[blockHeight].Content == null)
                                        {
                                            _dictionaryBlockObjectMemory[blockHeight].ObjectIndexed = true;
                                            _dictionaryBlockObjectMemory[blockHeight].Content = null;
                                            _dictionaryBlockObjectMemory[blockHeight].CacheUpdated = true;
                                            _dictionaryBlockObjectMemory[blockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_PERSISTENT_CACHE;
                                        }
                                        break;

                                    }
                                }
                            }
                            else
                            {
                                while (!TryAdd(blockHeight, new BlockchainMemoryObject()
                                {
                                    Content = value,
                                    CacheUpdated = false,
                                    ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY
                                }))
                                {
                                    cancellation?.Token.ThrowIfCancellationRequested();

                                    if (ContainsKey(blockHeight))
                                    {
                                        _dictionaryBlockObjectMemory[blockHeight].Content = value;
                                        _dictionaryBlockObjectMemory[blockHeight].CacheUpdated = false;
                                        _dictionaryBlockObjectMemory[blockHeight].Content.BlockLastChangeTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                                        _dictionaryBlockObjectMemory[blockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;
                                        break;
                                    }
                                }
                                result = true;
                            }

                        }
                        break;
                }

                if (result)
                {
                    if (value.BlockStatus != ClassBlockEnumStatus.LOCKED)
                    {
                        AddOrUpdateBlockMirrorObject(value);
                    }
                }

            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreSlimMemoryAccess.Release();
                }
            }
            return result;
        }

        /// <summary>
        /// Attempt to remove an element of disk cache, and on memory.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="useSemaphore"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> Remove(long blockHeight, bool useSemaphore, CancellationTokenSource cancellation)
        {
            bool result = false;

            bool semaphoreUsed = false;
            try
            {
                if (useSemaphore)
                {
                    if (cancellation != null)
                    {
                        await _semaphoreSlimMemoryAccess.WaitAsync(cancellation.Token);

                    }
                    else
                    {
                        await _semaphoreSlimMemoryAccess.WaitAsync();
                    }
                    semaphoreUsed = true;
                }

                if (ContainsKey(blockHeight))
                {
                    // Remove from cache if indexed and if the cache is enabled.
                    if (_dictionaryBlockObjectMemory[blockHeight].ObjectIndexed && _blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
                    {
                        #region Remove from cache first, then from the active memory.

                        bool taskRemoveBlockObjectFromCache = await RemoveMemoryDataOfCache(blockHeight, cancellation);

                        if (taskRemoveBlockObjectFromCache)
                        {
                            if (_dictionaryBlockObjectMemory.ContainsKey(blockHeight))
                            {
                                if (_dictionaryBlockObjectMemory.Remove(blockHeight))
                                {
                                    result = true;
                                }
                            }
                        }

                        #endregion
                    }
                    else
                    {
                        if (_dictionaryBlockObjectMemory.ContainsKey(blockHeight))
                        {
                            if (_dictionaryBlockObjectMemory.Remove(blockHeight))
                            {
                                if (ContainBlockHeightMirror(blockHeight))
                                {
                                    result = true;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreSlimMemoryAccess.Release();
                }
            }

            return result;
        }

        /// <summary>
        /// Try to add an element.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="blockchainMemoryObject"></param>
        /// <returns></returns>
        private bool TryAdd(long blockHeight, BlockchainMemoryObject blockchainMemoryObject)
        {
            try
            {
                if (!ContainsKey(blockHeight))
                {
                    _dictionaryBlockObjectMemory.Add(blockHeight, blockchainMemoryObject);

                    return true;
                }
                else
                {
                    return true;
                }
            }
            catch
            {
                // Ignored.
            }

            return false;
        }

        /// <summary>
        /// Check if the block height data is cached and removed from the active memory.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <returns></returns>
        public bool CheckIfBlockHeightOutOfActiveMemory(long blockHeight)
        {
            if (_dictionaryBlockObjectMemory[blockHeight].Content == null)
            {
                return true;
            }
            return false;
        }

        #endregion


        #region Blockchain functions.

        #region Specific Insert/Update blocks data with the cache system.

        /// <summary>
        /// Insert or update a block object in the cache.
        /// </summary>
        /// <param name="blockObject"></param>
        /// <param name="containKey"></param>
        /// <param name="keepAlive"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> InsertOrUpdateBlockObjectToCache(ClassBlockObject blockObject, bool containKey, bool keepAlive, CancellationTokenSource cancellation)
        {
            bool result = false;

            if (ContainsKey(blockObject.BlockHeight))
            {

                blockObject.BlockLastChangeTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
                {

                    // Update the active memory if the content of the block height target if this one is not empty.
                    if (_dictionaryBlockObjectMemory[blockObject.BlockHeight].Content != null)
                    {
                        _dictionaryBlockObjectMemory[blockObject.BlockHeight].Content = blockObject;
                        _dictionaryBlockObjectMemory[blockObject.BlockHeight].CacheUpdated = false;
                        _dictionaryBlockObjectMemory[blockObject.BlockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;

                        if (_dictionaryBlockObjectMemory[blockObject.BlockHeight].ObjectIndexed)
                        {
                            await AddOrUpdateMemoryDataToCache(blockObject, keepAlive, cancellation);
                        }
                        result = true;
                    }
                    else
                    {
                        // Try to update or add the block data updated to the cache.
                        if (await AddOrUpdateMemoryDataToCache(blockObject, keepAlive, cancellation))
                        {
                            result = true;
                        }
                    }
                }
                else
                {
                    _dictionaryBlockObjectMemory[blockObject.BlockHeight].Content = blockObject;
                    result = true;
                }

                if (result)
                {
                    if (blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                    {
                        AddOrUpdateBlockMirrorObject(blockObject);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Insert a block transaction in the cache.
        /// </summary>
        /// <param name="blockTransaction"></param>
        /// <param name="blockHeight"></param>
        /// <param name="keepAlive"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> InsertBlockTransactionToCache(ClassBlockTransaction blockTransaction, long blockHeight, bool keepAlive, CancellationTokenSource cancellation)
        {
            return await InsertBlockTransactionToMemoryDataCache(blockTransaction, blockHeight, keepAlive, cancellation);
        }

        #endregion

        #region Specific research of data on blocks managed with the cache system.

        /// <summary>
        /// Get a block transaction count by cache strategy.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="useSemaphore">Lock or not the access of the cache, determine also if the access require a simple get of the data cache or not.</param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<int> GetBlockTransactionCountStrategy(long blockHeight, CancellationTokenSource cancellation)
        {
            int transactionCount = 0;
            if (blockHeight >= BlockchainSetting.GenesisBlockHeight)
            {
                if (ContainsKey(blockHeight))
                {
                    if (_dictionaryBlockObjectMemory[blockHeight].Content == null)
                    {

                        if (!GetBlockMirrorObject(blockHeight, out ClassBlockObject blockObject))
                        {
                            transactionCount = await GetBlockTransactionCountMemoryDataFromCacheByKey(blockHeight, cancellation);

                            if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                            {
                                transactionCount = _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions.Count;
                            }
                        }
                        else
                        {
                            transactionCount = blockObject.TotalTransaction;
                            if (blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                            {
                                if (transactionCount == 0)
                                {
                                    transactionCount = await GetBlockTransactionCountMemoryDataFromCacheByKey(blockHeight, cancellation);
                                }
                            }
                        }


                    }
                    else
                    {
                        return _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions.Count;
                    }
                }
            }
            return transactionCount;
        }

        /// <summary>
        /// Get a block information data by cache strategy.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<ClassBlockObject> GetBlockInformationDataStrategy(long blockHeight, CancellationTokenSource cancellation)
        {
            if (blockHeight >= BlockchainSetting.GenesisBlockHeight)
            {
                bool retrieved = false;

                if (GetBlockMirrorObject(blockHeight, out ClassBlockObject blockObject))
                {
                    if (blockObject != null)
                    {
                        retrieved = true;
                    }
                }

                if (!retrieved)
                {
                    if (ContainsKey(blockHeight))
                    {
                        if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                        {
                            blockObject = _dictionaryBlockObjectMemory[blockHeight].Content;
                            AddOrUpdateBlockMirrorObject(blockObject);
                            retrieved = true;
                        }
                    }
                }

                if (!retrieved)
                {
                    blockObject = await GetBlockInformationMemoryDataFromCacheByKey(blockHeight, cancellation);
                }

                return blockObject;

            }
            return null;
        }

        /// <summary>
        /// Get a list of block informations data by cache strategy.
        /// </summary>
        /// <param name="listBlockHeight"></param>
        /// <param name="useSemaphore"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<List<ClassBlockObject>> GetListBlockInformationDataFromListBlockHeightStrategy(HashSet<long> listBlockHeight, CancellationTokenSource cancellation)
        {
            List<ClassBlockObject> listBlockInformationData = new List<ClassBlockObject>();

            HashSet<long> listBlockHeightNotFound = new HashSet<long>();

            if (listBlockHeight.Count > 0)
            {
                foreach (long blockHeight in listBlockHeight)
                {
                    bool found = false;
                    if (ContainsKey(blockHeight))
                    {
                        if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                        {
                            _dictionaryBlockObjectMemory[blockHeight].Content.DeepCloneBlockObject(false, out ClassBlockObject blockObject);
                            if (blockObject != null)
                            {
                                listBlockInformationData.Add(blockObject);
                                found = true;
                            }
                        }
                        if (!found)
                        {
                            if (GetBlockMirrorObject(blockHeight, out ClassBlockObject blockObject))
                            {
                                if (blockObject != null)
                                {
                                    listBlockInformationData.Add(blockObject);
                                    found = true;
                                }
                            }
                        }

                        if (!found)
                        {
                            listBlockHeightNotFound.Add(blockHeight);
                        }
                    }
                }

                if (listBlockHeightNotFound.Count > 0)
                {
                    foreach (var blockObjectPair in await GetBlockInformationListByBlockHeightListTargetFromMemoryDataCache(listBlockHeightNotFound, cancellation))
                    {
                        if (blockObjectPair.Value != null)
                        {
                            listBlockInformationData.Add(blockObjectPair.Value);
                        }
                    }
                }

                // Clean up.
                listBlockHeightNotFound.Clear();

                // Sorting block object by height before to return them.
                return listBlockInformationData.OrderBy(x => x.BlockHeight).ToList();
            }


            return listBlockInformationData;
        }

        /// <summary>
        /// Get a block data by cache strategy.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="keepAlive"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<ClassBlockObject> GetBlockDataStrategy(long blockHeight, bool keepAlive, CancellationTokenSource cancellation)
        {
            if (blockHeight >= BlockchainSetting.GenesisBlockHeight)
            {
                if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                {
                    return _dictionaryBlockObjectMemory[blockHeight].Content;
                }
                else
                {
                    ClassBlockObject blockObject = await GetBlockMemoryDataFromCacheByKey(blockHeight, keepAlive, cancellation);

                    if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                    {
                        return _dictionaryBlockObjectMemory[blockHeight].Content;
                    }

                    return blockObject;
                }
            }
            return null;
        }

        /// <summary>
        /// Check if the block height target is cached.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> BlockHeightIsCached(long blockHeight, CancellationTokenSource cancellation)
        {
            if (ContainsKey(blockHeight))
            {
                if (_dictionaryBlockObjectMemory[blockHeight].Content == null)
                {
                    return true;
                }
                return false;
            }

            if (await CheckBlockHeightExistOnMemoryDataCache(blockHeight, cancellation))
            {
                // Insert the block height.
                _dictionaryBlockObjectMemory.Add(blockHeight, new BlockchainMemoryObject()
                {
                    Content = null,
                    ObjectIndexed = true,
                    CacheUpdated = true,
                    ObjectCacheType = CacheBlockMemoryEnumState.IN_PERSISTENT_CACHE
                });
                return true;
            }
            return false;
        }

        /// <summary>
        /// Get the amount of blocks locked.
        /// </summary>
        /// <returns></returns>
        public long GetCountBlockLocked()
        {
            long totalBlockLocked = 0;
            if (Count > 0)
            {
                long lastBlockHeight = GetLastBlockHeight;

                long startHeight = lastBlockHeight;
                while (startHeight >= BlockchainSetting.GenesisBlockHeight)
                {
                    if (_dictionaryBlockObjectMemory[startHeight].Content != null)
                    {
                        if (_dictionaryBlockObjectMemory[startHeight].Content.BlockStatus == ClassBlockEnumStatus.LOCKED)
                        {
                            totalBlockLocked++;
                        }
                        else
                        {
                            if (startHeight < lastBlockHeight)
                            {
                                break;
                            }
                        }
                    }
                    startHeight--;
                    if (startHeight < BlockchainSetting.GenesisBlockHeight)
                    {
                        break;
                    }
                }
            }

            return totalBlockLocked;
        }

        /// <summary>
        /// Get the listing of block unconfirmed totally with the network.
        /// </summary>
        /// <param name="blockNetworkConfirmations"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<DisposableList<long>> GetListBlockNetworkUnconfirmed(int blockNetworkConfirmations, CancellationTokenSource cancellation)
        {
            DisposableList<long> blockNetworkUnconfirmedList = new DisposableList<long>();

            HashSet<long> listBlockHeight = new HashSet<long>();

            long lastBlockHeight = GetLastBlockHeight;

            for (long i = 0; i < lastBlockHeight; i++)
            {
                long blockHeight = i + 1;

                bool found = false;

                if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                {
                    if (!_dictionaryBlockObjectMemory[blockHeight].Content.BlockUnlockValid || _dictionaryBlockObjectMemory[blockHeight].Content.BlockNetworkAmountConfirmations < blockNetworkConfirmations)
                    {
                        blockNetworkUnconfirmedList.Add(blockHeight);
                    }
                    found = true;
                }
                if (!found)
                {
                    if (GetBlockMirrorObject(blockHeight, out ClassBlockObject blockObject))
                    {
                        if (blockObject != null)
                        {
                            if (!blockObject.BlockUnlockValid || blockObject.BlockNetworkAmountConfirmations < blockNetworkConfirmations)
                            {
                                blockNetworkUnconfirmedList.Add(blockHeight);
                            }

                            found = true;
                        }
                    }
                }
                if (!found)
                {
                    listBlockHeight.Add(blockHeight);
                }
            }

            if (listBlockHeight.Count > 0)
            {
                foreach (var blockObjectPair in await GetBlockInformationListByBlockHeightListTargetFromMemoryDataCache(listBlockHeight, cancellation))
                {
                    if (blockObjectPair.Value != null)
                    {
                        if (blockObjectPair.Value.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                        {
                            if (!blockObjectPair.Value.BlockUnlockValid || blockObjectPair.Value.BlockNetworkAmountConfirmations < blockNetworkConfirmations)
                            {
                                blockNetworkUnconfirmedList.Add(blockObjectPair.Value.BlockHeight);
                            }
                        }
                    }
                }
            }


            blockNetworkUnconfirmedList.Sort();

            return blockNetworkUnconfirmedList;
        }

        #endregion

        #region Specific getting of data managed with the cache system.

        /// <summary>
        /// Get the much closer block height from a timestamp provided.
        /// </summary>
        /// <param name="timestamp"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public long GetCloserBlockHeightFromTimestamp(long timestamp, CancellationTokenSource cancellation)
        {
            long closerBlockHeight = 0;

            if (Count > 0)
            {
                long lastBlockHeight = GetLastBlockHeight;

                while (lastBlockHeight > 0)
                {
                    if (cancellation != null)
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    long blockHeight = lastBlockHeight;

                    bool found = false;

                    if (GetBlockMirrorObject(blockHeight, out ClassBlockObject blockObject))
                    {
                        if (blockObject != null)
                        {
                            found = true;

                            if (timestamp == blockObject.TimestampCreate)
                            {
                                closerBlockHeight = blockHeight;
                                break;
                            }

                        }
                    }
                    if (!found)
                    {
                        if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                        {

                            if (timestamp == _dictionaryBlockObjectMemory[blockHeight].Content.TimestampCreate)
                            {
                                closerBlockHeight = blockHeight;
                                break;
                            }

                        }
                    }

                    lastBlockHeight--;

                    if (lastBlockHeight < BlockchainSetting.GenesisBlockHeight)
                    {
                        break;
                    }
                }
            }

            return closerBlockHeight;
        }

        /// <summary>
        /// Get the last block height unlocked.
        /// </summary>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public long GetLastBlockHeightUnlocked(CancellationTokenSource cancellation)
        {
            long lastBlockHeightUnlocked = 0;

            if (Count > 0)
            {
                long lastBlockHeight = GetLastBlockHeight;

                while (lastBlockHeight > 0)
                {
                    if (cancellation != null)
                    {
                        if (cancellation.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    long blockHeight = lastBlockHeight;

                    if (GetBlockMirrorObject(blockHeight, out ClassBlockObject blockObject))
                    {
                        if (blockObject != null)
                        {
                            if (blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                            {
                                lastBlockHeightUnlocked = blockObject.BlockHeight;
                                break;
                            }
                        }
                    }
                    else if (ContainsKey(lastBlockHeight))
                    {
                        if (_dictionaryBlockObjectMemory[lastBlockHeight].Content != null)
                        {
                            if (_dictionaryBlockObjectMemory[lastBlockHeight].Content.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                            {
                                lastBlockHeightUnlocked = lastBlockHeight;
                                break;
                            }
                        }
                    }

                    lastBlockHeight--;

                    if (lastBlockHeight < BlockchainSetting.GenesisBlockHeight)
                    {
                        break;
                    }
                }
            }

            return lastBlockHeightUnlocked;
        }

        /// <summary>
        /// Get the last block height confirmed with the network.
        /// </summary>
        /// <param name="blockNetworkConfirmations"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<long> GetLastBlockHeightConfirmationNetworkChecked(int blockNetworkConfirmations, CancellationTokenSource cancellation)
        {
            long lastBlockHeightUnlockedChecked = 0;

            if (Count > 0)
            {
                #region If the block height is not provided or if the block height provided is not the correct one.

                HashSet<long> listBlockHeight = new HashSet<long>();

                long lastBlockHeight = GetLastBlockHeight;

                for (long i = 0; i < lastBlockHeight; i++)
                {
                    long blockHeight = i + 1;

                    bool found = false;
                    bool locked = false;
                    if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                    {
                        if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockStatus == ClassBlockEnumStatus.LOCKED)
                        {
                            locked = true;
                        }
                        if (!locked)
                        {
                            if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockStatus == ClassBlockEnumStatus.UNLOCKED &&
                                _dictionaryBlockObjectMemory[blockHeight].Content.BlockUnlockValid &&
                                _dictionaryBlockObjectMemory[blockHeight].Content.BlockNetworkAmountConfirmations >= blockNetworkConfirmations)
                            {
                                lastBlockHeightUnlockedChecked = _dictionaryBlockObjectMemory[blockHeight].Content.BlockHeight;
                            }
                            else
                            {
                                break;
                            }
                            found = true;
                        }
                    }
                    else
                    {
                        if (GetBlockMirrorObject(blockHeight, out ClassBlockObject blockObject))
                        {
                            if (blockObject != null)
                            {
                                if (blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED &&
                                    blockObject.BlockUnlockValid &&
                                    blockObject.BlockNetworkAmountConfirmations >= blockNetworkConfirmations)
                                {
                                    lastBlockHeightUnlockedChecked = blockObject.BlockHeight;
                                }
                                else
                                {
                                    break;
                                }
                                found = true;
                            }
                        }
                    }
                    if (!found && !locked)
                    {
                        listBlockHeight.Add(blockHeight);
                    }
                }

                if (listBlockHeight.Count > 0)
                {
                    foreach (var blockObjectPair in await GetBlockInformationListByBlockHeightListTargetFromMemoryDataCache(listBlockHeight, cancellation))
                    {
                        if (blockObjectPair.Value != null)
                        {
                            if (blockObjectPair.Value.BlockStatus == ClassBlockEnumStatus.UNLOCKED && blockObjectPair.Value.BlockUnlockValid && blockObjectPair.Value.BlockNetworkAmountConfirmations >= blockNetworkConfirmations)
                            {
                                lastBlockHeightUnlockedChecked = blockObjectPair.Value.BlockHeight;
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }


                // Clean up.
                listBlockHeight.Clear();

                #endregion
            }

            return lastBlockHeightUnlockedChecked;
        }

        /// <summary>
        /// Get the last block height transaction confirmation done.
        /// </summary>
        /// <param name="useSemaphore">Lock or not the access of the cache, determine also if the access require a simple get of the data cache or not.</param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<long> GetLastBlockHeightTransactionConfirmationDone(CancellationTokenSource cancellation)
        {
            long lastBlockHeightTransactionConfirmationDone = 0;

            if (Count > 0)
            {
                #region If the block height is not provided or if the block height provided is not the correct one.


                HashSet<long> listBlockHeight = new HashSet<long>();

                long lastBlockHeight = GetLastBlockHeight;

                for (long i = 0; i < lastBlockHeight; i++)
                {
                    long blockHeight = i + 1;
                    bool found = false;

                    if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                    {

                        if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                        {
                            found = true;

                            if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockLastHeightTransactionConfirmationDone > 0 &&
                                _dictionaryBlockObjectMemory[blockHeight].Content.BlockUnlockValid &&
                                _dictionaryBlockObjectMemory[blockHeight].Content.BlockNetworkAmountConfirmations >= BlockchainSetting.BlockAmountNetworkConfirmations)
                            {
                                lastBlockHeightTransactionConfirmationDone = blockHeight;
                            }
                        }
                    }
                    else
                    {
                        if (GetBlockMirrorObject(blockHeight, out ClassBlockObject blockObject))
                        {
                            if (blockObject != null)
                            {
                                if (blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                                {
                                    found = true;

                                    if (blockObject.BlockLastHeightTransactionConfirmationDone > 0 &&
                                        blockObject.BlockUnlockValid &&
                                        blockObject.BlockNetworkAmountConfirmations >= BlockchainSetting.BlockAmountNetworkConfirmations)
                                    {
                                        lastBlockHeightTransactionConfirmationDone = blockHeight;
                                    }
                                }
                            }
                        }
                    }
                    if (!found)
                    {
                        listBlockHeight.Add(blockHeight);
                    }
                }

                if (listBlockHeight.Count > 0)
                {
                    foreach (var blockObjectPair in await GetBlockInformationListByBlockHeightListTargetFromMemoryDataCache(listBlockHeight, cancellation))
                    {
                        if (blockObjectPair.Value != null)
                        {

                            if (blockObjectPair.Value.BlockStatus == ClassBlockEnumStatus.LOCKED)
                            {

                                if (blockObjectPair.Value.BlockLastHeightTransactionConfirmationDone > 0 &&
                                    blockObjectPair.Value.BlockUnlockValid &&
                                    blockObjectPair.Value.BlockNetworkAmountConfirmations >= BlockchainSetting.BlockAmountNetworkConfirmations)
                                {
                                    lastBlockHeightTransactionConfirmationDone = blockObjectPair.Value.BlockHeight;
                                }
                                break;
                            }
                        }
                    }


                    // Clean up.
                    listBlockHeight.Clear();
                }

                #endregion
            }

            return lastBlockHeightTransactionConfirmationDone;
        }

        /// <summary>
        /// Return a list of block height who is missing.
        /// </summary>
        /// <param name="blockHeightTarget"></param>
        /// <param name="enableMaxRange"></param>
        /// <param name="ignoreLockedBlocks"></param>
        /// <param name="useSemaphore">Lock or not the access of the cache, determine also if the access require a simple get of the data cache or not.</param>
        /// <param name="cancellation"></param>
        /// <param name="maxRange"></param>
        /// <returns></returns>
        public DisposableList<long> GetListBlockMissing(long blockHeightTarget, bool enableMaxRange, bool ignoreLockedBlocks, bool useSemaphore, CancellationTokenSource cancellation, int maxRange)
        {
            DisposableList<long> blockMiss = new DisposableList<long>();

            long blockHeightExpected = 0;
            int countBlockListed = 0;
            for (long i = 0; i < blockHeightTarget; i++)
            {
                if (enableMaxRange)
                {
                    if (countBlockListed >= maxRange)
                    {
                        break;
                    }
                }

                blockHeightExpected++;
                bool found = false;

                if (!ContainsKey(blockHeightExpected))
                {
                    blockMiss.Add(blockHeightExpected);
                    countBlockListed++;
                    found = true;
                }

                if (!found)
                {
                    if (!ignoreLockedBlocks)
                    {
                        if (_dictionaryBlockObjectMemory[blockHeightExpected].Content != null)
                        {
                            if (_dictionaryBlockObjectMemory[blockHeightExpected].Content.BlockStatus == ClassBlockEnumStatus.LOCKED)
                            {
                                blockMiss.Add(blockHeightExpected);
                                countBlockListed++;
                            }
                        }
                    }
                }
            }

            blockMiss.Sort();

            return blockMiss;
        }

        /// <summary>
        /// Get a block transaction target by his transaction hash his block height.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <param name="useBlockTransactionCache"></param>
        /// <returns></returns>
        public async Task<ClassBlockTransaction> GetBlockTransactionFromSpecificTransactionHashAndHeight(string transactionHash, long blockHeight, bool useBlockTransactionCache, CancellationTokenSource cancellation)
        {

            if (transactionHash.IsNullOrEmpty())
            {
                return null;
            }

            if (blockHeight == 0)
            {
                blockHeight = ClassTransactionUtility.GetBlockHeightFromTransactionHash(transactionHash);
            }

            if (ContainsKey(blockHeight))
            {

                ClassBlockTransaction blockTransaction = await GetBlockTransactionByTransactionHashFromMemoryDataCache(transactionHash, blockHeight, useBlockTransactionCache, cancellation);

                if (blockTransaction != null)
                {
                    return blockTransaction;
                }

            }

            return null;
        }

        /// <summary>
        /// Get a block transaction list by a block height target from the active memory or the cache.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="useSemaphore">Lock or not the access of the cache, determine also if the access require a simple get of the data cache or not.</param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<SortedList<string, ClassBlockTransaction>> GetTransactionListFromBlockHeightTarget(long blockHeight, bool keepAlive, CancellationTokenSource cancellation)
        {
            return await GetTransactionListFromBlockHeightTargetFromMemoryDataCache(blockHeight, keepAlive, cancellation);
        }

        /// <summary>
        /// Get a list of transaction hash by wallet address.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="useSemaphore"></param>
        /// <param name="blockHeightTarget"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<HashSet<string>> GetListTransactionHashByWalletAddressTarget(string walletAddress, long blockHeightTarget, bool keepAlive, CancellationTokenSource cancellation)
        {
            HashSet<string> listTransactionHash = new HashSet<string>();


            if (blockHeightTarget >= BlockchainSetting.GenesisBlockHeight)
            {
                bool found = false;

                if (ContainsKey(blockHeightTarget))
                {
                    if (_dictionaryBlockObjectMemory[blockHeightTarget].Content != null)
                    {
                        foreach (var transactionHash in _dictionaryBlockObjectMemory[blockHeightTarget].Content.BlockTransactions.Keys)
                        {
                            listTransactionHash.Add(transactionHash);
                        }

                        found = true;
                    }

                    if (!found)
                    {
                        if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
                        {
                            foreach (var transactionPair in await GetTransactionListFromBlockHeightTarget(blockHeightTarget, keepAlive, cancellation))
                            {
                                if (transactionPair.Value.TransactionObject.WalletAddressReceiver == walletAddress ||
                                    transactionPair.Value.TransactionObject.WalletAddressSender == walletAddress)
                                {
                                    if (!listTransactionHash.Contains(transactionPair.Key))
                                    {
                                        listTransactionHash.Add(transactionPair.Key);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            else
            {

                long lastBlockHeight = GetLastBlockHeight;

                for (long i = 0; i < lastBlockHeight; i++)
                {
                    long blockHeight = i + 1;
                    if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                    {
                        foreach (string transactionHash in _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions.Keys)
                        {
                            listTransactionHash.Add(transactionHash);
                        }
                    }
                }

                if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
                {
                    foreach (string transactionHash in await GetListTransactionHashByWalletAddressTargetFromMemoryDataCache(walletAddress, cancellation))
                    {
                        if (!listTransactionHash.Contains(transactionHash))
                        {
                            listTransactionHash.Add(transactionHash);
                        }
                    }
                }
            }

            return listTransactionHash;
        }

        #endregion

        #region Specific update of data managed with the cache system.

        /// <summary>
        /// Get the last blockchain stats.
        /// </summary>
        /// <param name="blockchainNetworkStatsObject"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<ClassBlockchainNetworkStatsObject> GetBlockchainNetworkStatsObjectAsync(ClassBlockchainNetworkStatsObject blockchainNetworkStatsObject, CancellationTokenSource cancellation)
        {
            if (blockchainNetworkStatsObject == null)
            {
                blockchainNetworkStatsObject = new ClassBlockchainNetworkStatsObject();
            }

            bool semaphoreUsed = false;

            try
            {
                if (Count > 0)
                {

                    semaphoreUsed = await _semaphoreSlimUpdateTransactionConfirmations.WaitAsync(1000, cancellation.Token);


                    if (blockchainNetworkStatsObject.LastNetworkBlockHeight == 0)
                    {
                        blockchainNetworkStatsObject.LastNetworkBlockHeight = GetLastBlockHeight;
                    }

                    long lastBlockHeight = GetLastBlockHeight;

                    if (lastBlockHeight >= BlockchainSetting.GenesisBlockHeight)
                    {
                        blockchainNetworkStatsObject.LastBlockHeight = lastBlockHeight;

                        if (semaphoreUsed)
                        {
                            var lastBlock = await GetBlockInformationDataStrategy(lastBlockHeight, cancellation);

                            if (lastBlock != null)
                            {
                                long lastBlockHeightUnlocked = GetLastBlockHeightUnlocked(cancellation);
                                long lastBlockHeightConfirmationDoneProgress = await GetLastBlockHeightTransactionConfirmationDone(cancellation);
                                long totalMemPoolTransaction = ClassMemPoolDatabase.GetCountMemPoolTx;
                                ClassBlockEnumStatus lastBlockStatus = lastBlock.BlockStatus;
                                string lastBlockHash = lastBlock.BlockHash;
                                BigInteger lastBlockDifficulty = lastBlock.BlockDifficulty;

                                if (blockchainNetworkStatsObject.LastBlockHeightTransactionConfirmationDone != lastBlockHeightConfirmationDoneProgress ||
                                    blockchainNetworkStatsObject.LastBlockHeightUnlocked != lastBlockHeightUnlocked ||
                                    blockchainNetworkStatsObject.LastBlockHeight != lastBlockHeight)
                                {
                                    bool canceled = false;

                                    long startHeightAvgMining = (lastBlockHeightUnlocked - BlockchainSetting.BlockDifficultyRangeCalculation);

                                    if (startHeightAvgMining <= 0)
                                    {
                                        startHeightAvgMining = 1;
                                    }

                                    long endHeightAvgMining = startHeightAvgMining + BlockchainSetting.BlockDifficultyRangeCalculation;

                                    BigInteger totalFeeCirculating = 0;
                                    BigInteger totalCoinPending = 0;
                                    BigInteger totalCoinCirculating = 0;
                                    long totalTransaction = 0;
                                    long totalTransactionConfirmed = 0;
                                    long averageMiningTotalTimespend = 0;
                                    long averageMiningTimespendExpected = 0;
                                    long lastBlockHeightTransactionConfirmationDone = 0;

                                    int totalTravel = 0;

                                    long timestampTaskStart = ClassUtility.GetCurrentTimestampInMillisecond();

                                    long lastBlockHeightTravel = 0;

                                    try
                                    {

                                        HashSet<long> listBlockHeight = new HashSet<long>();

                                        for (long i = 0; i < lastBlockHeight; i++)
                                        {
                                            long blockHeight = i + 1;
                                            listBlockHeight.Add(blockHeight);
                                        }

                                        foreach (ClassBlockObject blockObject in await GetListBlockInformationDataFromListBlockHeightStrategy(listBlockHeight, cancellation))
                                        {
                                            if (blockObject != null)
                                            {
                                                if (blockObject.BlockHeight > lastBlockHeightTravel)
                                                {
                                                    if (blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                                                    {
                                                        if (totalTravel >= startHeightAvgMining && totalTravel <= endHeightAvgMining)
                                                        {
                                                            averageMiningTotalTimespend += blockObject.TimestampFound - blockObject.TimestampCreate;
                                                            averageMiningTimespendExpected += BlockchainSetting.BlockTime;
                                                        }

                                                        lastBlockHeightUnlocked = blockObject.BlockHeight;

                                                        if (blockObject.BlockUnlockValid && blockObject.BlockNetworkAmountConfirmations >= BlockchainSetting.BlockAmountNetworkConfirmations)
                                                        {

                                                            totalTransaction += blockObject.TotalTransaction;
                                                            totalTransactionConfirmed += blockObject.TotalTransactionConfirmed;


                                                            if (blockObject.BlockTransactionConfirmationCheckTaskDone)
                                                            {
                                                                lastBlockHeightTransactionConfirmationDone = blockObject.BlockLastHeightTransactionConfirmationDone;
                                                            }

                                                            totalCoinPending += blockObject.TotalCoinPending;
                                                            totalCoinCirculating += blockObject.TotalCoinConfirmed;
                                                            if (blockObject.TotalFee > 0)
                                                            {
                                                                totalFeeCirculating += blockObject.TotalFee;
                                                            }

                                                        }
                                                    }

                                                    lastBlockHeightTravel = blockObject.BlockHeight;
                                                    totalTravel++;
                                                }
                                            }
                                            else
                                            {
                                                canceled = true;

                                                break;
                                            }
                                        }

                                        // Clean up.
                                        listBlockHeight.Clear();
                                    }
                                    catch (Exception error)
                                    {
                                        canceled = true;

                                        ClassLog.WriteLine("Error on building latest blockchain stats. Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_GENERAL, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);
#if DEBUG
                                        Debug.WriteLine("Error on building latest blockchain stats. Exception: " + error.Message);
#endif
                                    }

                                    long timespend = ClassUtility.GetCurrentTimestampInMillisecond() - timestampTaskStart;

                                    // Retrieve latest stats calculated if their is any cancellation done.
                                    if (!canceled)
                                    {
#if DEBUG
                                        Debug.WriteLine("Timespend to generate the latest blockchain stats: " + timespend + " ms.");
#endif
                                        // Update tasks stats confirmed.
                                        blockchainNetworkStatsObject.TotalCoinCirculating = totalCoinCirculating;
                                        blockchainNetworkStatsObject.TotalCoinPending = totalCoinPending;
                                        blockchainNetworkStatsObject.TotalFee = totalFeeCirculating;
                                        blockchainNetworkStatsObject.TotalTransactionsConfirmed = totalTransactionConfirmed;
                                        blockchainNetworkStatsObject.TotalTransactions = totalTransaction;
                                        blockchainNetworkStatsObject.LastAverageMiningTimespendDone = averageMiningTotalTimespend;
                                        blockchainNetworkStatsObject.LastAverageMiningTimespendExpected = averageMiningTimespendExpected;
                                        blockchainNetworkStatsObject.LastBlockHeightTransactionConfirmationDone = lastBlockHeightTransactionConfirmationDone;
                                        blockchainNetworkStatsObject.LastBlockHeightUnlocked = lastBlockHeightUnlocked;
                                        blockchainNetworkStatsObject.LastUpdateStatsDateTime = ClassUtility.GetDatetimeFromTimestamp(ClassUtility.GetCurrentTimestampInSecond());
                                        blockchainNetworkStatsObject.BlockchainStatsTimestampToGenerate = timespend;
                                        blockchainNetworkStatsObject.LastBlockHeight = lastBlockHeight;
                                        blockchainNetworkStatsObject.LastBlockDifficulty = lastBlockDifficulty;
                                        blockchainNetworkStatsObject.LastBlockHash = lastBlockHash;
                                        blockchainNetworkStatsObject.LastBlockStatus = lastBlockStatus;
                                    }
                                }

                            }
                        }
                    }

                    if (semaphoreUsed)
                    {
                        _semaphoreSlimUpdateTransactionConfirmations.Release();
                        semaphoreUsed = false;
                    }
                }
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreSlimUpdateTransactionConfirmations.Release();
                }
            }
            return blockchainNetworkStatsObject;
        }

        /// <summary>
        /// Update the amount of confirmations of transactions from a block target.
        /// </summary>
        /// <param name="lastBlockHeightUnlockedChecked"></param>
        /// <param name="lastBlockHeightTransactionConfirmationDone"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<ClassBlockchainBlockConfirmationResultObject> UpdateBlockDataTransactionConfirmations(long lastBlockHeightUnlockedChecked, CancellationTokenSource cancellation)
        {
            ClassBlockchainBlockConfirmationResultObject result = await IncrementBlockTransactionConfirmation(lastBlockHeightUnlockedChecked, cancellation);

            return result;
        }

        #region Functions dedicated to confirm block transaction(s).

        /// <summary>
        /// Increment block transactions confirmations once the block share is voted and valid.
        /// </summary>
        private async Task<ClassBlockchainBlockConfirmationResultObject> IncrementBlockTransactionConfirmation(long lastBlockHeightUnlockedChecked, CancellationTokenSource cancellation)
        {
            bool semaphoreUsed = false;

            ClassBlockchainBlockConfirmationResultObject blockchainBlockConfirmationResultObject = new ClassBlockchainBlockConfirmationResultObject();

            try
            {
                await _semaphoreSlimUpdateTransactionConfirmations.WaitAsync(cancellation.Token);
                semaphoreUsed = true;

                try
                {
                    long lastBlockHeightUnlocked = GetLastBlockHeightUnlocked(cancellation);

                    long totalBlockTravel = 0;
                    bool canceled = false;
                    bool previousBlockedConfirmed = true;
                    bool changeDone = false;

                    long timestampStart = ClassUtility.GetCurrentTimestampInMillisecond();

                    ClassBlockObject lastBlockHeightUnlockedObject = await GetBlockInformationDataStrategy(lastBlockHeightUnlockedChecked, cancellation);
                    long lastBlockHeightTransactionConfirmationDone = await GetLastBlockHeightTransactionConfirmationDone(cancellation);

                    if (lastBlockHeightUnlockedObject == null)
                    {
                        canceled = true;
                    }
                    else
                    {
                        long totalTask = lastBlockHeightUnlockedChecked - lastBlockHeightTransactionConfirmationDone;

                        if (totalTask > 0)
                        {

#if DEBUG
                            Debug.WriteLine("Total task to do " + totalTask + " - > Last unlocked checked: " + lastBlockHeightUnlockedChecked + " | Last block height progress confirmation height: " + lastBlockHeightTransactionConfirmationDone);
#endif

                            ClassLog.WriteLine("Total task to do " + totalTask + " - > Last unlocked checked: " + lastBlockHeightUnlockedChecked + " | Last block height progress confirmation height: " + lastBlockHeightTransactionConfirmationDone, ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Yellow);


                            #region Travel each block ranges generated, retrieve blocks by range and update confirmations.

                            ClassBlockObject previousBlockObject = null;
                            long previousBlockHeight = 0;
                            long totalTravelBeforePause = 0;
                            long lastBlockHeightCheckpoint = ClassBlockchainDatabase.GetLastBlockHeightTransactionCheckpoint();

                            HashSet<long> listBlockHeightFullyConfirmed = new HashSet<long>();

                            #region Generate a list of block height where tx's are fully confirmed.

                            for (long i = 0; i < lastBlockHeightTransactionConfirmationDone; i++)
                            {
                                long blockHeight = i + 1;
                                previousBlockHeight = blockHeight;

                                ClassBlockObject blockInformationObject = await GetBlockInformationDataStrategy(blockHeight, cancellation);

                                bool isFullyConfirmed = true;

                                if (blockInformationObject == null)
                                {
                                    isFullyConfirmed = false;
                                }
                                if (!blockInformationObject.BlockTransactionConfirmationCheckTaskDone || !blockInformationObject.BlockUnlockValid || !blockInformationObject.BlockTransactionFullyConfirmed)
                                {
                                    isFullyConfirmed = false;
                                }

                                if (isFullyConfirmed)
                                {
                                    if (blockHeight > BlockchainSetting.GenesisBlockHeight)
                                    {
                                        ClassBlockObject previousBlockInformationObject = await GetBlockInformationDataStrategy(blockHeight - 1, cancellation);

                                        if (previousBlockInformationObject == null)
                                        {
                                            isFullyConfirmed = false;
                                        }
                                        if (!previousBlockInformationObject.BlockTransactionConfirmationCheckTaskDone || !previousBlockInformationObject.BlockUnlockValid || !previousBlockInformationObject.BlockTransactionFullyConfirmed)
                                        {
                                            isFullyConfirmed = false;
                                        }
                                    }
                                }

                                if (isFullyConfirmed)
                                {
                                    listBlockHeightFullyConfirmed.Add(blockHeight);
                                    blockchainBlockConfirmationResultObject.ListBlockHeightConfirmed.Add(blockHeight);
                                }
                                totalBlockTravel++;
                            }

                            #endregion

                            // Build a list of block height by range to proceed and ignore block height's fully confirmed.
                            foreach (var blockRange in BuildListBlockHeightByRange(lastBlockHeightUnlockedChecked, listBlockHeightFullyConfirmed))
                            {
                                if (blockRange.Count > 0)
                                {
                                    blockRange.Sort();

                                    if (blockRange.Count > 0)
                                    {
                                        long blockMinRange = blockRange[0];
                                        long blockMaxRange = blockRange[blockRange.Count - 1];

                                        int blockFromCache = 0;

                                        SortedList<long, Tuple<ClassBlockObject, bool>> blockDataListRetrievedRead = await GetBlockListFromBlockHeightRangeTargetFromMemoryDataCache(blockMinRange, blockMaxRange, true, cancellation);

                                        if (blockDataListRetrievedRead != null)
                                        {
                                            if (blockDataListRetrievedRead.Count > 0)
                                            {
                                                // List of blocks updated.
                                                Dictionary<long, ClassBlockObject> listBlockObjectUpdated = new Dictionary<long, ClassBlockObject>();

                                                // Retrieve by range blocks from the active memory if possible or by the cache system.
                                                foreach (var blockRetrieve in blockDataListRetrievedRead.Values)
                                                {
                                                    if (blockRetrieve?.Item1 != null)
                                                    {
                                                        if (blockRetrieve.Item1.BlockTransactions != null)
                                                        {
                                                            long blockHeight = blockRetrieve.Item1.BlockHeight;

                                                            if (blockRetrieve.Item2)
                                                            {
                                                                blockFromCache++;
                                                            }
                                                            if (blockHeight >= BlockchainSetting.GenesisBlockHeight && blockHeight <= lastBlockHeightUnlockedChecked)
                                                            {
                                                                ClassBlockObject blockObject = blockRetrieve.Item1;

                                                                if (blockObject != null)
                                                                {
                                                                    if (blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                                                                    {
                                                                        if (previousBlockedConfirmed)
                                                                        {
                                                                            if (!blockObject.BlockUnlockValid)
                                                                            {
                                                                                if (blockObject.BlockNetworkAmountConfirmations >= BlockchainSetting.BlockAmountNetworkConfirmations)
                                                                                {
                                                                                    blockObject.BlockUnlockValid = true;
                                                                                }
                                                                                else
                                                                                {
#if DEBUG
                                                                                    Debug.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", the block is not confirmed by network.");
#endif
                                                                                    ClassLog.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", the block is not confirmed by network.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);

                                                                                    canceled = true;
                                                                                    break;
                                                                                }
                                                                            }
                                                                            if (blockObject.BlockUnlockValid)
                                                                            {
                                                                                if (blockHeight <= lastBlockHeightUnlockedChecked)
                                                                                {
                                                                                    changeDone = false;

                                                                                    bool cancelBlockTransactionConfirmationTask = false;

                                                                                    #region Check the block data integrety if it's not done yet.

                                                                                    if (!blockObject.BlockTransactionConfirmationCheckTaskDone)
                                                                                    {
                                                                                        if (blockObject.BlockUnlockValid && blockObject.BlockNetworkAmountConfirmations >= BlockchainSetting.BlockAmountNetworkConfirmations)
                                                                                        {
                                                                                            // Clean up invalid tx's probably late or push by something else on the bad moment.

                                                                                            var memPoolCheckBlockHeight = await ClassMemPoolDatabase.MemPoolContainsBlockHeight(blockObject.BlockHeight, cancellation);

                                                                                            if (memPoolCheckBlockHeight.Item1 && memPoolCheckBlockHeight.Item2 > 0)
                                                                                            {

                                                                                                await ClassMemPoolDatabase.RemoveMemPoolAllTxFromBlockHeightTarget(blockHeight, cancellation);

                                                                                                ClassLog.WriteLine("Some tx's on mem pool are invalid, they target a block height: " + blockObject.BlockHeight + " already traveled.", ClassEnumLogLevelType.LOG_LEVEL_GENERAL, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);

                                                                                            }

                                                                                            if (blockObject.BlockHeight > BlockchainSetting.GenesisBlockHeight)
                                                                                            {


                                                                                                bool failedToGet = previousBlockObject == null;

                                                                                                if (failedToGet)
                                                                                                {

                                                                                                    previousBlockObject = await GetBlockDataStrategy(blockHeight - 1, true, cancellation);

                                                                                                    if (previousBlockObject == null)
                                                                                                    {
                                                                                                        cancelBlockTransactionConfirmationTask = true;

#if DEBUG
                                                                                                        Debug.WriteLine("Failed to get the previous block height: " + previousBlockHeight);
#endif
                                                                                                    }
                                                                                                }
                                                                                                if (!cancelBlockTransactionConfirmationTask)
                                                                                                {
                                                                                                    ClassBlockEnumCheckStatus blockCheckStatus = ClassBlockUtility.CheckBlockHash(blockObject.BlockHash, blockObject.BlockHeight, blockObject.BlockDifficulty, previousBlockObject.BlockTransactions.Count, previousBlockObject.BlockFinalHashTransaction);

                                                                                                    if (blockCheckStatus != ClassBlockEnumCheckStatus.VALID_BLOCK_HASH)
                                                                                                    {
                                                                                                        ClassLog.WriteLine("Invalid data integrity on block height: " + blockObject.BlockHeight + " cancel task of transaction confirmation. Result: " + blockCheckStatus, ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
#if DEBUG
                                                                                                        Debug.WriteLine("Invalid data integrity on block height: " + blockObject.BlockHeight + " cancel task of transaction confirmation. Result: " + blockCheckStatus);
#endif
                                                                                                        cancelBlockTransactionConfirmationTask = true;
                                                                                                    }

                                                                                                    if (!CheckBlockMinedShare(blockObject, previousBlockObject))
                                                                                                    {
                                                                                                        ClassLog.WriteLine("Invalid block mining share on block height: " + blockObject.BlockHeight + " cancel task of transaction confirmation.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
#if DEBUG
                                                                                                        Debug.WriteLine("Invalid block mining on block height: " + blockObject.BlockHeight + " cancel task of transaction confirmation.");
#endif
                                                                                                        cancelBlockTransactionConfirmationTask = true;
                                                                                                    }
                                                                                                }
                                                                                            }
                                                                                            else
                                                                                            {
                                                                                                if (blockObject.BlockWalletAddressWinner != BlockchainSetting.WalletAddressDev(0))
                                                                                                {
                                                                                                    ClassLog.WriteLine("Warning the genesis block is invalid.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
#if DEBUG
                                                                                                    Debug.WriteLine("Warning the genesis block is invalid.");
#endif
                                                                                                    cancelBlockTransactionConfirmationTask = true;
                                                                                                }
                                                                                                if (blockObject.BlockTransactions.Count == BlockchainSetting.GenesisBlockTransactionCount)
                                                                                                {
                                                                                                    bool error = false;
                                                                                                    foreach (var tx in blockObject.BlockTransactions)
                                                                                                    {
                                                                                                        if (tx.Value.TransactionObject.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION)
                                                                                                        {
                                                                                                            error = true;
                                                                                                            break;
                                                                                                        }
                                                                                                        if (tx.Value.TransactionObject.WalletAddressReceiver != BlockchainSetting.WalletAddressDev(0))
                                                                                                        {
                                                                                                            error = true;
                                                                                                            break;
                                                                                                        }
                                                                                                        if (tx.Value.TransactionObject.Amount != BlockchainSetting.GenesisBlockAmount)
                                                                                                        {
                                                                                                            error = true;
                                                                                                            break;
                                                                                                        }
                                                                                                    }
                                                                                                    if (error)
                                                                                                    {
                                                                                                        cancelBlockTransactionConfirmationTask = true;

                                                                                                        ClassLog.WriteLine("Warning the genesis block is invalid.", ClassEnumLogLevelType.LOG_LEVEL_GENERAL, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
#if DEBUG
                                                                                                        Debug.WriteLine("Warning the genesis block is invalid.");
#endif
                                                                                                    }
                                                                                                }
                                                                                                else
                                                                                                {
                                                                                                    cancelBlockTransactionConfirmationTask = true;
                                                                                                    ClassLog.WriteLine("Warning the genesis block is invalid.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
#if DEBUG
                                                                                                    Debug.WriteLine("Warning the genesis block is invalid.");
#endif
                                                                                                }

                                                                                            }

                                                                                            if (!cancelBlockTransactionConfirmationTask)
                                                                                            {
                                                                                                blockObject.BlockTransactionConfirmationCheckTaskDone = true;
                                                                                                changeDone = true;
                                                                                            }
                                                                                            else
                                                                                            {
                                                                                                blockObject.BlockUnlockValid = false;
                                                                                                blockObject.BlockNetworkAmountConfirmations = 0;
                                                                                                blockObject.BlockSlowNetworkAmountConfirmations = 0;
                                                                                                await AddOrUpdateMemoryDataToCache(blockObject, true, null);
#if DEBUG
                                                                                                Debug.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", the confirmation check task failed.");
#endif
                                                                                                ClassLog.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", the confirmation check task failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);

                                                                                                canceled = true;
                                                                                                break;
                                                                                            }
                                                                                        }
                                                                                    }

                                                                                    #endregion

                                                                                    #region Attempt to increment block transaction confirmation(s).

                                                                                    if (blockObject.BlockTransactionConfirmationCheckTaskDone)
                                                                                    {
                                                                                        long totalTaskToDo = lastBlockHeightUnlockedChecked - blockObject.BlockHeight;
                                                                                        if (blockObject.BlockTotalTaskTransactionConfirmationDone <= totalTaskToDo)
                                                                                        {
                                                                                            ClassBlockObject blockObjectUpdated;

                                                                                            if (!blockObject.BlockTransactionFullyConfirmed)
                                                                                            {
                                                                                                blockObjectUpdated = await TaskTravelBlockTransaction(blockObject, lastBlockHeightUnlockedObject, listBlockObjectUpdated, cancellation);
                                                                                            }
                                                                                            else
                                                                                            {
                                                                                                blockObjectUpdated = blockObject;
                                                                                            }

                                                                                            if (blockObjectUpdated != null)
                                                                                            {
                                                                                                lastBlockHeightTransactionConfirmationDone = blockHeight;
                                                                                                blockObjectUpdated.BlockTransactionConfirmationCheckTaskDone = true;
                                                                                                blockObjectUpdated.BlockTotalTaskTransactionConfirmationDone = totalTaskToDo;
                                                                                                blockObjectUpdated.BlockLastHeightTransactionConfirmationDone = lastBlockHeightUnlockedChecked;
                                                                                                blockObjectUpdated.BlockUnlockValid = true;
                                                                                                blockObjectUpdated.BlockNetworkAmountConfirmations = BlockchainSetting.BlockAmountNetworkConfirmations;
                                                                                                blockObjectUpdated.BlockLastChangeTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                                                                                                blockObjectUpdated.TotalTransaction = blockObjectUpdated.BlockTransactions.Count;
                                                                                                blockObjectUpdated.TotalCoinConfirmed = 0;
                                                                                                blockObjectUpdated.TotalFee = 0;
                                                                                                blockObjectUpdated.TotalCoinPending = 0;
                                                                                                blockObjectUpdated.TotalTransactionConfirmed = 0;

                                                                                                // Update block cached confirmed retrieved.
                                                                                                foreach (var txHash in blockObjectUpdated.BlockTransactions.Keys)
                                                                                                {
                                                                                                    if (blockObjectUpdated.BlockTransactions[txHash].TransactionStatus)
                                                                                                    {
                                                                                                        blockObjectUpdated.BlockTransactions[txHash].TransactionTotalConfirmation = (lastBlockHeightUnlockedChecked - blockHeight);

                                                                                                        long totalConfirmationsToReach = blockObjectUpdated.BlockTransactions[txHash].TransactionBlockHeightTarget - blockObjectUpdated.BlockTransactions[txHash].TransactionBlockHeightInsert;

                                                                                                        if (blockObjectUpdated.BlockTransactions[txHash].TransactionTotalConfirmation >= totalConfirmationsToReach)
                                                                                                        {
                                                                                                            BigInteger coinsAvailable = blockObjectUpdated.BlockTransactions[txHash].TransactionObject.Amount - blockObjectUpdated.BlockTransactions[txHash].TotalSpend;
                                                                                                            blockObjectUpdated.TotalCoinConfirmed += coinsAvailable;
                                                                                                            blockObjectUpdated.TotalTransactionConfirmed++;
                                                                                                        }
                                                                                                        else
                                                                                                        {
                                                                                                            blockObjectUpdated.TotalCoinPending += blockObjectUpdated.BlockTransactions[txHash].TransactionObject.Amount;
                                                                                                        }


                                                                                                        if (blockObjectUpdated.BlockTransactions[txHash].TransactionObject.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION &&
                                                                                                            blockObjectUpdated.BlockTransactions[txHash].TransactionObject.TransactionType != ClassTransactionEnumType.DEV_FEE_TRANSACTION)
                                                                                                        {
                                                                                                            blockObjectUpdated.TotalFee += blockObjectUpdated.BlockTransactions[txHash].TransactionObject.Fee;
                                                                                                        }
                                                                                                    }
                                                                                                }

                                                                                                // Update the active memory if this one is available on the active memory.
                                                                                                if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                                                                                                {
                                                                                                    _dictionaryBlockObjectMemory[blockHeight].Content = blockObjectUpdated;
                                                                                                    _dictionaryBlockObjectMemory[blockHeight].Content.BlockNetworkAmountConfirmations = BlockchainSetting.BlockAmountNetworkConfirmations;
                                                                                                    _dictionaryBlockObjectMemory[blockHeight].Content.BlockUnlockValid = true;
                                                                                                    _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactionConfirmationCheckTaskDone = true;
                                                                                                    _dictionaryBlockObjectMemory[blockHeight].Content.BlockTotalTaskTransactionConfirmationDone = totalTaskToDo;
                                                                                                    _dictionaryBlockObjectMemory[blockHeight].Content.BlockLastHeightTransactionConfirmationDone = lastBlockHeightUnlockedChecked;
                                                                                                    _dictionaryBlockObjectMemory[blockHeight].Content.BlockLastChangeTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                                                                                                }

                                                                                                listBlockObjectUpdated.Add(blockObjectUpdated.BlockHeight, blockObjectUpdated);

                                                                                                changeDone = true;


                                                                                                if (blockObjectUpdated.BlockHeight >= lastBlockHeightCheckpoint + BlockchainSetting.TaskVirtualBlockCheckpoint)
                                                                                                {
                                                                                                    ClassBlockchainDatabase.InsertCheckpoint(ClassCheckpointEnumType.BLOCK_HEIGHT_TRANSACTION_CHECKPOINT, blockObjectUpdated.BlockHeight, string.Empty, 0, 0);
                                                                                                    lastBlockHeightCheckpoint = blockObjectUpdated.BlockHeight;
                                                                                                }
                                                                                            }
                                                                                            else
                                                                                            {
#if DEBUG
                                                                                                Debug.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + " the block returned after the work done is empty.");
#endif
                                                                                                ClassLog.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + " the block returned after the work done is empty.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);
                                                                                                canceled = true;
                                                                                                break;
                                                                                            }

                                                                                        }
                                                                                        else
                                                                                        {
#if DEBUG
                                                                                            Debug.WriteLine("The block height: " + blockObject.BlockHeight + ", task of transaction confirmation until the latest block height unlocked checked is done.");
#endif
                                                                                            ClassLog.WriteLine("The block height: " + blockObject.BlockHeight + ", task of transaction confirmation until the latest block height unlocked checked is done.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);

                                                                                            changeDone = true;
                                                                                        }
                                                                                    }
                                                                                    else
                                                                                    {
#if DEBUG
                                                                                        Debug.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", the confirmation check task failed.");
#endif
                                                                                        ClassLog.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", the confirmation check task failed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);

                                                                                        canceled = true;
                                                                                        break;
                                                                                    }

                                                                                    #endregion

                                                                                    if (!changeDone)
                                                                                    {
#if DEBUG
                                                                                        Debug.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", something is wrong.");
#endif
                                                                                        ClassLog.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", something is wrong.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);

                                                                                        canceled = true;
                                                                                        break;
                                                                                    }
                                                                                    else
                                                                                    {
                                                                                        previousBlockObject = blockObject;
                                                                                        previousBlockHeight = blockHeight;
                                                                                        totalBlockTravel++;
                                                                                    }
                                                                                }
                                                                            }
                                                                            else
                                                                            {
#if DEBUG
                                                                                Debug.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", the block is not confirmed.");
#endif
                                                                                ClassLog.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", the block is not confirmed..", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);

                                                                                canceled = true;
                                                                                break;
                                                                            }
                                                                        }
                                                                        else
                                                                        {
#if DEBUG
                                                                            Debug.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", the previous block is not confirmed.");
#endif
                                                                            ClassLog.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height: " + blockObject.BlockHeight + ", the previous block is not confirmed..", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);

                                                                            canceled = true;
                                                                            break;
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                            else
                                                            {
                                                                break;
                                                            }
                                                        }
                                                        else
                                                        {
                                                            canceled = true;
                                                        }
                                                    }
                                                    else
                                                    {
#if DEBUG
                                                        Debug.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height range: " + blockMinRange + "/" + blockMaxRange + " can't retrieve back propertly data.");
#endif
                                                        ClassLog.WriteLine("Failed to update block transaction(s) confirmation(s) on the block height range: " + blockMinRange + "/" + blockMaxRange + " can't retrieve back propertly data.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);

                                                        canceled = true;
                                                    }

                                                    if (canceled)
                                                    {
                                                        break;
                                                    }

                                                    totalTravelBeforePause++;
                                                }

                                                // Clean up.
                                                blockDataListRetrievedRead.Clear();

                                                // Apply updates done.
                                                if (changeDone && !canceled)
                                                {
                                                    if (listBlockObjectUpdated.Count > 0)
                                                    {
                                                        if (!await AddOrUpdateListBlockObjectOnMemoryDataCache(listBlockObjectUpdated.Values.ToList(), true, cancellation))
                                                        {
#if DEBUG
                                                            Debug.WriteLine("Can't update block(s) updated on the cache system, cancel task of block transaction confirmation.");
#endif
                                                            ClassLog.WriteLine("Can't update block(s) updated on the cache system, cancel task of block transaction confirmation.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
                                                            canceled = true;
                                                        }
                                                    }

                                                }

                                                // Clean up.
                                                listBlockObjectUpdated.Clear();
                                            }
                                        }

                                        // Clean up.
                                        blockRange.Clear();
                                    }
                                }
                                if (canceled)
                                {
                                    changeDone = false;
                                    break;
                                }
                            }


                            // Clean up.
                            listBlockHeightFullyConfirmed.Clear();

                            #endregion

                        }
                        else
                        {
                            changeDone = true;
                        }
                    }

                    if (changeDone && !canceled)
                    {
                        if (totalBlockTravel >= lastBlockHeightUnlockedChecked)
                        {
                            long timestampProceedTx = ClassUtility.GetCurrentTimestampInMillisecond() - timestampStart;
#if DEBUG
                            Debug.WriteLine("Time spend to confirm: " + lastBlockHeightUnlockedChecked + " blocks and to travel: " + lastBlockHeightUnlocked + " blocks: " + timestampProceedTx + " ms. Last block height confirmation done:" + lastBlockHeightTransactionConfirmationDone);
#endif
                            ClassLog.WriteLine("Time spend to confirm: " + lastBlockHeightUnlockedChecked + " blocks and to travel: " + lastBlockHeightUnlocked + " blocks: " + timestampProceedTx + " ms. Last block height confirmation done:" + lastBlockHeightTransactionConfirmationDone, ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Green);

                            blockchainBlockConfirmationResultObject.Status = true;
                            blockchainBlockConfirmationResultObject.LastBlockHeightConfirmationDone = lastBlockHeightTransactionConfirmationDone;
                        }
                        else
                        {
                            blockchainBlockConfirmationResultObject.Status = true;
                            blockchainBlockConfirmationResultObject.LastBlockHeightConfirmationDone = 0;
                        }
                    }
                }
                // Catch the exception once cancelled.
                catch (Exception error)
                {
#if DEBUG
                    Debug.WriteLine("Exception pending to confirm block transaction. Details: " + error.Message);
#endif
                    ClassLog.WriteLine("Exception pending to confirm block transaction. Details: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);


                }

                _semaphoreSlimUpdateTransactionConfirmations.Release();
                semaphoreUsed = false;
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreSlimUpdateTransactionConfirmations.Release();
                }
            }

            return blockchainBlockConfirmationResultObject;
        }

        /// <summary>
        /// Increment block transactions confirmations on blocks fully confirmed.
        /// </summary>
        /// <param name="listBlockHeightConfirmed"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task IncrementBlockTransactionConfirmationOnBlockFullyConfirmed(List<long> listBlockHeightConfirmed, long lastBlockHeightTransactionConfirmationDone, CancellationTokenSource cancellation)
        {
            bool semaphoreUsed = false;

            try
            {
                await _semaphoreSlimUpdateTransactionConfirmations.WaitAsync(cancellation.Token);
                semaphoreUsed = true;
                long totalPassed = 0;
                long lastBlockHeight = GetLastBlockHeight;
                long taskStartTimestamp = ClassUtility.GetCurrentTimestampInMillisecond();

                foreach (long blockHeight in listBlockHeightConfirmed)
                {
                    cancellation.Token.ThrowIfCancellationRequested();

                    bool cancelConfirmations = false;

                    if (lastBlockHeight != GetLastBlockHeight)
                    {
                        break;
                    }

                    ClassBlockObject blockObjectInformations = await GetBlockInformationDataStrategy(blockHeight, cancellation);

                    if (blockObjectInformations.BlockTotalTaskTransactionConfirmationDone >= lastBlockHeightTransactionConfirmationDone ||
                        blockObjectInformations.BlockTotalTaskTransactionConfirmationDone >= lastBlockHeightTransactionConfirmationDone - blockHeight)
                    {
                        continue;
                    }

                    ClassBlockObject blockObjectUpdated = await GetBlockDataStrategy(blockHeight, false, cancellation);

                    blockObjectUpdated.BlockTransactionConfirmationCheckTaskDone = true;
                    blockObjectUpdated.BlockTotalTaskTransactionConfirmationDone = lastBlockHeightTransactionConfirmationDone - blockHeight;
                    blockObjectUpdated.BlockLastHeightTransactionConfirmationDone = lastBlockHeightTransactionConfirmationDone;
                    blockObjectUpdated.BlockUnlockValid = true;
                    blockObjectUpdated.BlockNetworkAmountConfirmations = BlockchainSetting.BlockAmountNetworkConfirmations;
                    blockObjectUpdated.BlockLastChangeTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                    blockObjectUpdated.TotalTransaction = blockObjectUpdated.BlockTransactions.Count;
                    blockObjectUpdated.TotalCoinConfirmed = 0;
                    blockObjectUpdated.TotalFee = 0;
                    blockObjectUpdated.TotalCoinPending = 0;
                    blockObjectUpdated.TotalTransactionConfirmed = 0;

                    // Update block cached confirmed retrieved.
                    foreach (var txHash in blockObjectUpdated.BlockTransactions.Keys)
                    {
                        if (lastBlockHeight != GetLastBlockHeight)
                        {
                            cancelConfirmations = true;
                            break;
                        }
                        if (blockObjectUpdated.BlockTransactions[txHash].TransactionStatus)
                        {
                            blockObjectUpdated.BlockTransactions[txHash].TransactionTotalConfirmation = lastBlockHeightTransactionConfirmationDone - blockHeight;

                            long totalConfirmationsToReach = blockObjectUpdated.BlockTransactions[txHash].TransactionBlockHeightTarget - blockObjectUpdated.BlockTransactions[txHash].TransactionBlockHeightInsert;

                            if (blockObjectUpdated.BlockTransactions[txHash].TransactionTotalConfirmation >= totalConfirmationsToReach)
                            {
                                BigInteger coinsAvailable = blockObjectUpdated.BlockTransactions[txHash].TransactionObject.Amount - blockObjectUpdated.BlockTransactions[txHash].TotalSpend;
                                blockObjectUpdated.TotalCoinConfirmed += coinsAvailable;
                                blockObjectUpdated.TotalTransactionConfirmed++;
                            }
                            else
                            {
                                blockObjectUpdated.TotalCoinPending += blockObjectUpdated.BlockTransactions[txHash].TransactionObject.Amount;
                            }


                            if (blockObjectUpdated.BlockTransactions[txHash].TransactionObject.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION &&
                                blockObjectUpdated.BlockTransactions[txHash].TransactionObject.TransactionType != ClassTransactionEnumType.DEV_FEE_TRANSACTION)
                            {
                                blockObjectUpdated.TotalFee += blockObjectUpdated.BlockTransactions[txHash].TransactionObject.Fee;
                            }
                        }
                    }

                    if (cancelConfirmations)
                    {
                        break;
                    }

                    // Update the active memory if this one is available on the active memory.
                    if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                    {
                        _dictionaryBlockObjectMemory[blockHeight].Content = blockObjectUpdated;
                        _dictionaryBlockObjectMemory[blockHeight].Content.BlockNetworkAmountConfirmations = BlockchainSetting.BlockAmountNetworkConfirmations;
                        _dictionaryBlockObjectMemory[blockHeight].Content.BlockUnlockValid = true;
                        _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactionFullyConfirmed = true;
                        _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactionConfirmationCheckTaskDone = true;
                        _dictionaryBlockObjectMemory[blockHeight].Content.BlockTotalTaskTransactionConfirmationDone = lastBlockHeightTransactionConfirmationDone - blockHeight;
                        _dictionaryBlockObjectMemory[blockHeight].Content.BlockLastHeightTransactionConfirmationDone = lastBlockHeightTransactionConfirmationDone;
                        _dictionaryBlockObjectMemory[blockHeight].Content.BlockLastChangeTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                    }

                    if (!await AddOrUpdateMemoryDataToCache(blockObjectUpdated, false, cancellation))
                    {
                        break;
                    }

                    totalPassed++;
                }
                long taskDoneTimestamp = ClassUtility.GetCurrentTimestampInMillisecond();


#if DEBUG
                Debug.WriteLine("Total block fully confirmed on their tx passed: " + totalPassed + ". Task done in: " + (taskDoneTimestamp - taskStartTimestamp) + "ms.");
#endif

                ClassLog.WriteLine("Total block fully confirmed on their tx passed: " + totalPassed + ". Task done in: " + (taskDoneTimestamp - taskStartTimestamp) + "ms.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);

            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreSlimUpdateTransactionConfirmations.Release();
                }
            }
        }

        /// <summary>
        /// Travel block transactions.
        /// </summary>
        /// <param name="blockObject"></param>
        /// <param name="lastBlockHeightUnlockedObject"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<ClassBlockObject> TaskTravelBlockTransaction(ClassBlockObject blockObject, ClassBlockObject lastBlockHeightUnlockedObject, Dictionary<long, ClassBlockObject> listBlockObjectUpdated, CancellationTokenSource cancellation)
        {
            if (blockObject.BlockHeight >= BlockchainSetting.GenesisBlockHeight)
            {
                long totalTaskToDo = lastBlockHeightUnlockedObject.BlockHeight - blockObject.BlockHeight;


                if (blockObject.BlockTotalTaskTransactionConfirmationDone <= totalTaskToDo && blockObject.BlockLastHeightTransactionConfirmationDone < lastBlockHeightUnlockedObject.BlockHeight)
                {

                    var blockTransactionConfirmationResult = await TravelBlockTransactionsToConfirm(blockObject, lastBlockHeightUnlockedObject, listBlockObjectUpdated, cancellation);

                    if (blockTransactionConfirmationResult.ConfirmationTaskStatus)
                    {
                        return blockTransactionConfirmationResult.BlockObjectUpdated;
                    }

#if DEBUG
                    Debug.WriteLine("Failed to increment confirmations on block height: " + blockObject.BlockHeight);
#endif

                }
                else
                {
#if DEBUG
                    Debug.WriteLine("Increment transactions confirmations on the block height: " + blockObject.BlockHeight + " already done.");

#endif
                    return blockObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Travel block transaction(s) to confirm.
        /// </summary>
        /// <param name="blockObject"></param>
        /// <param name="lastBlockObjectUnlocked"></param>
        private async Task<ClassBlockConfirmationTransactionResultObject> TravelBlockTransactionsToConfirm(ClassBlockObject blockObject, ClassBlockObject lastBlockObjectUnlocked, Dictionary<long, ClassBlockObject> listBlockObjectUpdated, CancellationTokenSource cancellation)
        {
            ClassBlockConfirmationTransactionResultObject blockConfirmationTransactionResultObject = new ClassBlockConfirmationTransactionResultObject
            {
                BlockObjectUpdated = blockObject,
                ConfirmationTaskStatus = true
            };

            long lastBlockHeightTransactionCheckpoint = ClassBlockchainDatabase.GetLastBlockHeightTransactionCheckpoint();

            if (blockConfirmationTransactionResultObject.BlockObjectUpdated != null)
            {
                if (blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockHeight <= lastBlockObjectUnlocked.BlockHeight)
                {
                    switch (blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockStatus)
                    {
                        case ClassBlockEnumStatus.UNLOCKED when blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockUnlockValid
                                                                && blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions.Count > 0:
                            {
                                int countTx = 0;
                                int countTxConfirmed = 0;

                                Dictionary<string, string> listWalletAndPublicKeysCache = new Dictionary<string, string>();

                                foreach (var blockTxPair in blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions.OrderBy(x => x.Value.TransactionObject.TimestampSend))
                                {
                                    string blockTxKey = blockTxPair.Key;

                                    if (blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey] != null)
                                    {
                                        if (blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionStatus)
                                        {
                                            if (blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionObject.BlockHeightTransaction <= lastBlockObjectUnlocked.BlockHeight)
                                            {
                                                countTx++;

                                                ClassBlockConfirmationTransactionStatusObject blockTransactionStatus = new ClassBlockConfirmationTransactionStatusObject()
                                                {
                                                    BlockObject = blockConfirmationTransactionResultObject.BlockObjectUpdated,
                                                    BlockTransactionStatus = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_HEIGHT_NOT_REACH
                                                };

                                                long totalConfirmationsDone = blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionTotalConfirmation;
                                                bool noError = true;

                                                if (!noError)
                                                {
                                                    blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionStatus = false;
                                                    blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionInvalidStatus = ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                                                    blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionInvalidRemoveTimestamp = blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionObject.TimestampSend + BlockchainSetting.TransactionInvalidDelayRemove;
                                                    blockConfirmationTransactionResultObject.TotalTransactionWrong++;

#if DEBUG
                                                    Debug.WriteLine(blockTransactionStatus.BlockObject.BlockHeight + " | Invalid amount sources | last unlocked: " + lastBlockObjectUnlocked.BlockHeight);
                                                    Debug.WriteLine("Transaction hash: " + blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionObject.TransactionHash);
                                                    Debug.WriteLine("Transaction Type: " + System.Enum.GetName(typeof(ClassTransactionEnumType), blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionObject.TransactionType));

#endif
                                                }
                                                else
                                                {
                                                    long transactionBlockHeightStart = blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionBlockHeightInsert;
                                                    long transactionBlockHeightTarget = blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionBlockHeightTarget;
                                                    long transactionTotalConfirmationsToReach = transactionBlockHeightTarget - transactionBlockHeightStart;


                                                    if (totalConfirmationsDone < transactionTotalConfirmationsToReach || blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionBlockHeightInsert + totalConfirmationsDone < lastBlockHeightTransactionCheckpoint)
                                                    {
                                                        blockTransactionStatus = await IncreaseBlockTransactionConfirmationFromTxHash(blockConfirmationTransactionResultObject.BlockObjectUpdated, blockTxKey, lastBlockObjectUnlocked, null, false, true, cancellation);
                                                    }
                                                    else
                                                    {
                                                        #region If the block height transaction is behind the last block height transaction checkpoint, we admit to have a transaction fully valided.

                                                        blockTransactionStatus.BlockTransactionStatus = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_ENOUGH_CONFIRMATIONS_REACH;

                                                        #endregion
                                                    }
                                                }

                                                switch (blockTransactionStatus.BlockTransactionStatus)
                                                {
                                                    case ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_ENOUGH_CONFIRMATIONS_REACH:
                                                    case ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_CONFIRMATIONS_INCREMENTED:
                                                        {
                                                            blockConfirmationTransactionResultObject.TotalIncrementedTransactions++;
                                                            blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionTotalConfirmation = (lastBlockObjectUnlocked.BlockHeight - blockObject.BlockHeight);
                                                            blockConfirmationTransactionResultObject.ConfirmationTaskStatus = true;
                                                            if (blockTransactionStatus.BlockTransactionStatus == ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_ENOUGH_CONFIRMATIONS_REACH)
                                                            {
                                                                blockConfirmationTransactionResultObject.TotalTransactionsConfirmed++;
                                                                countTxConfirmed++;
                                                            }
                                                        }
                                                        break;
                                                    case ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_HEIGHT_NOT_REACH:
                                                        {
#if DEBUG
                                                            Debug.WriteLine(blockTransactionStatus.BlockObject.BlockHeight + " |Status " + blockTransactionStatus.BlockTransactionStatus + " | last unlocked: " + lastBlockObjectUnlocked.BlockHeight);
#endif
                                                            blockConfirmationTransactionResultObject.TotalTransactionInPending++;
                                                        }
                                                        break;
                                                    case ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID_DATA:
                                                    case ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID:
                                                    case ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_AMOUNT_OF_CONFIRMATIONS_WRONG:
                                                        {
                                                            blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionStatus = false;
                                                            blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionInvalidRemoveTimestamp = blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionObject.TimestampSend + BlockchainSetting.TransactionInvalidDelayRemove;
                                                            blockConfirmationTransactionResultObject.TotalTransactionWrong++;
#if DEBUG
                                                            Debug.WriteLine(blockTransactionStatus.BlockObject.BlockHeight + " |Status " + blockTransactionStatus.BlockTransactionStatus + " | last unlocked: " + lastBlockObjectUnlocked.BlockHeight);
                                                            Debug.WriteLine("Transaction hash: " + blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionObject.TransactionHash);
                                                            Debug.WriteLine("Transaction Type: " + System.Enum.GetName(typeof(ClassTransactionEnumType), blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactions[blockTxKey].TransactionObject.TransactionType));

#endif
                                                        }
                                                        break;

                                                }
                                            }
                                        }
                                        else
                                        {
                                            blockConfirmationTransactionResultObject.TotalTransactionWrong++;
                                        }
                                    }
                                    else
                                    {
                                        blockConfirmationTransactionResultObject.TotalTransactionWrong++;
                                    }
                                }

                                if (countTxConfirmed == countTx)
                                {
                                    blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockTransactionFullyConfirmed = true;


#if DEBUG
                                    Debug.WriteLine("Every transactions of the block height " + blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockHeight + " are fully confirmed.");
#endif
                                    ClassLog.WriteLine("Every transactions of the block height " + blockConfirmationTransactionResultObject.BlockObjectUpdated.BlockHeight + " are fully confirmed.", ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Green);

                                }

                                // Clean up cache.
                                listWalletAndPublicKeysCache.Clear();

                                break;
                            }
                    }
                }
            }
            else
            {
#if DEBUG
                Debug.WriteLine("Warning Block Object is null.");
#endif
                blockConfirmationTransactionResultObject.BlockObjectUpdated = null;
                blockConfirmationTransactionResultObject.ConfirmationTaskStatus = false;
            }

            return blockConfirmationTransactionResultObject;
        }

        /// <summary>
        /// Update a block transaction from his amount source list.
        /// </summary>
        /// <param name="blockTransactionToCheck"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> UpdateBlockTransactionFromAmountSourceList(ClassBlockTransaction blockTransactionToCheck, Dictionary<long, ClassBlockObject> listBlockObjectUpdated, CancellationTokenSource cancellation)
        {
            bool updateStatus = true;

            Dictionary<long, List<ClassBlockTransaction>> listBlockTransactionSpendUpdated = new Dictionary<long, List<ClassBlockTransaction>>();
            Dictionary<long, List<ClassBlockTransaction>> listBlockTransactionSpendOriginal = new Dictionary<long, List<ClassBlockTransaction>>();

            if (blockTransactionToCheck.TransactionObject.AmountTransactionSource != null)
            {
                if (blockTransactionToCheck.TransactionObject.AmountTransactionSource.Count > 0)
                {
                    if (await CheckTransactionAmountSourceList(blockTransactionToCheck.TransactionObject, cancellation) == ClassTransactionEnumStatus.VALID_TRANSACTION)
                    {
                        foreach (var amountHashSourceObject in blockTransactionToCheck.TransactionObject.AmountTransactionSource)
                        {
                            long blockHeightSource = ClassTransactionUtility.GetBlockHeightFromTransactionHash(amountHashSourceObject.Key);

                            if (ContainsKey(blockHeightSource))
                            {
                                string transactionHash = amountHashSourceObject.Key;

                                ClassBlockTransaction blockTransaction = null;

                                if (listBlockObjectUpdated.ContainsKey(blockHeightSource))
                                {
                                    if (listBlockObjectUpdated[blockHeightSource].BlockTransactions.ContainsKey(transactionHash))
                                    {
                                        blockTransaction = listBlockObjectUpdated[blockHeightSource].BlockTransactions[transactionHash];
                                    }
                                }
                                if (blockTransaction == null)
                                {
                                    blockTransaction = await GetBlockTransactionFromSpecificTransactionHashAndHeight(transactionHash, blockHeightSource, false, cancellation);
                                }
                                if (blockTransaction?.TransactionObject != null)
                                {
                                    if (!listBlockTransactionSpendOriginal.ContainsKey(blockHeightSource))
                                    {
                                        listBlockTransactionSpendOriginal.Add(blockHeightSource, new List<ClassBlockTransaction>());
                                    }
                                    listBlockTransactionSpendOriginal[blockHeightSource].Add(blockTransaction);

                                    if (blockTransaction.TransactionStatus && !blockTransaction.Spent)
                                    {
                                        if (blockTransaction.TotalSpend + amountHashSourceObject.Value.Amount <=
                                            blockTransaction.TransactionObject.Amount)
                                        {
                                            blockTransaction.TotalSpend += amountHashSourceObject.Value.Amount;

                                            if (blockTransaction.TotalSpend ==
                                                blockTransaction.TransactionObject.Amount)
                                            {
                                                blockTransaction.Spent = true;
                                            }

                                            if (!listBlockTransactionSpendUpdated.ContainsKey(blockHeightSource))
                                            {
                                                listBlockTransactionSpendUpdated.Add(blockHeightSource, new List<ClassBlockTransaction>());
                                            }
                                            listBlockTransactionSpendUpdated[blockHeightSource].Add(blockTransaction);
                                        }
                                        else
                                        {
                                            updateStatus = false;
                                            break;
                                        }
                                    }
                                    else
                                    {
                                        updateStatus = false;
                                        break;
                                    }
                                }
                                else
                                {
                                    updateStatus = false;
                                    break;
                                }
                            }
                            else
                            {
                                updateStatus = false;
                                break;
                            }
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            if (updateStatus)
            {
                if (!await InsertListBlockTransactionToMemoryDataCache(listBlockTransactionSpendUpdated.SelectMany(x => x.Value).ToList(), true, cancellation))
                {
                    updateStatus = false;
                }

                if (updateStatus)
                {
                    // Push updated block transactions from the source list of a block transaction spend.
                    foreach (long blockHeightSource in listBlockTransactionSpendUpdated.Keys)
                    {
                        if (listBlockObjectUpdated.ContainsKey(blockHeightSource))
                        {
                            foreach (ClassBlockTransaction blockTransaction in listBlockTransactionSpendUpdated[blockHeightSource])
                            {
                                listBlockObjectUpdated[blockHeightSource].BlockTransactions[blockTransaction.TransactionObject.TransactionHash] = blockTransaction;
                            }
                        }
                    }
                }

                // Push back original block transactions if an update failed.
                if (!updateStatus)
                {
                    if (!await InsertListBlockTransactionToMemoryDataCache(listBlockTransactionSpendOriginal.SelectMany(x => x.Value).ToList(), true, cancellation))
                    {
                        updateStatus = false;
                    }

                    foreach (long blockHeightSource in listBlockTransactionSpendOriginal.Keys)
                    {
                        if (listBlockObjectUpdated.ContainsKey(blockHeightSource))
                        {
                            foreach (ClassBlockTransaction blockTransaction in listBlockTransactionSpendOriginal[blockHeightSource])
                            {
                                listBlockObjectUpdated[blockHeightSource].BlockTransactions[blockTransaction.TransactionObject.TransactionHash] = blockTransaction;
                            }
                        }
                    }
                }
            }

            // Clean up.
            listBlockTransactionSpendUpdated.Clear();
            listBlockTransactionSpendOriginal.Clear();

            return updateStatus;
        }

        /// <summary>
        /// Increase the amount of confirmations of a transaction once a block is unlocked.
        /// </summary>
        /// <param name="blockObject"></param>
        /// <param name="transactionHash"></param>
        /// <param name="lastBlockHeightUnlocked"></param>
        /// <param name="useSemaphore"></param>
        /// <param name="useCheckpoint"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<ClassBlockConfirmationTransactionStatusObject> IncreaseBlockTransactionConfirmationFromTxHash(ClassBlockObject blockObject, string transactionHash, ClassBlockObject lastBlockHeightUnlocked, Dictionary<string, string> listWalletAndPublicKeysCache, bool useSemaphore, bool useCheckpoint, CancellationTokenSource cancellation)
        {
            ClassBlockTransactionEnumStatus transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_HEIGHT_NOT_REACH;

            if (blockObject.BlockUnlockValid)
            {
                switch (lastBlockHeightUnlocked.BlockStatus)
                {
                    case ClassBlockEnumStatus.UNLOCKED:
                        {
                            long totalConfirmationsDone = blockObject.BlockTransactions[transactionHash].TransactionTotalConfirmation;
                            long transactionBlockHeightStart = blockObject.BlockTransactions[transactionHash].TransactionBlockHeightInsert;
                            long transactionBlockHeightTarget = blockObject.BlockTransactions[transactionHash].TransactionBlockHeightTarget;
                            long transactionTotalConfirmationsToReach = transactionBlockHeightTarget - transactionBlockHeightStart;


                            if (transactionBlockHeightTarget < transactionBlockHeightStart)
                            {
                                transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID_DATA;
                            }
                            else
                            {

                                if (transactionTotalConfirmationsToReach < BlockchainSetting.TransactionMandatoryMinBlockTransactionConfirmations)
                                {
                                    transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID_DATA;
                                }
                                else
                                {
                                    bool cancel = false;

                                    if (totalConfirmationsDone + transactionBlockHeightStart > lastBlockHeightUnlocked.BlockHeight)
                                    {
                                        if (totalConfirmationsDone + transactionBlockHeightStart > lastBlockHeightUnlocked.BlockHeight)
                                        {
                                            cancel = true;
                                            transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_AMOUNT_OF_CONFIRMATIONS_WRONG;
                                        }
                                    }

                                    if (!cancel)
                                    {
                                        if (totalConfirmationsDone + transactionBlockHeightStart <= lastBlockHeightUnlocked.BlockHeight)
                                        {
                                            ClassTransactionEnumStatus transactionStatus;

                                            if (totalConfirmationsDone >= transactionTotalConfirmationsToReach || totalConfirmationsDone >= BlockchainSetting.TaskVirtualTransactionCheckpoint)
                                            {
                                                transactionStatus = ClassTransactionEnumStatus.VALID_TRANSACTION;
                                            }
                                            else
                                            {
                                                ClassTransactionObject transactionObject = blockObject.BlockTransactions[transactionHash].TransactionObject;
                                                transactionStatus = await ClassTransactionUtility.CheckTransactionWithBlockchainData(transactionObject, false, false, false, blockObject, totalConfirmationsDone, listWalletAndPublicKeysCache, useSemaphore, cancellation);

                                            }

                                            if (transactionStatus == ClassTransactionEnumStatus.VALID_TRANSACTION)
                                            {
                                                switch (blockObject.BlockTransactions[transactionHash].TransactionObject.TransactionType)
                                                {
                                                    case ClassTransactionEnumType.NORMAL_TRANSACTION:
                                                    case ClassTransactionEnumType.TRANSFER_TRANSACTION:
                                                        {
                                                            if (listWalletAndPublicKeysCache != null)
                                                            {
                                                                if (!listWalletAndPublicKeysCache.ContainsKey(blockObject.BlockTransactions[transactionHash].TransactionObject.WalletAddressSender))
                                                                {
                                                                    listWalletAndPublicKeysCache.Add(blockObject.BlockTransactions[transactionHash].TransactionObject.WalletAddressSender, blockObject.BlockTransactions[transactionHash].TransactionObject.WalletPublicKeySender);
                                                                }
                                                                if (blockObject.BlockTransactions[transactionHash].TransactionObject.TransactionType == ClassTransactionEnumType.TRANSFER_TRANSACTION)
                                                                {
                                                                    if (!listWalletAndPublicKeysCache.ContainsKey(blockObject.BlockTransactions[transactionHash].TransactionObject.WalletAddressReceiver))
                                                                    {
                                                                        listWalletAndPublicKeysCache.Add(blockObject.BlockTransactions[transactionHash].TransactionObject.WalletAddressReceiver, blockObject.BlockTransactions[transactionHash].TransactionObject.WalletPublicKeyReceiver);
                                                                    }
                                                                }
                                                            }
                                                            if (blockObject.BlockTransactions[transactionHash].TransactionTotalConfirmation < transactionTotalConfirmationsToReach)
                                                            {
                                                                var walletBalanceSender = await GetWalletBalanceFromTransaction(blockObject.BlockTransactions[transactionHash].TransactionObject.WalletAddressSender, blockObject.BlockHeight, useCheckpoint, true, false, false, cancellation);
                                                                var walletBalanceReceiver = await GetWalletBalanceFromTransaction(blockObject.BlockTransactions[transactionHash].TransactionObject.WalletAddressReceiver, blockObject.BlockHeight, useCheckpoint, true, false, false, cancellation);

                                                                if (walletBalanceSender.WalletBalance >= 0 && walletBalanceReceiver.WalletBalance >= 0)
                                                                {
                                                                    transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_CONFIRMATIONS_INCREMENTED;
                                                                }
                                                                else
                                                                {

                                                                    transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID;
                                                                }
                                                            }
                                                            else
                                                            {
                                                                transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_ENOUGH_CONFIRMATIONS_REACH;
                                                            }

                                                        }
                                                        break;
                                                    case ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION:
                                                    case ClassTransactionEnumType.DEV_FEE_TRANSACTION:
                                                        {
                                                            if (blockObject.BlockHeight > BlockchainSetting.GenesisBlockHeight)
                                                            {
                                                                if (blockObject.BlockTransactions[transactionHash].TransactionObject.TransactionType == ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION)
                                                                {
                                                                    if (blockObject.BlockUnlockValid)
                                                                    {
                                                                        if (blockObject.BlockWalletAddressWinner != blockObject.BlockTransactions[transactionHash].TransactionObject.WalletAddressReceiver)
                                                                        {
                                                                            cancel = true;
                                                                            transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID;
                                                                        }
                                                                    }
                                                                    else
                                                                    {
                                                                        cancel = true;
                                                                        transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID;
                                                                    }
                                                                }
                                                                else
                                                                {
                                                                    if (blockObject.BlockTransactions[transactionHash].TransactionObject.WalletAddressReceiver != BlockchainSetting.WalletAddressDev(blockObject.BlockTransactions[transactionHash].TransactionObject.TimestampSend))
                                                                    {
                                                                        cancel = true;
                                                                        transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID;
                                                                    }
                                                                }
                                                            }

                                                            if (!cancel)
                                                            {
                                                                if (blockObject.BlockTransactions[transactionHash].TransactionTotalConfirmation < transactionTotalConfirmationsToReach)
                                                                {
                                                                    var walletBalanceReceiver = await GetWalletBalanceFromTransaction(blockObject.BlockTransactions[transactionHash].TransactionObject.WalletAddressReceiver, blockObject.BlockHeight, useCheckpoint, true, false, false, cancellation);

                                                                    if (walletBalanceReceiver.WalletBalance >= 0)
                                                                    {
                                                                        transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_CONFIRMATIONS_INCREMENTED;
                                                                    }
                                                                    else
                                                                    {
                                                                        transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID;
                                                                    }
                                                                }
                                                                else
                                                                {
                                                                    transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_ENOUGH_CONFIRMATIONS_REACH;
                                                                }
                                                            }
                                                        }
                                                        break;
                                                    default:
                                                        transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID;
                                                        break;
                                                }


                                            }
                                            else
                                            {
                                                blockObject.BlockTransactions[transactionHash].TransactionInvalidStatus = transactionStatus;
#if DEBUG
                                                Debug.WriteLine(transactionHash + " is invalid:  " + transactionStatus);
#endif
                                                ClassLog.WriteLine(transactionHash + " is invalid:  " + transactionStatus, ClassEnumLogLevelType.LOG_LEVEL_PEER_TASK_TRANSACTION_CONFIRMATION, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                                                transactionResult = ClassBlockTransactionEnumStatus.TRANSACTION_BLOCK_INVALID;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        break;
                }
            }

            return new ClassBlockConfirmationTransactionStatusObject()
            {
                BlockObject = blockObject,
                BlockTransactionStatus = transactionResult
            };
        }

        #endregion

        #endregion

        #region Specific check of data managed with the cache system.

        /// <summary>
        /// Check if a transaction hash provided already exist on blocks.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> CheckIfTransactionHashAlreadyExist(string transactionHash, long blockHeight, CancellationTokenSource cancellation)
        {
            bool inMemory = false;

            if (blockHeight < BlockchainSetting.GenesisBlockHeight)
            {
                blockHeight = ClassTransactionUtility.GetBlockHeightFromTransactionHash(transactionHash);
            }

            #region Check on the active memory first.

            if (ContainsKey(blockHeight))
            {

                if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                {
                    inMemory = true;
                    try
                    {

                        if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions.ContainsKey(transactionHash))
                        {
                            return true;
                        }

                    }
                    catch
                    {
                        inMemory = false;
                    }
                }
            }

            #endregion

            if (!inMemory)
            {
                return await CheckTransactionHashExistOnMemoryDataOnCache(transactionHash, blockHeight, cancellation);
            }
            return false;
        }

        /// <summary>
        ///  Check if the block height contains block reward.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="blockData"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> CheckBlockHeightContainsBlockReward(long blockHeight, ClassBlockObject blockData, CancellationTokenSource cancellation)
        {
            SortedList<string, ClassBlockTransaction> blockTransactions = null;

            if (blockData != null)
            {
                blockTransactions = new SortedList<string, ClassBlockTransaction>();

                foreach (var blockTransaction in blockData.BlockTransactions)
                {
                    blockTransactions.Add(blockTransaction.Key, blockTransaction.Value);
                }
            }
            else
            {
                if (blockHeight >= BlockchainSetting.GenesisBlockHeight)
                {
                    if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                    {
                        blockTransactions = new SortedList<string, ClassBlockTransaction>();

                        foreach (var blockTransaction in _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions)
                        {
                            blockTransactions.Add(blockTransaction.Key, blockTransaction.Value);
                        }
                    }
                }

                if (blockTransactions == null)
                {
                    blockTransactions = await GetTransactionListFromBlockHeightTargetFromMemoryDataCache(blockHeight, true, cancellation);
                }
            }

            if (blockTransactions != null)
            {
                bool containBlockReward = false;
                bool containDevFee = BlockchainSetting.BlockDevFee(blockHeight) == 0;

                foreach (var tx in blockTransactions)
                {
                    if (tx.Value.TransactionObject.TransactionType == ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION)
                    {
                        containBlockReward = true;
                        if (BlockchainSetting.BlockDevFee(blockHeight) > 0)
                        {
                            if (containDevFee)
                            {
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (blockHeight > BlockchainSetting.GenesisBlockHeight)
                    {
                        if (BlockchainSetting.BlockDevFee(blockHeight) > 0)
                        {
                            if (tx.Value.TransactionObject.TransactionType == ClassTransactionEnumType.DEV_FEE_TRANSACTION)
                            {
                                containDevFee = true;
                                if (containBlockReward)
                                {
                                    break;
                                }
                            }
                        }
                    }
                    else
                    {
                        containDevFee = true;
                    }
                }

                if (containDevFee && containBlockReward)
                {
                    return true;
                }

                foreach (var tx in await ClassMemPoolDatabase.GetMemPoolTxObjectFromBlockHeight(blockHeight, cancellation))
                {
                    if (tx.TransactionType == ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION)
                    {
                        containBlockReward = true;
                        if (containDevFee)
                        {
                            return true;
                        }
                    }
                    if (tx.TransactionType == ClassTransactionEnumType.DEV_FEE_TRANSACTION)
                    {
                        containDevFee = true;
                        if (containBlockReward)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Check amount source list of a transaction.
        /// </summary>
        /// <param name="transactionObject"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<ClassTransactionEnumStatus> CheckTransactionAmountSourceList(ClassTransactionObject transactionObject, CancellationTokenSource cancellation)
        {
            if (transactionObject.AmountTransactionSource != null)
            {
                if (transactionObject.AmountTransactionSource.Count == 0)
                {
                    return ClassTransactionEnumStatus.EMPTY_TRANSACTION_SOURCE_LIST;
                }

                if (transactionObject.AmountTransactionSource.Count(x => x.Key.IsNullOrEmpty()) > 0)
                {
                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                }

                if (transactionObject.AmountTransactionSource.Count(x => x.Value == null) > 0)
                {
                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                }

                if (transactionObject.AmountTransactionSource.Count(x => x.Value.Amount < BlockchainSetting.MinAmountTransaction) > 0)
                {
                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                }
            }
            else
            {
                return ClassTransactionEnumStatus.EMPTY_TRANSACTION_SOURCE_LIST;
            }

            // Check transaction source list spending.
            int countSource = transactionObject.AmountTransactionSource.Count;
            int countSourceValid = 0;
            string walletAddress = transactionObject.WalletAddressSender;

            BigInteger totalAmount = 0;
            foreach (string transactionHash in transactionObject.AmountTransactionSource.Keys)
            {
                long blockHeightSource = ClassTransactionUtility.GetBlockHeightFromTransactionHash(transactionHash);

                if (ContainsKey(blockHeightSource))
                {
                    ClassBlockTransaction blockTransaction = null;
                    if (_dictionaryBlockObjectMemory[blockHeightSource].Content != null)
                    {
                        if (!_dictionaryBlockObjectMemory[blockHeightSource].Content.BlockTransactions.ContainsKey(transactionHash))
                        {
                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                        }
                        else
                        {
                            blockTransaction = _dictionaryBlockObjectMemory[blockHeightSource].Content.BlockTransactions[transactionHash];
                        }
                    }

                    if (blockTransaction == null)
                    {
                        blockTransaction = await GetBlockTransactionFromSpecificTransactionHashAndHeight(transactionHash, blockHeightSource, false, cancellation);
                    }

                    if (blockTransaction?.TransactionObject != null)
                    {
                        if (blockTransaction.TransactionStatus && !blockTransaction.Spent)
                        {
                            if (blockTransaction.TransactionObject.WalletAddressReceiver == walletAddress)
                            {
                                if (blockTransaction.TransactionTotalConfirmation >= (blockTransaction.TransactionObject.BlockHeightTransactionConfirmationTarget - (blockTransaction.TransactionObject.BlockHeightTransaction)))
                                {
                                    if (blockTransaction.TransactionObject.Amount >= transactionObject.AmountTransactionSource[transactionHash].Amount)
                                    {
                                        if (blockTransaction.TransactionObject.Amount - blockTransaction.TotalSpend >= transactionObject.AmountTransactionSource[transactionHash].Amount)
                                        {
                                            countSourceValid++;
                                            totalAmount += transactionObject.AmountTransactionSource[transactionHash].Amount;
                                        }
                                        else
                                        {
                                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                                        }
                                    }
                                    else
                                    {
                                        return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                                    }
                                }
                                else
                                {
                                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                                }
                            }
                            else
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                            }
                        }
                        else
                        {
                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                        }

                    }
                    else
                    {
                        return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                    }
                }
                else
                {
                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                }
            }

            if (countSource != countSourceValid)
            {
                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
            }

            if (totalAmount != transactionObject.Amount + transactionObject.Fee)
            {
                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
            }

            return ClassTransactionEnumStatus.VALID_TRANSACTION;
        }

        /// <summary>
        /// Check a transaction, this function is basic, she is normally used alone when an incoming transaction is sent by a user.
        /// This function is also used has completement with the Peer transaction check for increment block confirmations.
        /// </summary>
        /// <param name="transactionObject">The transaction object data to check.</param>
        /// <param name="blockObjectSource">The block object source if provided.</param>
        /// <param name="checkFromBlockData">If true, check the transaction with the blockchain data.</param>
        /// <param name="listWalletAndPublicKeys">Speed up lookup wallet address with public keys already checked previously if provided.</param>
        /// <param name="cancellation"></param>
        /// <returns>Return the check status result of the transaction.</returns>
        public async Task<ClassTransactionEnumStatus> CheckTransaction(ClassTransactionObject transactionObject, ClassBlockObject blockObjectSource, bool checkFromBlockData, Dictionary<string, string> listWalletAndPublicKeys, CancellationTokenSource cancellation, bool external)
        {
            #region Check transaction content.

            if (transactionObject.WalletAddressSender.IsNullOrEmpty())
            {
                return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_SENDER;
            }

            if (transactionObject.WalletAddressReceiver.IsNullOrEmpty())
            {
                return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_SENDER;
            }

            if (transactionObject.WalletAddressSender == transactionObject.WalletAddressReceiver)
            {
                return ClassTransactionEnumStatus.SAME_WALLET_ADDRESS;
            }

            if (transactionObject.TransactionHash.IsNullOrEmpty())
            {
                return ClassTransactionEnumStatus.INVALID_TRANSACTION_NULL_HASH;
            }

            if (transactionObject.TransactionHash.Length != BlockchainSetting.TransactionHashSize)
            {
                return ClassTransactionEnumStatus.INVALID_TRANSACTION_HASH;
            }

            // Check if the hash is in hex format.
            if (!ClassUtility.CheckHexStringFormat(transactionObject.TransactionHash))
            {
                return ClassTransactionEnumStatus.INVALID_TRANSACTION_FORMAT_HASH;
            }

            if (!transactionObject.TransactionHash.Any(char.IsUpper))
            {
                return ClassTransactionEnumStatus.INVALID_TRANSACTION_HASH;
            }

            if (ClassTransactionUtility.GetBlockHeightFromTransactionHash(transactionObject.TransactionHash) != transactionObject.BlockHeightTransaction)
            {
                return ClassTransactionEnumStatus.INVALID_TRANSACTION_HASH;
            }

            if (transactionObject.PaymentId < 0)
            {
                return ClassTransactionEnumStatus.INVALID_PAYMENT_ID;
            }

            if (transactionObject.TransactionVersion != BlockchainSetting.TransactionVersion)
            {
                return ClassTransactionEnumStatus.INVALID_TRANSACTION_VERSION;
            }

            #endregion

            #region Check block target confirmations.

            if (transactionObject.BlockHeightTransactionConfirmationTarget - transactionObject.BlockHeightTransaction < BlockchainSetting.TransactionMandatoryMinBlockTransactionConfirmations)
            {
                return ClassTransactionEnumStatus.INVALID_BLOCK_HEIGHT_TARGET_CONFIRMATION;
            }

            #endregion

            if (transactionObject.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION &&
                transactionObject.TransactionType != ClassTransactionEnumType.DEV_FEE_TRANSACTION)
            {
                if (transactionObject.Fee < BlockchainSetting.MinFeeTransaction)
                {
                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_FEE;
                }
            }

            if (transactionObject.Amount < BlockchainSetting.MinAmountTransaction)
            {
                return ClassTransactionEnumStatus.INVALID_TRANSACTION_AMOUNT;
            }

            if (!ClassTransactionUtility.CheckTransactionHash(transactionObject))
            {
                return ClassTransactionEnumStatus.INVALID_TRANSACTION_HASH;
            }

            switch (transactionObject.TransactionType)
            {
                case ClassTransactionEnumType.DEV_FEE_TRANSACTION:
                    {
                        if (!transactionObject.WalletPublicKeyReceiver.IsNullOrEmpty() ||
                            !transactionObject.WalletPublicKeySender.IsNullOrEmpty() ||
                            transactionObject.PaymentId != 0 ||
                            transactionObject.TimestampBlockHeightCreateSend != transactionObject.TimestampSend ||
                            !transactionObject.TransactionSignatureReceiver.IsNullOrEmpty() ||
                            !transactionObject.TransactionSignatureSender.IsNullOrEmpty())
                        {
                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_TYPE;
                        }

                        if (transactionObject.WalletAddressSender != BlockchainSetting.BlockRewardName)
                        {
                            return ClassTransactionEnumStatus.INVALID_BLOCK_REWARD_SENDER_NAME;
                        }

                        if (!ClassWalletUtility.CheckWalletAddress(transactionObject.WalletAddressReceiver))
                        {
                            return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_RECEIVER;
                        }

                        if (transactionObject.Amount != BlockchainSetting.BlockDevFee(transactionObject.BlockHeightTransaction))
                        {
                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_DEV_FEE_AMOUNT;
                        }

                        if (transactionObject.AmountTransactionSource != null)
                        {
                            if (transactionObject.AmountTransactionSource.Count > 0)
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                            }
                        }

                        if (checkFromBlockData)
                        {
                            if (blockObjectSource != null)
                            {
                                if (blockObjectSource.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                                {
                                    if (transactionObject.WalletAddressReceiver != BlockchainSetting.WalletAddressDev(transactionObject.TimestampSend))
                                    {
                                        return ClassTransactionEnumStatus.INVALID_BLOCK_DEV_FEE_WALLET_ADDRESS_RECEIVER;
                                    }

                                    if (transactionObject.BlockHeightTransaction > BlockchainSetting.GenesisBlockHeight)
                                    {
                                        if (transactionObject.BlockHash != blockObjectSource.BlockHash)
                                        {
                                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_HASH;
                                        }
                                        if (ClassUtility.GetHexStringFromByteArray(BitConverter.GetBytes(transactionObject.BlockHeightTransaction)) + ClassUtility.GenerateSha3512FromString(transactionObject.TransactionHashBlockReward) != transactionObject.TransactionHash)
                                        {
                                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_HASH;
                                        }

                                        if (transactionObject.BlockHeightTransactionConfirmationTarget - transactionObject.BlockHeightTransaction != BlockchainSetting.TransactionMandatoryBlockRewardConfirmations)
                                        {
                                            return ClassTransactionEnumStatus.INVALID_BLOCK_HEIGHT_TARGET_CONFIRMATION;
                                        }
                                    }
                                }
                                else
                                {
                                    return ClassTransactionEnumStatus.BLOCK_HEIGHT_LOCKED;
                                }
                            }
                            else
                            {
                                return ClassTransactionEnumStatus.INVALID_BLOCK_HEIGHT;
                            }
                        }
                    }
                    break;
                case ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION:
                    {
                        if (transactionObject.BlockHeightTransaction > BlockchainSetting.GenesisBlockHeight)
                        {
                            if (!transactionObject.WalletPublicKeyReceiver.IsNullOrEmpty() ||
                                !transactionObject.WalletPublicKeySender.IsNullOrEmpty() ||
                                transactionObject.PaymentId != 0 ||
                                transactionObject.TimestampBlockHeightCreateSend != transactionObject.TimestampSend ||
                                !transactionObject.TransactionSignatureReceiver.IsNullOrEmpty() ||
                                !transactionObject.TransactionSignatureSender.IsNullOrEmpty())
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_TYPE;
                            }
                        }
                        else
                        {
                            if (!transactionObject.WalletPublicKeyReceiver.IsNullOrEmpty() ||
                                transactionObject.PaymentId != 0 ||
                                transactionObject.TimestampBlockHeightCreateSend != transactionObject.TimestampSend ||
                                !transactionObject.TransactionSignatureReceiver.IsNullOrEmpty())
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_TYPE;
                            }
                        }

                        if (transactionObject.WalletAddressSender != BlockchainSetting.BlockRewardName)
                        {
                            return ClassTransactionEnumStatus.INVALID_BLOCK_REWARD_SENDER_NAME;
                        }

                        if (!ClassWalletUtility.CheckWalletAddress(transactionObject.WalletAddressReceiver))
                        {
                            return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_RECEIVER;
                        }

                        if (transactionObject.AmountTransactionSource != null)
                        {
                            if (transactionObject.AmountTransactionSource.Count > 0)
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                            }
                        }

                        if (transactionObject.BlockHeightTransaction > BlockchainSetting.GenesisBlockHeight)
                        {
                            if (BlockchainSetting.BlockDevFee(transactionObject.BlockHeightTransaction) > 0)
                            {
                                if (transactionObject.Amount < BlockchainSetting.BlockRewardWithDevFee(transactionObject.BlockHeightTransaction) || transactionObject.Amount > BlockchainSetting.BlockRewardWithDevFee(transactionObject.BlockHeightTransaction))
                                {
                                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_BLOCK_AMOUNT;
                                }
                                if (transactionObject.Fee != BlockchainSetting.BlockDevFee(transactionObject.BlockHeightTransaction))
                                {
                                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_DEV_FEE_AMOUNT;
                                }
                            }
                            else
                            {
                                if (transactionObject.Amount != BlockchainSetting.BlockReward(transactionObject.BlockHeightTransaction))
                                {
                                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_BLOCK_AMOUNT;
                                }
                                // Always 0 timestamp.
                                if (transactionObject.WalletAddressReceiver != BlockchainSetting.WalletAddressDev(0))
                                {
                                    return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_RECEIVER;
                                }
                            }

                            if (transactionObject.BlockHeightTransactionConfirmationTarget - transactionObject.BlockHeightTransaction != BlockchainSetting.TransactionMandatoryBlockRewardConfirmations)
                            {
                                return ClassTransactionEnumStatus.INVALID_BLOCK_HEIGHT_TARGET_CONFIRMATION;
                            }
                        }
                        else
                        {
                            if (transactionObject.Amount != BlockchainSetting.GenesisBlockAmount)
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_AMOUNT;
                            }

                            if (checkFromBlockData)
                            {
                                if (blockObjectSource != null)
                                {
                                    if (transactionObject.WalletAddressReceiver != blockObjectSource.BlockWalletAddressWinner)
                                    {
                                        return ClassTransactionEnumStatus.INVALID_BLOCK_WALLET_ADDRESS_WINNER;
                                    }
                                }
                                else
                                {
                                    return ClassTransactionEnumStatus.INVALID_BLOCK_HEIGHT;
                                }
                            }
                            else
                            {
                                if (ClassBlockUtility.GetFinalTransactionHashList(new List<string>() { transactionObject.TransactionHash }, string.Empty) != BlockchainSetting.GenesisBlockFinalTransactionHash)
                                {
                                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_HASH;
                                }
                            }

                            #region Check signature(s) with public key(s).

                            if (!ClassWalletUtility.WalletCheckSignature(transactionObject.TransactionHash, transactionObject.TransactionSignatureSender, BlockchainSetting.WalletAddressDevPublicKey(transactionObject.TimestampSend)))
                            {
#if DEBUG
                                Debug.WriteLine("Transaction signature invalid for transaction hash: " + transactionObject.TransactionHash);
#endif
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SIGNATURE;
                            }


                            #endregion
                        }


                        if (checkFromBlockData)
                        {
                            if (blockObjectSource != null)
                            {

                                if (blockObjectSource.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                                {
                                    if (transactionObject.BlockHeightTransaction > BlockchainSetting.GenesisBlockHeight)
                                    {
                                        if (transactionObject.BlockHash != blockObjectSource.BlockHash)
                                        {
                                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_HASH;
                                        }
                                    }
                                }
                                else
                                {
                                    return ClassTransactionEnumStatus.BLOCK_HEIGHT_LOCKED;
                                }
                            }
                            else
                            {
                                return ClassTransactionEnumStatus.INVALID_BLOCK_HEIGHT;
                            }

                        }
                    }
                    break;
                case ClassTransactionEnumType.NORMAL_TRANSACTION:
                case ClassTransactionEnumType.TRANSFER_TRANSACTION:
                    {
                        bool checkAddressSender = false;

                        if (listWalletAndPublicKeys != null)
                        {
                            if (listWalletAndPublicKeys.ContainsKey(transactionObject.WalletAddressSender))
                            {
                                if (listWalletAndPublicKeys[transactionObject.WalletAddressSender] == transactionObject.WalletPublicKeySender)
                                {
                                    checkAddressSender = true;
                                }
                                else
                                {
                                    return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_SENDER;
                                }
                            }
                        }

                        if (!checkAddressSender)
                        {
                            if (!ClassWalletUtility.CheckWalletAddress(transactionObject.WalletAddressSender))
                            {
                                return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_SENDER;
                            }

                            if (!ClassWalletUtility.CheckWalletPublicKey(transactionObject.WalletPublicKeySender))
                            {
                                return ClassTransactionEnumStatus.INVALID_WALLET_PUBLIC_KEY;
                            }
                        }

                        bool checkAddressReceiver = false;

                        if (transactionObject.TransactionType == ClassTransactionEnumType.TRANSFER_TRANSACTION)
                        {
                            if (listWalletAndPublicKeys != null)
                            {
                                if (listWalletAndPublicKeys.ContainsKey(transactionObject.WalletAddressReceiver))
                                {
                                    if (listWalletAndPublicKeys[transactionObject.WalletAddressReceiver] == transactionObject.WalletPublicKeyReceiver)
                                    {
                                        checkAddressReceiver = true;
                                    }
                                    else
                                    {
                                        return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_RECEIVER;
                                    }
                                }
                            }
                        }

                        if (!checkAddressReceiver)
                        {
                            if (!ClassWalletUtility.CheckWalletAddress(transactionObject.WalletAddressReceiver))
                            {
                                return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_RECEIVER;
                            }
                            if (transactionObject.TransactionType == ClassTransactionEnumType.TRANSFER_TRANSACTION)
                            {
                                if (!ClassWalletUtility.CheckWalletPublicKey(transactionObject.WalletPublicKeyReceiver))
                                {
                                    return ClassTransactionEnumStatus.INVALID_WALLET_PUBLIC_KEY;
                                }
                            }
                        }

                        if (transactionObject.AmountTransactionSource != null)
                        {
                            if (transactionObject.AmountTransactionSource.Count == 0)
                            {
                                return ClassTransactionEnumStatus.EMPTY_TRANSACTION_SOURCE_LIST;
                            }

                            if (transactionObject.AmountTransactionSource.Count(x => x.Key.IsNullOrEmpty()) > 0)
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                            }

                            if (transactionObject.AmountTransactionSource.Count(x => x.Value == null) > 0)
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                            }

                            if (transactionObject.AmountTransactionSource.Count(x => x.Value.Amount < BlockchainSetting.MinAmountTransaction) > 0)
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SOURCE_LIST;
                            }
                        }
                        else
                        {
                            return ClassTransactionEnumStatus.EMPTY_TRANSACTION_SOURCE_LIST;
                        }

                        #region Check Base 64 Signature formatting.

                        if (!ClassUtility.CheckBase64String(transactionObject.TransactionSignatureSender))
                        {
                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_SIGNATURE;
                        }

                        if (!ClassUtility.CheckBase64String(transactionObject.TransactionBigSignatureSender))
                        {
                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_SIGNATURE;
                        }

                        if (transactionObject.TransactionType == ClassTransactionEnumType.TRANSFER_TRANSACTION)
                        {
                            if (!ClassUtility.CheckBase64String(transactionObject.TransactionSignatureReceiver))
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SIGNATURE;
                            }

                            if (!ClassUtility.CheckBase64String(transactionObject.TransactionBigSignatureReceiver))
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SIGNATURE;
                            }
                        }
                        #endregion

                        #region Check Fee.

                        if (checkFromBlockData)
                        {
                            long blockHeightSendExpected = GetCloserBlockHeightFromTimestamp(transactionObject.TimestampBlockHeightCreateSend, cancellation);

                            long blockHeightConfirmationStartExpected = await ClassTransactionUtility.GenerateBlockHeightStartTransactionConfirmation(blockHeightSendExpected - 1, blockHeightSendExpected, cancellation);

                            long blockHeightStartConfirmationExpected = blockHeightConfirmationStartExpected + (transactionObject.BlockHeightTransactionConfirmationTarget - transactionObject.BlockHeightTransaction);

                            if (blockHeightConfirmationStartExpected != transactionObject.BlockHeightTransaction)
                            {
                                return ClassTransactionEnumStatus.INVALID_BLOCK_HEIGHT_TARGET_CONFIRMATION;
                            }

                            long blockHeightUnlockedExpected = blockHeightSendExpected - 1;

                            var feeCostFromBlockchainActivity = await ClassTransactionUtility.GetFeeCostFromWholeBlockchainTransactionActivity(blockHeightUnlockedExpected, transactionObject.BlockHeightTransaction, transactionObject.BlockHeightTransactionConfirmationTarget, cancellation);

                            if (!feeCostFromBlockchainActivity.Item2)
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_FEE;
                            }

                            var feeCostTransaction = ClassTransactionUtility.GetFeeCostSizeFromTransactionData(transactionObject) + feeCostFromBlockchainActivity.Item1;

                            if (feeCostTransaction > transactionObject.Fee)
                            {
#if DEBUG
                                Debug.WriteLine("Invalid fee from transaction: " + transactionObject.TransactionHash + ". Calculated: " + feeCostTransaction + " | TX: " + transactionObject.Fee);
#endif
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_FEE;
                            }
                        }
                        else
                        {
                            if (transactionObject.Fee < BlockchainSetting.MinFeeTransaction)
                            {
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_FEE;
                            }
                        }


                        #endregion

                        #region Check Public Keys results.

                        if (!checkAddressSender)
                        {
                            if (ClassWalletUtility.GenerateWalletAddressFromPublicKey(transactionObject.WalletPublicKeySender) != transactionObject.WalletAddressSender)
                            {
                                ClassLog.WriteLine("Transaction public key sender not return the same wallet address of sending: " + transactionObject.WalletAddressSender, ClassEnumLogLevelType.LOG_LEVEL_GENERAL, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);
                                return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_SENDER_FROM_PUBLIC_KEY;
                            }
                        }

                        if (transactionObject.TransactionType == ClassTransactionEnumType.TRANSFER_TRANSACTION)
                        {
                            if (!checkAddressReceiver)
                            {
                                if (ClassWalletUtility.GenerateWalletAddressFromPublicKey(transactionObject.WalletPublicKeyReceiver) != transactionObject.WalletAddressReceiver)
                                {
                                    ClassLog.WriteLine("Transaction public key receiver not return the same wallet address of receive: " + transactionObject.WalletAddressReceiver, ClassEnumLogLevelType.LOG_LEVEL_GENERAL, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);
                                    return ClassTransactionEnumStatus.INVALID_WALLET_ADDRESS_RECEIVER_FROM_PUBLIC_KEY;
                                }
                            }
                        }

                        #endregion

                        #region Check signature(s) with public key(s).

                        if (!ClassWalletUtility.WalletCheckSignature(transactionObject.TransactionHash, transactionObject.TransactionSignatureSender, transactionObject.WalletPublicKeySender))
                        {
#if DEBUG
                            Debug.WriteLine("Transaction signature invalid for transaction hash: " + transactionObject.TransactionHash);
#endif
                            return ClassTransactionEnumStatus.INVALID_TRANSACTION_SIGNATURE;
                        }

                        if (transactionObject.TransactionType == ClassTransactionEnumType.TRANSFER_TRANSACTION)
                        {
                            if (!ClassWalletUtility.WalletCheckSignature(transactionObject.TransactionHash, transactionObject.TransactionSignatureReceiver, transactionObject.WalletPublicKeyReceiver))
                            {
#if DEBUG
                                Debug.WriteLine("Transaction signature invalid for transaction hash: " + transactionObject.TransactionHash);
#endif
                                return ClassTransactionEnumStatus.INVALID_TRANSACTION_SIGNATURE;
                            }
                        }

                        // Check only big signature to reduce cpu cost once the block is provided and don't have pass any confirmation task.
                        if (blockObjectSource != null)
                        {
                            if (blockObjectSource.BlockTotalTaskTransactionConfirmationDone == 0)
                            {
                                if (!ClassTransactionUtility.CheckBigTransactionSignature(transactionObject, cancellation))
                                {
#if DEBUG
                                    Debug.WriteLine("Transaction big signature invalid for transaction hash: " + transactionObject.TransactionHash);
#endif
                                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_SIGNATURE;
                                }
                            }
                        }
                        #endregion
                    }
                    break;
                default:
                    return ClassTransactionEnumStatus.INVALID_TRANSACTION_TYPE;
            }

            return ClassTransactionEnumStatus.VALID_TRANSACTION;
        }

        #endregion

        #region Misc functions who can be managed with the cache system.

        /// <summary>
        /// Check the block mined.
        /// </summary>
        /// <param name="blockObject"></param>
        /// <param name="previousBlockObject"></param>
        /// <returns></returns>
        private bool CheckBlockMinedShare(ClassBlockObject blockObject, ClassBlockObject previousBlockObject)
        {
            if (blockObject.BlockUnlockValid && blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
            {
                if (blockObject.BlockMiningPowShareUnlockObject != null)
                {
                    string blockHash = blockObject.BlockHash;
                    BigInteger blockDifficulty = blockObject.BlockDifficulty;
                    string walletAddressWinner = blockObject.BlockWalletAddressWinner;
                    ClassMiningPoWaCShareObject miningPocShareObject = blockObject.BlockMiningPowShareUnlockObject;

                    if (walletAddressWinner == miningPocShareObject.WalletAddress)
                    {
                        string previousFinalBlockTransactionHash = previousBlockObject.BlockFinalHashTransaction;

                        int previousBlockTransactionCount = previousBlockObject.BlockTransactions.Count;

                        var resultShare = ClassMiningPoWaCUtility.CheckPoWaCShare(BlockchainSetting.CurrentMiningPoWaCSettingObject(blockObject.BlockHeight), miningPocShareObject, blockObject.BlockHeight, blockHash, blockDifficulty, previousBlockTransactionCount, previousFinalBlockTransactionHash, out BigInteger jobDifficulty, out int jobCompabilityValue);

                        if (resultShare == ClassMiningPoWaCEnumStatus.VALID_UNLOCK_BLOCK_SHARE)
                        {
                            if (jobDifficulty == miningPocShareObject.PoWaCShareDifficulty && jobCompabilityValue == previousBlockTransactionCount)
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Calculate a wallet balance from blockchain data.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="maxBlockHeightTarget"></param>
        /// <param name="useCheckpoint"></param>
        /// <param name="buildCheckpoint"></param>
        /// <param name="isWallet"></param>
        /// <param name="useSemaphore"></param>
        /// <param name="cancellation"></param>
        /// <returns>Return the wallet balance.</returns>
        public async Task<ClassBlockchainWalletBalanceCalculatedObject> GetWalletBalanceFromTransaction(string walletAddress, long maxBlockHeightTarget, bool useCheckpoint, bool buildCheckpoint, bool isWallet, bool useSemaphore, CancellationTokenSource cancellation)
        {
            ClassBlockchainWalletBalanceCalculatedObject blockchainWalletBalance = new ClassBlockchainWalletBalanceCalculatedObject { WalletBalance = 0, WalletPendingBalance = 0 };

            bool semaphoreEnabled = false;

            try
            {

                if (useSemaphore)
                {
                    if (cancellation != null)
                    {
                        await _semaphoreSlimGetWalletBalance.WaitAsync(cancellation.Token);
                    }
                    else
                    {
                        await _semaphoreSlimGetWalletBalance.WaitAsync();
                    }
                    semaphoreEnabled = true;
                }

                try
                {
                    if (useCheckpoint)
                    {
                        #region Use checkpoint for speed up the wallet balance calculation.

                        long lastBlockHeightWalletCheckpoint = 0;

                        BlockchainWalletMemoryObject blockchainWalletMemoryObject = null;

                        if (BlockchainWalletIndexMemoryCacheObject != null)
                        {
                            if (BlockchainWalletIndexMemoryCacheObject.ContainsKey(walletAddress, cancellation, out blockchainWalletMemoryObject))
                            {

                                if (!isWallet)
                                {
                                    foreach (var blockHeight in blockchainWalletMemoryObject.GetListWalletBalanceBlockHeightCheckPoint())
                                    {
                                        if (blockHeight >= BlockchainSetting.GenesisBlockHeight && blockHeight <= maxBlockHeightTarget)
                                        {

                                            lastBlockHeightWalletCheckpoint = blockHeight;

                                        }
                                        else
                                        {
                                            break;
                                        }
                                    }
                                }
                                else
                                {
                                    lastBlockHeightWalletCheckpoint = blockchainWalletMemoryObject.GetLastBlockHeightCheckPoint();
                                }

                                if (lastBlockHeightWalletCheckpoint > 0)
                                {
                                    blockchainWalletBalance.WalletBalance = blockchainWalletMemoryObject.GetWalletBalanceCheckpoint(lastBlockHeightWalletCheckpoint);
                                }
                            }
                        }

                        long lastBlockHeightFromTransaction = 0;

                        int totalTx = 0;

                        bool allTxConfirmed = true;

                        // Retrieve back all block transaction from the list of tx hash of the wallet address target.
                        Dictionary<long, List<ClassBlockTransaction>> listTxFromCacheOrMemory = new Dictionary<long, List<ClassBlockTransaction>>();

                        // Generate a list of block heights if each list retrieved have a tx linked to the wallet address.
                        for (long i = lastBlockHeightFromTransaction; i < maxBlockHeightTarget; i++)
                        {
                            long blockHeight = i;

                            if (blockHeight >= BlockchainSetting.GenesisBlockHeight)
                            {
                                bool containsBlockHeight = false;

                                ClassBlockObject blockObject = await GetBlockDataStrategy(blockHeight, true, cancellation);

                                if (blockObject != null)
                                {
                                    if (blockObject.BlockTransactions?.Count > 0)
                                    {
                                        foreach (var transactionPair in blockObject.BlockTransactions)
                                        {
                                            if (transactionPair.Value.TransactionStatus)
                                            {
                                                if (transactionPair.Value.TransactionObject.WalletAddressSender == walletAddress
                                                    || transactionPair.Value.TransactionObject.WalletAddressReceiver == walletAddress)
                                                {
                                                    if (!containsBlockHeight)
                                                    {
                                                        listTxFromCacheOrMemory.Add(blockHeight, new List<ClassBlockTransaction>());
                                                        containsBlockHeight = true;
                                                    }

                                                    listTxFromCacheOrMemory[blockHeight].Add(transactionPair.Value);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (listTxFromCacheOrMemory.Count > 0)
                        {
                            // Travel all block height and transactions hash indexed to the wallet address.
                            foreach (var blockHeight in listTxFromCacheOrMemory.Keys.OrderBy(x => x))
                            {
                                if (blockHeight > lastBlockHeightWalletCheckpoint && blockHeight <= maxBlockHeightTarget)
                                {
                                    if (listTxFromCacheOrMemory[blockHeight].Count > 0)
                                    {
                                        foreach (ClassBlockTransaction blockTransaction in listTxFromCacheOrMemory[blockHeight].OrderBy(x => x.TransactionObject.TimestampSend))
                                        {
                                            if (blockTransaction != null)
                                            {
                                                if (blockTransaction.TransactionStatus)
                                                {
                                                    if (blockTransaction.TransactionBlockHeightInsert <= maxBlockHeightTarget)
                                                    {
                                                        bool txConfirmed = false;

                                                        if (blockTransaction.TransactionTotalConfirmation >= BlockchainSetting.TransactionMandatoryMinBlockTransactionConfirmations)
                                                        {
                                                            long totalConfirmationToReach = blockTransaction.TransactionBlockHeightTarget - blockTransaction.TransactionBlockHeightInsert;

                                                            if (blockTransaction.TransactionTotalConfirmation >= totalConfirmationToReach)
                                                            {
                                                                txConfirmed = true;
                                                                totalTx++;
                                                            }
                                                        }

                                                        if (txConfirmed)
                                                        {
                                                            int typeTx = 0;

                                                            if (blockTransaction.TransactionObject.WalletAddressReceiver == walletAddress)
                                                            {
                                                                typeTx = 1;
                                                            }
                                                            else if (blockTransaction.TransactionObject.WalletAddressSender == walletAddress)
                                                            {
                                                                typeTx = 2;
                                                            }

                                                            switch (typeTx)
                                                            {
                                                                // Received.
                                                                case 1:
                                                                    blockchainWalletBalance.WalletBalance += blockTransaction.TransactionObject.Amount;
                                                                    break;
                                                                // Sent.
                                                                case 2:
                                                                    blockchainWalletBalance.WalletBalance -= blockTransaction.TransactionObject.Amount;
                                                                    if (blockTransaction.TransactionObject.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION)
                                                                    {
                                                                        blockchainWalletBalance.WalletBalance -= blockTransaction.TransactionObject.Fee;
                                                                    }
                                                                    break;
                                                            }
                                                        }
                                                        //  Take in count only sent transactions. Received tx not confirmed not increment the balance.
                                                        else
                                                        {
                                                            //allTxConfirmed = false;
                                                            if (blockTransaction.TransactionObject.WalletAddressSender == walletAddress)
                                                            {
                                                                blockchainWalletBalance.WalletBalance -= blockTransaction.TransactionObject.Amount;
                                                                if (blockTransaction.TransactionObject.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION)
                                                                {
                                                                    blockchainWalletBalance.WalletBalance -= blockTransaction.TransactionObject.Fee;
                                                                }
                                                            }
                                                            if (blockTransaction.TransactionObject.WalletAddressReceiver == walletAddress)
                                                            {
                                                                blockchainWalletBalance.WalletPendingBalance += blockTransaction.TransactionObject.Amount;
                                                            }
                                                        }


                                                        if (blockHeight > lastBlockHeightFromTransaction)
                                                        {
                                                            lastBlockHeightFromTransaction = blockHeight;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }

                            // Clean up.
                            listTxFromCacheOrMemory.Clear();
                        }

                        // Do a wallet balance checkpoint only if all blocks transactions travelled are confirmed.
                        if (!isWallet)
                        {
                            if (buildCheckpoint && blockchainWalletMemoryObject != null)
                            {
                                if (allTxConfirmed && totalTx > 0)
                                {
                                    long blockHeightDifference = lastBlockHeightFromTransaction - blockchainWalletMemoryObject.GetLastBlockHeightCheckPoint();

                                    if (blockHeightDifference >= BlockchainSetting.TaskVirtualWalletBalanceCheckpoint)
                                    {
                                        if (!blockchainWalletMemoryObject.ContainsBlockHeightCheckpoint(lastBlockHeightFromTransaction))
                                        {
                                            blockchainWalletMemoryObject.InsertWalletBalanceCheckpoint(lastBlockHeightFromTransaction, blockchainWalletBalance.WalletBalance, blockchainWalletBalance.WalletPendingBalance, totalTx, walletAddress);
                                            BlockchainWalletIndexMemoryCacheObject.UpdateWalletIndexData(walletAddress, blockchainWalletMemoryObject, cancellation);
                                        }
                                    }
                                }
                            }
                        }

                        #endregion
                    }
                    else
                    {
                        #region Slower way. do not use wallet balance checkpoint.

#if DEBUG
                        if (isWallet)
                        {
                            Debug.WriteLine(walletAddress + " use slow way to calculate his balance.");
                        }
                        else
                        {
                            Debug.WriteLine("Internal system use slow way to calculate a wallet balance.");
                        }
#endif
                        for (long i = 0; i < maxBlockHeightTarget; i++)
                        {
                            long blockIndex = i + 1;
                            if (blockIndex >= BlockchainSetting.GenesisBlockHeight && blockIndex <= maxBlockHeightTarget)
                            {
                                foreach (var transactionPair in await GetTransactionListFromBlockHeightTargetFromMemoryDataCache(blockIndex, true, cancellation))
                                {
                                    if (transactionPair.Value != null)
                                    {
                                        if (transactionPair.Value.TransactionStatus)
                                        {
                                            if (transactionPair.Value.TransactionBlockHeightInsert <= maxBlockHeightTarget)
                                            {
                                                bool txConfirmed = false;

                                                if (transactionPair.Value.TransactionTotalConfirmation >= BlockchainSetting.TransactionMandatoryMinBlockTransactionConfirmations)
                                                {
                                                    long totalConfirmationToReach = transactionPair.Value.TransactionBlockHeightTarget - transactionPair.Value.TransactionBlockHeightInsert;

                                                    if (transactionPair.Value.TransactionTotalConfirmation >= totalConfirmationToReach)
                                                    {
                                                        txConfirmed = true;
                                                    }
                                                }

                                                if (txConfirmed)
                                                {
                                                    int typeTx = 0;

                                                    if (transactionPair.Value.TransactionObject.WalletAddressReceiver == walletAddress)
                                                    {
                                                        typeTx = 1;
                                                    }
                                                    else if (transactionPair.Value.TransactionObject.WalletAddressSender == walletAddress)
                                                    {
                                                        typeTx = 2;
                                                    }

                                                    switch (typeTx)
                                                    {
                                                        // Received.
                                                        case 1:
                                                            blockchainWalletBalance.WalletBalance += transactionPair.Value.TransactionObject.Amount;
                                                            break;
                                                        // Sent.
                                                        case 2:
                                                            blockchainWalletBalance.WalletBalance -= transactionPair.Value.TransactionObject.Amount;
                                                            if (transactionPair.Value.TransactionObject.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION)
                                                            {
                                                                blockchainWalletBalance.WalletBalance -= transactionPair.Value.TransactionObject.Fee;
                                                            }
                                                            break;
                                                    }
                                                }
                                                //  Take in count only sent transactions. Received tx not confirmed not increment the balance.
                                                else
                                                {
                                                    if (transactionPair.Value.TransactionObject.WalletAddressSender == walletAddress)
                                                    {
                                                        blockchainWalletBalance.WalletBalance -= transactionPair.Value.TransactionObject.Amount;
                                                        if (transactionPair.Value.TransactionObject.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION)
                                                        {
                                                            blockchainWalletBalance.WalletBalance -= transactionPair.Value.TransactionObject.Fee;
                                                        }
                                                    }
                                                    if (transactionPair.Value.TransactionObject.WalletAddressReceiver == walletAddress)
                                                    {
                                                        blockchainWalletBalance.WalletPendingBalance += transactionPair.Value.TransactionObject.Amount;
                                                    }
                                                }

                                            }
                                        }
                                    }
                                }

                            }

                        }

                        #endregion
                    }

                    // Take in count mem pool transaction indexed only sending.
                    if (ClassMemPoolDatabase.GetCountMemPoolTx > 0)
                    {
                        foreach (var memPoolTransactionIndexed in ClassMemPoolDatabase.GetMemPoolTxFromWalletAddressTarget(walletAddress, maxBlockHeightTarget, cancellation))
                        {
                            if (memPoolTransactionIndexed != null)
                            {
                                if (memPoolTransactionIndexed.WalletAddressSender == walletAddress)
                                {
                                    blockchainWalletBalance.WalletBalance -= memPoolTransactionIndexed.Amount;
                                    if (memPoolTransactionIndexed.TransactionType != ClassTransactionEnumType.BLOCK_REWARD_TRANSACTION)
                                    {
                                        blockchainWalletBalance.WalletBalance -= memPoolTransactionIndexed.Fee;
                                    }
                                }
                                else if (memPoolTransactionIndexed.WalletAddressReceiver == walletAddress)
                                {
                                    blockchainWalletBalance.WalletPendingBalance += memPoolTransactionIndexed.Amount;
                                }
                            }
                        }
                    }

                    if (semaphoreEnabled)
                    {
                        _semaphoreSlimGetWalletBalance.Release();
                        semaphoreEnabled = false;
                    }
                }
                catch (Exception error)
                {
                    // On exception, set the balance and the pending balance at an invalid amount.
                    blockchainWalletBalance.WalletBalance -= 1;
                    blockchainWalletBalance.WalletPendingBalance -= 1;
#if DEBUG
                    Debug.WriteLine("Error on calculating the wallet balance of " + walletAddress + ". Exception: " + error.Message);
#endif

                    ClassLog.WriteLine("Error on calculating the wallet balance of " + walletAddress + ". Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_GENERAL, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, true);
                }
            }
            finally
            {
                if (useSemaphore)
                {
                    if (semaphoreEnabled)
                    {
                        _semaphoreSlimGetWalletBalance.Release();
                    }
                }
            }



            return blockchainWalletBalance;
        }

        #endregion

        #endregion


        #region Functions to manage memory.

        /// <summary>
        /// Task who automatically save objects in memory not used into cache disk.
        /// </summary>
        public void StartTaskManageActiveMemory()
        {
            try
            {
                Task.Factory.StartNew(async () =>
                {
                    bool useSemaphoreMemory = false;
                    bool useSemaphoreUpdateTransactionConfirmation = false;
                    while (_cacheStatus)
                    {
                        _cancellationTokenMemoryManagement.Token.ThrowIfCancellationRequested();

                        if (!_pauseMemoryManagement)
                        {
                            try
                            {
                                try
                                {
                                    if (Count > 0)
                                    {
                                        await _semaphoreSlimUpdateTransactionConfirmations.WaitAsync(_cancellationTokenMemoryManagement.Token);
                                        useSemaphoreUpdateTransactionConfirmation = true;

                                        await _semaphoreSlimMemoryAccess.WaitAsync(_cancellationTokenMemoryManagement.Token);
                                        useSemaphoreMemory = true;


                                        long timestamp = ClassUtility.GetCurrentTimestampInSecond();
                                        long lastBlockHeight = GetLastBlockHeight;
                                        long limitIndexToCache = lastBlockHeight - _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalMaxBlockCountToKeepInMemory;
                                        bool changeDone = false;

                                        // Update the memory depending of the cache system selected.
                                        switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                                        {
                                            case CacheEnumName.IO_CACHE:
                                                {
                                                    Dictionary<long, bool> dictionaryCache = new Dictionary<long, bool>();

                                                    #region List blocks to update on the cache and list blocks to push out of the memory.

                                                    for (long i = 0; i < lastBlockHeight; i++)
                                                    {
                                                        if (i < lastBlockHeight)
                                                        {
                                                            long blockHeight = i + 1;

                                                            if (ContainsKey(blockHeight))
                                                            {
                                                                if (_pauseMemoryManagement || _cancellationTokenMemoryManagement.IsCancellationRequested)
                                                                {
                                                                    break;
                                                                }

                                                                if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                                                                {
                                                                    #region Insert/Update data cache from an element of memory recently updated. 

                                                                    // Ignore locked block.
                                                                    if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                                                                    {
                                                                        // Used and updated frequently, update disk data to keep changes if a crash happen.
                                                                        if ((_dictionaryBlockObjectMemory[blockHeight].Content.BlockLastChangeTimestamp + _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalObjectCacheUpdateLimitTime >= timestamp ||
                                                                            !_dictionaryBlockObjectMemory[blockHeight].ObjectIndexed ||
                                                                            !_dictionaryBlockObjectMemory[blockHeight].CacheUpdated) &&
                                                                            (_dictionaryBlockObjectMemory[blockHeight].Content.BlockStatus == ClassBlockEnumStatus.UNLOCKED &&
                                                                            _dictionaryBlockObjectMemory[blockHeight].Content.BlockUnlockValid &&
                                                                            _dictionaryBlockObjectMemory[blockHeight].Content.BlockNetworkAmountConfirmations >= BlockchainSetting.BlockAmountNetworkConfirmations
                                                                            && blockHeight >= limitIndexToCache) || (_dictionaryBlockObjectMemory[blockHeight].Content.BlockHeight == BlockchainSetting.GenesisBlockHeight))
                                                                        {
                                                                            dictionaryCache.Add(blockHeight, false);
                                                                        }
                                                                        // Unused elements.
                                                                        else
                                                                        {

                                                                            if (blockHeight < limitIndexToCache &&
                                                                                _dictionaryBlockObjectMemory[blockHeight].Content.BlockStatus == ClassBlockEnumStatus.UNLOCKED &&
                                                                                _dictionaryBlockObjectMemory[blockHeight].Content.BlockUnlockValid &&
                                                                                _dictionaryBlockObjectMemory[blockHeight].Content.BlockNetworkAmountConfirmations >= BlockchainSetting.BlockAmountNetworkConfirmations
                                                                                && _dictionaryBlockObjectMemory[blockHeight].Content.BlockHeight != BlockchainSetting.GenesisBlockHeight)
                                                                            {
                                                                                if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockLastChangeTimestamp + _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalObjectExpirationMemoryCached <= timestamp)
                                                                                {
                                                                                    dictionaryCache.Add(blockHeight, true);
                                                                                }
                                                                            }

                                                                        }
                                                                    }

                                                                    #endregion
                                                                }
                                                            }
                                                        }
                                                    }

                                                    #endregion

                                                    #region Push data updated to the cache, released old data from the active memory.

                                                    if (dictionaryCache.Count > 0)
                                                    {

                                                        List<ClassBlockObject> blockObjectToCacheOut = new List<ClassBlockObject>();
                                                        List<ClassBlockObject> blockObjectToCacheUpdate = new List<ClassBlockObject>();

                                                        #region Push blocks to put out of memory to the cache system.

                                                        int countBlockOutOfMemory = dictionaryCache.Count(x => x.Value);

                                                        if (countBlockOutOfMemory > 0)
                                                        {
                                                            foreach (var blockPair in dictionaryCache.Where(x => x.Value))
                                                            {
                                                                _dictionaryBlockObjectMemory[blockPair.Key].Content.DeepCloneBlockObject(true, out ClassBlockObject blockObjectCopy);
                                                                blockObjectToCacheOut.Add(blockObjectCopy);
                                                            }

                                                            if (await AddOrUpdateListBlockObjectOnMemoryDataCache(blockObjectToCacheOut, true, _cancellationTokenMemoryManagement))
                                                            {
                                                                foreach (var blockPair in dictionaryCache.Where(x => x.Value))
                                                                {
                                                                    _dictionaryBlockObjectMemory[blockPair.Key].CacheUpdated = true;
                                                                    _dictionaryBlockObjectMemory[blockPair.Key].ObjectIndexed = true;
                                                                    _dictionaryBlockObjectMemory[blockPair.Key].ObjectCacheType = CacheBlockMemoryEnumState.IN_PERSISTENT_CACHE;
                                                                    _dictionaryBlockObjectMemory[blockPair.Key].Content = null;
                                                                }
                                                                changeDone = true;
#if DEBUG
                                                                Debug.WriteLine("Total block object(s) cached out of memory: " + countBlockOutOfMemory);
#endif
                                                                ClassLog.WriteLine("Memory management - Total block object(s) cached out of memory: " + countBlockOutOfMemory, ClassEnumLogLevelType.LOG_LEVEL_MEMORY_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);

                                                            }

                                                            // Clean up.
                                                            blockObjectToCacheOut.Clear();
                                                        }

                                                        #endregion

                                                        #region Push updated blocks to the cache system.

                                                        int countBlockToUpdate = dictionaryCache.Count(x => x.Value == false);

                                                        if (countBlockToUpdate > 0)
                                                        {
                                                            foreach (var blockPair in dictionaryCache.Where(x => x.Value == false))
                                                            {
                                                                _dictionaryBlockObjectMemory[blockPair.Key].Content.DeepCloneBlockObject(true, out ClassBlockObject blockObjectCopy);
                                                                blockObjectToCacheUpdate.Add(blockObjectCopy);
                                                            }

                                                            if (await AddOrUpdateListBlockObjectOnMemoryDataCache(blockObjectToCacheUpdate, true, _cancellationTokenMemoryManagement))
                                                            {
                                                                changeDone = true;
                                                                foreach (var blockPair in dictionaryCache.Where(x => x.Value == false))
                                                                {
                                                                    _dictionaryBlockObjectMemory[blockPair.Key].CacheUpdated = true;
                                                                    _dictionaryBlockObjectMemory[blockPair.Key].ObjectIndexed = true;
                                                                    _dictionaryBlockObjectMemory[blockPair.Key].ObjectCacheType = CacheBlockMemoryEnumState.IN_CACHE;
                                                                }
#if DEBUG
                                                                Debug.WriteLine("Total block object(s) cached updated and keep in memory: " + countBlockToUpdate);
#endif
                                                                ClassLog.WriteLine("Memory management - Total block object(s) cached updated and keep in memory: " + countBlockToUpdate, ClassEnumLogLevelType.LOG_LEVEL_MEMORY_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);

                                                            }

                                                            // Clean up.
                                                            blockObjectToCacheUpdate.Clear();
                                                        }

                                                        #endregion

                                                        // Clean up.
                                                        dictionaryCache.Clear();

                                                        if (changeDone)
                                                        {
                                                            // Purge the IO Cache system.
                                                            await _cacheIoSystem.PurgeCacheIoSystem();
                                                        }
                                                    }

                                                    #endregion

                                                }
                                                break;

                                        }

                                        // Update the block transaction cache.
                                        long totalRemoved = await UpdateBlockTransactionCacheTask(_cancellationTokenMemoryManagement);

                                        if (totalRemoved > 0)
                                        {
                                            changeDone = true;
                                        }

                                        // Update the blockchain wallet index memory cache.
                                        //totalRemoved = await BlockchainWalletIndexMemoryCacheObject.UpdateBlockchainWalletIndexMemoryCache(_cancellationTokenMemoryManagement);

                                        if (totalRemoved > 0)
                                        {
                                            changeDone = true;
                                        }

                                        if (changeDone)
                                        {
                                            ClassUtility.CleanGc();
                                        }
                                    }
                                }
                                catch (Exception error)
                                {
#if DEBUG
                                    Debug.WriteLine("Error on the memory update task. Exception: " + error.Message);
#endif
                                    ClassLog.WriteLine("Error on the memory update task. Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_MEMORY_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);

                                    if (useSemaphoreUpdateTransactionConfirmation)
                                    {
                                        _semaphoreSlimUpdateTransactionConfirmations.Release();
                                        useSemaphoreUpdateTransactionConfirmation = false;
                                    }
                                    if (useSemaphoreMemory)
                                    {
                                        _semaphoreSlimMemoryAccess.Release();
                                        useSemaphoreMemory = false;
                                    }
                                }
                            }
                            finally
                            {
                                if (useSemaphoreUpdateTransactionConfirmation)
                                {
                                    _semaphoreSlimUpdateTransactionConfirmations.Release();
                                    useSemaphoreUpdateTransactionConfirmation = false;
                                }
                                if (useSemaphoreMemory)
                                {
                                    _semaphoreSlimMemoryAccess.Release();
                                    useSemaphoreMemory = false;
                                }
                            }
                        }
#if DEBUG
                        else
                        {
                            Debug.WriteLine("Memory management in pause status.");
                        }
#endif
                        await Task.Delay(_blockchainDatabaseSetting.BlockchainCacheSetting.GlobalTaskManageMemoryInterval);
                    }
                }, _cancellationTokenMemoryManagement.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Ignored, catch the exception once the task is cancelled.
            }
        }

        /// <summary>
        /// Stop the task of memory management who save memory in cache disk (SQLite).
        /// </summary>
        public void SetMemoryManagementPauseStatus(bool pauseStatus)
        {
            _pauseMemoryManagement = pauseStatus;
        }

        /// <summary>
        /// Stop Memory Management.
        /// </summary>
        public async Task StopMemoryManagement()
        {
            SetMemoryManagementPauseStatus(true);
            bool semaphoreUpdateTransactionConfirmationUsed = false;
            bool semaphoreMemoryAccessUsed = false;

            try
            {
                await _semaphoreSlimUpdateTransactionConfirmations.WaitAsync();
                semaphoreUpdateTransactionConfirmationUsed = true;
                await _semaphoreSlimMemoryAccess.WaitAsync();
                semaphoreMemoryAccessUsed = true;

                if (_cancellationTokenMemoryManagement != null)
                {
                    if (!_cancellationTokenMemoryManagement.IsCancellationRequested)
                    {
                        _cancellationTokenMemoryManagement.Cancel();
                    }
                }
            }
            finally
            {
                if (semaphoreUpdateTransactionConfirmationUsed)
                {
                    _semaphoreSlimUpdateTransactionConfirmations.Release();
                }
                if (semaphoreMemoryAccessUsed)
                {
                    _semaphoreSlimMemoryAccess.Release();
                }
            }
        }

        /// <summary>
        /// Force to purge the memory data cache.
        /// </summary>
        /// <param name="useSemaphore">Lock or not the access of the cache, determine also if the access require a simple get of the data cache or not.</param>
        /// <returns></returns>
        private async Task ForcePurgeMemoryDataCache()
        {
            switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
            {
                case CacheEnumName.IO_CACHE:
                    await _cacheIoSystem.PurgeCacheIoSystem();
                    break;
            }
        }

        /// <summary>
        /// Get block memory data from cache, depending of the key selected and depending of the cache system selected.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="keepAlive">Determine if the access require a simple get of the data cache or not.</param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<ClassBlockObject> GetBlockMemoryDataFromCacheByKey(long blockHeight, bool keepAlive, CancellationTokenSource cancellation)
        {
            ClassBlockObject blockObject = null;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            blockObject = await _cacheIoSystem.GetIoBlockObject(blockHeight, keepAlive, cancellation);

                            AddOrUpdateBlockMirrorObject(blockObject);
                        }
                        break;
                }
            }
            return blockObject;
        }

        /// <summary>
        /// Get block information memory data from cache, depending of the key selected and depending of the cache system selected.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<ClassBlockObject> GetBlockInformationMemoryDataFromCacheByKey(long blockHeight, CancellationTokenSource cancellation)
        {
            ClassBlockObject blockObject = null;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            blockObject = await _cacheIoSystem.GetIoBlockInformationObject(blockHeight, cancellation);
                            AddOrUpdateBlockMirrorObject(blockObject);
                        }
                        break;
                }

            }

            return blockObject;
        }

        /// <summary>
        /// Get block information memory data from cache, depending of the key selected and depending of the cache system selected.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<int> GetBlockTransactionCountMemoryDataFromCacheByKey(long blockHeight, CancellationTokenSource cancellation)
        {
            int transactionCount = 0;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            transactionCount = await _cacheIoSystem.GetIoBlockTransactionCount(blockHeight, cancellation);
                        }
                        break;
                }
            }
            return transactionCount;
        }

        /// <summary>
        /// Add or update data to cache.
        /// </summary>
        /// <param name="blockObject"></param>
        /// <param name="keepAlive">Keep alive or not the data provided to the cache in the active memory.</param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> AddOrUpdateMemoryDataToCache(ClassBlockObject blockObject, bool keepAlive, CancellationTokenSource cancellation)
        {

            bool updateAddResult = false;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            updateAddResult = await _cacheIoSystem.PushOrUpdateIoBlockObject(blockObject, keepAlive, cancellation);
                            if (updateAddResult)
                            {
                                AddOrUpdateBlockMirrorObject(blockObject);
                            }
                        }
                        break;
                }
            }
            return updateAddResult;
        }

        /// <summary>
        /// Remove data from the cache.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> RemoveMemoryDataOfCache(long blockHeight, CancellationTokenSource cancellation)
        {
            bool deleteResult = false;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            deleteResult = await _cacheIoSystem.TryDeleteIoBlockObject(blockHeight, cancellation);
                        }
                        break;
                }
            }
            return deleteResult;
        }

        /// <summary>
        /// Insert directly a block transaction into the memory cache.
        /// </summary>
        /// <param name="blockTransaction"></param>
        /// <param name="blockHeight"></param>
        /// <param name="keepAlive">Keep alive or not the data updated in the active memory.</param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> InsertBlockTransactionToMemoryDataCache(ClassBlockTransaction blockTransaction, long blockHeight, bool keepAlive, CancellationTokenSource cancellation)
        {
            bool result = false;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            result = await _cacheIoSystem.InsertOrUpdateBlockTransactionObject(blockTransaction, blockHeight, keepAlive, cancellation);

                            if (result)
                            {
                                await UpdateBlockTransactionCache(blockTransaction, cancellation);
                            }
                        }
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Insert directly a list of block transaction into the memory cache.
        /// </summary>
        /// <param name="listBlockTransaction"></param>
        /// <param name="keepAlive">Keep alive or not the data updated in the active memory.</param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> InsertListBlockTransactionToMemoryDataCache(List<ClassBlockTransaction> listBlockTransaction, bool keepAlive, CancellationTokenSource cancellation)
        {
            bool result = false;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            result = await _cacheIoSystem.InsertOrUpdateListBlockTransactionObject(listBlockTransaction, keepAlive, cancellation);

                            if (result)
                            {
                                await UpdateListBlockTransactionCache(listBlockTransaction, cancellation, true);
                            }
                        }
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Check if the block height already exist on the cache.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> CheckBlockHeightExistOnMemoryDataCache(long blockHeight, CancellationTokenSource cancellation)
        {
            bool result = false;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            result = await _cacheIoSystem.ContainIoBlockHeight(blockHeight, cancellation);
                        }
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Check if the transaction hash exist on the cache.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> CheckTransactionHashExistOnMemoryDataOnCache(string transactionHash, long blockHeight, CancellationTokenSource cancellation)
        {

            if (blockHeight < BlockchainSetting.GenesisBlockHeight)
            {
                blockHeight = ClassTransactionUtility.GetBlockHeightFromTransactionHash(transactionHash);
            }

            if (await ContainBlockTransactionHashInCache(transactionHash, blockHeight, cancellation))
            {
                return true;
            }

            bool result = false;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            result = await _cacheIoSystem.CheckTransactionHashExistOnIoBlockCached(transactionHash, blockHeight, cancellation);
                        }
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Add or update a list of block objects on the cache.
        /// </summary>
        /// <param name="listBlockObjects"></param>
        /// <param name="keepAliveData">Keep alive or not the data saved.</param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<bool> AddOrUpdateListBlockObjectOnMemoryDataCache(List<ClassBlockObject> listBlockObjects, bool keepAliveData, CancellationTokenSource cancellation)
        {
            bool result = false;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {

                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            result = await _cacheIoSystem.PushOrUpdateListIoBlockObject(listBlockObjects, keepAliveData, cancellation);

                            if (result)
                            {
                                foreach (ClassBlockObject blockObject in listBlockObjects)
                                {
                                    AddOrUpdateBlockMirrorObject(blockObject);
                                    await UpdateListBlockTransactionCache(blockObject.BlockTransactions.Values.ToList(), cancellation, true);
                                }
                            }
                        }
                        break;
                }
            }

            return result;
        }

        /// <summary>
        /// Retrieve back a transaction by his hash from memory cache or from the active memory.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="useBlockTransactionCache"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<ClassBlockTransaction> GetBlockTransactionByTransactionHashFromMemoryDataCache(string transactionHash, long blockHeight, bool useBlockTransactionCache, CancellationTokenSource cancellation)
        {
            ClassBlockTransaction resultBlockTransaction = null;

            if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
            {
                bool blockIsLocked = false;
                bool blockTransactionIsLocked = false;
                try
                {
                    if (Monitor.TryEnter(_dictionaryBlockObjectMemory[blockHeight].Content))
                    {
                        blockIsLocked = true;
                        if (Monitor.TryEnter(_dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions))
                        {
                            blockTransactionIsLocked = true;
                            if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions.ContainsKey(transactionHash))
                            {
                                resultBlockTransaction = _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions[transactionHash];
                            }
                        }
                    }
                }
                finally
                {
                    if (blockTransactionIsLocked)
                    {
                        Monitor.Exit(_dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions);
                    }
                    if (blockIsLocked)
                    {
                        Monitor.Exit(_dictionaryBlockObjectMemory[blockHeight].Content);
                    }
                }
            }

            if (useBlockTransactionCache)
            {
                if (resultBlockTransaction == null)
                {
                    resultBlockTransaction = await GetBlockTransactionCached(transactionHash, blockHeight, cancellation);
                }
            }

            if (resultBlockTransaction != null)
            {
                return resultBlockTransaction;
            }


            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            resultBlockTransaction = await _cacheIoSystem.GetBlockTransactionFromTransactionHashOnIoBlockCached(transactionHash, blockHeight, cancellation);
                            if (resultBlockTransaction != null)
                            {
                                await UpdateBlockTransactionCache(resultBlockTransaction, cancellation);
                            }
                        }
                        break;
                }
            }

            return resultBlockTransaction;
        }

        /// <summary>
        /// Retrieve back every block transactions by a block height target from the memory cache or the active memory.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<SortedList<string, ClassBlockTransaction>> GetTransactionListFromBlockHeightTargetFromMemoryDataCache(long blockHeight, bool keepAlive, CancellationTokenSource cancellation)
        {
            SortedList<string, ClassBlockTransaction> listBlockTransaction = new SortedList<string, ClassBlockTransaction>();

            #region Check the active memory.

            if (_dictionaryBlockObjectMemory.ContainsKey(blockHeight))
            {
                if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                {
                    foreach (var blockTransaction in _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions)
                    {

                        listBlockTransaction.Add(blockTransaction.Key, blockTransaction.Value);

                    }

                    if (listBlockTransaction.Count > 0)
                    {
                        return listBlockTransaction;
                    }
                }
            }

            #endregion

            #region Use the block transaction cache if possible.

            if (GetBlockMirrorObject(blockHeight, out ClassBlockObject blockInformationObject))
            {
                if (blockInformationObject != null)
                {
                    if (blockInformationObject.TotalTransaction > 0)
                    {

                        // Retrieve cache transaction if they exist.

                        var getListBlockTransaction = await GetEachBlockTransactionFromBlockHeightCached(blockHeight, cancellation);

                        if (getListBlockTransaction != null)
                        {
                            foreach (ClassCacheIoBlockTransactionObject blockTransaction in getListBlockTransaction)
                            {
                                if (blockTransaction.BlockTransaction != null)
                                {
                                    if (!listBlockTransaction.ContainsKey(blockTransaction.BlockTransaction.TransactionObject.TransactionHash))
                                    {
                                        listBlockTransaction.Add(blockTransaction.BlockTransaction.TransactionObject.TransactionHash, blockTransaction.BlockTransaction);
                                    }
                                }
                                else
                                {
                                    listBlockTransaction.Clear();
                                    break;
                                }
                            }

                            if (listBlockTransaction.Count == blockInformationObject.TotalTransaction)
                            {
                                return listBlockTransaction;
                            }
                            else
                            {
                                listBlockTransaction.Clear();
                            }

                        }
                    }
                }
            }

            #endregion

            #region Then ask the cache system.

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            foreach (var blockTransaction in await _cacheIoSystem.GetBlockTransactionListFromBlockHeightTarget(blockHeight, keepAlive, cancellation))
                            {
                                if (blockTransaction.Value != null)
                                {
                                    if (!listBlockTransaction.ContainsKey(blockTransaction.Key))
                                    {
                                        listBlockTransaction.Add(blockTransaction.Key, blockTransaction.Value);
                                    }
                                }
                            }

                            if (listBlockTransaction.Count > 0)
                            {
                                await UpdateListBlockTransactionCache(listBlockTransaction.Values.ToList(), cancellation, false);
                            }
                        }
                        break;
                }
            }

            #endregion

            return listBlockTransaction;
        }

        /// <summary>
        /// Retrieve back a block list by a block height range target from the cache or the active memory if possible.
        /// </summary>
        /// <param name="blockHeightStart"></param>
        /// <param name="blockHeightEnd"></param>
        /// <param name="keepAlive"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<SortedList<long, Tuple<ClassBlockObject, bool>>> GetBlockListFromBlockHeightRangeTargetFromMemoryDataCache(long blockHeightStart, long blockHeightEnd, bool keepAlive, CancellationTokenSource cancellation)
        {
            SortedList<long, Tuple<ClassBlockObject, bool>> listBlockObjects = new SortedList<long, Tuple<ClassBlockObject, bool>>();

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {

                if (GetLastBlockHeight >= BlockchainSetting.GenesisBlockHeight)
                {
                    #region Generate at first the list of block object in the active memory and change range if necessary.
                    // Do not allow invalid range index lower than the genesis block height.
                    if (blockHeightStart < BlockchainSetting.GenesisBlockHeight)
                    {
                        blockHeightStart = BlockchainSetting.GenesisBlockHeight;
                    }

                    // Do not allow invalid range index above the maximum of blocks indexed.
                    if (blockHeightEnd > Count)
                    {
                        blockHeightEnd = Count;
                    }

                    List<long> listBlockCached = new List<long>();

                    HashSet<long> blockListAlreadyRetrieved = new HashSet<long>();

                    // Check if some data are in the active memory first.
                    for (long i = blockHeightStart - 1; i < blockHeightEnd; i++)
                    {
                        if (i <= blockHeightEnd)
                        {
                            long blockHeight = i + 1;

                            if (blockHeight >= blockHeightStart && blockHeight <= blockHeightEnd)
                            {
                                if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                                {
                                    blockListAlreadyRetrieved.Add(blockHeight);
                                    listBlockObjects.Add(blockHeight, new Tuple<ClassBlockObject, bool>(_dictionaryBlockObjectMemory[blockHeight].Content, false));
                                }
                                else
                                {
                                    listBlockCached.Add(blockHeight);
                                }
                            }
                        }
                    }
                    #endregion

                    switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                    {
                        case CacheEnumName.IO_CACHE:
                            {
                                if (listBlockCached.Count > 0)
                                {
                                    try
                                    {
                                        listBlockCached.Sort();

                                        // Calculated again the new range to retrieve.
                                        long minBlockHeight = listBlockCached[0];
                                        long maxBlockHeight = listBlockCached[listBlockCached.Count - 1];

                                        listBlockObjects = await _cacheIoSystem.GetBlockObjectListFromBlockHeightRange(minBlockHeight, maxBlockHeight, blockListAlreadyRetrieved, listBlockObjects, keepAlive, cancellation);
                                    }
                                    catch (Exception error)
                                    {
                                        ClassLog.WriteLine("Error on trying to retrieve a list of blocks cached. Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_PEER_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
#if DEBUG
                                        Debug.WriteLine("Error on trying to retrieve a list of blocks cached. Exception: " + error.Message);
#endif
                                    }
                                }
                            }
                            break;
                    }
                }
            }
            else
            {
                #region Generate the list of block object in the active memory and change range if necessary.
                // Do not allow invalid range index lower than the genesis block height.
                if (blockHeightStart < BlockchainSetting.GenesisBlockHeight)
                {
                    blockHeightStart = BlockchainSetting.GenesisBlockHeight;
                }

                // Do not allow invalid range index above the maximum of blocks indexed.
                if (blockHeightEnd > Count)
                {
                    blockHeightEnd = Count;
                }

                // Check if some data are in the active memory first.
                for (long i = blockHeightStart - 1; i < blockHeightEnd; i++)
                {
                    if (i <= blockHeightEnd)
                    {
                        long blockHeight = i + 1;

                        if (blockHeight >= blockHeightStart && blockHeight <= blockHeightEnd)
                        {
                            if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                            {
                                listBlockObjects.Add(blockHeight, new Tuple<ClassBlockObject, bool>(_dictionaryBlockObjectMemory[blockHeight].Content, false));
                            }
                        }
                    }
                }
                #endregion

            }
            return listBlockObjects;
        }

        /// <summary>
        /// Retrieve back a block information list by a list of block height target from the cache or the active memory if possible.
        /// </summary>
        /// <param name="listBlockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<SortedList<long, ClassBlockObject>> GetBlockInformationListByBlockHeightListTargetFromMemoryDataCache(HashSet<long> listBlockHeight, CancellationTokenSource cancellation)
        {
            SortedList<long, ClassBlockObject> listBlockInformation = new SortedList<long, ClassBlockObject>();

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                if (listBlockHeight.Count > 0)
                {
                    switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                    {
                        case CacheEnumName.IO_CACHE:
                            {
                                listBlockInformation = await _cacheIoSystem.GetIoListBlockInformationObject(listBlockHeight, cancellation);

                                foreach (var blockPair in listBlockInformation)
                                {
                                    AddOrUpdateBlockMirrorObject(blockPair.Value);
                                }
                            }
                            break;
                    }
                }
            }

            return listBlockInformation;
        }

        /// <summary>
        /// Retrieve back block objects from the cache to the active memory by a range of block height provided.
        /// </summary>
        /// <param name="blockHeightStart"></param>
        /// <param name="blockHeightEnd"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task RetrieveBlockFromBlockHeightRangeTargetFromMemoryDataCacheToActiveMemory(long blockHeightStart, long blockHeightEnd, CancellationTokenSource cancellation)
        {
            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            foreach (var block in await GetBlockListFromBlockHeightRangeTargetFromMemoryDataCache(blockHeightStart, blockHeightEnd, false, cancellation))
                            {
                                if (block.Value.Item1 != null)
                                {
                                    if (block.Value.Item2)
                                    {
                                        if (_dictionaryBlockObjectMemory.ContainsKey(block.Value.Item1.BlockHeight))
                                        {
                                            if (_dictionaryBlockObjectMemory[block.Value.Item1.BlockHeight].Content == null)
                                            {
                                                _dictionaryBlockObjectMemory[block.Value.Item1.BlockHeight].Content = block.Value.Item1;
                                                _dictionaryBlockObjectMemory[block.Value.Item1.BlockHeight].Content.BlockLastChangeTimestamp = GetTimestampFromInsertPolicy(CacheBlockMemoryEnumInsertPolicy.INSERT_MOSTLY_USED);
                                                _dictionaryBlockObjectMemory[block.Value.Item1.BlockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;
                                                AddOrUpdateBlockMirrorObject(block.Value.Item1);
                                            }
                                            else
                                            {
                                                // If the block height data is on the active memory, compare the last block change timestamp with the one of the block data cached.
                                                if (_dictionaryBlockObjectMemory[block.Value.Item1.BlockHeight].Content.BlockLastChangeTimestamp < block.Value.Item1.BlockLastChangeTimestamp)
                                                {
                                                    _dictionaryBlockObjectMemory[block.Value.Item1.BlockHeight].Content = block.Value.Item1;
                                                    _dictionaryBlockObjectMemory[block.Value.Item1.BlockHeight].Content.BlockLastChangeTimestamp = GetTimestampFromInsertPolicy(CacheBlockMemoryEnumInsertPolicy.INSERT_MOSTLY_USED);
                                                    _dictionaryBlockObjectMemory[block.Value.Item1.BlockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;
                                                    AddOrUpdateBlockMirrorObject(block.Value.Item1);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Get a list of transaction hash by wallet address target from memory cache.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<HashSet<string>> GetListTransactionHashByWalletAddressTargetFromMemoryDataCache(string walletAddress, CancellationTokenSource cancellation)
        {
            HashSet<string> listTransactionHash = new HashSet<string>();

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                switch (_blockchainDatabaseSetting.BlockchainCacheSetting.CacheName)
                {
                    case CacheEnumName.IO_CACHE:
                        {
                            listTransactionHash = await _cacheIoSystem.GetIoListTransactionHashFromWalletAddressTarget(walletAddress, cancellation);
                        }
                        break;
                }
            }
            else
            {
                foreach (long blockHeight in _dictionaryBlockObjectMemory.Keys)
                {
                    if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                    {
                        if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions.Count > 0)
                        {
                            foreach (string transactionHash in _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions.Keys)
                            {
                                if (_dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions[transactionHash].TransactionObject.WalletAddressReceiver == walletAddress ||
                                    _dictionaryBlockObjectMemory[blockHeight].Content.BlockTransactions[transactionHash].TransactionObject.WalletAddressSender == walletAddress)
                                {
                                    listTransactionHash.Add(transactionHash);
                                }
                            }
                        }
                    }
                }
            }

            return listTransactionHash;
        }

        /// <summary>
        /// Get an object from get.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        private async Task<ClassBlockObject> GetObjectByKeyFromMemoryOrCacheAsync(long blockHeight, CancellationTokenSource cancellation)
        {

            switch (_dictionaryBlockObjectMemory[blockHeight].ObjectCacheType)
            {
                // Retrieve it from the active memory if not empty, otherwise, retrieved it from the persistent cache.
                case CacheBlockMemoryEnumState.IN_CACHE:
                case CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY:
                    {
                        if (ContainsKey(blockHeight))
                        {
                            // In this case, we retrieve the element of the active memory.
                            if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                            {
                                return _dictionaryBlockObjectMemory[blockHeight].Content;
                            }
                        }
                        _dictionaryBlockObjectMemory[blockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_PERSISTENT_CACHE;
                        return await GetObjectByKeyFromMemoryOrCacheAsync(blockHeight, cancellation);
                    }
                // Retrieved it from the persistent cache, otherwise pending to retrieve the data of the persistent cache if the active memory is not empty return the active memory instead.
                case CacheBlockMemoryEnumState.IN_PERSISTENT_CACHE:
                    {
                        // In this case, we retrieve the element of the active memory.
                        if (ContainsKey(blockHeight))
                        {
                            if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                            {
                                _dictionaryBlockObjectMemory[blockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;

                                return _dictionaryBlockObjectMemory[blockHeight].Content;
                            }
                        }

                        ClassBlockObject getBlockObject = null;
                        bool semaphoreUsed = false;

                        try
                        {
                            if (cancellation != null)
                            {
                                await _semaphoreSlimMemoryAccess.WaitAsync(cancellation.Token);
                            }
                            else
                            {
                                await _semaphoreSlimMemoryAccess.WaitAsync();
                            }
                            semaphoreUsed = true;

                            getBlockObject = await GetBlockMemoryDataFromCacheByKey(blockHeight, false, cancellation);
                            if (ContainsKey(blockHeight))
                            {
                                // In this case, we retrieve the element of the active memory.
                                if (_dictionaryBlockObjectMemory[blockHeight].Content != null)
                                {
                                    _dictionaryBlockObjectMemory[blockHeight].ObjectCacheType = CacheBlockMemoryEnumState.IN_ACTIVE_MEMORY;

                                    getBlockObject = _dictionaryBlockObjectMemory[blockHeight].Content;
                                }
                            }
                            else
                            {
                                if (getBlockObject != null)
                                {
                                    await Add(getBlockObject.BlockHeight, null, true, CacheBlockMemoryInsertEnumType.INSERT_IN_PERSISTENT_CACHE_OBJECT, CacheBlockMemoryEnumInsertPolicy.INSERT_PROBABLY_NOT_REALLY_USED, cancellation);
                                }
                            }

                        }
                        finally
                        {
                            if (semaphoreUsed)
                            {
                                _semaphoreSlimMemoryAccess.Release();
                            }
                        }

                        return getBlockObject;
                    }
            }

            return null;
        }

        /// <summary>
        /// Get timestamp to insert on object depending of the insert policy selected.
        /// </summary>
        /// <param name="insertPolicy"></param>
        /// <returns></returns>
        private long GetTimestampFromInsertPolicy(CacheBlockMemoryEnumInsertPolicy insertPolicy)
        {
            long currentTime = ClassUtility.GetCurrentTimestampInSecond();
            switch (insertPolicy)
            {
                case CacheBlockMemoryEnumInsertPolicy.INSERT_PROBABLY_NOT_REALLY_USED:
                    return (currentTime + _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalObjectExpiredFromCache);
                case CacheBlockMemoryEnumInsertPolicy.INSERT_MOSTLY_USED:
                    return (currentTime + _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalObjectCacheUpdateLimitTime);
                case CacheBlockMemoryEnumInsertPolicy.INSERT_IN_AND_CACHE:
                    return (currentTime - _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalObjectExpirationMemoryCached);
                case CacheBlockMemoryEnumInsertPolicy.INSERT_FROZEN:
                    return -1;

            }
            return currentTime;
        }

        /// <summary>
        /// Retrieve back the amount of active memory used by the cache.
        /// </summary>
        /// <returns></returns>
        public long GetActiveMemoryUsageFromCache()
        {
            long totalMemoryUsageFromCache = 0;

            if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
            {
                totalMemoryUsageFromCache = _cacheIoSystem.GetIoCacheSystemMemoryConsumption();
            }
            return totalMemoryUsageFromCache;
        }

        #endregion


        #region Functions to manage mirror memory.

        /// <summary>
        /// Check if the block height possess his mirror block content.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <returns></returns>
        public bool ContainBlockHeightMirror(long blockHeight)
        {
            if (_dictionaryBlockObjectMemory.ContainsKey(blockHeight))
            {
                if (_dictionaryBlockObjectMemory[blockHeight].ContentMirror != null)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Retrieve back the block mirror content of a block height.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="blockObject"></param>
        /// <returns></returns>
        public bool GetBlockMirrorObject(long blockHeight, out ClassBlockObject blockObject)
        {

            if (ContainBlockHeightMirror(blockHeight))
            {
                if (_dictionaryBlockObjectMemory[blockHeight].ContentMirror != null)
                {
                    blockObject = _dictionaryBlockObjectMemory[blockHeight].ContentMirror;
                    return true;
                }
            }

            blockObject = null; // Default.

            return false;
        }

        /// <summary>
        /// Get the transaction count stored on a block mirror.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="blockTransactionCount"></param>
        /// <returns></returns>
        public bool GetBlockMirrorTransactionCount(long blockHeight, out int blockTransactionCount)
        {

            if (ContainBlockHeightMirror(blockHeight))
            {
                blockTransactionCount = _dictionaryBlockObjectMemory[blockHeight].ContentMirror.TotalTransaction;
                return true;
            }

            blockTransactionCount = 0; // Default.
            return false;
        }

        /// <summary>
        /// Insert or a update a block mirror content object.
        /// </summary>
        /// <param name="blockObject"></param>
        private void AddOrUpdateBlockMirrorObject(ClassBlockObject blockObject)
        {
            if (blockObject != null)
            {
                if (blockObject.BlockStatus == ClassBlockEnumStatus.UNLOCKED)
                {


                    if (ContainBlockHeightMirror(blockObject.BlockHeight))
                    {
                        _dictionaryBlockObjectMemory[blockObject.BlockHeight].ContentMirror = blockObject;
                    }
                    else
                    {
                        if (ContainsKey(blockObject.BlockHeight))
                        {
                            _dictionaryBlockObjectMemory[blockObject.BlockHeight].ContentMirror = blockObject;
                        }
                        else
                        {
                            _dictionaryBlockObjectMemory.Add(blockObject.BlockHeight, new BlockchainMemoryObject()
                            {
                                ContentMirror = blockObject,
                                ObjectCacheType = CacheBlockMemoryEnumState.IN_PERSISTENT_CACHE,
                                CacheUpdated = true,
                                ObjectIndexed = true,
                            });
                        }
                    }
                }

            }
        }

        #endregion


        #region Manage the block transaction cache in front of IO Cache files/network.

        /// <summary>
        /// Insert a wallet address to keep reserved for the block transaction cache.
        /// </summary>
        /// <param name="walletAddress">Wallet address linked to transaction to keep on the cache</param>
        public void InsertWalletAddressReservedToBlockTransactionCache(string walletAddress)
        {
            if (!_listWalletAddressReservedForBlockTransactionCache.Contains(walletAddress))
            {
                _listWalletAddressReservedForBlockTransactionCache.Add(walletAddress);
            }
        }

        /// <summary>
        /// Remove a wallet address to not keep it reserved for the block transaction cache.
        /// </summary>
        /// <param name="walletAddress">Wallet address linked to transaction to keep on the cache</param>
        public void RemoveWalletAddressReservedToBlockTransactionCache(string walletAddress)
        {
            if (!_listWalletAddressReservedForBlockTransactionCache.Contains(walletAddress))
            {
                _listWalletAddressReservedForBlockTransactionCache.Remove(walletAddress);
            }
        }

        /// <summary>
        /// Update the block transaction cache task.
        /// </summary>
        private async Task<long> UpdateBlockTransactionCacheTask(CancellationTokenSource cancellation)
        {
            long totalTransactionRemoved = 0;
            bool semaphoreUsed = false;
            try
            {
                if (cancellation != null)
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync(cancellation.Token);
                }
                else
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync();
                }
                semaphoreUsed = true;

                try
                {
                    if (Count > 0)
                    {

                        long totalTransaction = 0;
                        long totalTransactionKeepAlive = 0;
                        long currentTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                        foreach (long blockHeight in _dictionaryBlockObjectMemory.Keys.ToArray())
                        {
                            if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Count > 0)
                            {
                                int totalRemovedFromBlock = 0;
                                foreach (string transactionHash in _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Keys.ToArray())
                                {
                                    totalTransaction++;

                                    if (_listWalletAddressReservedForBlockTransactionCache.Contains(_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[transactionHash].BlockTransaction.TransactionObject.WalletAddressSender) ||
                                        _listWalletAddressReservedForBlockTransactionCache.Contains(_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[transactionHash].BlockTransaction.TransactionObject.WalletAddressReceiver))
                                    {
                                        totalTransactionKeepAlive++;
                                    }
                                    else
                                    {
                                        if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[transactionHash].LastUpdateTimestamp + _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalMaxDelayKeepAliveBlockTransactionCached < currentTimestamp)
                                        {
                                            long blockTransactionMemorySize = _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[transactionHash].BlockTransactionMemorySize;
                                            if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Remove(transactionHash))
                                            {
                                                totalTransactionRemoved++;
                                                totalRemovedFromBlock++;
                                                if (_totalBlockTransactionCacheCount > 0)
                                                {
                                                    _totalBlockTransactionCacheCount--;
                                                }
                                                _totalBlockTransactionMemorySize -= blockTransactionMemorySize;
                                                if (_totalBlockTransactionMemorySize < 0)
                                                {
                                                    _totalBlockTransactionMemorySize = 0;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            totalTransactionKeepAlive++;
                                        }
                                    }

                                }


                                // Clean previous capacity.
                                if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Count == 0)
                                {
                                    _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Clear();
                                }
                            }

                        }

                        double percentRemoved = ((double)totalTransactionRemoved / totalTransaction) / 100d;

                        // If this percent of remove is above 30%, call GC Collector.
                        if (percentRemoved > 30)
                        {
                            ClassUtility.CleanGc();
                        }

#if DEBUG
                        Debug.WriteLine("Block transaction cache - Clean up block transaction cache. Total Removed: " + totalTransactionRemoved + " | Total Keep Alive: " + totalTransactionKeepAlive + " on total transaction: " + totalTransaction);
                        Debug.WriteLine("Block transaction cache - Total active memory spend by the block transaction cache: " + ClassUtility.ConvertBytesToMegabytes(GetBlockTransactionCachedMemorySize()));
#endif

                        ClassLog.WriteLine("Block transaction cache - Clean up block transaction cache. Total Removed: " + totalTransactionRemoved + " | Total Keep Alive: " + totalTransactionKeepAlive + " on total transaction: " + totalTransaction, ClassEnumLogLevelType.LOG_LEVEL_MEMORY_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);
                        ClassLog.WriteLine("Block transaction cache - Total active memory spend by the block transaction cache: " + ClassUtility.ConvertBytesToMegabytes(GetBlockTransactionCachedMemorySize()), ClassEnumLogLevelType.LOG_LEVEL_MEMORY_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);

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
                    _semaphoreSlimCacheBlockTransactionAccess.Release();
                }
            }
            return totalTransactionRemoved;
        }

        /// <summary>
        /// Update the block transaction cache.
        /// </summary>
        /// <param name="blockTransaction"></param>
        /// <param name="cancellation"></param>
        public async Task UpdateBlockTransactionCache(ClassBlockTransaction blockTransaction, CancellationTokenSource cancellation)
        {
            bool semaphoreUsed = false;
            try
            {
                if (cancellation != null)
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync(cancellation.Token);
                }
                else
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync();
                }
                semaphoreUsed = true;

                try
                {
                    if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
                    {
                        if (blockTransaction?.TransactionObject != null)
                        {
                            if (blockTransaction.TransactionBlockHeightInsert > BlockchainSetting.GenesisBlockHeight)
                            {
                                if (blockTransaction.TransactionTotalConfirmation >= (blockTransaction.TransactionBlockHeightTarget - blockTransaction.TransactionBlockHeightInsert))
                                {
                                    long blockHeight = blockTransaction.TransactionBlockHeightInsert;
                                    long blockTransactionMemorySize = blockTransaction.TransactionSize;

                                    if (blockTransactionMemorySize <= 0)
                                    {
                                        blockTransactionMemorySize = ClassTransactionUtility.GetBlockTransactionMemorySize(blockTransaction);
                                    }

                                    // Insert or update block transactions of wallet address reserved.
                                    if (_listWalletAddressReservedForBlockTransactionCache.Contains(blockTransaction.TransactionObject.WalletAddressSender) ||
                                        _listWalletAddressReservedForBlockTransactionCache.Contains(blockTransaction.TransactionObject.WalletAddressReceiver))
                                    {
                                        if (!_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(blockTransaction.TransactionObject.TransactionHash))
                                        {

                                            _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Add(blockTransaction.TransactionObject.TransactionHash, new ClassCacheIoBlockTransactionObject()
                                            {
                                                BlockTransaction = blockTransaction,
                                                LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond(),
                                                BlockTransactionMemorySize = blockTransactionMemorySize
                                            });

                                            _totalBlockTransactionCacheCount++;

                                            // Increment size.
                                            _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                        }
                                        else
                                        {
                                            _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransaction = blockTransaction;
                                            _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                                            // Remove previous size.
                                            _totalBlockTransactionMemorySize -= _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize;
                                            if (_totalBlockTransactionMemorySize < 0)
                                            {
                                                _totalBlockTransactionMemorySize = 0;
                                            }

                                            // Increment new size.
                                            _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize = blockTransactionMemorySize;
                                            _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                        }
                                    }
                                    else
                                    {
                                        if (_totalBlockTransactionMemorySize + blockTransactionMemorySize < _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalCacheMaxBlockTransactionKeepAliveMemorySize)
                                        {
                                            if (!_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(blockTransaction.TransactionObject.TransactionHash))
                                            {

                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Add(blockTransaction.TransactionObject.TransactionHash, new ClassCacheIoBlockTransactionObject()
                                                {
                                                    BlockTransaction = blockTransaction,
                                                    LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond(),
                                                    BlockTransactionMemorySize = blockTransactionMemorySize
                                                });

                                                _totalBlockTransactionCacheCount++;

                                                // Increment size.
                                                _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                            }
                                            else
                                            {
                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransaction = blockTransaction;
                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                                                // Remove previous size.
                                                _totalBlockTransactionMemorySize -= _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize;
                                                if (_totalBlockTransactionMemorySize < 0)
                                                {
                                                    _totalBlockTransactionMemorySize = 0;
                                                }

                                                // Increment new size.
                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize = blockTransactionMemorySize;
                                                _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                            }
                                        }
                                        else
                                        {
                                            // If the tx already stored, but was updated, we simply replace the previous one by the new one.
                                            if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(blockTransaction.TransactionObject.TransactionHash))
                                            {
                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransaction = blockTransaction;
                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                                                // Remove previous size.
                                                _totalBlockTransactionMemorySize -= _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize;
                                                if (_totalBlockTransactionMemorySize < 0)
                                                {
                                                    _totalBlockTransactionMemorySize = 0;
                                                }

                                                // Increment new size.
                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize = blockTransactionMemorySize;
                                                _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                            }
                                            // If not, and if the amount of memory allocated for the cache is reach, try to remove some old tx cached to retrieve back some memory.
                                            else
                                            {
                                                long totalMemoryRetrieved = 0;
                                                bool memoryRetrived = false;
                                                long lastBlockHeight = GetLastBlockHeight;

                                                for (long i = 0; i < lastBlockHeight; i++)
                                                {
                                                    long blockHeightIndex = i + 1;
                                                    if (blockHeightIndex <= lastBlockHeight)
                                                    {
                                                        if (_dictionaryBlockObjectMemory[blockHeightIndex].BlockTransactionCache.Count > 0)
                                                        {
                                                            List<string> listTxHashToRemove = new List<string>();

                                                            foreach (var txHash in _dictionaryBlockObjectMemory[blockHeightIndex].BlockTransactionCache.Keys)
                                                            {
                                                                if (_dictionaryBlockObjectMemory[blockHeightIndex].BlockTransactionCache.TryGetValue(txHash, out var cacheIoBlockTransactionObject))
                                                                {
                                                                    if (cacheIoBlockTransactionObject != null)
                                                                    {
                                                                        // Remove previous size.
                                                                        long previousSize = cacheIoBlockTransactionObject.BlockTransactionMemorySize;

                                                                        listTxHashToRemove.Add(txHash);

                                                                        _totalBlockTransactionMemorySize -= previousSize;
                                                                        _totalBlockTransactionCacheCount--;


                                                                        if (totalMemoryRetrieved >= blockTransactionMemorySize)
                                                                        {
                                                                            memoryRetrived = true;
                                                                            break;
                                                                        }
                                                                    }
                                                                }
                                                            }

                                                            if (listTxHashToRemove.Count > 0)
                                                            {
                                                                foreach (string txHash in listTxHashToRemove)
                                                                {
                                                                    _dictionaryBlockObjectMemory[blockHeightIndex].BlockTransactionCache.Remove(txHash);
                                                                }

                                                                listTxHashToRemove.Clear();
                                                            }
                                                        }
                                                    }

                                                    if (memoryRetrived)
                                                    {
                                                        break;
                                                    }
                                                }

                                                if (memoryRetrived)
                                                {
                                                    if (!_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(blockTransaction.TransactionObject.TransactionHash))
                                                    {
                                                        _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Add(blockTransaction.TransactionObject.TransactionHash, new ClassCacheIoBlockTransactionObject()
                                                        {
                                                            BlockTransaction = blockTransaction,
                                                            LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond(),
                                                            BlockTransactionMemorySize = blockTransactionMemorySize
                                                        });

                                                        _totalBlockTransactionCacheCount++;

                                                        // Increment size.
                                                        _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                                    }
                                                    else
                                                    {
                                                        _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransaction = blockTransaction;
                                                        _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                                                        // Remove previous size.
                                                        _totalBlockTransactionMemorySize -= _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize;
                                                        if (_totalBlockTransactionMemorySize < 0)
                                                        {
                                                            _totalBlockTransactionMemorySize = 0;
                                                        }

                                                        // Increment new size.
                                                        _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize = blockTransactionMemorySize;
                                                        _totalBlockTransactionMemorySize += blockTransactionMemorySize;
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
                catch
                {
                    // Ignored.
                }

            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreSlimCacheBlockTransactionAccess.Release();
                }
            }
        }

        /// <summary>
        /// Update the block transaction cache.
        /// </summary>
        /// <param name="blockTransaction"></param>
        /// <param name="cancellation"></param>
        public async Task UpdateListBlockTransactionCache(List<ClassBlockTransaction> blockTransactionList, CancellationTokenSource cancellation, bool onlyIfExist)
        {
            bool semaphoreUsed = false;
            try
            {
                if (cancellation != null)
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync(cancellation.Token);
                }
                else
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync();
                }
                semaphoreUsed = true;

                try
                {
                    if (_blockchainDatabaseSetting.BlockchainCacheSetting.EnableCacheDatabase)
                    {
                        foreach (var blockTransaction in blockTransactionList)
                        {
                            cancellation?.Token.ThrowIfCancellationRequested();

                            if (blockTransaction?.TransactionObject != null)
                            {
                                if (blockTransaction.TransactionBlockHeightInsert > BlockchainSetting.GenesisBlockHeight)
                                {
                                    if (blockTransaction.TransactionTotalConfirmation >= (blockTransaction.TransactionBlockHeightTarget - blockTransaction.TransactionBlockHeightInsert))
                                    {
                                        long blockHeight = blockTransaction.TransactionBlockHeightInsert;
                                        long blockTransactionMemorySize = blockTransaction.TransactionSize;

                                        if (blockTransactionMemorySize <= 0)
                                        {
                                            blockTransactionMemorySize = ClassTransactionUtility.GetBlockTransactionMemorySize(blockTransaction);
                                        }

                                        // Insert or update block transactions of wallet address reserved.
                                        if (_listWalletAddressReservedForBlockTransactionCache.Contains(blockTransaction.TransactionObject.WalletAddressSender) ||
                                            _listWalletAddressReservedForBlockTransactionCache.Contains(blockTransaction.TransactionObject.WalletAddressReceiver))
                                        {
                                            if (!_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(blockTransaction.TransactionObject.TransactionHash))
                                            {
                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Add(blockTransaction.TransactionObject.TransactionHash, new ClassCacheIoBlockTransactionObject()
                                                {
                                                    BlockTransaction = blockTransaction,
                                                    LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond(),
                                                    BlockTransactionMemorySize = blockTransactionMemorySize
                                                });

                                                _totalBlockTransactionCacheCount++;

                                                // Increment size.
                                                _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                            }
                                            else
                                            {
                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransaction = blockTransaction;
                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                                                // Remove previous size.
                                                _totalBlockTransactionMemorySize -= _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize;
                                                if (_totalBlockTransactionMemorySize < 0)
                                                {
                                                    _totalBlockTransactionMemorySize = 0;
                                                }

                                                // Increment new size.
                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize = blockTransactionMemorySize;
                                                _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                            }
                                        }
                                        else
                                        {
                                            if (_totalBlockTransactionMemorySize + blockTransactionMemorySize < _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalCacheMaxBlockTransactionKeepAliveMemorySize)
                                            {
                                                if (!_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(blockTransaction.TransactionObject.TransactionHash))
                                                {
                                                    if (!onlyIfExist)
                                                    {
                                                        _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Add(blockTransaction.TransactionObject.TransactionHash, new ClassCacheIoBlockTransactionObject()
                                                        {
                                                            BlockTransaction = blockTransaction,
                                                            LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond(),
                                                            BlockTransactionMemorySize = blockTransactionMemorySize
                                                        });

                                                        _totalBlockTransactionCacheCount++;

                                                        // Increment size.
                                                        _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                                    }
                                                }
                                                else
                                                {
                                                    _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransaction = blockTransaction;
                                                    _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                                                    // Remove previous size.
                                                    _totalBlockTransactionMemorySize -= _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize;
                                                    if (_totalBlockTransactionMemorySize < 0)
                                                    {
                                                        _totalBlockTransactionMemorySize = 0;
                                                    }

                                                    // Increment new size.
                                                    _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize = blockTransactionMemorySize;
                                                    _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                                }
                                            }
                                            else
                                            {
                                                // If the tx already stored, but was updated, we simply replace the previous one by the new one.
                                                if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(blockTransaction.TransactionObject.TransactionHash))
                                                {
                                                    _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransaction = blockTransaction;
                                                    _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                                                    // Remove previous size.
                                                    _totalBlockTransactionMemorySize -= _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize;
                                                    if (_totalBlockTransactionMemorySize < 0)
                                                    {
                                                        _totalBlockTransactionMemorySize = 0;
                                                    }

                                                    // Increment new size.
                                                    _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize = blockTransactionMemorySize;
                                                    _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                                }
                                                // If not, and if the amount of memory allocated for the cache is reach, try to remove some old tx cached to retrieve back some memory.
                                                else
                                                {
                                                    if (!onlyIfExist)
                                                    {
                                                        long totalMemoryRetrieved = 0;
                                                        bool memoryRetrived = false;
                                                        long lastBlockHeight = GetLastBlockHeight;

                                                        for (long i = 0; i < lastBlockHeight; i++)
                                                        {
                                                            long blockHeightIndex = i + 1;
                                                            if (blockHeightIndex <= lastBlockHeight)
                                                            {
                                                                if (_dictionaryBlockObjectMemory[blockHeightIndex].BlockTransactionCache.Count > 0)
                                                                {
                                                                    List<string> listTxHashToRemove = new List<string>();

                                                                    foreach (var txHash in _dictionaryBlockObjectMemory[blockHeightIndex].BlockTransactionCache.Keys)
                                                                    {
                                                                        if (_dictionaryBlockObjectMemory[blockHeightIndex].BlockTransactionCache.TryGetValue(txHash, out var cacheIoBlockTransactionObject))
                                                                        {
                                                                            if (cacheIoBlockTransactionObject != null)
                                                                            {
                                                                                // Remove previous size.
                                                                                long previousSize = cacheIoBlockTransactionObject.BlockTransactionMemorySize;

                                                                                listTxHashToRemove.Add(txHash);

                                                                                _totalBlockTransactionMemorySize -= previousSize;
                                                                                _totalBlockTransactionCacheCount--;


                                                                                if (totalMemoryRetrieved >= blockTransactionMemorySize)
                                                                                {
                                                                                    memoryRetrived = true;
                                                                                    break;
                                                                                }
                                                                            }
                                                                        }
                                                                    }

                                                                    if (listTxHashToRemove.Count > 0)
                                                                    {
                                                                        foreach (string txHash in listTxHashToRemove)
                                                                        {
                                                                            _dictionaryBlockObjectMemory[blockHeightIndex].BlockTransactionCache.Remove(txHash);
                                                                        }

                                                                        listTxHashToRemove.Clear();
                                                                    }
                                                                }
                                                            }

                                                            if (memoryRetrived)
                                                            {
                                                                break;
                                                            }
                                                        }

                                                        if (memoryRetrived)
                                                        {
                                                            if (!_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(blockTransaction.TransactionObject.TransactionHash))
                                                            {
                                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Add(blockTransaction.TransactionObject.TransactionHash, new ClassCacheIoBlockTransactionObject()
                                                                {
                                                                    BlockTransaction = blockTransaction,
                                                                    LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond(),
                                                                    BlockTransactionMemorySize = blockTransactionMemorySize
                                                                });

                                                                _totalBlockTransactionCacheCount++;

                                                                // Increment size.
                                                                _totalBlockTransactionMemorySize += blockTransactionMemorySize;
                                                            }
                                                            else
                                                            {
                                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransaction = blockTransaction;
                                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInSecond();

                                                                // Remove previous size.
                                                                _totalBlockTransactionMemorySize -= _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize;
                                                                if (_totalBlockTransactionMemorySize < 0)
                                                                {
                                                                    _totalBlockTransactionMemorySize = 0;
                                                                }

                                                                // Increment new size.
                                                                _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[blockTransaction.TransactionObject.TransactionHash].BlockTransactionMemorySize = blockTransactionMemorySize;
                                                                _totalBlockTransactionMemorySize += blockTransactionMemorySize;
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
                    _semaphoreSlimCacheBlockTransactionAccess.Release();
                }
            }
        }

        /// <summary>
        /// Check if the cache contain the transaction hash in the cache.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> ContainBlockTransactionHashInCache(string transactionHash, long blockHeight, CancellationTokenSource cancellation)
        {
            bool result = false;
            bool semaphoreUsed = false;
            try
            {
                if (cancellation != null)
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync(cancellation.Token);
                }
                else
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync();
                }
                semaphoreUsed = true;

                try
                {
                    if (ContainsKey(blockHeight))
                    {
                        result = _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(transactionHash);
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
                    _semaphoreSlimCacheBlockTransactionAccess.Release();
                }
            }

            return result;
        }

        /// <summary>
        /// Retrieve back a block transaction cached.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<ClassBlockTransaction> GetBlockTransactionCached(string transactionHash, long blockHeight, CancellationTokenSource cancellation)
        {
            ClassBlockTransaction blockTransaction = null;

            bool semaphoreUsed = false;
            try
            {
                if (cancellation != null)
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync(cancellation.Token);
                }
                else
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync();
                }
                semaphoreUsed = true;

                if (ContainsKey(blockHeight))
                {
                    try
                    {
                        if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(transactionHash))
                        {
                            long currentTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                            if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[transactionHash].LastUpdateTimestamp + _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalMaxDelayKeepAliveBlockTransactionCached >= currentTimestamp)
                            {
                                blockTransaction = _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[transactionHash].BlockTransaction;
                            }
                            else
                            {
                                long totalMemoryTransactionSize = _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[transactionHash].BlockTransactionMemorySize;
                                if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Remove(transactionHash))
                                {
                                    _totalBlockTransactionMemorySize -= totalMemoryTransactionSize;
                                    _totalBlockTransactionCacheCount--;

                                    if (_totalBlockTransactionCacheCount < 0)
                                    {
                                        _totalBlockTransactionCacheCount = 0;
                                    }
                                    if (_totalBlockTransactionMemorySize < 0)
                                    {
                                        _totalBlockTransactionMemorySize = 0;
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
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreSlimCacheBlockTransactionAccess.Release();
                }
            }

            return blockTransaction;
        }

        /// <summary>
        /// Retrieve back a list of block transaction cached.
        /// </summary>
        /// <param name="listTransactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<List<ClassBlockTransaction>> GetListBlockTransactionCache(List<string> listTransactionHash, long blockHeight, CancellationTokenSource cancellation)
        {
            List<ClassBlockTransaction> listBlockTransactions = new List<ClassBlockTransaction>();
            bool semaphoreUsed = false;
            try
            {
                if (cancellation != null)
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync(cancellation.Token);
                }
                else
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync();
                }
                semaphoreUsed = true;

                if (ContainsKey(blockHeight))
                {
                    try
                    {
                        long currentTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                        foreach (string transactionHash in listTransactionHash)
                        {
                            if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.ContainsKey(transactionHash))
                            {
                                if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[transactionHash].LastUpdateTimestamp + _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalMaxDelayKeepAliveBlockTransactionCached >= currentTimestamp)
                                {
                                    listBlockTransactions.Add(_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[transactionHash].BlockTransaction);
                                }
                                else
                                {
                                    long totalMemoryTransactionSize = _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache[transactionHash].BlockTransactionMemorySize;
                                    if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Remove(transactionHash))
                                    {
                                        _totalBlockTransactionMemorySize -= totalMemoryTransactionSize;
                                        _totalBlockTransactionCacheCount--;

                                        if (_totalBlockTransactionCacheCount < 0)
                                        {
                                            _totalBlockTransactionCacheCount = 0;
                                        }
                                        if (_totalBlockTransactionMemorySize < 0)
                                        {
                                            _totalBlockTransactionMemorySize = 0;
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
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreSlimCacheBlockTransactionAccess.Release();
                }
            }

            return listBlockTransactions;
        }

        /// <summary>
        /// Get each block transaction cached of a block height target.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<IList<ClassCacheIoBlockTransactionObject>> GetEachBlockTransactionFromBlockHeightCached(long blockHeight, CancellationTokenSource cancellation)
        {
            IList<ClassCacheIoBlockTransactionObject> result = null;
            bool semaphoreUsed = false;
            try
            {
                if (cancellation != null)
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync(cancellation.Token);
                }
                else
                {
                    await _semaphoreSlimCacheBlockTransactionAccess.WaitAsync();
                }
                semaphoreUsed = true;

                if (ContainsKey(blockHeight))
                {
                    long currentTimestamp = ClassUtility.GetCurrentTimestampInSecond();
                    if (_dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Count > 0)
                    {
                        result = _dictionaryBlockObjectMemory[blockHeight].BlockTransactionCache.Values.TakeWhile(x => (x.LastUpdateTimestamp + _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalMaxDelayKeepAliveBlockTransactionCached >= currentTimestamp)).ToList();
                    }
                }
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreSlimCacheBlockTransactionAccess.Release();
                }
            }

            return result;
        }

        /// <summary>
        /// Return the amount of block transaction cached.
        /// </summary>
        /// <returns></returns>
        public long GetBlockTransactionCachedCount()
        {
            return _totalBlockTransactionCacheCount;
        }

        /// <summary>
        /// Return the amount of memory used by block transactions cached.
        /// </summary>
        /// <returns></returns>
        public long GetBlockTransactionCachedMemorySize()
        {
            return _totalBlockTransactionMemorySize + (GetBlockTransactionCachedCount() * (BlockchainSetting.TransactionHashSize * sizeof(char)));
        }

        #endregion


        #region Other functions.

        /// <summary>
        /// Build a list of block height by range.
        /// </summary>
        /// <param name="lastBlockHeightUnlocked"></param>
        /// <returns></returns>
        private List<List<long>> BuildListBlockHeightByRange(long lastBlockHeightUnlocked, HashSet<long> listBlockHeightToExcept)
        {
            List<List<long>> listOfBlockRange = new List<List<long>>
            {
                // Default list.
                new List<long>()
            };

            for (long i = 0; i < lastBlockHeightUnlocked; i++)
            {
                long blockHeight = i + 1;

                if (!listBlockHeightToExcept.Contains(blockHeight))
                {
                    if (blockHeight <= lastBlockHeightUnlocked)
                    {
                        int countList = listOfBlockRange.Count - 1;
                        if (listOfBlockRange[countList].Count < _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalMaxRangeReadBlockDataFromCache)
                        {
                            listOfBlockRange[countList].Add(blockHeight);
                        }
                        else
                        {
                            listOfBlockRange.Add(new List<long>());
                            countList = listOfBlockRange.Count - 1;
                            listOfBlockRange[countList].Add(blockHeight);
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return listOfBlockRange;
        }

        #endregion
    }
}


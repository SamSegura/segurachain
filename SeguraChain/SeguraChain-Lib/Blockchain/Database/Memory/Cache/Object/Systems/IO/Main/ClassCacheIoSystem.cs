using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using SeguraChain_Lib.Blockchain.Database.DatabaseSetting;
using SeguraChain_Lib.Blockchain.Database.Memory.Cache.Object.Systems.IO.Disk.Object;
using SeguraChain_Lib.Blockchain.Setting;
using SeguraChain_Lib.Blockchain.Transaction.Utility;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Other.Object.List;

using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Blockchain.Database.Memory.Cache.Object.Systems.IO.Main
{
    public class ClassCacheIoSystem
    {


        /// <summary>
        /// Objects and settings of the IO cache in disk mode.
        /// </summary>
        private const string IoFileExtension = ".ioblock";
        private Dictionary<string, ClassCacheIoIndexObject> _dictionaryCacheIoIndexObject;
        private string _cacheIoDirectoryPath;

        /// <summary>
        /// Blockchain database settings.
        /// </summary>
        private ClassBlockchainDatabaseSetting _blockchainDatabaseSetting;

        /// <summary>
        /// Multithreading settings.
        /// </summary>
        private SemaphoreSlim _semaphoreIoCacheIndexAccess;

        /// <summary>
        /// Save the last memory usage of the io cache system.
        /// </summary>
        private long _totalIoCacheSystemMemoryUsage;


        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="blockchainDatabaseSetting"></param>
        public ClassCacheIoSystem(ClassBlockchainDatabaseSetting blockchainDatabaseSetting)
        {
            _blockchainDatabaseSetting = blockchainDatabaseSetting;
            _cacheIoDirectoryPath = _blockchainDatabaseSetting.GetBlockchainCacheDirectoryPath;
            _semaphoreIoCacheIndexAccess = new SemaphoreSlim(1, ClassUtility.GetMaxAvailableProcessorCount());
            _dictionaryCacheIoIndexObject = new Dictionary<string, ClassCacheIoIndexObject>();
        }

        #region Manage IO Cache system.

        /// <summary>
        /// Initialize the IO cache system.
        /// </summary>
        /// <returns></returns>
        public async Task<Tuple<bool, HashSet<long>>> InitializeCacheIoSystem()
        {
            HashSet<long> listBlockHeight = new HashSet<long>();
            if (!Directory.Exists(_cacheIoDirectoryPath))
            {
                Directory.CreateDirectory(_cacheIoDirectoryPath);
            }
            else
            {
                string[] cacheIoFileList = Directory.GetFiles(_cacheIoDirectoryPath, "*" + IoFileExtension);

                if (cacheIoFileList.Length > 0)
                {
                    foreach (var ioFileName in cacheIoFileList)
                    {
                        Tuple<bool, HashSet<long>> result = await InitializeNewCacheIoIndex(Path.GetFileName(ioFileName));

                        foreach (long blockHeight in result.Item2)
                        {
                            listBlockHeight.Add(blockHeight);
                        }
                    }
                }
            }
            return new Tuple<bool, HashSet<long>>(true, listBlockHeight);
        }

        /// <summary>
        /// Initialize a new cache io index.
        /// </summary>
        /// <param name="ioFileName"></param>
        /// <returns></returns>
        private async Task<Tuple<bool, HashSet<long>>> InitializeNewCacheIoIndex(string ioFileName)
        {
            HashSet<long> listBlockHeight = new HashSet<long>();

            if (!_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
            {
                ClassCacheIoIndexObject cacheIoIndexObject = new ClassCacheIoIndexObject(ioFileName, _blockchainDatabaseSetting, this);

                Tuple<bool, HashSet<long>> result = await cacheIoIndexObject.InitializeIoCacheObjectAsync();

                if (result.Item1)
                {
                    try
                    {
                        _dictionaryCacheIoIndexObject.Add(ioFileName, cacheIoIndexObject);

                        foreach (long blockHeight in result.Item2)
                        {
                            listBlockHeight.Add(blockHeight);
                        }

                    }
                    catch
                    {
#if DEBUG
                        Debug.WriteLine("Cache IO System - Failed to index the new io cache file: " + ioFileName);
#endif
                        ClassLog.WriteLine("Cache IO System - Failed to index the new io cache file: " + ioFileName, ClassEnumLogLevelType.LOG_LEVEL_MEMORY_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);
                        return result;
                    }
                }
                else
                {
#if DEBUG
                    Debug.WriteLine("Cache IO System - Failed to initialize the new io cache file: " + ioFileName);
#endif

                    ClassLog.WriteLine("Cache IO System - Failed to initialize the new io cache file: " + ioFileName, ClassEnumLogLevelType.LOG_LEVEL_MEMORY_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);
                }
            }
            return new Tuple<bool, HashSet<long>>(true, listBlockHeight);
        }

        /// <summary>
        /// Purge the io cache system.
        /// </summary>
        /// <returns></returns>
        public async Task PurgeCacheIoSystem()
        {
            CancellationTokenSource cancellation = new CancellationTokenSource();

            try
            {
                await _semaphoreIoCacheIndexAccess.WaitAsync();

                if (_dictionaryCacheIoIndexObject.Count > 0)
                {
                    string[] ioFileNameArray = _dictionaryCacheIoIndexObject.Keys.ToArray();
                    int totalTaskToDo = ioFileNameArray.Length;
                    int totalTaskDone = 0;

                    foreach (string ioFileName in ioFileNameArray)
                    {
                        if (_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskEnableMultiTask && ioFileNameArray.Length > 1)
                        {
                            try
                            {
                                await Task.Factory.StartNew(async () =>
                                {
                                    await _dictionaryCacheIoIndexObject[ioFileName].PurgeIoBlockDataMemory(false, cancellation, 0, false);
                                    totalTaskDone++;

                                }, cancellation.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Ignored, catch the exception the task is cancelled.
                            }
                        }
                        else
                        {
                            await _dictionaryCacheIoIndexObject[ioFileName].PurgeIoBlockDataMemory(false, cancellation, 0, false);
                        }
                    }

                    if (_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskEnableMultiTask && ioFileNameArray.Length > 1)
                    {
                        while (totalTaskToDo > totalTaskDone)
                        {
                            try
                            {
                                await Task.Delay(_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskParallelTaskWaitDelay, cancellation.Token);
                            }
                            catch
                            {
                                break;
                            }
                        }
                    }

                    long totalIoCacheMemoryUsage = GetIoCacheSystemMemoryConsumption(cancellation);


                    if (!cancellation.IsCancellationRequested)
                        cancellation.Cancel();

#if DEBUG
                    Debug.WriteLine("Cache IO Index Object - Total Memory usage from the cache: " + ClassUtility.ConvertBytesToMegabytes(totalIoCacheMemoryUsage));
#endif
                    ClassLog.WriteLine("Cache IO Index Object - Total Memory usage from the cache: " + ClassUtility.ConvertBytesToMegabytes(totalIoCacheMemoryUsage), ClassEnumLogLevelType.LOG_LEVEL_MEMORY_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY);
                }
            }
            finally
            {
                if (!cancellation.IsCancellationRequested)
                {
                    cancellation.Cancel();
                }
                _semaphoreIoCacheIndexAccess.Release();
            }
        }

        /// <summary>
        /// Clean the io cache system.
        /// </summary>
        public async Task CleanCacheIoSystem()
        {
            await _semaphoreIoCacheIndexAccess.WaitAsync();


            if (_dictionaryCacheIoIndexObject.Count > 0)
            {
                string[] ioFileNameArray = _dictionaryCacheIoIndexObject.Keys.ToArray();
                int totalTaskToDo = ioFileNameArray.Length;
                int totalTaskDone = 0;
                CancellationTokenSource cancellation = new CancellationTokenSource();

                foreach (string ioFileName in ioFileNameArray)
                {
                    if (_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskEnableMultiTask && ioFileNameArray.Length > 1)
                    {
                        try
                        {
                            await Task.Factory.StartNew(() =>
                            {
                                string ioFilePath = _blockchainDatabaseSetting.GetBlockchainCacheDirectoryPath + ioFileName;
                                _dictionaryCacheIoIndexObject[ioFileName].CloseLockStream();
                                _dictionaryCacheIoIndexObject.Remove(ioFilePath);
                                File.Delete(ioFilePath);
                                totalTaskDone++;

                            }, cancellation.Token, TaskCreationOptions.PreferFairness, TaskScheduler.Current).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Ignored, catch the exception the task is cancelled.
                        }
                    }
                    else
                    {
                        string ioFilePath = _blockchainDatabaseSetting.GetBlockchainCacheDirectoryPath + ioFileName;
                        _dictionaryCacheIoIndexObject[ioFileName].CloseLockStream();
                        _dictionaryCacheIoIndexObject.Remove(ioFilePath);
                        File.Delete(ioFilePath);
                    }
                }

                if (_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskEnableMultiTask && ioFileNameArray.Length > 1)
                {
                    while (totalTaskToDo > totalTaskDone)
                    {
                        try
                        {
                            await Task.Delay(_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskParallelTaskWaitDelay, cancellation.Token);
                        }
                        catch
                        {
                            break;
                        }
                    }
                }

                cancellation.Cancel();

                try
                {
                    Directory.Delete(_blockchainDatabaseSetting.GetBlockchainCacheDirectoryPath, true);
                }
                catch
                {
                    // Ignored.
                }
            }
            _semaphoreIoCacheIndexAccess.Release();

        }

        /// <summary>
        /// Do a purge of the io cache system from a io cache file index to except.
        /// </summary>
        /// <param name="ioFileNameSource"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<bool> DoPurgeFromIoCacheIndex(string ioFileNameSource, long memoryAsked, CancellationTokenSource cancellation)
        {

            if (memoryAsked == 0)
            {
                return true;
            }

            long totalMemoryRetrieved = 0;

            foreach (string ioFileFileIndex in _dictionaryCacheIoIndexObject.Keys.ToArray())
            {
                if (ioFileFileIndex != ioFileNameSource)
                {
                    long restMemoryToTask = memoryAsked - totalMemoryRetrieved;

                    if (GetIoCacheSystemMemoryConsumption(cancellation) + restMemoryToTask <= _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalMaxActiveMemoryAllocationFromCache)
                        return true;

                    long totalMemoryFreeRetrieved = await _dictionaryCacheIoIndexObject[ioFileFileIndex].PurgeIoBlockDataMemory(false, cancellation, restMemoryToTask, true);

                    totalMemoryRetrieved += totalMemoryFreeRetrieved;

                    if (totalMemoryRetrieved >= memoryAsked)
                        return true;
                }
            }

            return false;
        }

        #endregion

        #region Get/Set/Update IO Cache data.

        /// <summary>
        /// Get io list block information objects by a list of block height from io cache files.
        /// </summary>
        /// <param name="listBlockHeight"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<SortedList<long, ClassBlockObject>> GetIoListBlockInformationObject(HashSet<long> listBlockHeight, CancellationTokenSource cancellationIoCache)
        {
            SortedList<long, ClassBlockObject> listBlockInformation = new SortedList<long, ClassBlockObject>();

            if (listBlockHeight.Count > 0)
            {
                bool useSemaphore = false;

                try
                {
                    if (cancellationIoCache != null)
                    {
                        await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                    }
                    else
                    {
                        await _semaphoreIoCacheIndexAccess.WaitAsync();
                    }

                    useSemaphore = true;

                    if (listBlockHeight.Count > 0)
                    {
                        Dictionary<string, HashSet<long>> dictionaryRangeBlockHeightIoCacheFile = new Dictionary<string, HashSet<long>>();

                        foreach (var blockHeight in listBlockHeight.ToArray())
                        {
                            string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                            if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                            {
                                if (!dictionaryRangeBlockHeightIoCacheFile.ContainsKey(ioFileName))
                                {
                                    dictionaryRangeBlockHeightIoCacheFile.Add(ioFileName, new HashSet<long>());
                                }
                                dictionaryRangeBlockHeightIoCacheFile[ioFileName].Add(blockHeight);
                            }
                        }

                        _semaphoreIoCacheIndexAccess.Release();
                        useSemaphore = false;

                        if (dictionaryRangeBlockHeightIoCacheFile.Count > 0)
                        {

                            foreach (string ioFileName in dictionaryRangeBlockHeightIoCacheFile.Keys.ToArray())
                            {
                                if (cancellationIoCache != null)
                                {
                                    if (cancellationIoCache.IsCancellationRequested)
                                    {
                                        break;
                                    }
                                }

                                foreach (ClassBlockObject blockObject in await _dictionaryCacheIoIndexObject[ioFileName].GetIoListBlockDataInformationFromListBlockHeight(dictionaryRangeBlockHeightIoCacheFile[ioFileName], cancellationIoCache))
                                {
                                    if (cancellationIoCache != null)
                                    {
                                        if (cancellationIoCache.IsCancellationRequested)
                                        {
                                            break;
                                        }
                                    }
                                    if (blockObject != null)
                                    {
                                        listBlockInformation.Add(blockObject.BlockHeight, blockObject);
                                    }
                                }
                            }

                            // Clean up.
                            dictionaryRangeBlockHeightIoCacheFile.Clear();
                        }
                    }
                }
                finally
                {
                    if (useSemaphore)
                    {
                        _semaphoreIoCacheIndexAccess.Release();
                    }
                }
            }

            return listBlockInformation;
        }

        /// <summary>
        /// Retrieve back a block information object from the io cache object.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<ClassBlockObject> GetIoBlockInformationObject(long blockHeight, CancellationTokenSource cancellationIoCache)
        {
            ClassBlockObject blockObject = null;

            bool useSemaphore = false;

            try
            {

                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);

                useSemaphore = true;

                string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                {
                    _semaphoreIoCacheIndexAccess.Release();
                    useSemaphore = false;
                    blockObject = await _dictionaryCacheIoIndexObject[ioFileName].GetIoBlockDataInformationFromBlockHeight(blockHeight, cancellationIoCache);
                }

            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return blockObject;
        }

        /// <summary>
        /// Retrieve back a block information object from the io cache object.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<int> GetIoBlockTransactionCount(long blockHeight, CancellationTokenSource cancellationIoCache)
        {
            int blockTransactionCount = 0;

            bool useSemaphore = false;

            try
            {

                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;

                string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                {
                    _semaphoreIoCacheIndexAccess.Release();
                    useSemaphore = false;
                    blockTransactionCount = await _dictionaryCacheIoIndexObject[ioFileName].GetIoBlockTransactionCountFromBlockHeight(blockHeight, cancellationIoCache);
                }
            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return blockTransactionCount;
        }

        /// <summary>
        /// Retrieve back a block object from the io cache object.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="keepAlive">Keep alive or not the data retrieved into the active memory.</param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<ClassBlockObject> GetIoBlockObject(long blockHeight, bool keepAlive, CancellationTokenSource cancellationIoCache)
        {
            ClassBlockObject blockObject = null;

            bool useSemaphore = false;

            try
            {
                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;

                string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                {
                    _semaphoreIoCacheIndexAccess.Release();
                    useSemaphore = false;
                    blockObject = await _dictionaryCacheIoIndexObject[ioFileName].GetIoBlockDataFromBlockHeight(blockHeight, keepAlive, cancellationIoCache);
                }
            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return blockObject;
        }

        /// <summary>
        /// Push or update a block object to the io cache object.
        /// </summary>
        /// <param name="blockObject"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<bool> PushOrUpdateIoBlockObject(ClassBlockObject blockObject, bool keepAlive, CancellationTokenSource cancellationIoCache)
        {
            bool pushOrUpdateStatus = true;

            bool useSemaphore = false;

            try
            {
                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;


                string ioFileName = GetIoFileNameFromBlockHeight(blockObject.BlockHeight);

                if (!_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                {
                    Tuple<bool, HashSet<long>> result = await InitializeNewCacheIoIndex(ioFileName);

                    if (!result.Item1)
                    {
                        pushOrUpdateStatus = false;
                    }
                }

                if (pushOrUpdateStatus)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                    useSemaphore = false;
                    pushOrUpdateStatus = await _dictionaryCacheIoIndexObject[ioFileName].PushOrUpdateIoBlockData(blockObject, keepAlive, cancellationIoCache);
                }
            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return pushOrUpdateStatus;
        }

        /// <summary>
        /// Push directly a list of object to insert/update directly to each io cache file indexed.
        /// </summary>
        /// <param name="blockObjectList"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<bool> PushOrUpdateListIoBlockObject(List<ClassBlockObject> blockObjectList, bool keepAlive, CancellationTokenSource cancellationIoCache)
        {
            bool result = true;

            bool useSemaphore = false;

            try
            {
                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;

                Dictionary<string, List<ClassBlockObject>> listBlockObject = new Dictionary<string, List<ClassBlockObject>>();

                // Generate a list of block object linked to the io file index.
                foreach (var blockObject in blockObjectList)
                {

                    string ioFileName = GetIoFileNameFromBlockHeight(blockObject.BlockHeight);

                    if (!_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                    {
                        Tuple<bool, HashSet<long>> resultInit = await InitializeNewCacheIoIndex(ioFileName);

                        if (!resultInit.Item1)
                        {
                            result = false;

                            break;
                        }
                    }

                    if (!listBlockObject.ContainsKey(ioFileName))
                    {
                        listBlockObject.Add(ioFileName, new List<ClassBlockObject>()
                        {
                            blockObject
                        });
                    }
                    else
                    {
                        listBlockObject[ioFileName].Add(blockObject);
                    }
                }

                _semaphoreIoCacheIndexAccess.Release();
                useSemaphore = false;

                // Much faster insert/update.
                if (listBlockObject.Count > 0 && result)
                {
                    string[] ioFileNameArray = listBlockObject.Keys.ToArray();
                    int totalTaskToDo = ioFileNameArray.Length;
                    int totalTaskDone = 0;

                    CancellationTokenSource cancellation = new CancellationTokenSource();
                    bool cancel = false;


                    foreach (string ioFileName in ioFileNameArray)
                    {
                        if (_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskEnableMultiTask && ioFileNameArray.Length > 1)
                        {
                            try
                            {
                                await Task.Factory.StartNew(async () =>
                                {

                                    if (cancellationIoCache != null)
                                    {
                                        if (cancellationIoCache.IsCancellationRequested)
                                        {
                                            cancel = true;
                                        }
                                    }
                                    if (!cancel)
                                    {

                                        if (!await _dictionaryCacheIoIndexObject[ioFileName].PushOrUpdateListIoBlockData(listBlockObject[ioFileName], keepAlive, cancellationIoCache))
                                        {
                                            result = false;
                                        }
                                    }

                                    totalTaskDone++;
                                }, cancellation.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Ignored, catch the exception once tasks are cancelled.
                            }
                        }
                        else
                        {
                            if (cancellationIoCache != null)
                            {
                                if (cancellationIoCache.IsCancellationRequested)
                                {
                                    cancel = true;
                                    break;
                                }
                            }
                            if (!cancel)
                            {
                                if (result)
                                {
                                    if (!await _dictionaryCacheIoIndexObject[ioFileName].PushOrUpdateListIoBlockData(listBlockObject[ioFileName], keepAlive, cancellationIoCache))
                                    {
                                        result = false;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskEnableMultiTask && ioFileNameArray.Length > 1)
                    {
                        while (totalTaskDone < totalTaskToDo)
                        {
                            if (cancel || !result)
                            {
                                break;
                            }

                            if (cancellationIoCache != null)
                            {
                                try
                                {
                                    await Task.Delay(_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskParallelTaskWaitDelay, cancellationIoCache.Token);
                                }
                                catch
                                {
                                    break;
                                }
                            }
                            else
                            {
                                await Task.Delay(_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskParallelTaskWaitDelay);
                            }
                        }
                    }

                    cancellation.Cancel();

                    // Clean up.
                    Array.Clear(ioFileNameArray, 0, ioFileNameArray.Length);
                    listBlockObject.Clear();
                }

            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return result;
        }

        /// <summary>
        /// Try to delete io block data object from the io cache.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<bool> TryDeleteIoBlockObject(long blockHeight, CancellationTokenSource cancellationIoCache)
        {
            bool result = true;

            bool semaphoreUsed = false;

            try
            {

                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                semaphoreUsed = true;


                string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                {

                    _semaphoreIoCacheIndexAccess.Release();
                    semaphoreUsed = false;

                    result = await _dictionaryCacheIoIndexObject[ioFileName].TryDeleteIoBlockData(blockHeight, cancellationIoCache);

                }

            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return result;
        }

        /// <summary>
        /// Check if the io cache system contain the block height indexed.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<bool> ContainIoBlockHeight(long blockHeight, CancellationTokenSource cancellationIoCache)
        {
            bool result = false;

            bool useSemaphore = false;

            try
            {

                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;

                string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                {
                    _semaphoreIoCacheIndexAccess.Release();
                    useSemaphore = false;

                    result = await _dictionaryCacheIoIndexObject[ioFileName].ContainsIoBlockHeight(blockHeight, cancellationIoCache);
                }
            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return result;
        }

        /// <summary>
        /// Insert or update a block transaction directly to the io cache system.
        /// </summary>
        /// <param name="blockTransaction"></param>
        /// <param name="blockHeight"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<bool> InsertOrUpdateBlockTransactionObject(ClassBlockTransaction blockTransaction, long blockHeight, bool keepAlive, CancellationTokenSource cancellationIoCache)
        {
            bool insertOrUpdateResult = false;

            bool useSemaphore = false;

            try
            {
                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;

                if (blockHeight < BlockchainSetting.GenesisBlockHeight)
                {
                    blockHeight = blockTransaction.TransactionBlockHeightInsert;
                }

                // If the block height is provided.
                if (blockHeight >= BlockchainSetting.GenesisBlockHeight)
                {
                    string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                    if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                    {
                        _semaphoreIoCacheIndexAccess.Release();
                        useSemaphore = false;

                        if (await _dictionaryCacheIoIndexObject[ioFileName].PushOrUpdateTransactionOnIoBlockData(blockTransaction, blockHeight, keepAlive, cancellationIoCache))
                        {
                            insertOrUpdateResult = true;
                        }

                    }
                }


            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return insertOrUpdateResult;
        }


        /// <summary>
        /// Insert or update a block transaction directly to the io cache system.
        /// </summary>
        /// <param name="listBlockTransaction"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<bool> InsertOrUpdateListBlockTransactionObject(List<ClassBlockTransaction> listBlockTransaction, bool keepAlive, CancellationTokenSource cancellationIoCache)
        {
            bool insertOrUpdateResult = false;

            bool useSemaphore = false;

            try
            {
                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;

                bool result = true;

                Dictionary<string, List<ClassBlockTransaction>> listBlockTransactionToInsertOrUpdate = new Dictionary<string, List<ClassBlockTransaction>>();

                // Generate a list of block transaction object linked to the io file index.
                foreach (var blockTransaction in listBlockTransaction)
                {

                    string ioFileName = GetIoFileNameFromBlockHeight(blockTransaction.TransactionObject.BlockHeightTransaction);

                    if (!_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                    {
                        Tuple<bool, HashSet<long>> resultInit = await InitializeNewCacheIoIndex(ioFileName);

                        if (!resultInit.Item1)
                        {
                            result = false;

                            break;
                        }
                    }

                    if (!listBlockTransactionToInsertOrUpdate.ContainsKey(ioFileName))
                    {
                        listBlockTransactionToInsertOrUpdate.Add(ioFileName, new List<ClassBlockTransaction>()
                        {
                            blockTransaction
                        });
                    }
                    else
                    {
                        listBlockTransactionToInsertOrUpdate[ioFileName].Add(blockTransaction);
                    }
                }

                _semaphoreIoCacheIndexAccess.Release();
                useSemaphore = false;

                // Much faster insert/update.
                if (listBlockTransactionToInsertOrUpdate.Count > 0 && result)
                {
                    string[] ioFileNameArray = listBlockTransactionToInsertOrUpdate.Keys.ToArray();
                    int totalTaskToDo = ioFileNameArray.Length;
                    int totalTaskDone = 0;

                    CancellationTokenSource cancellation = new CancellationTokenSource();
                    bool cancel = false;


                    foreach (string ioFileName in ioFileNameArray)
                    {
                        if (_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskEnableMultiTask && ioFileNameArray.Length > 1)
                        {
                            try
                            {
                                await Task.Factory.StartNew(async () =>
                                {

                                    if (cancellationIoCache != null)
                                    {
                                        if (cancellationIoCache.IsCancellationRequested)
                                        {
                                            cancel = true;
                                        }
                                    }
                                    if (!cancel)
                                    {

                                        if (!await _dictionaryCacheIoIndexObject[ioFileName].PushOrUpdateListIoBlockTransactionData(listBlockTransactionToInsertOrUpdate[ioFileName], keepAlive, cancellationIoCache))
                                        {
                                            result = false;
                                        }
                                    }

                                    totalTaskDone++;
                                }, cancellation.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Ignored, catch the exception once tasks are cancelled.
                            }
                        }
                        else
                        {
                            if (cancellationIoCache != null)
                            {
                                if (cancellationIoCache.IsCancellationRequested)
                                {
                                    cancel = true;
                                    break;
                                }
                            }
                            if (!cancel)
                            {
                                if (result)
                                {
                                    if (!await _dictionaryCacheIoIndexObject[ioFileName].PushOrUpdateListIoBlockTransactionData(listBlockTransactionToInsertOrUpdate[ioFileName], keepAlive, cancellationIoCache))
                                    {
                                        result = false;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskEnableMultiTask && ioFileNameArray.Length > 1)
                    {
                        while (totalTaskDone < totalTaskToDo)
                        {
                            if (cancel || !result)
                            {
                                break;
                            }

                            if (cancellationIoCache != null)
                            {
                                try
                                {
                                    await Task.Delay(_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskParallelTaskWaitDelay, cancellationIoCache.Token);
                                }
                                catch
                                {
                                    break;
                                }
                            }
                            else
                            {
                                await Task.Delay(_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskParallelTaskWaitDelay);
                            }
                        }
                    }

                    cancellation.Cancel();

                    // Clean up.
                    Array.Clear(ioFileNameArray, 0, ioFileNameArray.Length);
                    listBlockTransactionToInsertOrUpdate.Clear();
                }


                insertOrUpdateResult = result;

            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return insertOrUpdateResult;
        }

        /// <summary>
        /// Check if a transaction hash exist on io blocks cached.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<bool> CheckTransactionHashExistOnIoBlockCached(string transactionHash, long blockHeight, CancellationTokenSource cancellationIoCache)
        {
            bool result = false;

            bool useSemaphore = false;

            try
            {
                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;

                long blockHeightFromTransactionHash = ClassTransactionUtility.GetBlockHeightFromTransactionHash(transactionHash);

                if (blockHeight != blockHeightFromTransactionHash || blockHeight < BlockchainSetting.GenesisBlockHeight)
                {
                    blockHeight = blockHeightFromTransactionHash;
                }

                if (_dictionaryCacheIoIndexObject.Count > 0)
                {

                    string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                    if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                    {
                        _semaphoreIoCacheIndexAccess.Release();
                        useSemaphore = false;


                        result = await _dictionaryCacheIoIndexObject[ioFileName].ContainIoBlockTransactionHash(transactionHash, blockHeight, cancellationIoCache);

                    }
                }

            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return result;
        }

        /// <summary>
        /// Retrieve every block transactions from a block object cached.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<SortedList<string, ClassBlockTransaction>> GetBlockTransactionListFromBlockHeightTarget(long blockHeight, bool keepAlive, CancellationTokenSource cancellationIoCache)
        {
            SortedList<string, ClassBlockTransaction> listBlockTransactions = new SortedList<string, ClassBlockTransaction>();

            bool useSemaphore = false;

            try
            {

                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;

                if (blockHeight >= BlockchainSetting.GenesisBlockHeight)
                {

                    string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                    if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                    {
                        _semaphoreIoCacheIndexAccess.Release();
                        useSemaphore = false;

                        ClassBlockObject blockObject = await _dictionaryCacheIoIndexObject[ioFileName].GetIoBlockDataFromBlockHeight(blockHeight, keepAlive, cancellationIoCache);

                        if (blockObject != null)
                        {

                            foreach (var blockTransaction in blockObject.BlockTransactions)
                            {
                                if (cancellationIoCache != null)
                                {
                                    if (cancellationIoCache.IsCancellationRequested)
                                    {
                                        break;
                                    }
                                }
                                if (blockTransaction.Value != null)
                                {
                                    if (!listBlockTransactions.ContainsKey(blockTransaction.Key))
                                    {
                                        listBlockTransactions.Add(blockTransaction.Key, blockTransaction.Value);
                                    }
                                    else
                                    {
                                        listBlockTransactions[blockTransaction.Key] = blockTransaction.Value;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return listBlockTransactions;
        }

        /// <summary>
        /// Insert or update a block transaction directly to the io cache system.
        /// </summary>
        /// <param name="transactionHash"></param>
        /// <param name="blockHeight"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<ClassBlockTransaction> GetBlockTransactionFromTransactionHashOnIoBlockCached(string transactionHash, long blockHeight, CancellationTokenSource cancellationIoCache)
        {
            ClassBlockTransaction blockTransaction = null;

            bool useSemaphore = false;

            try
            {

                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;

                // If the block height is provided.
                if (blockHeight >= BlockchainSetting.GenesisBlockHeight)
                {
                    string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                    if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                    {
                        _semaphoreIoCacheIndexAccess.Release();
                        useSemaphore = false;

                        blockTransaction = await _dictionaryCacheIoIndexObject[ioFileName].GetBlockTransactionFromIoBlockHeightByTransactionHash(blockHeight, transactionHash, cancellationIoCache);
                    }
                }
            }
            finally
            {

                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return blockTransaction;
        }

        /// <summary>
        /// Retrieve back a list of block object between a range.
        /// </summary>
        /// <param name="blockHeightStart"></param>
        /// <param name="blockHeightEnd"></param>
        /// <param name="listBlockHeightAlreadyCached"></param>
        /// <param name="listBlockAlreadyCached"></param>
        /// <param name="keepAlive"></param>
        /// <param name="cancellationIoCache"></param>
        /// <returns></returns>
        public async Task<SortedList<long, Tuple<ClassBlockObject, bool>>> GetBlockObjectListFromBlockHeightRange(long blockHeightStart, long blockHeightEnd, HashSet<long> listBlockHeightAlreadyCached, SortedList<long, Tuple<ClassBlockObject, bool>> listBlockAlreadyCached, bool keepAlive, CancellationTokenSource cancellationIoCache)
        {
            bool useSemaphore = false;

            try
            {
                await _semaphoreIoCacheIndexAccess.WaitAsync(cancellationIoCache.Token);
                useSemaphore = true;

                Dictionary<string, List<long>> listBlockHeightIndexedByIoFile = new Dictionary<string, List<long>>();

                bool error = false;

                for (long i = blockHeightStart - 1; i < blockHeightEnd; i++)
                {
                    long blockHeight = i + 1;
                    if (blockHeight <= blockHeightEnd)
                    {
                        string ioFileName = GetIoFileNameFromBlockHeight(blockHeight);

                        if (_dictionaryCacheIoIndexObject.ContainsKey(ioFileName))
                        {
                            if (!listBlockHeightIndexedByIoFile.ContainsKey(ioFileName))
                            {
                                listBlockHeightIndexedByIoFile.Add(ioFileName, new List<long>());
                            }

                            if (!listBlockAlreadyCached.ContainsKey(blockHeight))
                            {
                                listBlockHeightIndexedByIoFile[ioFileName].Add(blockHeight);
                            }
                        }
                    }
                    else
                    {
                        break;
                    }
                }


                _semaphoreIoCacheIndexAccess.Release();
                useSemaphore = false;

                if (listBlockHeightIndexedByIoFile.Count > 0)
                {
                    if (!error)
                    {
                        string[] ioFileNameArray = listBlockHeightIndexedByIoFile.Keys.ToArray();
                        int totalTaskToDo = ioFileNameArray.Length;
                        int totalTaskDone = 0;
                        bool cancel = false;

                        if (_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskEnableMultiTask && ioFileNameArray.Length > 1)
                        {
                            CancellationTokenSource cancellation = new CancellationTokenSource();

                            foreach (string ioFileName in ioFileNameArray)
                            {
                                try
                                {
                                    await Task.Factory.StartNew(async () =>
                                    {
                                        if (listBlockHeightIndexedByIoFile[ioFileName].Count > 0)
                                        {
                                            if (!cancel)
                                            {
                                                using (DisposableList<ClassBlockObject> listBlockObject = await _dictionaryCacheIoIndexObject[ioFileName].GetIoListBlockDataFromListBlockHeight(new HashSet<long>(listBlockHeightIndexedByIoFile[ioFileName]), keepAlive, cancellationIoCache))
                                                {
                                                    if (listBlockObject.Count > 0)
                                                    {
                                                        foreach (ClassBlockObject blockObject in listBlockObject.GetAll)
                                                        {
                                                            if (cancellationIoCache != null)
                                                            {
                                                                if (cancellationIoCache.IsCancellationRequested)
                                                                {
                                                                    cancel = true;
                                                                    break;
                                                                }
                                                            }
                                                            if (blockObject != null)
                                                            {
                                                                if (!listBlockAlreadyCached.ContainsKey(blockObject.BlockHeight) && !listBlockHeightAlreadyCached.Contains(blockObject.BlockHeight))
                                                                {
                                                                    listBlockAlreadyCached.Add(blockObject.BlockHeight, new Tuple<ClassBlockObject, bool>(blockObject, true));
                                                                }
                                                            }
                                                        }

                                                    }
                                                }
                                            }
                                            totalTaskDone++;
                                        }
                                    }, cancellation.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                                }
                                catch
                                {
                                    // Ignored, catch the exception once tasks are cancelled.
                                }
                            }
                            while (totalTaskDone < totalTaskToDo)
                            {
                                if (cancel)
                                {
                                    break;
                                }

                                if (cancellationIoCache != null)
                                {
                                    try
                                    {
                                        await Task.Delay(_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskParallelTaskWaitDelay, cancellationIoCache.Token);
                                    }
                                    catch
                                    {
                                        break;
                                    }
                                }
                                else
                                {
                                    await Task.Delay(_blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskParallelTaskWaitDelay);
                                }
                            }

                            cancellation.Cancel();
                        }
                        else
                        {
                            foreach (string ioFileName in ioFileNameArray)
                            {
                                if (listBlockHeightIndexedByIoFile[ioFileName].Count > 0)
                                {
                                    if (!cancel)
                                    {
                                        using (DisposableList<ClassBlockObject> listBlockObject = await _dictionaryCacheIoIndexObject[ioFileName].GetIoListBlockDataFromListBlockHeight(new HashSet<long>(listBlockHeightIndexedByIoFile[ioFileName]), keepAlive, cancellationIoCache))
                                        {
                                            if (listBlockObject.Count > 0)
                                            {
                                                foreach (ClassBlockObject blockObject in listBlockObject.GetAll)
                                                {
                                                    if (cancellationIoCache != null)
                                                    {
                                                        if (cancellationIoCache.IsCancellationRequested)
                                                        {
                                                            cancel = true;
                                                            break;
                                                        }
                                                    }
                                                    if (blockObject != null)
                                                    {
                                                        if (!listBlockAlreadyCached.ContainsKey(blockObject.BlockHeight) && !listBlockHeightAlreadyCached.Contains(blockObject.BlockHeight))
                                                        {
                                                            listBlockAlreadyCached.Add(blockObject.BlockHeight, new Tuple<ClassBlockObject, bool>(blockObject, true));
                                                        }
                                                    }
                                                }

                                            }
                                        }
                                    }
                                    else
                                    {
                                        break;
                                    }
                                    totalTaskDone++;
                                }
                            }
                        }



                        // Clean up.
                        if (ioFileNameArray.Length > 0)
                        {
                            Array.Clear(ioFileNameArray, 0, ioFileNameArray.Length);
                        }
                    }

                    // Clean up.
                    listBlockHeightIndexedByIoFile.Clear();
                }
            }
            finally
            {
                if (useSemaphore)
                {
                    _semaphoreIoCacheIndexAccess.Release();
                }
            }

            return listBlockAlreadyCached;
        }


        #endregion

        #region Functions dedicated to io files indexing.

        /// <summary>
        /// Return the io file name depending of the block height and the limit of max block inside a io cache file.
        /// </summary>
        /// <param name="blockHeight"></param>
        /// <returns></returns>
        private string GetIoFileNameFromBlockHeight(long blockHeight)
        {
            return ((blockHeight / _blockchainDatabaseSetting.BlockchainCacheSetting.IoCacheDiskMaxBlockPerFile) + IoFileExtension);
        }

        /// <summary>
        /// Return the total memory usage from the io cache system.
        /// </summary>
        /// <returns></returns>
        public long GetIoCacheSystemMemoryConsumption(CancellationTokenSource cancellation)
        {
            if (_dictionaryCacheIoIndexObject.Count > 0)
            {
                long totalMemoryUsagePendingCalculation = 0;

                foreach (string ioFileName in _dictionaryCacheIoIndexObject.Keys.ToArray())
                {
                    if (cancellation != null)
                        cancellation.Token.ThrowIfCancellationRequested();

                    totalMemoryUsagePendingCalculation += _dictionaryCacheIoIndexObject[ioFileName].GetIoMemoryUsage(cancellation);
                }

                _totalIoCacheSystemMemoryUsage = totalMemoryUsagePendingCalculation;
            }

            return _totalIoCacheSystemMemoryUsage;
        }

        #endregion
    }
}

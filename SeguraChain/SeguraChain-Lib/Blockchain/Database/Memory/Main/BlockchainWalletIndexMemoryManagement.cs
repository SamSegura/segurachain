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
using SeguraChain_Lib.Blockchain.Database.DatabaseSetting;
using SeguraChain_Lib.Blockchain.Database.Memory.Main.Object;
using SeguraChain_Lib.Blockchain.Wallet.Object.Blockchain;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Other.Object.List;

using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Blockchain.Database.Memory.Main
{
    /// <summary>
    /// Store wallet address and checkpoints.
    /// </summary>
    public class BlockchainWalletIndexMemoryManagement
    {
        /// <summary>
        /// Wallet index files.
        /// </summary>
        private const string WalletIndexFilename = "wallet";
        private const string WalletIndexFileExtension = ".index";

        /// <summary>
        /// Wallet index file data content format.
        /// </summary>
        private const string WalletIndexBegin = ">WALLET-INDEX-BEGIN=";
        private const string WalletIndexBeginStringClose = "~";
        private const string WalletIndexEnd = "WALLET-INDEX-END<";
        private const string WalletIndexCheckpointLineDataSeperator = ";";
        private const string WalletCheckpointDataSeperator = "|";

        /// <summary>
        /// Store the range of amount of wallet address index files.
        /// </summary>
        private BigInteger _walletAddressMaxPossibilities = BigInteger.Pow(2, 512);

        /// <summary>
        /// Database.
        /// </summary>
        private ConcurrentDictionary<string, BlockchainWalletMemoryObject> _dictionaryBlockchainWalletIndexDataObjectMemory;

        /// <summary>
        /// Settings.
        /// </summary>
        private ClassBlockchainDatabaseSetting _blockchainDatabaseSetting;

        /// <summary>
        /// Multithreading access.
        /// </summary>
        private SemaphoreSlim _semaphoreBlockchainWalletIndexDataAccess;

        /// <summary>
        /// Store the last memory usage.
        /// </summary>
        public long LastMemoryUsage { get; private set; }

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="blockchainDatabaseSetting"></param>
        public BlockchainWalletIndexMemoryManagement(ClassBlockchainDatabaseSetting blockchainDatabaseSetting)
        {
            _blockchainDatabaseSetting = blockchainDatabaseSetting;

          
            _dictionaryBlockchainWalletIndexDataObjectMemory = new ConcurrentDictionary<string, BlockchainWalletMemoryObject>();
            _semaphoreBlockchainWalletIndexDataAccess = new SemaphoreSlim(1, 1);

            InitializeBlockchainWalletIndexDataCache();
        }

        #region Original Dictionary functions.
        
        /// <summary>
        /// Return back a wallet index data object by wallet address.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public BlockchainWalletMemoryObject GetBlockchainWalletMemoryObject(string walletAddress, CancellationTokenSource cancellation)
        {
            if (ContainsKey(walletAddress, cancellation, out BlockchainWalletMemoryObject blockchainWalletMemoryObject))
            {
                return blockchainWalletMemoryObject;
            }
            return null;
        }

        /// <summary>
        /// Update a wallet index data.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="blockchainWalletMemoryObject"></param>
        /// <param name="cancellation"></param>
        public void UpdateWalletIndexData(string walletAddress, BlockchainWalletMemoryObject blockchainWalletMemoryObject, CancellationTokenSource cancellation)
        {
            TryUpdateWalletIndexMemoryData(walletAddress, blockchainWalletMemoryObject, cancellation);
        }

        /// <summary>
        /// Check if the database or cache files contains the wallet address target.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="cancellation"></param>
        /// <param name="blockchainWalletMemoryObject"></param>
        /// <returns></returns>
        public bool ContainsKey(string walletAddress, CancellationTokenSource cancellation, out BlockchainWalletMemoryObject blockchainWalletMemoryObject)
        {

            bool result = false;
            bool semaphoreUsed = false;
            try
            {
                if (cancellation != null)
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Wait(cancellation.Token);
                }
                else
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Wait();
                }
                semaphoreUsed = true;

                if (_dictionaryBlockchainWalletIndexDataObjectMemory.ContainsKey(walletAddress))
                {
                    _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress].LastTimestampCallOrUpdate = ClassUtility.GetCurrentTimestampInMillisecond();
                    blockchainWalletMemoryObject = new BlockchainWalletMemoryObject()
                    {
                        LastTimestampCallOrUpdate = _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress].LastTimestampCallOrUpdate,
                        ListBlockchainWalletBalanceCheckpoints = _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress].ListBlockchainWalletBalanceCheckpoints,
                        MemorySize = _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress].MemorySize,
                        Updated = _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress].Updated
                    };
                    result = true;
                }
                else
                {
                    blockchainWalletMemoryObject = TryRetrieveWalletIndex(walletAddress, GetWalletIndexDataCacheFilename(walletAddress), out bool exist);
                    if (exist || blockchainWalletMemoryObject != null)
                    {
                        result = true;
                    }
                }

                _semaphoreBlockchainWalletIndexDataAccess.Release();
                semaphoreUsed = false;
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Release();
                }
            }
            return result;
        }

        /// <summary>
        /// Clear every wallet index data from memory and the cache.
        /// </summary>
        public void Clear()
        {
            bool semaphoreUsed = false;
            try
            {
                _semaphoreBlockchainWalletIndexDataAccess.Wait();
                semaphoreUsed = true;

                _dictionaryBlockchainWalletIndexDataObjectMemory.Clear();

                if (Directory.Exists(_blockchainDatabaseSetting.GetBlockchainWalletIndexCacheDirectoryPath))
                {
                    foreach(string walletIndexCacheFile in Directory.GetFiles(_blockchainDatabaseSetting.GetBlockchainWalletIndexCacheDirectoryPath))
                    {
                        File.Delete(walletIndexCacheFile);
                    }
                }

                _semaphoreBlockchainWalletIndexDataAccess.Release();
                semaphoreUsed = false;
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Release();
                }
            }
        }

        /// <summary>
        /// Try to insert a new wallet address to index.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public bool TryAdd(string walletAddress, CancellationTokenSource cancellation)
        {
            bool result;
            bool semaphoreUsed = false;

            try
            {
                if (cancellation != null)
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Wait(cancellation.Token);
                }
                else
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Wait();
                }
                semaphoreUsed = true;

                result = _dictionaryBlockchainWalletIndexDataObjectMemory.TryAdd(walletAddress, new BlockchainWalletMemoryObject()
                {
                    Updated = true
                });

                _semaphoreBlockchainWalletIndexDataAccess.Release();
                semaphoreUsed = false;
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Release();
                }
            }
            return result;
        }

        #endregion

        #region Wallet index data cache management functions.
 
        /// <summary>
        /// Initialization of the blockchain wallet index data cache.
        /// </summary>
        /// <returns></returns>
        public bool InitializeBlockchainWalletIndexDataCache()
        {
            bool result = false;

            if (!Directory.Exists(_blockchainDatabaseSetting.GetBlockchainWalletIndexCacheDirectoryPath))
            {
                Directory.CreateDirectory(_blockchainDatabaseSetting.GetBlockchainWalletIndexCacheDirectoryPath);
                result = true;
            }
            else
            {
                string[] walletIndexCacheFiles = Directory.GetFiles(_blockchainDatabaseSetting.GetBlockchainWalletIndexCacheDirectoryPath, "*"+WalletIndexFileExtension, SearchOption.TopDirectoryOnly);

                if(walletIndexCacheFiles.Length > 0)
                {
                    bool failed = false;
                    foreach (var walletIndexCacheFile in walletIndexCacheFiles)
                    {
                        if (!InitializeWalletIndexDataFromCachedFile(walletIndexCacheFile))
                        {
                            failed = true;
                            break;
                        }
                    }

                    if (!failed)
                    {
                        result = true;
                    }
                }
                else
                {
                    result = true;
                }

            }

            return result;
        }

        /// <summary>
        /// Read wallet index data cache file and initialize their data in memory.
        /// </summary>
        /// <param name="walletIndexCacheFile"></param>
        /// <returns></returns>
        private bool InitializeWalletIndexDataFromCachedFile(string walletIndexCacheFile)
        {
            bool result = true;
            BlockchainWalletMemoryObject blockchainWalletMemoryObject = new BlockchainWalletMemoryObject();

            try
            {
                using (StreamReader reader = new StreamReader(walletIndexCacheFile))
                {
                    string line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith(WalletIndexBegin))
                        {
                            string walletAddress = line.GetStringBetweenTwoStrings(WalletIndexBegin, WalletIndexBeginStringClose);

                            bool failed = false;
                            bool completeRead = false;



                            while (true)
                            {
                                line = reader.ReadLine();

                                if (line != null)
                                {
                                    if (line.StartsWith(WalletIndexEnd))
                                    {
                                        completeRead = true;
                                        break;
                                    }

                                    using (DisposableList<string> listCheckpointCombinedSplitted = line.DisposableSplit(WalletIndexCheckpointLineDataSeperator))
                                    {
                                        if (listCheckpointCombinedSplitted.Count > 0)
                                        {
                                            foreach (string checkpointCombinedLineData in listCheckpointCombinedSplitted.GetAll)
                                            {
                                                using (DisposableList<string> checkpointDataSplitted = checkpointCombinedLineData.DisposableSplit(WalletCheckpointDataSeperator))
                                                {
                                                    if (!long.TryParse(checkpointDataSplitted[0], out long blockHeight))
                                                    {
                                                        failed = true;
                                                        break;
                                                    }

                                                    if (!BigInteger.TryParse(checkpointDataSplitted[1], out BigInteger lastWalletBalance))
                                                    {
                                                        failed = true;
                                                        break;
                                                    }

                                                    if (!BigInteger.TryParse(checkpointDataSplitted[2], out BigInteger lastWalletPendingBalance))
                                                    {
                                                        failed = true;
                                                        break;
                                                    }

                                                    if (!int.TryParse(checkpointDataSplitted[3], out int totalTx))
                                                    {
                                                        failed = true;
                                                        break;
                                                    }

                                                    if (!blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints.ContainsKey(blockHeight))
                                                    {
                                                        blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints.Add(blockHeight, new ClassBlockchainWalletBalanceCheckpointObject()
                                                        {
                                                            BlockHeight = blockHeight,
                                                            LastWalletBalance = lastWalletBalance,
                                                            LastWalletPendingBalance = lastWalletPendingBalance,
                                                            TotalTx = totalTx
                                                        });
                                                    }
                                                }

                                                if (failed)
                                                {
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    break;
                                }
                            }

                            if (!failed && completeRead)
                            {
                                blockchainWalletMemoryObject.MemorySize = CalculateMemorySizeFromBlockchainWalletMemoryObject(walletAddress, blockchainWalletMemoryObject);
                                blockchainWalletMemoryObject.LastTimestampCallOrUpdate = ClassUtility.GetCurrentTimestampInMillisecond();

                                if (!_dictionaryBlockchainWalletIndexDataObjectMemory.ContainsKey(walletAddress))
                                {
                                    if (CanInsertObjectInActiveMemory(walletAddress, blockchainWalletMemoryObject))
                                    {
                                        _dictionaryBlockchainWalletIndexDataObjectMemory.TryAdd(walletAddress, blockchainWalletMemoryObject);
                                    }
                                }
                                else
                                {
                                    _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress] = blockchainWalletMemoryObject;
                                }
                            }
                        }
                    }
                }
            }
            catch(Exception error)
            {
#if DEBUG
                Debug.WriteLine("Error on initialize wallet index data from cached files. Exception: "+error.Message);
#endif
                ClassLog.WriteLine("Error on initialize wallet index data from cached files. Exception: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_MEMORY_MANAGER, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);
                result = false;
            }
            return result;
        }

        /// <summary>
        /// Try to retreive a wallet index data, indicate if the wallet address target exist on the file.
        /// </summary>
        /// <param name="walletIndexCacheFile"></param>
        /// <param name="walletAddressTarget"></param>
        /// <param name="exist"></param>
        /// <returns></returns>
        private BlockchainWalletMemoryObject TryRetrieveWalletIndex(string walletIndexCacheFile, string walletAddressTarget, out bool exist)
        {
            exist = false; // Default.
            BlockchainWalletMemoryObject blockchainWalletMemoryObject = null;

            if (File.Exists(_blockchainDatabaseSetting.GetBlockchainWalletIndexCacheDirectoryPath + walletIndexCacheFile))
            {
                using (StreamReader reader = new StreamReader(walletIndexCacheFile))
                {
                    string line;

                    // Continue to read, the system cam write multiple times if the wallet index file cached has been updated and not purged yet.
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.StartsWith(WalletIndexBegin))
                        {
                            string walletAddress = line.GetStringBetweenTwoStrings(WalletIndexBegin, WalletIndexBeginStringClose);

                            if (walletAddressTarget == walletAddress)
                            {
                                exist = true;
                                bool failed = false;
                                bool completeRead = false;

                                if (blockchainWalletMemoryObject == null)
                                {
                                    blockchainWalletMemoryObject = new BlockchainWalletMemoryObject();
                                }

                                while (true)
                                {
                                    line = reader.ReadLine();

                                    if (line != null)
                                    {
                                        if (line.StartsWith(WalletIndexEnd))
                                        {
                                            completeRead = true;
                                            break;
                                        }

                                        using (DisposableList<string> listCheckpointCombinedSplitted = line.DisposableSplit(WalletIndexCheckpointLineDataSeperator))
                                        {
                                            if (listCheckpointCombinedSplitted.Count > 0)
                                            {
                                                foreach (string checkpointCombinedLineData in listCheckpointCombinedSplitted.GetAll)
                                                {
                                                    using (DisposableList<string> checkpointDataSplitted = checkpointCombinedLineData.DisposableSplit(WalletCheckpointDataSeperator))
                                                    {
                                                        if (!long.TryParse(checkpointDataSplitted[0], out long blockHeight))
                                                        {
                                                            failed = true;
                                                            break;
                                                        }

                                                        if (!BigInteger.TryParse(checkpointDataSplitted[1], out BigInteger lastWalletBalance))
                                                        {
                                                            failed = true;
                                                            break;
                                                        }

                                                        if (!BigInteger.TryParse(checkpointDataSplitted[2], out BigInteger lastWalletPendingBalance))
                                                        {
                                                            failed = true;
                                                            break;
                                                        }

                                                        if (!int.TryParse(checkpointDataSplitted[3], out int totalTx))
                                                        {
                                                            failed = true;
                                                            break;
                                                        }

                                                        if (!blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints.ContainsKey(blockHeight))
                                                        {
                                                            blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints.Add(blockHeight, new ClassBlockchainWalletBalanceCheckpointObject()
                                                            {
                                                                BlockHeight = blockHeight,
                                                                LastWalletBalance = lastWalletBalance,
                                                                LastWalletPendingBalance = lastWalletPendingBalance,
                                                                TotalTx = totalTx
                                                            });
                                                        }
                                                    }

                                                    if (failed)
                                                    {
                                                        break;
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }

                                if (!failed && completeRead)
                                {
                                    blockchainWalletMemoryObject.MemorySize = CalculateMemorySizeFromBlockchainWalletMemoryObject(walletAddress, blockchainWalletMemoryObject);
                                    blockchainWalletMemoryObject.LastTimestampCallOrUpdate = ClassUtility.GetCurrentTimestampInMillisecond();
                                }
                            }
                        }
                    }
                }
            }


            return blockchainWalletMemoryObject;
        }

        /// <summary>
        /// Retrieve all wallet index cached.
        /// </summary>
        /// <returns></returns>
        public IEnumerable<Tuple<string, BlockchainWalletMemoryObject>> RetrieveAllWalletIndexCached()
        {
            bool semaphoreUsed = false;
            try
            {
                _semaphoreBlockchainWalletIndexDataAccess.Wait();
                semaphoreUsed = true;

                if (_dictionaryBlockchainWalletIndexDataObjectMemory.Count > 0)
                {
                    foreach (var walletAddress in _dictionaryBlockchainWalletIndexDataObjectMemory.Keys)
                    {
                        yield return new Tuple<string, BlockchainWalletMemoryObject>(walletAddress, _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress]);
                    }
                }

                string[] walletIndexCacheFileArray = Directory.GetFiles(_blockchainDatabaseSetting.GetBlockchainWalletIndexCacheDirectoryPath);

                if (walletIndexCacheFileArray.Length > 0)
                {
                    foreach(string walletIndexCacheFile in walletIndexCacheFileArray)
                    {
                        using (StreamReader reader = new StreamReader(walletIndexCacheFile))
                        {
                            string line;

                            // Continue to read, the system cam write multiple times if the wallet index file cached has been updated and not purged yet.
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.StartsWith(WalletIndexBegin))
                                {
                                    string walletAddress = line.GetStringBetweenTwoStrings(WalletIndexBegin, WalletIndexBeginStringClose);

                                    bool failed = false;
                                    bool completeRead = false;


                                    BlockchainWalletMemoryObject blockchainWalletMemoryObject = new BlockchainWalletMemoryObject();


                                    while (true)
                                    {
                                        line = reader.ReadLine();

                                        if (line != null)
                                        {
                                            if (line.StartsWith(WalletIndexEnd))
                                            {
                                                completeRead = true;
                                                break;
                                            }

                                            using (DisposableList<string> listCheckpointCombinedSplitted = line.DisposableSplit(WalletIndexCheckpointLineDataSeperator))
                                            {
                                                if (listCheckpointCombinedSplitted.Count > 0)
                                                {
                                                    foreach (string checkpointCombinedLineData in listCheckpointCombinedSplitted.GetAll)
                                                    {
                                                        using (DisposableList<string> checkpointDataSplitted = checkpointCombinedLineData.DisposableSplit(WalletCheckpointDataSeperator))
                                                        {
                                                            if (!long.TryParse(checkpointDataSplitted[0], out long blockHeight))
                                                            {
                                                                failed = true;
                                                                break;
                                                            }

                                                            if (!BigInteger.TryParse(checkpointDataSplitted[1], out BigInteger lastWalletBalance))
                                                            {
                                                                failed = true;
                                                                break;
                                                            }

                                                            if (!BigInteger.TryParse(checkpointDataSplitted[2], out BigInteger lastWalletPendingBalance))
                                                            {
                                                                failed = true;
                                                                break;
                                                            }

                                                            if (!int.TryParse(checkpointDataSplitted[3], out int totalTx))
                                                            {
                                                                failed = true;
                                                                break;
                                                            }

                                                            if (!blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints.ContainsKey(blockHeight))
                                                            {
                                                                blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints.Add(blockHeight, new ClassBlockchainWalletBalanceCheckpointObject()
                                                                {
                                                                    BlockHeight = blockHeight,
                                                                    LastWalletBalance = lastWalletBalance,
                                                                    LastWalletPendingBalance = lastWalletPendingBalance,
                                                                    TotalTx = totalTx
                                                                });
                                                            }
                                                        }

                                                        if (failed)
                                                        {
                                                            break;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            break;
                                        }
                                    }

                                    if (!failed && completeRead)
                                    {
                                        yield return new Tuple<string, BlockchainWalletMemoryObject>(walletAddress, blockchainWalletMemoryObject);
                                    }

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
                    _semaphoreBlockchainWalletIndexDataAccess.Release();
                }
            }
        }

        /// <summary>
        /// Try to update a wallet index data in memory or write his content on the wallet index data cache file.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="blockchainWalletMemoryObject"></param>
        /// <param name="cancellation"></param>
        private void TryUpdateWalletIndexMemoryData(string walletAddress, BlockchainWalletMemoryObject blockchainWalletMemoryObject, CancellationTokenSource cancellation)
        {
            bool semaphoreUsed = false;
            try
            {
                if (cancellation != null)
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Wait(cancellation.Token);
                }
                else
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Wait();
                }
                semaphoreUsed = true;

                blockchainWalletMemoryObject.MemorySize = CalculateMemorySizeFromBlockchainWalletMemoryObject(walletAddress, blockchainWalletMemoryObject);
                blockchainWalletMemoryObject.LastTimestampCallOrUpdate = ClassUtility.GetCurrentTimestampInMillisecond();
                blockchainWalletMemoryObject.Updated = true;

                if (_dictionaryBlockchainWalletIndexDataObjectMemory.ContainsKey(walletAddress))
                {
                    _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress] = blockchainWalletMemoryObject;
                }
                else
                {
                    if (CanInsertObjectInActiveMemory(walletAddress, blockchainWalletMemoryObject))
                    {
                        _dictionaryBlockchainWalletIndexDataObjectMemory.TryAdd(walletAddress, blockchainWalletMemoryObject);
                    }
                    else
                    {
                        WriteWalletIndexData(walletAddress, blockchainWalletMemoryObject);
                    }
                }

                _semaphoreBlockchainWalletIndexDataAccess.Release();
                semaphoreUsed = false;
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Release();
                }
            }
        }

        /// <summary>
        /// Write wallet index data to the specific wallet index data cache file.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="blockchainWalletMemoryObject"></param>
        private void WriteWalletIndexData(string walletAddress, BlockchainWalletMemoryObject blockchainWalletMemoryObject)
        {
            if (!File.Exists(_blockchainDatabaseSetting.GetBlockchainWalletIndexCacheDirectoryPath + GetWalletIndexDataCacheFilename(walletAddress)))
            {
                File.Create(_blockchainDatabaseSetting.GetBlockchainWalletIndexCacheDirectoryPath + GetWalletIndexDataCacheFilename(walletAddress)).Close();
            }
            using(StreamWriter writer = new StreamWriter(_blockchainDatabaseSetting.GetBlockchainWalletIndexCacheDirectoryPath + GetWalletIndexDataCacheFilename(walletAddress), true))
            {
                foreach(string walletDataLine in WalletMemoryObjectToWalletFileStringDataCache(walletAddress, blockchainWalletMemoryObject))
                {
                    writer.WriteLine(walletDataLine);
                }
            }
        }

        /// <summary>
        /// Update the blockchain wallet index memory cache.
        /// </summary>
        /// <param name="cancellation"></param>
        /// <returns></returns>
        public async Task<long> UpdateBlockchainWalletIndexMemoryCache(CancellationTokenSource cancellation)
        {
            bool semaphoreUsed = false;
            long totalWalletIndexOutOfMemory = 0;

            try
            {
                if (cancellation != null)
                {
                    await _semaphoreBlockchainWalletIndexDataAccess.WaitAsync(cancellation.Token);
                }
                else
                {
                    await _semaphoreBlockchainWalletIndexDataAccess.WaitAsync();
                }

                semaphoreUsed = true;

                long totalWalletIndexKeepInMemory = 0;
                long totalMemoryUsage = 0;

                if (_dictionaryBlockchainWalletIndexDataObjectMemory.Count > 0)
                {
                    foreach(string walletAddress in _dictionaryBlockchainWalletIndexDataObjectMemory.Keys)
                    {
                        if (_dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress].LastTimestampCallOrUpdate + _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalMaxDelayKeepAliveWalletIndexCached < ClassUtility.GetCurrentTimestampInMillisecond())
                        {
                            if (_dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress].Updated)
                            {
                                WriteWalletIndexData(walletAddress, _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress]);
                            }
                            if(_dictionaryBlockchainWalletIndexDataObjectMemory.TryRemove(walletAddress, out _))
                            {
                                totalWalletIndexOutOfMemory++;
                            }
                            else
                            {
                                totalMemoryUsage += _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress].MemorySize;
                                totalWalletIndexKeepInMemory++;
                            }
                        }
                        else
                        {
                            totalMemoryUsage += _dictionaryBlockchainWalletIndexDataObjectMemory[walletAddress].MemorySize;
                            totalWalletIndexKeepInMemory++;
                        }
                    }
                }

                LastMemoryUsage = totalMemoryUsage;
#if DEBUG
                Debug.WriteLine("Total wallet index data put out of memory: " + totalWalletIndexOutOfMemory + " | Total keep in memory: " + totalWalletIndexKeepInMemory);
                Debug.WriteLine("Total memory usage from wallet index keep alive: " + ClassUtility.ConvertBytesToMegabytes(totalMemoryUsage) + "/" + ClassUtility.ConvertBytesToMegabytes(_blockchainDatabaseSetting.BlockchainCacheSetting.GlobalCacheMaxWalletIndexKeepMemorySize));
#endif
                _semaphoreBlockchainWalletIndexDataAccess.Release();
                semaphoreUsed = false;
            }
            finally
            {
                if (semaphoreUsed)
                {
                    _semaphoreBlockchainWalletIndexDataAccess.Release();
                }
            }

            return totalWalletIndexOutOfMemory;
        }

        #endregion

        #region Wallet Index data format functions.

        /// <summary>
        /// Convert a wallet memory object data into a wallet file string data cache.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="blockchainWalletMemoryObject"></param>
        /// <returns></returns>
        private IEnumerable<string> WalletMemoryObjectToWalletFileStringDataCache(string walletAddress, BlockchainWalletMemoryObject blockchainWalletMemoryObject)
        {
            yield return WalletIndexBegin + walletAddress + WalletIndexBeginStringClose;

            if (blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints.Count > 0)
            {
                int totalWritten = 0;

                string blockchainWalletCheckpoints = string.Empty;

                foreach (var checkpoint in blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints)
                {
                    // Block height | last balance | last pending balance | total tx.
                    blockchainWalletCheckpoints += checkpoint.Key + WalletCheckpointDataSeperator + checkpoint.Value.LastWalletBalance + WalletCheckpointDataSeperator + checkpoint.Value.TotalTx + WalletCheckpointDataSeperator + checkpoint.Value.LastWalletPendingBalance + WalletIndexCheckpointLineDataSeperator;
                    totalWritten++;

                    if (totalWritten >= _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalCacheMaxWalletIndexCheckpointsPerLine)
                    {
                        yield return blockchainWalletCheckpoints;
                        totalWritten = 0;
                        blockchainWalletCheckpoints = string.Empty;
                    }
                }

                if (!blockchainWalletCheckpoints.IsNullOrEmpty(out _))
                {
                    yield return blockchainWalletCheckpoints;
                }
            }

            yield return WalletIndexEnd;
        }

        #endregion

        #region Wallet Index data cache memory functions.

        /// <summary>
        /// Indicate if the object can be inserted into the active memory depending of the amount of memory spend.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="blockchainWalletMemoryObject"></param>
        /// <returns></returns>
        private bool CanInsertObjectInActiveMemory(string walletAddress, BlockchainWalletMemoryObject blockchainWalletMemoryObject)
        {
            long totalMemoryUsage = 0;

            #region Calculate the memory usage of the object to insert.

            blockchainWalletMemoryObject.MemorySize = CalculateMemorySizeFromBlockchainWalletMemoryObject(walletAddress, blockchainWalletMemoryObject);

            totalMemoryUsage += blockchainWalletMemoryObject.MemorySize;

            #endregion

            #region Merge memory usage previously calculated from objects stored.

            foreach (var key in _dictionaryBlockchainWalletIndexDataObjectMemory.Keys)
            {
                totalMemoryUsage += _dictionaryBlockchainWalletIndexDataObjectMemory[key].MemorySize;
            }

            #endregion

            LastMemoryUsage = totalMemoryUsage;

            return totalMemoryUsage <= _blockchainDatabaseSetting.BlockchainCacheSetting.GlobalCacheMaxWalletIndexKeepMemorySize;
        }

        /// <summary>
        /// Calculate the memory size spend by a blockchain wallet memory object.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <param name="blockchainWalletMemoryObject"></param>
        /// <returns></returns>
        private long CalculateMemorySizeFromBlockchainWalletMemoryObject(string walletAddress, BlockchainWalletMemoryObject blockchainWalletMemoryObject)
        {
            long totalMemoryUsage = 0;

            totalMemoryUsage += (walletAddress.Length * sizeof(char));

            // Last timestamp memory size.
            totalMemoryUsage += sizeof(long);

            if (blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints.Count > 0)
            {
                // Total block heights keys memory size.
                totalMemoryUsage += (sizeof(long) * blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints.Count);

                foreach (long blockHeight in blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints.Keys.ToArray())
                {
                    totalMemoryUsage += sizeof(long);
                    totalMemoryUsage += sizeof(int);

                    if (blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints[blockHeight].LastWalletBalance > 0)
                    {
                        totalMemoryUsage += blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints[blockHeight].LastWalletBalance.ToByteArray().Length;
                    }

                    if (blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints[blockHeight].LastWalletPendingBalance > 0)
                    {
                        totalMemoryUsage += blockchainWalletMemoryObject.ListBlockchainWalletBalanceCheckpoints[blockHeight].LastWalletPendingBalance.ToByteArray().Length;
                    }

                }
            }

            return totalMemoryUsage;
        }

        #endregion

        #region Wallet Index data cache indexing functions.

        /// <summary>
        /// Calculate the wallet address index file name.
        /// </summary>
        /// <param name="walletAddress"></param>
        /// <returns></returns>
        private string GetWalletIndexDataCacheFilename(string walletAddress)
        {
            BigInteger indexFile = new BigInteger(ClassBase58.DecodeWithCheckSum(walletAddress, true));

            if (indexFile.Sign < 0)
            {
                indexFile *= -1;
            }


            if (indexFile > 0)
            {
                double percent = (((double)indexFile / (double)_walletAddressMaxPossibilities) * 100d);

                if (percent > 0)
                {

                    double indexCalculated = ((_blockchainDatabaseSetting.BlockchainCacheSetting.GlobalCacheMaxWalletIndexPerFile * percent));

                    indexFile = (BigInteger)indexCalculated;
                }
                else
                {
                    indexFile = 1;
                }
            }

            return WalletIndexFilename + indexFile + WalletIndexFileExtension;
        }

        #endregion
    }
}

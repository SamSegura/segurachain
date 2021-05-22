using SeguraChain_Lib.Blockchain.Block.Function;
using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using SeguraChain_Lib.Utility;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace SeguraChain_Lib.Blockchain.Database.Memory.Cache.Object.Systems.IO.Disk.Object
{
    public class ClassCacheIoStructureObject
    {
        
        /// <summary>
        /// Get/Set the block data. Synchronization forced, the monitor help to lock access from multithreading changes and notify changes done.
        /// </summary>
        public ClassBlockObject BlockObject
        {
            [MethodImpl(MethodImplOptions.Synchronized)]
            get
            {
                if (!IsNull)
                {

                    LastGetTimestamp = ClassUtility.GetCurrentTimestampInMillisecond();

                    return ClassUtility.LockReturnObject(_blockObject);
                }

            
                return null;
            }
            [MethodImpl(MethodImplOptions.Synchronized)]
            set
            {
                if (value != null)
                {
                    bool needPulse = false;
                    bool isNull = true;
                    if (!IsNull)
                    {
                        isNull = false;
                        if (Monitor.IsEntered(_blockObject))
                        {
                            needPulse = true;
                        }
                    }


                    if (!isNull)
                    {
                        lock (_blockObject)
                        {
                            if (value.BlockIsUpdated)
                                IsUpdated = true;

                            _blockObject = value;
                            _blockObject.BlockIsUpdated = false;
                            _blockObject.Disposed = false;

                            if (needPulse)
                            {
                                Monitor.PulseAll(_blockObject);
                            }
                        }
                    }
                    else
                    {
                        _blockObject = value;
                        _blockObject.Disposed = false;
                        IsUpdated = true;
                    }
                    
                    LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInMillisecond();

                }
                else
                {
                    if (!IsNull)
                    {
                        if (Monitor.IsEntered(_blockObject))
                        {
                            _blockObject.Dispose();
                            _blockObject?.BlockTransactions.Clear();
                            _blockObject = null;
                            _ioDataSizeOnMemory = 0;
                            IsUpdated = false;
                        }
                        else
                        {
                            _blockObject.Dispose();
                            _blockObject = null;
                            _ioDataSizeOnMemory = 0;
                            IsUpdated = false;
                        }
                    }
                }
            }
        }


        /// <summary>
        /// Store the block data.
        /// </summary>
        private ClassBlockObject _blockObject;

        /// <summary>
        /// The last position of the block data on the io cache file.
        /// </summary>
        public long IoDataPosition { get; set; }

        /// <summary>
        /// The last size of the block data on the io cache file.
        /// </summary>
        private long _ioDataSizeOnMemory;

        /// <summary>
        /// The last amount of memory of the block data.
        /// </summary>
        public long IoDataSizeOnMemory
        {
            get
            {
                if (_ioDataSizeOnMemory == 0)
                {
                    if (!IsNull)
                    {
                        lock (_blockObject)
                        {
                            _ioDataSizeOnMemory = ClassBlockUtility.GetIoBlockSizeOnMemory(_blockObject);
                        }
                    }
                }
                return _ioDataSizeOnMemory;
            }
            set => _ioDataSizeOnMemory = value;
        }

        /// <summary>
        /// The last size of the block data on the io cache file.
        /// </summary>
        public long IoDataSizeOnFile { get; set; }

        /// <summary>
        /// Indicate if the block data is written on the io cache file.
        /// </summary>
        public bool IsWritten { get; set; }

        /// <summary>
        /// Provide the last update timestamp.
        /// </summary>
        public long LastUpdateTimestamp { get; private set; }

        /// <summary>
        /// Provide the last get timestamp.
        /// </summary>
        public long LastGetTimestamp { get; private set; }

        /// <summary>
        /// Indicate if the block has been deleted.
        /// </summary>
        public bool IsDeleted { get; set; }

        /// <summary>
        /// Indicate if the block has been updated.
        /// </summary>
        public bool IsUpdated { get; private set; }

        /// <summary>
        /// Indicate if the block is empty.
        /// </summary>
        public bool IsNull
        {
            [MethodImpl(MethodImplOptions.Synchronized)]
            get
            {
                if (_blockObject == null)
                {
                    return true;
                }

                try
                {
                    if (Monitor.IsEntered(_blockObject))
                    {
                        try
                        {
                            if (_blockObject.Disposed)
                            {
                                return true;
                            }

                            if (_blockObject.BlockTransactions == null)
                            {
                                return true;
                            }

                            if (_blockObject.BlockTransactions.Count != _blockObject.TotalTransaction)
                            {
                                return true;
                            }

                            if (IsDeleted)
                            {
                                return true;
                            }
                        }
                        catch
                        {
                            return true;
                        }
                    }
                    else
                    {

                        bool isLocked = false;
                        bool exception = false;
                        try
                        {
                            if (Monitor.TryEnter(_blockObject))
                            {
                                isLocked = true;
                                try
                                {
                                    if (_blockObject.Disposed)
                                    {
                                        return true;
                                    }

                                    if (_blockObject.BlockTransactions == null)
                                    {
                                        return true;
                                    }

                                    if (_blockObject.BlockTransactions.Count != _blockObject.TotalTransaction)
                                    {
                                        return true;
                                    }

                                    if (IsDeleted)
                                    {
                                        return true;
                                    }
                                }
                                catch
                                {
                                    exception = true;
                                }
                            }
                        }
                        finally
                        {
                            if (isLocked)
                            {
                                Monitor.Exit(_blockObject);
                            }
                        }

                        if (exception) return true;

                    }
                }
                catch
                {
                    // Ignored.
                }
                return false;
            }
        }


    }
}

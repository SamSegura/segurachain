using SeguraChain_Lib.Blockchain.Block.Function;
using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using SeguraChain_Lib.Utility;
using System.Runtime.CompilerServices;
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
                    lock (value)
                    {
                        if (value.BlockIsUpdated)
                            IsUpdated = true;

                        if (!IsNull)
                        {
                            lock (_blockObject)
                            {
                                if (value.BlockFromMemory || value.BlockCloned)
                                    _blockObject = value.DirectCloneBlockObject();
                                else
                                {
                                    if (_blockObject.BlockLastChangeTimestamp <= value.BlockLastChangeTimestamp)
                                        _blockObject = value;
                                }
                                _blockObject.BlockFromMemory = false;
                                _blockObject.BlockFromCache = true;
                                _blockObject.BlockCloned = false;
                                _blockObject.BlockIsUpdated = false;
                                _blockObject.Disposed = false;
                            }
                        }
                        else
                        {
                            _blockObject = value.BlockFromMemory || value.BlockFromCache ? value.DirectCloneBlockObject() : value;
                            _blockObject.BlockFromMemory = false;
                            _blockObject.BlockFromCache = true;
                            _blockObject.BlockCloned = false;
                            _blockObject.BlockIsUpdated = false;
                            _blockObject.Disposed = false;
                        }

                        if (_ioDataSizeOnMemory == 0)
                            _ioDataSizeOnMemory = ClassBlockUtility.GetIoBlockSizeOnMemory(_blockObject);
                    }
                    LastUpdateTimestamp = ClassUtility.GetCurrentTimestampInMillisecond();

                }
                else
                {
                    if (!IsNull)
                    {
                        _blockObject?.Dispose();
                        _blockObject = null;
                        IsUpdated = false;
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
                            _ioDataSizeOnMemory = ClassBlockUtility.GetIoBlockSizeOnMemory(_blockObject);
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
                    return true;

                try
                {
                    if (Monitor.IsEntered(_blockObject))
                    {
                        try
                        {
                            if (_blockObject.Disposed)
                                return true;

                            if (_blockObject.BlockTransactions == null)
                                return true;

                            if (_blockObject.BlockTransactions.Count != _blockObject.TotalTransaction)
                                return true;

                            if (IsDeleted)
                                return true;
                        }
                        catch
                        {
                            return true;
                        }
                    }
                    else
                    {

                        bool exception = false;

                        try
                        {
                            if (_blockObject.Disposed)
                                return true;

                            if (_blockObject.BlockTransactions == null)
                                return true;

                            if (_blockObject.BlockTransactions.Count != _blockObject.TotalTransaction)
                                return true;

                            if (IsDeleted)
                                return true;
                        }
                        catch
                        {
                            exception = true;
                        }


                        if (exception) return true;

                    }
                }
                catch
                {
                    return true;
                }
                return false;
            }
        }
    }
}

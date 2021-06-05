using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using SeguraChain_Lib.Blockchain.Block.Enum;
using SeguraChain_Lib.Blockchain.Block.Function;
using SeguraChain_Lib.Blockchain.Mining.Object;
using SeguraChain_Lib.Blockchain.Setting;
using SeguraChain_Lib.Blockchain.Transaction.Utility;
using SeguraChain_Lib.Utility;

namespace SeguraChain_Lib.Blockchain.Block.Object.Structure
{
    public class ClassBlockObject : IDisposable
    {
        #region Dispose functions

        public bool Disposed;

        ~ClassBlockObject()
        {
            Dispose(true);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Disposed)
                return;


            if (disposing)
            {
                BlockTransactions?.Clear();
                GC.SuppressFinalize(this);
                Disposed = true;
            }
        }

        #endregion

        public long BlockHeight;
        public BigInteger BlockDifficulty;
        public string BlockHash;
        public ClassMiningPoWaCShareObject BlockMiningPowShareUnlockObject;
        public long TimestampCreate;
        public long TimestampFound;
        public string BlockWalletAddressWinner
        {
            get
            {
                if (BlockHeight > BlockchainSetting.GenesisBlockHeight)
                    return BlockMiningPowShareUnlockObject?.WalletAddress;

                if (BlockHeight == BlockchainSetting.GenesisBlockHeight)
                    return BlockchainSetting.WalletAddressDev(0);

                return null;
            }
        }

        public ClassBlockEnumStatus BlockStatus;
        public bool BlockUnlockValid;
        public long BlockLastChangeTimestamp;
        public long BlockNetworkAmountConfirmations;
        public bool BlockIsUpdated;
        public bool BlockCloned;

        /// <summary>
        /// Only used pending to sync a block.
        /// </summary>
        public int BlockTransactionCountInSync;
        public int BlockSlowNetworkAmountConfirmations;

        #region About transactions.

        public SortedList<string, ClassBlockTransaction> BlockTransactions
        {
            get
            {
                SortedList<string, ClassBlockTransaction> blockTransaction = null;

                bool isLocked = false;
                try
                {
                    if (_blockTransactions != null)
                    {
                        if (!Monitor.IsEntered(_blockTransactions))
                        {
                            if (Monitor.TryEnter(_blockTransactions))
                            {
                                isLocked = true;
                                blockTransaction = _blockTransactions;
                            }
                        }
                        else
                            blockTransaction = _blockTransactions;
                    }
                }
                finally
                {
                    if (isLocked)
                        Monitor.Exit(_blockTransactions);
                }

                return blockTransaction;
            }
        }

        private SortedList<string, ClassBlockTransaction> _blockTransactions; // Contains transactions id's and transactions confirmations numbers.

        public bool BlockTransactionConfirmationCheckTaskDone;
        public long BlockTotalTaskTransactionConfirmationDone;
        public long BlockLastHeightTransactionConfirmationDone;
        public string BlockFinalHashTransaction;
        public bool BlockTransactionFullyConfirmed;
        public BigInteger TotalCoinConfirmed;
        public BigInteger TotalCoinPending;
        public BigInteger TotalFee;
        public int TotalTransaction;
        public int TotalTransactionConfirmed;

        #endregion


        /// <summary>
        /// Constructor.
        /// </summary>
        public ClassBlockObject(long blockHeight, BigInteger blockDifficulty, string blockHash, long timestampCreate, long timestampFound, ClassBlockEnumStatus blockStatus, bool blockUnlockValid, bool blockTransactionConfirmationCheckTaskDone)
        {
            BlockHeight = blockHeight;
            BlockDifficulty = blockDifficulty;
            BlockHash = blockHash;
            TimestampCreate = timestampCreate;
            TimestampFound = timestampFound;
            BlockStatus = blockStatus;
            BlockUnlockValid = blockUnlockValid;
            _blockTransactions = new SortedList<string, ClassBlockTransaction>();
            BlockTransactionConfirmationCheckTaskDone = blockTransactionConfirmationCheckTaskDone;
            BlockLastChangeTimestamp = ClassUtility.GetCurrentTimestampInSecond();
            TotalCoinConfirmed = 0;
            TotalCoinPending = 0;
            TotalFee = 0;
            TotalTransaction = 0;
            TotalTransactionConfirmed = 0;
        }


        /// <summary>
        /// Permit to copy the block object and his transaction completly, to bypass the GC memory indexing process.
        /// </summary>
        /// <param name="retrieveTx"></param>
        /// <param name="blockObjectCopy"></param>
        public void DeepCloneBlockObject(bool retrieveTx, out ClassBlockObject blockObjectCopy)
        {
            blockObjectCopy = null; // Default.
            try
            {
                ClassBlockUtility.StringToBlockObject(ClassBlockUtility.SplitBlockObject(this), out blockObjectCopy);

                // Retrieve the count of tx's of the block source.
                if (_blockTransactions != null && retrieveTx)
                {
                    if (_blockTransactions.Count > 0)
                    {
                        blockObjectCopy.TotalTransaction = _blockTransactions.Count;

                        // Copy tx's into the block object copied if asked.
                        if (retrieveTx)
                        {
                            foreach (var tx in BlockTransactions)
                                if (ClassTransactionUtility.StringToBlockTransaction(ClassTransactionUtility.SplitBlockTransactionObject(tx.Value), out ClassBlockTransaction blockTransaction))
                                    blockObjectCopy.BlockTransactions.Add(tx.Key, blockTransaction);
                        }
                    }
                }

                blockObjectCopy.BlockCloned = true;
            }
            catch
            {
                if (retrieveTx)
                    blockObjectCopy = null;
            }
        
        }

        /// <summary>
        /// Direct clone block object.
        /// </summary>
        /// <returns></returns>
        public ClassBlockObject DirectCloneBlockObject()
        {
            ClassBlockObject blockObjectCopy = null;
            bool locked = false;
            try
            {
                locked = Monitor.TryEnter(this);
                if (locked)
                    DeepCloneBlockObject(true, out blockObjectCopy);
            }
            finally
            {
                if (locked)
                    Monitor.Exit(this);
            }
            return blockObjectCopy;
        }
    }
}

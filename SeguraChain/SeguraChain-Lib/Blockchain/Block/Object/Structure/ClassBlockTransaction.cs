using System.Numerics;
using SeguraChain_Lib.Blockchain.Transaction.Enum;
using SeguraChain_Lib.Blockchain.Transaction.Object;
using SeguraChain_Lib.Blockchain.Transaction.Utility;

namespace SeguraChain_Lib.Blockchain.Block.Object.Structure
{
    /// <summary>
    ///  Contains transactions id's and transactions confirmations numbers.
    /// </summary>
    public class ClassBlockTransaction
    {
        public long TransactionBlockHeightInsert;
        public long TransactionBlockHeightTarget;
        public long TransactionTotalConfirmation;
        private ClassTransactionObject _transactionObject;
        public ClassTransactionObject TransactionObject
        {
            get => _transactionObject;
            set
            {
                _transactionObject = value;
                if (TransactionSize == 0)
                {
                    TransactionSize = ClassTransactionUtility.GetTransactionMemorySize(_transactionObject, false);
                }
            }
        }

        public bool TransactionStatus;
        public long TransactionInvalidRemoveTimestamp;
        public ClassTransactionEnumStatus TransactionInvalidStatus;
        public int IndexInsert;
        public bool Spent;
        public BigInteger TotalSpend;
        public long TransactionSize;

        public ClassBlockTransaction Clone()
        {
            ClassTransactionUtility.StringToBlockTransaction(ClassTransactionUtility.SplitBlockTransactionObject(this), out ClassBlockTransaction blockTransaction);

            return blockTransaction;
        }
    }
}

using SeguraChain_Lib.Blockchain.Block.Object.Structure;

namespace SeguraChain_Desktop_Wallet.Sync.Object
{
    public class ClassSyncCacheBlockTransactionObject
    {
        public string WalletAddressOwner;
        public bool IsSender;
        public bool IsMemPool;
        public ClassBlockTransaction BlockTransaction;

        public ClassSyncCacheBlockTransactionObject(string walletAddressOwner)
        {
            WalletAddressOwner = walletAddressOwner;
        }
    }
}

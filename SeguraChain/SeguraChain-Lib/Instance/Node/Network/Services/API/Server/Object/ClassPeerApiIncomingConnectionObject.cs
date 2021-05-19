using System.Collections.Concurrent;
using System.Threading;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Client;


namespace SeguraChain_Lib.Instance.Node.Network.Services.API.Server.Object
{
    public class ClassPeerApiIncomingConnectionObject
    {
        public ConcurrentDictionary<string, ClassPeerApiClientObject> ListeApiClientObject;
        public bool OnCleanUp;
        public SemaphoreSlim SemaphoreHandleConnection;

        public ClassPeerApiIncomingConnectionObject()
        {
            SemaphoreHandleConnection = new SemaphoreSlim(1, 1);
            ListeApiClientObject = new ConcurrentDictionary<string, ClassPeerApiClientObject>();
            OnCleanUp = false;
        }
    }
}

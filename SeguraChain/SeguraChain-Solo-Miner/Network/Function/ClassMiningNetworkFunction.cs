using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SeguraChain_Lib.Blockchain.Block.Function;
using SeguraChain_Lib.Blockchain.Block.Object.Structure;
using SeguraChain_Lib.Blockchain.Mining.Enum;
using SeguraChain_Lib.Blockchain.Mining.Function;
using SeguraChain_Lib.Blockchain.Mining.Object;
using SeguraChain_Lib.Blockchain.Setting;
using SeguraChain_Lib.Instance.Node.Network.Enum.API.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Client.Enum;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Packet;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Packet.SubPacket.Request;
using SeguraChain_Lib.Instance.Node.Network.Services.API.Packet.SubPacket.Response;
using SeguraChain_Lib.Log;
using SeguraChain_Lib.Utility;
using SeguraChain_Solo_Miner.Network.Object;
using SeguraChain_Solo_Miner.Setting.Object;

namespace SeguraChain_Solo_Miner.Network.Function
{
    public class ClassMiningNetworkFunction
    {
        private ClassSoloMinerSettingObject _minerSettingObject;
        private ClassMiningNetworkStatsObject _miningNetworkStatsObject;
        private CancellationTokenSource _cancellationTokenMiningNetworkTask;
        private const int DelayTaskAskBlockTemplate = 1000;

        /// <summary>
        /// Contructor.
        /// </summary>
        /// <param name="minerSettingObject"></param>
        /// <param name="miningNetworkStatsObject"></param>
        public ClassMiningNetworkFunction(ClassSoloMinerSettingObject minerSettingObject, ClassMiningNetworkStatsObject miningNetworkStatsObject)
        {
            _minerSettingObject = minerSettingObject;
            _miningNetworkStatsObject = miningNetworkStatsObject;
            _cancellationTokenMiningNetworkTask = new CancellationTokenSource();
        }

        /// <summary>
        /// Start every mining network tasks.
        /// </summary>
        public void StartMiningNetworkTask()
        {
            TaskAskBlockTemplateFromPeer();
        }

        #region Network tasks


        /// <summary>
        /// Start a task who contact peers to retrieve back the current blocktemplate.
        /// </summary>
        private void TaskAskBlockTemplateFromPeer()
        {
            try
            {
                Task.Factory.StartNew(async () =>
                {
                    while (true)
                    {

                        string currentBlockHash = string.Empty;
                        string currentMiningPowacSettingSerializedString = string.Empty;
                        bool taskDone = false;
                        long taskTimestampEnd = ClassUtility.GetCurrentTimestampInSecond() + BlockchainSetting.PeerApiMaxConnectionDelay;
                        CancellationTokenSource cancellationTokenRequest = new CancellationTokenSource();

                        try
                        {
                            await Task.Factory.StartNew(() =>
                            {
                                try
                                {
                                    if (SendAskBlockTemplateRequest(_minerSettingObject.SoloMinerNetworkSetting.peer_ip_target, _minerSettingObject.SoloMinerNetworkSetting.peer_api_port_target, out ClassApiPeerPacketSendNetworkStats apiPeerPacketSendNetworkStats))
                                    {
                                        if (apiPeerPacketSendNetworkStats != null)
                                        {
                                            if (!apiPeerPacketSendNetworkStats.CurrentBlockHash.IsNullOrEmpty(out _) && apiPeerPacketSendNetworkStats.CurrentMiningPoWaCSetting != null)
                                            {
                                                if (apiPeerPacketSendNetworkStats.CurrentBlockHash.Length == BlockchainSetting.BlockHashHexSize)
                                                {

                                                    currentBlockHash = apiPeerPacketSendNetworkStats.CurrentBlockHash;


                                                    if (ClassMiningPoWaCUtility.CheckMiningPoWaCSetting(apiPeerPacketSendNetworkStats.CurrentMiningPoWaCSetting))
                                                    {
                                                        currentMiningPowacSettingSerializedString = ClassUtility.SerializeData(apiPeerPacketSendNetworkStats.CurrentMiningPoWaCSetting);
                                                    }
                                                }
                                            }
                                        }

                                    }

                                    taskDone = true;
                                }
                                catch
                                {
                                    // Ignored.
                                }
                            }, cancellationTokenRequest.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Ignored, catch the exception once the task is cancelled.
                        }

                        // Waiting all tasks done.
                        while (taskTimestampEnd >= ClassUtility.GetCurrentTimestampInSecond())
                        {
                            if (taskDone)
                            {
                                break;
                            }
                            await Task.Delay(100, _cancellationTokenMiningNetworkTask.Token);
                        }

                        cancellationTokenRequest.Cancel();

                        if (ClassUtility.TryDeserialize(currentMiningPowacSettingSerializedString, out ClassMiningPoWaCSettingObject miningPoWaCSettingObject, ObjectCreationHandling.Replace))
                        {
                            if (miningPoWaCSettingObject != null)
                            {
                                _miningNetworkStatsObject.UpdateMiningPoWacSetting(miningPoWaCSettingObject);
                            }
                        }
                           
                       
                        if (ClassBlockUtility.GetBlockTemplateFromBlockHash(currentBlockHash, out ClassBlockTemplateObject blockTemplateObject))
                        {
                            if (blockTemplateObject != null)
                            {
                                _miningNetworkStatsObject.UpdateBlocktemplate(blockTemplateObject);
                            }
                        }

                        
                        await Task.Delay(DelayTaskAskBlockTemplate);
                    }
                }, _cancellationTokenMiningNetworkTask.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Ignored, catch the exception once cancelled.
            }
        }

        #endregion

        #region Packet API Request

        /// <summary>
        /// Send a request to retrieve back the current blocktemplate.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="apiPeerPacketSendNetworkStats"></param>
        /// <returns></returns>
        private bool SendAskBlockTemplateRequest(string peerIp, int peerPort, out ClassApiPeerPacketSendNetworkStats apiPeerPacketSendNetworkStats)
        {
            try
            {

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(("http://" + peerIp + ":" + peerPort + "/" + ClassPeerApiEnumGetRequest.GetBlockTemplate));
                request.AutomaticDecompression = DecompressionMethods.GZip;
                request.ServicePoint.Expect100Continue = false;
                request.KeepAlive = false;
                request.Timeout = BlockchainSetting.PeerApiMaxConnectionDelay * 1000;
                string result;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponseAsync().Result)
                {
                    using (Stream stream = response.GetResponseStream())
                    {
                        if (stream != null)
                        {
                            using (StreamReader reader = new StreamReader(stream))
                            {
                                result = reader.ReadToEnd();

                                response.Close();

                                if (!result.IsNullOrEmpty(out _))
                                {
                                    if (ClassUtility.TryDeserialize(result, out ClassApiPeerPacketObjetReceive apiPeerPacketObjetReceive))
                                    {
                                        if (apiPeerPacketObjetReceive != null)
                                        {
                                            if (ClassUtility.TryDeserialize(apiPeerPacketObjetReceive.PacketObjectSerialized, out apiPeerPacketSendNetworkStats))
                                            {
                                                return true;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                }
            }
#if DEBUG
            catch (System.Exception error)
            {
                Debug.WriteLine("Exception on sending post request to the API Server. Details: " + error.Message);
#else
            catch
            {
#endif
            }
            apiPeerPacketSendNetworkStats = null;
            return false;
        }

        /// <summary>
        /// Send a request who push a mining share to a peer target, then retrieve back the response from the peer.
        /// </summary>
        /// <param name="peerIp"></param>
        /// <param name="peerPort"></param>
        /// <param name="miningPoWaCShareObject"></param>
        /// <param name="apiPeerPacketSendMiningShareResponse"></param>
        /// <returns></returns>
        private bool SendMiningShareRequest(string peerIp, int peerPort, ClassMiningPoWaCShareObject miningPoWaCShareObject, out ClassApiPeerPacketSendMiningShareResponse apiPeerPacketSendMiningShareResponse)
        {
            try
            {

                using (HttpClient client = new HttpClient())
                {
                    var response = client.PostAsync("http://" + peerIp + ":" + peerPort, new StringContent(ClassUtility.SerializeData(new ClassApiPeerPacketObjectSend()
                    {
                        PacketType = ClassPeerApiEnumPacketSend.PUSH_MINING_SHARE,
                        PacketContentObjectSerialized = ClassUtility.SerializeData(new ClassApiPeerPacketSendMiningShare()
                        {
                            MiningPowShareObject = miningPoWaCShareObject,
                            PacketTimestamp = miningPoWaCShareObject.Timestamp,
                        })
                    }))).Result;

                    response.EnsureSuccessStatusCode();

                    if (ClassUtility.TryDeserialize(response.Content.ReadAsStringAsync().Result, out ClassApiPeerPacketObjetReceive apiPeerPacketObjetReceive))
                    {
                        if (apiPeerPacketObjetReceive != null)
                        {
                            if (ClassUtility.TryDeserialize(apiPeerPacketObjetReceive.PacketObjectSerialized, out apiPeerPacketSendMiningShareResponse))
                            {
                                return true;
                            }
                        }
                    }

                }


                /*
                byte[] packet = ClassUtility.GetByteArrayFromString(ClassUtility.SerializeData(new ClassApiPeerPacketObjectSend()
                {
                    PacketType = ClassPeerApiEnumPacketSend.PUSH_MINING_SHARE,
                    PacketContentObjectSerialized = ClassUtility.SerializeData(new ClassApiPeerPacketSendMiningShare()
                    {
                        MiningPowShareObject = miningPoWaCShareObject,
                        PacketTimestamp = miningPoWaCShareObject.Timestamp,
                    })
                }));

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(("http://" + peerIp + ":" + peerPort));
                request.Method = "POST";
                request.ContentLength = packet.Length;
                request.AutomaticDecompression = DecompressionMethods.GZip;
                request.ServicePoint.Expect100Continue = false;
                request.KeepAlive = false;
                request.Timeout = BlockchainSetting.PeerApiMaxConnectionDelay * 1000;
                using (Stream inputStream = request.GetRequestStream())
                {

                    inputStream.Write(packet, 0, packet.Length);
                }



                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {

                    using (StreamReader streamReader = new StreamReader(response.GetResponseStream()))
                    {

                        if (ClassUtility.TryDeserialize(streamReader.ReadToEnd(), out ClassApiPeerPacketObjetReceive apiPeerPacketObjetReceive))
                        {
                            if (apiPeerPacketObjetReceive != null)
                            {
                                if (ClassUtility.TryDeserialize(apiPeerPacketObjetReceive.PacketObjectSerialized, out apiPeerPacketSendMiningShareResponse))
                                {
                                    return true;
                                }
                            }
                        }

                    }
                }*/

            }
            catch (System.Exception error)
            {
                ClassLog.WriteLine("Exception on sending the mining share request to the peer API Server " + peerIp + ":" + peerPort + ". Details: " + error.Message, ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);

            }
            apiPeerPacketSendMiningShareResponse = null;
            return false;
        }

        #endregion

        #region Mining network task

        /// <summary>
        /// Send a mining share who potentialy can found the block.
        /// </summary>
        /// <param name="miningPoWaCShareObject"></param>
        public void UnlockCurrentBlockTemplate(ClassMiningPoWaCShareObject miningPoWaCShareObject)
        {
            try
            {
                Debug.WriteLine(miningPoWaCShareObject.BlockHeight + " share seems to potentially found the block, send the request..");
                Task.Factory.StartNew(async () =>
                {
                    ClassMiningPoWaCEnumStatus miningShareResponse = ClassMiningPoWaCEnumStatus.EMPTY_SHARE;

                    bool taskDone = false;
                    long taskTimestampEnd = ClassUtility.GetCurrentTimestampInSecond() + BlockchainSetting.PeerApiMaxConnectionDelay;
                    CancellationTokenSource cancellationTokenRequest = new CancellationTokenSource();

                    try
                    {
                        await Task.Factory.StartNew(() =>
                        {
                            try
                            {
                                if (SendMiningShareRequest(_minerSettingObject.SoloMinerNetworkSetting.peer_ip_target, _minerSettingObject.SoloMinerNetworkSetting.peer_api_port_target, miningPoWaCShareObject, out ClassApiPeerPacketSendMiningShareResponse apiPeerPacketSendMiningShareResponse))
                                {
                                    if (apiPeerPacketSendMiningShareResponse != null)
                                    {

                                        miningShareResponse = apiPeerPacketSendMiningShareResponse.MiningPoWShareStatus;
                                    }
                                }
                            }
                            catch
                            {
                                // Ignored.
                            }

                            taskDone = true;
                        }, cancellationTokenRequest.Token, TaskCreationOptions.RunContinuationsAsynchronously, TaskScheduler.Current).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignored, catch the exception once the task is cancelled.
                    }


                    // Waiting all tasks done.
                    while (taskTimestampEnd >= ClassUtility.GetCurrentTimestampInSecond())
                    {
                        if (taskDone)
                        {
                            break;
                        }
                        await Task.Delay(100, _cancellationTokenMiningNetworkTask.Token);
                    }

                    cancellationTokenRequest.Cancel();



#if DEBUG
                    Debug.WriteLine("Max vote mining share(s) returned: " + miningShareResponse + " from the peers.");
#endif
                    switch (miningShareResponse)
                    {
                        case ClassMiningPoWaCEnumStatus.BLOCK_ALREADY_FOUND:
                            ClassLog.WriteLine("The Block Height: " + miningPoWaCShareObject.BlockHeight + " seems to be already found.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Yellow);
                            _miningNetworkStatsObject.IncrementTotalOrphanedBlock();
                            break;
                        case ClassMiningPoWaCEnumStatus.VALID_UNLOCK_BLOCK_SHARE:
                            ClassLog.WriteLine("The Block Height: " + miningPoWaCShareObject.BlockHeight + " seems to be accepted by peers.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Green);
                            _miningNetworkStatsObject.IncrementTotalUnlockedBlock();
                            break;
                        case ClassMiningPoWaCEnumStatus.INVALID_SHARE_DATA:
                            ClassLog.WriteLine("The Block Height: " + miningPoWaCShareObject.BlockHeight + " return invalid shares from peers.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.DarkRed);
                            _miningNetworkStatsObject.IncrementTotalInvalidShare();
                            break;
                        default:
                            ClassLog.WriteLine("The Block Height: " + miningPoWaCShareObject.BlockHeight + " refused from peers.", ClassEnumLogLevelType.LOG_LEVEL_MINING, ClassEnumLogWriteLevel.LOG_WRITE_LEVEL_MANDATORY_PRIORITY, false, ConsoleColor.Red);
                            _miningNetworkStatsObject.IncrementTotalRefusedShare();
                            break;
                    }

                }, _cancellationTokenMiningNetworkTask.Token, TaskCreationOptions.LongRunning, TaskScheduler.Current).ConfigureAwait(false);
            }
            catch
            {
                // Ignored, catch the exception once the task is cancelled.
            }
        }

        #endregion
    }
}

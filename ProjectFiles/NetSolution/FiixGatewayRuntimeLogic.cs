#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.DataLogger;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.NativeUI;
using FTOptix.UI;
using FTOptix.CoreBase;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using System.Collections.Generic;
using System.Linq;
using HttpAPIGateway;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json;
using System.IO;
using System.Text;
using System.Net.Http;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Net.Http.Json;
using Newtonsoft.Json.Linq;
using FTOptix.WebUI;
using FTOptix.EventLogger;
#endregion
/*
Fiix Gateway runtime script to manage Push Agent and meter reading data sending.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class FiixGatewayRuntimeLogic : BaseNetLogic
{
    // Runtime Store_and_Send functioin base on PushAgent with changes: replace mqtt with http, [TODO] replace NewtonsoftJson to Net.Json
    public override void Start()
    {
        Log.Verbose1("FiixGatewayRuntime", "Start Gateway Runtime.");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");

        // Update Assets status and properties if Fiix URL is set
        IUANode designTimeLogic = LogicObject.Owner.Find("FiixGatewayDesigntimeLogic");
        if (designTimeLogic == null || designTimeLogic.GetVariable("Cfg_FiixURL") == null || designTimeLogic.GetVariable("Cfg_FiixURL").Value == "")
        {
            Log.Info("Fiix Gateway", "Fiix connection is not configured.");
            return;
        }
            
        SyncAssets();
       
        int AssetStatusAutoUpdatePeriod = LogicObject.GetVariable("Set_AssetStatusAutoUpdate").Value;
        if (AssetStatusAutoUpdatePeriod > 0)
        {
            PeriodicTask assetAutoUpdateTask = new PeriodicTask(SyncAssets, AssetStatusAutoUpdatePeriod, LogicObject);
            assetAutoUpdateTask.Start();
        }

        // Initial gateway stores and buffer if DataLogger is set and VariableToLog is not empty
        DataLogger metereadingLogger = (DataLogger)InformationModel.Get(LogicObject.GetVariable("Cfg_DataLogger").Value);
        if (metereadingLogger == null || metereadingLogger.VariablesToLog.Count == 0)
        {
            Log.Info("Fiix Gateway", "Meter Reading auto-send is not configured, push agent will not be initiated.");
            return;
        }

        enableGatewaySend = LogicObject.GetVariable("Set_MeterReadingStoreAndSend").Value;
        LogicObject.GetVariable("Set_MeterReadingStoreAndSend").VariableChange += ChangeSending;

        try
        {
            cancellationTokenSource = new CancellationTokenSource();
            LoadPushAgentConfiguration();
            ConfigureStores();
            ConfigureDataLoggerRecordPuller();
        }
        catch (Exception e)
        {
            Log.Error("PushAgent", $"Unable to initialize PushAgent, an error occurred: {e.Message}.");
            throw;
        }
        EnablePushAgentSending();

    }

    public override void Stop()
    {
        Log.Verbose1("PushAgent", "Stop push agent.");
        DisablePushAgentSending();
        IUANode tempDB = (SQLiteStore)LogicObject.Find("tempDB");
        if (tempDB != null) tempDB.Delete();
    }

    public void ChangeSending(object sender, VariableChangeEventArgs e)
    {
        enableGatewaySend = e.NewValue;

        //if (e.NewValue) EnablePushAgentSending();
        //else DisablePushAgentSending();
    }

    private void EnablePushAgentSending()
    {
        Log.Verbose1("GatewayRuntime", "Start Fetch Timer.");
        StartFetchTimer();
    }

    private void DisablePushAgentSending()
    {
        Log.Verbose1("PushAgent", "Stop push agent.");

        if (cancellationTokenSource!=null) cancellationTokenSource.Cancel();
        try
        {
            dataLoggerRecordPuller.StopPullTask();
            lock (dataFetchLock)
            {
                dataFetchTask.Cancel();
            }
        }
        catch (Exception e)
        {
            Log.Warning("PushAgent", $"Error occurred during stoping push agent: {e.Message}");
        }
    }

    [ExportMethod]
    public void ClearPushAgentTempStore()
    {
        if (pushAgentStore != null)
        {
            try
            {
                pushAgentStore.DeleteRecords(100000000);
                Log.Info("Fiix PushAgent", "Deleting PushAgent Buffer TempStore records.");
            }
            catch { }
        }
    }

    [ExportMethod]
    public void ClearDataLoggerStore()
    {
        if (dataLoggerStore != null)
        {
            try
            {
                dataLoggerStore.DeleteRecords(100000000);
                dataLoggerStore.DeleteTemporaryTable();      // When user change variable DataType after creation, temporary table might stuck with old data with cast data type error
                Log.Info("Fiix PushAgent", "Deleting DataLogger Store records.");
            }
            catch (Exception ex)
            { Log.Info("Fiix Gateway", "No record in DataStore to be cleared"); }
        }
    }

    [ExportMethod]
    public void SyncAssets()
    {
        // Used in Runtime to update Asset properties only, will pause when user trying to change Online status.
        //Log.Info("Update Asset Status.");
        if (!LogicObject.GetVariable("Sts_AssetStatusUpdatePause").Value) GatewayUtils.SyncAssetTree(false);
    }

    // Used to update Online status only, as backup only
    void UpdateOnlineStatus()
    {
        IUANode designTimeLogic = LogicObject.Owner.Find("FiixGatewayDesigntimeLogic");
        if (designTimeLogic == null) {
            Log.Error("Fiix Gateway", "Update Online Status error: Could not find DesignTimeLogic to get configuration");
            return;
        }
        IUANode modelFolder = LogicObject.Owner.Owner.Find("Assets");
        IUANode AssetType = LogicObject.Owner.Find("Asset");
        int updateSiteCount = 0, updateAssetCount = 0;
        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();

        // Update Sites
        string filterName = (string)designTimeLogic.GetVariable("Set_FilterSiteNames").Value;
        
        Fiix_Asset[] fiixSites = fiixHttpClient.FindAssetsBatch(true, -1, -1, filterName, true).Result;
        LogicObject.GetVariable("Sts_LastExecutionDatetime").Value = DateTime.Now;
        if (fiixSites == null)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value = "Get Fiix Sites online status with no result.";
            return;
        }
        List<IUANode> sites = modelFolder.Children.Cast<IUANode>().ToList();

        foreach (var fiixsite in fiixSites)
        {
            IUANode newSite;

            if (sites.Exists(site => (fiixsite.id == (int)site.GetVariable("id").Value) || fiixsite.strName == site.GetVariable("strName").Value))
            {
                newSite = sites.Find(site => fiixsite.id == site.GetVariable("id").Value);
                newSite.GetVariable("bolIsOnline").Value = Convert.ToBoolean(fiixsite.bolIsOnline);
                newSite.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixsite.intUpdated).DateTime;
                updateSiteCount++;
            }
        }

        // Sync all Assets with isSite is false
        sites = modelFolder.Children.Cast<IUANode>().ToList();
        //if (modelFolder.Find("Template") != null) sites.Remove(modelFolder.Get("Template"));

        string sfilterName = (string)designTimeLogic.GetVariable("Set_FilterAssetNames").Value;
        Fiix_Asset[] fiixFacilities;
        if (sfilterName != null && sfilterName.Trim() == "" && !(bool)designTimeLogic.GetVariable("Set_FilterEnabledAssetCategoryOnly").Value)
        {
            fiixFacilities = fiixHttpClient.FindAssetsBatch(false, -1, -1, sfilterName, true).Result;
        }
        else
        {
            // Seperate Facility/Location with Equipment/Tool assets for filtering by name function. Get Equipment/Tool with text included in filter only.
            Fiix_Asset[] pureAssets = fiixHttpClient.FindAssetsBatch(false, 1, -1, sfilterName, true).Result;
            Fiix_Asset[] nonAssets = fiixHttpClient.FindAssetsBatch(false, 2, -1, "", true).Result;
            if (nonAssets == null)
            {
                if (pureAssets != null) fiixFacilities = pureAssets.ToArray();
                else fiixFacilities = null;
            }
            else
            {
                if (pureAssets != null && pureAssets.Length > 0) fiixFacilities = nonAssets.Concat(pureAssets).ToArray();
                else fiixFacilities = nonAssets;
            }
        }

        if (fiixFacilities == null)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += ", Get Fiix Facilities online status with no result.";
            return;
        }

        // Loop through sites to nested call find fiixFacility with parentID
        foreach (IUANode site in sites) AddUpdateFacilityByLocation(site, fiixFacilities);

        LogicObject.GetVariable("Sts_LastExecutionResult").Value = updateSiteCount + " sites status and " + updateAssetCount + " assets online status updated";

        void AddUpdateFacilityByLocation(IUANode parentNode, Fiix_Asset[] assets)
        {
            if (parentNode == null) return;
            // Get existing object nodes children
            var existingChildren = parentNode.Children.Cast<IUANode>().ToList();
            existingChildren.RemoveAll(x => x.NodeClass != NodeClass.Object || x.BrowseName.Contains("DesignTimeLogic"));

            foreach (Fiix_Asset asset in assets)
            {
                if (asset.intAssetLocationID == (int)parentNode.GetVariable("id").Value)
                {
                    // Check if the child already existing by id
                    IUANode currentNode = null;

                    foreach (IUANode childNode in existingChildren)
                    {
                        if ((int)childNode.GetVariable("id").Value == asset.id || childNode.GetVariable("strName").Value == asset.strName)
                        // node with the same id exist, update
                        {
                            childNode.GetVariable("bolIsOnline").Value = Convert.ToBoolean(asset.bolIsOnline);
                            childNode.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(asset.intUpdated).DateTime;
                            updateAssetCount++;
                            existingChildren.Remove(childNode);
                            currentNode = childNode;
                            break;
                        }
                    };
                    AddUpdateFacilityByLocation(currentNode, assets);
                }
            }
        }
    }

    private void ConfigureDataLoggerRecordPuller()
    {
        int dataLoggerPullPeriod = LogicObject.GetVariable("Cfg_DataLoggerPullTime").Value; // Period used to pull new data from the DataLogger

        if (pushAgentConfigurationParameters.preserveDataLoggerHistory)
        {
            dataLoggerRecordPuller = new DataLoggerRecordPuller(LogicObject,
                                                                LogicObject.GetVariable("Cfg_DataLogger").Value,
                                                                pushAgentStore,
                                                                statusStoreWrapper,
                                                                dataLoggerStore,
                                                                pushAgentConfigurationParameters.preserveDataLoggerHistory,
                                                                pushAgentConfigurationParameters.pushFullSample,
                                                                dataLoggerPullPeriod,
                                                                pushAgentConfigurationParameters.dataLogger.VariablesToLog.ToList().Count);
        }
        else
        {
            dataLoggerRecordPuller = new DataLoggerRecordPuller(LogicObject,
                                                                LogicObject.GetVariable("Cfg_DataLogger").Value,
                                                                pushAgentStore,
                                                                dataLoggerStore,
                                                                pushAgentConfigurationParameters.preserveDataLoggerHistory,
                                                                pushAgentConfigurationParameters.pushFullSample,
                                                                dataLoggerPullPeriod,
                                                                pushAgentConfigurationParameters.dataLogger.VariablesToLog.ToList().Count);
        }
    }

    private void ConfigureStores()
    {
        string pushAgentStoreBrowseName = "PushAgentStore";
        string pushAgentFilename = "push_agent_store";
        CreatePushAgentStore(pushAgentStoreBrowseName, pushAgentFilename);

        var variableLogOpCode = pushAgentConfigurationParameters.dataLogger.GetVariable("LogVariableOperationCode");
        insertOpCode = variableLogOpCode != null ? (bool)variableLogOpCode.Value : false;

        var variableTimestamp = pushAgentConfigurationParameters.dataLogger.GetVariable("LogVariableTimestamp");
        insertVariableTimestamp = variableTimestamp != null ? (bool)variableTimestamp.Value : false;

        var logLocalTimestamp = pushAgentConfigurationParameters.dataLogger.GetVariable("LogLocalTime");
        logLocalTime = logLocalTimestamp != null ? (bool)logLocalTimestamp.Value : false;

        jsonCreator = new JSONBuilder(insertOpCode, insertVariableTimestamp, logLocalTime);

        dataLoggerStore = new DataLoggerStoreWrapper(InformationModel.Get<FTOptix.Store.Store>(pushAgentConfigurationParameters.dataLogger.Store),
                                            GetDataLoggerTableName(),
                                            pushAgentConfigurationParameters.dataLogger.VariablesToLog.ToList(),
                                            insertOpCode,
                                            insertVariableTimestamp,
                                            logLocalTime);

        if (!pushAgentConfigurationParameters.pushFullSample)
        {
            string tableName = "PushAgentTableRowPerVariable";
            pushAgentStore = new PushAgentStoreRowPerVariableWrapper(LogicObject.Get<SQLiteStore>(pushAgentStoreBrowseName),
                                                                     tableName,
                                                                     insertOpCode);
        }
        else
        {
            string tableName = "PushAgentTableDataLogger";
            pushAgentStore = new PushAgentStoreDataLoggerWrapper(LogicObject.Get<SQLiteStore>(pushAgentStoreBrowseName),
                                                                tableName,
                                                                pushAgentConfigurationParameters.dataLogger.VariablesToLog.ToList(),
                                                                insertOpCode,
                                                                insertVariableTimestamp,
                                                                logLocalTime);
            if (GetMaximumRecordsPerPacket() != 1)
            {
                Log.Warning("PushAgent", "For PushByRow mode maximum one row per packet is supported. Setting value to 1.");
                LogicObject.GetVariable("Cfg_MaximumItemsPerPacket").Value = 1;
            }
        }

        if (pushAgentConfigurationParameters.preserveDataLoggerHistory)
        {
            string tableName = "DataLoggerStatusStore";
            statusStoreWrapper = new DataLoggerStatusStoreWrapper(LogicObject.Get<SQLiteStore>(pushAgentStoreBrowseName),
                                                                                            tableName,
                                                                                            pushAgentConfigurationParameters.dataLogger.VariablesToLog.ToList(),
                                                                                            insertOpCode,
                                                                                            insertVariableTimestamp);
        }
    }

    private void StartFetchTimer()
    {
        if (cancellationTokenSource.IsCancellationRequested)
            return;
        try
        {
            // Set the correct timeout by checking number of records to be sent
            if (pushAgentStore.RecordsCount() >= GetMaximumRecordsPerPacket())
                nextRestartTimeout = GetMinimumPublishTime();
            else
                nextRestartTimeout = GetMaximumPublishTime();
            dataFetchTask = new DelayedTask(OnFetchRequired, nextRestartTimeout, LogicObject);

            lock (dataFetchLock)
            {
                dataFetchTask.Start();
            }
            Log.Verbose1("PushAgent", $"Fetching next data in {nextRestartTimeout} ms.");
        }
        catch (Exception e)
        {
            OnFetchError("Set time delay on fetch data from temp store" + e.Message);
        }
    }

    private void OnFetchRequired()
    {
        if (pushAgentStore.RecordsCount() > 0 && enableGatewaySend)
            FetchData();
        //  else
        StartFetchTimer();
    }

    private void FetchData()
    {
        if (cancellationTokenSource.IsCancellationRequested)
            return;

        Log.Verbose1("PushAgent", "Fetching data from push agent temporary store");
        var records = GetRecordsToSend();
        Log.Info("Fiix Gateway","MeterReading records being sent from PushAgentTempStore: " + records.Count + "; Store has total " + pushAgentStore.RecordsCount() + " records.");
        recordToSendCount = records.Count;
        if (records.Count > 0)
        {
            // Publish(GenerateJSON(records));
            // clientID is replace with "Fiix" as it is used in the restAPI
            var now = DateTime.Now;
            if (pushAgentConfigurationParameters.pushFullSample)
            {
                pendingSendPacket = new DataLoggerRowPacket(now, "Fiix", records.Cast<DataLoggerRecord>().ToList());
            }
            else
            {
                pendingSendPacket = new VariablePacket(now, "Fiix", records.Cast<VariableRecord>().ToList());
            }
            Publish(GatewayUtils.GetMeterReadingBatchPayloadFromLogRecords(records));
        }
    }

    private List<Record> GetRecordsToSend()
    {
        List<Record> result = null;
        try
        {
            result = pushAgentStore.QueryOlderEntries(GetMaximumRecordsPerPacket());
        }
        catch (Exception e)
        {
            OnFetchError("Get Agent Store records to send " + e.Message);
        }
        return result;
    }

    private string GenerateJSON(List<Record> records)  // Not used in Fiix JSON composition for special data format required
    {
        var now = DateTime.Now;
        var clientId = pushAgentConfigurationParameters.mqttConfigurationParameters.clientId;

        if (pushAgentConfigurationParameters.pushFullSample)
        {
            pendingSendPacket = new DataLoggerRowPacket(now, clientId, records.Cast<DataLoggerRecord>().ToList());
            return jsonCreator.CreatePacketFormatJSON((DataLoggerRowPacket)pendingSendPacket);
        }
        else
        {
            pendingSendPacket = new VariablePacket(now, clientId, records.Cast<VariableRecord>().ToList());
            return jsonCreator.CreatePacketFormatJSON((VariablePacket)pendingSendPacket);
        }
    }

    private void Publish(string json)
    {
        try
        {
            // ==== Replace following mqtt publich with http post for http client =====
            //mqttClientConnector.PublishAsync(json,
            //                                 pushAgentConfigurationParameters.mqttConfigurationParameters.brokerTopic,
            //                                 false,
            //                                 pushAgentConfigurationParameters.mqttConfigurationParameters.qos)
            //    .Wait();

            // DeleteRecordsFromTempStore();
            if (json.Trim() == "")
            {
                DeleteRecordsFromTempStore();
                return;
            }
            LogicObject.GetVariable("Sts_PushAgentLastSendDatetime").Value = DateTime.Now;
            FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();
            Fiix_AddBatchResponse_MeterReading result = fiixHttpClient.AddMeterReadingBatch(json).Result;
            if (result != null && result.responses != null && result.responses.Length > 0)
            {
                DeleteRecordsFromTempStore();
                LogicObject.GetVariable("Sts_PushAgentLastSendResult").Value = recordToSendCount + " sent" ;
            }
            else LogicObject.GetVariable("Sts_PushAgentLastSendResult").Value = "Failed";
            // StartFetchTimer();
        }
        catch (OperationCanceledException)
        {
            // empty
            LogicObject.GetVariable("Sts_PushAgentLastSendResult").Value = "Canceled";
        }
        catch (Exception e)
        {
            Log.Warning("PushAgent", $"Error occurred during publishing: {e.Message}");
            LogicObject.GetVariable("Sts_PushAgentLastSendResult").Value = "Error";
            // StartFetchTimer();
        }
    }

    private void DeleteRecordsFromTempStore()
    {
        try
        {
            Log.Verbose1("PushAgent", "Delete records from push agent temporary store.");
            if (pushAgentConfigurationParameters.pushFullSample)
                pushAgentStore.DeleteRecords(((DataLoggerRowPacket)pendingSendPacket).records.Count);
            else
                pushAgentStore.DeleteRecords(((VariablePacket)pendingSendPacket).records.Count);
            pendingSendPacket = null;
        }
        catch (Exception e)
        {
            OnFetchError("Delete records from temp agent store" + e.Message);
        }
    }

    private void OnFetchError(string message)
    {
        Log.Error("PushAgent", $"Error while fetching data: {message}.");
        dataLoggerRecordPuller.StopPullTask();
        lock (dataFetchLock)
        {
            dataFetchTask.Cancel();
        }
    }

    //private void LoadMQTTConfiguration()
    //{
    //    pushAgentConfigurationParameters.mqttConfigurationParameters = new MQTTConfigurationParameters
    //    {
    //        clientId = LogicObject.GetVariable("ClientId").Value,
    //        brokerIPAddress = LogicObject.GetVariable("BrokerIPAddress").Value,
    //        brokerPort = LogicObject.GetVariable("BrokerPort").Value,
    //        brokerTopic = LogicObject.GetVariable("BrokerTopic").Value,
    //        qos = LogicObject.GetVariable("QoS").Value,
    //        useSSL = LogicObject.GetVariable("UseSSL").Value,
    //        pathCACert = ResourceUriValueToAbsoluteFilePath(LogicObject.GetVariable("UseSSL/CACert").Value),
    //        pathClientCert = ResourceUriValueToAbsoluteFilePath(LogicObject.GetVariable("UseSSL/ClientCert").Value),
    //        passwordClientCert = LogicObject.GetVariable("UseSSL/ClientCertPassword").Value,
    //        username = LogicObject.GetVariable("Username").Value,
    //        password = LogicObject.GetVariable("Password").Value
    //    };
    //}

    // For Fiix MeterReading, use Variable Packet only, ignore "PushFullSample" setting from standard PushAgent.
    private void LoadPushAgentConfiguration()
    {
        pushAgentConfigurationParameters = new PushAgentConfigurationParameters();

        try
        {
            // LoadMQTTConfiguration();

            pushAgentConfigurationParameters.dataLogger = GetDataLogger();
            // pushAgentConfigurationParameters.pushFullSample = LogicObject.GetVariable("Cfg_PushFullSample").Value;
            // Ignore PushFullSample setting which is not applicable to MeterReading in Fiix
            // use Variable Packet only
            pushAgentConfigurationParameters.pushFullSample = false;
            pushAgentConfigurationParameters.preserveDataLoggerHistory = LogicObject.GetVariable("Cfg_PreserveDataLoggerHistory").Value;
        }
        catch (Exception e)
        {
            throw new CoreConfigurationException("PushAgent: Configuration error", e);
        }

    }

    //private void CheckMQTTParameters()
    //{
    //    if (pushAgentConfigurationParameters.mqttConfigurationParameters.useSSL && string.IsNullOrWhiteSpace(pushAgentConfigurationParameters.mqttConfigurationParameters.pathCACert))
    //    {
    //        Log.Warning("PushAgent", "CA certificate path is not set. Set CA certificate path or install CA certificate in the system.");
    //    }
    //    var qos = pushAgentConfigurationParameters.mqttConfigurationParameters.qos;
    //    if (qos < 0 || qos > 2)
    //    {
    //        Log.Warning("PushAgent", "QoS Values valid are 0, 1, 2.");
    //    }
    //}

    private int GetMaximumRecordsPerPacket()
    {
        return LogicObject.GetVariable("Cfg_MaximumItemsPerPacket").Value;
    }

    private int GetMaximumPublishTime()
    {
        return LogicObject.GetVariable("Cfg_MaximumPublishTime").Value;
    }

    private int GetMinimumPublishTime()
    {
        return LogicObject.GetVariable("Cfg_MinimumPublishTime").Value;
    }

    private DataLogger GetDataLogger()
    {
        var dataLoggerNodeId = LogicObject.GetVariable("Cfg_DataLogger").Value;
        return InformationModel.Get<DataLogger>(dataLoggerNodeId);
    }

    private string ResourceUriValueToAbsoluteFilePath(UAValue value)
    {
        var resourceUri = new ResourceUri(value);
        return resourceUri.Uri;
    }

    private string GetDataLoggerTableName()
    {
        if (pushAgentConfigurationParameters.dataLogger.TableName != null)
            return pushAgentConfigurationParameters.dataLogger.TableName;

        return pushAgentConfigurationParameters.dataLogger.BrowseName;
    }

    private void CreatePushAgentStore(string browsename, string filename)
    {
        Log.Verbose1("PushAgent", $"Create push agent store with filename: {filename}.");
        try
        {
            SQLiteStore store = InformationModel.MakeObject<SQLiteStore>(browsename);
            store.Filename = filename;
            LogicObject.Add(store);
        }
        catch (Exception e)
        {
            Log.Error("PushAgent", $"Unable to create push agent store ({e.Message}).");
            throw;
        }
    }

    private readonly object dataFetchLock = new object();
    private bool insertOpCode;
    private bool insertVariableTimestamp;
    private bool logLocalTime;
    private int nextRestartTimeout;
    private Packet pendingSendPacket;
    private DelayedTask dataFetchTask;
    private PushAgentConfigurationParameters pushAgentConfigurationParameters;
    //private MQTTConnector mqttClientConnector;
    private SupportStore pushAgentStore;
    private DataLoggerStoreWrapper dataLoggerStore;
    private DataLoggerStatusStoreWrapper statusStoreWrapper;
    private JSONBuilder jsonCreator;
    private CancellationTokenSource cancellationTokenSource;
    DataLoggerRecordPuller dataLoggerRecordPuller;
    private bool enableGatewaySend;
    int recordToSendCount;

    class MQTTConfigurationParameters
    {
        public string clientId;
        public string brokerIPAddress;
        public int brokerPort;
        public string brokerTopic;
        public int qos;
        public bool useSSL;
        public string pathClientCert;
        public string passwordClientCert;
        public string pathCACert;
        public string username;
        public string password;
    }

    class PushAgentConfigurationParameters
    {
        public MQTTConfigurationParameters mqttConfigurationParameters;
        public DataLogger dataLogger;
        public bool pushFullSample;
        public bool preserveDataLoggerHistory;
    }
}

namespace HttpAPIGateway
{

    public class FiixHttpClient : HttpClient
    {
        string FiixURL;
        string appKey;
        string acessKey;

        public FiixHttpClient(HttpClientHandler handler, string FiixURL, string appKey, string accessKey, string secretKey)
        {
            this.FiixURL = FiixURL;
            this.appKey = appKey;
            this.acessKey = accessKey;
            //this.BaseAddress = new Uri(FiixURL);
            this.DefaultRequestHeaders.Accept.Clear();
            //this.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            this.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(getAuth(FiixURL, appKey, accessKey, secretKey));
        }

        static String getAuth(string FiixURL, string appKey, string accessKey, string secretKey)
        {
            String requestUri = getRequestUri(FiixURL, appKey, accessKey);
            if (requestUri.IndexOf("http://") == 0)
            {
                requestUri = requestUri.Substring(7);
            }
            else if (requestUri.IndexOf("https://") == 0)
            {
                requestUri = requestUri.Substring(8);
            }

            byte[] requestUriBytes = System.Text.Encoding.UTF8.GetBytes(requestUri);
            byte[] secretKeyBytes = System.Text.Encoding.UTF8.GetBytes(secretKey);

            HMACSHA256 hmac = new HMACSHA256(secretKeyBytes);
            byte[] hashValue = hmac.ComputeHash(requestUriBytes);
            return String.Concat(Array.ConvertAll(hashValue, x => x.ToString("X2"))).ToLower();
        }

        static String getRequestUri(string FiixURL, string appKey, string accessKey)
        {
            String requestUri = String.Format(FiixURL + "/api/?appKey={0}&accessKey={1}&signatureMethod=HmacSHA256&signatureVersion=1", appKey, accessKey);
            return requestUri;
        }

        public string GetFiixRequestURLwithParameters()
        {
            return getRequestUri(FiixURL, appKey, acessKey);
        }

        // Find assets by filters using batch request, support multiple namestrings separated by comma; if categoryFilterCode or assetLocationID = -1 then no filter on that field.
        // categoryFilterCode       -1: no filter;  1: Asset only (SysCode in 0,2,3,4,11);  2: Non-Asset only (SysCode Not in 0,2,3,4,11)
        // filterNameStrings is multiple strings seperated by comma
        public async Task<Fiix_Asset[]> FindAssetsBatch(bool findSites, int categoryFilterCode, int assetLocationID, string filterNameStrings, bool onlineStatusOnly)
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();
            string jsonBatchPayload = GetBatchPayloadBase() + "\"requests\": [";
            string requests = "";

            if (filterNameStrings.Trim() == "")
            {
                string filters = GetAssetFilterString(findSites, categoryFilterCode, assetLocationID, filterNameStrings);
                string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"Asset\"," + filters + ",\"fields\":\"*\"}";
                requests = requests + jsonPayload + ",";
            }
            else
            {
                // Split filter string
                string[] filterWords = filterNameStrings.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (string filterWord in filterWords)
                {
                    string filters = GetAssetFilterString(findSites, categoryFilterCode, assetLocationID, filterWord);
                    string jsonPayload;
                    if (onlineStatusOnly) jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"Asset\"," + filters + ",\"fields\":\"id,strName,bolIsOnline,intUpdated\"}";
                    else jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"Asset\"," + filters + ",\"fields\":\"*\"}";
                    requests = requests + jsonPayload + ",";
                }
            }
            jsonBatchPayload = jsonBatchPayload + requests.Remove(requests.Length - 1, 1) + "]}";
            //Log.Info("payload: " + jsonBatchPayload);

            var content = new StringContent(jsonBatchPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindBatchResponse_Asset apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindBatchResponse_Asset>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.responses != null && apiResult.responses.Length > 0)
                {
                    var assetObjects = new List<Fiix_Asset>();
                    foreach (Fiix_FindResponse_Asset assetResponse in apiResult.responses)
                    {
                        assetObjects.AddRange(assetResponse.objects);
                    }
                    var arrayAssets = assetObjects.Distinct().ToArray();
                    Log.Debug("Get Fiix Assets with result of " + arrayAssets.Length);
                    return arrayAssets;
                }
                else
                {
                    if (apiResult.error != null) Log.Error("Fiix Gateway", "Get Fiix Assets with error response, " + apiResult.error.message);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Get Fiix Assets in batch with error " + ex.Message);
                return null;
            }
        }

        // Find single asset by ID, used to update given asset property values
        public async Task<Fiix_Asset> FindAssetByID(int assetID)
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();
            string filters = "\"filters\": [{ \"ql\": \"id = ?\", \"parameters\" : [" + assetID + "]}]";
            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"Asset\"," + filters + ",\"fields\":\"*\"}";

            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindResponse_Asset apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_Asset>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    // Log.Info("Get Fiix Assets with result of " + apiResult.totalObjects);
                    return apiResult.objects[0];
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Find Fiix Asset with ID with error" + ex.Message);
                return null;
            }
        }

        // Find assets by filters with single API call, backup only, use Batch API call instead.
        // if categorySysCode or assetLocationID = -1 then no filter on that field, filterNameString is a single string
        private async Task<Fiix_Asset[]> FindAssets(bool findSites, int categoryFilterCode, int assetLocationID, string filterNameString)
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();
            string jsonBatchPayload = GetBatchPayloadBase() + "\"requests\": [";
            //string requests = "";

            string filters = GetAssetFilterString(findSites, categoryFilterCode, assetLocationID, filterNameString);
            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"Asset\"," + filters + ",\"fields\":\"*\"}";

            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindResponse_Asset apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_Asset>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    // Log.Info("Get Fiix Assets with result of " + apiResult.totalObjects);
                    return apiResult.objects;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Find Fiix Assets with error" + ex.Message);
                return null;
            }
        }
        private async Task<Fiix_Asset[]> FindFacilitiesBySiteID(string siteID, string filterNameString)   //Backup only
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();

            string filters = "\"filters\": [{ \"ql\": \"bolIsSite = ?\", \"parameters\" : [0]},{\"ql\": \"intSiteID = ?\", \"parameters\" : [" + siteID + "]}";
            if (filterNameString.Trim() != "") filters = filters + ",{\"fields\":\"strName\",\"fullText\":\"" + filterNameString + "\"}]";
            else filters = filters + "]";

            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"Asset\"," + filters + ",\"fields\":\"*\"}";

            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindResponse_Asset apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_Asset>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    Log.Info("Fiix Gateway","Get Fiix Sites with result of " + apiResult.totalObjects);
                    return apiResult.objects;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Get Fiix Sites with error" + ex.Message);
                return null;
            }
        }

        public async Task<Fiix_AssetCategory[]> FindAssetCategories()
        {
            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"AssetCategory\",\"fields\":\"*\"}";
            var apiUrl = this.GetFiixRequestURLwithParameters();
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindResponse_AssetCategory apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_AssetCategory>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    // Log.Info("Get Fiix Assets with result of " + apiResult.totalObjects);
                    return apiResult.objects;
                }
                else
                {
                    if (apiResult.error!=null)  Log.Error("Fiix Gateway", "Get Fiix AssetCategories with error response, " +  apiResult.error.message);
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Get Fiix AssetCategories with error" + ex.Message);
                return null;
            }
        }

        public async Task<Fiix_AssetEventType[]> FindAssetEventTypes()
        {
            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"AssetEventType\",\"fields\":\"*\"}";
            var apiUrl = this.GetFiixRequestURLwithParameters();
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindResponse_AssetEventType apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_AssetEventType>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    // Log.Info("Get Fiix AssetEventTypes with result of " + apiResult.totalObjects);
                    return apiResult.objects;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Get Fiix AssetEventTypes with error" + ex.Message);
                return null;
            }
        }

        public async Task<Fiix_ReasonToSetAssetOffline[]> FindReasonToSetAssetOffline()
        {
            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"ReasonToSetAssetOffline\",\"fields\":\"*\"}";
            var apiUrl = this.GetFiixRequestURLwithParameters();
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindResponse_ReasonToSetAssetOffline apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_ReasonToSetAssetOffline>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    // Log.Info("Get Fiix ReasonToSetAssetOffline with result of " + apiResult.totalObjects);
                    return apiResult.objects;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Get Fiix ReasonToSetAssetOffline with error" + ex.Message);
                return null;
            }
        }

        public async Task<Fiix_ReasonToSetAssetOffline[]> FindReasonToSetAssetOnline()
        {
            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"ReasonToSetAssetOnline\",\"fields\":\"*\"}";
            var apiUrl = this.GetFiixRequestURLwithParameters();
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindResponse_ReasonToSetAssetOffline apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_ReasonToSetAssetOffline>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    // Log.Info("Get Fiix ReasonToSetAssetOnline with result of " + apiResult.totalObjects);
                    return apiResult.objects;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Get Fiix ReasonToSetAssetOnline with error" + ex.Message);
                return null;
            }
        }

        public async Task<Fiix_MeterReadingUnit[]> FindMeterReadingUnits()
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();

            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"MeterReadingUnit\",\"fields\":\"*\"}";

            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindResponse_MeterReadingUnit apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_MeterReadingUnit>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    // Log.Info("Get Fiix MeterReadingUnits with result of " + apiResult.totalObjects);
                    return apiResult.objects;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Get Fiix MeterReadingUnits with error" + ex.Message);
                return null;
            }
        }

        public async Task<Fiix_RequestResponse> ChangeAssetOnlineStatus(int id, int isOnline)
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();

            string jsonPayload = GetPayloadBase("ChangeRequest") + "\"className\": \"Asset\",\"fields\":\"id, strName, bolIsOnline\",\"changeFields\":\"bolIsOnline\",";
            jsonPayload = jsonPayload + "\"object\":{\"id\":" + id + ",\"bolIsOnline\":" + isOnline + ",\"className\":\"Asset\"}}";

            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_RequestResponse apiResult = await response.Content.ReadFromJsonAsync<Fiix_RequestResponse>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null)
                {
                    // Log.Info("Change Asset Online Status of result of affected object count " + apiResult.count);
                    return apiResult;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Fiix Change Asset Status call with error" + ex.Message);
                return null;
            }
        }

        public async Task<string> CloseLastAssetOfflineTracker(int id, int reasonOnlineID, string additionalInfo, double hoursAffected)
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();

           // Find last AsssetOfflineTracker by the given AssetID
            string filters = "\"filters\": [{ \"ql\": \"intAssetID = ?\", \"parameters\" : [" + id + "]},{\"ql\": \"dtmoffLineTo IS NULL\"}]";
            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"AssetOfflineTracker\"," + filters + ",\"fields\":\"*\",\"maxObjects\":100,\"orderBy\":\"dtmOfflineFrom\"}";            

            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();

                Fiix_FindResponse_AssetOfflineTracker apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_AssetOfflineTracker>();

                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    Fiix_AssetOfflineTracker fiix_AssetOfflineTracker = apiResult.objects[apiResult.totalObjects-1];
                    // If the AssetOfflineTracker have no dtmOffLineTo data, add with current Datetime.
                    if (fiix_AssetOfflineTracker != null && fiix_AssetOfflineTracker.dtmOffLineTo == 0)
                    {
                        Int64 dt = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        jsonPayload = GetPayloadBase("ChangeRequest") + "\"className\": \"AssetOfflineTracker\",\"fields\":\"id, dtmOfflineFrom, dtmOffLineTo, strOnlineAdditionalInfo, dblProductionHoursAffected\",\"changeFields\":\"dtmOffLineTo, strOnlineAdditionalInfo, dblProductionHoursAffected\",";
                        jsonPayload = jsonPayload + "\"object\":{\"id\":" + fiix_AssetOfflineTracker.id + ",\"dtmOffLineTo\":" + dt + ",\"intReasonOnlineID\":" + reasonOnlineID + ",\"strOnlineAdditionalInfo\":\"" + additionalInfo + "\"";
                        if (reasonOnlineID > -1) jsonPayload = jsonPayload + ",\"intReasonOnlineID\":" + reasonOnlineID;
                        if (hoursAffected > -1) jsonPayload = jsonPayload + ",\"dblProductionHoursAffected\":" + hoursAffected + ",\"className\":\"AssetOfflineTracker\"}}";
                        else jsonPayload = jsonPayload + ",\"className\":\"AssetOfflineTracker\"}}";

                        content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
                        response = await this.PostAsync(apiUrl, content);
                        response.EnsureSuccessStatusCode();
                        Fiix_RequestResponse apiResult2 = await response.Content.ReadFromJsonAsync<Fiix_RequestResponse>();
                        if (apiResult2 != null && apiResult2.count > 0)
                        {
                            return "Closing OfflineTracker " + fiix_AssetOfflineTracker.id + " succeeded.";
                        }
                        else return "Closing OfflineTracker " + fiix_AssetOfflineTracker.id + " failed.";
                    }
                    else return ("Last OfflineTracker of the asset is already closed");
                }
                else return "No OfflineTracker to be closed";
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Fiix Change Asset Status call with error" + ex.Message);
                return "Error when try close existing Offline Tracker" + ex.Message;
            }

 

        }

        public async Task<Fiix_RequestResponse> AddAssetEvent(int assetID, int eventTypeID, string additionalDescription)
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();
            Int64 dt = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            string jsonPayload = GetPayloadBase("AddRequest") + "\"className\": \"AssetEvent\",\"fields\":\"id, intAssetEventTypeID, intAssetID\",";
            jsonPayload = jsonPayload + "\"object\":{\"dtmDateSubmitted\":" + dt + ",\"intAssetEventTypeID\":" + eventTypeID + ",\"intAssetID\":" + assetID;
            // if (workOrderID != -1) jsonPayload = jsonPayload + ",\"intWorkOrderID\":" + workOrderID;
            if (additionalDescription != null && additionalDescription.Trim() != "") jsonPayload = jsonPayload + ",\"strAdditionalDescription\":" + additionalDescription;
            jsonPayload = jsonPayload + ",\"className\":\"AssetEvent\"}}";

            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_RequestResponse apiResult = await response.Content.ReadFromJsonAsync<Fiix_RequestResponse>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null)
                {
                    return apiResult;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Fiix Add AssetEvent call with error" + ex.Message);
                return null;
            }
        }

        public async Task<Fiix_RequestResponse> AddAssetOfflineTracker(int assetID, int reasonOfflineID, int workOrderID, string additionalInfo)
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();
            // string dt = DateTimeOffset.Now.ToString("s");   When using dtm format, always pass UTC time
            string dt = DateTime.UtcNow.ToString("s");

            string jsonPayload = GetPayloadBase("AddRequest") + "\"className\": \"AssetOfflineTracker\",\"fields\":\"id, intReasonOfflineID, intAssetID\",";
            //jsonPayload = jsonPayload + "\"object\":{\"dtmOfflineFrom\":\"" + dt + "\",\"intReasonOfflineID\":" + reasonOfflineID + ",\"intAssetID\":" + assetID;
            jsonPayload = jsonPayload + "\"object\":{\"dtmOfflineFrom\":\"" + dt +  "\",\"intAssetID\":" + assetID;
            if (reasonOfflineID != -1) jsonPayload = jsonPayload +  ",\"intReasonOfflineID\":" + reasonOfflineID;
            if (workOrderID != -1) jsonPayload = jsonPayload + ",\"intWorkOrderID\":" + workOrderID;
            if (additionalInfo != null && additionalInfo.Trim() != "") jsonPayload = jsonPayload + ",\"strOfflineAdditionalInfo\":\"" + additionalInfo + "\"";
            jsonPayload = jsonPayload + ",\"className\":\"AssetOfflineTracker\"}}";

            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_RequestResponse apiResult = await response.Content.ReadFromJsonAsync<Fiix_RequestResponse>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null)
                {
                    return apiResult;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Fiix Add AssetOfflineTracker call with error" + ex.Message);
                return null;
            }
        }


        public async Task<Fiix_RequestResponse> AddMeterReading(int assetID, int meterReadingUnitsID, double meterReading)
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();
            Int64 dt = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            string jsonPayload = GetPayloadBase("AddRequest") + "\"className\": \"MeterReading\",\"fields\":\"id,intUpdated\",";
            jsonPayload = jsonPayload + "\"object\":{\"dtmDateSubmitted\":" + dt + ",\"dblMeterReading\":" + meterReading + ",\"intAssetID\":" + assetID;
            // if (workOrderID != -1) jsonPayload = jsonPayload + ",\"intWorkOrderID\":" + workOrderID ;
            jsonPayload = jsonPayload + ",\"intMeterReadingUnitsID\":" + meterReadingUnitsID + ",\"className\":\"MeterReading\"}}";

            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_RequestResponse apiResult = await response.Content.ReadFromJsonAsync<Fiix_RequestResponse>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null)
                {
                    return apiResult;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Fiix Add MeterReading call with error" + ex.Message);
                return null;
            }
        }

        public async Task<Fiix_AddBatchResponse_MeterReading> AddMeterReadingBatch(string batchPayload)
        {
            var apiUrl = this.GetFiixRequestURLwithParameters();
            string jsonBatchPayload = GetBatchPayloadBase() + "\"requests\": [" + batchPayload + "]}";

            var content = new StringContent(jsonBatchPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_AddBatchResponse_MeterReading apiResult = await response.Content.ReadFromJsonAsync<Fiix_AddBatchResponse_MeterReading>();
                return apiResult;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Add multiple Assets meter readings in batch with error " + ex.Message);
                return null;
            }
        }
        public async Task<Fiix_MeterReading[]> FindMeterReadingByAssetAndTimeRange(int intAssetID, DateTime startDT, DateTime endDT)
        {
            string dtmStart = startDT.ToString("s");
            string dtmEnd = endDT.ToString("s");

            var apiUrl = this.GetFiixRequestURLwithParameters();
            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"MeterReading\",\"fields\":\"*\",";

            string filters = "\"filters\": [{ \"ql\": \"intAssetID = ?\", \"parameters\" : [" + intAssetID + "]}";
            filters = filters + ",{ \"ql\": \"dtmDateSubmitted > ? AND dtmDateSubmitted < ?\", \"parameters\" : [\"" + dtmStart + "\",\"" + dtmEnd + "\"]}]";

            jsonPayload = jsonPayload + filters + "}";


            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindResponse_MeterReading apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_MeterReading>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    // Log.Info("Get Fiix MeterReading with result of " + apiResult.totalObjects);
                    return apiResult.objects;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Get Fiix MeterReading by Asset and time with error" + ex.Message);
                return null;
            }
        }

        public async Task<Fiix_AssetEvent[]> FindAssetEventByAssetAndTimeRange(int intAssetID, DateTime startDT, DateTime endDT)
        {
            string dtmStart = startDT.ToString("s");
            string dtmEnd = endDT.ToString("s");

            var apiUrl = this.GetFiixRequestURLwithParameters();
            string jsonPayload = GetPayloadBase("FindRequest") + "\"className\": \"AssetEvent\",\"fields\":\"*\",";

            string filters = "\"filters\": [{ \"ql\": \"intAssetID = ?\", \"parameters\" : [" + intAssetID + "]}";
            filters = filters + ",{ \"ql\": \"dtmDateSubmitted > ? AND dtmDateSubmitted < ?\", \"parameters\" : [\"" + dtmStart + "\",\"" + dtmEnd + "\"]}]";

            jsonPayload = jsonPayload + filters + "}";


            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "text/plain");
            try
            {
                var response = await this.PostAsync(apiUrl, content);
                response.EnsureSuccessStatusCode();
                Fiix_FindResponse_AssetEvent apiResult = await response.Content.ReadFromJsonAsync<Fiix_FindResponse_AssetEvent>();
                //Log.Info(response.StatusCode.ToString());
                if (apiResult != null && apiResult.totalObjects > 0)
                {
                    // Log.Info("Get Fiix AssetEvent with result of " + apiResult.totalObjects);
                    return apiResult.objects;
                }
                else return null;
            }
            catch (Exception ex)
            {
                Log.Error("Fiix Gateway", "Get Fiix AssetEvent by Asset and time with error" + ex.Message);
                return null;
            }
        }
        public static string GetPayloadBase(string requestName)
        {
            string payLoad = "{\"_maCn\":\"" + requestName + "\", \"clientVersion\": {\"major\":2, \"minor\":8, \"patch\":1},";
            return payLoad;
        }

        public string GetBatchPayloadBase()
        {
            string payLoad = "{\"_maCn\":\"" + "BatchRequest" + "\", \"clientVersion\": {\"major\":2, \"minor\":8, \"patch\":1},";
            return payLoad;
        }

        // Compose filter string
        // categoryFilterCode       -1: no filter;  1: Asset only (SysCode in 0,2,3,4,11);  2: Non-Asset only (SysCode Not in 0,2,3,4,11)
        //                          categoryFilterCode to provide filter for different set of assets.  (for example, filter Equipment/Tool having given text in name only)
        private string GetAssetFilterString(bool findSites, int categoryFilterCode, int assetLocationID, string filterNameString)
        {
            string filters = "\"filters\": [{ \"ql\": \"bolIsSite = ?\", \"parameters\" : [0]}";
            if (findSites) filters = "\"filters\": [{ \"ql\": \"bolIsSite = ?\", \"parameters\" : [1]}";
            if (categoryFilterCode > -1)
            {
                switch (categoryFilterCode)
                {
                    case 2:
                        filters = filters + ",{ \"ql\": \"intSuperCategorySysCode NOT IN (?,?,?,?,?)\", \"parameters\" : [0,2,3,4,11]}";
                        break;
                    case 1:
                        filters = filters + ",{ \"ql\": \"intSuperCategorySysCode IN (?,?,?,?,?)\", \"parameters\" : [0,2,3,4,11]}";

                        // Get enabled CategoryID array from Model Folder 
                        string part1 = "", part2 = "";
                        try
                        {
                            IUANode LogicObject = Project.Current.Find("FiixGatewayDesigntimeLogic");
                            if ((bool)LogicObject.GetVariable("Set_FilterEnabledAssetCategoryOnly").Value)
                            {
                                var categoryFolder = LogicObject.Owner.Owner.Find("AssetCategories");
                                if (categoryFolder != null)
                                {
                                    List<IUANode> assetCategories = categoryFolder.Children.Cast<IUANode>().ToList();
                                    foreach (IUANode category in assetCategories)
                                    {
                                        if ((bool)category.GetVariable("Cfg_enabled").Value)
                                        {
                                            part1 += "?,";
                                            part2 += category.GetVariable("id").Value + ",";
                                        }
                                    }
                                    if (part1.Length > 0) part1 = part1.Remove(part1.Length - 1, 1);
                                    if (part2.Length > 0) part2 = part2.Remove(part2.Length - 1, 1);
                                }
                            }
                        }
                        catch (Exception ex)
                        { Log.Error("Fiix Gateway", "Prepare Asset Filter by Category error: " + ex.Message); }
                        if (part1 != "") filters = filters + ",{ \"ql\": \"intCategoryID IN (" + part1 + " )\", \"parameters\" : [" + part2 + "]}";
                        //else filters = filters + ",{ \"ql\": \"intCategoryID IN (?)\", \"parameters\" : [-1]}";
                        break;
                    default:
                        break;
                }
            }
            if (assetLocationID > -1) filters = filters + ",{ \"ql\": \"intAssetLocationID = ?\", \"parameters\" : [" + assetLocationID + "]}";
            if (filterNameString.Trim() != "") filters = filters + ",{\"fields\":\"strName\",\"fullText\":\"" + filterNameString + "\"}]";
            else filters = filters + "]";
            //Log.Info("Asset Filter is: " + filters);
            return filters;
        }
    }

    public class GatewayUtils
    {
        public static FiixHttpClient GetFiixHttpClient()
        {
            // Get Configurations from DesigntimeLogic   
            IUANode designtimeLogic = Project.Current.Find("FiixGatewayDesigntimeLogic");
            if (designtimeLogic == null)
            {
                Log.Error("Fiix Gateway", "Get Fiix Http Client error: Couldnot find DesignTimeLogic to get configuration");
                return null;
            }

            // Initiate HttpClient
            var handler = new HttpClientHandler();
            handler.ClientCertificateOptions = ClientCertificateOption.Manual;
            handler.ServerCertificateCustomValidationCallback =
                (httpRequestMessage, cert, cetChain, policyErrors) =>
                {
                    return true;
                };
            string appKey = designtimeLogic.GetVariable("Cfg_AppKey").Value;
            string accessKey = designtimeLogic.GetVariable("Cfg_AccessKey").Value;
            string FiixURL = designtimeLogic.GetVariable("Cfg_FiixURL").Value;
            string secretKey = designtimeLogic.GetVariable("Cfg_SecretKey").Value;
            FiixHttpClient fiixHttpClient = new FiixHttpClient(handler, FiixURL, appKey, accessKey, secretKey);
            return fiixHttpClient;
        }

        public static void SyncAssetTree(bool isDesignTimeRun)
        {
            // when run in Runtime mode, no node will be added or removed upon discrepancy from call result.
            
            IUANode LogicObject = Project.Current.Find("FiixGatewayDesigntimeLogic");
            IUANode reportObject = LogicObject;
            if (!isDesignTimeRun) { reportObject = Project.Current.Find("FiixGatewayRuntimeLogic"); }
            if (LogicObject == null)
            {
                Log.Error("Fiix Gateway", "Update Assets in Runtime error: Couldnot find DesignTimeLogic to get configuration");
                return;
            }

            IUANode modelFolder = LogicObject.Owner.Owner.Find("Assets");
            IUANode AssetType = LogicObject.Owner.Find("Asset");
            FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();

            int newSiteCount = 0, updateSiteCount = 0, newAssetCount = 0, updateAssetCount = 0;

            // Sync Sites
            string filterName = (string)LogicObject.GetVariable("Set_FilterSiteNames").Value;
            Fiix_Asset[] fiixSites = fiixHttpClient.FindAssetsBatch(true, -1, -1, filterName, false).Result;
            reportObject.GetVariable("Sts_LastExecutionDatetime").Value = DateTime.Now;
            if (fiixSites == null)
            {
                reportObject.GetVariable("Sts_LastExecutionResult").Value = "Get Fiix Sites with no result.";
                return;
            }
            List<IUANode> sites = modelFolder.Children.Cast<IUANode>().ToList();

            // Delete extra nodes if enabled
            if (isDesignTimeRun && (bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
            {
                foreach (IUANode site in sites)
                {
                    if (!Array.Exists(fiixSites, fiixsite => fiixsite.id == (int)site.GetVariable("id").Value || fiixsite.strName == (string)site.GetVariable("strName").Value))
                    {
                        site.Delete();
                    }
                }
            }

            foreach (var fiixsite in fiixSites)
            {
                IUANode newSite;

                if (!sites.Exists(site => fiixsite.id == (int)site.GetVariable("id").Value))
                {
                    newSite = InformationModel.MakeObject("Site_" + fiixsite.strName, AssetType.NodeId);
                    newSite.GetVariable("id").Value = fiixsite.id;
                    newSite.GetVariable("strName").Value = fiixsite.strName;
                    newSite.GetVariable("strCode").Value = fiixsite.strCode;
                    newSite.GetVariable("strAddressParsed").Value = fiixsite.strAddressParsed;
                    newSite.GetVariable("strTimezone").Value = fiixsite.strTimezone;
                    newSite.GetVariable("intAssetLocationID").Value = fiixsite.intAssetLocationID;
                    newSite.GetVariable("intCategoryID").Value = fiixsite.intCategoryID;
                    newSite.GetVariable("intSiteID").Value = fiixsite.intSiteID;
                    newSite.GetVariable("intSuperCategorySysCode").Value = fiixsite.intSuperCategorySysCode;
                    newSite.GetVariable("strBinNumber").Value = fiixsite.strBinNumber;
                    newSite.GetVariable("strRow").Value = fiixsite.strRow;
                    newSite.GetVariable("strAisle").Value = fiixsite.strAisle;
                    newSite.GetVariable("strDescription").Value = fiixsite.strDescription;
                    newSite.GetVariable("strInventoryCode").Value = fiixsite.strInventoryCode;
                    newSite.GetVariable("strMake").Value = fiixsite.strMake;
                    newSite.GetVariable("strModel").Value = fiixsite.strModel;
                    newSite.GetVariable("strSerialNumber").Value = fiixsite.strSerialNumber;
                    newSite.GetVariable("bolIsOnline").Value = Convert.ToBoolean(fiixsite.bolIsOnline);
                    newSite.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixsite.intUpdated).DateTime;

                    newSiteCount++;
                    modelFolder.Add(newSite);

                    // Sort by name
                    var updatedSites = modelFolder.Children.Cast<IUANode>().ToList();
                    var cpCount = sites.Count();
                    for (int i = cpCount - 1; i >= 0; i--)
                    {
                        try
                        {
                            if (string.Compare(updatedSites[i].BrowseName, newSite.BrowseName) > 0 || updatedSites[i].BrowseName == "Template") newSite.MoveUp();
                        }
                        catch { Log.Info("Fiix Gateway", "error when sorting sites"); }
                    }
                }
                else
                {
                    newSite = sites.Find(site => fiixsite.id == site.GetVariable("id").Value);
                    newSite.BrowseName = "Site_" + fiixsite.strName;
                    newSite.GetVariable("strName").Value = fiixsite.strName;
                    newSite.GetVariable("strCode").Value = fiixsite.strCode;
                    newSite.GetVariable("strAddressParsed").Value = fiixsite.strAddressParsed;
                    newSite.GetVariable("strTimezone").Value = fiixsite.strTimezone;
                    newSite.GetVariable("intAssetLocationID").Value = fiixsite.intAssetLocationID;
                    newSite.GetVariable("intCategoryID").Value = fiixsite.intCategoryID;
                    newSite.GetVariable("intSiteID").Value = fiixsite.intSiteID;
                    newSite.GetVariable("intSuperCategorySysCode").Value = fiixsite.intSuperCategorySysCode;
                    newSite.GetVariable("strBinNumber").Value = fiixsite.strBinNumber;
                    newSite.GetVariable("strRow").Value = fiixsite.strRow;
                    newSite.GetVariable("strAisle").Value = fiixsite.strAisle;
                    newSite.GetVariable("strDescription").Value = fiixsite.strDescription;
                    newSite.GetVariable("strInventoryCode").Value = fiixsite.strInventoryCode;
                    newSite.GetVariable("strMake").Value = fiixsite.strMake;
                    newSite.GetVariable("strModel").Value = fiixsite.strModel;
                    newSite.GetVariable("strSerialNumber").Value = fiixsite.strSerialNumber;
                    newSite.GetVariable("bolIsOnline").Value = Convert.ToBoolean(fiixsite.bolIsOnline);
                    newSite.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixsite.intUpdated).DateTime;
                    updateSiteCount++;
                }
            }

            // Sync all Assets with isSite is false
            sites = modelFolder.Children.Cast<IUANode>().ToList();
            //if (modelFolder.Find("Template") != null) sites.Remove(modelFolder.Get("Template"));

            string sfilterName = (string)LogicObject.GetVariable("Set_FilterAssetNames").Value;
            Fiix_Asset[] fiixAllAssets;

            // Get all assets if filter string is empty, otherwise get all Location/Facility and assets with filter on name.
            if (sfilterName != null && sfilterName.Trim() == "" && !(bool)LogicObject.GetVariable("Set_FilterEnabledAssetCategoryOnly").Value)
            {
                fiixAllAssets = fiixHttpClient.FindAssetsBatch(false, -1, -1, sfilterName, false).Result;
            }
            else
            {
                // Seperate Facility/Location with Equipment/Tool assets for filtering by name function. Get Equipment/Tool with text included in filter only.
                Fiix_Asset[] pureAssets = fiixHttpClient.FindAssetsBatch(false, 1, -1, sfilterName, false).Result;
                Fiix_Asset[] nonAssets = fiixHttpClient.FindAssetsBatch(false, 2, -1, "", false).Result;
                if (nonAssets == null)
                {
                    if (pureAssets != null) fiixAllAssets = pureAssets.ToArray();
                    else fiixAllAssets = null;
                }
                else
                {
                    if (pureAssets != null && pureAssets.Length > 0) fiixAllAssets = nonAssets.Concat(pureAssets).ToArray();
                    else fiixAllAssets = nonAssets;
                }
            }

            if (fiixAllAssets == null)
            {
                reportObject.GetVariable("Sts_LastExecutionResult").Value += ", Get Fiix Facilities with no result.";
                return;
            }

            // Loop through sites to nested call find fiixFacility with parentID
            // if found in fiix array, check it is in Nodes (under the site), update when yes, create when no; remove the node from Nodes list
            // Check Delete extra node flag, remove nodes left in Nodes list
            foreach (IUANode site in sites) AddUpdateFacilityByLocation(site, fiixAllAssets);

            reportObject.GetVariable("Sts_LastExecutionResult").Value = newSiteCount + " new and " + updateSiteCount + " synced sites; " + newAssetCount + " new and " + updateAssetCount + " synced assets";

            void AddUpdateFacilityByLocation(IUANode parentNode, Fiix_Asset[] assets)
            {
                if (parentNode == null) return;
                // Get existing object nodes children
                var existingChildren = parentNode.Children.Cast<IUANode>().ToList();
                existingChildren.RemoveAll(x => x.NodeClass != NodeClass.Object || x.BrowseName.Contains("DesignTimeLogic"));

                foreach (Fiix_Asset asset in assets)
                {
                    // Check parent child relationship. Specially for (No Site), link any asset with AssetLocationID is 0 to (No Site)'s ID
                    bool isRootLocationWithoutSite = ((string)parentNode.GetVariable("strName").Value).Contains("(No Site)") && asset.intAssetLocationID == 0;
                    if ((asset.intAssetLocationID == (int)parentNode.GetVariable("id").Value) || isRootLocationWithoutSite)
                    {
                        // Check if the child already existing by id
                        IUANode currentNode = null;
                        bool found = false;

                        foreach (IUANode childNode in existingChildren)
                        {
                            if ((int)childNode.GetVariable("id").Value == asset.id)
                            // node with the same id exist, update; Specially for (No Site), update its children's LocationID from 0 to (No Site)'s ID
                            {
                                if (isRootLocationWithoutSite) childNode.GetVariable("intAssetLocationID").Value = (int)parentNode.GetVariable("id").Value;
                                else childNode.GetVariable("intAssetLocationID").Value = asset.intAssetLocationID;
                                childNode.GetVariable("intCategoryID").Value = asset.intCategoryID;
                                childNode.GetVariable("intSiteID").Value = asset.intSiteID;
                                childNode.GetVariable("intSuperCategorySysCode").Value = asset.intSuperCategorySysCode;
                                childNode.GetVariable("strAddressParsed").Value = asset.strAddressParsed;
                                childNode.GetVariable("strCode").Value = asset.strCode;
                                childNode.GetVariable("strName").Value = asset.strName;
                                childNode.GetVariable("strTimezone").Value = asset.strTimezone;
                                childNode.GetVariable("strBinNumber").Value = asset.strBinNumber;
                                childNode.GetVariable("strRow").Value = asset.strRow;
                                childNode.GetVariable("strAisle").Value = asset.strAisle;
                                childNode.GetVariable("strDescription").Value = asset.strDescription;
                                childNode.GetVariable("strInventoryCode").Value = asset.strInventoryCode;
                                childNode.GetVariable("strMake").Value = asset.strMake;
                                childNode.GetVariable("strModel").Value = asset.strModel;
                                childNode.GetVariable("strSerialNumber").Value = asset.strSerialNumber;
                                childNode.GetVariable("bolIsOnline").Value = Convert.ToBoolean(asset.bolIsOnline);
                                childNode.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(asset.intUpdated).DateTime;
                                updateAssetCount++;
                                existingChildren.Remove(childNode);
                                currentNode = childNode;
                                found = true;
                                break;
                            }
                        };
                        if (!found && isDesignTimeRun)      // add new; Specially for (No Site), update its children's LocationID from 0 to (No Site)'s ID
                        {
                            IUANode newNode = InformationModel.MakeObject(asset.strName, AssetType.NodeId);
                            newNode.GetVariable("id").Value = asset.id;
                            if (isRootLocationWithoutSite) newNode.GetVariable("intAssetLocationID").Value = (int)parentNode.GetVariable("id").Value;
                            else newNode.GetVariable("intAssetLocationID").Value = asset.intAssetLocationID;
                            newNode.GetVariable("intCategoryID").Value = asset.intCategoryID;
                            newNode.GetVariable("intSiteID").Value = asset.intSiteID;
                            newNode.GetVariable("intSuperCategorySysCode").Value = asset.intSuperCategorySysCode;
                            newNode.GetVariable("strAddressParsed").Value = asset.strAddressParsed;
                            newNode.GetVariable("strCode").Value = asset.strCode;
                            newNode.GetVariable("strName").Value = asset.strName;
                            newNode.GetVariable("strTimezone").Value = asset.strTimezone;
                            newNode.GetVariable("strBinNumber").Value = asset.strBinNumber;
                            newNode.GetVariable("strRow").Value = asset.strRow;
                            newNode.GetVariable("strAisle").Value = asset.strAisle;
                            newNode.GetVariable("strDescription").Value = asset.strDescription;
                            newNode.GetVariable("strInventoryCode").Value = asset.strInventoryCode;
                            newNode.GetVariable("strMake").Value = asset.strMake;
                            newNode.GetVariable("strModel").Value = asset.strModel;
                            newNode.GetVariable("strSerialNumber").Value = asset.strSerialNumber;
                            newNode.GetVariable("bolIsOnline").Value = Convert.ToBoolean(asset.bolIsOnline);
                            newNode.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(asset.intUpdated).DateTime;
                            parentNode.Add(newNode);
                            currentNode = newNode;
                            newAssetCount++;
                        }
                        AddUpdateFacilityByLocation(currentNode, assets);
                    }
                }
                // Delete extra node based on setting
                if (isDesignTimeRun && (bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
                {
                    foreach (IUANode childNode in existingChildren)
                    {
                        childNode.Delete();
                    }
                }
            }
        }

        // Prepare API payload for Fiix Add meter reading from DataLogger record, one variable per row format.
        // Decode base on var naming:  [AssetName]_AssetID[AssetID]_EU[EUName]_EUID[EUID]
        public static string GetMeterReadingBatchPayloadFromLogRecords(List<Record> records)
        {
            
            string jsonBatchPayload = " ";

            try
            {
                foreach (VariableRecord record in records)
                {
                    DateTimeOffset dto = new DateTimeOffset((DateTime)record.timestamp, TimeSpan.Zero);
                    Int64 dt = dto.ToUnixTimeMilliseconds();
                    int assetIDPos = record.variableId.IndexOf("_AssetID");
                    int EUPos = record.variableId.IndexOf("_EU");
                    int EUIDPos = record.variableId.IndexOf("_EUID");
                    if (assetIDPos < 0 || EUPos < 0 || EUIDPos < 0) continue;

                    assetIDPos = assetIDPos + 8;
                    EUIDPos = EUIDPos + 5;
                    // Log.Info("record:" + record.variableId + "  assetIDPos:" + assetIDPos + " ; EUPos" + EUPos);                
                    string assetID = record.variableId.Substring(assetIDPos, EUPos - assetIDPos);
                    string meterReadingUnitsID = record.variableId.Substring(EUIDPos);
                    double meterReading;
                    if (record.value == null) meterReading = Convert.ToDouble(record.serializedValue);
                    else meterReading = (double)record.value;

                    Log.Debug("Sending record with assetID " + assetID + ", unitID " + meterReadingUnitsID + ", value " + meterReading);

                    string jsonPayload = FiixHttpClient.GetPayloadBase("AddRequest") + "\"className\": \"MeterReading\",\"fields\":\"id,intUpdated\",";
                    jsonPayload = jsonPayload + "\"object\":{\"dtmDateSubmitted\":" + dt + ",\"dblMeterReading\":" + meterReading + ",\"intAssetID\":" + assetID;
                    jsonPayload = jsonPayload + ",\"intMeterReadingUnitsID\":" + meterReadingUnitsID + ",\"className\":\"MeterReading\"}}";
                    jsonBatchPayload = jsonBatchPayload + jsonPayload + ",";
                }
            }
            catch (Exception e)
            {
                Log.Error("Fiix Gateway", "Error in GetMeterReadingBatchPayloadFromLogRecords" + e.Message);
            }
            jsonBatchPayload = jsonBatchPayload.Remove(jsonBatchPayload.Length - 1, 1);
            return jsonBatchPayload;
        }

        public static string GetEngineeringUnitNameByID(int ID)
        {
            string result = "";
            IUAVariable meterReadingUnits = (IUAVariable)Project.Current.Find("MeterReadingUnits");
            if (meterReadingUnits == null) return result;
            Struct[] euStructItems = (Struct[])meterReadingUnits.Value.Value;
            foreach (EngineeringUnitDictionaryItem item in euStructItems)
            {
                if (item.UnitId == ID )
                {
                    result = item.DisplayName.Text;
                    break;
                }
            }
            return result;
        }
    }

    public abstract class Record
    {
        public Record(DateTime? timestamp)
        {
            this.timestamp = timestamp;
        }

        public override bool Equals(object obj)
        {
            var other = obj as Record;
            return timestamp == other.timestamp;
        }

        public readonly DateTime? timestamp;

    }

    public class DataLoggerRecord : Record
    {
        public DataLoggerRecord(DateTime timestamp, List<VariableRecord> variables) : base(timestamp)
        {
            this.variables = variables;
        }

        public DataLoggerRecord(DateTime timestamp, DateTime? localTimestamp, List<VariableRecord> variables) : base(timestamp)
        {
            this.localTimestamp = localTimestamp;
            this.variables = variables;
        }

        public override bool Equals(object obj)
        {
            DataLoggerRecord other = obj as DataLoggerRecord;

            if (other == null)
                return false;

            if (timestamp != other.timestamp)
                return false;

            if (localTimestamp != other.localTimestamp)
                return false;

            if (variables.Count != other.variables.Count)
                return false;

            for (int i = 0; i < variables.Count; ++i)
            {
                if (!variables[i].Equals(other.variables[i]))
                    return false;
            }

            return true;
        }

        public readonly DateTime? localTimestamp;
        public readonly List<VariableRecord> variables;
    }

    public class VariableRecord : Record
    {
        public VariableRecord(DateTime? timestamp,
                              string variableId,
                              UAValue value,
                              string serializedValue) : base(timestamp)
        {
            this.variableId = variableId;
            this.value = value;
            this.serializedValue = serializedValue;
            this.variableOpCode = null;
        }

        public VariableRecord(DateTime? timestamp,
                              string variableId,
                              UAValue value,
                              string serializedValue,
                              int? variableOpCode) : base(timestamp)
        {
            this.variableId = variableId;
            this.value = value;
            this.serializedValue = serializedValue;
            this.variableOpCode = variableOpCode;
        }

        public override bool Equals(object obj)
        {
            var other = obj as VariableRecord;
            return timestamp == other.timestamp &&
                   variableId == other.variableId &&
                   value == other.value &&
                   serializedValue == other.serializedValue &&
                   variableOpCode == other.variableOpCode;
        }

        public readonly string variableId;
        public readonly string serializedValue;
        public readonly UAValue value;
        public readonly int? variableOpCode;
    }

    public class Packet
    {
        public Packet(DateTime timestamp, string clientId)
        {
            this.timestamp = timestamp.ToUniversalTime();
            this.clientId = clientId;
        }

        public readonly DateTime timestamp;
        public readonly string clientId;
    }

    public class VariablePacket : Packet
    {
        public VariablePacket(DateTime timestamp,
                              string clientId,
                              List<VariableRecord> records) : base(timestamp, clientId)
        {
            this.records = records;
        }

        public readonly List<VariableRecord> records;
    }

    public class DataLoggerRowPacket : Packet
    {
        public DataLoggerRowPacket(DateTime timestamp,
                                   string clientId,
                                   List<DataLoggerRecord> records) : base(timestamp, clientId)
        {
            this.records = records;
        }

        public readonly List<DataLoggerRecord> records;
    }

    public class DataLoggerRecordUtils
    {
        public static List<DataLoggerRecord> GetDataLoggerRecordsFromQueryResult(object[,] resultSet,
                                                                                 string[] header,
                                                                                 List<VariableToLog> variablesToLogList,
                                                                                 bool insertOpCode,
                                                                                 bool insertVariableTimestamp,
                                                                                 bool logLocalTime)
        {
            var records = new List<DataLoggerRecord>();
            var rowCount = resultSet != null ? resultSet.GetLength(0) : 0;
            var columnCount = header != null ? header.Length : 0;
            for (int i = 0; i < rowCount; ++i)
            {
                var j = 0;
                var rowVariables = new List<VariableRecord>();
                DateTime rowTimestamp = GetTimestamp(resultSet[i, j++]);
                DateTime? rowLocalTimestamp = null;
                if (logLocalTime)
                    rowLocalTimestamp = DateTime.Parse(resultSet[i, j++].ToString());

                int variableIndex = 0;
                while (j < columnCount)
                {
                    string variableId = header[j];
                    object value = resultSet[i, j];
                    string serializedValue = SerializeValue(value, variablesToLogList[variableIndex]);

                    DateTime? timestamp = null;
                    if (insertVariableTimestamp)
                    {
                        ++j; // Consume timestamp column
                        var timestampColumnValue = resultSet[i, j];
                        if (timestampColumnValue != null)
                            timestamp = GetTimestamp(timestampColumnValue);
                    }

                    VariableRecord variableRecord;
                    if (insertOpCode)
                    {
                        ++j; // Consume operation code column
                        var opCodeColumnValue = resultSet[i, j];
                        int? opCode = (opCodeColumnValue != null) ? (Int32.Parse(resultSet[i, j].ToString())) : (int?)null;
                        variableRecord = new VariableRecord(timestamp, variableId, GetUAValue(value, variablesToLogList[variableIndex]), serializedValue, opCode);
                    }
                    else
                        variableRecord = new VariableRecord(timestamp, variableId, GetUAValue(value, variablesToLogList[variableIndex]), serializedValue);

                    rowVariables.Add(variableRecord);

                    ++j; // Consume Variable Column
                    ++variableIndex;
                }

                DataLoggerRecord record;
                if (logLocalTime)
                    record = new DataLoggerRecord(rowTimestamp, rowLocalTimestamp, rowVariables);
                else
                    record = new DataLoggerRecord(rowTimestamp, rowVariables);

                records.Add(record);
            }

            return records;
        }

        private static string SerializeValue(object value, VariableToLog variableToLog)
        {
            if (value == null)
                return null;
            var valueType = variableToLog.ActualDataType;
            if (valueType == OpcUa.DataTypes.DateTime)
                return (GetTimestamp(value)).ToString("O");
            else if (valueType == OpcUa.DataTypes.Float)
                return ((float)((double)value)).ToString("G9");
            else if (valueType == OpcUa.DataTypes.Double)
                return ((double)value).ToString("G17");

            return value.ToString();
        }

        private static UAValue GetUAValue(object value, VariableToLog variableToLog)
        {
            if (value == null)
                return null;
            try
            {
                NodeId valueType = variableToLog.ActualDataType;
                if (valueType == OpcUa.DataTypes.Boolean)
                    return new UAValue(Int32.Parse(GetBoolean(value)));
                else if (valueType == OpcUa.DataTypes.Integer)
                    return new UAValue(Int64.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.UInteger)
                    return new UAValue(UInt64.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.Byte)
                    return new UAValue(Byte.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.SByte)
                    return new UAValue(SByte.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.Int16)
                    return new UAValue(Int16.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.UInt16)
                    return new UAValue(UInt16.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.Int32)
                    return new UAValue(Int32.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.UInt32)
                    return new UAValue(UInt32.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.Int64)
                    return new UAValue(Int64.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.UInt64)
                    return new UAValue(UInt64.Parse(value.ToString()));
                else if (valueType == OpcUa.DataTypes.Float)
                    return new UAValue((float)((double)value));
                else if (valueType == OpcUa.DataTypes.Double)
                    return new UAValue((double)value);
                else if (valueType == OpcUa.DataTypes.DateTime)
                    return new UAValue(GetTimestamp(value));
                else if (valueType == OpcUa.DataTypes.String)
                    return new UAValue(value.ToString());
                else if (valueType == OpcUa.DataTypes.ByteString)
                    return new UAValue((ByteString)value);
                else if (valueType == OpcUa.DataTypes.NodeId)
                    return new UAValue((NodeId)value);
            }
            catch (Exception e)
            {
                Log.Warning("DataLoggerRecordUtils", $"Parse Exception: {e.Message}.");
                throw;
            }

            return null;
        }

        private static string GetBoolean(object value)
        {
            var valueString = value.ToString();
            if (valueString == "0" || valueString == "1")
                return valueString;

            if (valueString.ToLower() == "false")
                return "0";
            else
                return "1";
        }

        private static DateTime GetTimestamp(object value)
        {
            if (Type.GetTypeCode(value.GetType()) == TypeCode.DateTime)
                return ((DateTime)value);
            else
                return DateTime.SpecifyKind(DateTime.Parse(value.ToString()), DateTimeKind.Utc);
        }
    }

    public class DataLoggerStoreWrapper
    {
        public DataLoggerStoreWrapper(Store store,
                                      string tableName,
                                      List<VariableToLog> variablesToLogList,
                                      bool insertOpCode,
                                      bool insertVariableTimestamp,
                                      bool logLocalTime)
        {
            this.store = store;
            this.tableName = tableName;
            this.variablesToLogList = variablesToLogList;
            this.insertOpCode = insertOpCode;
            this.insertVariableTimestamp = insertVariableTimestamp;
            this.logLocalTime = logLocalTime;
        }

        public void DeletePulledRecords()
        {
            if (store.Status == StoreStatus.Offline)
                return;

            try
            {
                Log.Verbose1("DataLoggerStoreWrapper", "Delete records pulled from data logger temporary table.");

                string query = $"DELETE FROM \"{tableName}\" AS D " +
                               $"WHERE \"Id\" IN " +
                               $"( SELECT \"Id\" " +
                               $"FROM \"##tempDataLoggerTable\")";

                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to delete from data logger temporary table {e.Message}.");
                throw;
            }

            DeleteTemporaryTable();
        }

        public List<DataLoggerRecord> QueryNewEntries()
        {
            Log.Verbose1("DataLoggerStoreWrapper", "Query new entries from data logger.");

            if (store.Status == StoreStatus.Offline)
                return new List<DataLoggerRecord>();

            CopyNewEntriesToTemporaryTable();
            List<DataLoggerRecord> records = QueryNewEntriesFromTemporaryTable();

            if (records.Count == 0)
                DeleteTemporaryTable();

            return records;
        }

        public List<DataLoggerRecord> QueryNewEntriesUsingLastQueryId(UInt64 rowId)
        {
            Log.Verbose1("DataLoggerStoreWrapper", $"Query new entries with id greater than {rowId} (store status: {store.Status}).");

            if (store.Status == StoreStatus.Offline)
                return new List<DataLoggerRecord>();

            CopyNewEntriesToTemporaryTableUsingId(rowId);
            List<DataLoggerRecord> records = QueryNewEntriesFromTemporaryTable();

            if (records.Count == 0)
                DeleteTemporaryTable();

            return records;
        }

        public UInt64? GetMaxIdFromTemporaryTable()
        {
            object[,] resultSet;

            try
            {
                string query = $"SELECT MAX(\"Id\") FROM \"##tempDataLoggerTable\"";

                if (store.Status == StoreStatus.Online)
                {
                    store.Query(query, out _, out resultSet);
                    DeleteTemporaryTable();

                    if (resultSet[0, 0] != null)
                    {
                        Log.Verbose1("DataLoggerStoreWrapper", $"Get max id from data logger temporary table returns {resultSet[0, 0]}.");
                        return UInt64.Parse(resultSet[0, 0].ToString());
                    }
                }

                return null;
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to query maxid from data logger temporary table: {e.Message}.");
                throw;
            }
        }

        public UInt64? GetDataLoggerMaxId()
        {
            object[,] resultSet;

            try
            {
                string query = $"SELECT MAX(\"Id\") FROM \"{tableName}\"";

                if (store.Status == StoreStatus.Online)
                {
                    store.Query(query, out _, out resultSet);

                    if (resultSet[0, 0] != null)
                    {
                        Log.Verbose1("DataLoggerStoreWrapper", $"Get data logger max id returns {resultSet[0, 0]}.");
                        return UInt64.Parse(resultSet[0, 0].ToString());
                    }
                }

                return null;
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to query maxid from data logger temporary table: {e.Message}.");
                throw;
            }
        }

        public StoreStatus GetStoreStatus()
        {
            return store.Status;
        }

        private void CopyNewEntriesToTemporaryTable()
        {
            try
            {
                Log.Verbose1("DataLoggerStoreWrapper", "Copy new entries to data logger temporary table.");

                string query = $"CREATE TEMPORARY TABLE \"##tempDataLoggerTable\" AS " +
                               $"SELECT * " +
                               $"FROM \"{tableName}\" " +
                               $"WHERE \"Id\" IS NOT NULL " +
                               $"ORDER BY \"Timestamp\" ASC ";

                if (store.Status == StoreStatus.Online)
                    store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to create internal temporary table: {e.Message}.");
                throw;
            }
        }

        private void CopyNewEntriesToTemporaryTableUsingId(UInt64 rowId)
        {
            try
            {
                Int64 id = rowId == Int64.MaxValue ? -1 : (Int64)rowId; // -1 to consider also id = 0
                Log.Verbose1("DataLoggerStoreWrapper", $"Copy new entries to data logger temporary table with id greater than {id}.");

                string query = $"CREATE TEMPORARY TABLE \"##tempDataLoggerTable\" AS " +
                               $"SELECT * " +
                               $"FROM \"{tableName}\" " +
                               $"WHERE \"Id\" > {id} " +
                               $"ORDER BY \"Timestamp\" ASC ";

                if (store.Status == StoreStatus.Online)
                    store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to create internal temporary table: {e.Message}.");
                throw;
            }
        }

        public void DeleteTemporaryTable()
        {
            object[,] resultSet;
            string[] header;

            try
            {
                Log.Verbose1("DataLoggerStoreWrapper", "Delete data logger temporary table.");
                string query = $"DROP TABLE \"##tempDataLoggerTable\"";
                store.Query(query, out header, out resultSet);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to delete internal temporary table: {e.Message}.");
                throw;
            }
        }

        private List<DataLoggerRecord> QueryNewEntriesFromTemporaryTable()
        {
            List<DataLoggerRecord> records = null;
            object[,] resultSet;
            string[] header;

            try
            {
                string query = $"SELECT {GetQuerySelectParameters()} " +
                               $"FROM \"##tempDataLoggerTable\"";

                if (store.Status == StoreStatus.Online)
                {
                    store.Query(query, out header, out resultSet);
                    records = DataLoggerRecordUtils.GetDataLoggerRecordsFromQueryResult(resultSet,
                                                                                        header,
                                                                                        variablesToLogList,
                                                                                        insertOpCode,
                                                                                        insertVariableTimestamp,
                                                                                        logLocalTime);
                }
                else
                    records = new List<DataLoggerRecord>();

                Log.Verbose1("DataLoggerStoreWrapper", $"Query new entries from data logger temporary table (records count={records.Count}, query={query}).");
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStoreWrapper", $"Failed to query the internal temporary table: {e.Message}.");
                throw;
            }

            return records;
        }

        private string GetQuerySelectParameters()
        {
            var selectParameters = "\"Timestamp\", ";
            if (logLocalTime)
                selectParameters += "\"LocalTimestamp\", ";

            selectParameters = $"{selectParameters} {GetQueryColumnsOrderedByVariableName()}";

            return selectParameters;
        }

        private string GetQueryColumnsOrderedByVariableName()
        {
            var columnsOrderedByVariableName = string.Empty;
            foreach (var variable in variablesToLogList)
            {
                if (columnsOrderedByVariableName != string.Empty)
                    columnsOrderedByVariableName += ", ";

                columnsOrderedByVariableName += "\"" + variable.BrowseName + "\"";

                if (insertVariableTimestamp)
                    columnsOrderedByVariableName += ", \"" + variable.BrowseName + "_Timestamp\"";

                if (insertOpCode)
                    columnsOrderedByVariableName += ", \"" + variable.BrowseName + "_OpCode\"";
            }

            return columnsOrderedByVariableName;
        }

        public void DeleteRecords(int numberOfRecordsToDelete)
        {
            try
            {

                string query = $"DELETE FROM \"{tableName}\" " +
                               $"ORDER BY \"Timestamp\" ASC " +
                               $"LIMIT {numberOfRecordsToDelete}";

                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("DataLogger Store", $"Failed to delete data from DataLogger store: {e.Message}.");
                throw;
            }
        }

        private readonly Store store;
        private readonly string tableName;
        private readonly List<VariableToLog> variablesToLogList;
        private readonly bool insertOpCode;
        private readonly bool insertVariableTimestamp;
        private readonly bool logLocalTime;
    }

    public interface SupportStore
    {
        void InsertRecords(List<Record> records);
        void DeleteRecords(int numberOfRecordsToDelete);
        long RecordsCount();
        List<Record> QueryOlderEntries(int numberOfEntries);
    }

    public class PushAgentStoreDataLoggerWrapper : SupportStore
    {
        public PushAgentStoreDataLoggerWrapper(Store store,
                                               string tableName,
                                               List<VariableToLog> variablesToLogList,
                                               bool insertOpCode,
                                               bool insertVariableTimestamp,
                                               bool logLocalTime)
        {
            this.store = store;
            this.tableName = tableName;
            this.variablesToLogList = variablesToLogList;
            this.insertOpCode = insertOpCode;
            this.insertVariableTimestamp = insertVariableTimestamp;
            this.logLocalTime = logLocalTime;

            try
            {
                CreateTable();
                table = GetTable();
                CreateColumns();
                CreateColumnIndex("Id", true);
                CreateColumnIndex("Timestamp", false);
                columns = GetTableColumnsOrderedByVariableName();
                idCount = GetMaxId();
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Unable to create PushAgent store: {e.Message}.");
                throw;
            }
        }

        public void DeleteRecords(int numberOfRecordsToDelete)
        {
            try
            {

                string query = $"DELETE FROM \"{tableName}\" " +
                               $"ORDER BY \"Timestamp\" ASC, \"Id\" ASC " +
                               $"LIMIT {numberOfRecordsToDelete}";

                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Failed to delete data from PushAgent store: {e.Message}.");
                throw;
            }
        }

        public void InsertRecords(List<Record> records)
        {
            List<DataLoggerRecord> dataLoggerRecords = records.Cast<DataLoggerRecord>().ToList();
            object[,] values = new object[records.Count, columns.Length];
            ulong tempIdCount = idCount;
            for (int i = 0; i < dataLoggerRecords.Count; ++i)
            {
                int j = 0;
                values[i, j++] = tempIdCount;
                values[i, j++] = dataLoggerRecords[i].timestamp;
                if (logLocalTime)
                    values[i, j++] = dataLoggerRecords[i].localTimestamp;

                foreach (var variable in dataLoggerRecords.ElementAt(i).variables)
                {
                    values[i, j++] = variable.value?.Value;
                    if (insertVariableTimestamp)
                        values[i, j++] = variable.timestamp;
                    if (insertOpCode)
                        values[i, j++] = variable.variableOpCode;
                }

                tempIdCount = GetNextInternalId(tempIdCount);
            }

            try
            {
                table.Insert(columns, values);
                idCount = tempIdCount;          // If all record are inserted then we update the idCount
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Failed to insert data into PushAgent store: {e.Message}.");
                throw;
            }
        }

        public List<Record> QueryOlderEntries(int numberOfEntries)
        {
            List<Record> records = null;
            object[,] resultSet;
            string[] header;

            try
            {
                string query = $"SELECT {GetQuerySelectParameters()} " +
                               $"FROM \"{tableName}\" " +
                               $"ORDER BY \"Timestamp\" ASC, \"Id\" ASC " +
                               $"LIMIT {numberOfEntries}";

                store.Query(query, out header, out resultSet);
                records = DataLoggerRecordUtils.GetDataLoggerRecordsFromQueryResult(resultSet,
                                                                                    header,
                                                                                    variablesToLogList,
                                                                                    insertOpCode,
                                                                                    insertVariableTimestamp,
                                                                                    logLocalTime).Cast<Record>().ToList();
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Failed to query older entries from PushAgent store: {e.Message}.");
                throw;
            }

            return records;
        }

        public long RecordsCount()
        {
            object[,] resultSet;
            long result = 0;

            try
            {
                string query = $"SELECT COUNT(*) FROM \"{tableName}\"";
                store.Query(query, out _, out resultSet);
                result = ((long)resultSet[0, 0]);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Failed to query count: {e.Message}.");
                throw;
            }

            return result;
        }

        private UInt64 GetMaxId()
        {
            object[,] resultSet;

            try
            {
                string query = $"SELECT MAX(\"Id\") FROM \"{tableName}\"";
                store.Query(query, out _, out resultSet);

                if (resultSet[0, 0] != null)
                    return GetNextInternalId(UInt64.Parse(resultSet[0, 0].ToString()));
                else
                    return 0;
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Failed to query maxid: {e.Message}.");
                throw;
            }
        }

        private UInt64 GetNextInternalId(UInt64 currentId)
        {
            return currentId < Int64.MaxValue ? currentId + 1 : 0;
        }

        private void CreateTable()
        {
            try
            {
                store.AddTable(tableName);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Unable to create PushAgent store: {e.Message}.");
                throw;
            }
        }

        private Table GetTable()
        {
            return store.Tables.FirstOrDefault(t => t.BrowseName == tableName);
        }

        private void CreateColumns()
        {
            try
            {
                table.AddColumn("Id", OpcUa.DataTypes.UInt64);
                table.AddColumn("Timestamp", OpcUa.DataTypes.DateTime);
                if (logLocalTime)
                    table.AddColumn("LocalTimestamp", OpcUa.DataTypes.DateTime);

                foreach (var variableToLog in variablesToLogList)
                {
                    table.AddColumn(variableToLog.BrowseName, variableToLog.ActualDataType);

                    if (insertVariableTimestamp)
                        table.AddColumn(variableToLog.BrowseName + "_Timestamp", OpcUa.DataTypes.DateTime);

                    if (insertOpCode)
                        table.AddColumn(variableToLog.BrowseName + "_OpCode", OpcUa.DataTypes.Int32);
                }
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreDataLoggerWrapper", $"Unable to create columns of PushAgent store: {e.Message}.");
                throw;
            }
        }

        private void CreateColumnIndex(string columnName, bool unique)
        {
            string uniqueKeyWord = string.Empty;
            if (unique)
                uniqueKeyWord = "UNIQUE";
            try
            {
                string query = $"CREATE {uniqueKeyWord} INDEX \"{columnName}_index\" ON  \"{tableName}\"(\"{columnName}\")";
                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Warning("PushAgentStoreDataLoggerWrapper", $"Unable to create index on PushAgent store: {e.Message}.");
            }
        }

        private string[] GetTableColumnsOrderedByVariableName()
        {
            List<string> columnNames = new List<string>();
            columnNames.Add("Id");
            columnNames.Add("Timestamp");
            if (logLocalTime)
                columnNames.Add("LocalTimestamp");

            foreach (var variableToLog in variablesToLogList)
            {
                columnNames.Add(variableToLog.BrowseName);

                if (insertVariableTimestamp)
                    columnNames.Add(variableToLog.BrowseName + "_Timestamp");

                if (insertOpCode)
                    columnNames.Add(variableToLog.BrowseName + "_OpCode");
            }

            return columnNames.ToArray();
        }

        private string GetQuerySelectParameters()
        {
            var selectParameters = "\"Timestamp\", ";
            if (logLocalTime)
                selectParameters += "\"LocalTimestamp\", ";

            selectParameters = $"{selectParameters} {GetQueryColumnsOrderedByVariableName()}";

            return selectParameters;
        }

        private string GetQueryColumnsOrderedByVariableName()
        {
            string columnsOrderedByVariableName = string.Empty;
            foreach (var variable in variablesToLogList)
            {
                if (columnsOrderedByVariableName != string.Empty)
                    columnsOrderedByVariableName += ", ";

                columnsOrderedByVariableName += "\"" + variable.BrowseName + "\"";

                if (insertVariableTimestamp)
                    columnsOrderedByVariableName += ", \"" + variable.BrowseName + "_Timestamp\"";

                if (insertOpCode)
                    columnsOrderedByVariableName += ", \"" + variable.BrowseName + "_OpCode\"";
            }

            return columnsOrderedByVariableName;
        }

        private readonly Store store;
        private readonly Table table;
        private readonly string tableName;
        private readonly List<VariableToLog> variablesToLogList;
        private readonly string[] columns;
        private readonly bool insertOpCode;
        private readonly bool insertVariableTimestamp;
        private readonly bool logLocalTime;
        private UInt64 idCount;
    }

    public class PushAgentStoreRowPerVariableWrapper : SupportStore
    {
        public PushAgentStoreRowPerVariableWrapper(SQLiteStore store, string tableName, bool insertOpCode)
        {
            this.store = store;
            this.tableName = tableName;
            this.insertOpCode = insertOpCode;

            try
            {
                CreateTable();
                table = GetTable();
                CreateColumns();
                CreateColumnIndex("Id", true);
                CreateColumnIndex("Timestamp", false);
                columns = GetTableColumnNames();
                idCount = GetMaxId();
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Unable to create PushAgent store: {e.Message}.");
                throw;
            }
        }

        public void DeleteRecords(int numberOfRecordsToDelete)
        {
            try
            {
                string query = $"DELETE FROM \"{tableName}\" " +
                               $"ORDER BY \"Timestamp\" ASC, \"Id\" ASC " +
                               $"LIMIT {numberOfRecordsToDelete}";

                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to delete data from PushAgent store: {e.Message}.");
                throw;
            }
        }

        public void InsertRecords(List<Record> records)
        {
            List<VariableRecord> variableRecords = records.Cast<VariableRecord>().ToList();
            object[,] values = new object[records.Count, columns.Length];
            UInt64 tempIdCount = idCount;
            for (int i = 0; i < variableRecords.Count; ++i)
            {
                values[i, 0] = tempIdCount;
                values[i, 1] = variableRecords[i].timestamp.Value;
                values[i, 2] = variableRecords[i].variableId;
                values[i, 3] = variableRecords[i].serializedValue;
                if (insertOpCode)
                    values[i, 4] = variableRecords[i].variableOpCode;

                tempIdCount = GetNextInternalId(tempIdCount);
            }

            try
            {
                table.Insert(columns, values);
                idCount = tempIdCount;
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to insert data into PushAgent store: {e.Message}.");
                throw;
            }
        }

        public List<Record> QueryOlderEntries(int numberOfEntries)
        {
            List<VariableRecord> records = new List<VariableRecord>();
            object[,] resultSet;
            string[] header;

            try
            {
                string query = $"SELECT {GetQueryColumns()} " +
                               $"FROM \"{tableName}\" " +
                               $"ORDER BY \"Timestamp\" ASC, \"Id\" ASC " +
                               $"LIMIT {numberOfEntries}";

                store.Query(query, out header, out resultSet);

                var rowCount = resultSet != null ? resultSet.GetLength(0) : 0;
                for (int i = 0; i < rowCount; ++i)
                {
                    int? opCodeValue = (int?)null;
                    if (insertOpCode)
                    {
                        if (resultSet[i, 3] == null)
                            opCodeValue = null;
                        else
                            opCodeValue = int.Parse(resultSet[i, 3].ToString());
                    }

                    VariableRecord record;
                    if (insertOpCode)
                        record = new VariableRecord(GetTimestamp(resultSet[i, 0]),
                                                    resultSet[i, 1].ToString(),
                                                    null,
                                                    resultSet[i, 2].ToString(),
                                                    opCodeValue);
                    else
                        record = new VariableRecord(GetTimestamp(resultSet[i, 0]),
                                                    resultSet[i, 1].ToString(),
                                                    null,
                                                    resultSet[i, 2].ToString());
                    records.Add(record);
                }
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to query older entries from PushAgent store: {e.Message}.");
                throw;
            }

            return records.Cast<Record>().ToList();
        }

        public long RecordsCount()
        {
            object[,] resultSet;
            long result = 0;

            try
            {
                string query = $"SELECT COUNT(*) FROM \"{tableName}\"";

                store.Query(query, out _, out resultSet);
                result = ((long)resultSet[0, 0]);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to query count: {e.Message}.");
                throw;
            }

            return result;
        }

        private ulong GetMaxId()
        {
            object[,] resultSet;

            try
            {
                string query = $"SELECT MAX(\"ID\") FROM \"{tableName}\"";

                store.Query(query, out _, out resultSet);

                if (resultSet[0, 0] != null)
                    return GetNextInternalId(UInt64.Parse(resultSet[0, 0].ToString()));
                else
                    return 0;
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Failed to query maxid: {e.Message}.");
                throw;
            }
        }

        private UInt64 GetNextInternalId(UInt64 currentId)
        {
            return currentId < Int64.MaxValue ? currentId + 1 : 0;
        }

        private void CreateTable()
        {
            try
            {
                store.AddTable(tableName);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Unable to create PushAgent store: {e.Message}.");
                throw;
            }
        }

        private Table GetTable()
        {
            return store.Tables.FirstOrDefault(t => t.BrowseName == tableName);
        }

        private void CreateColumns()
        {
            try
            {
                table.AddColumn("Id", OpcUa.DataTypes.UInt64);
                table.AddColumn("Timestamp", OpcUa.DataTypes.DateTime);
                table.AddColumn("VariableId", OpcUa.DataTypes.String);
                table.AddColumn("Value", OpcUa.DataTypes.String);

                if (insertOpCode)
                    table.AddColumn("OpCode", OpcUa.DataTypes.Int32);
            }
            catch (Exception e)
            {
                Log.Error("PushAgentStoreRowPerVariableWrapper", $"Unable to create columns of PushAgent store: {e.Message}.");
                throw;
            }
        }

        private void CreateColumnIndex(string columnName, bool unique)
        {
            string uniqueKeyWord = string.Empty;
            if (unique)
                uniqueKeyWord = "UNIQUE";
            try
            {
                string query = $"CREATE {uniqueKeyWord} INDEX \"{columnName}_index\" ON  \"{tableName}\"(\"{columnName}\")";
                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Warning("PushAgentStoreRowPerVariableWrapper", $"Unable to create index on PushAgent store: {e.Message}.");
            }
        }

        private string[] GetTableColumnNames()
        {
            if (table == null)
                return null;

            var result = new List<string>();
            foreach (var column in table.Columns)
                result.Add(column.BrowseName);

            return result.ToArray();
        }

        private string GetQueryColumns()
        {
            string columns = "\"Timestamp\", ";
            columns += "\"VariableId\", ";
            columns += "\"Value\"";

            if (insertOpCode)
                columns += ", OpCode";

            return columns;
        }

        private DateTime GetTimestamp(object value)
        {
            if (Type.GetTypeCode(value.GetType()) == TypeCode.DateTime)
                return ((DateTime)value);
            else
                return DateTime.SpecifyKind(DateTime.Parse(value.ToString()), DateTimeKind.Utc);
        }

        private readonly SQLiteStore store;
        private readonly string tableName;
        private readonly Table table;
        private readonly string[] columns;
        private readonly bool insertOpCode;
        private UInt64 idCount;
    }

    public class DataLoggerStatusStoreWrapper
    {
        public DataLoggerStatusStoreWrapper(Store store,
                                            string tableName,
                                            List<VariableToLog> variablesToLogList,
                                            bool insertOpCode,
                                            bool insertVariableTimestamp)
        {
            this.store = store;
            this.tableName = tableName;
            this.variablesToLogList = variablesToLogList;
            this.insertOpCode = insertOpCode;
            this.insertVariableTimestamp = insertVariableTimestamp;

            try
            {
                CreateTable();
                table = GetTable();
                CreateColumns();
                columns = GetTableColumnsOrderedByVariableName();
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Unable to initialize internal DataLoggerStatusStoreWrapper {e.Message}.");
                throw;
            }
        }

        public void UpdateRecord(UInt64 rowId)
        {
            if (RecordsCount() == 0)
            {
                InsertRecord(rowId);
                return;
            }

            try
            {
                string query = $"UPDATE \"{tableName}\" SET \"RowId\" = {rowId} WHERE \"Id\"= 1";
                Log.Verbose1("DataLoggerStatusStoreWrapper", $"Update data logger status row id to {rowId}.");

                store.Query(query, out _, out _);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Failed to update internal data logger status: {e.Message}.");
                throw;
            }
        }

        public void InsertRecord(UInt64 rowId)
        {
            var values = new object[1, columns.Length];

            values[0, 0] = 1;
            values[0, 1] = rowId;

            try
            {
                Log.Verbose1("DataLoggerStatusStoreWrapper", $"Set data logger status row id to {rowId}.");
                table.Insert(columns, values);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Failed to update internal data logger status: {e.Message}.");
                throw;
            }
        }

        public UInt64? QueryStatus()
        {
            object[,] resultSet;
            string[] header;

            try
            {
                string query = $"SELECT \"RowId\" FROM \"{tableName}\"";

                store.Query(query, out header, out resultSet);

                if (resultSet[0, 0] != null)
                {
                    Log.Verbose1("DataLoggerStatusStoreWrapper", $"Query data logger status returns {resultSet[0, 0]}.");
                    return UInt64.Parse(resultSet[0, 0].ToString());
                }
                return null;
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Failed to query internal data logger status: {e.Message}.");
                throw;
            }
        }

        public long RecordsCount()
        {
            object[,] resultSet;
            long result = 0;

            try
            {
                string query = $"SELECT COUNT(*) FROM \"{tableName}\"";

                store.Query(query, out _, out resultSet);
                result = ((long)resultSet[0, 0]);
                Log.Verbose1("DataLoggerStatusStoreWrapper", $"Get data logger status records count returns {result}.");
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Failed to query count: {e.Message}.");
                throw;
            }

            return result;
        }

        private void CreateTable()
        {
            try
            {
                store.AddTable(tableName);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Unable to create internal table to DataLoggerStatusStore: {e.Message}.");
                throw;
            }
        }

        private Table GetTable()
        {
            return store.Tables.FirstOrDefault(t => t.BrowseName == tableName);
        }

        private void CreateColumns()
        {
            try
            {
                table.AddColumn("Id", OpcUa.DataTypes.Int32);

                // We need to store only the last query's last row's id to retrieve the dataLogger row
                table.AddColumn("RowId", OpcUa.DataTypes.Int64);
            }
            catch (Exception e)
            {
                Log.Error("DataLoggerStatusStoreWrapper", $"Unable to create columns of internal DataLoggerStatusStore: {e.Message}.");
                throw;
            }
        }

        private string[] GetTableColumnsOrderedByVariableName()
        {
            List<string> columnNames = new List<string>();
            columnNames.Add("Id");
            columnNames.Add("RowId");

            return columnNames.ToArray();
        }

        private readonly Store store;
        private readonly Table table;
        private readonly string tableName;
        private readonly List<VariableToLog> variablesToLogList;
        private readonly string[] columns;
        private readonly bool insertOpCode;
        private readonly bool insertVariableTimestamp;
    }

    public class DataLoggerRecordPuller
    {
        public DataLoggerRecordPuller(IUAObject logicObject,
                                      NodeId dataLoggerNodeId,
                                      SupportStore pushAgentStore,
                                      DataLoggerStatusStoreWrapper statusStoreWrapper,
                                      DataLoggerStoreWrapper dataLoggerStore,
                                      bool preserveDataLoggerHistory,
                                      bool pushByRow,
                                      int pullPeriod,
                                      int numberOfVariablesToLog)
        {
            this.logicObject = logicObject;
            this.pushAgentStore = pushAgentStore;
            this.statusStoreWrapper = statusStoreWrapper;
            this.dataLoggerStore = dataLoggerStore;
            this.dataLoggerNodeId = dataLoggerNodeId;
            this.preserveDataLoggerHistory = preserveDataLoggerHistory;
            this.pushByRow = pushByRow;
            this.numberOfVariablesToLog = numberOfVariablesToLog;

            if (this.preserveDataLoggerHistory)
            {
                UInt64? dataLoggerMaxId = this.dataLoggerStore.GetDataLoggerMaxId();

                if (statusStoreWrapper.RecordsCount() == 1)
                    lastPulledRecordId = statusStoreWrapper.QueryStatus();

                // Check if DataLogger has elements or if the maximum id is greater than lastPulledRecordId
                if (dataLoggerMaxId == null || (dataLoggerMaxId.HasValue && dataLoggerMaxId < lastPulledRecordId))
                    lastPulledRecordId = Int64.MaxValue;  // We have no elements in DataLogger so we will restart the count from 0
            }

            lastInsertedValues = new Dictionary<string, UAValue>();

            dataLoggerPullTask = new PeriodicTask(PullDataLoggerRecords, pullPeriod, this.logicObject);
            dataLoggerPullTask.Start();
        }

        public DataLoggerRecordPuller(IUAObject logicObject,
                                      NodeId dataLoggerNodeId,
                                      SupportStore pushAgentStore,
                                      DataLoggerStoreWrapper dataLoggerStore,
                                      bool preserveDataLoggerHistory,
                                      bool pushByRow,
                                      int pullPeriod,
                                      int numberOfVariablesToLog)
        {
            this.logicObject = logicObject;
            this.pushAgentStore = pushAgentStore;
            this.dataLoggerStore = dataLoggerStore;
            this.dataLoggerNodeId = dataLoggerNodeId;
            this.preserveDataLoggerHistory = preserveDataLoggerHistory;
            this.pushByRow = pushByRow;
            this.numberOfVariablesToLog = numberOfVariablesToLog;

            lastInsertedValues = new Dictionary<string, UAValue>();

            dataLoggerPullTask = new PeriodicTask(PullDataLoggerRecords, pullPeriod, this.logicObject);
            dataLoggerPullTask.Start();
        }

        public void StopPullTask()
        {
            dataLoggerPullTask.Cancel();
        }

        private void PullDataLoggerRecords()
        {
            try
            {
                dataLoggerPulledRecords = null;
                if (!preserveDataLoggerHistory || lastPulledRecordId == null)
                    dataLoggerPulledRecords = dataLoggerStore.QueryNewEntries();
                else
                    dataLoggerPulledRecords = dataLoggerStore.QueryNewEntriesUsingLastQueryId(lastPulledRecordId.Value);

                if (dataLoggerPulledRecords.Count > 0)
                {
                    Log.Info("Fiix Gateway","Pulling " + dataLoggerPulledRecords.Count + " MeterReading records from DataLogger to AgentStore, lastPulledRecordId: " + (lastPulledRecordId == null ? "null" : lastPulledRecordId.Value));
                    InsertDataLoggerRecordsIntoPushAgentStore();

                    if (!preserveDataLoggerHistory)
                        dataLoggerStore.DeletePulledRecords();
                    else
                    {
                        lastPulledRecordId = dataLoggerStore.GetMaxIdFromTemporaryTable();

                        statusStoreWrapper.UpdateRecord(lastPulledRecordId.Value);
                    }

                    dataLoggerPulledRecords.Clear();
                }
            }
            catch (Exception e)
            {
                if (dataLoggerStore.GetStoreStatus() != StoreStatus.Offline)
                {
                    Log.Error("DataLoggerRecordPuller", $"Unable to retrieve data from DataLogger store: {e.Message}.");
                    StopPullTask();
                }
            }
        }

        private void InsertDataLoggerRecordsIntoPushAgentStore()
        {
            if (!IsStoreSpaceAvailable())
            {
                Log.Warning("InsertDataLoggerRecordsIntoPushAgentStore, no store space available.");
                return;
            }

            if (pushByRow)
                InsertRowsIntoPushAgentStore();
            else
                InsertVariableRecordsIntoPushAgentStore();
        }

        private VariableRecord CreateVariableRecord(VariableRecord variable, DateTime recordTimestamp)
        {
            VariableRecord variableRecord;
            if (variable.timestamp == null)
                variableRecord = new VariableRecord(recordTimestamp,
                                                    variable.variableId,
                                                    variable.value,
                                                    variable.serializedValue,
                                                    variable.variableOpCode);
            else
                variableRecord = new VariableRecord(variable.timestamp,
                                                    variable.variableId,
                                                    variable.value,
                                                    variable.serializedValue,
                                                    variable.variableOpCode);



            return variableRecord;
        }

        private void InsertRowsIntoPushAgentStore()
        {
            int numberOfStorableRecords = CalculateNumberOfElementsToInsert();

            if (dataLoggerPulledRecords.Count > 0)
                pushAgentStore.InsertRecords(dataLoggerPulledRecords.Cast<Record>().ToList().GetRange(0, numberOfStorableRecords));
        }

        private void InsertVariableRecordsIntoPushAgentStore()
        {
            int numberOfStorableRecords = CalculateNumberOfElementsToInsert();

            // Temporary dictionary is used to update values, once the records are inserted then the content is copied to lastInsertedValues
            Dictionary<string, UAValue> tempLastInsertedValues = lastInsertedValues.Keys.ToDictionary(_ => _, _ => lastInsertedValues[_]);
            List<VariableRecord> pushAgentRecords = new List<VariableRecord>();
            foreach (var record in dataLoggerPulledRecords.GetRange(0, numberOfStorableRecords))
            {
                foreach (var variable in record.variables)
                {
                    VariableRecord variableRecord = CreateVariableRecord(variable, record.timestamp.Value);
                    if (GetSamplingMode() == SamplingMode.VariableChange)
                    {
                        if (!tempLastInsertedValues.ContainsKey(variable.variableId))
                        {
                            if (variableRecord.serializedValue != null)
                            {
                                pushAgentRecords.Add(variableRecord);
                                tempLastInsertedValues.Add(variableRecord.variableId, variableRecord.value);
                            }
                        }
                        else
                        {
                            if (variable.value != tempLastInsertedValues[variable.variableId] && variableRecord.serializedValue != null)
                            {
                                pushAgentRecords.Add(variableRecord);
                                tempLastInsertedValues[variableRecord.variableId] = variableRecord.value;
                            }
                        }
                    }
                    else
                    {
                        if (variableRecord.serializedValue != null)
                            pushAgentRecords.Add(variableRecord);
                    }
                }
            }

            if (pushAgentRecords.Count > 0)
            {
                pushAgentStore.InsertRecords(pushAgentRecords.Cast<Record>().ToList());

                if (GetSamplingMode() == SamplingMode.VariableChange)
                    lastInsertedValues = tempLastInsertedValues.Keys.ToDictionary(_ => _, _ => tempLastInsertedValues[_]);
            }
        }

        private int GetMaximumStoreCapacity()
        {
            return logicObject.GetVariable("Cfg_MaximumStoreCapacity").Value;
        }

        private SamplingMode GetSamplingMode()
        {
            var dataLogger = InformationModel.Get<DataLogger>(dataLoggerNodeId);
            return dataLogger.SamplingMode;
        }

        private int CalculateNumberOfElementsToInsert()
        {
            // Calculate the number of records that can be effectively stored
            int numberOfStorableRecords;

            if (pushByRow)
                numberOfStorableRecords = (GetMaximumStoreCapacity() - (int)pushAgentStore.RecordsCount());
            else
            {
                if (GetSamplingMode() == SamplingMode.VariableChange)
                    numberOfStorableRecords = (GetMaximumStoreCapacity() - (int)pushAgentStore.RecordsCount());
                else
                    numberOfStorableRecords = (int)Math.Floor((double)(GetMaximumStoreCapacity() - (int)pushAgentStore.RecordsCount()) / numberOfVariablesToLog);
            }

            if (numberOfStorableRecords > dataLoggerPulledRecords.Count)
                numberOfStorableRecords = dataLoggerPulledRecords.Count;

            return numberOfStorableRecords;
        }

        private bool IsStoreSpaceAvailable()
        {
            if (pushAgentStore.RecordsCount() >= GetMaximumStoreCapacity() - 1)
            {
                Log.Warning("DataLoggerRecordPuller", "Maximum store capacity reached! Skipping...");
                return false;
            }

            var percentageStoreCapacity = ((double)pushAgentStore.RecordsCount() / GetMaximumStoreCapacity()) * 100;
            if (percentageStoreCapacity >= 70)
                Log.Warning("DataLoggerRecordPuller", "Store capacity 70% reached!");

            return true;
        }

        private List<DataLoggerRecord> dataLoggerPulledRecords;
        private UInt64? lastPulledRecordId;
        private readonly PeriodicTask dataLoggerPullTask;
        private readonly SupportStore pushAgentStore;
        private readonly DataLoggerStatusStoreWrapper statusStoreWrapper;
        private readonly DataLoggerStoreWrapper dataLoggerStore;
        private readonly bool preserveDataLoggerHistory;
        private readonly bool pushByRow;
        private readonly IUAObject logicObject;
        private readonly int numberOfVariablesToLog;
        private readonly NodeId dataLoggerNodeId;
        private Dictionary<string, UAValue> lastInsertedValues;
    }

    public class JSONBuilder
    {
        public JSONBuilder(bool insertOpCode, bool insertVariableTimestamp, bool logLocalTime)
        {
            this.insertOpCode = insertOpCode;
            this.insertVariableTimestamp = insertVariableTimestamp;
            this.logLocalTime = logLocalTime;
        }

        public string CreatePacketFormatJSON(DataLoggerRowPacket packet)
        {
            var sb = new StringBuilder();
            var sw = new StringWriter(sb);
            using (var writer = new JsonTextWriter(sw))
            {

                writer.Formatting = Formatting.None;

                writer.WriteStartObject();
                writer.WritePropertyName("Timestamp");
                writer.WriteValue(packet.timestamp);
                writer.WritePropertyName("ClientId");
                writer.WriteValue(packet.clientId);
                writer.WritePropertyName("Rows");
                writer.WriteStartArray();
                foreach (var record in packet.records)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("RowTimestamp");
                    writer.WriteValue(record.timestamp);

                    if (logLocalTime)
                    {
                        writer.WritePropertyName("RowLocalTimestamp");
                        writer.WriteValue(record.localTimestamp);
                    }

                    writer.WritePropertyName("Variables");
                    writer.WriteStartArray();
                    foreach (var variable in record.variables)
                    {
                        writer.WriteStartObject();

                        writer.WritePropertyName("VariableName");
                        writer.WriteValue(variable.variableId);
                        writer.WritePropertyName("Value");
                        writer.WriteValue(variable.value?.Value);

                        if (insertVariableTimestamp)
                        {
                            writer.WritePropertyName("VariableTimestamp");
                            writer.WriteValue(variable.timestamp);
                        }

                        if (insertOpCode)
                        {
                            writer.WritePropertyName("VariableOpCode");
                            writer.WriteValue(variable.variableOpCode);
                        }

                        writer.WriteEndObject();
                    }
                    writer.WriteEnd();
                    writer.WriteEndObject();
                }
                writer.WriteEnd();
                writer.WriteEndObject();
            }

            return sb.ToString();
        }

        public string CreatePacketFormatJSON(VariablePacket packet)
        {
            var sb = new StringBuilder();
            var sw = new StringWriter(sb);
            using (var writer = new JsonTextWriter(sw))
            {
                writer.Formatting = Formatting.None;

                writer.WriteStartObject();
                writer.WritePropertyName("Timestamp");
                writer.WriteValue(packet.timestamp);
                writer.WritePropertyName("ClientId");
                writer.WriteValue(packet.clientId);
                writer.WritePropertyName("Records");
                writer.WriteStartArray();
                foreach (var record in packet.records)
                {
                    writer.WriteStartObject();

                    writer.WritePropertyName("VariableName");
                    writer.WriteValue(record.variableId);
                    writer.WritePropertyName("SerializedValue");
                    writer.WriteValue(record.serializedValue);
                    writer.WritePropertyName("VariableTimestamp");
                    writer.WriteValue(record.timestamp);

                    if (insertOpCode)
                    {
                        writer.WritePropertyName("VariableOpCode");
                        writer.WriteValue(record.variableOpCode);
                    }

                    writer.WriteEndObject();
                }
                writer.WriteEnd();
                writer.WriteEndObject();
            }

            return sb.ToString();
        }

        private readonly bool insertOpCode;
        private readonly bool insertVariableTimestamp;
        private readonly bool logLocalTime;
    }

    public class RestAPIService
    {
        private readonly HttpClient httpClient;
        public RestAPIService(RESTConfigurationParameters config)
        {
            httpClient = new HttpClient
            {
                BaseAddress = new Uri(config.BaseAddress)
            };
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            // Set Authentication header
            httpClient.DefaultRequestHeaders.Add("X-Plex-Connect-Api-Key", config.APIKey); ;
        }
        public async Task SendData(string work_center)
        {
            var currentTimestamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
            var tagNamespace = "";
            var boolTagValue = "";
            var apiUrl = "";
            var jsonPayload = ("{\"si\": \"" + work_center + "\",\"st\": " + currentTimestamp + ", \"f\": [{\"id\": \"" + tagNamespace + "\",\"q\": true,\"v\": " + boolTagValue + ",\"t\": " + currentTimestamp + "}]}");
            var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(apiUrl, content);
            response.EnsureSuccessStatusCode();

            var jsonString = await response.Content.ReadAsStringAsync();
            JObject jObject = JObject.Parse(jsonString);
        }

        public class RESTConfigurationParameters
        {
            public string BaseAddress;
            public string APIKey;
        }
    }
}


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
using HttpAPIGateway;
using FTOptix.OPCUAClient;
using System.Linq;
using FTOptix.WebUI;
using FTOptix.EventLogger;
using System.Collections.Generic;
#endregion
/*
Fiix Gateway runtime script to provide actions on Asset.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
[CustomBehavior]
public class AssetBehavior : BaseNetBehavior
{
    public override void Start()
    {
        // Insert code to be executed when the user-defined behavior is started
    }

    public override void Stop()
    {
        // Insert code to be executed when the user-defined behavior is stopped
    }

    [ExportMethod]
    public void SwitchOnline()
    {
        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();
        var response = fiixHttpClient.ChangeAssetOnlineStatus(this.Node.id, 1);
        if (response.Result != null && response.Result.count > 0) this.Node.Sts_LastActionResult = "Switch Asset " + Node.strName + " Online succeeded.";
        else
        {
            string errMessage = "";
            if (response.Result != null && response.Result.error != null) errMessage += response.Result.error.message;
            this.Node.Sts_LastActionResult = "Switch Asset " + Node.strName + " Online failed. " + errMessage;
            Log.Error("Switch Asset" + Node.strName + " online error: " + errMessage );
        }
    }

    [ExportMethod]
    public void SwitchOffline()
    {
        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();
        var response = fiixHttpClient.ChangeAssetOnlineStatus(this.Node.id, 0);
        if (response.Result != null && response.Result.count > 0)
        {
            this.Node.Sts_LastActionResult = "Switch Asset " + Node.strName + " Offline succeeded.";
        }
        else
        {
            string errMessage = "";
            if (response.Result != null && response.Result.error != null) errMessage += response.Result.error.message;
            this.Node.Sts_LastActionResult = "Switch Asset " + Node.strName + " Offline failed. " + errMessage;
            Log.Error("Switch Asset" + Node.strName + " offline error: " + errMessage);
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
    }

    // Get latest properties values from Fiix for this Asset
    [ExportMethod]
    public void UpdateRuntimeAsset() {
        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();
        int assetID = (int)this.Node.GetVariable("id").Value;
        Fiix_Asset asset = fiixHttpClient.FindAssetByID(assetID).Result;
        if (asset != null)
        {
            this.Node.GetVariable("id").Value = asset.id;
            this.Node.GetVariable("strName").Value = asset.strName;
            this.Node.GetVariable("strCode").Value = asset.strCode;
            this.Node.GetVariable("strAddressParsed").Value = asset.strAddressParsed;
            this.Node.GetVariable("strTimezone").Value = asset.strTimezone;
            this.Node.GetVariable("intAssetLocationID").Value = asset.intAssetLocationID;
            this.Node.GetVariable("intCategoryID").Value = asset.intCategoryID;
            this.Node.GetVariable("intSiteID").Value = asset.intSiteID;
            this.Node.GetVariable("intSuperCategorySysCode").Value = asset.intSuperCategorySysCode;
            this.Node.GetVariable("strBinNumber").Value = asset.strBinNumber;
            this.Node.GetVariable("strRow").Value = asset.strRow;
            this.Node.GetVariable("strAisle").Value = asset.strAisle;
            this.Node.GetVariable("strDescription").Value = asset.strDescription;
            this.Node.GetVariable("strInventoryCode").Value = asset.strInventoryCode;
            this.Node.GetVariable("strMake").Value = asset.strMake;
            this.Node.GetVariable("strModel").Value = asset.strModel;
            this.Node.GetVariable("strSerialNumber").Value = asset.strSerialNumber;
            this.Node.GetVariable("bolIsOnline").Value = Convert.ToBoolean(asset.bolIsOnline);
            this.Node.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(asset.intUpdated).DateTime;
            this.Node.Sts_LastActionResult = "Get asset " + Node.strName + " data succeeded.";
        }
        else
        {
            this.Node.Sts_LastActionResult = "Get asset " + Node.strName + " data failed.";
            Log.Error("Update runtime Asset " + Node.strName + " error.");
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
    }

    [ExportMethod]
    public void AddEvent(int eventTypeID = -1, string additionalDescription = "")
    {
        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();
        var response = fiixHttpClient.AddAssetEvent(this.Node.id, eventTypeID, additionalDescription);
        if (response.Result != null && response.Result.count > 0)
        {
            string eventName = "";
            IUANode eventTypeFolder = Project.Current.Find("AssetEventTypes");
            if (eventTypeFolder != null)
            {
                List<IUANode> eventTypes = eventTypeFolder.Children.Cast<IUANode>().ToList();
                IUANode selectedEvent = eventTypes.Find(ev => ev.GetVariable("id").Value == eventTypeID);
                if (selectedEvent != null) { eventName = selectedEvent.GetVariable("strEventName").Value; }
            }
            this.Node.Sts_LastActionResult = "Add Event " + eventName + " on Asset " + Node.strName + " succeeded.";
        }
        else
        {
            string errMessage = "";
            if (response.Result != null && response.Result.error != null) errMessage += response.Result.error.message;
            this.Node.Sts_LastActionResult = "Add Event with TypeID " + eventTypeID + " on Asset " + Node.strName + " failed. " + errMessage;
            Log.Error("Add Event with TypeID " + eventTypeID + "on Asset " + Node.strName + " failed. " + errMessage);
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
    }

    [ExportMethod]
    public void AddMeterReading(string analogVariableName)
    {
        AnalogItem[] variableList = Node.GetNodesByType<AnalogItem>().ToArray();
        this.Node.Sts_LastActionResult = "Added MeterReading on " + Node.BrowseName;
        bool found = false;
        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();
        foreach (AnalogItem item in variableList) 
        {
            if (item.BrowseName == analogVariableName || analogVariableName.Trim() == "")
            {
                found = true;
                
                var response = fiixHttpClient.AddMeterReading(Node.id, item.EngineeringUnits.UnitId, item.Value);
                if (response.Result != null && response.Result.count > 0)
                {
                    this.Node.Sts_LastActionResult += " with " + item.BrowseName + " of value " + item.Value + ";";
                }
                else
                {
                    string errMessage = "";
                    if (response.Result != null && response.Result.error != null) errMessage += response.Result.error.message;
                    this.Node.Sts_LastActionResult = " with " + item.BrowseName + " failed; " + errMessage; 
                    Log.Error("Add MeterReading " + item.BrowseName + " of value " + item.Value + " on " + Node.BrowseName + " failed. " + errMessage);
                }
                if (analogVariableName.Trim() != "") break;
            }
        }
        if (!found) 
        {
            this.Node.Sts_LastActionResult += " failed, provided Variable Name is invalid.";
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
    }

    [ExportMethod]
    public void AddOfflineTracker(int reasonOfflineID = -1, int workOrderID=-1, string additionalInfo = "")
    {
        // Asset Online status is updated before add Offline tracker
        this.SwitchOffline();
        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();
        var response = fiixHttpClient.AddAssetOfflineTracker(this.Node.id, reasonOfflineID, workOrderID, additionalInfo);
        if (response.Result != null && response.Result.count > 0)
        {
            if (reasonOfflineID != -1) this.Node.Sts_LastActionResult += "; Added Offline Tracker with ReasonID " + reasonOfflineID;
            else this.Node.Sts_LastActionResult += "; Added Offline Tracker with no ReasonID.";
            //Log.Info("Add Offline Tracker on Asset " + Node.strName + " succeeded.");
        }
        else
        {
            string errMessage = "";
            if (response.Result != null && response.Result.error != null) errMessage += response.Result.error.message;
            this.Node.Sts_LastActionResult = "Add Offline Tracker with reasonID " + reasonOfflineID + " on Asset " + Node.strName + " failed. " + errMessage;
            Log.Error("Add Offline Tracker with reasonID " + reasonOfflineID + " on Asset " + Node.strName + " failed. " + errMessage);
        }
        this.Node.Sts_LastActionDatetime = DateTime.Now;
        // Update asset' online status
        if (runtimeLogic != null)
        {
            DelayedTask updateStatusTask = new DelayedTask(UpdateRuntimeAsset, 200, (IUANode)runtimeLogic);
            updateStatusTask.Start();
        }
    }

    [ExportMethod]
    public void CloseOfflineTracker(int reasonOnlineID = -1, string additionalInfo = "", double hoursAffected = -1)
    {
        this.SwitchOnline();
        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();
        var response = fiixHttpClient.CloseLastAssetOfflineTracker(this.Node.id, reasonOnlineID, additionalInfo, hoursAffected);

        this.Node.Sts_LastActionResult += response.Result;
        Log.Info("Closing Offline Tracker on Asset " + Node.strName + " with result: " + response.Result);
        this.Node.Sts_LastActionDatetime = DateTime.Now;
        // Update Online status of the asset
        if (runtimeLogic != null)
        {
            DelayedTask updateStatusTask = new DelayedTask(UpdateRuntimeAsset, 200, (IUANode)runtimeLogic);
            updateStatusTask.Start();
        }
    }

    [ExportMethod]
    public Fiix_AssetEvent[] GetAssetEvents(DateTime startDT, DateTime endDT)
    {
        //if (startDT == null) startDT = DateTime.Now.AddHours(-24);
        //if (endDT == null) endDT = DateTime.Now;

        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();
        var response = fiixHttpClient.FindAssetEventByAssetAndTimeRange(this.Node.id, (DateTime)startDT, (DateTime)endDT);
        this.Node.Sts_LastActionDatetime = DateTime.Now;
        if (response.Result != null && response.Result.Length > 0)
        {
            this.Node.Sts_LastActionResult = "Get asset historical events with " + response.Result.Length + " records.";
            return response.Result;
        }
        else
        {
            this.Node.Sts_LastActionResult = "Get asset historical events with no result";
            return null;
        }
    }

    [ExportMethod]
    public Fiix_MeterReading[] GetMeterReadings(DateTime startDT, DateTime endDT)
    {
        //if (startDT == null) startDT = DateTime.Now.AddHours(-24);
        //if (endDT == null) endDT = DateTime.Now;

        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();
        var response = fiixHttpClient.FindMeterReadingByAssetAndTimeRange(this.Node.id, (DateTime)startDT, (DateTime)endDT);
        this.Node.Sts_LastActionDatetime = DateTime.Now;
        if (response.Result != null && response.Result.Length > 0)
        {
            this.Node.Sts_LastActionResult = "Get asset historical meter readings with " + response.Result.Length + " records.";
            return response.Result;
        }
        else
        {
            this.Node.Sts_LastActionResult = "Get asset historical meter readings with no result";
            return null;
        }
    }

    private NetLogicObject runtimeLogic = (NetLogicObject)Project.Current.Find("FiixGatewayRuntimeLogic");

    #region Auto-generated code, do not edit!
    protected new Asset Node => (Asset)base.Node;
#endregion
}

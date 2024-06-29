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
using System.Runtime.CompilerServices;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Net.Http.Json;
using System.Linq;
using System.Net.Http.Headers;
using System.Diagnostics;
using System.ComponentModel;
using FTOptix.WebUI;
using FTOptix.EventLogger;
using System.IO;
#endregion
/*
Fiix Gateway designtime script hosting Fiix objects classes and fetch assets and their support data from Fiix, sync as Optix data model.
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class FiixGatewayDesigntimeLogic : BaseNetLogic
{
    [ExportMethod]
    public void SyncCategoriesTypesUnits()
    {
        IUANode categoryFolder = LogicObject.Owner.Owner.Find("AssetCategories");
        IUANode AssetCategoryType = LogicObject.Owner.Find("AssetCategory");
        IUANode eventTypeFolder = LogicObject.Owner.Owner.Find("AssetEventTypes");
        IUANode AssetEventTypeType = LogicObject.Owner.Find("AssetEventType");
        IUANode assetOfflineReasonFolder = LogicObject.Owner.Owner.Find("AssetOfflineReasons");
        IUANode assetOnlineReasonFolder = LogicObject.Owner.Owner.Find("AssetOnlineReasons");
        IUANode AssetOfflineReasonType = LogicObject.Owner.Find("AssetOfflineReason");
        FiixHttpClient fiixHttpClient = GatewayUtils.GetFiixHttpClient();

        int newCategoryCount = 0, updateCategoryCount = 0, newEventTypeCount = 0, updateEventTypeCount = 0;
        int newOfflineReasonCount = 0, updateOfflineReasonCount = 0, newOnlineReasonCount = 0, updateOnlineReasonCount = 0;

        // Assign model to Asset faceplate meter reading trend model, to avoid runtime Trend source error
        IUANode meterReadingDataStore = Project.Current.Find("MeterReadingDataStore");
        PanelType meterReadingTrendPanel = (PanelType)Project.Current.Find("raI_FiixAsset_1_00_MeterReadingTrend");
        if (meterReadingDataStore != null && meterReadingTrendPanel != null)
        {
            Trend meterReadingTrend = (Trend)meterReadingTrendPanel.Find("Trend1");
            if (meterReadingTrend != null) meterReadingTrend.Model = meterReadingDataStore.NodeId;
        }

     // Sync AssetCategory, exclude System Category
        Fiix_AssetCategory[] fiixAssetCategories = fiixHttpClient.FindAssetCategories().Result;
        LogicObject.GetVariable("Sts_LastExecutionDatetime").Value = DateTime.Now;
        if (fiixAssetCategories == null || fiixAssetCategories.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value = "Get Fiix AssetCategories with no result.";
            goto SyncAssetEventType;
        }
        List<IUANode> assetCategories = categoryFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode assetCategory in assetCategories)
            {
                if (!Array.Exists(fiixAssetCategories, fiixCategory => fiixCategory.id == (int)assetCategory.GetVariable("id").Value || fiixCategory.strName == (string)assetCategory.GetVariable("strName").Value))
                {
                    assetCategory.Delete();
                }
            }
        }

        foreach (var fiixAssetCategory in fiixAssetCategories)
        {
            IUANode newCategory;

            // Include SysCode that equal -1 only which is the default value when no value return from API (in the case of System Categories)
            // Updated to include System Categories by comment out the filtering below.
            //if (fiixAssetCategory.intSysCode != -1) continue; 
            if (!assetCategories.Exists(category => fiixAssetCategory.id == (int)category.GetVariable("id").Value))   
            {
                newCategory = InformationModel.MakeObject(fiixAssetCategory.strName, AssetCategoryType.NodeId);
                newCategory.GetVariable("id").Value = fiixAssetCategory.id;
                newCategory.GetVariable("strName").Value = fiixAssetCategory.strName;
                newCategory.GetVariable("strUuid").Value = fiixAssetCategory.strUuid;
                newCategory.GetVariable("intSysCode").Value = fiixAssetCategory.intSysCode;
                newCategory.GetVariable("intParentID").Value = fiixAssetCategory.intParentID;
                newCategory.GetVariable("Cfg_enabled").Value = false;

                newCategoryCount++;
                categoryFolder.Add(newCategory);

                // Sort by name
                var updateds = categoryFolder.Children.Cast<IUANode>().ToList();
                var cpCount = assetCategories.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newCategory.BrowseName) > 0 ) newCategory.MoveUp();
                    }
                    catch { Log.Info("error when sort AssetCategories"); }
                }
            }
            else
            {
                newCategory = assetCategories.Find(site => fiixAssetCategory.id == site.GetVariable("id").Value);
                newCategory.BrowseName = fiixAssetCategory.strName;
                newCategory.GetVariable("strName").Value = fiixAssetCategory.strName;
                newCategory.GetVariable("strUuid").Value = fiixAssetCategory.strUuid;
                newCategory.GetVariable("intSysCode").Value = fiixAssetCategory.intSysCode;
                newCategory.GetVariable("intParentID").Value = fiixAssetCategory.intParentID;
                updateCategoryCount++;
            }
        }
        LogicObject.GetVariable("Sts_LastExecutionResult").Value = newCategoryCount + " new and " + updateCategoryCount + " synced AssetCategory; ";

     // Sync AssetEventType
        SyncAssetEventType:
        Fiix_AssetEventType[] fiixAssetEventTypes = fiixHttpClient.FindAssetEventTypes().Result;
        if (fiixAssetEventTypes == null || fiixAssetEventTypes.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix AssetEventTypes with no result.";
            goto SyncMeterReadingUnit;
        }
        List<IUANode> assetEventTypes = eventTypeFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode assetEventType in assetEventTypes)
            {
                if (!Array.Exists(fiixAssetEventTypes, fiixEventType => fiixEventType.id == (int)assetEventType.GetVariable("id").Value || fiixEventType.strEventCode == (string)assetEventType.GetVariable("strEventCode").Value))
                {
                    assetEventType.Delete();
                }
            }
        }

        foreach (var fiixAssetEventType in fiixAssetEventTypes)
        {
            IUANode newEventType;

            if (!assetEventTypes.Exists(eventType => fiixAssetEventType.id == (int)eventType.GetVariable("id").Value))
            {
                newEventType = InformationModel.MakeObject(fiixAssetEventType.strEventCode + " - " + fiixAssetEventType.strEventName, AssetEventTypeType.NodeId);
                newEventType.GetVariable("id").Value = fiixAssetEventType.id;
                newEventType.GetVariable("strEventName").Value = fiixAssetEventType.strEventName;
                newEventType.GetVariable("strUniqueKey").Value = fiixAssetEventType.strUniqueKey;
                newEventType.GetVariable("strEventCode").Value = fiixAssetEventType.strEventCode;
                newEventType.GetVariable("strEventDescription").Value = fiixAssetEventType.strEventDescription;

                newEventTypeCount++;
                eventTypeFolder.Add(newEventType);

                // Sort by name
                var updateds = eventTypeFolder.Children.Cast<IUANode>().ToList();
                var cpCount = assetEventTypes.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newEventType.BrowseName) > 0) newEventType.MoveUp();
                    }
                    catch { Log.Info("error when sort AssetEventTypes"); }
                }
            }
            else
            {
                newEventType = assetEventTypes.Find(site => fiixAssetEventType.id == site.GetVariable("id").Value);
                newEventType.BrowseName = fiixAssetEventType.strEventCode + " - " + fiixAssetEventType.strEventName;
                newEventType.GetVariable("strEventName").Value = fiixAssetEventType.strEventName;
                newEventType.GetVariable("strUniqueKey").Value = fiixAssetEventType.strUniqueKey;
                newEventType.GetVariable("strEventCode").Value = fiixAssetEventType.strEventCode;
                newEventType.GetVariable("strEventDescription").Value = fiixAssetEventType.strEventDescription;
                updateEventTypeCount++;
            }
        }

        LogicObject.GetVariable("Sts_LastExecutionResult").Value += newEventTypeCount + " new and " + updateEventTypeCount + " synced EventType; ";

     // Sync MeterReadingUnit
        SyncMeterReadingUnit:
        Fiix_MeterReadingUnit[] fiixMeterReadingUnits = fiixHttpClient.FindMeterReadingUnits().Result;
        if (fiixMeterReadingUnits == null || fiixMeterReadingUnits.Length==0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix MeterReadingUnits with no result.";
            goto SyncOfflineReason;
        }
        var arrayDimensions = new uint[1];
        arrayDimensions[0] = (uint)fiixMeterReadingUnits.Length;

        IUAVariable meterReadingEngineeringUnitDictionary;
        if (LogicObject.Owner.Owner.Find("MeterReadingUnits") != null)
        {
            meterReadingEngineeringUnitDictionary = (IUAVariable)LogicObject.Owner.Owner.Find("MeterReadingUnits");
        }
        else
        {
            meterReadingEngineeringUnitDictionary = InformationModel.MakeVariable("MeterReadingUnits", FTOptix.Core.DataTypes.EngineeringUnitDictionaryItem, FTOptix.Core.VariableTypes.EngineeringUnitDictionary, arrayDimensions);
            LogicObject.Owner.Owner.Add(meterReadingEngineeringUnitDictionary);
        }

        EngineeringUnitDictionaryItem[] newItems = new EngineeringUnitDictionaryItem[fiixMeterReadingUnits.Length];

        for (int i = 0; i < fiixMeterReadingUnits.Length; i++)
        {
            EngineeringUnitDictionaryItem newItem = new EngineeringUnitDictionaryItem();
            newItem.PhysicalDimension = PhysicalDimension.None;
            newItem.Slope = 0;
            newItem.Description = new LocalizedText("Fiix_" + fiixMeterReadingUnits[i].strName, Session.ActualLocaleId);
            newItem.UnitId = fiixMeterReadingUnits[i].id;
            newItem.Intercept = 0;
            newItem.DisplayName = new LocalizedText(fiixMeterReadingUnits[i].strSymbol, Session.ActualLocaleId);
            //newItem.DisplayName = new LocalizedText(fiixMeterReadingUnits[i].strSymbol, "en-US");
            newItems[i] = newItem;
        }  
        meterReadingEngineeringUnitDictionary.Value = newItems;

        // Sync AssetOfflineReason and AssetOnlineReason
        SyncOfflineReason:
        Fiix_ReasonToSetAssetOffline[] fiix_AssetOfflineReasons = fiixHttpClient.FindReasonToSetAssetOffline().Result;
        if (fiix_AssetOfflineReasons == null || fiix_AssetOfflineReasons.Length == 0)
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix OfflineReason with no result.";
            goto SyncOnlineReason;
        }
        List<IUANode> assetOfflineReasons = assetOfflineReasonFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode assetOfflineReason in assetOfflineReasons)
            {
                if (!Array.Exists(fiix_AssetOfflineReasons, fiixOfflineReason => fiixOfflineReason.id == (int)assetOfflineReason.GetVariable("id").Value))
                {
                    assetOfflineReason.Delete();
                }
            }
        }

        foreach (var fiixAssetOfflineReason in fiix_AssetOfflineReasons)
        {
            IUANode newOfflineReason;

            if (!assetOfflineReasons.Exists(OfflineReason => fiixAssetOfflineReason.id == (int)OfflineReason.GetVariable("id").Value))
            {
                newOfflineReason = InformationModel.MakeObject(fiixAssetOfflineReason.strName, AssetOfflineReasonType.NodeId);
                newOfflineReason.GetVariable("id").Value = fiixAssetOfflineReason.id;
                newOfflineReason.GetVariable("strName").Value = fiixAssetOfflineReason.strName;
                newOfflineReason.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixAssetOfflineReason.intUpdated).DateTime; 
                newOfflineReason.GetVariable("strUuid").Value = fiixAssetOfflineReason.strUuid;

                newOfflineReasonCount++;
                assetOfflineReasonFolder.Add(newOfflineReason);

                // Sort by name
                var updateds = assetOfflineReasonFolder.Children.Cast<IUANode>().ToList();
                var cpCount = assetOfflineReasons.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newOfflineReason.BrowseName) > 0) newOfflineReason.MoveUp();
                    }
                    catch { Log.Info("error when sort AssetOfflineReasons"); }
                }
            }
            else
            {
                newOfflineReason = assetOfflineReasons.Find(site => fiixAssetOfflineReason.id == site.GetVariable("id").Value);
                newOfflineReason.BrowseName = fiixAssetOfflineReason.strName;
                newOfflineReason.GetVariable("strName").Value = fiixAssetOfflineReason.strName;
                newOfflineReason.GetVariable("id").Value = fiixAssetOfflineReason.id;
                newOfflineReason.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixAssetOfflineReason.intUpdated).DateTime;
                newOfflineReason.GetVariable("strUuid").Value = fiixAssetOfflineReason.strUuid;
                updateOfflineReasonCount++;
            }
        }

        LogicObject.GetVariable("Sts_LastExecutionResult").Value += newOfflineReasonCount + " new and " + updateOfflineReasonCount + " synced OfflineReason; ";

    // Sync Online Reasons
    SyncOnlineReason:
        Fiix_ReasonToSetAssetOffline[] fiix_AssetOnlineReasons = fiixHttpClient.FindReasonToSetAssetOnline().Result;
        if (fiix_AssetOnlineReasons == null || fiix_AssetOnlineReasons.Length == 0  )
        {
            LogicObject.GetVariable("Sts_LastExecutionResult").Value += "Get Fiix OnlineReason with no result.";
            return;
        }
        List<IUANode> assetOnlineReasons = assetOnlineReasonFolder.Children.Cast<IUANode>().ToList();

        // Delete extra nodes if enabled
        if ((bool)LogicObject.GetVariable("Set_DeleteExtraNodes").Value)
        {
            foreach (IUANode assetOnlineReason in assetOnlineReasons)
            {
                if (!Array.Exists(fiix_AssetOnlineReasons, fiixOnlineReason => fiixOnlineReason.id == (int)assetOnlineReason.GetVariable("id").Value))
                {
                    assetOnlineReason.Delete();
                }
            }
        }

        foreach (var fiixAssetOnlineReason in fiix_AssetOnlineReasons)
        {
            IUANode newOnlineReason;

            if (!assetOnlineReasons.Exists(OnlineReason => fiixAssetOnlineReason.id == (int)OnlineReason.GetVariable("id").Value))
            {
                newOnlineReason = InformationModel.MakeObject(fiixAssetOnlineReason.strName, AssetOfflineReasonType.NodeId);
                newOnlineReason.GetVariable("id").Value = fiixAssetOnlineReason.id;
                newOnlineReason.GetVariable("strName").Value = fiixAssetOnlineReason.strName;
                newOnlineReason.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixAssetOnlineReason.intUpdated).DateTime;
                newOnlineReason.GetVariable("strUuid").Value = fiixAssetOnlineReason.strUuid;

                newOnlineReasonCount++;
                assetOnlineReasonFolder.Add(newOnlineReason);

                // Sort by name
                var updateds = assetOnlineReasonFolder.Children.Cast<IUANode>().ToList();
                var cpCount = assetOnlineReasons.Count();
                for (int i = cpCount - 1; i >= 0; i--)
                {
                    try
                    {
                        if (string.Compare(updateds[i].BrowseName, newOnlineReason.BrowseName) > 0) newOnlineReason.MoveUp();
                    }
                    catch { Log.Info("error when sort AssetOnlineReasons"); }
                }
            }
            else
            {
                newOnlineReason = assetOnlineReasons.Find(site => fiixAssetOnlineReason.id == site.GetVariable("id").Value);
                newOnlineReason.BrowseName = fiixAssetOnlineReason.strName;
                newOnlineReason.GetVariable("strName").Value = fiixAssetOnlineReason.strName;
                newOnlineReason.GetVariable("id").Value = fiixAssetOnlineReason.id;
                newOnlineReason.GetVariable("dtUpdated").Value = DateTimeOffset.FromUnixTimeMilliseconds(fiixAssetOnlineReason.intUpdated).DateTime;
                newOnlineReason.GetVariable("strUuid").Value = fiixAssetOnlineReason.strUuid;
                updateOnlineReasonCount++;
            }
        }

        LogicObject.GetVariable("Sts_LastExecutionResult").Value += newOnlineReasonCount + " new and " + updateOnlineReasonCount + " synced OnlineReason; ";

    }

    [ExportMethod]
    public void SyncAssets()
    {
        GatewayUtils.SyncAssetTree(true);
    }

}

public class Fiix_Asset
{
    public int id { get; set; }
    public string strName { get; set; } = "";
    public string strDescription { get; set; } = "";
    public string strMake { get; set; } = "";
    public string strModel { get; set; } = "";
    public string strInventoryCode { get; set; } = "";
    public string strBinNumber { get; set; } = "";
    public string strSerialNumber { get; set; } = "";
    public string strRow { get; set; } = "";
    public string strAisle { get; set; } = "";
    public string strCode { get; set; } = "";
    public string strAddressParsed { get; set; } = "";
    public string strTimezone { get; set; } = "";   
    public int intCategoryID { get; set; }
    public int intSuperCategorySysCode { get; set; }
    public int intSiteID { get; set; }
    public int intAssetLocationID { get; set; }
    public uint bolIsOnline { get; set; }
    public Int64 intUpdated { get; set; }

    public override bool Equals(object obj) => this.Equals(obj as Fiix_Asset);
    public bool Equals(Fiix_Asset obj)
    {
        if (obj == null) return false;
        if (this.GetType() != obj.GetType()) return false;
        return this.id == obj.id;    
    }
    public override int GetHashCode()
    {
        return this.id;
    }
}

    class Fiix_FindResponse_Asset
{
    public Fiix_Asset[] objects { get; set; }
    public uint totalObjects { get; set; }
}

public class Fiix_AssetOfflineTracker
{
    public int id { get; set; }
    public int intReasonOnlineID { get; set; }
    public string strOnlineAdditionalInfo { get; set; } = "";
    public Int64 dtmOffLineTo { get; set; } = 0;
    public int intAssetID { get; set; }
    public Int64 dtmOfflineFrom { get; set; } = 0;
    public int intReasonOfflineID { get; set; } 
    public string strUuid { get; set; } = "";
    public int intSetOnlineByUserID { get; set; }
    public int intSetOfflineByUserID { get; set; }
    public string strOfflineAdditionalInfo { get; set; } = "";
    public int intWorkOrderID { get; set; }
    public Int64 intUpdated { get; set; } 
    public double dblProductionHoursAffected { get; set; }

}

class Fiix_FindResponse_AssetOfflineTracker
{
    public Fiix_AssetOfflineTracker[] objects { get; set; }
    public uint totalObjects { get; set; }
}

public class Fiix_AssetCategory
{
    public int id { get; set; }
    public string strName { get; set; } = "";
    public string strUuid { get; set; } = "";
    public int intSysCode { get; set; } = -1;
    public int intParentID { get; set; }

    public override bool Equals(object obj) => this.Equals(obj as Fiix_AssetCategory);
    public bool Equals(Fiix_AssetCategory obj)
    {
        if (obj == null) return false;
        if (this.GetType() != obj.GetType()) return false;
        return this.id == obj.id;
    }
    public override int GetHashCode()
    {
        return this.id;
    }
}

class Fiix_FindResponse_AssetCategory
{
    public Fiix_AssetCategory[] objects { get; set; }
    public uint totalObjects { get; set; }
    public Fiix_MessageError error { get; set; }

}

public class Fiix_AssetEventType
{
    public int id { get; set; }
    public string strEventName { get; set; } = "";
    public string strUniqueKey { get; set; } = "";
    public string strEventDescription { get; set; } = "";
    public string strEventCode { get; set; } = "";
    public override bool Equals(object obj) => this.Equals(obj as Fiix_AssetEventType);
    public bool Equals(Fiix_AssetEventType obj)
    {
        if (obj == null) return false;
        if (this.GetType() != obj.GetType()) return false;
        return this.id == obj.id;
    }
    public override int GetHashCode()
    {
        return this.id;
    }
}

public class Fiix_ReasonToSetAssetOffline
{
    public int id { get; set; }
    public string strName { get; set; } = "";
    public Int64 intUpdated { get; set; } 
    public string strUuid { get; set; } = "";
    public override bool Equals(object obj) => this.Equals(obj as Fiix_AssetEventType);
    public bool Equals(Fiix_AssetEventType obj)
    {
        if (obj == null) return false;
        if (this.GetType() != obj.GetType()) return false;
        return this.id == obj.id;
    }
    public override int GetHashCode()
    {
        return this.id;
    }
}

class Fiix_FindResponse_AssetEventType
{
    public Fiix_AssetEventType[] objects { get; set; }
    public uint totalObjects { get; set; }
}

class Fiix_FindResponse_ReasonToSetAssetOffline
{
    public Fiix_ReasonToSetAssetOffline[] objects { get; set; }
    public uint totalObjects { get; set; }
}

public class Fiix_MeterReadingUnit
{
    public int id { get; set; }
    public string strName { get; set; } = "";
    public string strSymbol { get; set; } = "";
    public string strUuid { get; set; } = "";
    public string strUniqueKey { get; set; } = "";
    public int intPrecision { get; set; }
    public override bool Equals(object obj) => this.Equals(obj as Fiix_MeterReadingUnit);
    public bool Equals(Fiix_MeterReadingUnit obj)
    {
        if (obj == null) return false;
        if (this.GetType() != obj.GetType()) return false;
        return this.id == obj.id;
    }
    public override int GetHashCode()
    {
        return this.id;
    }
}

class Fiix_FindResponse_MeterReadingUnit
{
    public Fiix_MeterReadingUnit[] objects { get; set; }
    public uint totalObjects { get; set; }
}

public class Fiix_MeterReading
{
    public int id { get; set; }
    public double dblMeterReading { get; set; }
    public Int64 dtmDateSubmitted { get; set; }
    public int intAssetID { get; set; }
    public int intMeterReadingUnitsID { get; set;}
}

public class Fiix_FindResponse_MeterReading
{
    public Fiix_MeterReading[] objects { get; set; }
    public uint totalObjects { get; set; } = 0;
}

public class Fiix_AssetEvent
{
    public int id { get; set; }
    public int intAssetEventTypeID { get; set; }
    public Int64 dtmDateSubmitted { get; set; }
    public int intAssetID { get; set; }
    public int intSubmittedByUserID { get; set; }
    public string strAdditionalDescription { get; set; }
}

public class Fiix_FindResponse_AssetEvent
{
    public Fiix_AssetEvent[] objects { get; set; }
    public uint totalObjects { get; set; } = 0;
}
class Fiix_FindBatchResponse_Asset
{
    public Fiix_FindResponse_Asset[] responses { get; set; }
    public Fiix_MessageError error { get; set; }
}
public class Fiix_RequestResponse
{
    public int count { get; set; }
    public Fiix_ResponseError error { get; set; }
}

public class Fiix_AddBatchResponse_MeterReading
{
    public Fiix_AddResponse_MeterReading[] responses { get; set; }
}

public class Fiix_ResponseError
{
    public int code { get; set; }
    public string message { get; set; } = "";
}

public class Fiix_AddResponse_MeterReading
{
    public Fiix_MeterReading @object { get; set; }
    public Fiix_MessageError error { get; set; }
}

public class Fiix_MessageError
{
    public int code { get; set; }
    public string leg { get; set; } = "";
    public string stackTrace { get; set; } = "";

    public string message { get; set; } = "";
}



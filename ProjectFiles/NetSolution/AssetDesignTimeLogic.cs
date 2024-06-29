#region Using directives
using System;
using UAManagedCore;
using OpcUa = UAManagedCore.OpcUa;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.DataLogger;
using FTOptix.NativeUI;
using FTOptix.UI;
using FTOptix.CoreBase;
using FTOptix.SQLiteStore;
using FTOptix.Store;
using FTOptix.OPCUAServer;
using FTOptix.RAEtherNetIP;
using FTOptix.CommunicationDriver;
using FTOptix.Core;
using System.Linq;
using FTOptix.WebUI;
using HttpAPIGateway;
using FTOptix.EventLogger;
#endregion
/*
Fiix Gateway designtime script to automate putting asset's meter readings to Push Agent's datalogger, following required naming convention. 
=============================================================

Disclaimer of Warranty
THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT ARE PROVIDED "AS IS" WITHOUT WARRANTIES OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION, ALL IMPLIED WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, NON-INFRINGEMENT OR OTHER VIOLATION OF RIGHTS. ROCKWELL AUTOMATION DOES NOT WARRANT OR MAKE ANY REPRESENTATIONS REGARDING THE USE, VALIDITY, ACCURACY, OR RELIABILITY OF, OR THE RESULTS OF ANY USE OF, OR OTHERWISE RESPECTING, THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT OR ANY WEB SITE LINKED TO THIS DOCUMENT 

Limitation of Liability
UNDER NO CIRCUMSTANCE (INCLUDING NEGLIGENCE AND TO THE FULLEST EXTEND PERMITTED BY APPLICABLE LAW) WILL ROCKWELL AUTOMATION BE LIABLE FOR ANY DIRECT, INDIRECT, SPECIAL, INCIDENTAL, PUNITIVE OR CONSEQUENTIAL DAMAGES (INCLUDING WITHOUT LIMITATION, BUSINESS INTERRUPTION, DELAYS, LOSS OF DATA OR PROFIT) ARISING OUT OF THE USE OR THE INABILITY TO USE THE MATERIALS PROVIDED OR REFERENCED BY WAY OF THIS DOCUMENT EVEN IF ROCKWELL AUTOMATION HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGES. IF USE OF SUCH MATERIALS RESULTS IN THE NEED FOR SERVICING, REPAIR OR CORRECTION OF USER EQUIPMENT OR DATA, USER ASSUMES ANY COSTS ASSOCIATED THEREWITH.

Copyright © Rockwell Automation, Inc.  All Rights Reserved. 

=============================================================
*/
public class AssetDesignTimeLogic : BaseNetLogic
{
    [ExportMethod]
    public void AddVariablesToDataLogger()
    {
        // Add all analog variables under the asset to Gateway dedicated DataLogger for Store and Send
        // DataLogger var naming:  [AssetName]_AssetID[AssetID]_EU[EUName]_EUID[EUID]
        DataLogger dataLogger = (DataLogger)Project.Current.Find("MeterReadingDataLogger");
        AnalogItem[] variableList = LogicObject.Owner.GetNodesByType<AnalogItem>().ToArray();

        foreach (AnalogItem item in variableList)
        {
            string varName = LogicObject.Owner.BrowseName + "_AssetID" + LogicObject.Owner.GetVariable("id").Value;
            string euName = GatewayUtils.GetEngineeringUnitNameByID(item.EngineeringUnits.UnitId);
            varName += "_EU" + euName + "_EUID" + item.EngineeringUnits.UnitId;
            bool found = false;
            foreach (VariableToLog var in dataLogger.VariablesToLog)
            {
                if (var.BrowseName == varName)
                {
                    found = true;
                    var.SetDynamicLink(item, DynamicLinkMode.Read);
                    var.DeadBandMode = dataLogger.DefaultDeadBandMode;
                    var.DeadBandValue = dataLogger.DefaultDeadBandValue;
                    break;
                }
            }

            if (!found)
            {
                var newVAR = InformationModel.MakeVariable<VariableToLog>(varName, OpcUa.DataTypes.Float);
                newVAR.SetDynamicLink(item, DynamicLinkMode.Read);
                newVAR.DeadBandMode = dataLogger.DefaultDeadBandMode;
                newVAR.DeadBandValue = dataLogger.DefaultDeadBandValue;
                dataLogger.VariablesToLog.Add(newVAR);
            }

        }
    }
}

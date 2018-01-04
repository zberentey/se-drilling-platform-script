int triggerUnloadAtPercent = 90; 
string pistonGroupName = "Drill Extenders";
string drillGroupName = "Drills";
 
string[] cursor = new string[4] {"/", "--", "\\", "|"}; 
int counter = 0;
bool runOnce = false;

DrillMode? lastMode;
DrillMode mode;
float fillPercent;
float? fillPercentSnapshot;
bool? drillsRunning;
IMyShipConnector connector;
bool depthLimitReached = false;

Dictionary<string, VRage.MyFixedPoint> inventories = new Dictionary<string, VRage.MyFixedPoint>();
 
public Program()
{
    Runtime.UpdateFrequency = UpdateFrequency.Update10;
    
    mode = DrillMode.RETRACTING;

    if ((Storage != null) && (Storage.Length > 0)) {
        string[] array = Storage.Split(';');
        foreach(string param in array) {
                string[] values = param.Split('=');
                
                if (values[0] == "lastMode") {
                        lastMode = (values[1] == "null" ? (DrillMode?)null : (DrillMode?)Enum.Parse(typeof(DrillMode), values[1]));
                }
                else if (values[0] == "mode") {
                        mode = (DrillMode)Enum.Parse(typeof(DrillMode), values[1]);
                }
                else if (values[0] == "depthLimitReached") {
                        depthLimitReached = (values[1] == "true" ? true : false);
                }
        }
    }
}

void Save() {
    StringBuilder sb = new StringBuilder();

     sb.Append("lastMode=").Append(lastMode.HasValue ? lastMode.Value + "" : "null");
     sb.Append(";mode=").Append(mode);
     sb.Append(";depthLimitReached=").Append(depthLimitReached);

    Storage = sb.ToString();
}

public void Main(string argument, UpdateType updateSource)
{
    if (updateSource == UpdateType.Terminal) {
            Echo("TERMINAL");

            switch (argument) {
                case "start":
                    mode = (lastMode.HasValue ? lastMode.Value : DrillMode.RETRACTING);
                    runOnce = false;
                    break; 
                case "stop":
                    mode = DrillMode.IDLE;
                    break;
                case "reset":
                    depthLimitReached = false;
                    mode = DrillMode.RETRACTING;
                    runOnce = true;
                    break;
            }
    }
         
    Echo(cursor[counter]); 
 
    counter = ++counter % 4; 

    fillPercent = CalculateVolumes();
    if (connector == null) { 
        if (!FindConnector()) { 
            return; 
        } 
    } 

    switch (mode) {
        case DrillMode.RETRACTING:
            RetractPistons();
            break;
        case DrillMode.DOCKING:
            Dock();
            break;
        case DrillMode.UNLOADING:
            UnloadCargo();
            break;
        case DrillMode.RUNNING:
            Drill();
            break;
        case DrillMode.IDLE:
            TurnOffDrills();
            StopPistons();
            break;
    }

    if (mode != DrillMode.IDLE) {
        lastMode = mode;
    }

    DisplayStatus();
} 

private float CalculateVolumes()
{ 
    inventories.Clear();
    VRage.MyFixedPoint current = default(VRage.MyFixedPoint);   
    VRage.MyFixedPoint total = default(VRage.MyFixedPoint);    
 
    List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();  
    GridTerminalSystem.GetBlocksOfType<IMyTerminalBlock>(blocks);  
  
    foreach (IMyTerminalBlock block in blocks) {
        if (block.CubeGrid != Me.CubeGrid) {
            continue;
        }
        if ((block is IMyProductionBlock) || (block is IMyReactor) || (block is IMyGasGenerator) || (block is IMyGasTank)) { 
            continue; 
        } 
 
        int count = block.InventoryCount;  
 
        for (int i = 0; i < count; i++) {  
            IMyInventory inventory = block.GetInventory(i);  
              
            if (inventory.MaxVolume == VRage.MyFixedPoint.MaxValue) {  
                // We're in creative mode, let's set the max inventory size to 100 000 L  
  
                total += (VRage.MyFixedPoint)100;  
            }  
            else {  
                total += inventory.MaxVolume;  
            }  
             
            current += inventory.CurrentVolume; 
 
            inventories.Add(block.CustomName, inventory.CurrentVolume * 1000f); 
        }  
    }
    
    return (float)current.RawValue / (float)total.RawValue;
} 

private void DisplayStatus()
{
    if (depthLimitReached) {
            Echo("Maximum drill depth reached!\n");
    }

    Echo("Last Mode: " + (lastMode.HasValue ? "" + lastMode.Value : ""));
    Echo("Mode: " + mode);
    
    if (drillsRunning.HasValue) {
            Echo("Drills: " + (drillsRunning.Value ? "On" : "Off"));
    }
    else {
            Echo("Drills: Not found");
    }

    if (connector == null) {
            Echo("Connector: Not found");
    }
    else {
            Echo("Connector: " + (connector.Status == MyShipConnectorStatus.Connected ? "Locked" : "Unlocked"));
    }

    float percent = (float)Math.Round(fillPercent * 100f, 1);

    Echo("Inventory: " + (percent == 0f ? "Empty\n" : percent + "%\n"));

    foreach (KeyValuePair<string, VRage.MyFixedPoint> item in inventories) {
        Echo(item.Key + ": " + ((float)item.Value).ToString("F2") + " L");    
    }
}

private void Dock()
{
        LockConnector();
    
        if (connector != null && connector.Status == MyShipConnectorStatus.Connected) {
                mode = DrillMode.UNLOADING;
        }
}

private void Drill()
{
    if (depthLimitReached) {
        mode = DrillMode.IDLE;
        return;
    }

    UnlockConnector();
    TurnOnDrills();

    IMyBlockGroup pistonGroup = GridTerminalSystem.GetBlockGroupWithName(pistonGroupName); 
 
    List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>(); 
    pistonGroup.GetBlocks(blocks); 
 
    bool extending = false;
    foreach (IMyPistonBase piston in blocks) {
            if (piston.CurrentPosition == piston.HighestPosition) {
                Echo(piston.CustomName + ": " + piston.Status + " (" + piston.Velocity + ")");
                piston.Velocity = 0f;
            }
            else {
                Echo(piston.CustomName + ": " + piston.Status + " (" + piston.Velocity + ")"); 
                piston.Velocity = 0.5f;
                extending = true;
            }
    }

    if (extending) {
        fillPercentSnapshot = null;
    }
    else {
        if (!fillPercentSnapshot.HasValue || fillPercent > fillPercentSnapshot) { 
            fillPercentSnapshot = fillPercent; 
        }
        else {
            // Maximum depth reached, without increasing cargo volume. That's our limit

            depthLimitReached = true;
            runOnce = true;
            mode = DrillMode.RETRACTING;
            return;
        }
    }

    if (fillPercent > (triggerUnloadAtPercent / 100f)) {
        mode = DrillMode.RETRACTING;
    }
}

private bool FindConnector() {
    List<IMyShipConnector> connectors = new List<IMyShipConnector>(); 
 
    GridTerminalSystem.GetBlocksOfType<IMyShipConnector>(connectors); 
 
    if (connectors.Count == 0) {
            connector = null;
            return false; 
    } 

    connector = connectors[0];

    return true;
}

private void LockConnector()
{ 
    if (connector != null && connector.Enabled == true && connector.Status == MyShipConnectorStatus.Connected) { 
            return;     
    } 
 
    connector.Enabled = true;  
    connector.Connect(); 
}

private void RetractPistons()
{
    TurnOffDrills();

    if (connector != null && connector.Enabled == true) {
        connector.Enabled = false;
    }

    IMyBlockGroup pistonGroup = GridTerminalSystem.GetBlockGroupWithName(pistonGroupName); 
 
    List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>(); 
    pistonGroup.GetBlocks(blocks); 
 
    foreach (IMyPistonBase piston in blocks) { 
            if (piston.Status == PistonStatus.Retracted) { 
                    mode = DrillMode.DOCKING;
                    return;
            }
            piston.Velocity = -0.5f;
    }
}

private void StopPistons()
{
    IMyBlockGroup pistonGroup = GridTerminalSystem.GetBlockGroupWithName(pistonGroupName);  

    if (pistonGroup == null) {
        return;
    }
  
    List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();  
    pistonGroup.GetBlocks(blocks);  

    foreach (IMyPistonBase piston in blocks) {  
            piston.Velocity = 0f; 
    }
}

private void TurnOffDrills()
{
    if (drillsRunning.HasValue && drillsRunning.Value == false) {
        return;
    }

    IMyBlockGroup drillGroup = GridTerminalSystem.GetBlockGroupWithName(drillGroupName);   

    if (drillGroup == null) {    
        return; 
    } 
   
    List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();   
    drillGroup.GetBlocks(blocks);   

    if (blocks.Count == 0) { 
            return; 
    } 
   
    foreach (IMyShipDrill drill in blocks) {   
            drill.Enabled = false; 
    }

    drillsRunning = false;
} 

private void TurnOnDrills()
{
    if (drillsRunning.HasValue && drillsRunning.Value == true) { 
        return; 
    }

    IMyBlockGroup drillGroup = GridTerminalSystem.GetBlockGroupWithName(drillGroupName);  
  
    if (drillGroup == null) {   
        return;
    }

    List<IMyTerminalBlock> blocks = new List<IMyTerminalBlock>();  
    drillGroup.GetBlocks(blocks);  
  
    if (blocks.Count == 0) {
            return;
    }

    foreach (IMyShipDrill drill in blocks) {  
            drill.Enabled = true;
    }

    drillsRunning = true;
}

private void UnloadCargo()
{
    if (fillPercent == 0) {
            mode = runOnce ? DrillMode.IDLE : DrillMode.RUNNING;
    }
}

private void UnlockConnector()
{
    if (connector != null && connector.Enabled == false) {
            return;    
    }

    connector.Disconnect();
    connector.Enabled = false;
}

private enum DrillMode
{
        IDLE, RUNNING, RETRACTING, DOCKING, UNLOADING
}

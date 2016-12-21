
//Automated Patrol and Defence drone
//Created by: Eldar Storm
//Website: http://radioactivecricket.com
//GitHub: https://github.com/eldarstorm/SE_Defence_and_Patrol_Drone

//Configuration Section 
const string strSensor = "SENSOR"; //Name your Sensor this
const string strCamera = "RangeFinder"; //Rangefinder Camera (Optional) (Future Feature)
const string strLCD = "LCD Panel"; //Name your LCD Info Panel This (Optional) 
const string strModeIndicator = "INDICATOR";  //Interior Light - Mode Indicator (Optional) 
const string strPatrol = "PATROL"; //For Second RC Block used for Patrol (Optional) 
const double maxFollowRange = 5000; //Distance from home or last location before returning 
const double maxEnemyRanege = 2500; //Max range enemy gets from the drone before returning 
const float turretRange = 600; //Turret Range 
const bool patrolOveride = false; //Set true if you want to overide the Patrol autodetection. true = no patrol
const float distanceBuffer = 5; //Used for home and last location. Distace from waypoint before it toggles done (meters)
const double followDistance = 50;  //Distance drone will keep from target
const double attackDistance = 50;  //Distance drone will keep from target
const bool autoHome = true; //If true, will auto home if not near home when in Idle mode

//Do not edit below this point.

//Pre Defined Global Variables 
IMySensorBlock sensor = null;
IMyInteriorLight modeIndicator = null;
IMyRemoteControl remote = null;
IMyRemoteControl patrol = null;
IMyTextPanel lcd = null;

MyDetectedEntityInfo targetGrid = new MyDetectedEntityInfo();

Vector3D lastLoc = new Vector3D(0, 0, 0);
Vector3D originLoc = new Vector3D(0, 0, 0);
Vector3D emptyLoc = new Vector3D(0, 0, 0);

bool patrolEnabled = false; //Code automatically sets this 
StringBuilder status = new StringBuilder(); //String Builder for status screen

List<IMyTerminalBlock> list = new List<IMyTerminalBlock>();
List<IMyUserControllableGun> turrets = new List<IMyUserControllableGun>();

//errorStatus = -1 Dead Stick Mode (Do nothing)
//errorStatus =  0 All good
//errorStatus =  1 Damaged, but can fly
int errorStatus = 0;

void Main(string argument)
{
	status = new StringBuilder(); //Resets the Status String
	errorStatus = 0; //Start in normal status

	status.AppendLine("Home: " + Me.CustomData.ToString());

	//Checks for arguments passed into the block.  
	if (argument == "reset")
	{
		Me.CustomData = null;
	}
	else if (argument.IndexOf("GPS") > -1) //Manual GPS Home Entry 
	{
		char[] delimiterChars = { ':' };
		string[] GPSSplit = argument.Split(delimiterChars);

		Me.CustomData = "{X:" + GPSSplit[2].ToString() + " Y:" + GPSSplit[3].ToString() + " Z:" + GPSSplit[4].ToString() + "}";
	}

	//Sets the Origin location into its persistent variable 
	if (Me.CustomData == null || Me.CustomData == "")
	{
		originLoc = Me.GetPosition();
		Me.CustomData = originLoc.ToString();
	}
	else
	{
		Vector3D.TryParse(Me.CustomData, out originLoc);
	}

	//Checks for patrol override and sets up the needed block 
	if (!patrolOveride)
	{
		patrol = (IMyRemoteControl)GridTerminalSystem.GetBlockWithName(strPatrol);
		patrolEnabled = patrol != null;
	}

	//Sets the main RC Block 
	GridTerminalSystem.GetBlocksOfType<IMyRemoteControl>(list);
	if (list.Count > 0)
	{
		status.AppendLine("RC Block Found");

		remote = list[0] as IMyRemoteControl;
		//If Patrol block is the first one found, use next in list
		if (list.Count > 1 && patrol != null)
		{
			if (patrol == remote)
				remote = list[1] as IMyRemoteControl;
		}

		//If no waypoints in list then dissable patrol system
		List<MyWaypointInfo> patList = new List<MyWaypointInfo>();
		patrol.GetWaypointInfo(patList);
		if (patList.Count < 1)
		{
			status.AppendLine("No Patrol points set.");
			patrolEnabled = false;
		}

		//If multiple waypoints in Remote then clear them
		List<MyWaypointInfo> wpList = new List<MyWaypointInfo>();
		remote.GetWaypointInfo(wpList);
		if (wpList.Count > 1)
			remote.ClearWaypoints();

		//Sets Patrol Status
		if (patrolEnabled)
			status.AppendLine("Patrol System Enabled");
		else
			status.AppendLine("Patrol System Dissabled");
	}
	else
	{
		errorStatus = -1;

		status.AppendLine("No RC Block Found: Dead Stick Mode");

		EchoLCD(status.ToString());

		//No point in continuing, Exiting function.
		return;
	}
		

	//Sets the sensor
	sensor = (IMySensorBlock)GridTerminalSystem.GetBlockWithName(strSensor);
	//Sets mod indicator light
	modeIndicator = (IMyInteriorLight)GridTerminalSystem.GetBlockWithName(strModeIndicator);
	//Larger Status screen (Debugging)
	lcd = GridTerminalSystem.GetBlockWithName(strLCD) as IMyTextPanel;

	//Checks for the Sensor 
	if (sensor != null)
	{
		if (sensor.IsFunctional) //If the sensor is functional
		{
			status.AppendLine("Sensor Found");
			status.AppendLine();
		}
		else
		{
			status.AppendLine("Sensor is damaged");
			errorStatus = 1; //Set Damaged State
		}

	}
	else
	{
		status.AppendLine("Sensor not found");
		errorStatus = 1; //Set Damaged State
	}

	//If turrets are destroyed, return back home
	turrets = new List<IMyUserControllableGun>();
	GridTerminalSystem.GetBlocksOfType<IMyUserControllableGun>(turrets);
	if (turrets.Count < 1)
	{
		status.AppendLine("Turret damaged or missing");
		errorStatus = 1; //Set Damaged State
	}

	//Ammo Check
	bool hasAmmo = false;
	turrets.ForEach(turret => {
		if (turret.HasInventory() && turret.GetInventory(0).IsItemAt(0))
			hasAmmo = true;	
	});

	//If no Ammo then return home
	if(!hasAmmo)
	{
		errorStatus = 1;
		status.AppendLine();
		status.Append("Turrets out of ammo.");
		status.AppendLine();
	}

	//If No Errors then run main AI
	if (errorStatus == 0)
	{
		//Makes sure the Drone does not wonder too far
		bool tooFar = false;
		if (Vector3D.DistanceSquared(Me.GetPosition(), originLoc) > maxFollowRange * maxFollowRange)
		{
			if (!Equals(lastLoc, emptyLoc))
				ReturnLastLoc();
			else
				ReturnHome();

			patrol.SetAutoPilotEnabled(false);
			tooFar = true;
		}

		//Main AI Section
		//Look for targets
		if(Scan() && !tooFar)
		{
			AttackTarget(targetGrid);
		}
		else //No Targets
		{
			//If Patrole system Enabled
			if(patrolEnabled)
			{
				if (!ReturnLastLoc()) //If no longer going to last location
					Patrol(); //Run patrole
			}
			else
			{
				if (!ReturnHome()) //If no longer returning home then go idle
					Idle();
			}
		}
		//End AI Section
	}
	else
	{
		MissingOrDamaged();
	}

	EchoLCD(status.ToString());
}

//Idle Drone function
void Idle()
{
	//If Auto Home enabled then return home if not within buffer range.
	if (Vector3.DistanceSquared(originLoc, Me.GetPosition()) > distanceBuffer * distanceBuffer && autoHome)
	{
		ReturnHome();
		return;
	}
	else
	{
		if (modeIndicator != null)
			modeIndicator.SetValue<Color>("Color", new Color(1f, 1f, 0f));

		status.AppendLine();
		status.AppendLine();
		status.AppendLine("Status: Idle");

		StopPatrol();
		ResetTargets(); //resets any target info
		
		remote?.SetAutoPilotEnabled(false);
		remote?.ClearWaypoints();
	}
}

//If part is missing or damaged
void MissingOrDamaged()
{
	status.AppendLine();
	status.AppendLine("--[[Damaged State]]--");

	ReturnHome();
}

//Sets the drone to use the patrol RC block
void Patrol()
{
	if (patrol != null)
	{
		if (modeIndicator != null)
			modeIndicator.SetValue<Color>("Color", new Color(0f, 0.5f, 1f));

		ResetTargets(); //resets any target info

		status.AppendLine();
		status.AppendLine();
		status.AppendLine("Status: Patrol");

		remote?.ClearWaypoints(); //Clears waypoints on RC Block
		patrol.SetAutoPilotEnabled(true);
	}
	else
	{
		patrolEnabled = false;
		Idle();
	}
}

//If Patrol RC then it will disable autopilot when called 
void StopPatrol()
{
	if (patrolEnabled)
	{
		patrol?.SetAutoPilotEnabled(false);
		
		if (Equals(lastLoc, emptyLoc))
			lastLoc = Me.GetPosition();
	}
}

//returns drone to its home location
private bool ReturnHome()
{
	//if the drone is within the buffer distance from the home location
	if (Vector3.DistanceSquared(originLoc, Me.GetPosition()) < distanceBuffer * distanceBuffer)
		return false;

	//indicator light
	modeIndicator?.SetValue<Color>("Color", new Color(0f, 1f, 0f));

	status.AppendLine();
	status.AppendLine();
	status.AppendLine("Status: " + "Returning Home");
	status.AppendLine("Distance Home: " + Vector3.Distance(originLoc, Me.GetPosition()).ToString());

	ResetTargets(); //resets any target info
	StopPatrol(); //In case patrole is still active

	SetWaypoint("Origin", originLoc);

	return true;
}

//Returns Drone to the last location
private bool ReturnLastLoc()
{
	//Checks to make sure the lastLoc was not cleared.
	if (Equals(lastLoc, emptyLoc))
		return false;

	//if the drone is within the buffer distance from its last location
	if (Vector3.DistanceSquared(lastLoc, Me.GetPosition()) < distanceBuffer * distanceBuffer)
	{
		lastLoc = new Vector3D(0, 0, 0);
		return false;
	}
	
	patrol.SetAutoPilotEnabled(false);

	modeIndicator?.SetValue<Color>("Color", new Color(0.5f, 1f, 0.5f));

	status.AppendLine();
	status.AppendLine("CK"+ Equals(lastLoc, emptyLoc).ToString());
	status.AppendLine("Status: Returning Last Location");

	ResetTargets(); //resets any target info

	SetWaypoint("Last Location", lastLoc);

	return true;
}

//Follows the given target
void Follow(MyDetectedEntityInfo grid)
{
	modeIndicator?.SetValue<Color>("Color", new Color(1f, 0f, 0f));

	StopPatrol(); //Stops the patrole RC Block
	AppendTargetInformation(grid, "Following Target");
	//Gets the offset waypoint.
	Vector3D newPosition = OffsetPos(Me.GetPosition(), grid.Position, followDistance);
	SetWaypoint("Following", newPosition);
}

//follow and attack given target
void AttackTarget(MyDetectedEntityInfo grid)
{
	modeIndicator?.SetValue<Color>("Color", new Color(1f, 0f, 0f));

	StopPatrol(); //Stops the patrole RC Block
	AppendTargetInformation(grid, "Attacking Target");
	//Gets the offset waypoint.
	Vector3D newPosition = OffsetPos(Me.GetPosition(), grid.Position, attackDistance);
	SetWaypoint("Target", newPosition);
	EnableTurrets(grid);
}

void AppendTargetInformation(MyDetectedEntityInfo grid, string status) {
	status.AppendLine($"Target ID: {grid.EntityId}");
	status.AppendLine($"Name: {grid.Name}");
	status.AppendLine($"Type: {grid.Type}");
	status.AppendLine($"Time: {grid.TimeStamp}");
	status.AppendLine($"Relationship: {grid.Relationship}");
	status.AppendLine($"Pos: {grid.Position}");
	status.AppendLine();
	status.AppendLine("Distance: " + Vector3.Distance(grid.Position, Me.GetPosition()).ToString());
	status.AppendLine();
	status.AppendLine($"Status: {status}");
}

//Scans for targets
private bool Scan()
{
	if (sensor.IsActive) //If sensor picks up any targets
	{
		List<MyDetectedEntityInfo> targetList = new List<MyDetectedEntityInfo>();
		sensor.DetectedEntities(targetList);
		status.AppendLine($"Found by Sensor: {targetList.Count}");

		double detectedGridDist = -1;
		double distHolder = 0;
		MyDetectedEntityInfo gridHolder = new MyDetectedEntityInfo();
		bool foundOne = false;

		//Finds the closest target
		targetList.ForEach(target => {
			if (ValidTarget(target))
			{
				distHolder = Vector3D.DistanceSquared(target.Position, Me.GetPosition());
				if (detectedGridDist < 0 || distHolder < detectedGridDist)
				 {
					detectedGridDist = distHolder;
					gridHolder = target;
					foundOne = true;
				}
			}
		});

		//if target found then set target grid
		if(foundOne)
		{
			status.AppendLine("Target Locked");
			targetGrid = gridHolder;
			return true;
		}
	   	else
		{
			status.AppendLine("No valid targets found.");
			return false;
		}
	}

	return false;
}

//Resets target var and dissables turrets
void ResetTargets()
{
	targetGrid = new MyDetectedEntityInfo();
	DissableTurrets(); //Dissables all turrets
}

//Makes sure the target is still valid 
private bool ValidTarget(MyDetectedEntityInfo grid)
{
	//Range Checks 
	if (grid.IsEmpty()
	    || Vector3D.DistanceSquared(Me.GetPosition(), grid.Position) > maxEnemyRanege * maxEnemyRanege
	    || grid.Relationship != VRage.Game.MyRelationsBetweenPlayerAndBlock.Enemies)
		return false;
	return true;
}

//Calculates a Vector3D point before point B
private Vector3D OffsetPos(Vector3D a, Vector3D b, double offset)
{
	return -offset * Vector3D.Normalize(b - a) + b;
}

//Sets the waypoint for the main RC Block
void SetWaypoint(string name, Vector3D pos)
{
	if (remote != null)
	{
		
		List<MyWaypointInfo> wpList = new List<MyWaypointInfo>();
		remote.GetWaypointInfo(wpList);
		
		if (wpList.Count == 0 || wpList.Count > 1)
		{
			remote.ClearWaypoints();
			remote.AddWaypoint(pos, name);
		}
		else if (wpList[0].Name != name || !Equals(wpList[0].Coords, pos))
		{
			remote.ClearWaypoints();
			remote.AddWaypoint(pos, name);
		}

		remote.SetAutoPilotEnabled(true);

		status.AppendLine();
		status.AppendLine("Waypoint Set. Distance: " + Vector3.Distance(pos, Me.GetPosition()).ToString());
		status.AppendLine("Waypoint Position: " + pos.ToString());
	}
	else
		errorStatus = -1;
}

//Enable all turrets on the grid 
void EnableTurrets(MyDetectedEntityInfo grid)
{
	var action = Vector3.DistanceSquared(grid.Position, Me.GetPosition()) < turretRange * turretRange ? "OnOff_On" : "OnOff_Off";
	turrets.ForEach(turret => turre.tApplyAction(action));
}

//Disable all turrets on the grid 
void DissableTurrets()
{
	turrets.ForEach(turret => turret.ApplyAction("OnOff_Off"));
}

//Outputs the status text to the LCD
void EchoLCD( string text)
	{
	lcd?.WritePublicText(text);
	lcd?.ShowTextureOnScreen();
	lcd?.ShowPublicTextOnScreen();
}

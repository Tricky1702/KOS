using kOS.AddOns.RemoteTech;
using kOS.Safe.Binding;
using kOS.Safe.Utilities;
using kOS.Safe.Exceptions;
using kOS.Suffixed;
using kOS.Utilities;
using System;
using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Math = System.Math;
using kOS.Control;
using kOS.Module;
using kOS.Communication;

namespace kOS.Binding
{
    [Binding("ksp")]
    public class FlightControlManager : Binding , IDisposable
    {
        private Vessel currentVessel;
        private readonly Dictionary<string, FlightCtrlParam> flightParameters = new Dictionary<string, FlightCtrlParam>();
        private static readonly Dictionary<uint, FlightControl> flightControls = new Dictionary<uint, FlightControl>();
        /// <summary>How often to re-attempt the remote tech hook, expressed as a number of physics updates</summary>
        private const int RemoteTechRehookPeriod = 25;
        private int counterRemoteTechRefresh = RemoteTechRehookPeriod - 2; // make sure it starts out ready to trigger soon
        public SharedObjects Shared { get; set; }

        public override void AddTo(SharedObjects shared)
        {
            Shared = shared;

            if (Shared.Vessel == null)
            {
                SafeHouse.Logger.LogWarning("FlightControlManager.AddTo Skipped: shared.Vessel== null");
                return;
            }

            if (Shared.Vessel.rootPart == null)
            {
                SafeHouse.Logger.LogWarning("FlightControlManager.AddTo Skipped: shared.Vessel.rootPart == null");
                return;
            }

            SafeHouse.Logger.Log("FlightControlManager.AddTo " + Shared.Vessel.id);

            currentVessel = shared.Vessel;
            ConnectivityManager.AddAutopilotHook(currentVessel, OnFlyByWire);

            AddNewFlightParam("throttle", Shared);
            AddNewFlightParam("steering", Shared);
            AddNewFlightParam("wheelthrottle", Shared);
            AddNewFlightParam("wheelsteering", Shared);

            shared.BindingMgr.AddSetter("SASMODE", value => SelectAutopilotMode(value));
            shared.BindingMgr.AddGetter("SASMODE", () => GetAutopilotModeName());
            shared.BindingMgr.AddSetter("NAVMODE", value => SetNavMode(value));
            shared.BindingMgr.AddGetter("NAVMODE", () => GetNavModeName());
        }


        private void OnFlyByWire(FlightCtrlState c)
        {
            foreach (var param in flightParameters.Values)
            {
                if (param.Enabled)
                {
                    param.OnFlyByWire(ref c);
                }
            }
        }

        public void ToggleFlyByWire(string paramName, bool enabled)
        {
            SafeHouse.Logger.Log(string.Format("FlightControlManager: ToggleFlyByWire: {0} {1}", paramName, enabled));
            if (!flightParameters.ContainsKey(paramName.ToLower())) { Debug.LogError("no such flybywire parameter " + paramName); return; }

            flightParameters[paramName.ToLower()].Enabled = enabled;

            if (!enabled)
            {
                flightParameters[paramName.ToLower()].ClearValue();
            }
        }

        public override void Update()
        {
            UnbindUnloaded();

            // Why the "currentVessel != null checks?
            //   Because of a timing issue where it can still be set to null during the span of one single
            //   update if the new vessel isn't valid and set up yet when the old vessel connection got
            //   broken off.
            //
            if (currentVessel != null && currentVessel.id == Shared.Vessel.id)
            {
                if (ConnectivityManager.NeedAutopilotResubscribe)
                {
                    if (++counterRemoteTechRefresh > RemoteTechRehookPeriod)
                    {
                        ConnectivityManager.AddAutopilotHook(currentVessel, OnFlyByWire);
                    }
                }
                else
                {
                    counterRemoteTechRefresh = RemoteTechRehookPeriod - 2;
                }
                return;
            }

            // If it gets this far, that means the part the kOSProcessor module is inside of
            // got disconnected from its original vessel and became a member
            // of a new child vessel, either due to undocking, decoupling, or breakage.

            // currentVessel is now a stale reference to the vessel this manager used to be a member of,
            // while Shared.Vessel is the new vessel it is now contained in.

            // Before updating currentVessel, use it to break connection from the old vessel,
            // so this this stops trying to pilot the vessel it's not attached to anymore:
            if (currentVessel != null && VesselIsValid(currentVessel))
            {
                ConnectivityManager.RemoveAutopilotHook(currentVessel, OnFlyByWire);
                currentVessel = null;
            }

            // If the new vessel isn't ready for it, then don't attach to it yet (wait for a future update):
            if (! VesselIsValid(Shared.Vessel)) return;
            
            // Now attach to the new vessel:
            currentVessel = Shared.Vessel;
            ConnectivityManager.AddAutopilotHook(currentVessel, OnFlyByWire);

            foreach (var param in flightParameters.Values)
                param.UpdateFlightControl(currentVessel);

            // If any paramers were removed in UnbindUnloaded, add them back here
            AddMissingFlightParam("throttle", Shared);
            AddMissingFlightParam("steering", Shared);
            AddMissingFlightParam("wheelthrottle", Shared);
            AddMissingFlightParam("wheelsteering", Shared);
        }

        public static FlightControl GetControllerByVessel(Vessel target)
        {
            FlightControl flightControl;
            if (!flightControls.TryGetValue(target.rootPart.flightID, out flightControl))
            {
                flightControl = new FlightControl(target);
                flightControls.Add(target.rootPart.flightID, flightControl);
            }

            if (flightControl.Vessel == null)
                flightControl.UpdateVessel(target);

            return flightControl;
        }

        private static void UnbindUnloaded()
        {
            var toRemove = new List<uint>();
            foreach (var key in flightControls.Keys)
            {
                var value = flightControls[key];
                if (value.Vessel.loaded) continue;
                SafeHouse.Logger.Log("Unloading " + value.Vessel.vesselName);
                toRemove.Add(key);
                value.Dispose();
            }

            foreach (var key in toRemove)
            {
                flightControls.Remove(key);
            }
        }

        private void AddNewFlightParam(string name, SharedObjects shared)
        {
            flightParameters[name] = new FlightCtrlParam(name, shared);
        }

        private void AddMissingFlightParam(string name, SharedObjects shared)
        {
            if (!flightParameters.ContainsKey(name))
            {
                AddNewFlightParam(name, shared);
            }
        }

        public void UnBind()
        {
            foreach (var parameter in flightParameters)
            {
                parameter.Value.Enabled = false;
            }
            if (!VesselIsValid(currentVessel)) return;

            FlightControl flightControl;
            if (flightControls.TryGetValue(currentVessel.rootPart.flightID, out flightControl))
            {
                flightControl.Unbind();
            }
        }

        public void Dispose()
        {
            flightParameters.Clear();
            if (!VesselIsValid(currentVessel)) return;

            UnBind();
            flightControls.Remove(currentVessel.rootPart.flightID);
        }

        private bool VesselIsValid(Vessel vessel)
        {
            return vessel != null && vessel.rootPart != null;
        }

        public void SelectAutopilotMode(object autopilotMode)
        {
            autopilotMode = Safe.Encapsulation.Structure.FromPrimitiveWithAssert(autopilotMode);
            if ((autopilotMode is Safe.Encapsulation.StringValue))
            {
                SelectAutopilotMode(autopilotMode.ToString());
            }
            else if (autopilotMode is Direction)
            {
                //TODO: implment use of direction subclasses.
                throw new KOSException(
                    string.Format("Cannot set SAS mode to a direction. Should use the name of the mode (as string, e.g. \"PROGRADE\", not PROGRADE) for SASMODE. Alternatively, can use LOCK STEERING TO Direction instead of using SAS"));
            }
            else
            {
                throw new KOSWrongControlValueTypeException(
                  "SASMODE", KOSNomenclature.GetKOSName(autopilotMode.GetType()), "name of the SAS mode (as string)");
            }
        }

        public void SelectAutopilotMode(VesselAutopilot.AutopilotMode autopilotMode)
        {
            if (currentVessel.Autopilot.Mode != autopilotMode)
            {
                if (!currentVessel.Autopilot.CanSetMode(autopilotMode))
                {
                    // throw an exception if the mode is not available
                    throw new KOSSituationallyInvalidException(
                        string.Format("Cannot set autopilot value, pilot/probe does not support {0}, or there is no node/target", autopilotMode));
                }
                currentVessel.Autopilot.SetMode(autopilotMode);
                //currentVessel.Autopilot.Enable();
                // change the autopilot indicator
                ((kOSProcessor)Shared.Processor).SetAutopilotMode((int)autopilotMode);
                if (RemoteTechHook.IsAvailable(currentVessel.id))
                {
                    //Debug.Log(string.Format("kOS: Adding RemoteTechPilot: autopilot For : " + currentVessel.id));
                    // TODO: figure out how to make RemoteTech allow the built in autopilot control.  This may require modification to RemoteTech itself.
                }
            }
        }

        public string GetAutopilotModeName()
        {
            // TODO: As of KSP 1.1.2, RadialIn and RadialOut are still swapped.  Check if still true in future versions.
            if (currentVessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.RadialOut) { return "RADIALIN"; }
            if (currentVessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.RadialIn) { return "RADIALOUT"; }

            return currentVessel.Autopilot.Mode.ToString().ToUpper();
        }

        public void SelectAutopilotMode(string autopilotMode)
        {
            // handle a null/empty value in case of an unset command or setting to empty string to clear.
            if (string.IsNullOrEmpty(autopilotMode))
            {
                SelectAutopilotMode(VesselAutopilot.AutopilotMode.StabilityAssist);
            }
            else
            {
                // determine the AutopilotMode to use
                switch (autopilotMode.ToLower())
                {
                    case "maneuver":
                        SelectAutopilotMode(VesselAutopilot.AutopilotMode.Maneuver);
                        break;
                    case "prograde":
                        SelectAutopilotMode(VesselAutopilot.AutopilotMode.Prograde);
                        break;
                    case "retrograde":
                        SelectAutopilotMode(VesselAutopilot.AutopilotMode.Retrograde);
                        break;
                    case "normal":
                        SelectAutopilotMode(VesselAutopilot.AutopilotMode.Normal);
                        break;
                    case "antinormal":
                        SelectAutopilotMode(VesselAutopilot.AutopilotMode.Antinormal);
                        break;
                    case "radialin":
                        // TODO: As of KSP 1.0.4, RadialIn and RadialOut are swapped.  Check if still true in future versions.
                        SelectAutopilotMode(VesselAutopilot.AutopilotMode.RadialOut);
                        break;
                    case "radialout":
                        SelectAutopilotMode(VesselAutopilot.AutopilotMode.RadialIn);
                        break;
                    case "target":
                        SelectAutopilotMode(VesselAutopilot.AutopilotMode.Target);
                        break;
                    case "antitarget":
                        SelectAutopilotMode(VesselAutopilot.AutopilotMode.AntiTarget);
                        break;
                    case "stability":
                    case "stabilityassist":
                        SelectAutopilotMode(VesselAutopilot.AutopilotMode.StabilityAssist);
                        break;
                    default:
                        // If the mode is not recognised, throw an exception rather than continuing or using a default setting
                        throw new KOSException(
                            string.Format("kOS does not recognize the SAS mode setting of {0}", autopilotMode));
                }
            }
        }

        public string GetNavModeName()
        {
            return GetNavMode().ToString().ToUpper();
        }


        public FlightGlobals.SpeedDisplayModes GetNavMode()
        {
            if (Shared.Vessel != FlightGlobals.ActiveVessel)
            {
                throw new KOSSituationallyInvalidException("NAVMODE can only be accessed for the Active Vessel");
            }
            return FlightGlobals.speedDisplayMode;
        }   

        public void SetNavMode(FlightGlobals.SpeedDisplayModes navMode)
        {
            FlightGlobals.SetSpeedMode(navMode);
        }

        public void SetNavMode(object navMode)
        {
            navMode = Safe.Encapsulation.Structure.FromPrimitiveWithAssert(navMode);
            if (!(navMode is Safe.Encapsulation.StringValue))
            {
                throw new KOSWrongControlValueTypeException(
                  "NAVMODE", KOSNomenclature.GetKOSName(navMode.GetType()), "string (\"ORBIT\", \"SURFACE\" or \"TARGET\")");
            }
            SetNavMode(navMode.ToString());
        }

        public void SetNavMode(string navMode)
        {
            if (Shared.Vessel != FlightGlobals.ActiveVessel)
            {
                throw new KOSSituationallyInvalidException("NAVMODE can only be accessed for the Active Vessel");
            }
            // handle a null/empty value in case of an unset command or setting to empty string to clear.
            if (string.IsNullOrEmpty(navMode))
            {
                SetNavMode(FlightGlobals.SpeedDisplayModes.Orbit);
            }
            else
            {
                // determine the navigation mode to use
                switch (navMode.ToLower())
                {
                    case "orbit":
                        SetNavMode(FlightGlobals.SpeedDisplayModes.Orbit);
                        break;
                    case "surface":
                        SetNavMode(FlightGlobals.SpeedDisplayModes.Surface);
                        break;
                    case "target":
                        if(FlightGlobals.fetch.VesselTarget== null) {
                            throw new KOSException("Cannot set navigation mode: there is no target");
                        }
                        SetNavMode(FlightGlobals.SpeedDisplayModes.Target);
                        break;
                    default:
                        // If the mode is not recognised, throw an exception rather than continuing or using a default setting
                        throw new KOSException(
                            string.Format("kOS does not recognize the navigation mode setting of {0}", navMode));
                }
            }
        }

        private class FlightCtrlParam : IDisposable
        {
            private readonly string name;
            private FlightControl control;
            private readonly IBindingManager binding;
            private object value;
            private bool enabled;
            private readonly SharedObjects shared;

            public FlightCtrlParam(string name, SharedObjects sharedObjects)
            {
                this.name = name;
                shared = sharedObjects;
                control = GetControllerByVessel(sharedObjects.Vessel);
                
                binding = sharedObjects.BindingMgr;
                Enabled = false;
                value = null;


                HookEvents();
            }

            private void HookEvents()
            {
                binding.AddGetter(name, () => getValue());
                binding.AddSetter(name, val => setValue(val));
            }

            private object getValue()
            {
                if (name == "throttle")
                {
                    if (Enabled) return value;
                    return shared.Vessel.ctrlState.mainThrottle;
                }
                else if (name == "steering")
                {
                    return kOSVesselModule.GetInstance(shared.Vessel).GetFlightControlParameter("steering").GetValue();
                }
                else
                {
                    return value;
                }
            }

            private void setValue(object val)
            {
                if (name == "steering")
                {
                    IFlightControlParameter param = kOSVesselModule.GetInstance(shared.Vessel).GetFlightControlParameter("steering");
                    if (param != null) param.UpdateValue(val, shared);
                }
                else
                {
                    value = val;
                }
            }

            public bool Enabled
            {
                get { return enabled; }
                set
                {
                    SafeHouse.Logger.Log(string.Format("FlightCtrlParam: Enabled: {0} {1} => {2}", name, enabled, value));

                    if (enabled != value)
                    {

                        enabled = value;
                        if (name == "steering")
                        {
                            SafeHouse.Logger.Log("FlightCtrlParam: toggle steering parameter");
                            IFlightControlParameter param = kOSVesselModule.GetInstance(shared.Vessel).GetFlightControlParameter("steering");
                            if (enabled)
                            {
                                param.EnableControl(shared);
                            }
                            else
                            {
                                param.DisableControl(shared);
                            }
                            return;
                        }
                    }
                }
            }

            public void ClearValue()
            {
                value = null;
            }

            public void OnFlyByWire(ref FlightCtrlState c)
            {
                if (value == null || !Enabled) return;

                switch (name)
                {
                    case "throttle":
                        UpdateThrottle(c);
                        break;
                    case "wheelthrottle":
                        UpdateWheelThrottle(c);
                        break;
                    case "steering":
                        SteerByWire(c);
                        break;
                    case "wheelsteering":
                        WheelSteer(c);
                        break;
                    default:
                        break;
                }
            }

            private void UpdateThrottle(FlightCtrlState c)
            {
                if (!Enabled) return;
                try
                {
                    double doubleValue = Convert.ToDouble(value);
                    if (!double.IsNaN(doubleValue))
                        c.mainThrottle = (float)Safe.Utilities.KOSMath.Clamp(doubleValue, 0, 1);
                }
                catch (InvalidCastException) // Note, very few types actually fail Convert.ToDouble(), so it's hard to get this to occur.
                {
                    // perform the "unlock" so this message won't spew every FixedUpdate:
                    Enabled = false;
                    ClearValue();
                    throw new KOSWrongControlValueTypeException(
                        "THROTTLE", value.GetType().Name, "Number in the range [0..1]");
                }
            }

            private void UpdateWheelThrottle(FlightCtrlState c)
            {
                if (!Enabled) return;
                try
                {
                    double doubleValue = Convert.ToDouble(value);
                    if (!double.IsNaN(doubleValue))
                        c.wheelThrottle = (float)Safe.Utilities.KOSMath.Clamp(doubleValue, -1, 1);
                }
                catch (InvalidCastException) // Note, very few types actually fail Convert.ToDouble(), so it's hard to get this to occur.
                {
                    // perform the "unlock" so this message won't spew every FixedUpdate:
                    Enabled = false;
                    ClearValue();
                    throw new KOSWrongControlValueTypeException(
                        "WHEELTHROTTLE", value.GetType().Name, "Number in the range [-1..1]");
                }
            }

            private void SteerByWire(FlightCtrlState c)
            {
                IFlightControlParameter param = kOSVesselModule.GetInstance(shared.Vessel).GetFlightControlParameter("steering");
                if (Enabled)
                {
                    if (!param.Enabled) param.EnableControl(shared);
                }
                else
                {
                    if (param.Enabled && param.ControlPartId == shared.KSPPart.flightID) param.DisableControl(shared);
                }
            }

            private void WheelSteer(FlightCtrlState c)
            {
                if (!Enabled) return;
                float bearing = 0;

                if (value is VesselTarget)
                {
                    bearing = VesselUtils.GetTargetBearing(control.Vessel, ((VesselTarget)value).Vessel);
                }
                else if (value is GeoCoordinates)
                {
                    bearing = (float) ((GeoCoordinates)value).GetBearing();
                }
                else
                {
                    try
                    {
                        double doubleValue = Convert.ToDouble(value);
                        if (Utils.IsValidNumber(doubleValue))
                        {
                            bearing = (float)(Math.Round(doubleValue) - Mathf.Round(FlightGlobals.ship_heading));
                            if (bearing < -180)
                                bearing += 360; // i.e. 359 degrees to the left is really 1 degree to the right.
                            else if (bearing > 180)
                                bearing -= 360; // i.e. 359 degrees to the right is really 1 degree to the left
                        }
                    }
                    catch (InvalidCastException) // Note, very few types actually fail Convert.ToDouble(), so it's hard to get this to occur.
                    {
                        // perform the "unlock" so this message won't spew every FixedUpdate:
                        Enabled = false;
                        ClearValue();
                        throw new KOSWrongControlValueTypeException(
                            "WHEELSTEER", value.GetType().Name, "Vessel, LATLNG, or Number (compass heading)");
                    }
                }

                if (!(control.Vessel.horizontalSrfSpeed > 0.1f)) return;

                if (Mathf.Abs(VesselUtils.AngleDelta(VesselUtils.GetHeading(control.Vessel), VesselUtils.GetVelocityHeading(control.Vessel))) <= 90)
                {
                    c.wheelSteer = Mathf.Clamp(bearing / -10, -1, 1);
                }
                else
                {
                    c.wheelSteer = -Mathf.Clamp(bearing / -10, -1, 1);
                }
            }

            public void Dispose()
            {
                Enabled = false;
            }

            public void UpdateFlightControl(Vessel vessel)
            {
                control = GetControllerByVessel(vessel);
            }
            
            public override string ToString() // added to aid in debugging.
            {
                return "FlightCtrlParam: name="+name+" enabled="+Enabled;
            }
 
        }
    }
}

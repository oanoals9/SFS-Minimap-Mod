using System;
using System.Collections.Generic;
using SFS.World;
using SFS.WorldBase;
using UnityEngine;

namespace SFSMiniMap
{
    /// <summary>
    /// One frame's worth of game state, read through public game API only. Everything is stored in
    /// "map units" (1 unit = 1 km) and relative to the centre of the planet the vehicle is at.
    /// </summary>
    public class MmSnapshot
    {
        public bool valid;
        public string failReason = "";

        public Rocket rocket;
        public Physics physics;
        public Location location;
        public Planet planet;
        public Trajectory trajectory;
        public Orbit orbit;

        public string planetName = "?";
        public string rocketName = "?";
        public Color planetColor = Color.white;
        public bool hasTerrain;

        public double radiusKm;
        public double soiKm;
        public double atmoKm;
        public double heightM;      // above ground, metres (game units)

        public Vector2 shipKm;      // planet relative
        public Vector2 velocity;    // planet relative, m/s

        /// <summary>The direction from the planet's centre to the vehicle, in degrees. Turning the map
        /// by this minus 90 puts the vehicle above the planet's centre, which is what makes the ground
        /// come out at the bottom.</summary>
        public double shipAngleDegrees;
        public double shipAngleRadians;

        public bool inOrbit;
        public bool inOrbitGeometric;
        public double surfaceM;
        public bool hasOrbit;
        public int pathCount;
        public string pathTypes = "";
        public string firstPathType = "none";
        public int warpIndex;
        public double warpSpeed;
        public bool realtimePhysics;
        public double apoapsisM;
        public double periapsisM;
        public double ecc;
        public string pathType = "?";

        public readonly List<Vector3> arc = new List<Vector3>();
        public bool hasArcEnd;
        public Vector2 arcEndKm;

        /// <summary>
        /// Half way between the apoapsis point and the periapsis point, in km from the centre of the
        /// orbit's planet - which for an ellipse is the centre of the ellipse itself. This is what the
        /// map is centred on once the vehicle is in orbit, instead of the planet's centre.
        /// </summary>
        public Vector2 orbitCentreKm;
        public bool hasOrbitCentre;

        /// <summary>
        /// Half the distance between the apoapsis point and the periapsis point, in km - which for an
        /// ellipse is the semi-major axis, the distance from its centre to either apsis. This is what
        /// the zoom is worked out from once the vehicle is in orbit, so that both apsis points stay
        /// inside the window (a point on an ellipse is never further from the centre than this, and
        /// the vehicle itself is on the ellipse).
        /// </summary>
        public float orbitSemiMajorKm;

        /// <summary>Whether there is an apoapsis worth marking. Asked of the value rather than of the
        /// eccentricity, so that an orbit whose eccentricity came out a hair above one is still marked
        /// while it has a real apoapsis.</summary>
        public bool HasAp { get { return hasOrbit && IsFinite(apoapsisM) && apoapsisM > 0.0; } }
        /// <summary>
        /// Whether there is a periapsis worth showing. Unlike the apoapsis this also has to be ABOVE
        /// THE GROUND: an orbit that would take the vehicle through the planet has a periapsis radius
        /// below the surface, and the game's map has nothing to show for it either. The vehicle is
        /// simply on its way to the ground, and a line reading "-120km" would only be noise.
        /// </summary>
        public bool HasPe
        {
            get
            {
                return hasOrbit && IsFinite(periapsisM) && periapsisM > 0.0 &&
                       (radiusKm <= 0.0 || periapsisM > radiusKm * 1000.0);
            }
        }

        public double ApoapsisAltitudeKm { get { return (apoapsisM - radiusKm * 1000.0) / 1000.0; } }
        public double PeriapsisAltitudeKm { get { return (periapsisM - radiusKm * 1000.0) / 1000.0; } }

        public static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
    }

    /// <summary>
    /// Reads the current vehicle, planet and trajectory out of the game. Every step is guarded, so a
    /// missing or half initialised game state produces an invalid snapshot instead of an exception.
    /// </summary>
    public static class MmTarget
    {
        private const int ArcSegments = 48;

        public static MmSnapshot Capture()
        {
            MmSnapshot snapshot = new MmSnapshot();
            try
            {
                Fill(snapshot);
            }
            catch (Exception e)
            {
                MmLog.Guard("read the game state", delegate { });
                MmLog.Error("reading the game state failed: " + e);
                snapshot.valid = false;
                snapshot.failReason = e.GetType().Name + ": " + e.Message;
            }
            return snapshot;
        }

        private static void Fill(MmSnapshot s)
        {
            PlayerController controller = PlayerController.main;
            if (MmLib.IsNull(controller))
            {
                s.failReason = "no PlayerController";
                return;
            }

            Player_Local playerLocal = controller.player;
            if (MmLib.IsNull(playerLocal))
            {
                s.failReason = "no player slot";
                return;
            }

            Player player = playerLocal.Value;
            if (MmLib.IsNull(player))
            {
                s.failReason = "no player (no vehicle)";
                return;
            }

            s.rocket = player as Rocket;
            s.rocketName = MmLib.IsSet(s.rocket) && !string.IsNullOrEmpty(s.rocket.rocketName)
                ? s.rocket.rocketName
                : "vehicle";

            WorldLocation worldLocation = player.location;
            if (MmLib.IsNull(worldLocation))
            {
                s.failReason = "no world location";
                return;
            }

            s.location = worldLocation.Value;
            s.planet = s.location.planet;
            if (MmLib.IsNull(s.planet))
            {
                s.failReason = "no planet";
                return;
            }

            s.planetName = string.IsNullOrEmpty(s.planet.codeName) ? s.planet.name : s.planet.codeName;
            s.radiusKm = s.planet.Radius / 1000.0;
            s.soiKm = MmSnapshot.IsFinite(s.planet.SOI) ? s.planet.SOI / 1000.0 : double.PositiveInfinity;
            s.heightM = s.location.Height;

            try
            {
                s.hasTerrain = s.planet.data != null && s.planet.data.hasTerrain;
                if (s.planet.data != null && s.planet.data.basics != null)
                    s.planetColor = s.planet.data.basics.mapColor;
            }
            catch (Exception e)
            {
                MmLog.Detail("planet appearance not readable: " + e.Message);
            }

            try
            {
                s.atmoKm = s.planet.HasAtmospherePhysics ? s.planet.AtmosphereHeightPhysics / 1000.0 : 0.0;
            }
            catch
            {
                s.atmoKm = 0.0;
            }

            s.shipKm = ToKm(s.location.position);
            s.velocity = new Vector2((float)s.location.velocity.x, (float)s.location.velocity.y);
            s.shipAngleDegrees = s.location.position.AngleDegrees;
            s.shipAngleRadians = s.location.position.AngleRadians;

            // ---- trajectory ------------------------------------------------------------------
            if (s.rocket != null && s.rocket.physics != null)
                s.physics = s.rocket.physics;

            s.trajectory = ReadTrajectory(s.physics);

            if (s.trajectory != null && s.trajectory.paths != null && s.trajectory.paths.Count > 0)
            {
                s.pathCount = s.trajectory.paths.Count;

                for (int i = 0; i < s.trajectory.paths.Count && i < 4; i++)
                {
                    I_Path path = s.trajectory.paths[i];
                    string name = path == null ? "null" : path.GetType().Name;
                    if (i > 0)
                        s.pathTypes += ", ";
                    s.pathTypes += name;
                }

                s.firstPathType = s.trajectory.paths[0] == null
                    ? "null"
                    : s.trajectory.paths[0].GetType().Name;

                // The first path is not always an Orbit - a trajectory can start with a marker
                // section of another kind - so the orbit is taken from the first path that really is
                // one, preferring the planet the vehicle is at.
                for (int i = 0; i < s.trajectory.paths.Count; i++)
                {
                    Orbit candidate = s.trajectory.paths[i] as Orbit;
                    if (candidate == null)
                        continue;

                    if (s.orbit == null)
                        s.orbit = candidate;

                    if (candidate.Planet == s.planet)
                    {
                        s.orbit = candidate;
                        break;
                    }
                }
            }

            if (s.orbit != null)
            {
                s.hasOrbit = true;
                s.apoapsisM = s.orbit.apoapsis;
                s.periapsisM = s.orbit.periapsis;
                s.ecc = s.orbit.ecc;
                s.pathType = s.orbit.PathType.ToString();

                // "In orbit" means the vehicle is on a closed path that is not going to come back
                // through the ground. The atmosphere is deliberately NOT part of this: a low orbit
                // that skims the atmosphere is still an orbit the player is flying, and requiring the
                // periapsis to clear the atmosphere kept the map centred on the vehicle in exactly
                // that case. The game's own test (Physics.InOrbit) is asked as well, so that whatever
                // it considers an orbit counts too.
                s.surfaceM = s.planet.Radius;
                s.inOrbitGeometric = s.orbit.ecc < 1.0 &&
                                     MmSnapshot.IsFinite(s.orbit.periapsis) &&
                                     s.orbit.periapsis > s.surfaceM;
                s.inOrbit = s.inOrbitGeometric;

                ReadOrbitCentre(s);
            }

            SampleArc(s);

            // Whether the game has the vehicle on rails (time warp) or in real time physics decides
            // which kind of trajectory it hands over, so it is part of every report.
            try
            {
                WorldTime time = WorldTime.main;
                if (time != null)
                {
                    s.warpIndex = time.timewarpIndex;
                    s.warpSpeed = time.timewarpSpeed;
                    s.realtimePhysics = MmLib.IsSet(time.realtimePhysics) && time.realtimePhysics.Value;
                }
            }
            catch (Exception e)
            {
                MmLog.Detail("the time warp state could not be read: " + e.Message);
            }

            s.valid = true;
        }

        /// <summary>
        /// The middle of the apoapsis point and the periapsis point, which is the centre of the
        /// ellipse the vehicle is flying. Only a closed orbit has both points on it (and the game's
        /// own maths is asked for them by true anomaly, the same way the two markers are placed), so
        /// anything else leaves the flag false and the map falls back to the planet's centre.
        /// </summary>
        private static void ReadOrbitCentre(MmSnapshot s)
        {
            try
            {
                if (s.orbit.ecc >= 1.0 || !MmSnapshot.IsFinite(s.orbit.apoapsis) ||
                    !MmSnapshot.IsFinite(s.orbit.periapsis))
                    return;

                Double2 apoapsis = s.orbit.GetPositionAtTrueAnomaly(Math.PI);
                Double2 periapsis = s.orbit.GetPositionAtTrueAnomaly(0.0);

                if (!MmSnapshot.IsFinite(apoapsis.x) || !MmSnapshot.IsFinite(apoapsis.y) ||
                    !MmSnapshot.IsFinite(periapsis.x) || !MmSnapshot.IsFinite(periapsis.y))
                    return;

                s.orbitCentreKm = new Vector2((float)((apoapsis.x + periapsis.x) * 0.0005),
                                              (float)((apoapsis.y + periapsis.y) * 0.0005));
                s.hasOrbitCentre = true;

                // The two points are a full major axis apart, in metres. Taking half of that distance
                // and turning it into kilometres is a single halving, which is why 0.0005 is used
                // here and NOT multiplied by 0.5 as well - that mistake made the map zoom in to half
                // the size it should have been, which put both apsis points outside the window.
                double dx = (apoapsis.x - periapsis.x) * 0.0005;
                double dy = (apoapsis.y - periapsis.y) * 0.0005;
                double semiMajor = Math.Sqrt(dx * dx + dy * dy);
                if (MmSnapshot.IsFinite(semiMajor) && semiMajor > 0.0)
                    s.orbitSemiMajorKm = (float)semiMajor;
                else
                    s.hasOrbitCentre = false;
            }
            catch (Exception e)
            {
                MmLog.Detail("the centre of the orbit could not be worked out: " + e.Message);
            }
        }

        /// <summary>
        /// The vehicle's trajectory, read the way the game's own map reads it.
        ///
        /// The "trajectory" field is only filled while the vehicle is on rails (time warp). While the
        /// active vehicle is being simulated in real time - which is the normal case at 1x, and the
        /// case this minimap spends all of its time in - Physics.GetTrajectory() computes the
        /// trajectory from the current location instead, and the field is left empty. Reading the
        /// field there gave an empty trajectory, hence no orbit, no apsides and a map that never
        /// moved to the planet.
        ///
        /// GetTrajectory caches its result and only recomputes when the position, the velocity or the
        /// time changed, so calling it once per frame is what the game itself does.
        /// </summary>
        private static Trajectory ReadTrajectory(Physics physics)
        {
            if (MmLib.IsNull(physics))
                return null;

            try
            {
                Trajectory computed = physics.GetTrajectory();
                if (computed != null)
                    return computed;
            }
            catch (Exception e)
            {
                if (!trajectoryReported)
                {
                    trajectoryReported = true;
                    MmLog.Warn("the vehicle's trajectory could not be read, falling back to the field: " + e);
                }
            }

            return physics.trajectory;
        }

        private static bool trajectoryReported;

        /// <summary>
        /// Walks the current trajectory and collects the part of it that stays at this planet, in
        /// planet relative kilometres. The last point is the impact point of a suborbital arc, which
        /// is what the "before orbit" zoom is built from.
        /// </summary>
        private static void SampleArc(MmSnapshot s)
        {
            if (s.trajectory == null || s.trajectory.paths == null)
                return;

            double now = 0.0;
            try
            {
                now = WorldTime.main.worldTime;
            }
            catch
            {
                // keep 0: the walk below then simply uses the path's own start time
            }

            try
            {
                for (int i = 0; i < s.trajectory.paths.Count; i++)
                {
                    Orbit path = s.trajectory.paths[i] as Orbit;
                    if (path == null)
                        continue;

                    if (path.Planet != s.planet)
                        break;

                    double start = path.PathStartTime;
                    double end = path.PathEndTime;
                    if (MmSnapshot.IsFinite(now) && now > start)
                        start = now;
                    if (!MmSnapshot.IsFinite(start) || !MmSnapshot.IsFinite(end) || end <= start)
                        continue;

                    double a0 = path.GetTrueAnomaly(start);
                    double a1 = path.GetTrueAnomaly(end);
                    if (!MmSnapshot.IsFinite(a0) || !MmSnapshot.IsFinite(a1))
                        continue;

                    Vector3[] points = path.GetPoints(a0, a1, ArcSegments, 0.001);
                    if (points != null && points.Length > 0)
                    {
                        for (int p = 0; p < points.Length; p++)
                        {
                            Vector3 point = points[p];
                            if (float.IsNaN(point.x) || float.IsNaN(point.y) ||
                                float.IsInfinity(point.x) || float.IsInfinity(point.y))
                            {
                                continue;
                            }
                            s.arc.Add(point);
                        }
                    }

                    // Only the current planet's first path is of interest.
                    break;
                }
            }
            catch (Exception e)
            {
                MmLog.Detail("the trajectory could not be sampled: " + e.Message);
            }

            if (s.arc.Count > 0)
            {
                Vector3 last = s.arc[s.arc.Count - 1];
                s.arcEndKm = new Vector2(last.x, last.y);
                s.hasArcEnd = true;
            }
        }

        public static Vector2 ToKm(Double2 value)
        {
            return new Vector2((float)(value.x / 1000.0), (float)(value.y / 1000.0));
        }

        public static Vector3 ToKm3(Double2 value)
        {
            return new Vector3((float)(value.x / 1000.0), (float)(value.y / 1000.0), 0f);
        }
    }
}

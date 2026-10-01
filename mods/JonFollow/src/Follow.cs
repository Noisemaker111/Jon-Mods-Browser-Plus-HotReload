using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace JonFollow
{
    public sealed class FollowState
    {
        public int TargetId = -1;
        public string Status;
        string targetName;
        readonly Queue<Vector3> trail = new Queue<Vector3>();
        readonly GridPath path = new GridPath();
        Vector3 lastTrail;
        bool haveTrail, sprinting, replanRequested;
        float nextPlan, trailUntil, progressTime, leaderSpeed, leaderTime;
        Vector3 progressPosition, leaderSample;
        EntityVehicle controlledVehicle;
        EntityPlayerLocal driver;
        bool previousControllerInput;
        public void Stop(string reason)
        {
            // Release the last physics input immediately, even when a menu,
            // disconnect or hot reload prevents the next native input frame.
            if (controlledVehicle != null && driver != null && controlledVehicle.AttachedMainEntity == driver)
            {
                var input = driver.playerUI?.playerInput?.VehicleActions;
                var movement = controlledVehicle.movementInput;
                bool menu = driver.windowManager.IsModalWindowOpen();
                movement.moveForward = input == null || menu ? 0 : input.Move.Y;
                movement.moveStrafe = input == null || menu ? 0 : input.Move.X;
                movement.jump = menu || (input != null && input.Brake.IsPressed);
                movement.down = !menu && input != null && input.Hop.IsPressed;
                movement.running = !menu && driver.movementInput.running;
                movement.lastInputController = previousControllerInput;
            }
            controlledVehicle = null; driver = null;
            if (TargetId >= 0) Telemetry.End(reason);
            TargetId = -1; trail.Clear(); haveTrail = false; path.Clear(); sprinting = false; leaderSpeed = 0; leaderTime = 0; Status = reason;
        }
        public void Start(EntityPlayer target)
        {
            Stop(null); TargetId = target.entityId; targetName = target.PlayerDisplayName; Status = "Following " + targetName;
            var player = GameManager.Instance.World.GetPrimaryPlayer();
            if (player != null) Telemetry.Begin(player, target);
        }
        // Follow lasts until the player acts or the leader pulls out of the
        // tether. A friend who is down nearby is waited for.
        public EntityPlayer Validate(EntityPlayerLocal player, out bool waiting)
        {
            waiting = false;
            if (TargetId < 0) return null;
            var world = GameManager.Instance.World;
            var target = world.GetEntity(TargetId) as EntityPlayer;
            var input = player.playerUI.playerInput;
            bool menu = player.windowManager.IsModalWindowOpen();
            var vehicle = player.AttachedToEntity as EntityVehicle;
            bool manual = !menu && (input.Move.Value.sqrMagnitude > 0.04f || input.Jump.IsPressed ||
                input.Crouch.IsPressed || input.Primary.IsPressed || input.Secondary.IsPressed || input.Activate.IsPressed ||
                (vehicle != null && (input.VehicleActions.Move.Value.sqrMagnitude > 0.04f || input.VehicleActions.Brake.IsPressed || input.VehicleActions.Hop.IsPressed)));
            bool member = player.Party != null && (target == null || player.Party.ContainsMember(target));
            float gap = target == null ? float.MaxValue : Vector3.Distance(player.position, target.position);
            if (Rules.CancelFollow(manual, player.IsDead(), member, gap))
            { Stop(manual ? "Follow stopped" : player.IsDead() ? "Follow stopped: you died" : !member ? "Follow stopped: not in your party" : "Follow stopped: " + targetName + " got too far ahead"); return null; }
            if (target.IsDead()) { waiting = true; Status = targetName + " is down · waiting"; return null; }
            SampleLeader(target.AttachedToEntity != null ? target.AttachedToEntity.position : target.position);
            return target;
        }
        // Leader speed from replicated positions; remote rigidbodies report none.
        void SampleLeader(Vector3 position)
        {
            float now = Time.time;
            if (leaderTime <= 0) { leaderSample = position; leaderTime = now; return; }
            float dt = now - leaderTime;
            if (dt < 0.25f) return;
            var moved = position - leaderSample; moved.y = 0;
            leaderSpeed = Mathf.Lerp(leaderSpeed, moved.magnitude / dt, 0.6f);
            leaderSample = position; leaderTime = now;
        }
        Vector3 Destination(EntityPlayer target, Vector3 current, float arrival)
        {
            Vector3 next = target.AttachedToEntity != null ? target.AttachedToEntity.position : target.position;
            if (!haveTrail || Vector3.Distance(lastTrail, next) > 2)
            { trail.Enqueue(next); lastTrail = next; haveTrail = true; }
            while (trail.Count > 1 && Vector3.Distance(current, trail.Peek()) < arrival) trail.Dequeue();
            return trail.Count > 1 ? trail.Peek() : next;
        }
        // The planned route around walls and ledges; the leader's own trail
        // when the blocks between us are not loaded or no route exists.
        Vector3 NextPoint(EntityPlayerLocal player, EntityPlayer target, Vector3 leader)
        {
            var trailPoint = Destination(target, player.position, 1.5f);
            path.Advance(player.position);
            if (path.Searching)
            {
                var found = path.Continue();
                if (found != null) Telemetry.Route(player.position, leader, found == true, path.LastExpanded, path.Points, GridPath.Walls(GameManager.Instance.World, player.position, leader));
                // No route: walk the leader's own trail and leave the search
                // alone for a while instead of re-running it every frame.
                if (found == false) { trailUntil = Time.time + 3f; nextPlan = Time.time + 3f; }
            }
            else if ((replanRequested || !path.Has || Vector3.Distance(path.Goal, leader) > 3f) && Time.time >= nextPlan)
            {
                nextPlan = Time.time + 0.5f; replanRequested = false;
                if (!path.Begin(GameManager.Instance.World, player.position, leader)) { trailUntil = Time.time + 2f; Telemetry.Note("no standable start or goal cell"); }
            }
            return path.Has && Time.time >= trailUntil ? path.Steer(player.position, point => Straight(player, point)) : trailPoint;
        }
        static void Idle(MovementInput input) { input.moveForward = input.moveStrafe = 0; input.running = false; }
        // Nothing solid between the follower's knees/chest and the point.
        static bool Straight(Entity mover, Vector3 point)
        {
            int solid = SolidMask(mover);
            Vector3 from = mover.position - Origin.position, to = point - Origin.position;
            foreach (float h in new[] { 0.6f, 1.4f })
                foreach (var hit in Physics.RaycastAll(from + Vector3.up * h, (to - from).normalized, Vector3.Distance(from, to), solid, QueryTriggerInteraction.Ignore))
                {
                    var entity = hit.collider.GetComponentInParent<Entity>();
                    if (entity == null && hit.normal.y < 0.65f) return false;
                }
            return true;
        }
        // Turns the view the way mouse look does, so walking, sprinting,
        // stamina and animation all follow the native forward-movement rules.
        static float Face(EntityPlayerLocal player, Vector3 direction, MovementInput input)
        {
            float yaw = !player.bFirstPersonView && player.CameraRelativeMovement && !player.vp_FPCamera.Locked3rdPerson
                ? player.playerCamera.transform.eulerAngles.y : player.rotation.y;
            float angle = Mathf.DeltaAngle(yaw, Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg);
            // Damped, rate-limited turning with a small dead zone: no
            // overshoot wobble from the one-frame lag in the player's yaw.
            if (Mathf.Abs(angle) < 2f) return angle;
            float limit = 300f * Time.deltaTime;
            input.rotation.y += Mathf.Clamp(angle * Mathf.Min(1f, 6f * Time.deltaTime), -limit, limit);
            return angle;
        }
        // Called by native movement/physics operations. No timer or state polling loop.
        public void Walk(EntityPlayerLocal player)
        {
            if (TargetId < 0 || player.AttachedToEntity != null) return;
            var target = Validate(player, out bool waiting);
            var input = player.movementInput;
            if (target == null) { if (waiting) Idle(input); return; }
            if (player.windowManager.IsModalWindowOpen()) { Idle(input); return; }
            Vector3 leader = target.AttachedToEntity != null ? target.AttachedToEntity.position : target.position;
            float gap = Vector3.Distance(player.position, leader);
            Status = "Following " + target.PlayerDisplayName;
            if (gap <= 2.5f)
            {
                Idle(input); path.Clear(); sprinting = false;
                var toward = leader - player.position; toward.y = 0;
                float facing = toward.sqrMagnitude > 0.01f ? Face(player, toward, input) : 0;
                progressTime = Time.time; progressPosition = player.position;
                Telemetry.Sample(player, leader, leader, "arrived", input, facing, leaderSpeed, "-");
                return;
            }
            var next = NextPoint(player, target, leader);
            var delta = next - player.position;
            float rise = delta.y; delta.y = 0;
            if (delta.sqrMagnitude < 0.0025f) { Idle(input); return; }
            var ahead = Probe(player, delta.normalized, 1.6f);
            if (ahead == Ahead.Wall || (ahead == Ahead.Drop && rise > -0.9f))
            {
                Idle(input); replanRequested = true; Status = "Following " + target.PlayerDisplayName + " · finding a way";
                Telemetry.Sample(player, leader, next, "blocked", input, 0, leaderSpeed, ahead.ToString());
                return;
            }
            float turn = Face(player, delta, input);
            sprinting = Rules.Sprint(sprinting, leaderSpeed, gap);
            // Turn on the spot first: sprint input is normalised to full
            // speed, so any forward input while sharply turned runs a circle.
            float sharp = Mathf.Abs(turn);
            input.moveForward = sharp > 70f ? 0f : sharp > 35f ? 0.5f : 1f;
            input.moveStrafe = 0;
            input.running = sprinting && sharp <= 35f;
            // A ledge below chest height is climbed the way a player does it.
            input.jump = ahead == Ahead.Step && player.onGround;
            if (Vector3.Distance(player.position, progressPosition) > 0.6f) { progressPosition = player.position; progressTime = Time.time; }
            else if (Time.time - progressTime > 1.5f) { replanRequested = true; input.jump = player.onGround; progressTime = Time.time; Telemetry.Note("stuck: no progress for 1.5 s"); }
            Telemetry.Sample(player, leader, next, path.Has && Time.time >= trailUntil ? (sprinting ? "run" : "walk") : "trail", input, turn, leaderSpeed, ahead.ToString());
        }
        public void Drive(EntityVehicle vehicle, EntityPlayerLocal player)
        {
            if (vehicle.AttachedMainEntity != player || player.AttachedToEntity != vehicle) return;
            if (controlledVehicle != vehicle)
            {
                controlledVehicle = vehicle; driver = player;
                previousControllerInput = vehicle.movementInput.lastInputController;
            }
            var target = Validate(player, out bool waiting);
            if (target == null)
            {
                if (waiting) { vehicle.movementInput.moveForward = 0; vehicle.movementInput.jump = true; vehicle.movementInput.lastInputController = true; }
                return;
            }
            if (vehicle is EntityVGyroCopter || vehicle.wheels == null || vehicle.wheels.Length == 0)
            { Stop("Follow stopped: ground vehicles only"); return; }
            var position = vehicle.position;
            var delta = Destination(target, position, 4f) - position;
            delta.y = 0;
            var targetEntity = target.AttachedToEntity != null ? target.AttachedToEntity : target;
            float speed = vehicle.vehicleRB == null ? 0 : vehicle.vehicleRB.velocity.magnitude;
            var targetVehicle = targetEntity as EntityVehicle;
            float targetSpeed = leaderSpeed;
            float heading = Vector3.SignedAngle(vehicle.PhysicsTransform.forward, delta, Vector3.up);
            bool blocked = player.windowManager.IsModalWindowOpen() || Probe(vehicle, delta.normalized, 4 + speed * speed / 6) is var ahead && (ahead == Ahead.Wall || ahead == Ahead.Drop);
            var controls = Rules.VehicleControl(Vector3.Distance(position, targetEntity.position), heading, speed, targetSpeed, blocked);
            vehicle.movementInput.moveForward = controls.Forward;
            vehicle.movementInput.moveStrafe = controls.Steer;
            vehicle.movementInput.lastInputController = true;
            vehicle.movementInput.jump = controls.Brake;
            vehicle.movementInput.running = false;
            Status = blocked ? "Follow braking: obstruction or drop" : "Following " + target.PlayerDisplayName + " (vehicle)";
        }
        // Only layers the mover's own physics collides with: grass and other
        // walk-through colliders are neither walls nor floors.
        static int SolidMask(Entity mover)
        {
            var body = mover.PhysicsTransform != null ? mover.PhysicsTransform : mover.transform;
            int layer = body.gameObject.layer, mask = 0;
            for (int i = 0; i < 32; i++) if (!Physics.GetIgnoreLayerCollision(layer, i)) mask |= 1 << i;
            return mask;
        }
        enum Ahead { Clear, Step, Wall, Drop }
        // A wall blocks at knee and chest height; a knee-only hit is a ledge
        // or steep terrain the leader just crossed.
        static Ahead Probe(Entity mover, Vector3 direction, float ahead)
        {
            if (direction.sqrMagnitude < 0.001f) return Ahead.Clear;
            Vector3 feet = mover.position - Origin.position;
            int solid = SolidMask(mover);
            bool knee = Hits(mover, feet + Vector3.up * 0.8f, direction, ahead, solid);
            if (knee && Hits(mover, feet + Vector3.up * 1.6f, direction, ahead, solid)) return Ahead.Wall;
            foreach (var hit in Physics.RaycastAll(feet + direction * Math.Min(ahead, 6) + Vector3.up * 1.5f,
                         Vector3.down, 3.5f, solid, QueryTriggerInteraction.Ignore))
                if (hit.collider.GetComponentInParent<Entity>() == null && hit.normal.y > 0.45f) return knee ? Ahead.Step : Ahead.Clear;
            return Ahead.Drop;
        }
        static bool Hits(Entity mover, Vector3 from, Vector3 direction, float ahead, int solid)
        {
            foreach (var hit in Physics.SphereCastAll(from, 0.3f, direction, ahead, solid, QueryTriggerInteraction.Ignore))
            {
                var entity = hit.collider.GetComponentInParent<Entity>();
                if (entity == mover || (entity != null && entity.AttachedToEntity == mover)) continue;
                // Floor slopes should not be treated as a wall.
                if (hit.normal.y < 0.65f) return true;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(EntityPlayerLocal), "MoveByInput")]
    public static class WalkFollow
    {
        public static void Prefix(EntityPlayerLocal __instance) { Runtime.Instance?.Follow.Walk(__instance); }
    }
    [HarmonyPatch(typeof(EntityVehicle), "PhysicsFixedUpdate")]
    public static class FollowSteeringMode
    {
        public static void Prefix(EntityVehicle __instance, out bool __state)
        {
            __state = EntityVehicle.isTurnTowardsLook;
            var runtime = Runtime.Instance;
            if (runtime == null || runtime.Follow.TargetId < 0) return;
            var player = __instance.GetAttachedPlayerLocal();
            if (player != null && __instance.AttachedMainEntity == player)
            {
                runtime.Follow.Drive(__instance, player);
                if (runtime.Follow.TargetId >= 0) EntityVehicle.isTurnTowardsLook = false;
            }
        }
        public static Exception Finalizer(bool __state, Exception __exception)
        { EntityVehicle.isTurnTowardsLook = __state; return __exception; }
    }
}

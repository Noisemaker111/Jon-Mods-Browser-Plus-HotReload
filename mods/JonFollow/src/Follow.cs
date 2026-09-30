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
        readonly Queue<Vector3> trail = new Queue<Vector3>();
        Vector3 lastTrail;
        bool haveTrail;
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
            TargetId = -1; trail.Clear(); haveTrail = false; Status = reason;
        }
        public void Start(EntityPlayer target)
        {
            Stop(null); TargetId = target.entityId; Status = "Following " + target.PlayerDisplayName;
        }
        public EntityPlayer Validate(EntityPlayerLocal player)
        {
            if (TargetId < 0) return null;
            var world = GameManager.Instance.World;
            var target = world.GetEntity(TargetId) as EntityPlayer;
            if (target == null) { Stop("Follow stopped: friend disconnected or left range"); return null; }
            var input = player.playerUI.playerInput;
            bool menu = player.windowManager.IsModalWindowOpen();
            var vehicle = player.AttachedToEntity as EntityVehicle;
            bool manual = !menu && (input.Move.Value.sqrMagnitude > 0.04f || input.Jump.IsPressed ||
                input.Crouch.IsPressed || input.Primary.IsPressed || input.Secondary.IsPressed || input.Activate.IsPressed ||
                (vehicle != null && (input.VehicleActions.Move.Value.sqrMagnitude > 0.04f || input.VehicleActions.Brake.IsPressed || input.VehicleActions.Hop.IsPressed)));
            if (Rules.CancelFollow(manual, player.IsDead(), target.IsDead(),
                    player.Party != null && player.Party.ContainsMember(target), Vector3.Distance(player.position, target.position)))
            { Stop("Follow stopped"); return null; }
            return target;
        }
        Vector3 Destination(EntityPlayer target, Vector3 current, float arrival)
        {
            Vector3 next = target.AttachedToEntity != null ? target.AttachedToEntity.position : target.position;
            if (!haveTrail || Vector3.Distance(lastTrail, next) > 2)
            { trail.Enqueue(next); lastTrail = next; haveTrail = true; }
            while (trail.Count > 1 && Vector3.Distance(current, trail.Peek()) < arrival) trail.Dequeue();
            return trail.Count > 1 ? trail.Peek() : next;
        }
        // Called by native movement/physics operations. No timer or state polling loop.
        public void Walk(EntityPlayerLocal player)
        {
            var target = Validate(player);
            if (target == null || player.AttachedToEntity != null) return;
            var input = player.movementInput;
            if (player.windowManager.IsModalWindowOpen()) { input.moveForward = input.moveStrafe = 0; input.running = false; return; }
            var delta = Destination(target, player.position, 1.5f) - player.position;
            float gap = Vector3.Distance(player.position, target.position);
            delta.y = 0;
            bool blocked = Blocked(player, delta.normalized, 2f);
            if (gap <= 3 || blocked || delta.sqrMagnitude < 0.4f)
            { input.moveForward = input.moveStrafe = 0; input.running = false; Status = blocked ? "Follow paused: obstruction or drop" : "Following " + target.PlayerDisplayName; return; }
            float yaw = !player.bFirstPersonView && player.CameraRelativeMovement && !player.vp_FPCamera.Locked3rdPerson
                ? player.playerCamera.transform.eulerAngles.y : player.rotation.y;
            var local = Quaternion.Inverse(Quaternion.Euler(0, yaw, 0)) * delta.normalized;
            input.moveForward = local.z;
            input.moveStrafe = local.x;
            input.running = gap > 9 && !player.bExhausted;
            Status = "Following " + target.PlayerDisplayName;
        }
        public void Drive(EntityVehicle vehicle, EntityPlayerLocal player)
        {
            if (vehicle.AttachedMainEntity != player || player.AttachedToEntity != vehicle) return;
            if (controlledVehicle != vehicle)
            {
                controlledVehicle = vehicle; driver = player;
                previousControllerInput = vehicle.movementInput.lastInputController;
            }
            var target = Validate(player);
            if (target == null) return;
            if (vehicle is EntityVGyroCopter || vehicle.wheels == null || vehicle.wheels.Length == 0)
            { Stop("Follow stopped: ground vehicles only"); return; }
            var position = vehicle.position;
            var delta = Destination(target, position, 4f) - position;
            delta.y = 0;
            var targetEntity = target.AttachedToEntity != null ? target.AttachedToEntity : target;
            float speed = vehicle.vehicleRB == null ? 0 : vehicle.vehicleRB.velocity.magnitude;
            var targetVehicle = targetEntity as EntityVehicle;
            float targetSpeed = targetVehicle != null && targetVehicle.vehicleRB != null ? targetVehicle.vehicleRB.velocity.magnitude : 0;
            float heading = Vector3.SignedAngle(vehicle.PhysicsTransform.forward, delta, Vector3.up);
            bool blocked = player.windowManager.IsModalWindowOpen() || Blocked(vehicle, delta.normalized, 4 + speed * speed / 6);
            var controls = Rules.VehicleControl(Vector3.Distance(position, targetEntity.position), heading, speed, targetSpeed, blocked);
            vehicle.movementInput.moveForward = controls.Forward;
            vehicle.movementInput.moveStrafe = controls.Steer;
            vehicle.movementInput.lastInputController = true;
            vehicle.movementInput.jump = controls.Brake;
            vehicle.movementInput.running = false;
            Status = blocked ? "Follow braking: obstruction or drop" : "Following " + target.PlayerDisplayName + " (vehicle)";
        }
        static bool Blocked(Entity mover, Vector3 direction, float ahead)
        {
            if (direction.sqrMagnitude < 0.001f) return false;
            Vector3 feet = mover.position - Origin.position;
            foreach (var hit in Physics.SphereCastAll(feet + Vector3.up * 0.8f, 0.3f, direction, ahead, ~0, QueryTriggerInteraction.Ignore))
            {
                var entity = hit.collider.GetComponentInParent<Entity>();
                if (entity == mover || (entity != null && entity.AttachedToEntity == mover)) continue;
                // Floor slopes should not be treated as a wall.
                if (hit.normal.y < 0.65f) return true;
            }
            foreach (var hit in Physics.RaycastAll(feet + direction * Math.Min(ahead, 6) + Vector3.up * 1.5f,
                         Vector3.down, 3.5f, ~0, QueryTriggerInteraction.Ignore))
                if (hit.collider.GetComponentInParent<Entity>() == null && hit.normal.y > 0.45f) return false;
            return true;
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

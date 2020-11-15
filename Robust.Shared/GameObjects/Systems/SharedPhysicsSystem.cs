﻿using System;
using System.Collections.Generic;
using System.Linq;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects.Components;
using Robust.Shared.Interfaces.Map;
using Robust.Shared.Interfaces.Physics;
using Robust.Shared.Interfaces.Random;
using Robust.Shared.Interfaces.Timing;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics;
using DependencyAttribute = Robust.Shared.IoC.DependencyAttribute;

namespace Robust.Shared.GameObjects.Systems
{
    public abstract class SharedPhysicsSystem : EntitySystem
    {
        [Dependency] private readonly ITileDefinitionManager _tileDefinitionManager = default!;
        [Dependency] private readonly IMapManager _mapManager = default!;
        [Dependency] private readonly IPhysicsManager _physicsManager = default!;
        [Dependency] private readonly IRobustRandom _random = default!;

        private const float Epsilon = 1.0e-6f;

        protected readonly List<Manifold> CollisionCache = new List<Manifold>();

        /// <summary>
        ///     Physics objects that are awake and usable for world simulation.
        /// </summary>
        private readonly HashSet<IPhysicsComponent> _awakeBodies = new HashSet<IPhysicsComponent>();

        /// <summary>
        ///     Physics objects that are awake and predicted and usable for world simulation.
        /// </summary>
        private readonly HashSet<IPhysicsComponent> _predictedAwakeBodies = new HashSet<IPhysicsComponent>();

        /// <summary>
        ///     VirtualControllers on applicable <see cref="IPhysicsComponent"/>s
        /// </summary>
        private Dictionary<IPhysicsComponent, IEnumerable<VirtualController>> _controllers =
            new Dictionary<IPhysicsComponent, IEnumerable<VirtualController>>();

        // We'll defer changes to IPhysicsComponent until each step is done.
        private readonly List<IPhysicsComponent> _queuedDeletions = new List<IPhysicsComponent>();
        private readonly List<IPhysicsComponent> _queuedUpdates = new List<IPhysicsComponent>();

        /// <summary>
        ///     Updates to EntityTree etc. that are deferred until the end of physics.
        /// </summary>
        private readonly HashSet<IPhysicsComponent> _deferredUpdates = new HashSet<IPhysicsComponent>();

        // CVars aren't replicated to client (yet) so not using a cvar server-side for this.
        private float _speedLimit = 30.0f;

        #region parameters
        /// <summary>
        ///     The maximum amount of object overlap we can correct in a single tick.
        /// </summary>
        private float _maxPositionCorrect = 0.4f;

        /// <summary>
        ///     Percentage of how much overlap we can correct per tick.
        /// </summary>
        private float _positionCorrectPercent = 0.2f;

        /// <summary>
        ///     Maximum amount of overlap allowed for 2 bodies.
        /// </summary>
        private float _positionAllowance = 1 / 128f;

        private byte _positionSolverIterations = 1;
        private byte _velocitySolverIterations = 4;
        #endregion

        /*
         * ISLANDS:
         * A few engines I've seen use island solvers where after they construct the manifolds
         * They'll group all of the collisions together, so for example if A<->B and A<->C are colliding then
         * it'd form one island of those 2 manifolds. From this you can solve these in parallel (at least
         * probably not the behavior step given you'll likely get multithreading issues).
         */

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<PhysicsUpdateMessage>(HandlePhysicsUpdateMessage);
        }

        private void HandlePhysicsUpdateMessage(PhysicsUpdateMessage message)
        {
            if (message.Component.Deleted || !message.Component.Awake)
            {
                _queuedDeletions.Add(message.Component);
            }
            else
            {
                _queuedUpdates.Add(message.Component);
            }
        }

        /// <summary>
        ///     Process the changes to cached <see cref="IPhysicsComponent"/>s
        /// </summary>
        private void ProcessQueue()
        {
            // At this stage only the dynamictree cares about asleep bodies
            // Implicitly awake bodies so don't need to check .Awake again
            // Controllers should wake their body up (inside)
            foreach (var physics in _queuedUpdates)
            {
                if (physics.Predict)
                    _predictedAwakeBodies.Add(physics);

                _awakeBodies.Add(physics);

                if (physics.Controllers.Count > 0 && !_controllers.ContainsKey(physics))
                    _controllers.Add(physics, physics.Controllers.Values);

            }

            _queuedUpdates.Clear();

            foreach (var physics in _queuedDeletions)
            {
                _awakeBodies.Remove(physics);
                _predictedAwakeBodies.Remove(physics);
                _controllers.Remove(physics);
            }

            _queuedDeletions.Clear();
        }

        /// <summary>
        ///     Simulates the physical world for a given amount of time.
        /// </summary>
        /// <param name="frameTime">Delta Time in seconds of how long to simulate the world.</param>
        /// <param name="prediction">Should only predicted entities be considered in this simulation step?</param>
        protected void SimulateWorld(float frameTime, bool prediction)
        {
            // The reason we clear this at the start of the simulation and not the end is because the client needs
            // to check what needs to be predicted next simulation.
            CollisionCache.Clear();

            var simulatedBodies = prediction ? _predictedAwakeBodies : _awakeBodies;

            ProcessQueue();

            IntegrateForces(simulatedBodies, prediction, frameTime);

            // Calculate collisions and store them in the cache
            // Don't use simulated because this is behavior stuff which WILL mispredict.
            BuildManifolds(_awakeBodies);

            VelocitySolver();

            ProcessFriction(simulatedBodies, frameTime);

            CollisionBehaviors();

            // Remove all entities that were deleted during collision handling
            ProcessQueue();

            foreach (var (_, controllers) in _controllers)
            {
                foreach (var controller in controllers)
                {
                    controller.UpdateAfterProcessing();
                }
            }

            // Remove all entities that were deleted due to the controller
            ProcessQueue();

            PositionSolver();

            var frameSpeedLimit = _speedLimit * frameTime;

            foreach (var physics in simulatedBodies)
            {
                if (physics.LinearVelocity != Vector2.Zero)
                {
                    UpdatePosition(physics, frameTime, frameSpeedLimit);
                }
            }

            // As we also defer the updates for the _collisionCache we need to update all entities
            foreach (var physics in _deferredUpdates)
            {
                var transform = physics.Owner.Transform;
                transform.DeferUpdates = false;
                transform.RunPhysicsDeferred();
            }

            // Cleanup
            _deferredUpdates.Clear();
        }

        private void IntegrateForces(IEnumerable<IPhysicsComponent> physicsComponents, bool prediction, float frameTime)
        {
            foreach (var body in physicsComponents)
            {
                // running prediction updates will not cause a body to go to sleep.
                if(!prediction)
                    body.SleepAccumulator++;

                // if the body cannot move, nothing to do here
                if(!body.CanMove())
                    continue;

                // TODO: We should store impulse instead in case the mass changes

                // TODO: Something fucky is going on with LinearVelocity, maybe try storing the frametime adjusted amount? IDFK

                var oldVelocity = body.WarmStart && body.LinearVelocity != Vector2.Zero ? body.LinearVelocity : Vector2.Zero;
                var deltaVelocity = Vector2.Zero;

                // See https://www.youtube.com/watch?v=SHinxAhv1ZE for an overall explanation

                // Integration
                foreach (var controller in body.Controllers.Values)
                {
                    controller.UpdateBeforeProcessing();
                    deltaVelocity += controller.LinearVelocity * frameTime;
                    deltaVelocity += controller.Impulse * body.InvMass * frameTime;
                    controller.Impulse = Vector2.Zero;
                    controller.LinearVelocity = Vector2.Zero;
                }

                // TODO https://youtu.be/SHinxAhv1ZE?t=1937
                // Should stop the "springing" squishing

                // Round it off
                var newVelocity = oldVelocity + deltaVelocity;

                if (newVelocity.LengthSquared < 0.00001f)
                {
                    body.LinearVelocity = Vector2.Zero;
                }
                else
                {
                    body.LinearVelocity = newVelocity;
                }

                // forces are instantaneous, so these properties are cleared
                // once integrated. If you want to apply a continuous force,
                // it has to be re-applied every tick.
                body.Force = Vector2.Zero;
                body.Torque = 0f;
            }
        }

        /// <summary>
        ///     Run through each active body and get their collisions.
        ///     Stores them into the _collisionCache manifolds.
        /// </summary>
        /// <param name="awakeBodies"></param>
        private void BuildManifolds(IEnumerable<IPhysicsComponent> awakeBodies)
        {
            var combinations = new HashSet<(EntityUid, EntityUid)>();
            foreach (var aPhysics in awakeBodies)
            {
                foreach (var b in _physicsManager.GetCollidingEntities(aPhysics, aPhysics.LinearVelocity, false))
                {
                    var aUid = aPhysics.Entity.Uid;
                    var bUid = b.Uid;

                    if (bUid.CompareTo(aUid) > 0)
                    {
                        var tmpUid = bUid;
                        bUid = aUid;
                        aUid = tmpUid;
                    }

                    if (!combinations.Add((aUid, bUid)))
                    {
                        continue;
                    }

                    var bPhysics = b.GetComponent<IPhysicsComponent>();
                    CollisionCache.Add(new Manifold(aPhysics, bPhysics, aPhysics.Hard && bPhysics.Hard));
                }
            }
        }

        private void CollisionBehaviors()
        {
            var collisionsWith = new Dictionary<ICollideBehavior, uint>();

            // Collision behavior
            for (var i = 0; i < CollisionCache.Count; i++)
            {
                var collision = CollisionCache[i];

                foreach (var behavior in collision.A.Entity.GetAllComponents<ICollideBehavior>().ToArray())
                {
                    var entity = collision.B.Entity;
                    if (entity.Deleted) break;
                    behavior.CollideWith(entity);
                    if (collisionsWith.ContainsKey(behavior))
                    {
                        collisionsWith[behavior] += 1;
                    }
                    else
                    {
                        collisionsWith[behavior] = 1;
                    }
                }

                foreach (var behavior in collision.B.Entity.GetAllComponents<ICollideBehavior>().ToArray())
                {
                    var entity = collision.A.Entity;
                    if (entity.Deleted) break;
                    behavior.CollideWith(entity);
                    if (collisionsWith.ContainsKey(behavior))
                    {
                        collisionsWith[behavior] += 1;
                    }
                    else
                    {
                        collisionsWith[behavior] = 1;
                    }
                }
            }

            // Post collide
            foreach (var behavior in collisionsWith.Keys)
            {
                behavior.PostCollide(collisionsWith[behavior]);
            }
        }

        private void VelocitySolver()
        {
            if (CollisionCache.Count == 0) return;

            for (var i = 0; i < _velocitySolverIterations; i++)
            {
                var offset = _random.Next(CollisionCache.Count - 1);
                for (var j = 0; j < CollisionCache.Count; j++)
                {
                    var collision = CollisionCache[(j + offset) % CollisionCache.Count];
                    if (!collision.Unresolved) continue;

                    collision.A.WakeBody();
                    collision.B.WakeBody();

                    var impulse = _physicsManager.SolveCollisionImpulse(collision);
                    if (collision.A.CanMove())
                    {
                        collision.A.ApplyImpulse(-impulse);
                    }

                    if (collision.B.CanMove())
                    {
                        collision.B.ApplyImpulse(impulse);
                    }
                }
            }
        }

        // Based off of Randy Gaul's ImpulseEngine code
        // https://github.com/RandyGaul/ImpulseEngine/blob/5181fee1648acc4a889b9beec8e13cbe7dac9288/Manifold.cpp#L123a
        private void PositionSolver()
        {
            if (CollisionCache.Count == 0) return;

            for (var i = 0; i < _positionSolverIterations; i++)
            {
                var done = true;
                var offset = _random.Next(CollisionCache.Count - 1);
                for (var j = 0; j < CollisionCache.Count; j++)
                {
                    var collision = CollisionCache[(j + offset) % CollisionCache.Count];

                    if (!collision.Hard)
                        continue;

                    var penetration = _physicsManager.CalculatePenetration(collision.A, collision.B);

                    if (penetration <= _positionAllowance)
                        continue;

                    done = false;
                    var correction = collision.Normal * MathF.Min(MathF.Max(penetration - _positionAllowance, 0.0f), _maxPositionCorrect * _positionCorrectPercent) / (collision.A.InvMass + collision.B.InvMass);

                    if (collision.A.CanMove())
                    {
                        collision.A.Owner.Transform.DeferUpdates = true;
                        _deferredUpdates.Add(collision.A);
                        collision.A.Owner.Transform.WorldPosition -= correction * collision.A.InvMass;
                    }

                    if (collision.B.CanMove())
                    {
                        collision.B.Owner.Transform.DeferUpdates = true;
                        _deferredUpdates.Add(collision.B);
                        collision.B.Owner.Transform.WorldPosition += correction * collision.B.InvMass;
                    }
                }

                if (done)
                    break;
            }
        }

        /// <summary>
        ///     Process friction between tiles and entities. Does not process collision friction.
        /// </summary>
        /// <param name="bodies"></param>
        /// <param name="frameTime"></param>
        private void ProcessFriction(IEnumerable<IPhysicsComponent> bodies, float frameTime)
        {
            foreach (var body in bodies)
            {
                if (body.LinearVelocity == Vector2.Zero || body.Status == BodyStatus.InAir) return;

                var friction = GetFriction(body);

                // friction between the two objects - Static friction not modelled
                var dynamicFriction = friction * body.Friction * 9.8f * frameTime;

                if (dynamicFriction == 0.0f)
                    return;

                if (dynamicFriction > body.LinearVelocity.Length)
                    dynamicFriction = body.LinearVelocity.Length;

                body.LinearVelocity -= body.LinearVelocity.Normalized * dynamicFriction;
            }
        }

        private void UpdatePosition(IPhysicsComponent physics, float frameTime, float frameSpeedLimit)
        {
            var ent = physics.Entity;

            if ((physics.LinearVelocity.LengthSquared < Epsilon && MathF.Abs(physics.AngularVelocity) < Epsilon))
                return;

            if (physics.LinearVelocity != Vector2.Zero)
            {
                if (ContainerHelpers.IsInContainer(ent))
                {
                    var relayEntityMoveMessage = new RelayMovementEntityMessage(ent);
                    ent.Transform.Parent!.Owner.SendMessage(ent.Transform, relayEntityMoveMessage);
                    // This prevents redundant messages from being sent if solveIterations > 1 and also simulates the entity "colliding" against the locker door when it opens.
                    physics.LinearVelocity = Vector2.Zero;
                }
            }

            physics.Owner.Transform.DeferUpdates = true;
            _deferredUpdates.Add(physics);

            var frameVelocity = physics.LinearVelocity * frameTime;

            if (frameVelocity.Length > frameSpeedLimit)
                frameVelocity = frameVelocity.Normalized * frameSpeedLimit;

            var newPosition = physics.WorldPosition + frameVelocity;
            var owner = physics.Owner;
            var transform = owner.Transform;

            // Change parent if necessary
            if (!ContainerHelpers.IsInContainer(owner))
            {
                // This shoouullddnnn'''tt de-parent anything in a container because none of that should have physics applied to it.
                if (_mapManager.TryFindGridAt(transform.MapID, newPosition, out var grid) &&
                    grid.GridEntityId.IsValid() &&
                    grid.GridEntityId != owner.Uid)
                {
                    if (grid.GridEntityId != transform.ParentUid)
                        transform.AttachParent(owner.EntityManager.GetEntity(grid.GridEntityId));
                }
                else
                {
                    transform.AttachParent(_mapManager.GetMapEntity(transform.MapID));
                }
            }

            physics.WorldRotation += physics.AngularVelocity * frameTime;
            physics.WorldPosition = newPosition;
        }

        private float GetFriction(IPhysicsComponent body)
        {
            var transform = body.Owner.Transform;

            if (body.Status != BodyStatus.OnGround || transform.GridID == GridId.Invalid)
                return 0f;

            var grid = _mapManager.GetGrid(transform.Coordinates.GetGridId(EntityManager));
            var tile = grid.GetTileRef(transform.Coordinates);
            var tileDef = _tileDefinitionManager[tile.Tile.TypeId];
            return tileDef.Friction;
        }
    }
}

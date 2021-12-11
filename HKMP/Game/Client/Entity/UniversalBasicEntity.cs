using Hkmp.Fsm;
using Hkmp.Networking.Client;
using Hkmp.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Hkmp.Game.Client.Entity {
    public class UniversalBasicEntity : HealthManagedEntity {
        private readonly PlayMakerFSM _fsm;
        public PlayMakerFSM FSM { get { return _fsm; } }

        private readonly Dictionary<string, int[]> _stateActionPairs; // key is state name, list is relevant animation indices
        private readonly List<Syncable> _syncables;
        public List<Syncable> Syncables { get { return _syncables; } }

        private readonly tk2dSpriteAnimator _animator;

        private readonly string _defaultStateName;
        public tk2dSpriteAnimator Animator { get { return _animator; } }

        public UniversalBasicEntity(
            NetClient netClient,
            byte entityId,
            GameObject gameObject,
            EntityType entityType,
            string fsmName,
            string defaultStateName
        ) : base(netClient, entityType, entityId, gameObject) {
            _fsm = gameObject.LocateMyFSM(fsmName);
            _animator = gameObject.GetComponent<tk2dSpriteAnimator>();
            // Get the initial scale of the object on enter. This is used to determine which way the sprite it facing. 
            _syncables = new List<Syncable>();
            _stateActionPairs = GetSyncedStatesAndAction(_fsm);
            _defaultStateName = defaultStateName;

            CreateAnimationEvents();
            RemoveAllTransitions(_fsm); // Stops jittering from fsm's starting up, breaks something in host switching. This resolution is temporary.
        }
        public string[] autoSyncedActions = new string[] { "AudioPlayRandom", "SetAudioPitch", "AudioPlayerOneShot" };

        /*
        Scrapes an fsm for the states with actions that have associated audio, 
        returns a dictionary of these with (key,value) pair (state,action index list)
        */
        private Dictionary<string, int[]> GetSyncedStatesAndAction(PlayMakerFSM _inputFSM) {
            Dictionary<string, int[]> output = new Dictionary<string, int[]>();

            foreach (var state in _inputFSM.FsmStates) {
                List<int> actionsToSync = new List<int>();
                for (int i = 0; i < state.Actions.Length; i++) { // starting from 1 as 0 is always the update action
                    var action = state.Actions[i];
                    // using action.Name / action.DisplayName both return an empty string
                    string className = action.GetType().ToString();
                    string actionName = className.Substring(className.LastIndexOf('.') + 1);

                    if (autoSyncedActions.Contains(actionName)) {
                        actionsToSync.Add(i); // keeping indices to use later
                    }
                }
                if (actionsToSync.Count != 0) {
                    output.Add(state.Name, actionsToSync.ToArray());
                }
            }
            return output;
        }

        private void CreateAnimationEvents() {
            // Some animations are not controlled by the FSM. Hence we must make all animations in the entity's animator to trigger `AnimationEventTriggered` to send state updates. 
            // Creating syncables for Animations //
            if (_animator != null) {
                foreach (var clip in _animator.Library.clips) {
                    // Add animation to dictionary
                    _syncables.Add(new AnimationSync(this, clip.name));
                    // Skip clips with no frames
                    if (clip.frames.Length == 0) {
                        continue;
                    }
                    var firstFrame = clip.frames[0];
                    // Enable event triggering on the first frame
                    firstFrame.triggerEvent = true;
                    // Also include the clip name as event info
                    firstFrame.eventInfo = clip.name;
                }
            }
            else {
                Logger.Get().Warn(this, "Animator not found");
            }
            // Making each animation send an update
            _animator.AnimationEventTriggered = (caller, currentClip, currentFrame) => {
                if (IsHostEntity) {
                    var syncableIndex = _syncables.FindIndex(x => {
                        return x is AnimationSync aSync && aSync.animationName == currentClip.name;
                    });
                    SendAnimationUpdate((byte)syncableIndex);
                    Log($"{FSM.name} Sending AUpdate {currentClip.name} with ID {syncableIndex}");
                }
            };

            // Create update event for each state (and associated actions) that needs syncing 
            Log("State Actions Pairs");
            foreach (var keyValuePair in _stateActionPairs) {
                Log(keyValuePair.Key.ToString() + ", " + string.Join(",", keyValuePair.Value.Select(p => p.ToString())) ); // string.Join(",", keyValuePair.Value.Select(ToString))
                string name = keyValuePair.Key;
                int[] actionsToSync = keyValuePair.Value;
                var syncableIndex = _syncables.Count;
                _syncables.Add(new StateActionSync(this, name, actionsToSync));
                // very weird bug, log printing means we should be sending SUpdate but client isn't recieving anything (i.e. UpdateAnimation not called) at all
                _fsm.InsertMethod(name, 0, CreateUpdateMethod(() => { SendAnimationUpdate((byte)syncableIndex); Log($"{FSM.name} Sending SUpdate {name} with action {string.Join(",", actionsToSync.Select(p => p.ToString()))} with ID {syncableIndex}"); }));
            }
        }

        protected override void InternalInitializeAsSceneHost() {
            RestoreAllTransitions(_fsm);
        }

        protected override void InternalInitializeAsSceneClient(byte? stateIndex) {
            _fsm.SetState(_defaultStateName);
            RemoveAllTransitions(_fsm);
            _animator.Stop();
        }

        protected override void InternalSwitchToSceneHost() {
            _fsm.SetState(_defaultStateName);
            RestoreAllTransitions(_fsm);
        }

        public override void UpdateAnimation(byte animationIndex, byte[] animationInfo) {
            Log($"{FSM.name} Recieved update with ID {animationIndex}");

            base.UpdateAnimation(animationIndex, animationInfo);
            
            if (animationIndex != 255) { // So long as the recieved state was not the death state
            var syncable = _syncables[animationIndex];
                syncable.RecieveUpdate(this, animationIndex, animationInfo);
                //Log(syncable.ToString());
            }
        }

        public override void UpdateState(byte stateIndex) {
        }

        public void Log(string s) {
            Logger.Get().Info(this, s);
        }
    }

    public abstract class Syncable {
        public readonly UniversalBasicEntity universalBasicEntity;

        protected Syncable(UniversalBasicEntity universalBasicEntity) {
            this.universalBasicEntity = universalBasicEntity;
        }

        public abstract void RecieveUpdate(UniversalBasicEntity universalBasicEntity, byte animationIndex, byte[] animationInfo);
        public abstract void SendUpdate();
        public abstract int GetIndex();
    }

    class AnimationSync : Syncable {
        public readonly string animationName;
        public AnimationSync(UniversalBasicEntity universalBasicEntity, string animationName) : base(universalBasicEntity) {
            this.animationName = animationName;
        }

        public override void RecieveUpdate(UniversalBasicEntity universalBasicEntity, byte animationIndex, byte[] animationInfo) {
            universalBasicEntity.Animator.Stop();
            universalBasicEntity.Animator.Play(animationName);
            Logger.Get().Info(this, $"{universalBasicEntity.FSM.name} Recieved AUpdate {animationName} with ID {animationIndex}");
        }
        public override int GetIndex() { return universalBasicEntity.Syncables.FindIndex(x => x is AnimationSync animationSync && animationSync.animationName == this.animationName); }
        public override string ToString() {
            return "AnimSync: " + animationName;
        }

        public override void SendUpdate() {
            throw new NotImplementedException();
        }
    }

    class StateActionSync : Syncable {
        public readonly string stateName;
        public readonly int[] actionsToSync;
        public StateActionSync(UniversalBasicEntity universalBasicEntity, string stateName, int[] actionsToSync) : base(universalBasicEntity) {
            this.actionsToSync = actionsToSync;
            this.stateName = stateName;
        }
        public override void RecieveUpdate(UniversalBasicEntity universalBasicEntity, byte animationIndex, byte[] animationInfo) {
            // Because we have inserted the update action at index 0, the actions must be offsett by 1 when recalled. 
            int[] correctedActionsToSync = actionsToSync.Select(x => x + 1).ToArray();
            universalBasicEntity.FSM.ExecuteActions(stateName, correctedActionsToSync);
            Logger.Get().Info(this, $"{universalBasicEntity.FSM.name} Recieved SUpdate {stateName} executing {string.Join(",", correctedActionsToSync.Select(p => p.ToString()))} with ID {animationIndex}");
        }

        public override int GetIndex() { return universalBasicEntity.Syncables.FindIndex(x => x is StateActionSync stateSync && stateSync.stateName == this.stateName); }

        public override string ToString() {
            return "StatSync: " + stateName + String.Join(",", actionsToSync);
        }

        public override void SendUpdate() {
            throw new NotImplementedException();
        }
    }
}
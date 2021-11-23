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

        private readonly Dictionary<int, string> _animationIds;
        private readonly Dictionary<string, int[]> _stateActionPairs; // key is state name, list is relevant animation indices

        private readonly tk2dSpriteAnimator _animator;

        private readonly string _defaultStateName;

        public UniversalBasicEntity(
            NetClient netClient,
            byte entityId,
            GameObject gameObject,
            EntityType entityType,
            string fsmName
        ) : base(netClient, entityType, entityId, gameObject) {
            _fsm = gameObject.LocateMyFSM(fsmName);
            _animator = gameObject.GetComponent<tk2dSpriteAnimator>();
            // Get the initial scale of the object on enter. This is used to determine which way the sprite it facing. 
            _animationIds = new Dictionary<int, string>();
            _stateActionPairs = GetSyncedStatesAndAction(_fsm);
            _defaultStateName = _fsm.ActiveStateName;
            Log(_defaultStateName);

            CreateAnimationEvents();
            RemoveAllTransitions(_fsm);//Testing
 
            _stateActionPairs = GetSyncedStatesAndAction(_fsm);

            string msg = _stateActionPairs.Aggregate( "", (acc, x) => acc + x.Key + ": " + string.Join(", ", x.Value) + "\n");
            Log(msg);
        }
        
        /*
        Scrapes an fsm for the states with actions that have associated audio, 
        returns a dictionary of these with (key,value) pair (state,action index list)
        */
        private Dictionary<string, int[]> GetSyncedStatesAndAction(PlayMakerFSM _inputFSM) {
            Dictionary<string, int[]> output = new Dictionary<string, int[]>();

            foreach (var state in _inputFSM.FsmStates) {
                List<int> actionsToSync = new List<int>();
                for (int i = 0; i < state.Actions.Length; i++) {
                    var action = state.Actions[i];

                    if (autoSyncedActions.Contains(action.Name)) {
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
            // Some animations are not controlled by the FSM. Hence we must make all animations in the entity's Walker component to trigger `AnimationEventTriggered` to send state updates. 
            if (_animator != null) {
                foreach (var clip in _animator.Library.clips) {
                    // Add animation to dictionary
                    _animationIds.Add(_animationIds.Count, clip.name);
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
            // Create update event for each state that needs syncing 
            foreach(var keyValuePair in _stateActionPairs) {
                string name = keyValuePair.Key;
                int[] actionsToExecute = keyValuePair.Value; // Array of the index of all actions that need to be exectued.
                _animationIds.Add(_animationIds.Count, name);
                _fsm.InsertMethod(name, 0, CreateUpdateMethod(() => { SendAnimationUpdate((byte)GetAnimationId(name)); }));
            }

            // Making each animation send an update
            _animator.AnimationEventTriggered = (caller, currentClip, currentFrame) => {
                if (IsHostEntity) {
                    SendAnimationUpdate((byte)GetAnimationId(currentClip.name));
                }
            };
        }
        protected override void InternalInitializeAsSceneHost() {
            RestoreAllTransitions(_fsm);
        }
        protected override void InternalInitializeAsSceneClient(byte? stateIndex) {
            RemoveAllTransitions(_fsm);
            _fsm.SetState(_defaultStateName);
            _animator.Stop();
        }
        protected override void InternalSwitchToSceneHost() {
            RestoreAllTransitions(_fsm);
            _fsm.SetState(_defaultStateName);
        }
        public override void UpdateAnimation(byte animationIndex, byte[] animationInfo) {
            base.UpdateAnimation(animationIndex, animationInfo);

            // Check if the animation is _strictly_ an animation
            if (animationIndex != 255) {
                // We must stop the previous animation in order to play the new one. 
                _animator.Stop();
                _animator.Play(_animationIds[animationIndex]);
            }
        }

        public override void UpdateState(byte stateIndex) {
        }

        public string[] autoSyncedActions = new string[] { "AudioPlayRandom", "SetAudioPitch" };
        public void Log(string s) {
            Logger.Get().Info(this, s);
        }
        public int GetAnimationId(string animationName) => _animationIds.FirstOrDefault(x => x.Value == animationName).Key;
    }
}
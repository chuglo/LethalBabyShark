using System.Collections;
using System.Diagnostics;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace LethalBabyShark
{
#nullable disable
    public class LethalBabyShark : EnemyAI
    {
        public AudioClip currentClip;
        public AudioClip babySharkSong;
        public AudioClip runaway_letsgohunt; // the distorted bit

        private bool isPlayingClip = false;
        private bool isKilling = false;
        private bool hasCollidedWithPlayer = false;
        private bool isDeadAnimationDone = false;

        private readonly float huntTimeout = 120f;
        private readonly float followBufferDistance = 2f;
        private readonly float huntBufferDistance = 1f;
        private readonly float maxAudioDistance = 25f;
        private readonly float minAudioDistance = 1f;
        private readonly float maxAudioVolume = 1f;

        private enum State
        {
            SearchingForPlayer,
            FollowingPlayer,
            HuntingPlayer,
            KillingPlayer
        }

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text)
        {
            LethalBabySharkPlugin.Logger.LogInfo(text);
        }

        public override void Start()
        {
            base.Start();
            LogIfDebugBuild("LethalBabyShark Spawned");

            isDeadAnimationDone = false;
            DoAnimationClientRpc("startSwim");
            agent.speed = 2f;
            currentBehaviourStateIndex = (int)State.SearchingForPlayer;
            StartSearch(transform.position);
        }

        public override void Update()
        {
            base.Update();
            if (isEnemyDead)
            {
                if (!isDeadAnimationDone)
                {
                    isDeadAnimationDone = true;
                    StopAudioClip();
                    PlayAudioClip(dieSFX);
                }
                return;
            }
        }

        public override void DoAIInterval()
        {
            base.DoAIInterval();
            if (isEnemyDead || StartOfRound.Instance.allPlayersDead) return;

            switch (currentBehaviourStateIndex)
            {
                case (int)State.SearchingForPlayer:
                    HandleSearchingForPlayer();
                    break;

                case (int)State.FollowingPlayer:
                    HandleFollowingPlayer();
                    break;

                case (int)State.HuntingPlayer:
                    HandleHuntingPlayer();
                    break;

                case (int)State.KillingPlayer:
                    HandleKillingPlayer();
                    break;
            }
        }

        private bool IsCurrentState(State state) => currentBehaviourStateIndex == (int)state;

        private void HandleSearchingForPlayer()
        {
            // player not close enough, don't do anything
            if (!FoundClosestPlayerInRange(25f, 3f)) return;

            // player in range, stop search and start following
            LogIfDebugBuild("Start targeting player");
            StopSearch(currentSearch);
            SwitchToBehaviourClientRpc((int)State.FollowingPlayer);
        }

        private void HandleFollowingPlayer()
        {
            agent.speed = 8f;
            FollowClosely(targetPlayer.transform, followBufferDistance);

            PlayAudioIfNotPlaying(babySharkSong, 10f, () =>
            {
                if (IsCurrentState(State.FollowingPlayer))
                {
                    LogIfDebugBuild("Switching to HuntingPlayer state");
                    SwitchToBehaviourClientRpc((int)State.HuntingPlayer);
                }
            });
        }

        private void HandleHuntingPlayer()
        {
            DoAnimationClientRpc("startHunting");
            agent.speed = 11f;
            FollowClosely(targetPlayer.transform, huntBufferDistance);

            PlayAudioIfNotPlaying(runaway_letsgohunt, 15f , () =>
            {
                if (IsCurrentState(State.HuntingPlayer))
                {
                    LogIfDebugBuild("Switching to KillingPlayer state");
                    SwitchToBehaviourClientRpc((int)State.KillingPlayer);
                }
            });
        }

        private void HandleKillingPlayer()
        {
            // Check if a kill can be initiated
            if (CanStartKill())
            {
                isKilling = true;
                StartKillingBehavior();
            }
            else
            {
                // Ensure the shark is actively moving toward the player
                if (targetPlayer != null)
                {
                    agent.speed = 11f;
                    agent.SetDestination(targetPlayer.transform.position);
                }

                // Start collision wait if it hasn't already been triggered
                if (!isKilling)
                {
                    StartCoroutine(WaitForCollision());
                }
            }
        }

        private void HandlePlayerCollision(PlayerControllerB playerControllerB)
        {
            if (playerControllerB == null) return;

            LogIfDebugBuild("Player collision detected");
            hasCollidedWithPlayer = true;
            targetPlayer = playerControllerB;

            if (IsCurrentState(State.KillingPlayer))
            {
                StartKillingBehavior();
            }
        }

        private void StartKillingBehavior()
        {
            if (!IsCurrentState(State.KillingPlayer) || !CanStartKill())
            {
                LogIfDebugBuild("Cannot start killing behavior.");
                return;
            }

            LogIfDebugBuild($"Starting kill behavior for player: {targetPlayer.name}");
            StopAudioClip();
            isKilling = true;
            DoAnimationClientRpc("kill"); // This will trigger the animation that calls ExecuteKill
        }

        private bool CanStartKill() => targetPlayer != null && hasCollidedWithPlayer && !isKilling;

        private IEnumerator HandleKillAndReset()
        {
            LogIfDebugBuild("HandleKillAndReset started.");
            // We don't need to do anything here - ExecuteKill will be called by the animation event
            yield break;
        }

        private void ResetStateAfterKill()
        {
            isKilling = false;
            hasCollidedWithPlayer = false;
            targetPlayer = null;
            StopAudioClip();  // Ensure any playing audio is stopped

            DoAnimationClientRpc("startSwim");
            StartSearch(transform.position);
            SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
        }

        private void ResetState()
        {
            LogIfDebugBuild("ResetState called");

            if (!isKilling)  // Only reset if we're not in the middle of a kill
            {
                isKilling = false;
                hasCollidedWithPlayer = false;
                targetPlayer = null;
                StopAudioClip();  // Ensure any playing audio is stopped

                DoAnimationClientRpc("startSwim");
                StartSearch(transform.position);
                SwitchToBehaviourClientRpc((int)State.SearchingForPlayer);
            }
        }

        private Vector3 CalculateFollowPosition(Transform target, float bufferDistance)
        {
            Vector3 directionToTarget = (target.position - transform.position).normalized;
            return target.position - directionToTarget * bufferDistance;
        }

        private void FollowClosely(Transform target, float bufferDistance)
        {
            if (target == null || !agent.isActiveAndEnabled) return;

            Vector3 followPosition = CalculateFollowPosition(target, bufferDistance);

            // Always set dest. towards target player
            agent.SetDestination(followPosition);
        }

        private void PlayAudioIfNotPlaying(AudioClip clip, float transitionDuration, System.Action onComplete)
        {
            if (isPlayingClip || clip == null) return;

            isPlayingClip = true;
            PlayAudioClip(clip);
            StartCoroutine(WaitForAudio(transitionDuration, () =>
            {
                isPlayingClip = false; // Reset the audio state before invoking the callback
                onComplete?.Invoke();
            }));
        }

        private IEnumerator WaitForAudio(float duration, System.Action onComplete)
        {
            yield return new WaitForSeconds(duration);
            StopAudioClip(); // Stop the audio after the duration
            onComplete?.Invoke(); // Trigger the callback, if provided
            isPlayingClip = false; // Reset the state
        }

        private IEnumerator WaitForCollision()
        {
            LogIfDebugBuild("Waiting for collision...");
            float elapsedTime = 0f;

            while (!hasCollidedWithPlayer && elapsedTime < huntTimeout)
            {
                // Update elapsed time and wait until the next frame
                elapsedTime += Time.deltaTime;
                yield return null;
            }

            // Handle collision or timeout
            if (IsCurrentState(State.KillingPlayer))
            {
                if (hasCollidedWithPlayer)
                {
                    LogIfDebugBuild("Collision detected within timeout.");
                    isKilling = true;
                    StartKillingBehavior();
                }
                else
                {
                    LogIfDebugBuild("No collision detected within 30 seconds. Resetting state.");
                    ResetState();
                }
            }
        }

        public override void OnCollideWithPlayer(Collider other)
        {
            if (!IsCurrentState(State.KillingPlayer)) return; // Only handle collisions with Player in the KillingPlayer state

            HandlePlayerCollision(MeetsStandardPlayerCollisionConditions(other));
        }

        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            base.HitEnemy(force, playerWhoHit, playHitSFX, hitID);
            if (isEnemyDead) return;

            enemyHP -= force;
            if (IsOwner)
            {
                if (enemyHP <= 0 && !isEnemyDead)
                {
                    // Game doesn't stop search coroutine when enemy dies, so we need to do it manually
                    StopCoroutine(searchCoroutine);
                    KillEnemyOnOwnerClient();
                }
            }
        }

        private bool FoundClosestPlayerInRange(float range, float senseRange)
        {
            TargetClosestPlayer(1.5f, true);
            if (targetPlayer != null) return true;

            TargetClosestPlayer(1.5f, false);
            return targetPlayer != null && Vector3.Distance(transform.position, targetPlayer.transform.position) < senseRange;
        }

        private void PlayAudioClip(AudioClip clip, bool interrupt = true)
        {
            if (creatureSFX == null || clip == null || (isPlayingClip && !interrupt)) return;
            PlayAudioClipClientRpc(clip.name);

        }

        private void StopAudioClip()
        {
            if (creatureSFX != null && creatureSFX.isPlaying)
            {
                LogIfDebugBuild("Stopping audio.");
                creatureSFX.Stop();
                isPlayingClip = false;
            }
        }

        // This is for an animation event in Unity. It will work despite no refs in code here.
        public void ExecuteKill()
        {
            if (targetPlayer != null)
            {
                LogIfDebugBuild($"Executing kill on player: {targetPlayer.name}");
                targetPlayer.KillPlayer(Vector3.zero);
                ResetStateAfterKill();
            }
            else
            {
                LogIfDebugBuild("No target to kill.");
                ResetState();
            }
        }

        [ClientRpc]
        public void DoAnimationClientRpc(string animationName)
        {
            creatureAnimator.SetTrigger(animationName);
        }

        [ClientRpc]
        private void PlayAudioClipClientRpc(string clipName)
        {
            AudioClip clip = null;
            switch (clipName)
            {
                case "babySharkSong":
                    clip = babySharkSong;
                    break;
                case "runaway_letsgohunt":
                    clip = runaway_letsgohunt;
                    break;
                case "splat":
                    clip = dieSFX;
                    break;
            }
            if (clip == null)
            {
              LogIfDebugBuild($"Audio clip not found: {clipName}");
              return;
            }
            // Spatial audio settings
            creatureSFX.spatialBlend = 1f; // 3D sound
            creatureSFX.rolloffMode = AudioRolloffMode.Linear;
            creatureSFX.maxDistance = maxAudioDistance;
            creatureSFX.minDistance = minAudioDistance;

            // Player for nearby players
            foreach (var player in StartOfRound.Instance.allPlayerScripts)
            {
                float distance = Vector3.Distance(transform.position, player.transform.position);
                if (distance <= maxAudioDistance)
                {
                    // calc volume based on distance
                    float volumeMultiplier = 1f - (distance / maxAudioDistance);
                    creatureSFX.volume = maxAudioVolume * volumeMultiplier;

                    creatureSFX.clip = clip;
                    LogIfDebugBuild($"Playing audio: {clip.name}");
                    creatureSFX.Play();
                    isPlayingClip = true;
                    break;
                }
            }

        }
    }
}

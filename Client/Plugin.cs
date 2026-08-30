//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using Diz.Jobs;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using EFT.Hideout;
using Newtonsoft.Json;
using HarmonyLib;
using SPT.Reflection.Patching;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;

// TODO test all timings

namespace SevenBoldPencil.TargetDummies
{
	public enum MannequinType
	{
		Mannequin1,
		Mannequin2,
		Mannequin3,

		Scav,
		ScavSniper,
		Raider,

		BEAR,
		USEC,

		Reshala,
		ReshalaGuard,

		Shturman,
		ShturmanGuard,

		Sanitar,
		SanitarGuard,

		Gluhar,
		GluharGuardAssault,
		GluharGuardSecurity,
		GluharGuardScout,
		GluharGuardSnipe,

		Killa,
		KillaLabyrinth,

		Tagilla,
		TagillaLabyrinth,

		Rogue,
		Knight,
		BigPipe,
		BirdEye,

		CultistWarrior,
		CultistPriest,

		Zryachiy,
		ZryachiyGuard,

		Kaban,
		KabanGuardBasmach,
		KabanGuardGus,
		KabanGuard,
		KabanGuardSniper,

		Kolontay,
		KolontayGuardAssault,
		KolontayGuardSecurity,

		Partisan,
	}

	public readonly record struct MannequinData
	(
		Vector3 Position,
		ConfigEntry<MannequinType> Type
	);

    [BepInPlugin("7Bpencil.TargetDummies", "7Bpencil.TargetDummies", "0.2.1")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
		public ManualLogSource LoggerInstance;

		public ConfigEntry<MannequinType> CloseLeftMannequinType;
		public ConfigEntry<MannequinType> CloseMiddleMannequinType;
		public ConfigEntry<MannequinType> CloseRightMannequinType;

		public ConfigEntry<MannequinType> FarLeftMannequinType;
		public ConfigEntry<MannequinType> FarMiddleMannequinType;
		public ConfigEntry<MannequinType> FarRightMannequinType;

		public ConfigEntry<float> Mannequin_Health_Head;
		public ConfigEntry<float> Mannequin_Health_Chest;
		public ConfigEntry<float> Mannequin_Health_Stomach;
		public ConfigEntry<float> Mannequin_Health_Arm;
		public ConfigEntry<float> Mannequin_Health_Leg;

		public Dictionary<LocalPlayer, MannequinData> Mannequins;

        private void Awake()
        {
            Instance = this;
			LoggerInstance = Logger;

			CloseLeftMannequinType = Config.Bind<MannequinType>("Close", "Left Mannequin Type", MannequinType.Scav, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 3 }));
			CloseMiddleMannequinType = Config.Bind<MannequinType>("Close", "Middle Mannequin Type", MannequinType.Scav, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 2 }));
			CloseRightMannequinType = Config.Bind<MannequinType>("Close", "Right Mannequin Type", MannequinType.Scav, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 1 }));

			FarLeftMannequinType = Config.Bind<MannequinType>("Far", "Left Mannequin Type", MannequinType.Scav, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 3 }));
			FarMiddleMannequinType = Config.Bind<MannequinType>("Far", "Middle Mannequin Type", MannequinType.Scav, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 2 }));
			FarRightMannequinType = Config.Bind<MannequinType>("Far", "Right Mannequin Type", MannequinType.Scav, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 1 }));

			Mannequin_Health_Head = Config.Bind<float>("Mannequin Settings", "Health Head", 35, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 5 }));
			Mannequin_Health_Chest = Config.Bind<float>("Mannequin Settings", "Health Chest", 85, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 4 }));
			Mannequin_Health_Stomach = Config.Bind<float>("Mannequin Settings", "Health Stomach", 70, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 3 }));
			Mannequin_Health_Arm = Config.Bind<float>("Mannequin Settings", "Health Arm", 60, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 2 }));
			Mannequin_Health_Leg = Config.Bind<float>("Mannequin Settings", "Health Leg", 65, new ConfigDescription("", null, new ConfigurationManagerAttributes { Order = 1 }));

			Mannequins = new();

			// PORTING NOTE (SPT 4.0.13): each patch enabled independently so one Harmony failure
			// (confirmed in-game: Patch_HideoutAreaTrigger_OnTriggerExit's ____area field injection
			// doesn't match a real field on HideoutAreaTrigger here - the private field was renamed,
			// not yet found) doesn't take the others down with it, and logs clearly instead of
			// crashing Awake() partway through.
			void TryEnable(ModulePatch patch, string name)
			{
				try
				{
					patch.Enable();
				}
				catch (Exception ex)
				{
					LoggerInstance.LogWarning($"Failed to enable patch {name}: {ex.Message}");
				}
			}

			TryEnable(new Patch_HideoutController_HideoutAwake(), nameof(Patch_HideoutController_HideoutAwake));
			TryEnable(new Patch_GameWorld_DestroyAllLoot(), nameof(Patch_GameWorld_DestroyAllLoot));
			TryEnable(new Patch_CorpseRagdoll_Start(), nameof(Patch_CorpseRagdoll_Start));
			TryEnable(new Patch_HideoutAreaTrigger_OnTriggerExit(), nameof(Patch_HideoutAreaTrigger_OnTriggerExit));

			// Ported from spt-hideout-shootout's own HollywoodFX hideout-effects compat fix -
			// TryEnable itself is defensive (reflection-based, no-ops cleanly if HollywoodFX isn't
			// installed), so it doesn't need the same try/catch wrapper as the patches above.
			Patch_HollywoodFX_ForceEffectsInHideout.TryEnable();
        }

		public async Task SpawnBot(MannequinData data)
		{
			// Declared out here so the catch below can still see it - it is filled in just before
			// LocalPlayer.Create, and used to clean up a half-built body if that call throws.
			HashSet<LocalPlayer> playersBeforeCreate = null;

			try
			{

			Patch_HollywoodFX_ForceEffectsInHideout.TryWireShotDelegateOnce();

    		if (!TarkovApplication.Exist(out var tarkovApplication))
            {
                Logger.LogWarning("SpawnBot aborted: TarkovApplication.Exist() returned false.");
                return;
            }

			// PORTING NOTE (SPT 4.0.13): the field is Task_0 (capital T) here, not task_0 - same
			// Task<GStruct156<HideoutGame>> shape, confirmed via DumpTool. GameWorld/Profile are
			// GameWorld_0/Profile_0 here (both declared on the BaseLocalGame<HideoutPlayerOwner>
			// base class), also confirmed via DumpTool.
			var hideoutController = tarkovApplication.HideoutControllerAccess;
			var hideoutGame = hideoutController.Task_0.Result.Value;
			var hideoutGameWorld = hideoutGame.GameWorld_0;
			var localPlayerPosition = new Vector3(-2.5263f, 0f, 9.3481f);

			var botPlayerProfile = await GenerateProfile(tarkovApplication.Session, hideoutGame.Profile_0, data.Type.Value);

			// PORTING NOTE (SPT 4.0.13): a prior version of this port stripped EBodyModelPart.Hands
			// from the profile's Customization here, copying a fix from spt-hideout-shootout's own
			// backport (there, the hands-rig bundle is never loadable outside a real raid's loading
			// screen). Confirmed WRONG for this mod: Profile.GetAllPrefabPaths iterates all
			// EBodyModelPart values and indexes Customization[part] directly, so removing an entry
			// throws KeyNotFoundException instead of skipping it - the exact opposite of the
			// intended effect. TargetDummies also mostly spawns real WildSpawnType bot profiles
			// fetched via session.LoadBots (not hand-built ones like hideout-shootout's scav
			// target), which should carry a legitimately loadable Hands entry already. Removed;
			// if hands still fail to load in-game, that will show as a bundle-load
			// warning/timeout from PreloadProfileBundlesAsync below, not a hard crash.

			// PORTING NOTE (SPT 4.0.13): ObjectsFactory.LoadBundlesAndCreatePools (4.1's name for
			// this singleton/method pair) doesn't exist under that name here - PoolManagerClass is
			// the obfuscated 4.0.13 name, and its equivalent LoadBundlesAndCreatePools has a
			// malformed callback delegate type the CLR refuses to load from mod code. Same blocker
			// already solved in spt-hideout-shootout's own 4.0.13 backport: register the Raid pool
			// category via RegisterPools for capacity, then load the profile's resources through
			// IAssetsManager.LoadAssetAsync(ResourceKey) - see PreloadProfileBundlesAsync for why
			// that overload, and not the string-based LoadBundlesAsync, is the working path here.
			await PreloadProfileBundlesAsync(botPlayerProfile);

			// PORTING NOTE (SPT 4.0.13): HideoutGame/BaseLocalGame<HideoutPlayerOwner> has no method
			// matching NextPlayerId (confirmed via DumpTool - no Player/Id/World/Profile-named
			// method exists on the whole base chain up to AbstractGame). A random id in a range
			// vanilla players won't collide with is all LocalPlayer.Create actually needs -
			// spt-hideout-shootout's own backport uses the exact same approach for its hideout bots.
			var botPlayerId = UnityEngine.Random.Range(100000, int.MaxValue);
			var rotation = Quaternion.LookRotation((localPlayerPosition - data.Position).normalized);

			// PORTING NOTE (SPT 4.0.13): AppEnvironment.Config.CharacterController.BotPlayerMode has
			// no equivalent preset instance on this client - built directly instead, same as
			// spt-hideout-shootout's backport.
			var botControllerMode = new CharacterControllerSpawner.Mode
			{
				Type = CharacterControllerSpawner.ControllerType.BotAISteeringImpostorWithDoors,
			};

			// PORTING NOTE (SPT 4.0.13): LocalPlayer.Create's 21-argument signature is unchanged,
			// but several argument types/sources are 4.1-only and don't exist here:
			// - LocalGame.CG_Class1642.CG_Class1642.method_4/method_5 (obfuscated 4.1 sensitivity
			//   callbacks) -> plain () => 1f lambdas (mannequins don't move the camera anyway).
			// - DumbStatisticsManager -> GClass2265 (confirmed via SPT's own 4.0->4.1 class name
			//   mapping table).
			// - ThirdPersonCustomizationFilter.Default -> GClass1856.Default (also confirmed via the
			//   mapping table - GClass1855 is the sibling PlayerCustomizationFilter, not this one).
			// Passed positionally rather than by name since the obfuscated build's real parameter
			// names aren't confirmed to match 4.1's.
			// PORTING NOTE (SPT 4.0.13): confirmed via the game's own errors.log - the "<bundle> is
			// not loaded" exceptions seen here for randomized character-mesh bundles
			// (e.g. "bear_head_slava", "wild_head_misha") are NOT a preload timing issue at all:
			// the bundle request itself gets an HTTP 404 from the SPT server ("Can't load bundle:
			// ...bear_head_slava HTTP/1.1 404 Not Found"). Some other installed mod registers extra
			// bot head/appearance templates into the bot generation pool without actually shipping
			// the bundle file for them, so any bot randomly assigned one of those templates can
			// never spawn - no amount of waiting or retrying fixes a 404. Retrying was tried and
			// removed: it doesn't help (the file still isn't there) and reusing the same profile/id
			// across attempts risked LocalPlayer.Create hanging instead of throwing again. A failure
			// here is caught by SpawnBot's own try/catch below and just skips this one mannequin.
			// PORTING NOTE (SPT 4.0.13): if LocalPlayer.Create throws partway through, the Player
			// GameObject it had already instantiated is left behind in the scene, half-wired. Unity
			// keeps calling its LateUpdate every frame, and EFT.Player.ComplexLateUpdate immediately
			// NullReferences on the components Create never got around to setting - flooding the
			// log with a NullReferenceException per orphan per frame (confirmed in-game: tens of
			// thousands of lines, plus "PlayerBody destroyed without being disposed" on teardown)
			// and dragging the framerate down for the rest of the session. Snapshot the live Player
			// objects first so the catch below can destroy whatever Create orphaned.
			playersBeforeCreate = new HashSet<LocalPlayer>(UnityEngine.Object.FindObjectsOfType<LocalPlayer>());

			// PORTING NOTE (SPT 4.0.13): some bot types - Raider was the one seen in-game, while Scav,
			// Shturman and Tagilla all spawned fine - fail inside PlayerBody.Init with a
			// NullReferenceException, from a piece of their randomly rolled loadout that cannot be
			// resolved on this install (several boss weapon bundles genuinely 404 here). Because the
			// loadout is re-rolled per profile request, asking the server for a fresh profile and
			// trying once more is often enough. Create throws quickly in this case, so the retry
			// costs little.
			LocalPlayer botPlayer = null;
			for (int attempt = 1; attempt <= 2; attempt++)
			{
				try
				{
					using (SuppressDisableDevMaskCheckPatch())
					{
						botPlayer = await LocalPlayer.Create(
							hideoutGameWorld,
							botPlayerId,
							data.Position,
							rotation,
							"Player",
							"",
							EPointOfView.ThirdPerson,
							botPlayerProfile,
							true,
							hideoutGame.UpdateQueue,
							Player.EUpdateMode.Auto,
							Player.EUpdateMode.Auto,
							botControllerMode,
							new Func<float>(() => 1f),
							new Func<float>(() => 1f),
							new GClass2265(),
							GClass1856.Default,
							null,
							ELocalMode.TRAINING,
							false,
							true);
					}

					break;
				}
				catch (Exception ex) when (attempt == 1)
				{
					Logger.LogWarning($"LocalPlayer.Create failed for {data.Type.Value} ({ex.GetType().Name}: {ex.Message}); re-rolling the profile and retrying once.");

					// Clean up whatever the failed attempt left behind before trying again.
					DestroyOrphanedPlayers(playersBeforeCreate);

					botPlayerId = UnityEngine.Random.Range(100000, int.MaxValue);
					botPlayerProfile = await GenerateProfile(tarkovApplication.Session, hideoutGame.Profile_0, data.Type.Value);
					await PreloadProfileBundlesAsync(botPlayerProfile);
					playersBeforeCreate = new HashSet<LocalPlayer>(UnityEngine.Object.FindObjectsOfType<LocalPlayer>());
				}
			}

			if (botPlayer == null)
			{
				Logger.LogWarning($"LocalPlayer.Create returned null for mannequin profile {botPlayerProfile.Id}.");
				return;
			}

			// TODO for some reason clothes skinned mesh renderers have enabled forceRenderingOff,
			// I guess culling component thinks that they are not in camera view because
			// something is not initalized properly, so for now just force rendering on

			// PORTING NOTE (SPT 4.0.13): the field is named localPlayerCullingHandlerClass here
			// (4.1 called it botPlayerCulling), typed LocalPlayerCullingHandlerClass rather than
			// OfflinePlayerCulling - confirmed by spt-hideout-shootout's backport to inherit
			// Disable()/ApplyVisibleState()/Mode/IsVisible instead of exposing SetMode directly.
			if (botPlayer.GetField<LocalPlayer, LocalPlayerCullingHandlerClass>("localPlayerCullingHandlerClass") is LocalPlayerCullingHandlerClass playerCulling)
			{
				playerCulling.Disable();
				playerCulling.ApplyVisibleState();
			}
			else
			{
				Logger.LogWarning("Could not resolve LocalPlayer.localPlayerCullingHandlerClass; mannequin body may stay invisible.");
			}

			// PORTING NOTE (SPT 4.0.13): register the mannequin BEFORE putting a weapon in its hands.
			// The bot is fully constructed by this point, so it must be tracked even if the steps
			// below fail - otherwise the catch in this method treats it as an orphan from a failed
			// LocalPlayer.Create and destroys it, which is exactly what was happening: the spawn
			// succeeded and then got cleaned up on its way out.
			Mannequins[botPlayer] = data;

			// take weapon in hands
			//
			// PORTING NOTE (SPT 4.0.13): this throws NullReferenceException inside
			// Player.ItemHandsController.smethod_4 whenever the weapon's own bundles could not be
			// loaded, which is common here - the hideout cannot load most gear bundles (see the
			// remarks on UsePlayerCharacterModel). An armed mannequin is cosmetic; a mannequin that
			// exists, takes hits and dies is the point. So a failure here is logged and swallowed
			// rather than aborting a bot that is otherwise fine.
			try
			{
				botPlayer.SetSlotItem(EquipmentSlot.FirstPrimaryWeapon, (_) => { });
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not put a weapon in mannequin {botPlayerProfile.Id}'s hands ({ex.GetType().Name}: {ex.Message}); giving it empty hands instead.");

				// PORTING NOTE (SPT 4.0.13): a bot left with NO hands controller at all
				// NullReferences every single frame in MovementContext -> Player.MouseLook ->
				// Player.LateUpdate, which floods the log and costs framerate for as long as the
				// mannequin exists. Establishing an empty-hands controller gives MouseLook the state
				// it dereferences. Done by reflection because the callback parameter's exact type
				// is not confirmed for this build and a wrong guess would break the whole build.
				TrySetEmptyHands(botPlayer);
			}

			Logger.LogWarning($"Spawned mannequin for profile {botPlayerProfile.Id} at {data.Position} (type={data.Type.Value}).");

			}
			catch (Exception e)
			{
				Logger.LogError(e);
				DestroyOrphanedPlayers(playersBeforeCreate);
			}
		}

		/// <summary>
		/// Gives a bot an empty-hands controller, so that MovementContext/MouseLook has the state it
		/// dereferences every frame. Reflection-based: the callback parameter's exact type is not
		/// confirmed for this obfuscated build, and a null callback is accepted either way.
		/// </summary>
		private void TrySetEmptyHands(LocalPlayer botPlayer)
		{
			try
			{
				var method = botPlayer.GetType()
					.GetMethods(BindingFlags.Public | BindingFlags.Instance)
					.FirstOrDefault(m => m.Name == "SetEmptyHands" && m.GetParameters().Length == 1);

				if (method == null)
				{
					Logger.LogWarning("LocalPlayer has no single-argument SetEmptyHands; the mannequin may log a MouseLook NullReferenceException every frame.");
					return;
				}

				method.Invoke(botPlayer, new object[] { null });
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"SetEmptyHands failed: {ex.GetType().Name}: {ex.Message}");
			}
		}

		/// <summary>
		/// Destroys any <see cref="Player"/> that appeared since <paramref name="playersBeforeCreate"/>
		/// was captured. Called only when <c>LocalPlayer.Create</c> threw, to clean up the
		/// half-constructed body it left in the scene - see the porting note at the snapshot site.
		/// </summary>
		private void DestroyOrphanedPlayers(HashSet<LocalPlayer> playersBeforeCreate)
		{
			if (playersBeforeCreate == null)
			{
				return;
			}

			try
			{
				foreach (var player in UnityEngine.Object.FindObjectsOfType<LocalPlayer>())
				{
					// Skip anything that already existed, and any mannequin that spawned fine.
					if (player == null || playersBeforeCreate.Contains(player) || Mannequins.ContainsKey(player))
					{
						continue;
					}

					Logger.LogWarning($"Destroying the half-constructed player object LocalPlayer.Create left behind ('{player.name}'), to stop its per-frame LateUpdate NullReferenceExceptions.");

					// Both of these routinely throw on a body that was never finished being built -
					// that is the whole point of the cleanup, so neither failure should stop the other.
					try { player.Dispose(); }
					catch (Exception ex) { Logger.LogDebug($"Dispose() on the orphaned player threw (expected): {ex.Message}"); }

					try { UnityEngine.Object.Destroy(player.gameObject); }
					catch (Exception ex) { Logger.LogWarning($"Destroy() on the orphaned player threw: {ex.Message}"); }
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not clean up orphaned player objects: {ex.Message}");
			}
		}

		/// <summary>
		/// Loads the mannequin's prefabs into the Raid asset pools before LocalPlayer.Create runs.
		/// Ported from spt-hideout-shootout's proven 4.0.13 fix for the same
		/// ObjectsFactory/PoolManagerClass gap - see the PORTING NOTE in SpawnBot above. Best-effort:
		/// see the PORTING NOTE below on why this always proceeds rather than ever blocking the spawn.
		/// </summary>
		private async Task PreloadProfileBundlesAsync(Profile profile)
		{
			if (Singleton<PoolManagerClass>.Instantiated)
			{
				var pools = Singleton<PoolManagerClass>.Instance;
				if (!pools.IsPoolReady(PoolManagerClass.PoolsCategory.Raid))
				{
					pools.RegisterPools(
						PoolManagerClass.PoolsCategory.Raid,
						null,
						ObjectsFactoryDataClass.Default,
						PoolManagerClass.AssemblyType.Local);
				}
			}
			else
			{
				Logger.LogWarning("PoolManagerClass singleton is unavailable; the Raid pool category could not be registered.");
			}

			var assetsManager = AssetsManagerSingletonClass.Manager;
			if (assetsManager == null)
			{
				Logger.LogWarning("AssetsManagerSingletonClass.Manager is unavailable; mannequin bundles cannot be preloaded.");
				return;
			}

			var resourceKeys = profile.GetAllPrefabPaths(true).Where(key => key != null).ToArray();
			if (resourceKeys.Length == 0)
			{
				Logger.LogWarning($"Profile {profile.Id} reported no prefab resource keys to preload.");
				return;
			}

			// PORTING NOTE (SPT 4.0.13): the long-running mystery here was that bundle loads never
			// settled - not Succeed, not Failed, just permanently pending. Confirmed via the game's own
			// Player.log what actually happens. The very first line after this method's first log
			// message is:
			//
			//   The AssetBundle '.../StreamingAssets/Windows/cubemaps' can't be loaded because another
			//   AssetBundle with the same files is already loaded.
			//   Error while getting Asset Bundle: ...
			//
			// 59 of those, all inside this mod's spawn windows and none before it ran. Loading a
			// character bundle pulls in its dependencies (cubemaps, shaders, per-weapon texture
			// client_assets, physicsmaterials), the hideout already has those resident, Unity refuses to
			// load a second copy, and the operation dies on the dependency without ever completing - so
			// the character bundle itself is never loaded and LocalPlayer.Create throws "<head> is not
			// loaded".
			//
			// So the fix is not a different entry point or a longer wait, it is to stop asking for
			// things that are already loaded. BundlesManagerClass exposes exactly what is needed:
			// FindBundle (is it resident?), FindDependences (what does it pull in?) and
			// LoadBundleAsync(name, logErrors). Load dependencies first, skipping any that are already
			// resident, then the bundle itself.
			//
			// The string format was right all along, incidentally - SPT's own BundleManager logs these
			// as "Loading locally assets/content/characters/character/prefabs/<name>.bundle", i.e.
			// forward slashes with a .bundle suffix, which is what NormalizeBundleName builds.
			string NormalizeBundleName(ResourceKey key)
			{
				string name;
				try { name = key.ToAssetName(); }
				catch { return null; }

				if (string.IsNullOrEmpty(name))
				{
					return null;
				}

				name = name.Replace('\\', '/');
				if (!name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
				{
					name += ".bundle";
				}

				return name;
			}

			bool IsCharacterMeshBundle(string name) =>
				name.IndexOf("/characters/character/", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("/content/hands/", StringComparison.OrdinalIgnoreCase) >= 0
				|| name.IndexOf("/content/feet/", StringComparison.OrdinalIgnoreCase) >= 0;

			var bundleNames = resourceKeys
				.Select(NormalizeBundleName)
				.Where(name => !string.IsNullOrEmpty(name))
				.Distinct()
				.ToArray();

			var meshBundles = bundleNames.Where(IsCharacterMeshBundle).ToArray();
			var otherBundles = bundleNames.Where(name => !IsCharacterMeshBundle(name)).ToArray();

			Logger.LogWarning($"Preloading {bundleNames.Length} bundles for profile {profile.Id} ({resourceKeys.Length} resource keys); mesh={meshBundles.Length} other={otherBundles.Length}.");

			// PORTING NOTE (SPT 4.0.13): these waits used to be 15s and 5s, which is where the
			// "spawning takes forever" complaint came from - 20s per mannequin, six of them. Both are
			// now short on purpose. The character model comes from the player (see
			// UsePlayerCharacterModel), so its bundles are already resident and need no wait at all;
			// and the remaining entries - "cubemaps", "shaders", and the gear bundles - are exactly
			// the ones confirmed to never settle no matter how long we wait. Anything that CAN load
			// still does, because these operations keep running after the wait expires; the wait only
			// controls how long the spawn blocks before proceeding.
			await LoadBundlesAsync(meshBundles, 2, "character mesh", profile.Id, logEachFailure: true);
			await LoadBundlesAsync(otherBundles, 2, "gear", profile.Id, logEachFailure: false);
		}

		/// <summary>
		/// Issues one <c>LoadAssetAsync(ResourceKey)</c> operation per key, all concurrently, and
		/// waits up to <paramref name="timeoutSeconds"/> for them to settle. Loading by ResourceKey
		/// rather than by hand-built bundle name is the point - see the porting note in
		/// <see cref="PreloadProfileBundlesAsync"/>. Per-key operations also mean an unreachable
		/// resource only ever stalls itself.
		/// </summary>
		private async Task LoadBundlesAsync(
			string[] bundleNames,
			double timeoutSeconds,
			string label,
			string profileId,
			bool logEachFailure)
		{
			if (bundleNames.Length == 0)
			{
				return;
			}

			var bundlesManager = TryGetBundlesManager();
			if (bundlesManager == null)
			{
				Logger.LogWarning($"BundlesManager is unavailable; {label} bundles for profile {profileId} cannot be preloaded.");
				return;
			}

			// A bundle's dependencies have to be resident before it will load, but asking for one that
			// is ALREADY resident is what was killing these operations - so build the full set
			// (dependencies first, then the bundle) and drop everything already loaded.
			var wanted = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			void Want(string name)
			{
				if (string.IsNullOrEmpty(name) || !seen.Add(name))
				{
					return;
				}

				try
				{
					if (bundlesManager.FindBundle(name) != null)
					{
						return; // already resident - requesting it again is the error we are avoiding
					}
				}
				catch (Exception ex)
				{
					Logger.LogDebug($"FindBundle('{name}') threw: {ex.Message}");
				}

				wanted.Add(name);
			}

			foreach (var name in bundleNames)
			{
				try
				{
					var dependencies = bundlesManager.FindDependences(name);
					if (dependencies != null)
					{
						foreach (var dependency in dependencies)
						{
							Want(dependency);
						}
					}
				}
				catch (Exception ex)
				{
					Logger.LogDebug($"FindDependences('{name}') threw: {ex.Message}");
				}

				Want(name);
			}

			if (wanted.Count == 0)
			{
				Logger.LogWarning($"{label} for profile {profileId}: all {bundleNames.Length} already resident, nothing to load.");
				return;
			}

			var pending = new List<PendingBundleLoad>(wanted.Count);
			foreach (var name in wanted)
			{
				try
				{
					var load = new PendingBundleLoad
					{
						Name = name,
						Tcs = new TaskCompletionSource<bool>(),
					};

					load.Operation = bundlesManager.LoadBundleAsync(name, false);
					StartCoroutine(DriveOperationCoroutine(load.Operation, load.Tcs));
					pending.Add(load);
				}
				catch (Exception ex)
				{
					Logger.LogWarning($"Could not start a {label} load for '{name}': {ex.Message}");
				}
			}

			var allSettled = Task.WhenAll(pending.Select(p => p.Tcs.Task));
			var waitStart = DateTime.UtcNow;
			while (!allSettled.IsCompleted)
			{
				await Task.WhenAny(allSettled, Task.Delay(TimeSpan.FromMilliseconds(250)));

				if ((DateTime.UtcNow - waitStart).TotalSeconds >= timeoutSeconds)
				{
					break;
				}
			}

			var stuck = pending.Where(p => !p.Tcs.Task.IsCompleted).Select(p => p.Name).ToArray();
			var failed = pending.Where(p => p.Tcs.Task.IsCompleted && !OperationSucceeded(p.Operation)).Select(p => p.Name).ToArray();
			int loaded = pending.Count - stuck.Length - failed.Length;

			if (stuck.Length == 0 && failed.Length == 0)
			{
				Logger.LogWarning($"{label} for profile {profileId}: all {pending.Count} loaded ({seen.Count - pending.Count} were already resident).");
				return;
			}

			if (logEachFailure)
			{
				Logger.LogWarning(
					$"{label} for profile {profileId}: {loaded}/{pending.Count} loaded. " +
					$"Never settled: [{string.Join(", ", stuck)}]. Failed: [{string.Join(", ", failed)}]. Proceeding anyway.");
			}
			else
			{
				Logger.LogWarning($"{label} for profile {profileId}: {loaded}/{pending.Count} loaded ({stuck.Length} never settled, {failed.Length} failed). Proceeding anyway.");
			}
		}

		/// <summary>
		/// Resolves the BundlesManager behind the asset manager singleton. AssetsManagerSingletonClass
		/// hands back the IAssetsManager interface, which does not expose it, so reach through the
		/// concrete AssetsManagerClass.
		/// </summary>
		private static BundlesManagerClass TryGetBundlesManager()
		{
			try
			{
				return AssetsManagerSingletonClass.Manager is AssetsManagerClass concrete ? concrete.BundlesManager : null;
			}
			catch (Exception ex)
			{
				Plugin.Instance?.LoggerInstance?.LogWarning($"Could not resolve BundlesManager: {ex.Message}");
				return null;
			}
		}

		/// <summary>
		/// Reads an operation's Succeed flag without the call site having to name its exact type.
		/// LoadAssetAsync returns IOperation&lt;object&gt;, and whether that derives from the
		/// non-generic IOperation is not confirmed for this obfuscated build, so fall back to
		/// reflection rather than betting the whole build on it.
		/// </summary>
		private static bool OperationSucceeded(object operation)
		{
			if (operation == null)
			{
				return false;
			}

			if (operation is IOperation plain)
			{
				return plain.Succeed;
			}

			try
			{
				var property = operation.GetType().GetProperty("Succeed");
				return property?.GetValue(operation) is bool succeeded && succeeded;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>One in-flight bundle load started by <see cref="LoadBundlesAsync"/>.</summary>
		private sealed class PendingBundleLoad
		{
			public string Name;

			/// <summary>
			/// The IOperation. Typed as object because LoadAssetAsync returns IOperation&lt;object&gt;
			/// and this build's generic/non-generic relationship isn't confirmed - read its result
			/// through <see cref="OperationSucceeded"/>.
			/// </summary>
			public object Operation;

			public TaskCompletionSource<bool> Tcs;
		}

		// Unity's coroutine runner nests any IEnumerator that gets yielded to it, so an operation
		// passed as object still drives correctly - and taking object here keeps this usable for
		// both the generic and non-generic IOperation.
		private static IEnumerator DriveOperationCoroutine(object operation, TaskCompletionSource<bool> tcs)
		{
			yield return operation;
			tcs.TrySetResult(OperationSucceeded(operation));
		}

		/// <summary>
		/// Temporarily removes SPT's DisableDevMaskCheckPatch transpiler from LocalPlayer.Create's
		/// async state machine for the duration of the returned scope. On 4.0.13 that transpiler
		/// double-completes the task when LocalPlayer.Create is invoked outside a real raid (4.1 no
		/// longer ships it), throwing an InvalidOperationException - same bug and same fix as
		/// spt-hideout-shootout's backport.
		/// </summary>
		private static IDisposable SuppressDisableDevMaskCheckPatch()
		{
			try
			{
				// PORTING NOTE: "Struct569" is the state machine name confirmed on the client
				// spt-hideout-shootout was built and tested against; it's a numeric-suffixed
				// obfuscated name that may differ on another 4.0.13 client build. If suppression
				// silently no-ops (logged at Warning below) and LocalPlayer.Create still throws an
				// InvalidOperationException about a task already being completed, the exception's
				// stack trace will show the real nested type name to use here instead.
				var stateMachine = typeof(LocalPlayer).GetNestedType("Struct569", BindingFlags.Public | BindingFlags.NonPublic);
				var moveNext = stateMachine?.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				if (moveNext == null)
				{
					return NoopDisposable.Instance;
				}

				var info = Harmony.GetPatchInfo(moveNext);
				var devMaskPatch = info?.Transpilers.FirstOrDefault(p => p.owner == "DisableDevMaskCheckPatch");
				if (devMaskPatch == null)
				{
					return NoopDisposable.Instance;
				}

				var harmony = new Harmony(devMaskPatch.owner);
				harmony.Unpatch(moveNext, devMaskPatch.PatchMethod);
				return new RestorePatchOnDispose(harmony, moveNext, devMaskPatch.PatchMethod);
			}
			catch (Exception ex)
			{
				Instance.Logger.LogWarning($"SuppressDisableDevMaskCheckPatch failed, proceeding without suppression: {ex.Message}");
				return NoopDisposable.Instance;
			}
		}

		private sealed class NoopDisposable : IDisposable
		{
			public static readonly NoopDisposable Instance = new NoopDisposable();
			public void Dispose() { }
		}

		private sealed class RestorePatchOnDispose : IDisposable
		{
			private readonly Harmony _harmony;
			private readonly MethodBase _target;
			private readonly MethodInfo _transpiler;

			public RestorePatchOnDispose(Harmony harmony, MethodBase target, MethodInfo transpiler)
			{
				_harmony = harmony;
				_target = target;
				_transpiler = transpiler;
			}

			public void Dispose()
			{
				try
				{
					_harmony.Patch(_target, transpiler: new HarmonyMethod(_transpiler));
				}
				catch (Exception ex)
				{
					Instance.Logger.LogWarning($"Failed to restore DisableDevMaskCheckPatch: {ex.Message}");
				}
			}
		}

		public async Task<Profile> GenerateProfile(ISession session, Profile playerProfile, MannequinType mannequinType)
		{
			if (mannequinType == MannequinType.Mannequin1)
			{
				return GenerateProfileWithMannequinEquipment(playerProfile, 0);
			}
			if (mannequinType == MannequinType.Mannequin2)
			{
				return GenerateProfileWithMannequinEquipment(playerProfile, 1);
			}
			if (mannequinType == MannequinType.Mannequin3)
			{
				return GenerateProfileWithMannequinEquipment(playerProfile, 2);
			}

			var botType = GetBotType(mannequinType);
			var botProfile = await GetBotProfile(session, botType);
			UsePlayerCharacterModel(botProfile, playerProfile);
			return botProfile;
		}

		/// <summary>
		/// Replaces a bot profile's body customization (head, body, hands, feet, voice) with the
		/// hideout player's own, keeping its gear and weapon.
		/// </summary>
		/// <remarks>
		/// PORTING NOTE (SPT 4.0.13): this is a deliberate compromise, and the reason for it is
		/// worth recording. A bot's randomly assigned character bundle - e.g.
		/// assets/content/characters/character/prefabs/wild_head_1.bundle - cannot be loaded from
		/// inside the hideout on this client. Confirmed from the game's own logs: the bundle is
		/// never reported missing and never 404s, but its dependency list includes the global
		/// "cubemaps" and "shaders" bundles, which the hideout already has resident under a loader
		/// that BundlesManagerClass has no record of. Asking for them again gets Unity's "another
		/// AssetBundle with the same files is already loaded", they therefore never register, and
		/// the head bundle's own load operation waits on them forever - it reports neither Succeed
		/// nor Failed. LocalPlayer.Create then throws "&lt;head&gt; is not loaded" partway through,
		/// leaving an invisible, half-built body. Every load entry point on this client was tried
		/// (LoadBundlesAsync batched and per-bundle, LoadAssetAsync per ResourceKey, and
		/// BundlesManagerClass.LoadBundleAsync with dependency skipping) and all four dead-end at
		/// that same dependency deadlock. PoolManagerClass.LoadBundlesAndCreatePools, which the SPT
		/// 4.1 version of this mod used and which presumably handles this correctly, is uncallable
		/// here: its signature contains GDelegate62, a delegate type the CLR refuses to load.
		///
		/// The player's own character bundles, by contrast, are always resident - the player is
		/// standing in the hideout wearing them. Reusing them makes every bot type spawn reliably
		/// with a visible body that takes hits, dies and respawns. The cost is that bots wear the
		/// player's face and body rather than their own; their gear, weapon and behaviour are still
		/// the real bot's. MannequinType.Mannequin1/2/3 already worked for exactly this reason.
		/// </remarks>
		private void UsePlayerCharacterModel(Profile botProfile, Profile playerProfile)
		{
			if (botProfile?.Customization == null || playerProfile?.Customization == null)
			{
				return;
			}

			// Only ever overwrite parts the bot profile already declares. Profile.GetAllPrefabPaths
			// indexes Customization[part] directly for every EBodyModelPart, so adding or removing a
			// key here would throw KeyNotFoundException instead of being skipped.
			foreach (var part in botProfile.Customization.Keys.ToArray())
			{
				if (playerProfile.Customization.TryGetValue(part, out var playerValue))
				{
					botProfile.Customization[part] = playerValue;
				}
			}
		}

		public Profile GenerateProfileWithMannequinEquipment(Profile playerProfile, int mannequinIndex)
		{
			var profileDescriptor = GenerateMannequinProfile();

			// PORTING NOTE (SPT 4.0.13): confirmed in-game - the hardcoded Voice customization id
			// in GenerateDefaultCustomization ("5fc613c80b735e7b024c76e2") maps to a bundle
			// ("assets/content/audio/phrases/scav_1_voice.bundle") that this SPT server never
			// serves, so LocalPlayer.Create throws "...is not loaded" on every single mannequin
			// spawn, aborting construction before health/AI wiring finishes (the mannequin still
			// visibly spawns - confirmed a half-built character stays in the scene - but never
			// reacts to hits or dies). The player's own Voice id is guaranteed loadable since the
			// player is already using it live, so use that instead - same trick already applied to
			// Feet below. Applied before the early-return fallbacks so it takes effect even when
			// there's no mannequin gear to clone.
			profileDescriptor.Customization[EBodyModelPart.Voice] = playerProfile.Customization[EBodyModelPart.Voice];

			if (!playerProfile.Inventory.HideoutAreaStashes.TryGetValue(EAreaType.EquipmentPresetsStand, out var equipmentPresetsStand))
			{
				return new(profileDescriptor);
			}

			var mannequinItem = equipmentPresetsStand.Slots[mannequinIndex].ContainedItem;
			if (mannequinItem == null || mannequinItem is not CompoundItem mannequin)
			{
				return new(profileDescriptor);
			}

			// TODO use pants player assigned in mannequin customization option

			// default mannequin pants don't have holster, so pistols will fly
			// in the air somewhere near mannequin, so use pants from player

			profileDescriptor.Customization[EBodyModelPart.Feet] = playerProfile.Customization[EBodyModelPart.Feet];

			var profile = new Profile(profileDescriptor);
			var profileSlots = profile.Inventory.Equipment.Slots;
			var mannequinSlots = mannequin.Slots;

			// clone all equipment items
			for (var i = 0; i < mannequinSlots.Length; i++)
			{
				var originalItem = mannequinSlots[i].ContainedItem;
				if (originalItem != null)
				{
					var clonedItem = originalItem.CloneItem();
					profileSlots[i].ChangeContainedItemDirectly(clonedItem);
				}
			}

			return profile;
		}

		// PORTING NOTE (SPT 4.0.13): ProfileDescriptor (4.1's name) is CompleteProfileDescriptorClass
		// here - confirmed via Profile's own constructor signature. Has many more fields than 4.1's
		// version (AccountId, Skills, Hideout, Stats, TradersInfo, ...), all left at their C# default
		// (null) since this mod's original code never set them either - only Id/Info/Customization/
		// Health/Inventory are populated below.
		public CompleteProfileDescriptorClass GenerateMannequinProfile()
		{
			return new()
			{
				Id = MongoID.Generate(true),
				Info = new(),
				Customization = GenerateDefaultCustomization(),
				Health = GenerateDefaultHealth(),
				Inventory = GenerateDefaultInventory(),
			};
		}

		public static Dictionary<EBodyModelPart, MongoID> GenerateDefaultCustomization()
		{
			return new()
			{
			    { EBodyModelPart.Head, "6644d2da35d958070c02642c" },
			    { EBodyModelPart.Body, "6644d2ffd85107e63500a61c" },
			    { EBodyModelPart.Feet, "6644d32235d958070c02642e" },
			    { EBodyModelPart.Hands, "5cc2e68f14c02e28b47de290" },
			    { EBodyModelPart.Voice, "5fc613c80b735e7b024c76e2" },
			};
		}

		// PORTING NOTE (SPT 4.0.13): Profile.HealthInfo (4.1's name) is Profile.ProfileHealthClass
		// here. Its nested ValueInfo type kept its name; BodyPartInfo is ProfileBodyPartHealthClass.
		// Both nested types also gained fields this code doesn't set (ValueInfo got
		// OverDamageReceivedMultiplier/EnvironmentDamageMultiplier, left at 0f; unconfirmed whether
		// a 0 multiplier has any unwanted effect on the mannequin's damage response - check this if
		// mannequins seem unkillable or take no damage).
		public Profile.ProfileHealthClass GenerateDefaultHealth()
		{
			return new()
			{
				BodyParts = new()
				{
					{ EBodyPart.Head, NewBodyPartInfo(Mannequin_Health_Head.Value) },
					{ EBodyPart.Chest, NewBodyPartInfo(Mannequin_Health_Chest.Value) },
					{ EBodyPart.Stomach, NewBodyPartInfo(Mannequin_Health_Stomach.Value) },
					{ EBodyPart.LeftArm, NewBodyPartInfo(Mannequin_Health_Arm.Value) },
					{ EBodyPart.RightArm, NewBodyPartInfo(Mannequin_Health_Arm.Value) },
					{ EBodyPart.LeftLeg, NewBodyPartInfo(Mannequin_Health_Leg.Value) },
					{ EBodyPart.RightLeg, NewBodyPartInfo(Mannequin_Health_Leg.Value) },
				},
				Energy = NewHealthValueInfo(100),
				Hydration = NewHealthValueInfo(100),
				Temperature = NewHealthValueInfo(36.6f, 28, 40),
				Poison = NewHealthValueInfo(0, 0, 100),
			};
		}

		public static Profile.ProfileHealthClass.ProfileBodyPartHealthClass NewBodyPartInfo(float maxHealthValue)
		{
			return new() { Health = NewHealthValueInfo(maxHealthValue) };
		}

		public static Profile.ProfileHealthClass.ValueInfo NewHealthValueInfo(float maxValue)
		{
			return NewHealthValueInfo(maxValue, 0, maxValue);
		}

		public static Profile.ProfileHealthClass.ValueInfo NewHealthValueInfo(float currentValue, float minValue, float maxValue)
		{
			return new()
			{
				Current = currentValue,
				Minimum = minValue,
				Maximum = maxValue,
			};
		}

		// PORTING NOTE (SPT 4.0.13, confirmed via DumpTool IL dump of EFTInventoryClass's getters
		// and their backing method_0): the earlier FastAccess/EBoundItem guess was wrong.
		// EFTInventoryClass (4.1's InventoryDescriptor) doesn't look anything up by enum key -
		// .Equipment/.Stash/.QuestRaidItems/.QuestStashItems/.SortingTable are each a plain field
		// (InventoryDescriptorClass, InventoryDescriptorClass_1..4 respectively), and method_0
		// (called by every one of those getters first) only (re)builds all five of those fields
		// from a live game Inventory via EFTItemSerializerClass.SerializeItem - but ONLY when
		// InventoryDescriptorClass (Equipment) is still null:
		//   if (this.InventoryDescriptorClass != null) return;
		//   ... rebuilds Equipment/QuestRaidItems/QuestStashItems/Stash/SortingTable ...
		// So pre-setting InventoryDescriptorClass ourselves makes method_0 a no-op and our value
		// sticks - no FastAccess/EBoundItem involvement needed at all.
		// KNOWN GAP: this leaves Equipment.Slots empty. That's fine for the default
		// MannequinType.Scav/boss/etc. path (GetBotProfile fetches a real profile with a real
		// Inventory - GenerateDefaultInventory is never called for those). But
		// MannequinType.Mannequin1/2/3 (GenerateProfileWithMannequinEquipment, below) indexes into
		// profile.Inventory.Equipment.Slots by position to clone the player's real mannequin gear,
		// and an empty Slots list will throw ArgumentOutOfRangeException there. All 6 config slots
		// default to MannequinType.Scav, so this only matters if Mannequin1/2/3 is selected.
		public static EFTInventoryClass GenerateDefaultInventory()
		{
		    var equipment = MongoID.Generate(true);
			return new()
			{
				Gclass1390_0 = new FlatItemsDataClass[]
				{
					new() { _id = equipment, _tpl = "55d7217a4bdc2d86028b456d" },
				},
				InventoryDescriptorClass = new InventoryDescriptorClass
				{
					Id = equipment,
					TemplateId = "55d7217a4bdc2d86028b456d",
					Slots = new List<GClass1915>(),
				},
			};
		}

		public async Task<Profile> GetBotProfile(ISession session, WildSpawnType botType)
		{
			// PORTING NOTE (SPT 4.0.13): CountTypeBotWave (4.1's name) is WaveInfoClass here -
			// confirmed via SPT's own 4.0->4.1 class name mapping table, and its (count, roleType,
			// difficulty) ctor shape is identical.
			var botProfileRequest = new WaveInfoClass(1, botType, BotDifficulty.normal);
			var profilesRequest = new List<WaveInfoClass>(1) { botProfileRequest };
			var profiles = await session.LoadBots(profilesRequest);
			var botPlayerProfile = profiles[0];
			return botPlayerProfile;
		}

		public static WildSpawnType GetBotType(MannequinType mannequinType)
		{
			return mannequinType switch
			{
				MannequinType.Scav => WildSpawnType.assault,
				MannequinType.ScavSniper => WildSpawnType.marksman,
				MannequinType.Raider => WildSpawnType.pmcBot,

				MannequinType.BEAR => WildSpawnType.pmcBEAR,
				MannequinType.USEC => WildSpawnType.pmcUSEC,

				MannequinType.Reshala => WildSpawnType.bossBully,
				MannequinType.ReshalaGuard => WildSpawnType.followerBully,

				MannequinType.Shturman => WildSpawnType.bossKojaniy,
				MannequinType.ShturmanGuard => WildSpawnType.followerKojaniy,

				MannequinType.Sanitar => WildSpawnType.bossSanitar,
				MannequinType.SanitarGuard => WildSpawnType.followerSanitar,

				MannequinType.Gluhar => WildSpawnType.bossGluhar,
				MannequinType.GluharGuardAssault => WildSpawnType.followerGluharAssault,
				MannequinType.GluharGuardSecurity => WildSpawnType.followerGluharSecurity,
				MannequinType.GluharGuardScout => WildSpawnType.followerGluharScout,
				MannequinType.GluharGuardSnipe => WildSpawnType.followerGluharSnipe,

				MannequinType.Killa => WildSpawnType.bossKilla,
				MannequinType.KillaLabyrinth => WildSpawnType.bossKillaAgro,

				MannequinType.Tagilla => WildSpawnType.bossTagilla,
				MannequinType.TagillaLabyrinth => WildSpawnType.bossTagillaAgro,

				MannequinType.Rogue => WildSpawnType.exUsec,
				MannequinType.Knight => WildSpawnType.bossKnight,
				MannequinType.BigPipe => WildSpawnType.followerBigPipe,
				MannequinType.BirdEye => WildSpawnType.followerBirdEye,

				MannequinType.CultistWarrior => WildSpawnType.sectantWarrior,
				MannequinType.CultistPriest => WildSpawnType.sectantPriest,

				MannequinType.Zryachiy => WildSpawnType.bossZryachiy,
				MannequinType.ZryachiyGuard => WildSpawnType.followerZryachiy,

				MannequinType.Kaban => WildSpawnType.bossBoar,
				MannequinType.KabanGuardBasmach => WildSpawnType.followerBoarClose1,
				MannequinType.KabanGuardGus => WildSpawnType.followerBoarClose2,
				MannequinType.KabanGuard => WildSpawnType.followerBoar,
				MannequinType.KabanGuardSniper => WildSpawnType.bossBoarSniper,

				MannequinType.Kolontay => WildSpawnType.bossKolontay,
				MannequinType.KolontayGuardAssault => WildSpawnType.followerKolontayAssault,
				MannequinType.KolontayGuardSecurity => WildSpawnType.followerKolontaySecurity,

				MannequinType.Partisan => WildSpawnType.bossPartisan,

				_ => throw new ArgumentException($"Unknown mannequin type: {mannequinType}"),
			};
		}

		public static string[] ShootingRangeTargets =
		[
			"Rail_targets/01_rail_target/Shooting_range_rails_02/Shooting_range_target_rails",
			"Rail_targets/02_rail_target/Shooting_range_rails_02 (1)/Shooting_range_target_rails",
			"Rail_targets/03_rail_target/Shooting_range_rails_02 (2)/Shooting_range_target_rails",
			"Popper_targets",
			"Target_stand_changed (1)",
			"Target_stand_changed (2)",
			"Target_stand_changed (3)",
			"Target_stand_changed (4)",
			"metal_target (1)",
			"metal_target (2)",
		];

		public void HideShootingRangeTargets(HideoutController __instance)
		{
			if (!__instance.Areas.TryGetValue(EAreaType.ShootingRange, out var shootingRange))
			{
				return;
			}

			var areaLevel = shootingRange.CurrentLevel;
			if (!areaLevel)
			{
				return;
			}

			if (areaLevel == shootingRange.AreaLevels[0])
			{
				// level 0, no shooting range
				return;
			}

			StartCoroutine(FindAndDisableTargets(areaLevel.HighlightTransform));
			StartCoroutine(SpawnInitialBots());
		}

		public IEnumerator FindAndDisableTargets(Transform targetsRoot)
		{
			foreach (var targetPath in ShootingRangeTargets)
			{
				var targetTransform = targetsRoot.Find(targetPath);
				if (targetTransform)
				{
					targetTransform.gameObject.SetActive(false);
					yield return null;
				}
			}
		}

		public IEnumerator SpawnInitialBots()
		{
			yield return new WaitForSeconds(1f);

			var closeLeft = new MannequinData(new(-4f, 0.01f, 16.2f), CloseLeftMannequinType);
			var closeMiddle = new MannequinData(new(-2.9f, 0.01f, 23.75f), CloseMiddleMannequinType);
			var closeRight = new MannequinData(new(-1.65f, 0.01f, 30.22f), CloseRightMannequinType);

			var farLeft = new MannequinData(new(-4.95f, 0.01f, 57.48f), FarLeftMannequinType);
			var farMiddle = new MannequinData(new(-2.75f, 0.01f, 57.47f), FarMiddleMannequinType);
			var farRight = new MannequinData(new(-0.56f, 0.01f, 57.47f), FarRightMannequinType);

			// PORTING NOTE (SPT 4.0.13): confirmed in-game - firing all 6 SpawnBot calls at once
			// (as 4.1's original fire-and-forget code did) makes their bundle preloads compete for
			// the asset manager at the same time, and PreloadProfileBundlesAsync's 20s timeout was
			// hit for every single one, with LocalPlayer.Create then throwing on whichever bundle
			// (usually a randomized "wild_head_N"/"wild_head_misha" scav head variant) hadn't
			// finished loading yet. Waiting for each mannequin's full spawn to finish before
			// starting the next removes the contention entirely.
			// PORTING NOTE (SPT 4.0.13): confirmed in-game - some randomly-assigned bot appearances
			// (a mod-added head/body template with a broken bundle reference) can make the whole
			// SpawnBot Task hang indefinitely - not throw, not time out via
			// PreloadProfileBundlesAsync's own 90s cutoff, just never complete (ModProfiler showed
			// WaitForTask spinning every frame with SpawnBot's Task never finishing, for the entire
			// rest of the session). Since that hang can happen inside game/BSG code this mod doesn't
			// control (IAssetsManager.LoadBundlesAsync's own operation, or LocalPlayer.Create itself),
			// a hard per-mannequin wall-clock cap here is the only way to guarantee one bad slot
			// can't block the other 5 forever - the abandoned Task keeps running in the background
			// and is simply never awaited past this point.
			foreach (var data in new[] { closeLeft, closeMiddle, closeRight, farLeft, farMiddle, farRight })
			{
				yield return WaitForTaskOrTimeout(SpawnBot(data), 120f);
			}
		}

		private IEnumerator WaitForTaskOrTimeout(Task task, float timeoutSeconds)
		{
			float elapsed = 0f;
			while (!task.IsCompleted && elapsed < timeoutSeconds)
			{
				yield return null;
				elapsed += Time.deltaTime;
			}

			if (!task.IsCompleted)
			{
				Logger.LogWarning($"A mannequin spawn did not finish within {timeoutSeconds}s; abandoning it and moving on to the next slot.");
			}
		}

		public void OnBotDeath(LocalPlayer bot)
		{
			StartCoroutine(DespawnBotSpawnAnotherOne(bot));
		}

		public IEnumerator DespawnBotSpawnAnotherOne(LocalPlayer bot)
		{
			if (!Mannequins.Remove(bot, out var mannequinData))
			{
				yield break;
			}

			yield return new WaitForSeconds(0.5f);

			bot.Dispose();
			AssetPoolObject.ReturnToPool(bot.gameObject, true);

			yield return new WaitForSeconds(0.5f);

			SpawnBot(mannequinData);
		}
    }

    public static class R
    {
        public static V GetField<T, V>(this T instance, string fieldName)
        {
            return (V)AccessTools.Field(typeof(T), fieldName).GetValue(instance);
        }

        public static V GetField<T, V>(string fieldName)
        {
            return (V)AccessTools.Field(typeof(T), fieldName).GetValue(null);
        }
    }

}

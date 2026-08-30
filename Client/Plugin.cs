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
using EFT.Interactive;
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

	public enum MannequinPose
	{
		Standing,
		Crouching,
		Prone,
	}

	public readonly record struct MannequinData
	(
		Vector3 Position,
		ConfigEntry<MannequinPose> Pose
	);

    [BepInPlugin("7Bpencil.TargetDummies", "7Bpencil.TargetDummies", "0.2.1")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance;
		public ManualLogSource LoggerInstance;

		public ConfigEntry<bool> DebugLogging;
		public ConfigEntry<bool> SpawnUnarmored;
		public ConfigEntry<bool> ForceWeaponLightsOff;
		public ConfigEntry<float> SpawnInterval;
		public ConfigEntry<float> RespawnDelay;
		public ConfigEntry<string> FallbackMeleeTemplateId;

		public ConfigEntry<KeyboardShortcut> RefreshHotkey;

		public ConfigEntry<MannequinPose> CloseRowPose;
		public ConfigEntry<MannequinPose> FarRowPose;

		public ConfigEntry<float> Mannequin_Health_Head;
		public ConfigEntry<float> Mannequin_Health_Chest;
		public ConfigEntry<float> Mannequin_Health_Stomach;
		public ConfigEntry<float> Mannequin_Health_Arm;
		public ConfigEntry<float> Mannequin_Health_Leg;

		public Dictionary<LocalPlayer, MannequinData> Mannequins;

		/// <summary>
		/// The six fixed slots. Refresh rebuilds from this rather than from <see cref="Mannequins"/>,
		/// which only ever holds the mannequins that are currently alive.
		/// </summary>
		private MannequinData[] _slots;

		/// <summary>In-flight respawn coroutines, so a refresh can cancel them instead of racing.</summary>
		private readonly List<Coroutine> _respawnRoutines = new();

		/// <summary>
		/// Bumped by every refresh. A respawn that started before the current generation belongs to
		/// a slot the refresh has already refilled, so it must not spawn a second mannequin into it.
		/// Backs up StopCoroutine, which cannot cancel a coroutine already past its last yield.
		/// </summary>
		private int _refreshGeneration;

        private void Awake()
        {
            Instance = this;
			LoggerInstance = Logger;

			// A button rather than a value: pressing it rebuilds every mannequin from your current
			// gear, so you do not have to run a raid or restart the game to see a loadout change.
			Config.Bind<string>("Mannequin Settings", "Refresh Mannequins", "", new ConfigDescription(
				"Respawn all mannequins now, using the gear you are currently wearing.",
				null,
				new ConfigurationManagerAttributes
				{
					Order = 10,
					HideDefaultButton = true,
					HideSettingName = true,
					CustomDrawer = _ => DrawRefreshButton(),
				}));

			RefreshHotkey = Config.Bind<KeyboardShortcut>("Mannequin Settings", "Refresh Hotkey", new KeyboardShortcut(KeyCode.F6), new ConfigDescription(
				"Key that respawns all mannequins with your current gear, without opening this menu.",
				null, new ConfigurationManagerAttributes { Order = 9 }));

			SpawnUnarmored = Config.Bind<bool>("Mannequin Settings", "Spawn Unarmored", false, new ConfigDescription(
				"Spawn mannequins with no gear at all, keeping only the melee weapon from your Scabbard slot. "
				+ "The knife is deliberately kept: a mannequin with completely empty hands has no hands controller, "
				+ "which makes the game throw a NullReferenceException every frame.",
				null, new ConfigurationManagerAttributes { Order = 8 }));

			ForceWeaponLightsOff = Config.Bind<bool>("Mannequin Settings", "Force Weapon Lights Off", true, new ConfigDescription(
				"Turn off weapon flashlights and lasers on mannequins, so a light you happen to be carrying does not blind you downrange.",
				null, new ConfigurationManagerAttributes { Order = 7 }));

			SpawnInterval = Config.Bind<float>("Mannequin Settings", "Spawn Interval", 0.5f, new ConfigDescription(
				"Seconds between one mannequin and the next when all six are being spawned at once - entering the "
				+ "shooting range, or pressing Refresh. Does not affect respawns after a kill; that is Respawn Delay. "
				+ "Lower is faster but packs more work into a single frame.",
				new AcceptableValueRange<float>(0.1f, 3f), new ConfigurationManagerAttributes { Order = 6 }));

			RespawnDelay = Config.Bind<float>("Mannequin Settings", "Respawn Delay", 5f, new ConfigDescription(
				"Seconds from a mannequin dying to its replacement appearing. The body lies there for this whole "
				+ "time, so it also decides how long you get to look at the corpse. The lower bound is 2s on "
				+ "purpose: HollywoodFX waits for the ragdoll to settle before playing its blood, so clearing the "
				+ "body sooner than that cuts the effect off before it starts.",
				new AcceptableValueRange<float>(2f, 30f), new ConfigurationManagerAttributes { Order = 8 }));

			FallbackMeleeTemplateId = Config.Bind<string>("Mannequin Settings", "Fallback Melee Template Id", "54491bb74bdc2d09088b4567", new ConfigDescription(
				"Item template id given to an unarmored mannequin when you are not carrying a melee weapon yourself. "
				+ "Something has to be in its hands - empty hands mean no hands controller, which throws a "
				+ "NullReferenceException every frame. Change this if the default does not resolve on your install.",
				null, new ConfigurationManagerAttributes { Order = 5 }));

			CloseRowPose = Config.Bind<MannequinPose>("Close", "Pose", MannequinPose.Standing, new ConfigDescription(
				"Pose for all three mannequins in the near row.", null, new ConfigurationManagerAttributes { Order = 1 }));

			FarRowPose = Config.Bind<MannequinPose>("Far", "Pose", MannequinPose.Standing, new ConfigDescription(
				"Pose for all three mannequins in the far row.", null, new ConfigurationManagerAttributes { Order = 1 }));

			DebugLogging = Config.Bind<bool>("Debug", "Debug Logging", false, new ConfigDescription(
				"Log every spawn step in detail. Leave off unless you are diagnosing a problem - it is noisy.",
				null, new ConfigurationManagerAttributes { Order = 1 }));

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

			var botPlayerProfile = GenerateProfileFromPlayerLoadout(hideoutGame.Profile_0);

			// PORTING NOTE (SPT 4.0.13): a prior version of this port stripped EBodyModelPart.Hands
			// from the profile's Customization here, copying a fix from spt-hideout-shootout's own
			// backport (there, the hands-rig bundle is never loadable outside a real raid's loading
			// screen). Confirmed WRONG for this mod: Profile.GetAllPrefabPaths iterates all
			// EBodyModelPart values and indexes Customization[part] directly, so removing an entry
			// throws KeyNotFoundException instead of skipping it - the exact opposite of the
			// intended effect. TargetDummies also mostly spawns real WildSpawnType bot profiles
			// fetched via session.LoadBots (not hand-built ones like hideout-shootout's scav
			// target), which should carry a legitimately loadable Hands entry already. Removed;
			// if hands still fail to load in-game, that shows as a warning rather than a crash.

			// PORTING NOTE (SPT 4.0.13): ObjectsFactory.LoadBundlesAndCreatePools (4.1's name for
			// this singleton/method pair) doesn't exist under that name here, and PoolManagerClass's
			// equivalent is uncallable because its signature contains GDelegate62, a delegate type
			// the CLR refuses to load. Only the pool registration half is needed now - the bundles
			// themselves are resident, because every mannequin is a copy of the player.
			EnsureRaidPoolsRegistered();

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
					Logger.LogWarning($"LocalPlayer.Create failed ({ex.GetType().Name}: {ex.Message}); rebuilding the profile and retrying once.");

					// Clean up whatever the failed attempt left behind before trying again.
					DestroyOrphanedPlayers(playersBeforeCreate);

					botPlayerId = UnityEngine.Random.Range(100000, int.MaxValue);
					botPlayerProfile = GenerateProfileFromPlayerLoadout(hideoutGame.Profile_0);
					EnsureRaidPoolsRegistered();
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
			// PORTING NOTE (SPT 4.0.13): pick a slot that actually holds something. This was hardcoded
			// to FirstPrimaryWeapon, so in unarmored mode - where the only item is the melee weapon -
			// it put nothing in the mannequin's hands at all: the knife sat on its belt and the
			// mannequin stood there empty-handed.
			var handsSlot = FindSlotToHold(botPlayer);

			try
			{
				botPlayer.SetSlotItem(handsSlot, (_) => { });
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not put the {handsSlot} item in mannequin {botPlayerProfile.Id}'s hands ({ex.GetType().Name}: {ex.Message}); giving it empty hands instead.");

				// PORTING NOTE (SPT 4.0.13): a bot left with NO hands controller at all
				// NullReferences every single frame in MovementContext -> Player.MouseLook ->
				// Player.LateUpdate, which floods the log and costs framerate for as long as the
				// mannequin exists. Establishing an empty-hands controller gives MouseLook the state
				// it dereferences. Done by reflection because the callback parameter's exact type
				// is not confirmed for this build and a wrong guess would break the whole build.
				TrySetEmptyHands(botPlayer);
			}

			ApplyPose(botPlayer, data.Pose.Value);

			DebugLog($"Spawned mannequin for profile {botPlayerProfile.Id} at {data.Position} ({data.Pose.Value}).");

			}
			catch (Exception e)
			{
				Logger.LogError(e);
				DestroyOrphanedPlayers(playersBeforeCreate);
			}
		}

		/// <summary>
		/// Removes a mannequin from the world: its corpse first, then the player object itself.
		/// </summary>
		/// <remarks>
		/// PORTING NOTE (SPT 4.0.13): destroying the corpse is the part that was missing. Disposing
		/// the player and returning it to the pool leaves the Corpse loot item behind in
		/// GameWorld.LootList, so the next mannequin spawned at that slot stands inside the previous
		/// one's body. In-game that showed up as a live, aiming mannequin carrying "search"/"corpse"
		/// interaction prompts, and as bodies being flung across the room by the overlapping
		/// colliders. They also stacked up, one more per kill, for the whole session.
		/// </remarks>
		private void DespawnMannequin(LocalPlayer bot)
		{
			if (bot == null)
			{
				return;
			}

			// PORTING NOTE (SPT 4.0.13): order matters here, and getting it wrong is what left
			// weapons hanging in mid-air. The Corpse owns the dead mannequin's body, so destroying
			// it first tore that body down and the following Dispose() then threw
			// NullReferenceException - once per despawn, confirmed in-game - which meant the pooling
			// step never ran and the held weapon was never cleaned up. So: find the corpse first,
			// dispose the player, and only then destroy the corpse.
			Corpse corpse = FindCorpseOf(bot);

			try
			{
				bot.Dispose();
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not dispose a mannequin: {ex.GetType().Name}: {ex.Message}");
			}

			// PORTING NOTE (SPT 4.0.13): destroy the body rather than returning it to the pool, and
			// this is load-bearing for the death effects. An A/B in-game settled it: in the build
			// where Dispose() happened to throw - so ReturnToPool never ran - kills produced blood;
			// the moment that throw was fixed and pooling actually started working, blood stopped.
			// A pooled body is handed straight back out for the next mannequin, carrying
			// HollywoodFX's spent gore state with it (its gore components detach on disable), so
			// every reused body is already poisoned. A fresh object per spawn is what makes the
			// effects play, and unlike the accidental version it leaves nothing floating behind.
			try
			{
				UnityEngine.Object.Destroy(bot.gameObject);
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not destroy a mannequin's body: {ex.GetType().Name}: {ex.Message}");
			}

			DestroyCorpse(corpse);
		}

		/// <summary>Destroys every corpse in the hideout - all of them are mannequins.</summary>
		private void DestroyAllCorpses()
		{
			try
			{
				var gameWorld = Singleton<GameWorld>.Instance;
				if (gameWorld?.LootList == null)
				{
					return;
				}

				foreach (var corpse in gameWorld.LootList.OfType<Corpse>().ToArray())
				{
					gameWorld.DestroyLoot(corpse);
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not clear the corpses: {ex.GetType().Name}: {ex.Message}");
			}
		}

		/// <summary>
		/// Finds the corpse belonging to a mannequin, by object identity.
		/// </summary>
		/// <remarks>
		/// PORTING NOTE (SPT 4.0.13): this used to fall back to matching by position, and that was
		/// wrong. A ragdoll slides and gets shoved around as it falls, so by the time the body is
		/// cleaned up it is no longer where the mannequin stood - the match missed, the corpse
		/// survived, and the next mannequin spawned inside it. Widening the radius is not a fix
		/// either: the two closest slots are only 2.19m apart, so a radius large enough to survive a
		/// ragdoll sliding is also large enough to delete the neighbour's body.
		///
		/// A corpse is built from the dead player's own body object, so hierarchy identity settles
		/// it exactly and cannot drift. Profile id is tried first where the corpse exposes one.
		/// There is deliberately no distance fallback: when nothing matches, nothing is destroyed.
		/// </remarks>
		private Corpse FindCorpseOf(LocalPlayer bot)
		{
			try
			{
				var gameWorld = Singleton<GameWorld>.Instance;
				if (gameWorld?.LootList == null || bot == null)
				{
					return null;
				}

				string profileId = bot.ProfileId;
				Transform botRoot = bot.gameObject != null ? bot.gameObject.transform : null;

				foreach (var corpse in gameWorld.LootList.OfType<Corpse>().ToArray())
				{
					if (corpse == null)
					{
						continue;
					}

					if (CorpseHasProfileId(corpse, profileId) || SharesHierarchy(corpse, botRoot))
					{
						return corpse;
					}
				}
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not find a mannequin's corpse: {ex.GetType().Name}: {ex.Message}");
			}

			return null;
		}

		/// <summary>Destroys a corpse previously found by <see cref="FindCorpseOf"/>.</summary>
		private void DestroyCorpse(Corpse corpse)
		{
			if (corpse == null)
			{
				return;
			}

			try
			{
				DebugLog("Destroying a mannequin's corpse.");
				Singleton<GameWorld>.Instance?.DestroyLoot(corpse);
			}
			catch (Exception ex)
			{
				Logger.LogWarning($"Could not destroy a mannequin's corpse: {ex.GetType().Name}: {ex.Message}");
			}
		}

		/// <summary>True if the corpse's object is the mannequin's body, or sits inside it.</summary>
		private static bool SharesHierarchy(Corpse corpse, Transform botRoot)
		{
			if (botRoot == null)
			{
				return false;
			}

			try
			{
				Transform corpseTransform = corpse.transform;
				return corpseTransform != null
					&& (corpseTransform.IsChildOf(botRoot) || botRoot.IsChildOf(corpseTransform));
			}
			catch
			{
				return false;
			}
		}

		/// <summary>True if anything the corpse exposes carries this profile id.</summary>
		private static bool CorpseHasProfileId(Corpse corpse, string profileId)
		{
			if (string.IsNullOrEmpty(profileId))
			{
				return false;
			}

			foreach (var member in corpse.GetType().GetMembers(BindingFlags.Public | BindingFlags.Instance))
			{
				object value = null;
				try
				{
					value = (member as PropertyInfo)?.GetValue(corpse)
						?? (member as FieldInfo)?.GetValue(corpse);
				}
				catch { }

				if (value == null || value is string)
				{
					continue;
				}

				string candidate = null;
				try
				{
					candidate = value.GetType().GetProperty("ProfileId", BindingFlags.Public | BindingFlags.Instance)
						?.GetValue(value) as string;
				}
				catch { }

				if (string.Equals(candidate, profileId, StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// Puts a spawned mannequin into the configured stance.
		/// </summary>
		/// <remarks>
		/// Reflection-based: MovementContext's pose members are obfuscated differently between
		/// builds, so each candidate is probed by name and a miss degrades to a debug line rather
		/// than breaking the build or throwing into the spawn path.
		/// </remarks>
		private void ApplyPose(LocalPlayer botPlayer, MannequinPose pose)
		{
			try
			{
				var movementContext = botPlayer.GetType()
					.GetProperty("MovementContext", BindingFlags.Public | BindingFlags.Instance)
					?.GetValue(botPlayer);

				if (movementContext == null)
				{
					DebugLog("Player.MovementContext was not found; mannequin pose left as spawned.");
					return;
				}

				var contextType = movementContext.GetType();

				if (pose == MannequinPose.Prone)
				{
					// Prone is a separate state from the standing/crouching pose level.
					var setProne = contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
						.FirstOrDefault(m => (m.Name == "SetProne" || m.Name == "TryProne")
							&& m.GetParameters().Length <= 1);

					if (setProne != null)
					{
						var args = setProne.GetParameters().Length == 1 ? new object[] { true } : Array.Empty<object>();
						setProne.Invoke(movementContext, args);
						return;
					}

					var proneFlag = contextType.GetProperty("IsInPronePose", BindingFlags.Public | BindingFlags.Instance);
					if (proneFlag != null && proneFlag.CanWrite)
					{
						proneFlag.SetValue(movementContext, true);
						return;
					}

					DebugLog("No prone member found on MovementContext; mannequin left standing.");
					return;
				}

				// 1 is fully upright, 0 is fully crouched.
				float poseLevel = pose == MannequinPose.Crouching ? 0f : 1f;

				var setPoseLevel = contextType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
					.FirstOrDefault(m => m.Name == "SetPoseLevel"
						&& m.GetParameters().Length >= 1
						&& m.GetParameters()[0].ParameterType == typeof(float));

				if (setPoseLevel != null)
				{
					var parameters = setPoseLevel.GetParameters();
					var args = new object[parameters.Length];
					args[0] = poseLevel;
					for (int i = 1; i < parameters.Length; i++)
					{
						args[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : (object)true;
					}

					setPoseLevel.Invoke(movementContext, args);
					return;
				}

				var poseProperty = contextType.GetProperty("PoseLevel", BindingFlags.Public | BindingFlags.Instance);
				if (poseProperty != null && poseProperty.CanWrite)
				{
					poseProperty.SetValue(movementContext, poseLevel);
					return;
				}

				DebugLog("No pose-level member found on MovementContext; mannequin pose left as spawned.");
			}
			catch (Exception ex)
			{
				DebugLog($"Could not set mannequin pose to {pose}: {ex.GetType().Name}: {ex.Message}");
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

					DebugLog($"Destroying the half-constructed player object LocalPlayer.Create left behind ('{player.name}'), to stop its per-frame LateUpdate NullReferenceExceptions.");

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
		/// Makes sure the Raid pool category exists before LocalPlayer.Create runs, since that is
		/// where it pops the player object from.
		/// </summary>
		/// <remarks>
		/// PORTING NOTE (SPT 4.0.13): this used to also preload the profile's bundles, and that is
		/// now deliberately gone. Once every mannequin became a copy of the player (see
		/// GenerateProfileFromPlayerLoadout) every bundle it needs is resident by definition, and
		/// the preload's own logs proved it had stopped doing anything: "gear: 0/170 loaded,
		/// 2 already resident, 170 not loaded", on every single mannequin, while the mannequins
		/// themselves rendered perfectly. BundlesManagerClass simply has no record of bundles the
		/// hideout loaded through another path, so FindBundle reports them missing and every
		/// re-request then fails.
		///
		/// What it did cost was the remaining spawn hitch: ~170 doomed LoadBundleAsync operations
		/// per mannequin, each with a coroutine pumping it every frame until the wait expired, six
		/// times over, plus 4 seconds of blocking per mannequin.
		/// </remarks>
		private void EnsureRaidPoolsRegistered()
		{
			if (!Singleton<PoolManagerClass>.Instantiated)
			{
				Logger.LogWarning("PoolManagerClass singleton is unavailable; the Raid pool category could not be registered.");
				return;
			}

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

		/// <summary>
		/// Builds a mannequin profile that is a copy of the player's own character - their
		/// appearance and the gear they are currently carrying.
		/// </summary>
		/// <remarks>
		/// PORTING NOTE (SPT 4.0.13): this replaces the per-slot MannequinType options, and the reason
		/// is that no other source of appearance works reliably in the hideout on this client.
		///
		/// Real bot profiles (Scav, Tagilla, Raider, ...) need character and gear bundles that are not
		/// resident, and those cannot be loaded from here: a bundle's dependency list includes the
		/// global "cubemaps" and "shaders" bundles, which the hideout already holds under a loader
		/// BundlesManagerClass has no record of, so re-requesting them gets Unity's "another
		/// AssetBundle with the same files is already loaded", they never register, and the dependent
		/// load waits on them forever. Every entry point was tried - LoadBundlesAsync batched and
		/// per-bundle, LoadAssetAsync per ResourceKey, BundlesManagerClass.LoadBundleAsync with
		/// dependency skipping - and all of them dead-end there. PoolManagerClass.LoadBundlesAndCreatePools,
		/// which the SPT 4.1 version used, is uncallable because its signature contains GDelegate62,
		/// a delegate type the CLR refuses to load.
		///
		/// The decorative Equipment Presets Stand mannequin skin loads fine, but it is a display prop
		/// with no ballistic material tagging, so shots produced no flinch and no blood.
		///
		/// The player's own character has neither problem: its bundles are resident by definition, and
		/// it is a real combat model with correct hit materials. It also makes the weapon actually go
		/// into the mannequin's hands, which stops the MouseLook NullReferenceException that a bot with
		/// no hands controller threw every single frame - 10,827 of them in one session, which is what
		/// was costing the framerate.
		/// </remarks>
		public Profile GenerateProfileFromPlayerLoadout(Profile playerProfile)
		{
			bool unarmored = SpawnUnarmored.Value;

			// Only inject a stand-in melee when it is actually needed: unarmored mode, and the player
			// has nothing in their own Scabbard for the mannequin to copy.
			string fallbackMelee = unarmored && FindScabbardItem(playerProfile) == null
				? FallbackMeleeTemplateId.Value
				: null;

			var profileDescriptor = GenerateMannequinProfile(fallbackMelee);

			// Take the player's whole appearance - head, body, hands, feet, voice. Every one of these
			// bundles is guaranteed loadable because the player is standing in the hideout wearing them.
			foreach (var part in profileDescriptor.Customization.Keys.ToArray())
			{
				if (playerProfile.Customization.TryGetValue(part, out var playerValue))
				{
					profileDescriptor.Customization[part] = playerValue;
				}
			}

			var profile = new Profile(profileDescriptor);

			var profileSlots = profile.Inventory?.Equipment?.Slots;
			var playerSlots = playerProfile.Inventory?.Equipment?.Slots;
			if (profileSlots == null || playerSlots == null)
			{
				return profile;
			}

			// Clone whatever the player is actually carrying. Bounded by both arrays: the two profiles
			// are not guaranteed to declare the same number of equipment slots, and indexing past the
			// end would throw ArgumentOutOfRangeException.
			int slotCount = Math.Min(profileSlots.Length, playerSlots.Length);
			for (var i = 0; i < slotCount; i++)
			{
				var originalItem = playerSlots[i].ContainedItem;
				if (originalItem == null)
				{
					continue;
				}

				// In unarmored mode the mannequin keeps only its melee weapon. Empty hands are not an
				// option: a mannequin with no hands controller makes the game throw a
				// NullReferenceException out of MouseLook every single frame.
				if (unarmored && !IsScabbardSlot(playerSlots[i]))
				{
					continue;
				}

				try
				{
					var clonedItem = originalItem.CloneItem();

					if (ForceWeaponLightsOff.Value)
					{
						TurnOffLights(clonedItem);
					}

					profileSlots[i].ChangeContainedItemDirectly(clonedItem);
				}
				catch (Exception ex)
				{
					Logger.LogWarning($"Could not clone the player's item in equipment slot {i} onto the mannequin: {ex.Message}");
				}
			}

			return profile;
		}

		/// <summary>
		/// Picks the equipment slot whose item the mannequin should hold: a primary weapon if it has
		/// one, otherwise a sidearm, otherwise its melee weapon.
		/// </summary>
		private EquipmentSlot FindSlotToHold(LocalPlayer botPlayer)
		{
			var preference = new[]
			{
				EquipmentSlot.FirstPrimaryWeapon,
				EquipmentSlot.SecondPrimaryWeapon,
				EquipmentSlot.Holster,
				EquipmentSlot.Scabbard,
			};

			try
			{
				var equipment = botPlayer.Profile?.Inventory?.Equipment;
				if (equipment != null)
				{
					foreach (var slot in preference)
					{
						if (equipment.GetSlot(slot)?.ContainedItem != null)
						{
							return slot;
						}
					}
				}
			}
			catch (Exception ex)
			{
				DebugLog($"Could not inspect the mannequin's equipment slots: {ex.GetType().Name}: {ex.Message}");
			}

			return EquipmentSlot.FirstPrimaryWeapon;
		}

		/// <summary>The player's own melee weapon, or null if they are not carrying one.</summary>
		private static Item FindScabbardItem(Profile playerProfile)
		{
			var slots = playerProfile?.Inventory?.Equipment?.Slots;
			if (slots == null)
			{
				return null;
			}

			foreach (var slot in slots)
			{
				if (IsScabbardSlot(slot))
				{
					return slot.ContainedItem;
				}
			}

			return null;
		}

		/// <summary>True if this is the melee slot - the one item kept in unarmored mode.</summary>
		private static bool IsScabbardSlot(Slot slot)
		{
			try
			{
				return string.Equals(slot?.ID, "Scabbard", StringComparison.OrdinalIgnoreCase);
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Switches off every flashlight and laser on an item and its attachments, so a mannequin
		/// built from a weapon you happen to be carrying does not shine it back down the range.
		/// </summary>
		/// <remarks>
		/// PORTING NOTE (SPT 4.0.13): resolved entirely by name, because the first attempt guessed
		/// wrong - it looked for Item.GetItemComponentsInChildren, which this build does not have
		/// ("Item.GetItemComponentsInChildren was not found" on every spawn). Rather than guess
		/// again, walk the item's own slot tree by hand and probe each item's components through
		/// whichever accessor exists. When nothing matches, the available member names are logged
		/// once so the correct one can be read straight out of the log.
		/// </remarks>
		private void TurnOffLights(Item item)
		{
			if (item == null)
			{
				return;
			}

			try
			{
				foreach (var current in EnumerateItemTree(item))
				{
					TurnOffLightsOnSingleItem(current);
				}
			}
			catch (Exception ex)
			{
				DebugLog($"Could not switch off weapon lights: {ex.GetType().Name}: {ex.Message}");
			}
		}

		/// <summary>Walks an item and everything attached into its slots, depth first.</summary>
		private static IEnumerable<Item> EnumerateItemTree(Item root)
		{
			var pending = new Stack<Item>();
			pending.Push(root);

			while (pending.Count > 0)
			{
				var current = pending.Pop();
				if (current == null)
				{
					continue;
				}

				yield return current;

				Slot[] slots = null;
				try { slots = (current as CompoundItem)?.Slots; }
				catch { }

				if (slots == null)
				{
					continue;
				}

				foreach (var slot in slots)
				{
					Item contained = null;
					try { contained = slot?.ContainedItem; }
					catch { }

					if (contained != null)
					{
						pending.Push(contained);
					}
				}
			}
		}

		private static bool _loggedMissingLightApi;

		/// <summary>Turns off any light-like component on one item, ignoring its attachments.</summary>
		private void TurnOffLightsOnSingleItem(Item item)
		{
			bool foundAny = false;

			foreach (var component in EnumerateComponents(item))
			{
				var type = component.GetType();
				if (type.Name.IndexOf("Light", StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}

				foundAny = true;

				// The flag has been IsActive / IsOn / Light on different builds; take whichever
				// writable bool exists.
				foreach (var name in new[] { "IsActive", "IsOn", "Light" })
				{
					var flag = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
					if (flag != null && flag.CanWrite && flag.PropertyType == typeof(bool))
					{
						flag.SetValue(component, false);
						break;
					}

					var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
					if (field != null && field.FieldType == typeof(bool))
					{
						field.SetValue(component, false);
						break;
					}
				}
			}

			if (!foundAny && !_loggedMissingLightApi)
			{
				_loggedMissingLightApi = true;
				var members = string.Join(", ", item.GetType()
					.GetMembers(BindingFlags.Public | BindingFlags.Instance)
					.Select(m => m.Name)
					.Where(n => n.IndexOf("Component", StringComparison.OrdinalIgnoreCase) >= 0)
					.Distinct());

				Logger.LogWarning($"No light component found on '{item.GetType().Name}'. Component-related members are: [{members}]. Weapon lights cannot be switched off until one of these is used.");
			}
		}

		/// <summary>
		/// Yields an item's components, trying each accessor this build might expose.
		/// </summary>
		private static IEnumerable<object> EnumerateComponents(Item item)
		{
			var type = item.GetType();

			// A plain property/field holding the component collection is the common shape.
			foreach (var name in new[] { "Components", "AllComponents", "ItemComponents" })
			{
				object value = null;
				try
				{
					value = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item)
						?? type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
				}
				catch { }

				if (value is IEnumerable enumerable and not string)
				{
					foreach (var component in enumerable)
					{
						if (component != null)
						{
							yield return component;
						}
					}

					yield break;
				}
			}

			// Otherwise a parameterless method returning them.
			MethodInfo method = null;
			try
			{
				method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
					.FirstOrDefault(m => !m.IsGenericMethodDefinition
						&& m.GetParameters().Length == 0
						&& (m.Name == "GetAllItemComponents" || m.Name == "GetItemComponents"));
			}
			catch { }

			if (method == null)
			{
				yield break;
			}

			object result = null;
			try { result = method.Invoke(item, Array.Empty<object>()); }
			catch { }

			if (result is IEnumerable results and not string)
			{
				foreach (var component in results)
				{
					if (component != null)
					{
						yield return component;
					}
				}
			}
		}

		/// <summary>Logs only when the F12 "Debug Logging" toggle is on.</summary>
		public void DebugLog(string message)
		{
			if (DebugLogging != null && DebugLogging.Value)
			{
				Logger.LogWarning(message);
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
		public CompleteProfileDescriptorClass GenerateMannequinProfile(string fallbackMeleeTemplateId = null)
		{
			return new()
			{
				Id = MongoID.Generate(true),
				Info = new(),
				Customization = GenerateDefaultCustomization(),
				Health = GenerateDefaultHealth(),
				Inventory = GenerateDefaultInventory(fallbackMeleeTemplateId),
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
		public static EFTInventoryClass GenerateDefaultInventory(string fallbackMeleeTemplateId = null)
		{
		    var equipment = MongoID.Generate(true);

			var items = new List<FlatItemsDataClass>
			{
				new() { _id = equipment, _tpl = "55d7217a4bdc2d86028b456d" },
			};

			// A mannequin has to be holding something. With completely empty hands it gets no hands
			// controller, and the game then throws a NullReferenceException out of MouseLook every
			// single frame. When the player carries no melee weapon of their own to copy, declare one
			// here as plain flat item data - the same mechanism the equipment item above uses, so no
			// item-factory call is needed.
			if (!string.IsNullOrWhiteSpace(fallbackMeleeTemplateId))
			{
				items.Add(new FlatItemsDataClass
				{
					_id = MongoID.Generate(true),
					_tpl = fallbackMeleeTemplateId,
					parentId = equipment,
					slotId = "Scabbard",
				});
			}

			return new()
			{
				Gclass1390_0 = items.ToArray(),
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

		private void Update()
		{
			if (RefreshHotkey != null && RefreshHotkey.Value.IsDown())
			{
				RefreshMannequins();
			}
		}

		/// <summary>Draws the "Refresh Mannequins" button in the F12 config menu.</summary>
		private void DrawRefreshButton()
		{
			bool available = Mannequins != null && Singleton<GameWorld>.Instance is HideoutGameWorld;

			GUI.enabled = available;
			if (GUILayout.Button(available ? "Refresh Mannequins" : "Refresh Mannequins (enter the shooting range first)", GUILayout.ExpandWidth(true)))
			{
				RefreshMannequins();
			}
			GUI.enabled = true;
		}

		/// <summary>
		/// Rebuilds every mannequin from the player's current gear, without needing a raid or a
		/// restart.
		/// </summary>
		public void RefreshMannequins()
		{
			if (Singleton<GameWorld>.Instance is not HideoutGameWorld)
			{
				Logger.LogWarning("Refresh ignored: you are not in the hideout shooting range.");
				return;
			}

			if (_refreshing)
			{
				DebugLog("Refresh ignored: one is already running.");
				return;
			}

			StartCoroutine(RefreshMannequinsRoutine());
		}

		private bool _refreshing;

		private IEnumerator RefreshMannequinsRoutine()
		{
			_refreshing = true;

			// PORTING NOTE (SPT 4.0.13): rebuild from the fixed slot list, NOT from Mannequins.
			// Mannequins only holds what is currently alive - OnBotDeath removes an entry the moment
			// its mannequin dies - so refreshing off it silently skipped any slot that happened to
			// be dead or mid-respawn, which is exactly the reported "press refresh while a body is
			// down and the ones near it never come back".
			//
			// Any respawn already in flight has to be cancelled too, or it would spawn a second
			// mannequin into a slot this routine is also filling.
			_refreshGeneration++;

			foreach (var routine in _respawnRoutines)
			{
				if (routine != null)
				{
					StopCoroutine(routine);
				}
			}
			_respawnRoutines.Clear();

			foreach (var bot in Mannequins.Keys.ToArray())
			{
				Mannequins.Remove(bot);
				DespawnMannequin(bot);
				yield return null;
			}

			// Clear every remaining body. Corpses left by mannequins that were already dead are not
			// covered by the despawn above, and a new mannequin spawned on top of one ends up inside
			// it. In the shooting range every corpse is one of ours.
			DestroyAllCorpses();

			yield return new WaitForSeconds(SpawnInterval.Value);

			foreach (var data in _slots ?? Array.Empty<MannequinData>())
			{
				yield return WaitForTaskOrTimeout(SpawnBot(data), 120f);
				yield return new WaitForSeconds(SpawnInterval.Value);
			}

			_refreshing = false;
		}

		public IEnumerator SpawnInitialBots()
		{
			yield return new WaitForSeconds(1f);

			var closeLeft = new MannequinData(new(-4f, 0.01f, 16.2f), CloseRowPose);
			var closeMiddle = new MannequinData(new(-2.9f, 0.01f, 23.75f), CloseRowPose);
			var closeRight = new MannequinData(new(-1.65f, 0.01f, 30.22f), CloseRowPose);

			var farLeft = new MannequinData(new(-4.95f, 0.01f, 57.48f), FarRowPose);
			var farMiddle = new MannequinData(new(-2.75f, 0.01f, 57.47f), FarRowPose);
			var farRight = new MannequinData(new(-0.56f, 0.01f, 57.47f), FarRowPose);

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
			_slots = new[] { closeLeft, closeMiddle, closeRight, farLeft, farMiddle, farRight };

			foreach (var data in _slots)
			{
				yield return WaitForTaskOrTimeout(SpawnBot(data), 120f);
				yield return new WaitForSeconds(SpawnInterval.Value);
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
			_respawnRoutines.Add(StartCoroutine(DespawnBotSpawnAnotherOne(bot)));
		}

		public IEnumerator DespawnBotSpawnAnotherOne(LocalPlayer bot)
		{
			if (!Mannequins.Remove(bot, out var mannequinData))
			{
				yield break;
			}

			int generation = _refreshGeneration;

			// PORTING NOTE (SPT 4.0.13): the body has to stay put for a while. HollywoodFX attaches
			// its gore to the ragdoll from a prefix on RagdollClass.Start (confirmed by dumping its
			// assembly - RagdollStartPrefixPatch) and waits for the ragdoll to settle before playing
			// it, so clearing the body promptly cuts the effect off before it starts. This used to
			// share the spawn-interval setting, which meant turning spawns up silently killed the
			// death effects; respawn timing is now its own setting with a 2s floor.
			yield return new WaitForSeconds(RespawnDelay.Value);

			DespawnMannequin(bot);

			// A short fixed beat so the body is fully gone before its replacement is built on the
			// same spot. Not configurable - there is no reason to tune it, and letting it reach zero
			// only reintroduces spawning into a corpse.
			yield return new WaitForSeconds(0.25f);

			// A refresh that happened while this was waiting has already refilled this slot.
			if (generation != _refreshGeneration)
			{
				DebugLog("Skipping a respawn that a refresh superseded.");
				yield break;
			}

			// Safety net for anything the identity match missed: with nothing alive, no corpse in
			// the range can still belong to a living mannequin, so they can all go. This is also
			// the only cleanup that runs if a corpse is somehow never matched to its mannequin.
			if (Mannequins.Count == 0)
			{
				DestroyAllCorpses();
			}

			// Deliberately not awaited - a coroutine cannot await, and SpawnBot already handles its
			// own failures. The discard is what says so to the compiler, instead of warning CS4014.
			_ = SpawnBot(mannequinData);
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

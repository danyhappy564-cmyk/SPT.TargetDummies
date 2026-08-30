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
        }

		public async Task SpawnBot(MannequinData data)
		{
			try
			{

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
			// category via RegisterPools for capacity, then actually load the bundles through
			// IAssetsManager.LoadBundlesAsync(string[]), trying every known ResourceKey->string
			// conversion in turn since which one "works" isn't documented.
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
			LocalPlayer botPlayer;
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

			// take weapon in hands
			botPlayer.SetSlotItem(EquipmentSlot.FirstPrimaryWeapon, (_) => {});

			Mannequins[botPlayer] = data;

			}
			catch (Exception e)
			{
				Logger.LogError(e);
			}
		}

		/// <summary>
		/// Loads the mannequin's prefabs into the Raid asset pools before LocalPlayer.Create runs.
		/// Ported from spt-hideout-shootout's proven 4.0.13 fix for the same
		/// ObjectsFactory/PoolManagerClass gap - see the PORTING NOTE in SpawnBot above.
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

			string SafeToAssetName(ResourceKey key)
			{
				try { return key.ToAssetName(); }
				catch { return null; }
			}

			// PORTING NOTE (SPT 4.0.13): confirmed in-game - trying candidates one at a time and
			// stopping at the first whose LoadBundlesAsync call reports overall Succeed==true is
			// NOT enough. That call still reported success while at least one specific bundle
			// (a randomized "wild_head_N.bundle" scav head variant) silently never resolved,
			// because a batch load can succeed overall even if individual malformed names in it are
			// just skipped rather than causing a failure. LocalPlayer.Create then threw "wild_head_N
			// is not loaded" mid-construction, leaving a bodyless bot with only gear attached.
			// Loading the UNION of every candidate's converted names in one call - rather than
			// racing candidates and stopping at the first that "works" - means whichever format a
			// given resource key actually needs still gets tried.
			var bundleNames = resourceKeys
				.SelectMany(key => new[] { SafeToAssetName(key), key.rcid, key.path })
				.Where(name => !string.IsNullOrEmpty(name))
				.Distinct()
				.ToArray();

			bool anyHeadBundleRequested = bundleNames.Any(n => n.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0);
			Logger.LogWarning($"Preloading {bundleNames.Length} bundle names for mannequin profile {profile.Id} ({resourceKeys.Length} resource keys); anyHeadBundleRequested={anyHeadBundleRequested}. Sample: {string.Join(" | ", bundleNames.Take(10))}");

			if (bundleNames.Length == 0)
			{
				Logger.LogWarning($"No usable bundle names were produced for mannequin profile {profile.Id}; proceeding anyway.");
				return;
			}

			var operation = assetsManager.LoadBundlesAsync(bundleNames);

			var tcs = new TaskCompletionSource<bool>();
			StartCoroutine(DriveOperationCoroutine(operation, tcs));

			// PORTING NOTE (SPT 4.0.13): confirmed in-game a single mannequin's full bundle set
			// (gear + character mesh) can take upward of 30s outside a real raid's loading screen -
			// 20s was cutting this off too early even with spawns serialized (no contention). 90s
			// gives real headroom; LocalPlayer.Create below also retries if a bundle still wasn't
			// ready by the time this returns.
			const double timeoutSeconds = 90;
			var waitStart = DateTime.UtcNow;
			while (true)
			{
				var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
				if (completed == tcs.Task)
				{
					break;
				}

				if ((DateTime.UtcNow - waitStart).TotalSeconds >= timeoutSeconds)
				{
					Logger.LogWarning($"Bundle preload timed out for mannequin profile {profile.Id}; proceeding anyway.");
					return;
				}
			}

			if (!operation.Succeed)
			{
				Logger.LogWarning($"Bundle preload did not succeed for mannequin profile {profile.Id} (Failed={operation.Failed} Error={operation.Error}); proceeding anyway.");
			}
		}

		private static IEnumerator DriveOperationCoroutine(IOperation operation, TaskCompletionSource<bool> tcs)
		{
			yield return operation;
			tcs.TrySetResult(operation.Succeed);
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
			return await GetBotProfile(session, botType);
		}

		public Profile GenerateProfileWithMannequinEquipment(Profile playerProfile, int mannequinIndex)
		{
			var profileDescriptor = GenerateMannequinProfile();

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

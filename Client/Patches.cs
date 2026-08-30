//
// Copyright (c) 2026 7Bpencil
//
// This source code is licensed under the MIT license found in the
// LICENSE file in the root directory of this source tree.
//

using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.Hideout;
using System;
using System.Linq;
using System.Reflection;
using SPT.Reflection.Patching;
using HarmonyLib;
using UnityEngine;

namespace SevenBoldPencil.TargetDummies
{
	public class Patch_HideoutController_HideoutAwake : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(HideoutController), nameof(HideoutController.HideoutAwake));
        }

        [PatchPostfix]
        public static void Postfix(HideoutController __instance)
		{
			Plugin.Instance.HideShootingRangeTargets(__instance);
		}
	}

	public class Patch_GameWorld_DestroyAllLoot : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(GameWorld), nameof(GameWorld.DestroyAllLoot));
        }

        [PatchPrefix]
        public static bool Prefix(GameWorld __instance)
		{
			if (__instance is not HideoutGameWorld)
			{
				return true;
			}

			// quitting shooting range destroys all loot and corpses are considered loot too
			foreach (var lootItem in __instance.LootList.OfType<LootItem>().ToArray<LootItem>())
			{
				if (lootItem is not Corpse)
				{
					__instance.DestroyLoot(lootItem);
				}
			}

			return false;
		}
	}

	// PORTING NOTE (SPT 4.0.13): CorpseRagdoll (4.1's name) doesn't exist here - found via DumpTool
	// by shape (a class with a Start() method plus a PlayerBody field and a bare System.Action
	// field, matching _owner/_onRigidbodyStopped's roles) rather than a straight name search.
	// RagdollClass._owner's replacement is PlayerBody_0 (confirmed EFT.PlayerBody inherits
	// UnityEngine.Component, so .TryGetComponent<LocalPlayer>() still resolves), and
	// _onRigidbodyStopped's replacement is Action_0 - the only plain no-arg Action field on the
	// class (its other delegate fields are Func<bool,float,bool>/Func<bool>, which don't fit a
	// "stopped" notification). Both are public, so no Harmony ____field injection is needed. This
	// mapping is inferred from field shape, not confirmed from source - if bot death stops
	// respawning mannequins, check whether Action_0 fires here at all.
	public class Patch_CorpseRagdoll_Start : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(RagdollClass), nameof(RagdollClass.Start));
        }

        [PatchPrefix]
        public static void Prefix(RagdollClass __instance)
		{
			var gameWorld = Singleton<GameWorld>.Instance;
			if (gameWorld is not HideoutGameWorld)
			{
				return;
			}

			// PORTING NOTE (SPT 4.0.13, diagnostic): confirmed in-game that mannequins die and
			// ragdoll correctly, but never respawn - meaning either this prefix never fires for
			// hideout ragdolls, Action_0 never actually invokes the subscribed callback (wrong
			// field guess), or PlayerBody_0.TryGetComponent<LocalPlayer> fails to resolve. Logging
			// every step so the next test's log shows exactly which one.
			Plugin.Instance.LoggerInstance.LogWarning($"Patch_CorpseRagdoll_Start.Prefix fired for a hideout ragdoll (instance={__instance}).");

			__instance.Action_0 += () =>
			{
				Plugin.Instance.LoggerInstance.LogWarning("RagdollClass.Action_0 callback fired.");
				if (__instance.PlayerBody_0.TryGetComponent<LocalPlayer>(out var localPlayer))
				{
					Plugin.Instance.LoggerInstance.LogWarning($"Resolved LocalPlayer from PlayerBody_0; calling OnBotDeath for profile {localPlayer.Profile?.Id}.");
					Plugin.Instance.OnBotDeath(localPlayer);
				}
				else
				{
					Plugin.Instance.LoggerInstance.LogWarning("Could not resolve LocalPlayer component from RagdollClass.PlayerBody_0; OnBotDeath not called.");
				}
			};
		}
	}

	// BSG doesnt check which collider exited shooting range, which means respawning
	// bots force player to exit shooting range, so add check if collider actually belongs to player
	public class Patch_HideoutAreaTrigger_OnTriggerExit : ModulePatch
	{
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(HideoutAreaTrigger), nameof(HideoutAreaTrigger.OnTriggerExit));
        }

        [PatchPrefix]
        public static bool Prefix(HideoutArea ____area, Collider col)
		{
			if (____area == null)
			{
				return false;
			}

			var gameWorld = Singleton<GameWorld>.Instance;
			var colliderOwner = gameWorld.GetPlayerByCollider(col);
			if (colliderOwner == null)
			{
				return false;
			}
			if (colliderOwner != gameWorld.MainPlayer)
			{
				return false;
			}

			____area.Data.Template.AreaBehaviour.OnExitLocation();
			return false;
		}
	}

	/// <summary>
	/// Ported from spt-hideout-shootout's own 4.0.13 HollywoodFX compat patch (that mod hit and
	/// solved this exact problem first). HollywoodFX detects the Hideout and deliberately skips its
	/// own blood/impact effects setup there - this forces its shared IsHideout flag false so those
	/// effects initialize for TargetDummies' mannequins too. Written entirely through reflection (no
	/// compile-time reference to HollywoodFX) so this mod neither requires HollywoodFX to be
	/// installed nor breaks if it isn't.
	/// </summary>
	internal class Patch_HollywoodFX_ForceEffectsInHideout : ModulePatch
	{
		private const string TargetTypeName = "HollywoodFX.Patches.GameWorldAwakePrefixPatch";
		private const string TargetMethodName = "Prefix";

		private static FieldInfo _isHideoutField;
		private static Type _materialRegistryType;
		private static Type _playerDamageRegistryType;

		public static void TryEnable()
		{
			try
			{
				Type targetType = AccessTools.TypeByName(TargetTypeName);
				if (targetType == null)
				{
					Plugin.Instance.LoggerInstance.LogInfo($"HollywoodFX hideout-effects compat: type '{TargetTypeName}' not found (HollywoodFX not installed) - skipped.");
					return;
				}

				MethodInfo method = AccessTools.Method(targetType, TargetMethodName);
				_isHideoutField = AccessTools.Field(targetType, "IsHideout");
				_materialRegistryType = AccessTools.TypeByName("HollywoodFX.Lighting.MaterialRegistry");
				_playerDamageRegistryType = AccessTools.TypeByName("HollywoodFX.Gore.PlayerDamageRegistry");

				if (method == null || _isHideoutField == null || _materialRegistryType == null || _playerDamageRegistryType == null)
				{
					Plugin.Instance.LoggerInstance.LogWarning(
						$"HollywoodFX hideout-effects compat skipped: method={(method == null ? "MISSING" : "ok")} " +
						$"IsHideout field={(_isHideoutField == null ? "MISSING" : "ok")} " +
						$"MaterialRegistry type={(_materialRegistryType == null ? "MISSING" : "ok")} " +
						$"PlayerDamageRegistry type={(_playerDamageRegistryType == null ? "MISSING" : "ok")} " +
						"- installed HollywoodFX version may not match what this was written against.");
					return;
				}

				new Patch_HollywoodFX_ForceEffectsInHideout().Enable();
				Plugin.Instance.LoggerInstance.LogInfo($"Hooked '{TargetTypeName}.{TargetMethodName}' so HollywoodFX's blood/impact effects also initialize in the Hideout.");
			}
			catch (Exception ex)
			{
				Plugin.Instance.LoggerInstance.LogWarning($"Failed to enable HollywoodFX hideout-effects compat: {ex.Message}");
			}
		}

		protected override MethodBase GetTargetMethod()
		{
			Type targetType = AccessTools.TypeByName(TargetTypeName);
			return AccessTools.Method(targetType, TargetMethodName);
		}

		[PatchPrefix]
		private static bool Prefix()
		{
			try
			{
				_isHideoutField.SetValue(null, false);
				CreateSingleton(_materialRegistryType);
				CreateSingleton(_playerDamageRegistryType);
			}
			catch (Exception ex)
			{
				Plugin.Instance.LoggerInstance.LogWarning($"HollywoodFX hideout-effects compat prefix failed: {ex.Message}");
			}

			// Skip HollywoodFX's own Prefix body entirely - it would just recompute IsHideout from
			// the real GameWorld instance (undoing the force above) and, for the Hideout, return
			// before creating the two singletons this replicates.
			return false;
		}

		private static void CreateSingleton(Type valueType)
		{
			object instance = Activator.CreateInstance(valueType);
			Type singletonType = typeof(Singleton<>).MakeGenericType(valueType);
			MethodInfo createMethod = singletonType
				.GetMethods(BindingFlags.Public | BindingFlags.Static)
				.FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == valueType);
			createMethod?.Invoke(null, new[] { instance });
		}

		private static bool _shotDelegateWireAttempted;

		/// <summary>
		/// HollywoodFX's <c>PlayerDamageRegistry</c>/<c>ImpactStatic.LocalPlayer</c> only get wired
		/// up from a <c>Postfix</c> on <c>GameWorld.OnGameStarted()</c>, which the Hideout never
		/// calls (unlike <c>GameWorld.Awake()</c>, which the effects-init fix above relies on).
		/// Worked around the same way spt-hideout-shootout's backport does: invoke HollywoodFX's own
		/// <c>ShotDelegateWrapperPatch.Postfix</c> and <c>GameWorldStartedPostfixPatch.Postfix</c>
		/// directly (both public and static) once the Hideout's GameWorld actually exists.
		/// </summary>
		internal static void TryWireShotDelegateOnce()
		{
			if (_shotDelegateWireAttempted)
			{
				return;
			}
			_shotDelegateWireAttempted = true;

			try
			{
				Type shotWrapperType = AccessTools.TypeByName("HollywoodFX.Patches.ShotDelegateWrapperPatch");
				if (shotWrapperType == null)
				{
					Plugin.Instance.LoggerInstance.LogInfo("HollywoodFX shot-delegate wiring skipped: type 'HollywoodFX.Patches.ShotDelegateWrapperPatch' not found (HollywoodFX not installed?).");
				}
				else
				{
					FieldInfo originalDelegateField = AccessTools.Field(shotWrapperType, "OriginalShotDelegate");
					if (originalDelegateField != null && originalDelegateField.GetValue(null) != null)
					{
						Plugin.Instance.LoggerInstance.LogInfo("HollywoodFX shot-delegate wrapper already wired; skipping.");
					}
					else
					{
						MethodInfo postfix = AccessTools.Method(shotWrapperType, "Postfix");
						if (postfix == null)
						{
							Plugin.Instance.LoggerInstance.LogWarning("HollywoodFX shot-delegate wiring skipped: 'Postfix' method not found on ShotDelegateWrapperPatch (installed HollywoodFX version may not match what this was written against).");
						}
						else if (!Singleton<GameWorld>.Instantiated)
						{
							Plugin.Instance.LoggerInstance.LogWarning("HollywoodFX shot-delegate wiring skipped: Singleton<GameWorld> is not instantiated yet.");
						}
						else
						{
							postfix.Invoke(null, new object[] { Singleton<GameWorld>.Instance });
							Plugin.Instance.LoggerInstance.LogInfo("Manually invoked HollywoodFX's ShotDelegateWrapperPatch.Postfix - the Hideout never calls GameWorld.OnGameStarted(), which is what normally triggers this wiring.");
						}
					}
				}
			}
			catch (Exception ex)
			{
				Plugin.Instance.LoggerInstance.LogWarning($"Failed to wire HollywoodFX's shot-delegate wrapper: {ex.Message}");
			}

			try
			{
				Type gameWorldStartedType = AccessTools.TypeByName("HollywoodFX.Patches.GameWorldStartedPostfixPatch");
				if (gameWorldStartedType == null)
				{
					Plugin.Instance.LoggerInstance.LogInfo("HollywoodFX GameWorldStartedPostfixPatch wiring skipped: type not found (HollywoodFX not installed?).");
					return;
				}

				MethodInfo postfix = AccessTools.Method(gameWorldStartedType, "Postfix");
				if (postfix == null)
				{
					Plugin.Instance.LoggerInstance.LogWarning("HollywoodFX GameWorldStartedPostfixPatch wiring skipped: 'Postfix' method not found (installed HollywoodFX version may not match what this was written against).");
					return;
				}

				if (!Singleton<GameWorld>.Instantiated)
				{
					Plugin.Instance.LoggerInstance.LogWarning("HollywoodFX GameWorldStartedPostfixPatch wiring skipped: Singleton<GameWorld> is not instantiated yet.");
					return;
				}

				postfix.Invoke(null, new object[] { Singleton<GameWorld>.Instance });
				Plugin.Instance.LoggerInstance.LogInfo("Manually invoked HollywoodFX's GameWorldStartedPostfixPatch.Postfix (sets ImpactStatic.LocalPlayer, needed for hit-effect impacts) - same GameWorld.OnGameStarted() gap as above.");
			}
			catch (Exception ex)
			{
				Plugin.Instance.LoggerInstance.LogWarning($"Failed to wire HollywoodFX's GameWorldStartedPostfixPatch: {ex.Message}");
			}
		}
	}
}

### 변경 사항 (2026-08-31, SPT 4.0.13 백포트)

원본 모드는 SPT 4.1 기준으로 작성되어 있어서, SPT 4.0.13으로 대상 버전을 낮추면서 클라이언트를 손봤습니다. 4.0.13 클라이언트가 4.1보다 훨씬 덜 탈난독화되어 있는 것도 문제였지만, 이번 백포트에서 진짜로 발목을 잡은 건 따로 있었습니다 — **하이드아웃에서는 새 캐릭터의 번들을 로드할 방법이 아예 없다**는 점입니다. 이것 때문에 마네킹을 만드는 방식 자체를 바꿔야 했습니다.

**번들 로딩 문제 (이번 백포트의 핵심)**

스카브든 보스든, 원래 방식대로 봇 프로필을 받아오면 그 캐릭터의 머리·몸·손 번들을 로드해야 합니다. 그런데 이게 안 됩니다. 게임 자체 로그(`Player.log`)로 확인한 원인은 이렇습니다:

- 캐릭터 번들은 의존성으로 전역 번들인 `cubemaps`, `shaders`를 끌고 옵니다.
- 하이드아웃은 이미 그것들을 **다른 경로로** 로드해둔 상태인데, `BundlesManagerClass`는 그 사실을 모릅니다.
- 그래서 다시 요청하면 Unity가 `another AssetBundle with the same files is already loaded`로 거부하고, 그 번들은 영원히 등록되지 않으며, 이를 기다리던 캐릭터 번들 로드는 **성공도 실패도 보고하지 않은 채 무한 대기**에 빠집니다.
- 결과적으로 `LocalPlayer.Create`가 `<머리 번들> is not loaded`로 중간에 터지고, 몸이 없는 투명한 캐릭터만 남습니다.

가능한 로딩 경로는 전부 시도했고 전부 같은 지점에서 막혔습니다 — `LoadBundlesAsync`(배치/개별), `LoadAssetAsync(ResourceKey)`, `BundlesManagerClass.LoadBundleAsync`(의존성 스킵). 4.1 원본이 쓰던 `ObjectsFactory.LoadBundlesAndCreatePools`는 4.0.13에서 `PoolManagerClass`로 이름이 바뀌었는데, 파라미터 타입 중 하나(`GDelegate62`)를 CLR이 로드 자체를 거부해서(`delegate class must be sealed`) `GetParameters()`도 `Invoke()`도 던집니다. 즉 모드 코드에서는 호출이 불가능합니다.

**그래서 방식을 바꿨습니다 — 마네킹은 플레이어 본체의 복사본입니다**

- 6개 마네킹 전부 **플레이어의 현재 외형과 착용 장비를 그대로 복사**해서 스폰합니다. 플레이어 번들은 정의상 이미 메모리에 있으니 로딩 문제가 원천적으로 사라집니다.
- 기존의 봇 타입 선택(스카브 / 타길라 / 레이더 등)은 전부 제거했습니다. 위 이유로 어차피 정상 동작하지 않습니다.
- 은신처 장비 진열대 마네킹 스킨도 후보였지만, 그건 전시용 소품이라 **탄도 재질 태그가 없어서** 총을 맞아도 움찔거림도 피분수도 나오지 않습니다. 플레이어 본체는 진짜 전투 모델이라 타격 판정이 정상입니다.
- 번들 프리로드는 통째로 제거했습니다. 로그상 마리당 170개를 요청해서 **0개를 로드**하면서 4초씩 잡아먹고 있었고, 끝나지 않는 코루틴이 스폰·리스폰마다 쌓여 프레임을 갉아먹고 있었습니다.

**4.0.13 API 대응 (이름/시그니처 변경)**

- `ObjectsFactory` → `PoolManagerClass`, `IEftSession` → `ISession`, `CountTypeBotWave` → `WaveInfoClass`, `DumbStatisticsManager` → `GClass2265`, `ThirdPersonCustomizationFilter` → `GClass1856`, `CorpseRagdoll` → `RagdollClass`, `InventoryDescriptor` → `EFTInventoryClass`, `ProfileDescriptor` → `CompleteProfileDescriptorClass`, `Profile.HealthInfo` → `Profile.ProfileHealthClass`.
- `HideoutGame.GameWorld` / `.Profile` → `GameWorld_0` / `Profile_0`, `task_0` → `Task_0`.
- `HideoutGame.NextPlayerId()`는 존재하지 않아서 난수 ID로 대체.
- `AppEnvironment.Config.CharacterController.BotPlayerMode`에 대응하는 프리셋이 없어서 `CharacterControllerSpawner.Mode`를 직접 구성.
- 오프라인 컬링 필드명이 `botPlayerCulling` → `localPlayerCullingHandlerClass`.

**안정성 / 성능 문제 해결**

- **매 프레임 NRE 도배** — 무기를 손에 못 쥔 마네킹은 핸즈 컨트롤러가 없는 상태가 되고, 그러면 `Player.MouseLook`이 **매 프레임** NullReferenceException을 던지며 스택 트레이스를 디스크에 씁니다. 한 세션에서 10,827번 찍혔고, 이게 렉의 정체였습니다. 실제로 아이템이 들어있는 슬롯(주무기 → 보조무기 → 권총 → 칼)을 골라 쥐여주고, 그래도 실패하면 빈손 컨트롤러라도 붙이도록 처리 — 12번으로 줄었습니다.
- **시체가 안 지워지는 문제** — `Dispose()` + 풀 반납은 플레이어 오브젝트만 치우고, `GameWorld.LootList`에 남은 `Corpse` 전리품은 그대로 남습니다. 그래서 같은 자리에 새 마네킹이 **이전 시체 안에 겹쳐서** 스폰됐고, 살아있는 마네킹에 '시체'·'검색' 프롬프트가 붙거나 콜라이더 중첩으로 몸이 날아갔습니다. 죽일 때마다 하나씩 계속 쌓이기도 했습니다. despawn 시 시체까지 같이 파괴하도록 수정.
- **무기가 공중에 떠있는 문제** — `Corpse`가 시체의 몸을 소유하기 때문에, 시체를 먼저 파괴하면 몸이 무너지고 뒤이은 `Dispose()`가 NRE로 터져 정리가 중단됐습니다. 순서를 바꿔서 해결.
- **죽어도 피가 안 터지는 문제** — 몸을 풀에 반납하면 **다음 마네킹에게 그대로 재활용**되는데, HollywoodFX의 gore 컴포넌트는 비활성화 시 분리되도록 되어 있어서 재활용된 몸은 이미 소모된 상태로 나옵니다. 풀 반납 대신 파괴해서 스폰마다 새 오브젝트를 받도록 수정.
- **리스폰 자체가 안 되던 문제** — `RagdollClass.PlayerBody_0.TryGetComponent<LocalPlayer>()`는 항상 실패합니다. `TryGetComponent`는 같은 GameObject만 보는데 이 클라이언트에서는 `LocalPlayer`가 다른 오브젝트에 있기 때문입니다. `GetComponentInParent`로 변경.
- `LocalPlayer.Create`가 중간에 터지면 반쯤 만들어진 몸이 씬에 남아 매 프레임 NRE를 던지므로, 실패 시 정리하도록 처리.
- 패치를 개별적으로 등록해서, 하나가 실패해도 나머지가 같이 죽지 않도록 변경.

**HollywoodFX 호환**

- 하이드아웃에서도 피/충격 이펙트가 초기화되도록 `GameWorldAwakePrefixPatch.IsHideout` 플래그를 강제로 `false` 처리하고, 하이드아웃에서는 호출되지 않는 `GameWorld.OnGameStarted()`에 걸려있는 `ShotDelegateWrapperPatch` / `GameWorldStartedPostfixPatch`를 직접 한 번 호출합니다.
- 리플렉션 기반이라 HollywoodFX가 설치되어 있지 않아도 안전하게 넘어갑니다.
- 참고로 이 배선을 **스폰마다** 다시 하면 안 됩니다. HollywoodFX가 gore 프리팹을 통째로 다시 만들기 때문에(한 세션 기준 `Registering material Puff` 2,642회, gore 충돌 핸들러 155개) 스폰렉이 돌아오고, 중복된 핸들러가 힘을 겹쳐 적용해서 시체가 방 반대편까지 날아갑니다.

### 알려진 제약사항

- 마네킹은 **플레이어와 똑같은 외형·장비**로만 나옵니다. 위의 번들 로딩 문제 때문에 다른 봇 외형은 불가능합니다.
- `Patch_HideoutAreaTrigger_OnTriggerExit`가 적용되지 않습니다 (`HideoutAreaTrigger`의 private 필드명이 바뀌어 Harmony 필드 주입이 실패). 마네킹이 근처에서 리스폰될 때 사격장에서 튕겨나가지 않게 하는 기능이라 스폰 자체에는 영향이 없습니다.

### 설정

**Mannequin Settings**
- **Refresh Mannequins** — 지금 착용 중인 장비로 6명 전부 다시 스폰하는 버튼. 레이드를 뛰거나 게임을 재시작할 필요 없이 즉시 반영됩니다.
- **Refresh Hotkey** — 위와 같은 동작의 단축키 (기본값 F6).
- **Spawn Unarmored** — 장비 없이 맨몸으로 스폰. 칼만 예외로 들려줍니다 — 손에 아무것도 없으면 위의 `MouseLook` NRE가 매 프레임 터집니다.
- **Fallback Melee Template Id** — 맨몸 모드에서 플레이어가 근접무기를 안 들고 있을 때 대신 쥐여줄 아이템 템플릿 ID. 기본값이 안 먹으면 원하는 칼의 ID로 바꾸면 됩니다.
- **Force Weapon Lights Off** — 마네킹의 후레쉬·레이저 강제 소등. 플래시 달린 총을 들고 있으면 사격장에서 눈뽕이 됩니다.
- **Corpse Linger** — 시체가 사라지고 리스폰되기까지의 시간.
- **Spawn Interval** — 마네킹 하나와 다음 하나 사이의 간격. 낮출수록 빠르지만 한 프레임에 작업이 몰립니다.
- **Health Head / Chest / Stomach / Arm / Leg** — 부위별 체력.

**Close / Far**
- **Pose** — 앞줄 3명 / 뒷줄 3명의 자세를 따로 설정 (서기 / 앉기 / 눕기).

**Debug**
- **Debug Logging** — 스폰 과정을 단계별로 로그. 문제 추적할 때만 켜면 됩니다. 실제 오류는 이 설정과 무관하게 항상 경고로 남습니다.

---

###### This project is distributed under the MIT License — see `LICENSE` for details.

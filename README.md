# LizardBase — 蜥蜴 VR 遊戲

Quest 3 上的 VR 遊戲。玩家是一隻只有螞蟻兩倍大的蜥蜴：用雙手抓地面、牆面把身體拉著爬；用**吹龍**（派對吹笛）伸出舌頭吃東西；被天敵鷺鷥攻擊時，**張開雙臂把身體撐大**擋下牠的嘴。

- 引擎：**Unity 6000.3.24f1**（Unity 6）、URP
- XR：Meta XR SDK 207（`com.meta.xr.sdk.all`、MR Utility Kit）、OpenXR + Unity OpenXR Meta
- 平台：Android / Quest 3（深度、身體追蹤、Passthrough 相機）
- 主要場景：`Assets/Scenes/FullTest.unity`（Build Settings 第一個）

---

## 取得與開啟專案

```bash
git clone https://github.com/Koikbin/LizardBase.git
git checkout fulltest-integration   # 目前最新的整合分支
```

1. Unity Hub 用 **6000.3.24f1** 開啟（套件會依 `Packages/manifest.json` 自動安裝）
2. 打開 `Assets/Scenes/FullTest.unity`
3. Build 到頭盔：File → Build Profiles → Android → Build And Run

> `Assets/Models/2地形2.obj` 超過 GitHub 100MB 上限，沒有放進 repo。

---

## 遊戲流程（FullTest）

由 `StageManager` 控制：

| 階段 | 內容 |
|------|------|
| **Forest** 森林 | 用舌頭吃葉子上的**水珠**，水分達到目標前不能進洞穴 |
| **GoToCave** | 水分足夠，洞穴標記亮起 |
| **Cave** 地洞 | 跳石頭階梯，咬**礦石**收集能量；收集夠之前不能離開 |
| **GoToExit** | 出口標記亮起 |
| **Finished** | 走進出口範圍就過關 |
| **GameOver** | 從石頭階梯掉進深淵 → 遊戲結束（右手 A / 鍵盤 R 重新開始） |

- **螞蟻**隨時都能吃（毒素），舌尖靠近時會逃跑，被吃掉後幾秒重生。
- **天敵鷺鷥**：玩家走進**廢棄實驗室**後 1.5～3 秒出發 → 走向玩家 → 一口一口咬。每口有預告動作，預告結束時身體撐得夠大就擋下（周圍出現六角形**防禦網**，快失效時閃爍）；沒擋下、嘴又碰到頭就被抓（顯示失敗畫面）。擋下夠多口鳥就退回去；玩家離開實驗室鳥也會退回。

---

## 操作方式

### 頭盔（Quest 3 手把）

| 動作 | 操作 |
|------|------|
| 地面爬行 | 手碰到地面就抓住，往後拉身體往前；左右手交替。不需按鍵 |
| 攀爬牆面 | 手碰到牆（`Climbable`）並**按住 Grip**，往下拉身體往上；兩手都放開會掉落 |
| 伸舌頭 | **吹龍**（頭盔深度感測偵測）或**右手扳機**（按多深伸多長），兩者取較大 |
| 吹龍偵測開關 | 按下**左手搖桿** |
| 撐大身體（膨脹） | 靜止時**張開雙臂**（手把距離 + 手肘外展角，手肘是必要條件）；走動或爬行時歸零 |
| 重新校準膨脹 | **左手 X**（校準時右手扳機是確認鍵） |
| 重設視角 | 左手 Y |
| 地面高度校準 | 右手 B |
| 原地轉向（偵錯） | 右手搖桿左右 |
| 遊戲結束後重來 | 右手 A |

爬行的核心規則：**手抓住表面後，手移動多少，身體就往反方向移動多少。** 地面只能水平移動、不能往視線後方爬；斜度 45° 以上且有 `Climbable` Tag 的表面才能攀爬。

膨脹預設使用內建的校準值（手把距離 0.10→0.50 m、手肘 30°→75°），開遊戲不必先校準；要用自己的數值就按左手 X。

### 沒有頭盔（編輯器 + `DebugInput`）

在場景裡啟用 `DebugInput` 並勾選 `Use Keyboard`：

| 按鍵 | 作用 |
|------|------|
| WASD / 按住滑鼠右鍵 | 移動 / 轉視角 |
| 按住 Space | 吹龍（舌頭伸出） |
| 按住 E | 雙手外展（膨脹） |
| 按住滑鼠左鍵 / F | 模擬左手 / 右手抓在游標指到的表面，往下拖把身體拉過去 |
| J | 跳（模擬兩手往下推） |
| R | 遊戲結束後重新開始 |

> 上頭盔前記得關掉 `Use Keyboard`，否則天敵會讀鍵盤而不是真實手勢。
> Link 不支援身體追蹤，用 Link 測試時要停用 `ElbowTracking` 並把 `ExpansionInput.useElbow` 取消勾選，否則膨脹永遠是 0。

---

## 場景

| 場景 | 用途 |
|------|------|
| `FullTest.unity` | **主要整合場景**：上色地形、關卡流程、螞蟻、舌頭、膨脹、天敵 |
| `MechanismTest.unity` | 機制測試場（原 `main.unity`），`Build FullTest Scene` 從這裡重建 FullTest |
| `LizardGame.unity` | 早期的爬行／攀爬測試場 |
| `ExpansionTest.unity` | 膨脹（雙手距離）測試 |
| `DepthTest.unity` | 深度感測測試 |
| `PassthroughTest.unity` | Passthrough 相機畫面測試 |
| `SB_*.unity` | 分鏡（storyboard）場景 |

---

## 程式架構

### 核心玩法（`Assets/Scripts/`）

| 腳本 | 說明 |
|------|------|
| `LizardLocomotion` | 地面爬行與攀爬（掛在 `Player`，含 CharacterController）；也處理視角重設與地面校準 |
| `TongueTrigger` | 舌頭：讀吹龍深度 / 扳機 → 伸出 → 黏住最近的可吃物 → 縮回吃掉 |
| `ExpansionInput` | 膨脹值：手把距離 + 手肘外展，縮放 `OVRCameraRig`；靜止才能膨脹 |
| `ExpansionCalibration` | 膨脹校準（由 `ExpansionInput` 自動加上） |
| `ElbowTracker` | 讀 Quest 3 上半身追蹤（`OVRBody`）算手肘外展角 |
| `AntWander` / `AntPath` | 螞蟻沿路線或隨機走、受驚逃跑、被吃後重生 |
| `HeronThreat` | 天敵狀態機：Hidden → Approach → Attack → Retreat；觸發區域、貼地、牆壁阻擋、咬中判定 |
| `HeronNeckAim` | 鷺鷥脖子骨頭（Neck_1～4）瞄準玩家頭部 |
| `HeronHipLean` | 鷺鷥繞髖部前傾、腳不動 |
| `HeronDeathScreen` | 被抓時的失敗文字 |
| `DefenseNet` | 能擋下這一口時顯示的六角形防禦網 |
| `LateralTunnel` | 橫向移動時視野周圍變暗，減少暈眩 |
| `BodyView` | 顯示自己的身體 |
| `SnapToGround` | 物件貼地 |

### 關卡系統（`Assets/Scripts/Game/`）

| 腳本 | 說明 |
|------|------|
| `StageManager` | 關卡流程（森林 → 地洞 → 出口）、範圍限制、HUD、Game Over |
| `LizardResources` | 資源（毒素、水分、礦石能量） |
| `ITongueEdible` | 舌頭可吃物的介面 |
| `TongueFood` | 水珠（整顆吞）、礦石（一次咬一塊） |
| `CaveVolume` / `ForestBoundary` / `GameZone` | 地洞空間、森林邊界、方塊區域 |
| `SlipperySlope` | 石頭階梯旁的坑壁：抓不住、會滑下去 |

### 吹龍偵測與偵錯

| 腳本 | 說明 |
|------|------|
| `BlowoutDepthProbe` | 用深度射線網量嘴前方的吹龍長度（掛在 `DepthProbe`，和 `EnvironmentRaycastManager` 同物件） |
| `DepthStripDetector` | 在深度圖上找「從畫面邊緣長出的細長物體」（版本二），可取代射線規則控制舌頭 |
| `DepthSnapshot` | 自動存彩色照片 + 深度圖 + 格子圖（頭盔上存到 `persistentDataPath/DepthShots/`） |
| `TongueDepthDebug` | 頭盔內顯示射線命中與舌頭長度；B/A/X/Y 在紀錄上加標記 |
| `ElbowProbeDisplay` | 身前顯示身體追蹤與手肘數值 |
| `IntegrationCheck` | 每秒在 Console 印一行整體狀態 |
| `DebugInput` / `DebugStickTurn` | 鍵盤模擬輸入 / 右搖桿轉向 |

### 編輯器工具（選單 **Lizard**，`Assets/Editor/`）

| 選單 | 作用 |
|------|------|
| `Build FullTest Scene` | 從 `MechanismTest.unity` 重建 `FullTest.unity`（換背景模型、建 Player、啟用膨脹、跑 Setup Terrain） |
| `Setup Terrain (Cliffs + Ants)` | 依坡度產生地面 / 峭壁碰撞體（`Climbable`）、Player 貼地、重放螞蟻 |
| `Setup Game (Stages + Food)` | 依標記產生水珠、礦石、地洞範圍、關卡系統與 HUD |
| `Apply Colored Terrain` | 把隊友上好的材質、天空盒、燈光套到 FullTest 的背景模型 |
| `標記工具` | 手動標記地面、水珠、礦石、地洞頂 / 底、森林範圍等（存在 `Assets/Generated/LevelMarks.json`） |
| `Run Pending Task` | 一次性的批次設定（`LizardPendingTask`） |

報告會寫到 `Logs/TerrainReport.txt`、`Logs/GameSetupReport.txt`。

> 背景模型的擺放改過之後，要依序重跑 Setup Terrain → Setup Game；`LevelMarks.json` 是用世界座標存的，要跟背景模型的位置一致。

---

## 頭盔測試

- adb 不在 PATH 上，路徑：`C:\Program Files\Unity Hub\6000.3.24f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe`
- 套件名稱：`com.yourname.lizardbase.depthtest`
- 拉吹龍偵錯照片：
  ```
  adb pull /sdcard/Android/data/com.yourname.lizardbase.depthtest/files/DepthShots ./DepthShots_日期
  ```
  `DepthShots*/` 資料夾很大（數 GB），不要 commit。
- 深度品質每次開機不一樣，測吹龍前建議先**重開頭盔**。
- Inspector 的修改要在**非 Play 模式**下改並 Ctrl+S 存場景，Build 才會帶進去。
- 目前 Build 設定為了加快建置用 IL2CPP Debug + Faster (smaller) builds，正式發布前改回 Release + Faster runtime。

---

## 分支

| 分支 | 內容 |
|------|------|
| `main` | 主線 |
| `fulltest-integration` | **目前最新**：FullTest 整合天敵、防禦網、手肘追蹤、吹龍長條偵測 |
| `blowout-depth-tuning` | 吹龍深度偵測調參（左右眼長條偵測） |
| `feature/elbow-inflation` | 手肘外展膨脹、天敵脖子 / 髖部、防禦網的原始開發分支 |
| `walk-&-climb` | 早期的爬行與攀爬 |
| `storyboard` | 分鏡場景 |

---

## 常見問題

**手放到地上往後拉，身體沒有移動？**
→ `LizardLocomotion` 的 `Grab Radius` 調高；確認 OVRManager 的 `Tracking Origin Type` 是 Floor Level，或按右手 B 校準地面高度。開 `Log Grab Events` 可在 Console 看有沒有抓住。

**手碰到牆卻爬不上去？**
→ 要按住 Grip，牆要有 `Climbable` Tag 和 Collider（用 Setup Terrain 產生的 `_CliffCollider` 會自動設好）。

**爬太慢 / 太快？**
→ 調 `LizardLocomotion` 的 `Pull Gain`。

**張開手臂卻沒有膨脹？**
→ 要先站著不動；確認身體追蹤有開（Link 不支援）；按左手 X 重新校準。

**吹了舌頭沒伸出來？**
→ 按左手搖桿確認吹龍偵測是開的；重開頭盔再試；用右手扳機確認舌頭本身正常。用 `TongueDepthDebug` / `DepthSnapshot` 看深度到底有沒有看到吹龍。

**螞蟻不見 / 碰撞體對不上地形？**
→ 背景模型被移動或旋轉過，重跑 Setup Terrain 和 Setup Game。

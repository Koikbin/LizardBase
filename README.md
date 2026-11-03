# 蜥蜴爬行遊戲 — Lizard Locomotion

## 更新內容（walk-&-climb branch）

### 新增檔案
| 檔案 | 位置 | 說明 |
|------|------|------|
| `LizardGame.unity` | `Assets/Scenes/` | 主要遊戲場景 |
| `LizardLocomotion.cs` | `Assets/Scripts/` | 地面爬行與攀爬邏輯腳本 |
| `Palm.prefab` | `Assets/Scenes/` | 手部視覺 Prefab（場景目前沒有使用，手以兩顆 Sphere 顯示） |

### 修改檔案
- `Assets/Settings/` — 渲染和專案設定更新
- `ProjectSettings/` — Tag、Graphics、XR 相關設定更新（包含新增 `Climbable` Tag）

---

## 取得專案

```bash
git clone [你的 repo 網址]
git checkout walk-&-climb
```

打開 Unity Hub → 用 **Unity 6** 開啟專案 → 安裝 **Meta XR All-in-One SDK**（Window → Asset Store 搜尋）→ 打開 `Assets/Scenes/LizardGame`

---

## 遊戲概念

模擬蜥蜴在地面爬行與垂直攀爬的 VR 體驗。玩家用手抓住地面或牆面，把身體拉過去。爬地不需要按鍵，爬牆要按住 Grip。

---

## 操作方式

**核心規則：手抓住表面後，手移動多少，身體就往反方向移動多少。**

### 動作一：地面爬行
手碰到地面就抓住，離開地面就放開，不需要按鍵。

1. 手往前伸，放到地面上
2. 手貼著地面往後拉，身體就跟著往前移動
3. 手抬起來，再往前伸到地面
4. 左右手**交替**做這個動作，模擬蜥蜴四肢爬行

地面規則：
- 只會水平移動，手往上抬不會讓身體上升
- 手貼著地面往前伸不會把身體往後推，所以無法往視線後方爬
- 兩手都抓住時，以移動較多的那隻手為準
- 手一放開，身體就會停下來，沒有滑行

### 動作二：攀爬
手碰到牆面並**按住 Grip 鍵**（手把側面、中指按的按鍵）才會抓住，放開 Grip 才會放手。

1. 爬到牆前，把手放到牆面上並按住 Grip
2. 手往下拉，身體就往上移動
3. 另一隻手舉高放到牆面上並按住 Grip，再放開原本的手，繼續往下拉
4. 至少一隻手抓著牆面時不會掉落，兩手都放開就會開始下落

手碰到牆但沒按 Grip，或按著 Grip 但沒碰到牆，都不會攀爬。

### 斜坡規則
- **緩坡（45度以下）**：用地面爬行就能通過
- **陡坡／垂直牆（45度以上）**：表面要有 `Climbable` Tag 才能攀爬

---

## 場景設定

可攀爬的牆壁需要加上 **`Climbable` Tag**：

1. 點選牆壁物件 → Inspector → `Tag` → 選 `Climbable`
2. 沒有這個 Tag 的物件只能當地面，手抓住時只能水平移動
3. 物件必須有 Collider，手才抓得到

`Player` 的設定：
- Camera Rig 上的 OVR Manager，`Tracking Origin Type` 要設為 **Floor Level**
- 手上的 Sphere 不要加 Collider，避免擋住身體

---

## 參數調整

點選 `Player` → Inspector → `Lizard Locomotion`

| 參數 | 說明 | 預設值 | 調整方向 |
|------|------|--------|---------|
| `Grab Radius` | 手距離表面多近會抓住（公尺） | 0.12 | 手要壓很低才抓得到 → 調高；還沒碰到就抓住 → 調低 |
| `Release Radius` | 手距離地面多遠會放開（公尺），要比 `Grab Radius` 大 | 0.18 | 容易不小心放開 → 調高；抬手抬很高才放開 → 調低 |
| `Pull Gain` | 手移動 1 公尺時，身體移動幾公尺 | 1 | 覺得太慢 → 調高；太快 → 調低 |
| `Max Hand Speed` | 手速超過這個值（公尺/秒）視為追蹤跳動，該幀不移動 | 6 | 快速拉手時身體偶爾不動 → 調高；仍會瞬間跳動 → 調低 |
| `Allow Backward On Ground` | 在地面上能否往視線後方移動 | 關閉 | 想要倒退爬 → 開啟，但手貼地往前伸時身體也會往後 |
| `Surface Mask` | 哪些 Layer 的物件可以抓 | Everything | 只想抓特定物件時再調整 |
| `Log Grab Events` | 在 Console 顯示抓住和放開 | 開啟 | 不需要除錯時可關閉 |

### 斜坡角度門檻
點選 `Player` → Inspector → `Character Controller`

| 參數 | 說明 | 預設值 | 調整方向 |
|------|------|--------|---------|
| `Slope Limit` | 幾度以內可以直接爬上去 | 45 | 想讓更陡的坡也能直接走 → 調高；想更早需要攀爬 → 調低 |

---

## 常見問題

**Q：手放到地上往後拉，身體沒有移動？**  
→ 把 `Grab Radius` 調高，並確認 `Tracking Origin Type` 是 Floor Level，頭顯的地板高度也設定正確。開啟 `Log Grab Events` 後，可以在 Console 確認有沒有抓住

**Q：手抬起來還是沒放開，手往前伸時也在抓著？**  
→ 把 `Grab Radius` 和 `Release Radius` 調低，或把手抬高一點再往前伸

**Q：手碰到牆卻爬不上去？**  
→ 確認有按住 Grip，牆壁也有 `Climbable` Tag 和 Collider

**Q：爬行速度太慢？**  
→ 把 `Pull Gain` 調高（例如改成 `1.5`）

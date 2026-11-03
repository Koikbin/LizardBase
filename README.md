# 蜥蜴爬行遊戲 — Lizard Locomotion

## 更新內容（walk-&-climb branch）

### 新增檔案
| 檔案 | 位置 | 說明 |
|------|------|------|
| `LizardGame.unity` | `Assets/Scenes/` | 主要遊戲場景 |
| `LizardLocomotion.cs` | `Assets/Scripts/` | 地面爬行邏輯腳本 |
| `LizardClimbing.cs` | `Assets/Scripts/` | 攀爬邏輯腳本 |
| `Palm.prefab` | `Assets/Scenes/` | 手部視覺 Prefab |

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

模擬蜥蜴在地面爬行與垂直攀爬的 VR 體驗。玩家透過揮動手把和按壓按鍵來控制角色移動。

---

## 操作方式

### 動作一：地面爬行
- **手往前伸**（抓地）→ **手往後拉**（推進）= 前進一步
- 左右手**交替**做這個動作，模擬蜥蜴四肢爬行
- 停止揮動後角色會慢慢滑停

### 動作二：攀爬
- 面對牆壁，**按住 Grip 鍵**（手把側邊的扳機）
- 手往上移動 → 身體跟著往上
- **左右手輪流**按 Grip 往上移，交替攀爬
- 放開 Grip → 手放開，角色開始受重力下落

### 斜坡規則
- **緩坡（45度以下）**：直接揮手爬行即可通過，不需要按 Grip
- **陡坡／垂直牆（45度以上）**：需要按住 Grip 才能攀爬

---

## 場景設定

可攀爬的牆壁需要加上 **`Climbable` Tag**：

1. 點選牆壁物件 → Inspector → `Tag` → 選 `Climbable`
2. 沒有這個 Tag 的物件無法攀爬

---

## 參數調整

### 地面爬行參數
點選 `Player` → Inspector → `Lizard Locomotion`

| 參數 | 說明 | 預設值 | 調整方向 |
|------|------|--------|---------|
| `Speed Multiplier` | 每次推進的速度 | 6 | 覺得太慢 → 調高；太快 → 調低 |
| `Pull Threshold` | 往後拉多快才觸發推進 | 0.15 | 覺得要用力揮才有反應 → 調低 |
| `Reach Threshold` | 往前伸多快才進入抓地 | 0.1 | 同上 → 調低 |
| `Momentum` | 推進後的滑行距離 | 0.92 | 想滑更遠 → 調高（最高 0.98）；想立即停 → 調低 |

### 攀爬參數
點選 `Player` → Inspector → `Lizard Climbing`

| 參數 | 說明 | 預設值 | 調整方向 |
|------|------|--------|---------|
| `Climb Speed` | 攀爬速度 | 3 | 覺得太慢 → 調高；太快 → 調低 |

### 斜坡角度門檻
點選 `Player` → Inspector → `Character Controller`

| 參數 | 說明 | 預設值 | 調整方向 |
|------|------|--------|---------|
| `Slope Limit` | 幾度以內可以直接爬上去 | 45 | 想讓更陡的坡也能直接走 → 調高；想更早需要用 Grip → 調低 |

---

## 常見問題

**Q：揮手幅度很大才有反應？**  
→ 把 `Pull Threshold` 和 `Reach Threshold` 調低（例如改成 `0.05`）

**Q：每次推進之間間隔太長、不流暢？**  
→ 把 `Momentum` 調高（例如改成 `0.95`）

**Q：按 Grip 沒有反應？**  
→ 確認你面對的牆壁有加上 `Climbable` Tag

**Q：攀爬速度太慢？**  
→ 把 `Climb Speed` 調高（例如改成 `5`）

# AirPodsSwitch

在 Windows 上，用小巧的浮動圖示或鍵盤快捷鍵，將 AirPods 連回電腦。

[下載 Windows 版本](https://github.com/baka9881/AirPodsSwitch/releases/latest) · [回報問題](https://github.com/baka9881/AirPodsSwitch/issues)

![40、48、64 像素的浮動圖示](docs/widget-preview.png)

## 開始使用

1. 先在 Windows 藍牙設定中完成耳機配對。
2. 從 [Releases](https://github.com/baka9881/AirPodsSwitch/releases/latest) 下載 `AirPodsSwitch-0.2.0-win-x64.zip` 並解壓縮。
3. 執行 `AirPodsSwitch.exe`，第一次啟動時選擇自己的耳機。
4. 點一下浮動圖示，就會要求耳機連回電腦並解除耳機輸出靜音。

需要 Windows 10／11 x64、.NET Framework 4.8，以及已配對的藍牙音訊耳機。介面使用繁體中文。

## 操作

| 操作 | 功能 |
| --- | --- |
| 左鍵單擊（未連線／已靜音） | 立刻連回電腦並解除靜音、保護音量與設為預設輸出 |
| 左鍵雙擊（已連線）或中鍵單擊 | 中斷電腦的耳機連線（讓給 iPhone） |
| 滑鼠滾輪（停在圖示上） | 即時微調耳機音量（每格 ±2%）並記住新音量 |
| `Ctrl + Alt + A` | 連回電腦並解除耳機靜音 |
| `Ctrl + Alt + D` | 中斷電腦的耳機連線 |
| 拖曳圖示 | 移動圖示，並記住位置 |
| 右鍵選單 | 選擇耳機、調整圖示大小、置頂、登入啟動、隱藏或結束 |

### 全天候保護與智慧連線

- **漸進重送節奏**：前 3 次每 2.5 秒重送搶快，第 4～6 次放寬為 4.0～4.5 秒，避免打斷藍牙交接；若 20 秒內未連上，會在背景續等 60 秒並溫和補送要求。
- **全天候自動守門員**：就算未按按鈕、直接戴上耳機讓電腦自動連上，也會自動解除靜音、將暴衝音量（與上次習慣音量差超過 10%）調回，並自動切回預設立體聲播放裝置。
- **視覺與狀態提示**：使用每像素透明無鋸齒圓角圖層。狀態色點：綠點＝已連線、橘點斜線＝已連線但靜音、灰點＝未連線、藍圈＝連線中、紅點＝失敗；若 Windows 有回報藍牙電量，滑鼠懸停時也會一併顯示。
- **自動重綁與紀錄瘦身**：藍牙重配對導致舊 ID 失效時自動依名稱重新綁定；本機 log 超過 1,000 行自動保留最新紀錄。
- **內建自動測試**：執行 `AirPodsSwitch.exe --self-test` 可驗證核心邏輯與透明圖層渲染。

### 連線狀態提示

切換時，浮動圖示旁會跳出小提示框，不需要開啟 Windows 通知：

- 超過 6 秒還沒連上，顯示「還在連線…」。
- 連線時間到了但耳機還沒出現時，不會馬上判定失敗；圖示繼續轉圈，在背景再等最多 60 秒。耳機一連上就自動解除靜音並顯示「已連回電腦」。60 秒後仍未連上，才顯示「沒有連上」，圖示右下角變紅點。
- 成功連回或中斷時，也會簡短顯示結果與音量。點提示框可立即關閉。

Windows 右下角的系統匣圖示會用小圓點顯示狀態：綠色為已連線、灰色為未連線、紅色為失敗、藍色為切換中。

圖示預設為 48 × 48 像素，也可以改為 40 或 64 像素。隱藏浮動圖示後，快捷鍵仍然可用；從 Windows 右下角圖示可再次顯示。

如果藍牙驅動在重連時將音量設為 0，程式會使用先前記錄的非零耳機音量。麥克風靜音與個別播放器的靜音由各自的程式控制。

## 與 iPhone 切換

在 iPhone 使用 AirPods 時，按 `Ctrl + Alt + A` 可要求耳機連回 Windows。即使 Windows 先前仍顯示「已連線」，程式也會實際送出重連要求。

按 `Ctrl + Alt + D` 會中斷 Windows 的耳機連線。iPhone 是否自動接回由手機與耳機決定；必要時請在 iPhone 控制中心選擇 AirPods。

此程式已在一台 Windows 11 x64／Intel Bluetooth 電腦上完成重連與解除靜音測試，並由使用者確認從 iPhone 成功切回電腦。其他驅動、耳機型號或通話情境仍可能有差異。原生驅動接受要求並不等於耳機一定完成交接。

## 設定資料

耳機選擇、圖示位置、音量記錄和本機操作紀錄儲存在 `%LOCALAPPDATA%\AirPodsSwitch`。程式本身不會將這些資料上傳。

從右鍵選單的「選擇耳機」可更換目標。若同時使用多副同名耳機，請先在系統中改成不同名稱。

## 從原始碼編譯

在 Windows 中執行：

```powershell
powershell -NoProfile -File .\build.ps1
```

腳本使用 Windows 的 .NET Framework C# 編譯器，會在 `dist` 產生可執行檔、ZIP 與 SHA-256 校驗檔。

[GitHub Actions 範例](docs/build-workflow.example.yml) 可複製至 `.github/workflows/build.yml`，讓推送與 Pull Request 自動編譯並保存下載成品。目前儲存庫未啟用此工作流程。

可指定版本或輸出目錄：

```powershell
.\build.ps1 -Version 0.2.0 -OutputDirectory .\dist
```

## 開發參考

音訊連線使用 Windows 的 [KSPROPERTY_ONESHOT_RECONNECT](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-reconnect) 與 [KSPROPSETID_BtAudio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/kspropsetid-btaudio)。

實作方向參考 [BluetoothDevicePairing](https://github.com/PolarGoose/BluetoothDevicePairing)。本專案不綁定 Apple 帳號，也不是 Apple 官方產品。

## 授權

[MIT License](LICENSE)。Copyright © 2026 baka9881。

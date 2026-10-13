# iPhoneMirror pre5 开发与验收清单

基线：`feature/stage1-pre4-shell-usb-upstream`，commit `c6b40645f80e24ceae9ee0e55e35e27e57eff07f`。
开发：`feature/stage1-pre5-media-layout-wireless`，PR #14（Draft）。
此文档是开发追踪而不是已验证完成声明。

## P0：媒体录制功能

| 项目 | 文件 | 当前开发状态 | 验收 |
|---|---|---|---|
| 默认 60 FPS | `src/App/Windows/MediaOutputSettingsWindow.xaml.cs` | 已提交 | 首次打开默认 60，视频实际 60；用户旧设置保留需后续补 |
| 录制目录 | `src/App/Updater/UpdateSettingsStore.cs`, `src/App/Windows/MediaOutputSettingsWindow.xaml[.cs]` | 已提交 | 首次目录 `Videos/iPhoneMirror`，可选择目录，自动完成并保存 MP4 |
| 完成录制按钮 | `MediaOutputSettingsWindow.xaml[.cs]` | 已提交 | 录制按钮“完成录制”，推流按钮“停止推流” |
| 音频连续性 | `src/App/Services/MediaOutputService.cs` | 已移除欠载后跳过缓存包的路径；根因诊断仍需实测 | 连续 1/10/30 min 实录，不得有明显爆音、丢段；同步漂移 <150 ms |
| 背景画布原始尺寸 | `PhoneShellMediaComposer.cs`, `MediaOutputService.cs` | 已提交 | 1280x1440 背景导出 1280x1440 MP4，不裁边；奇数宽高给出约束提示 |
| 自定义布局 | `src/App/Windows/RecordingLayoutEditorWindow.cs`, `MediaOutputSettingsWindow.xaml[.cs]`, `UpdateSettingsStore.cs`, `PhoneShellMediaComposer.cs` | 已提交，待窗口交互验证 | 可拖动、滚轮缩放、居中重置、持久化；录制与编辑器位置一致 |
| 停止封装与恢复 | `MediaOutputService.cs`, `PendingRecordingStore.cs`, `MediaOutputSettingsWindow.xaml.cs` | 继承 pre3，增加自动保存 | 正常封装、可拖动进度条；异常保留 partial 并提示 |

## P0：无线反向控制

| 项目 | 文件 | 当前开发状态 | 验收 |
|---|---|---|---|
| 不再排除 AirPlay 媒体投屏设备 | `MainViewModel.cs`, `DeviceBindingWindow.xaml.cs` | 已提交 | AirPlay 投屏设备出现在绑定列表，控制按钮可进入前置检查 |
| 未绑定自动打开设备绑定窗口 | `MainViewModel.cs`, `MainWindow.xaml.cs` | 已提交 | 未找到 Apple UDID 时有具体提示及绑定入口 |
| 有效且唯一的 Apple 身份 | `DeviceIdentityResolver.cs`, `DeviceBindingManager.cs`, `DirectUsbInputBridge.cs` | 待真机 | USB 已信任的 iPhone 与 AirPlay 身份准确绑定，不可选择其他设备 |
| Wi-Fi CoreDevice HID 建链 | `UsbTouchBridgeHost.cs`, `tools/usb_touch_bridge.py` | 保留 pre4 原版桥接 | 真机启动 HID（包含 `mainTouchscreen` 接口 257）并成功鼠标操作；不能保证 iOS 27.2 支持 |

## P1：手机壳精确一致性

| 项目 | 文件 | 当前开发状态 | 验收 |
|---|---|---|---|
| 最终指定高分辨率 PNG | `src/App/Assets/iphone16_pro_max_shell.png` | **尚未替换入 GitHub 包** | 使用最后一张用户原图制作真正透明壳；无顶部裂缝，不应重复叠加灵动岛 |
| 统一屏幕矩形/圆角裁切 | `src/App/Models/DeviceFrameProfile.cs`, `DeviceFrameProfileResolver.cs`, `PhoneShellMediaComposer.cs` | 待再次校准 | 1x、1.25x、2x 缩放每侧不存在露白、顶边色条、1px 缝 |
| D3D11 独立窗口贴图一致 | `src/Core/src/Renderer/D3D11PreviewRenderer.cpp` | 继承 pre4；新素材待接 | 横竖屏缩放及窗口切换不破坏触控坐标 |
| 录制 / 推流壳与背景对齐 | `PhoneShellMediaComposer.cs`, `MediaOutputService.cs` | 合成参数变更已提交，待实机 | 两条输出共用素材和布局；不二次填充、不裁中间 |

## P2：自动化与发布

- `src/App.Logic.Tests/Program.cs`：背景画布不裁切、无音频/音频、保存布局默认参数断言
- `src/App.Runtime.Tests`：窗口加载、透明素材、拖放坐标、旧配置恢复
- `scripts/verify_localization.ps1`：中英繁多语言
- `.github/workflows/windows-build.yml`：Windows x64 build/test/package，产出单层 ZIP Artifact
- 仅当 CI 全绿，并经过 iPhone 16 Pro Max 实机验收后，把 Draft 变更为可合并。

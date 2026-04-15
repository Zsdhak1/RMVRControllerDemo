# 临时修改记录

## 修改时间
2026/04/13

## 修改原因
手柄暂时无法使用，需禁用所有与 VR 手柄/控制器相关的输入功能。

## 修改文件清单

### 1. EngineerVRInput.cs
- **修改位置**: 新增 `disableControllerInput = true` 字段
- **影响范围**: `Update()` 方法开头增加 `if (disableControllerInput) return;`
- **禁用功能**: 左手柄 Grip 转鼠标位移、左摇杆 WASD、物理按键宏、菜单键呼出 UI

### 2. DirectGrabController.cs
- **修改位置**: 新增 `disableGrabInput = true` 字段
- **影响范围**: `HandlePointerEvent()` 开头增加禁用判断
- **禁用功能**: VR 手柄抓取/松开事件不再通知 IK 控制器接管或停止跟踪

### 3. RobotDataSender.cs
- **修改位置**: 新增 `disableGripperInput = true` 字段
- **影响范围**: 
  - `bool triggerPressed` 从读取 `OVRInput` 改为固定 `false`
  - 夹爪数据 (Byte 14) 固定为 `0`（松开状态）
- **禁用功能**: 不再读取右手食指扳机控制夹爪

### 4. VisorController.cs
- **修改位置**: 新增 `disableVisorInput = true` 字段
- **影响范围**: `Update()` 方法开头增加 `if (disableVisorInput) return;`
- **禁用功能**: 左手柄摇杆隐藏面罩、长按 Y 键复位面罩位置

### 5. PassthroughController.cs
- **修改位置**: 新增 `disablePassthroughInput = true` 字段
- **影响范围**: `Update()` 方法开头增加 `if (disablePassthroughInput) return;`
- **禁用功能**: 右手 B 键切换 MR/VR 透视模式

### 6. OVRInputVisualizer.cs
- **修改位置**: 新增 `disableVisualizer = true` 字段
- **影响范围**: `Update()` 方法开头增加 `if (disableVisualizer) return;`
- **禁用功能**: 手柄摇杆、扳机、ABXY 按键的可视化更新

### 7. RobotIKController.cs
- **修改位置**: 新增 `disableControllerJ7Input = true` 字段
- **影响范围**: `Update()` 中 J7 摇杆旋转逻辑增加 `if (!disableControllerJ7Input)` 判断
- **禁用功能**: 右手柄摇杆控制 J7 末端旋转

### 8. RobotDataSender.cs — 数据发送调整
- **新增字段**:
  - `useManualAngles = false`：是否启用手动角度覆盖
  - `manualAngles = float[7]`：Inspector 中直接设置的 7 个关节角度
- **逻辑调整**:
  - `PackAndSend()` 中，当 `useManualAngles = true` 时，不再调用 `robotController.GetPacketData()`，而是直接将 `manualAngles` 按 `short ×100` 编码填入 `byte[30]`
  - 夹爪状态因 `disableGripperInput = true` 固定为 `0`
- **用途**: 无手柄时，可在 Inspector 中手动指定固定姿态进行下位机通信测试

## 恢复方法
将上述各脚本中的 `disableXxx = true` 改回 `false`，或在 Unity Inspector 中取消勾选对应开关。

## 当前数据发送状态（无手柄时）
- `RobotDataSender` 仍以 50Hz 发送 `CustomControl`
- 若 `useManualAngles = false`：发送 `RobotIKController` 当前的 `outAngles`（可能保持初始姿态）
- 若 `useManualAngles = true`：发送 Inspector 中手动填写的 7 个角度
- **数据编码已从 `short ×100` 改为 `float` 小端序直接拷贝**：
  - 参考 `CC_Task.c` 中 `((uint8_t *)&cc.send_pos[i])[0..3]` 的强转方式
  - `Byte 0~27`：7 个 `float` 关节角度（每个 4 字节，小端序）
  - `Byte 28`：夹爪状态（固定 `0`）
  - `Byte 29`：校验和（已禁用，保持 `0`）
- `RobotIKController.GetPacketData()` 不再被 `RobotDataSender` 使用，改为直接读取 `robotController.outAngles`

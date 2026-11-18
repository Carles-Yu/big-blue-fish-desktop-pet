# Cursor 额度查询

Windows 鱼鱼的角色右键菜单、托盘菜单在「额度查询」中提供 Cursor。打开后自动读取一次；点击「刷新额度」手动重查。面板复用现有卡片布局，显示 Auto 与 API 的剩余百分比、重置时间与最近成功更新时间，时间使用本机时区。没有后台轮询或自动弹出的低额度提醒；剩余不超过 20% 时只在卡片内标记「余量较低」。Linux 没有此菜单。

## 数据与登录

官方 `cursor` 编辑器命令行没有额度查询参数。官方 Agent CLI 的文档命令列表也没有非交互的 `usage` 子命令；changelog 中的 `/usage` 只出现在交互式会话里，不能当作 `agy -p "/usage"` 那样的只读命令来调用。团队 Admin API 需要管理员密钥，不适用于本机个人登录的这块面板。

因此仍使用本机已登录的 Cursor 账户查询 `https://cursor.com/api/usage-summary`：只读打开 `%APPDATA%\Cursor\User\globalStorage\state.vscdb` 中的 `cursorAuth/accessToken`。若令牌尚未加密，直接组装会话 cookie；若为 `cursor-safe-storage:v1`，则用当前 Windows 账户的 DPAPI 从 `%APPDATA%\Cursor\Local State` 的 `os_crypt.encrypted_key` 取出 Chromium OSCrypt 密钥，再按 AES-256-GCM 解锁。不调用非官方的内部接口去绕过登录，不发起模型对话。

令牌、密钥与密文不会写入日志、界面、聊天或记忆。显示的是查询进程使用的本机 Cursor 登录账户。需要先在本机 Cursor 中保持登录，并用同一 Windows 账户运行鱼鱼。未知的 `cursor-safe-storage` 版本会提示加密方式已更新；缺少解锁文件或无法在当前账户下解密时给出对应中文说明，而不把密文或令牌展示出来。缺少的数据展示「未知」，不是 0 或无限量。刷新失败时保留上次成功结果并明确标记尚未刷新。

## 验证

- 会话 cookie、过期令牌、尚未解锁的密文直接交给 cookie 组装时仍被拒绝。
- 损坏的 v1 密文无法解锁；未知版本提示加密方式已更新。
- 用已知 AES 密钥往返解锁测试令牌；在临时 Local State 与临时数据库上通过当前用户 DPAPI 解锁后查询 usage-summary。
- 查询结果、失败说明和临时 Local State 都不包含令牌或密文前缀。
- 本机当前没有 .NET 10 SDK 时，上述 C# 验证需在 SDK 可用后运行。

品牌图标来源见 `assets/brands/README.md`；图标嵌入程序集，不依赖运行时网络。

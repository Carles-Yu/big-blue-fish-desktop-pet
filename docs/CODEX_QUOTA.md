# Codex 额度查询

Windows 鱼鱼的角色右键菜单、托盘菜单均增加「Codex 额度」。打开后自动读取一次；点击「刷新额度」手动重查。面板显示各额度窗口的剩余百分比、重置时间与最近成功更新时间，时间使用本机时区。没有后台轮询或自动低额度提醒。

## 数据与登录

使用官方 Codex app-server 的 stdio 协议：initialize → initialized → account/rateLimits/read。不创建线程、不发起模型请求、不消费额度重置券、不发送邮件，不修改 Codex 配置或读取凭据文件。Codex 自己处理本机既有登录和网络请求；鱼鱼不接收或保存登录令牌。额度不会进入聊天记录或记忆同步。

显示的是查询进程使用的本机 Codex 登录账户，不保证与你另一个自定义 CODEX_HOME 或其他设备里的账户相同。API Key 登录无法提供这里的订阅额度。需要先在本机 Codex 中使用 ChatGPT 账户登录。

优先解析 rateLimitsByLimitId，兼容旧版 rateLimits；remaining = clamp(100 - usedPercent, 0, 100)。窗口长度由返回值决定，不把 primary/secondary 固定假设成 5 小时/每周。缺少的数据展示「未知」，不是 0 或无限量。刷新失败时保留上次成功结果并明确标记尚未刷新。

## Codex 路径

依次发现：BIGBLUEFISH_CODEX_PATH 显式路径、PATH 中的 codex.exe、当前用户 LocalAppData/OpenAI/Codex/bin 下的桌面客户端捆绑程序、常见 npm 安装目录里的 Windows x64 原生程序。显式路径必须为存在的绝对 exe 路径；无效时提示用户，不静默换到其他程序。不通过 cmd/PowerShell 执行 npm shim。

每次查询启动一个隐藏的只读 app-server 子进程，30 秒超时。完成后关闭 stdin 并等待退出；超时终止该查询进程及其子树。Windows Job 对象保障鱼鱼退出时回收。关闭查询面板取消请求。stderr 只排空，不保存或展示；服务端原始错误体不进入界面或日志。

## 验证

- 16 项规则、子进程协议和 WPF 界面检查：新旧响应、多个额度池、缺失值、真实零剩余、异常百分比、登录失败、旧版、异常 JSON、取消、缺少程序、刷新失败保留旧数据。
- 协议检查只发送 initialize、initialized 和 account/rateLimits/read。
- 本机真实 Codex 查询成功，返回 300 分钟和 10080 分钟窗口；百分比为查询时快照，不写入文档作为当前值。
- Windows 专属；Linux 本次没有新增额度菜单。

官方参考：[Codex App Server](https://learn.chatgpt.com/docs/app-server)。

## 2026-10-27 外观调整

角色右键与托盘的「额度查询」二级菜单使用对应品牌图标，来源见 assets/brands/README.md；图标嵌入程序集，不依赖运行时网络。Codex/Cursor 额度面板共用卡片布局：品牌标记、查询状态、大号剩余百分比、低余量提示、重置时间及更新时间。刷新失败保留旧数据并标记尚未刷新；未知额度显示中性色状态。保留各自查询实现和独立 Auto/API 数据，不增加自动查询或模型调用。

验证：Codex 协议/格式/UI 18 项、Cursor 查询规则 17 项通过；已渲染查看两个面板的示例数据。图标同时验证 WPF 与原生托盘渲染所需位图加载。

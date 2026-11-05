# 当前华为云服务器与记忆同步适配检查

检查日期：2026-10-26。依据：[SSH 连接说明](CLOUD_SSH.md)、当前工作区的客户端/服务端源码、部署脚本，以及通过 `ssh bluefish` 获得的服务器实测结果。

## 结论

**硬件与系统适配，客户端/API 架构可沿用；当前部署配置尚未适配这台服务器的实际网络，也尚未完成真实云端同步验收。** 无需为了私人文字记忆同步更换或升级服务器。首期应采用现有 Tailscale 私网入口，完成网络和运行环境准备后再部署。

**同日复测修正：IPv6 公网访问正常，当前主要障碍是系统所用 IPv4 DNS 不可达，以及部分依赖源的 IPv6 可用性。不能将初次域名访问超时概括成“所有外网不通”。最新实测见第 6 节；前文超时记录保留为初次检查结果。**

**后续实施更新：DNS 已修复，PostgreSQL 与 systemd API 已部署，并通过 11 项真实服务端检查；Serve 已授权，私网 HTTPS、证书及配对/撤销均通过本机实测，具体见第 7、8 节。前文“本次只读”指初次检查，后续授权开展的配置与安装操作单独记录。**

本次只对服务器执行查询与连接探测，没有安装软件、上传部署文件、生成配对码、更改网络、启用 HTTPS 或修改数据库。仅在本地执行构建与已有测试。

## 1. 实测服务器状态

| 项目 | 结果 | 判断 |
| --- | --- | --- |
| SSH | `ssh bluefish` 可登录 | 已连通，沿用本机现有 SSH 配置，无需新密钥 |
| 系统 | Ubuntu 24.04.4 LTS，Linux 6.8，x86_64 | 适合 Linux x64 容器或 .NET 服务 |
| CPU | 2 个逻辑处理器 | 适合少量个人设备的轻量同步，尚未压测 |
| 内存 | 总计约 3.45 GiB，可用约 2.98 GiB；无 swap | 与原计划的 4 GB 档位相符，不宜同机跑模型 |
| 磁盘 | 根盘 50 GB，可用约 44 GB | 文字记忆及数据库有足够起步空间，需限制日志和备份增长 |
| 接入网络 | Tailscale `100.70.65.105`，本机连接显示经 relay `lax` | 可访问，但不能假定当前连接为低延迟直连 |
| 运行环境 | PATH 中未找到 Docker、Podman、dotnet、Caddy、psql | 尚不具备按现有 Compose 部署文件启动服务的条件 |
| 服务端口 | 未见同步 HTTP/HTTPS 服务监听；`tailscale serve status` 返回 `No serve config` | 当前无法进行真实记忆 API 联调 |
| HTTPS | 节点名 `hcss-ecs-1b1e.tailc95d8b.ts.net`；状态未列出证书域名 | 不能认定证书已准备好，需后续核实并启用 HTTPS |
| 防火墙 | UFW inactive；未检查华为云控制台安全组 | 不等于所有端口已开放；部署时需核查云端安全组与 Tailscale 访问策略 |
| 时钟 | NTP 已同步 | 适合配对码有效期和服务日志时间判断 |

SSH 文档记录公网入口只有 IPv6，本机没有可用公网 IPv6 出口；本次沿用 Tailscale 成功连接，没有据此推断公网 IPv6 已能从本机访问。

## 2. 需要先解决的部署阻碍

### 2.1 现有公网域名入口与实际接入方式不一致

当前 [Compose](../server/deploy/docker-compose.yml) 启动 Caddy 并映射 80/443，[Caddyfile](../server/deploy/Caddyfile) 依赖 `SYNC_DOMAIN`，配置示例使用 `memory.example.com`。这是一套需要域名、证书与可达入口准备的部署方式，不能直接把 SSH 别名 `bluefish` 当成桌宠同步地址。

建议为本服务器增加独立的 Tailscale 部署配置：

```text
各设备上的鱼鱼（设备需有 Tailscale 访问权限）
                  ↓ HTTPS，Tailscale 私网
          主机上的 Tailscale Serve
                  ↓ http://127.0.0.1:8080
       Docker API，端口仅映射到宿主机 loopback
                  ↓ Docker 内部网络
             PostgreSQL，不映射公网
```

适配动作：API 增加 `127.0.0.1:8080:8080` 映射；此部署模式停用现有 Caddy 容器；配置 Tailscale Serve 代理回环地址，不启用 Funnel 公网发布。原有 Caddy 公网模式可另行保留，避免混用。

完成 HTTPS 开启、设备访问策略及 `/health` 检查后，客户端预计填写 `https://hcss-ecs-1b1e.tailc95d8b.ts.net`。**这是待启用的目标地址，本次没有部署或验证其 HTTPS 服务。** 不能直接填 `https://100.70.65.105` 并假定证书匹配。

Tailscale 官方说明 Serve 可把本地服务以 HTTPS 提供给 tailnet 内设备，未配置 HTTPS 时可能要求管理员先开启功能。[Serve 文档](https://tailscale.com/docs/features/tailscale-serve)、[HTTPS 配置](https://tailscale.com/docs/how-to/set-up-https-certificates)。

现有客户端使用可配置服务器 URL 和标准 HttpClient，无需为 Tailscale 改同步协议。网络隧道不替代设备 Token、配对与记忆正文加密；也不应为图方便关闭证书验证。

### 2.2 外网解析和出口尚未通过

服务器对以下地址的 HTTPS 探测均在 DNS 解析阶段超时：

- `registry-1.docker.io`：PostgreSQL/Caddy 镜像来源相关。
- `mcr.microsoft.com`：当前 Dockerfile 的 .NET SDK 与 ASP.NET 运行时来源。
- `api.nuget.org`：构建依赖来源。

`curl -6` 访问 MCR 同样解析超时；额外对公网 IPv4/IPv6 地址的 TCP 443 短时连接探测均未连通。系统当前配置的上游 DNS 为 `100.125.1.250`，但**这些结果不足以确认根因**，不能直接断言只改 DNS 即可。

部署前应核实云端出站规则、DNS 可达性、IPv4 出口能力与 IPv6 路径。若暂时无法稳定拉取镜像，可在可信且联网的机器构建 Linux amd64 镜像，导出后通过现有 SSH 通道传输并加载；但服务器仍需安装容器运行环境，证书签发/更新与远端备份的网络条件也要独立验证，离线传镜像不能替代全部出口需求。

## 3. 当前代码与脚本的适配风险

以下来自源码检查，尚未通过远程实际部署复现，不是完整代码审计。

| 项目 | 当前情况 | 部署前处理 |
| --- | --- | --- |
| 数据库启动等待 | Compose 仅有普通 `depends_on`；API 在启动阶段立即打开数据库、执行迁移 | 为 PostgreSQL 增加 `pg_isready` 健康检查与 `service_healthy` 等待，迁移连接失败做有限重试 |
| HTTPS 地址校验 | `CompanionMemory.RequireServer()` 提示 HTTPS，但实际允许任意 HTTP URL | 正式服务器要求 HTTPS；开发用 HTTP 应限制为明确的本机调试场景 |
| 推送批次 | 服务端最多接收 50 个操作；当前 `SyncClient` 把全部 Outbox 作为一次推送 | 超过 50 条时会被拒绝；客户端应分批、逐批确认，验证离线积累后的补传 |
| 拉取与状态显示 | 当前每次最多拉 200 条；报告的“送出”数量是尝试发送数量，非接受数量 | 明确分页追赶与冲突/拒绝结果，避免把未完成同步显示为完成 |
| 备份变量 | 脚本直接要求环境变量，README 未交代如何把 `.env` 中所需变量传给备份任务 | 给出可复现的受限凭据加载方式，并验证定时任务环境 |
| 备份失败判定 | `sh` 管道只有 `set -eu`，没有逐段失败检查 | 确保 `pg_dump` 失败不会留下看似成功的加密备份；上传成功后再报告远端备份完成 |
| 恢复行为 | 普通 SQL dump 直接灌回现有库，脚本没有清理现有数据、严格 SQL 失败退出或重置同步世代 | 修正恢复流程并做演练；当前脚本不能视作已验证的“覆盖恢复” |

Docker 文档明确区分容器启动顺序与健康就绪，`service_healthy` 可用于等待依赖健康。[Compose 启动顺序](https://docs.docker.com/compose/how-tos/startup-order/)。

## 4. 本次本地验证

- `dotnet build server/BigBlueFish.SyncServer/BigBlueFish.SyncServer.csproj -c Release --no-restore`：成功，0 警告、0 错误。
- `dotnet run --project code/tests/MemoryVerification/MemoryVerification.csproj -c Release --no-restore`：`MEMORY-OK 28`。
- 这些检查证明当前本机构建与已有记忆测试通过；没有覆盖 Linux 容器构建、真实 PostgreSQL、Tailscale HTTPS、真实双客户端同步或备份恢复，不应据此宣布云端可用。

## 5. 下一步顺序

1. 修复并验证服务器 DNS/出口；确定在线安装或可信离线镜像传输路线。
2. 安装容器环境，增加 Tailscale 专用部署配置及数据库健康等待。
3. 修正客户端批次、状态反馈与正式 HTTPS 限制，完善备份恢复脚本。
4. 配置 Tailscale HTTPS 入口和访问策略，部署 API 与数据库；凭据单独生成，不进入仓库。
5. 两台设备实际验证 `/health`、配对、记忆新增/修改/删除、离线补传和大于 50 条的批量同步。
6. 完成独立备份与恢复演练，再投入日常使用。

该顺序不需要新增公网 IPv4 或先购买独立域名；前提是相关设备能够持续通过 Tailscale 访问服务器，且 Serve HTTPS 的启用与证书网络条件满足要求。

## 6. 国内网络及 IPv6 专项复测（同日补充）

主人已提供出方向配置：允许 IPv4 全部协议到 `0.0.0.0/0`，允许 IPv6 全部协议到 `::/0`。本轮不再仅用境外服务判断外网状况，也没有修改服务器 DNS 或其他配置。

### 6.1 DNS 与路径

- 使用系统 DNS 访问百度、华为云官网、华为云软件源、阿里云软件源，IPv4/IPv6 的 curl 均在解析阶段超时；这不能证明目标网站的 IPv6 路径不通。
- 直接查询华为云 IPv4 DNS `100.125.1.250`、阿里 IPv4 DNS `223.5.5.5`、腾讯 IPv4 DNS `119.29.29.29` 均超时。
- 直接查询阿里 IPv6 DNS `2400:3200::1` 成功，UDP 和 TCP 查询均收到应答；ping 延迟约 11 ms。
- 只读检查中，iptables 与 ip6tables 的 OUTPUT 策略均为 ACCEPT，未发现额外的主机出站拒绝规则。不能据此排除所有上游 IPv4 网络限制。

### 6.2 绕过系统 DNS 后的 HTTPS 实测

方法：使用 `dig -6 @2400:3200::1` 查询 AAAA，再以 `curl -6 --resolve` 连接返回的 IPv6 地址；保留域名 SNI 与正常证书校验，未关闭 TLS 校验。

| 目标 | 结果 | 含义 |
| --- | --- | --- |
| 百度 `www.baidu.com` | HTTP 200，约 0.06 秒 | 国内 IPv6 HTTPS 可用 |
| 清华源 `mirrors.tuna.tsinghua.edu.cn` | HTTP 200，约 0.19 秒 | 国内软件源 IPv6 HTTPS 可用 |
| 中科大源 `mirrors.ustc.edu.cn` | HTTP 200，约 0.17 秒 | 国内软件源 IPv6 HTTPS 可用 |
| 华为云官网 `www.huaweicloud.com` | 收到 HTTP 567 | 网络及 TLS 已连通，但应用返回非正常状态，不能称为网站正常可用 |
| 微软镜像源 `mcr.microsoft.com/v2/` | HTTP 200，另行复核成功 | 至少该境外镜像入口也可通过 IPv6 连通；未验证实际镜像与各层下载 |
| 华为云软件源、阿里云软件源、NuGet API | 本次解析未获得最终 AAAA 地址 | 不能直接假设它们能用于这台服务器的 IPv6-only 下载流程 |
| Docker Hub Registry | 对本次 DNS 返回的 IPv6 地址连接超时 | 尚未通过；需另核实解析准确性及可用下载路线，不能仅凭这项判断全部境外网络 |

### 6.3 更新后的处理顺序

先为服务器配置经过验证的 IPv6 DNS，再验证系统解析、系统软件源、Docker 安装来源及镜像/依赖下载。IPv4 不可达是否因为没有 NAT/公网出口，尚未确定，不应直接归因安全组。

已证实无需为了访问百度、清华源、中科大源或 MCR `/v2/` 再开放入方向端口。现有 Dockerfile 仍依赖 NuGet 和 Docker Hub 相关下载，因此“IPv6 通”不等于现有 Compose 已可直接部署；必要时采用可信机器构建后传输 Linux amd64 镜像的路线。

本轮仅连接探测并补充报告，未执行 DNS 修改、软件安装或服务部署。

## 7. IPv6-only 原生部署进度（2026-10-26）

主人要求继续处理后，已完成以下操作：

- 运行时设置 eth0 的 IPv6 DNS 为 `2400:3200::1`、`2400:3200:baba::1`，验证系统解析及清华源 HTTPS 成功。
- 新建独立 `/etc/netplan/99-bluefish-ipv6-dns.yaml`，禁止该链路 DHCP 覆盖 DNS，配置 IPv6 DNS；`netplan generate` 校验成功。没有执行网卡重启、`netplan apply` 或修改 Tailscale 配置；重启后的持久效果尚待验证。
- 使用清华 Ubuntu 源与官方 Ubuntu 签名验证，安装 PostgreSQL 16.15；数据库监听 loopback，不开放公网。
- 在本机发布 Linux x64 程序；因 Tailscale relay 传输较慢，改为上传约 1.5 MB 的框架依赖包。服务器通过 IPv6 下载微软官方 ASP.NET Core 10.0.13 运行时，并验证官方 SHA-512。
- 用 `fishsync` 专用账户与 systemd 运行 API，配置随机数据库与管理凭据，配置文件仅 root 可读，未在工具输出中打印凭据。
- API 仅监听 `127.0.0.1:8080`，`/health` 返回 HTTP 200 和 `{"status":"ok"}`；实测服务 cgroup 内存约 39 MiB，这不是并发压测结果。
- 真实 PostgreSQL/API 冒烟测试 `REMOTE-SMOKE-OK 11`：健康、未授权拒绝、双设备身份、加密写入、幂等、跨设备读取与 AES-GCM 解密、冲突、删除、禁止复活、重置代次。使用合成数据，测试档案已重置，测试设备 Token 已撤销。

随后尝试启用 Tailscale Serve，工具返回 `Serve is not enabled on your tailnet` 并要求账户管理员通过管理页启用。未启用 Funnel，没有新增公网服务监听；`tailscale serve status` 仍是 `No serve config`。

部署脚本与操作说明在 [server/deploy/native](../server/deploy/native/README.md)。下一步是管理员启用 Serve/HTTPS、重新配置转发并从电脑验证 HTTPS，然后处理桌宠真实双端接入与先前记录的客户端边界问题。备份定时任务、外部备份及恢复演练尚未完成，当前状态不能表述成“完整上线验收通过”。

## 8. Serve 授权后 HTTPS 验证（2026-10-26）

管理员完成授权后，已成功运行 `tailscale serve --bg http://127.0.0.1:8080`，入口是 `https://hcss-ecs-1b1e.tailc95d8b.ts.net`，仅 tailnet 内可访问，没有启用 Funnel。首次访问期间 ACME 证书正在签发，短暂超时；证书签发完成后，正常域名访问成功，无需固定 IP 或跳过 TLS 验证。

本机结果：

- `/health`：HTTP 200，`{"status":"ok"}`，curl 的 `TLS_VERIFY=0` 表示证书验证成功。
- 未带设备凭据访问 `/v1/devices`：HTTP 401。
- 无效配对码：HTTP 400，返回配对码无效。
- 使用一次性测试配对码，通过 HTTPS 配对、授权查询设备及撤销设备均成功；撤销后的 Token 再访问返回 401。测试凭据未打印，设备已撤销。

另定位到本机系统代理 `127.0.0.1:7890` 会导致 .NET 默认 HTTP 请求访问 tailnet 发生 EOF。已在共享记忆核心增加 `TailnetBypassProxy`：仅 `.ts.net`、Tailscale IPv4 段及专用 IPv6 段绕过系统 HTTP 代理，公网自建服务继续沿用原代理。不更改用户系统代理、TLS 校验或模型 API 请求。

记忆验证新增代理边界与真实 HTTPS 检查，执行 `dotnet run --project code/tests/MemoryVerification/MemoryVerification.csproj -c Release --no-restore -- --https-health https://hcss-ecs-1b1e.tailc95d8b.ts.net` 返回 `MEMORY-OK 35`。代理适配需重新构建桌宠后才能进入用户正在运行的程序。上述结果仍不代替真实桌宠双设备同步与独立备份恢复验收。

## 9. 客户端、备份及界面后续交付

已修复同档案接入、恢复档案编号、批量分页、离线版本、丢回执重试、冲突选择和重置处理；本机两份独立目录通过当前服务器真实 HTTPS 联调，综合 `MEMORY-OK 61`。服务端同档案事务锁已部署，真实并发版本检查通过，冒烟结果更新为 `REMOTE-SMOKE-OK 12`。合成档案已清空，测试设备 Token 已撤销。

原生加密日备份与本机每日异机拉取已启用，校验、篡改拒绝及隔离恢复演练通过；恢复密钥保存在本机受限目录，与密文快照分开。Windows 记忆页按主人反馈重做为三分区卡片布局。详细结果、试用入口与仍未覆盖的真实多机/灾难切换边界，统一见[交付记录](MEMORY_SYNC_ACCEPTANCE.md)。

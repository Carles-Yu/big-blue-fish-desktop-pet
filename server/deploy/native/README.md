# IPv6-only 华为云：systemd 原生部署

本方案用于当前只有公网 IPv6、通过 Tailscale 管理的个人服务器。无需 Docker、公开注册或公网数据库端口。原 Compose/Caddy 配置保留用于其他网络环境。

链路：`鱼鱼 → Tailscale HTTPS → 127.0.0.1:8080 同步 API → 本机 PostgreSQL 16`。服务器不运行大模型。

## 网络准备

先验证 IPv6 DNS，例如 `dig -6 @2400:3200::1 mirrors.tuna.tsinghua.edu.cn AAAA`。当前服务器已在运行时设置 eth0 的 IPv6 DNS，并添加 `/etc/netplan/99-bluefish-ipv6-dns.yaml`：

```yaml
network:
  version: 2
  ethernets:
    eth0:
      nameservers:
        addresses: ["2400:3200::1", "2400:3200:baba::1"]
      dhcp4-overrides:
        use-dns: false
      dhcp6-overrides:
        use-dns: false
```

已执行 `netplan generate` 校验及生成配置，没有执行 `netplan apply` 或重启网卡。持久配置重启后的效果仍需另行验证。Tailscale 的 DNS 和路由保持原样。

回退 DNS：删除上述独立配置文件后执行 `netplan generate`，再执行 `resolvectl revert eth0` 恢复该链路的运行时设置；不要删除原有 `/etc/netplan/01-netcfg.yaml`。

服务器安装 PostgreSQL 时，使用 `/var/lib/bigbluefish-bootstrap/ubuntu.sources` 指定清华 Ubuntu 镜像，并保持 `Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg` 官方签名验证。apt 命令用 `Acquire::ForceIPv6=true` 及 `Dir::Etc::sourcelist=...`，不修改全局系统源。该源包含 noble、noble-updates、noble-security，main 与 universe；后续更新数据库也需选用可达源。

## 编译与上传

当前采用“小程序包 + 服务器直接下载官方运行时”，避免经 Tailscale 中转上传约 49 MB 自包含包。在有可用 NuGet 网络的开发机，从仓库根目录编译 Linux x64 服务包：

```powershell
dotnet publish server/BigBlueFish.SyncServer/BigBlueFish.SyncServer.csproj -c Release -r linux-x64 --self-contained false -p:RuntimeFrameworkVersion=10.0.13 -p:NuGetLockFilePath=packages.linux-x64.lock.json -o .work/sync-server-linux-x64-framework
tar -czf .work/sync-server-linux-x64-framework.tar.gz -C .work/sync-server-linux-x64-framework .
Get-FileHash .work/sync-server-linux-x64-framework.tar.gz -Algorithm SHA256
scp .work/sync-server-linux-x64-framework.tar.gz server/deploy/native/install.py server/deploy/native/bigbluefish-sync.service bluefish:/var/lib/bigbluefish-bootstrap/
```

如本机 NuGet 索引不可达，可先通过官方 `www.nuget.org/api/v2/package/...` 下载缺少的固定版本包到临时本地源，然后为 publish 指定该源与已缓存包源。不能因此关闭 TLS 或包签名校验。服务器需要单独维护 ASP.NET 运行时安全补丁，以下版本是本次实际部署版本，不代表以后无需更新。

服务器通过 IPv6 下载官方 ASP.NET Core 10.0.13 Linux x64 运行时。先下载 tar.gz 及 `.sha512`，校验成功后才解压：

```sh
cd /var/lib/bigbluefish-bootstrap
curl -6 -fSLO https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.13/aspnetcore-runtime-10.0.13-linux-x64.tar.gz
curl -6 -fSLO https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/10.0.13/aspnetcore-runtime-10.0.13-linux-x64.tar.gz.sha512
sha512sum -c aspnetcore-runtime-10.0.13-linux-x64.tar.gz.sha512
mkdir -p /opt/bigbluefish-sync/dotnet
tar -xzf aspnetcore-runtime-10.0.13-linux-x64.tar.gz -C /opt/bigbluefish-sync/dotnet
/opt/bigbluefish-sync/dotnet/dotnet --list-runtimes
```

服务器准备 PostgreSQL 16 后执行：

```sh
python3 /var/lib/bigbluefish-bootstrap/install.py /var/lib/bigbluefish-bootstrap/sync-server-linux-x64-framework.tar.gz <SHA256>
systemctl status bigbluefish-sync
curl http://127.0.0.1:8080/health
```

安装器为初次部署生成本机数据库角色、数据库和随机管理凭据，已有同名库且没有对应配置时会拒绝接管；后续部署复用凭据，不重建数据库。正文迁移由应用现有代码执行。服务以 `fishsync` 用户运行，程序版本在 `/opt/bigbluefish-sync/releases/`，`current` 指向当前版本；配置在 `/etc/bigbluefish-sync/server.env`，仅 root 可读，勿上传或粘贴到对话。服务单元指定 `DOTNET_ROOT=/opt/bigbluefish-sync/dotnet`，不会依赖全局安装的 dotnet。

## Tailscale HTTPS

确认 loopback 健康后，执行 `tailscale serve --bg http://127.0.0.1:8080`。若工具提示管理页授权，需账户管理员在提示页面启用 Serve/HTTPS；不要改用关闭证书校验或直接公开 HTTP。不启用 Funnel。

当前服务器预期地址是 `https://hcss-ecs-1b1e.tailc95d8b.ts.net`，仅在配置完成并实测 `/health` 后方可交给客户端。所有客户端需有该 tailnet 服务的访问权限；仍需鱼鱼的独立设备配对和正文加密。

2026-10-26 已完成管理员授权与 Serve 配置：该地址的 `/health` 已从本机返回 HTTP 200，证书正常，真实 HTTPS 配对与撤销已验证。若本机有系统 HTTP 代理，应让 tailnet 地址直连；共享记忆核心现已内置此适配，更新客户端构建后生效，无需关闭整个系统代理。

生成配对码使用 root 在服务器本地执行以下命令；码仅短期有效，用完即失效：

```sh
systemd-run --quiet --pipe --wait --collect -p User=fishsync -p EnvironmentFile=/etc/bigbluefish-sync/server.env -p Environment=DOTNET_ROOT=/opt/bigbluefish-sync/dotnet /opt/bigbluefish-sync/current/BigBlueFish.SyncServer --pair
```

## 验收与维护边界

先验收原生 Linux 程序、真实 PostgreSQL、HTTPS 与两设备加密记忆读写，再验证大量补传、冲突和删除。既有客户端超过 50 条操作的分批等问题仍需修正；安装成功不等于完整同步功能已验收。

提供 `smoke.py`，root 在服务器执行 `python3 smoke.py`，依赖 `python3-cryptography`。它通过 loopback API 创建独立测试设备与随机档案，验证真实 PostgreSQL、AES-GCM 读写、幂等、冲突、删除和重置，最后重置测试档案并撤销测试设备凭据；不打印 Token、密钥或配对码。2026-10-26 实际结果：`REMOTE-SMOKE-OK 11`。该测试不代替真实 Windows/Linux 桌宠双端接入验收。

2026-10-26 后续验证：客户端已修复同档案接入、50 条分批、200 条分页、离线版本与冲突选择；真实 HTTPS 隔离双目录验证共 `MEMORY-OK 61`，测试档案已清空。服务端已更新同档案事务锁及单次快照拉取，冒烟新增并发写入验证，结果为 `REMOTE-SMOKE-OK 12`。实际桌宠不同机器测试仍待有其他设备后完成。

原有 `server/deploy/backup.sh` 与 `restore.sh` 依赖 Docker，**不能直接用于原生部署**。原生部署改用本目录的 `backup.py`，依赖 `python3-cryptography`，以 AES-256-GCM 认证加密一致性 pg_dump。安装到 `/opt/bigbluefish-sync/maintenance/backup.py`，将 `bigbluefish-backup.service` 和 `.timer` 放到 `/etc/systemd/system/`，执行 `systemctl daemon-reload` 与 `systemctl enable --now bigbluefish-backup.timer`。当前实例已部署并启用。

```sh
python3 /opt/bigbluefish-sync/maintenance/backup.py backup
python3 /opt/bigbluefish-sync/maintenance/backup.py verify /var/backups/bigbluefish-sync/<备份文件> --restore-test
systemctl list-timers bigbluefish-backup.timer
```

恢复演练只创建随机 `fish_restorecheck_...` 数据库，验证结构后删除临时库，绝不覆盖 `fish` 正式库；篡改认证测试与真实恢复演练均已通过。密钥在 `/etc/bigbluefish-sync/backup.key`，仅 root 可读；备份保留 28 天，明文 dump 限制 256 MiB，超限明确失败。

本机异机副本、密钥分开保管与每日拉取任务见[试用交付记录](../../../docs/MEMORY_SYNC_ACCEPTANCE.md)。旧备份切回正式服务之前，还必须停止同步并对账删除、代次、游标和设备授权；工具不提供未经核对的一键覆盖恢复。

停止 API 用 `systemctl stop bigbluefish-sync`，不影响 SSH/Tailscale。程序回滚可将 `current` 指回前一版本并重启服务；涉及不兼容数据库迁移时，必须按已验证的备份恢复方案处理，不能仅切换二进制。

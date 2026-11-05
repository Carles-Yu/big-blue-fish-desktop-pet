# 云服务器 SSH

华为云主机目前只有公网 IPv6。Cursor 和 Codex 共用本机 SSH 配置，别名是 `bluefish`。

这份说明包含服务器地址和公钥，不含私钥。私钥不要提交，也不要贴进对话。

## 当前状态

2026-10-26 已通过 Tailscale 登录。服务器主机名是 `hcss-ecs-1b1e`，系统是 Linux 6.8.0-136-generic。本机执行 `ssh bluefish` 即可。

公网 IPv6 `2409:8c20:5021:2100:2:fa03:7e44:4a4b` 从这台电脑仍不可达。日常连接走 Tailscale 地址 `100.70.65.105`。

## 连接参数

| 项 | 值 |
| --- | --- |
| 别名 | `bluefish` |
| Tailscale 地址 | `100.70.65.105` |
| 公网地址 | `2409:8c20:5021:2100:2:fa03:7e44:4a4b`（本机没有 IPv6 出口，不使用） |
| 端口 | `22` |
| 用户 | `root` |
| 私钥 | `C:\Users\黎玊\.ssh\id_ed25519_remote` |
| 公钥注释 | `bluefatfish-remote` |

公钥：

```text
ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFnDS9JYtQUgzLdpgltaBEIptDcgShYExaAU2viEpvun bluefatfish-remote
```

这把钥匙只给这台云服务器。GitHub 仍使用 `C:\Users\黎玊\.ssh\id_ed25519`。私钥没有口令。

## 本机配置

`C:\Users\黎玊\.ssh\config`：

```sshconfig
Host bluefish
    HostName 100.70.65.105
    User root
    Port 22
    IdentityFile C:\Users\黎玊\.ssh\id_ed25519_remote
    IdentitiesOnly yes
```

之后 Cursor 和 Codex 都用：

```powershell
ssh bluefish
ssh bluefish "uname -a"
scp .\本地文件 bluefish:/root/
```

## Codex

Codex 在这台 Windows 上直接读这份说明和同一份 SSH 配置，不需要另一把钥匙。让它执行 `ssh bluefish`。不要把私钥写进仓库或对话。

Codex 若跑在别的电脑上，那台电脑也要登录同一个 Tailscale 账号，并复制 `id_ed25519_remote`、`id_ed25519_remote.pub` 和上面的 `Host bluefish`。复制私钥时走加密通道。

有公网 IPv6 的机器才可以直接连：

```powershell
ssh -6 -i $env:USERPROFILE\.ssh\id_ed25519_remote root@[2409:8c20:5021:2100:2:fa03:7e44:4a4b]
```

# 大肥鱼同步服务

这是给华为云 2 核 4 GB 云主机准备的单机部署。接口和 PostgreSQL 跑在同一台虚拟机上。数据库不映射到公网。桌宠只连接 HTTPS 接口，不需要华为云账号、AK/SK 或数据库密码。

服务器只保存密文、版本号和设备元数据。档案密钥留在设备上。没有公开注册，启动时只会准备一个主人账号。

## 启动

1. 安装 Docker 和 Caddy 所用的 80/443 端口。域名解析到这台云主机。
2. 复制 `.env.example` 为 `.env`，换掉三个口令。不要把 `.env` 提交到仓库。
3. 在本目录执行 `docker compose up -d --build`。
4. 生成一次性配对码：`docker compose exec api dotnet BigBlueFish.SyncServer.dll --pair`。把打印出来的码填进桌宠的“鱼鱼的记忆”，用过即失效。
5. 第一台设备配对后，自己生成档案密钥。用传递口令发给第二台。传递口令本身不落库。

管理接口使用 `Authorization: Bearer <SYNC_ADMIN_TOKEN>`：

- `POST /v1/admin/pairings` 生成配对码
- `POST /v1/profiles/{档案编号}/reset` 清空云端记忆并提升 epoch。旧设备的未同步写入会被拒绝，不会自动写回去。

健康检查是 `GET /health`。

## 备份

`backup.sh` 把 `pg_dump` 压紧后用口令加密，写到 `BACKUP_DIR`。如果虚拟机里已经配置好 `obsutil`，并且设置了 `OBS_BACKUP_URI`，脚本会把加密文件拷到私有桶。对象存储只做备份，桌宠不同步到那里。

也可以不配 `obsutil`，改由另一台机器拉取 `BACKUP_DIR`。两种至少留一种。

恢复前先停止 api：`docker compose stop api`，再执行 `BACKUP_PASSPHRASE=... ./restore.sh 备份文件`。恢复会覆盖库里的数据。完成后 `docker compose start api`。

云主机建议按天执行 `backup.sh`。4 GB 内存足够这个单用户接口和数据库，不要再在同一台机器上跑模型。

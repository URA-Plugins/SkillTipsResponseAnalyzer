# SkillTipsResponseAnalyzer Hachimi 原生桥接

Windows x64 Hachimi v3 插件，为 SkillTipsResponseAnalyzer 提供技能页快照、推荐选择预检及待学习选择替换。插件不执行最终学习。

## 版本

内嵌 profile：`umamusume-20261026-il2cpp-x64`，日服客户端 `2.32.0`、Unity `2022.3.62f2`。初始化校验方法名、参数数量、RVA 与机器码；不匹配时在 Hachimi 日志中报告具体方法并停止桥接。游戏更新后必须重新核验 profile。

Hachimi API v3 的主线程调度和 IL2CPP API 签名依据 [Hachimi-Edge eaf95cd](https://github.com/EtherealAO/Hachimi-Edge/blob/eaf95cd526211a02832e079b0dd26cddb67eb15e/src/core/plugin_api.rs)。`runtime_invoke_convert_args` 使用该版本 Windows 符号映射。

## 手动安装

1. 将 `skill_tips_bridge.dll` 放到游戏目录的 `hachimi` 目录。
2. 在现有 `hachimi/config.json` 的 `load_libraries` 数组中添加 `"hachimi\\skill_tips_bridge.dll"`，保留其他已安装插件条目。
3. 确认 `hachimi/httpforward.json` 中的 `notifier_host` 指向 URA 的 HTTP 地址；只接受 `localhost` 或回环 IP。关闭同时 hook 技能选择方法的参考事件插件。
4. 重启游戏，并检查 `hachimi.log` 是否记录 profile 校验通过和 WebSocket 连接成功。

该 DLL 与 URA 托管插件 ZIP 并列交付。卸载时从 `load_libraries` 移除对应条目并重启游戏后移除 DLL。

## 通信与操作

原生插件主动连接 URA 现有 HTTP 监听器的 `/SkillTipsResponseAnalyzer/bridge` WebSocket 端点。地址取自 Hachimi 基目录下 `httpforward.json` 的 `notifier_host`，保留端口与基础路径；HTTP/HTTPS 分别升级为 WS/WSS。连接不使用代理或重定向，HTTPS 证书由 Windows 校验。

协议版本为 `2`，每条 WebSocket 文本消息包含一个 UTF-8 JSON 值，完整消息最多 65536 字节，支持分片。连接后依次发送 `hello`、当前 `state`；初始快照的 `selection_event` 为 `false`。新进入技能页获得新的 `page_id`，真实选择或页面状态变化递增 `revision`。

握手期限为 5 秒；断线后每隔 1 秒重读配置并尝试建立新连接。配置缺失或无效会记录具体文件和原因，不猜测默认地址。重连仅同步新快照，已发送的命令及预检不转移到新连接。

`prepare` 校验页面版本及育成身份、回合、点数、已学技能；在主线程新建独立模型并按请求顺序选择推荐技能，返回包含游戏自动下位技能的完整目标、需移除的现有非推荐选择，以及剩余点数。临时模型使用 GC handle 保活，预检不改变真实页选择。游戏构造函数可能填充和排序共享 master 缓存。

`replace` 只接受同一连接最新的预检，执行前再次核对真实状态。状态变化或断线使待执行请求失效；没有自动重试。替换调用游戏的 `ResetSelected`、`Select`、`OnSelectUpdate`。执行中发生错误时发布真实状态，原选择可能已变化，不自动回滚。最终学习仍由用户在游戏中确认。

## 构建与验证

```powershell
pwsh -File .\build.ps1
```

在 `native/` 中执行上述命令。脚本构建 `target/x86_64-pc-windows-msvc/release/skill_tips_bridge.dll`，并验证 PE x64 与 `hachimi_init_v3` 导出。脚本不部署或启动游戏。

仓库根目录的 `package.ps1` 直接在 `artifacts/` 输出 `SkillTipsResponseAnalyzer.zip`、相邻的 `skill_tips_bridge.dll` 和 `SHA256SUMS.txt`，覆盖同名构建产物。游戏若直接加载此目录中的 DLL，须先退出游戏。本地 SDK 构建可通过脚本参数 `-RestoreConfigFile`、`-RestorePackagesPath` 指定 NuGet 配置与包目录。

游戏构造、依赖展开、UI 刷新与主线程生命周期需要实机验收。验收应比较预检前后真实选择不变、预检结果与替换结果一致，并覆盖切页、改变选择、切换育成和确认期间断线。

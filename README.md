# SkillTipsResponseAnalyzer

育成角色进入结束状态且没有未处理事件时，计算技能评分方案并在 `SkillTipsResponseAnalyzer` workspace 中显示结果。single-mode `finish` 和插件的 `DisposeAsync` 都会移除该 workspace。

## 排序设置

插件设置中的「默认排序」「按性价比排序」决定后续结果的显示顺序，保存后从下一次结果生效。默认排序按游戏内顺序排列；性价比按显示评分 / 所需技能点降序排列，同值保持默认顺序，不改变推荐技能或评分计算。

workspace 中的排序切换只影响当前结果，不写入配置；下一次结果使用已保存的默认值。配置保存在 `PluginData/SkillTipsResponseAnalyzer/settings.json`，格式为 `{ "sortByCostEffectiveness": false }`。文件缺失时采用默认排序；无效配置会明确报错。设置对话框只有「保存」会写入，取消、Esc 和关闭均保留原值。

## Hachimi 技能选择

游戏侧桥接面向 Windows x64 日服及 Hachimi v3，使用 `umamusume-20261026-il2cpp-x64` profile。宿主必须包含 `WebSocketRoute`，对应 SDK 版本为 `2026.10.30`。将托管插件 `SkillTipsResponseAnalyzer.zip` 放入 URA 数据目录的 `Plugins/` 后重启 URA；默认数据目录为 `%LOCALAPPDATA%/UmamusumeResponseAnalyzer`。原生 DLL 独立安装，见 [原生桥接说明](native/README.md)。

原生 DLL 读取 `hachimi/httpforward.json` 的 `notifier_host`，在其基础路径后追加 `/SkillTipsResponseAnalyzer/bridge`，通过 URA 现有监听端口建立 WebSocket。仅允许同一台电脑上的原生客户端；配置缺失、地址无效、协议或游戏方法不匹配时明确报错。连接中断后清除高亮及待处理操作，重连重新读取配置并同步快照，不重放选择命令。

进入游戏技能学习页面后，推荐表高亮游戏内的待学习选择。当前可见技能从顶部连续选中达到 75% 时，推荐表向下滚动对应行数；不足整数行时向上取整。初始同步、排序和手动滚动不触发自动翻页。

在本插件 workspace 直接连续键入小写 `thisisunsafe`，可显示「选择全部推荐技能」按钮，不要求焦点在推荐表内。命令模式和其他文本输入框中的输入不参与解锁。解锁仅属于当前推荐结果，workspace 往返保留；新结果、视图销毁或重启后清除，不保存到配置。

按钮先用游戏的独立临时模型校验目标技能与点数，再替换游戏中的待学习选择。当前含有推荐范围之外的待学习技能时，会列出额外技能并要求确认；取消不改变选择。金技能所需的下位技能按游戏规则纳入目标集合。页面、育成或当前状态变化后必须重新操作。

此功能只选择待学习技能，不执行游戏的最终学习。操作失败会显示原因并同步实际选择，不自动重试。临时模型会使用游戏共享的 master 查询缓存，但不修改真实页面的待学习集合。

## 构建

```powershell
dotnet build .\SkillTipsResponseAnalyzer.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false
```

Host-dependent smoke 位于 `tests/SkillTipsResponseAnalyzerSmoke`。

运行 `pwsh -File .\package.ps1` 构建 Release 产物，直接在 `artifacts/` 输出 `SkillTipsResponseAnalyzer.zip`、相邻的 `skill_tips_bridge.dll` 和 `SHA256SUMS.txt`，覆盖同名构建产物。原生构建需要 Rust 的 `x86_64-pc-windows-msvc` 工具链及 Visual Studio C++ Build Tools。如果游戏直接加载该目录中的 DLL，须先退出游戏。该脚本不修改安装目录或 Hachimi 配置。

验证尚未发布的宿主 SDK 时，先从匹配的宿主源码生成本地 NuGet 包，通过临时 NuGet 配置精确映射 `UmamusumeResponseAnalyzer` 包来源，并使用独立缓存。将同一组 `-RestoreConfigFile <配置路径> -RestorePackagesPath <缓存目录>` 传给打包脚本；smoke 构建使用对应的 MSBuild 属性和 `UraTestHostRoot`。生产插件保留 NuGet 引用，不引用宿主项目。

默认 smoke 包含本仓发布边界、闲置生命周期、技能名的字面文本显示、警告数量、推荐 workspace 释放及配置、排序、鼠标和 WebSocket 检查。测试工具源码由 `tests/Support/` 持有。

## 验证与发布

在 Windows 仓库根执行 `act workflow_dispatch --artifact-server-path "$env:TEMP/ura-act-artifacts"`。本地与 GitHub 使用同一份 workflow；托管构建、Host 验包与 smoke 由共用 workflow 执行，原生 job 执行 `native/build.ps1` 的 locked Cargo 测试、Release 构建及 x64 PE / `hachimi_init_v3` 导出检查。环境要求和共用 workflow 本地映射见 [URA plugin workflows](https://github.com/URA-Plugins/.github/blob/927fa76bae243fc22cc6fa4e09a0e4754f096abb/README.md)。

版本 tag 必须匹配插件 manifest 版本。两组验证都通过后，同一个 GitHub Release 发布 `SkillTipsResponseAnalyzer.zip`、`skill_tips_bridge.dll` 和包含两者 SHA256 的 `SHA256SUMS.txt`；原生 DLL 不放入 Host 插件 ZIP。下载后可用 `Get-FileHash -Algorithm SHA256 <文件路径>` 与校验文件对照。

# SkillTipsResponseAnalyzer

育成角色进入结束状态且没有未处理事件时，计算技能评分方案并在 `SkillTipsResponseAnalyzer` workspace 中显示结果。single-mode `finish` 和插件的 `DisposeAsync` 都会移除该 workspace。

预测等级在 Windows 且终端支持 Kitty 或 Sixel 图形协议时显示为图片，否则显示为文字。

## 计算与数据

OR-Tools CP-SAT 同时选择购买、学习解锁和角色／剧本／继承进化，最大化最终预计评价；同分时最少花费技能点。输入已有技能、学习后持有技能和进化后技能分别计算。剧本名额来自主数据，同一基础技能的进化互斥；继承进化按游戏规则自动完成。

Host 的 `skill_planning.br` 提供静态定义。条件以服务器下发的 `current_count / total_count` 为基线，按主数据定义增减学习后的持有计数；同组上下位只计最终持有项。比赛、事件等进度使用服务器结果。缺失进度按游戏行为判定为未达成；无法解释且影响候选资格的学习条件明确报错。

技能费用使用 Host `SkillCatalog`：勤勉好学（效果 `40`）减 4%，切者（效果 `7`）减 10%，同组取切者；与 Hint 折扣相加后逐技能向下取整，累计未学习的前置和紫技解除费用。属性不足及不可解除紫技会限制候选。

候选来自技能提示、觉醒技能、已持有技能和已解除技能；普通技能展开同组白技升级链，金技须有对应解锁来源。没有提示也能计算已持有白技升级及紫技解除后的正向购买。

评分按 master 的脚质、距离标签分别取最高适性，乘完两类倍率后每个技能取整一次。最优性仅针对当前规则和预计评分模型；1199 个独立历史状态中，1152 个完全吻合，47 个预计总分高 1 分，差异集中在 2000 以上属性估算段。属性表保持原值，未加补偿；界面直接使用求解结果中的费用、评分和进化选择。

## 排序设置

插件设置中的「默认排序」「按性价比排序」决定后续结果的显示顺序，保存后从下一次结果生效。默认排序按游戏内顺序排列；性价比按显示评分 / 所需技能点降序排列，同值保持默认顺序，不改变推荐技能或评分计算。

workspace 中的排序切换只影响当前结果，不写入配置；下一次结果使用已保存的默认值。配置保存在 `PluginData/SkillTipsResponseAnalyzer/settings.json`，格式为 `{ "sortByCostEffectiveness": false }`。文件缺失时采用默认排序；无效配置会明确报错。设置对话框只有「保存」会写入，取消、Esc 和关闭均保留原值。

## Hachimi 技能选择

游戏侧桥接面向 Windows x64 日服及 Hachimi v3，使用 `umamusume-20261026-il2cpp-x64` profile。本版本要求包含单次取整评分的 Host 及配套 `skill_planning.br`；本地验证使用 SDK `2026.11.1.2`。将 `SkillTipsResponseAnalyzer.zip` 放入 URA 数据目录的 `Plugins/` 后重启 URA；默认数据目录为 `%LOCALAPPDATA%/UmamusumeResponseAnalyzer`。Hachimi 桥接 DLL 独立安装，见 [原生桥接说明](native/README.md)。

OR-Tools 的 NuGet 原生依赖随 ZIP 提供；系统须安装 [Microsoft Visual C++ x64 运行库](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist/)，这是 [OR-Tools Windows 运行要求](https://developers.google.com/optimization/install/dotnet/binary_windows)。缺失时会报告原生组件加载失败。

原生 DLL 读取 `hachimi/httpforward.json` 的 `notifier_host`，在其基础路径后追加 `/SkillTipsResponseAnalyzer/bridge`，通过 URA 现有监听端口建立 WebSocket。仅允许同一台电脑上的原生客户端；配置缺失、地址无效、协议或游戏方法不匹配时明确报错。连接中断后清除高亮及待处理操作，重连重新读取配置并同步快照，不重放选择命令。

进入游戏技能学习页面后，推荐表高亮游戏内的待学习选择。当前可见技能从顶部连续选中达到 75% 时，推荐表向下滚动对应行数；不足整数行时向上取整。初始同步、排序和手动滚动不触发自动翻页。

在本插件 workspace 直接连续键入小写 `thisisunsafe`，可显示「选择全部推荐技能」按钮，不要求焦点在推荐表内。命令模式和其他文本输入框中的输入不参与解锁。解锁仅属于当前推荐结果，workspace 往返保留；新结果、视图销毁或重启后清除，不保存到配置。

按钮先用游戏的独立临时模型校验完整学习步骤（含白技前置和紫技解除）。预检剩余点数必须与求解结果一致，才允许替换游戏中的待学习选择。当前含有推荐范围之外的待学习技能时，会列出额外技能并要求确认；取消不改变选择。页面、育成或当前状态变化后必须重新操作。

此功能只选择待学习技能，不执行游戏的最终学习。操作失败会显示原因并同步实际选择，不自动重试。临时模型会使用游戏共享的 master 查询缓存，但不修改真实页面的待学习集合。

箭头标注角色／剧本进化的选择；继承进化标为自动进化。插件不操作进化界面，也不监听手动学习后的刷新。

## 构建

```powershell
dotnet build .\SkillTipsResponseAnalyzer.csproj -c Release -m:1 -p:RuntimeIdentifier=win-x64 -p:SelfContained=false -p:PlatformTarget=AnyCPU -p:GenerateUraPluginManifestOnBuild=false -p:PackageUraPluginOnBuild=false -p:DeployUraPluginToLocalAppDataOnBuild=false
```

运行 `pwsh -File .\package.ps1` 构建 Release 产物，直接在 `artifacts/` 输出 `SkillTipsResponseAnalyzer.zip`、相邻的 `skill_tips_bridge.dll` 和 `SHA256SUMS.txt`，覆盖同名构建产物。原生构建需要 Rust 的 `x86_64-pc-windows-msvc` 工具链及 Visual Studio C++ Build Tools。如果游戏直接加载该目录中的 DLL，须先退出游戏。该脚本不修改安装目录或 Hachimi 配置。

验证尚未发布的宿主 SDK 时，先从匹配的宿主源码生成本地 NuGet 包，通过临时 NuGet 配置精确映射 `UmamusumeResponseAnalyzer` 包来源，并使用独立缓存。将同一组 `-RestoreConfigFile <配置路径> -RestorePackagesPath <缓存目录>` 传给打包脚本。生产插件保留 NuGet 引用，不引用宿主项目。

## 验证与发布

在 Windows 仓库根执行 `act workflow_dispatch --artifact-server-path "$env:TEMP/ura-act-artifacts"`。本地与 GitHub 使用同一份 workflow；托管构建与 Host 验包由共用 workflow 执行，原生 job 执行 `native/build.ps1` 的 locked Cargo Release 构建及 x64 PE / `hachimi_init_v3` 导出检查。环境要求和共用 workflow 本地映射见 [URA plugin workflows](https://github.com/URA-Plugins/.github/blob/927fa76bae243fc22cc6fa4e09a0e4754f096abb/README.md)。

版本 tag 必须匹配插件 manifest 版本。两组验证都通过后，同一个 GitHub Release 发布 `SkillTipsResponseAnalyzer.zip`、`skill_tips_bridge.dll` 和包含两者 SHA256 的 `SHA256SUMS.txt`。OR-Tools 原生依赖位于 ZIP 的 `runtimes/win-x64/native/`；Hachimi 桥接 DLL 单独发布。下载后可用 `Get-FileHash -Algorithm SHA256 <文件路径>` 与校验文件对照。

离线验证入口、冻结样本及规则证据见 [重写验证记录](docs/skilltips-rewrite.md)。发布顺序为主数据、Host 运行时／SDK、插件及受影响调用方；单独安装本插件不足以完成升级。

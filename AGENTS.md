# AV Collection · 项目交接文档

> 这份文档写给**接手继续开发的人或 AI**。读完这一篇就能直接改代码，不需要翻聊天记录。
> 最后更新：2026-11-03，当前版本 **1.0.0.0**。

---

## 1. 这是什么

一个 **Jellyfin 12.2 插件**：按演员自动维护合集。

前身是服务器上的一个 Python 脚本 `/usr/local/bin/jellyfin-actor-collections.py`
（systemd timer 每 6 小时跑一次，状态文件 `/var/lib/jellyfin-actor-collections.json`）。
插件在脚本的两个能力之上，新增了**艺名合并**。

- 仓库：<https://github.com/Louis-15/Jellyfin-AV-Collection>
- 插件仓库地址（Jellyfin 内添加，支持在线更新）：
  `https://raw.githubusercontent.com/Louis-15/Jellyfin-AV-Collection/main/manifest.json`

---

## 2. 30 秒上手

```powershell
# 编译
dotnet build .\Jellyfin.Plugin.AVCollection\Jellyfin.Plugin.AVCollection.csproj -c Release

# 跑测试
dotnet test .\tests\Jellyfin.Plugin.AVCollection.Tests\Jellyfin.Plugin.AVCollection.Tests.csproj -c Release

# 打包（artifacts/av-collection_<version>.zip）
.\build.ps1

# 发版（自动改版本号 + commit + 打 tag + push，CI 编译/打包/写 manifest/发 Release）
.\release.ps1 -Version 1.0.1.0 -Changelog "改了什么"
```

> SDK 装在 `C:\Users\aaa\dotnet-sdk`（10.0.403），**不在 PATH 里**。若 `dotnet` 报
> “兼容的 .NET SDK 未找到”，用全路径 `C:\Users\aaa\dotnet-sdk\dotnet.exe`。
> `global.json` 固定 `10.0.400` + `rollForward: latestFeature`。

> ⚠️ **`build.ps1` / `release.ps1` / `scripts\update-manifest.ps1` 必须保存成 UTF-8 with BOM**。
> Windows PowerShell 5.1 会把无 BOM 的 UTF-8 当 ANSI(GBK) 解析，中文注释里的全角标点会
> 吃掉引号，脚本直接语法错误。

---

## 3. 技术栈

| 维度 | 选择 |
|---|---|
| 目标平台 | Jellyfin **12.2**（`meta.json` 里 `targetAbi = 12.2.0.0`） |
| 运行时 | `net10.0` |
| 依赖 | `Jellyfin.Controller 12.2.0`（`ExcludeAssets=runtime`）+ `FrameworkReference Microsoft.AspNetCore.App` |
| 合集 API | `ICollectionManager`（`CreateCollectionAsync` / `AddToCollectionAsync` / `RemoveFromCollectionAsync`） |
| 演员 API | `IPeopleRepository.GetPeopleByItems`（**Jellyfin 12 的 `BaseItem` 没有 `People` 属性**） |
| 封面 API | `IProviderManager.SaveImage` + `BaseItem.DeleteImageAsync(type, index)` |
| 配置 | `BasePluginConfiguration`（XML 序列化，**不能用 Dictionary**） |
| 运行期状态 | `<data>/av-collection/state.json` |
| 插件页 | 单文件 HTML + ES5 JS，走 `IHasWebPages` |

---

## 4. 目录结构

```
Jellyfin.Plugin.AVCollection/
├── Plugin.cs                       插件入口（IHasWebPages 只返回 1 个页面）
├── PluginConfiguration.cs          配置模型 + ActorAliasGroup + AiOptions
├── PluginServiceRegistrator.cs     DI 注册（定时任务必须在这里注册）
├── Api/
│   └── CollectionController.cs     设置页用的 REST API
├── Core/
│   ├── ActorInfo.cs                演员 / 合集计划的数据模型
│   ├── ActorIndex.cs               扫描库 → 演员映射（批量查 People）
│   ├── CollectionPlanner.cs        ★纯逻辑：分组、命名、封面艺名（有单测覆盖）
│   ├── CollectionWriter.cs         单个合集的写操作（认领/建/改名/成员/封面/清元数据）
│   ├── CollectionSyncJob.cs        一次同步的完整流程 + 校验轮
│   ├── RuntimeState.cs             groupId → collectionId 持久化
│   ├── AiAliasAdvisor.cs           OpenAI 兼容接口的艺名建议
│   └── PluginLog.cs                日志前缀包装
├── Tasks/
│   └── ActorCollectionSyncTask.cs  IScheduledTask（默认 6 小时）
└── Web/
    └── configPage.html             设置页（<style> 在 <body> 里！）
```

---

## 5. 核心算法

### 5.1 分组键（最重要）

```
合并组   → group:{ActorAliasGroup.Id}
独立演员 → actor:{演员名}
```

`RuntimeState` 按这个键记合集 Id。**绝对不能用合集名当键**：
合并组的名字按作品数动态拼（`小野夕子 / 葵` ←→ `葵 / 小野夕子`），
演员作品数一变名字就变，靠名字找合集就会每次新建一个，旧合集变孤儿。

### 5.2 合集命名

```
手动 DisplayName 非空 → 用它
否则 → 组内"实际有作品"的艺名按 (作品数降序, 名字 ordinal) 排序，取前 MaxAliasesInName 个
       用 " / " 拼接
```

排序里加 `ThenBy(Name, Ordinal)` 是为了**稳定性**：作品数相同时顺序必须固定，
否则合集名会在两次运行之间来回跳。

### 5.3 目标合集认领顺序

1. `state.json` 里记录过、且现在仍然存在 → 用（名字变了也不丢）
2. 名字精确等于目标名 → 用
3. 组内某个艺名的旧合集（名字 == 艺名）→ 用（接管 Python 脚本建的那批）
4. 都没有 → `ICollectionManager.CreateCollectionAsync`

### 5.4 合并时的旧合集处理

`plan.IsMergeGroup == true` 且组内艺名 ≥ 2 时，把其余艺名各自的旧合集加入 `absorbees`，
同步结束后按 `MergeRemoveOldCollections` 决定是否 `ILibraryManager.DeleteItem`。

> **`IsMergeGroup` 必须要求 `ranked.Count >= 2`**。
> 用户可能把同一个艺名配进两个组，先到先得后另一个组只剩一个艺名；
> 若仍标记为合并组，会去删那个演员原本正常的合集。这个 bug 是单测抓出来的。

### 5.5 下限保护

`MinActorCount`（默认 3）：扫到的演员少于这个数就直接中止。
媒体库扫描期间 `People` 会整体返回空，没有这道闸，一次"扫到 0 个演员"就会清空所有合集。

### 5.6 校验轮

建合集与加成员之间有时间缝，Jellyfin 自带的 `CollectionPostScanTask` 会清理"空合集"。
`VerifyRounds × VerifyDelaySeconds` 之后重新核对成员，缺的补一次。

---

## 6. 已知的坑（都是真踩过的）

1. **`<style>` 必须写在 `<body>` 里**：Jellyfin 只搬 `<body>` 子节点进 SPA，
   `<head>` 里的 `<style>` 会被整块丢弃。现象是"结构对、样式全丢"。
   判断方法：看 `legend` / 分组标题有没有变粗，没变粗就是 CSS 没加载。
   （Thunder Subtitle 1.8.3.1 踩过一模一样的坑。）
2. **Jellyfin 12 的 `BaseItem` 没有 `People`**：要用 `IPeopleRepository.GetPeopleByItems`。
   逐条 `GetPeople` 会变成 N+1，大库上慢到没法用。
3. **`CollapseBoxSetItems = false` 不能省**：折叠后"已进合集"的影片会从查询结果消失，
   第二次运行就以为演员没作品，把合集清空或删掉。
4. **`MimeTypes` 工具类在 12.2 里找不到**：`CollectionWriter.GuessMimeType` 自己按扩展名判断。
5. **`IProviderManager` 只有 `SaveImage`，没有 `DeleteImage`**：删图用
   `BaseItem.DeleteImageAsync(ImageType, int imageIndex)`（2 个参数）。
6. **`InternalItemsQuery` 不能用 `SearchTerm` 认领合集**：演员名常有一两个字相同
   （葵 / 小野夕子），模糊匹配会认领错。`LoadCollectionsByName` 一次性全量拉回来按名字精确匹配。
7. **PowerShell 脚本必须带 BOM**（见 §2）。
8. **不要把 Jellyfin 程序集拷到插件目录**：`CopyLocalLockFileAssemblies=false` 不能改，
   否则同一类型会有两个身份，DI 静默匹配失败。

---

## 7. 与 Python 脚本的关系（迁移状态）

Python 脚本仍在服务器上按 timer 运行，**插件还没有接管**。
用户明确表示"先编好插件，脚本怎么办我再告诉你"。

两边的差异：

| | Python 脚本 | 插件 |
|---|---|---|
| 单元 | 一个演员名 = 一个合集 | 合并组 = 一个合集 |
| 状态 | `/var/lib/jellyfin-actor-collections.json`（演员名 → 合集 Id） | `<data>/av-collection/state.json`（分组键 → 合集 Id） |
| 运行位置 | 宿主机 cron + REST API | Jellyfin 进程内 + 服务 API |
| 艺名合并 | 无 | 有 |

**接管时的注意点**：
- 插件按"名字 == 艺名"能认领脚本建的所有合集，不会重复建。
- 脚本的状态文件在宿主机 `/var/lib`，插件在容器 `/config`，两者**不共享**。
  插件第一次跑会自己按名字重新认领，不需要导入。
- 如果先合并了「小野夕子」和「葵」，脚本下一次跑会因为找不到名为「小野夕子」的合集
  （已被改名/删除）而**重新建一个**，导致重复。所以**合并功能上线前必须先停掉脚本**。

---

## 8. 测试

```powershell
dotnet test .\tests\Jellyfin.Plugin.AVCollection.Tests\...\csproj -c Release
```

当前 15 个用例，全部集中在 `CollectionPlanner`（纯逻辑，无 Jellyfin 依赖）：
单个演员命名、合并组取前 N 个艺名、作品数相同时排序稳定、手动名优先、
无作品演员不建合集、合并组里失效艺名不参与命名、封面艺名回落、
`ParentLibraryId` 取影片最多的库、失效组合识别、退化组合不算合并组。

**改命名 / 分组 / 封面规则时先加断言再改实现**：这一层出错线上表现是
"合集名来回跳"或"两个合集抢同一批影片"，排查成本极高。

---

## 9. 发版与部署

```
改代码 → dotnet test → .\release.ps1 -Version x.y.z.w -Changelog "..."
        → CI: test → build.ps1 打包 → MD5 写进 manifest.json → 提交 manifest → 发 Release
        → 在服务器上装新版本 → 重启 → 验证
```

**装到服务器**（SSH 免密可用，不需要 Jellyfin API Key）：

```powershell
scp -P 2222 .\artifacts\av-collection_<version>.zip root@931002.xyz:/tmp/
# 服务器上：解压到 /www/jellyfin/config/plugins/AV Collection_<version>/ 后
ssh -p 2222 root@931002.xyz "docker restart jellyfin"
```

**验证**：

```powershell
# 插件是否加载
ssh -p 2222 root@931002.xyz "docker logs --since 10m jellyfin 2>&1 | grep -a 'AV Collection'"

# 配置页能否取到（应为 200，且含 av-group-title）
curl.exe -sk "https://931002.xyz:8443/web/ConfigurationPage?name=avcollection"
```

服务器环境要点：

- Jellyfin 12.2，docker 容器名 `jellyfin`，插件目录 `/config/plugins/<name>_<version>/`。
- 媒体在宿主机 `/opt/litepan/strm/日本大姐姐/`，容器内 `/media/strm/日本大姐姐/`。
- 前面有 **Caddy** 反代；Caddy 会自己做 gzip，排查时要么不带 `Accept-Encoding`，要么先解压。

---

## 10. 待办 / 后续可做

1. **迁移 Python 脚本**（等用户拍板，见 §7）。合并功能上线前必须先停脚本。
2. 演员列表缓存：现在设置页每次打开都重新扫库，库很大时会慢。
3. 合并组支持"注解"字段，记录为什么把这两个艺名并在一起。
4. 支持从合集反向操作（按合集名批量补人），方便手工纠错。

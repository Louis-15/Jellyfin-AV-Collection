# AV Collection · 项目交接文档

> 这份文档写给**接手继续开发的人或 AI**。读完这一篇就能直接改代码，不需要翻聊天记录。
> 最后更新：2026-11-04，当前版本 **1.0.1.2**。

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

另外**未配置 `LibraryIds` 时直接跳过**（不发散到"所有电影库"）。
早期版本有过这个回退，结果一次预览就要给「电影」库的 431 位演员各建一个合集（Dry-run 实测），
属于灾难级默认值，已移除。

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
9. **`ApiClient.ajax` 对 GET 不返回解析后的 JSON**：它返回 `Response` 对象，
   直接当数组用会 `libraries.length === undefined`，页面永远显示"没有找到媒体库"。
   GET 一律走 `ApiClient.getJSON`；写操作走 `ApiClient.ajax` 并自己判断
   `result.json` 是否存在。**这个 bug 只在真实 Jellyfin 里出现，本地 harness 用 mock 复现不了**，
   所以改前端时 mock 必须模拟"ajax 返回 Response、getJSON 返回对象"。
10. **`ApiClient.ajax` 失败时 throw 的是 `Response`**，没有 `responseText`；
    错误处理要按 `error.status` / `error.json()` 读。
11. **`style.display = ''` 不会把元素显示出来**：`.av-status` 在样式表里是 `display:none`，
    置空字符串只是"去掉内联样式"，于是又回落到样式表 → 仍然隐藏。必须显式写 `'block'`。
    （这个 bug 让"正在同步/同步完成"的提示一直看不见，是测试里量 `getComputedStyle` 才发现的。）
12. **改配置 ≠ 生效**：合并组保存后必须跑一次同步才会真正改合集。
    所以「艺名合并」区域也放了一个「立即同步」按钮，`setStatus` 会同时写两处状态框。
13. **`IPeopleRepository.GetPeopleByItems` 返回的 `PersonInfo.Id` 不是 Person 条目 Id**（这是最坑的一个）：    它是另一个 GUID，`_libraryManager.GetItemById(personInfo.Id)` 永远是 null。
    症状：**每位演员都被判定"没有头像"，合集封面永远设不上**，日志里一片
    「「XXX」没有头像，跳过封面」。
    正确做法：扫描时用 `_libraryManager.GetItemList(new InternalItemsQuery {
    Recursive = true, IncludeItemTypes = [BaseItemKind.Person] })` **一次性按名字**建索引，
    再回填到 `ActorInfo.PersonId`（见 `ActorIndex.ResolvePersonIds`）。
    旧 Python 脚本没这个问题，因为它用的是 REST `/Items?fields=People` 里的 DTO `Id`，
    那个才是真实的 Person 条目 Id。

    排查手法：拿插件返回的 `PersonId` 去 `/Items/{id}/Images/Primary`，
    如果 404 但 `/Persons/{name}` 能查到同一个演员且有图像，就说明 id 用错了。
14. **改代码里的默认值救不了已存在的配置文件**：
    Jellyfin 把插件配置存成 XML，**文件里的旧值会覆盖代码里的新默认值**。
    把 `AiOptions.TimeoutSeconds` 默认从 60 改成 300 后，服务器上仍然读到 60，
    「AI 智能合并」继续超时 —— 必须在 `Plugin` 构造函数里做一次性迁移
    （靠 `PluginConfiguration.ConfigVersion` 判断，见 `Plugin.MigrateConfiguration`）。
    任何"改默认值"的发版都要想一遍这件事。
15. **不要用 `is="emby-checkbox"` 构造动态复选行**：
    - `emby-checkbox` 是自定义元素，靠 `attachedCallback` 往 label 末尾插
      `<span class="checkboxOutline">`。JS 创建、随后才挂进文档的行容易漏掉升级。
    - 对勾的显隐依赖 `.emby-checkbox:checked + span + .checkboxOutline`，
      要求 span 是 input 的**紧邻兄弟**、outline **紧跟其后**。label 里多出作品数
      这类元素就永远匹配不上 → `input.checked` 变成 true 了，但对勾不显示。
      这就是 1.0.0.4 里"点艺名复选框没反应"的真正原因。
    - 结论：动态列表一律用自己画的 `.av-box`（`createCheckRow`），
      状态由 `row.is-checked` + `aria-checked` 驱动。静态 HTML 里的几个开关
      仍可以用 `emby-checkbox`（它们在解析时就升级，且结构是 input→span→outline）。

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

## 8. AI 接入

`AiOptions`（`PluginConfiguration.cs`）只有两个供应商：

| 供应商 | 地址 | 请求头 |
|---|---|---|
| `OpenCode`（默认，内置） | 代码固定 `https://opencode.ai/inference/go/openai/v1` | 自动附带 `x-opencode-session` |
| `Custom` | 用户填（`BaseUrl`，不含 `/chat/completions`） | 不加 |

**地址和请求头都由代码决定，不做成用户可改的开关**（见 `AiOptions.ResolveBaseUrl()`）。
理由：用户选了 Open Code 却把地址改坏，报错只会是"连接失败"，极难排查。

### 超时（重要）

实测数据（Open Code 网关 + `deepseek-v4.1-flash`）：

| 请求 | 耗时 |
|---|---|
| `Ai/Test`（"只回复两个字：可以"，`max_tokens=64`） | **1.3-1.9 秒** |
| `Suggest`（21 位演员，不限制 `max_tokens`） | **107-149 秒** |

所以默认超时是 **300 秒**，60 秒必然超时。推理模型会先把预算花在 reasoning 上：
`max_tokens=16` 时 `content` 直接返回空串，64 才有正文。

超时必须写日志（早期版本静默返回，服务器上完全查不到）。

### 测试连接

`POST /AVCollection/Ai/Test` 用极短请求验证三件事：地址通、Key 有效、模型名存在。
返回 `{ok, message, endpoint, model, elapsedMs, reply}`，设置页单独显示在 AI 分组里
（`setStatus` 会跳过 `#AiTestStatus`，避免被合并/同步的结果覆盖）。

`AiAliasAdvisor` 只产出**建议**，永远不自动写配置 —— 演员同名判定出错会把不相干的作品
并进一个合集，还带着别人的头像，必须由用户在设置页点「采纳」。

设置页的「AI 智能合并」按钮在「艺名合并」组里，`POST /AVCollection/Suggest` 只返回建议列表。

---

## 9. 测试

```powershell
dotnet test .\tests\Jellyfin.Plugin.AVCollection.Tests\...\csproj -c Release
```

当前 20 个用例，集中在两块纯逻辑（无 Jellyfin 依赖）：

- `CollectionPlanner`：单个演员命名、合并组取前 N 个艺名、作品数相同时排序稳定、
  手动名优先、无作品演员不建合集、合并组里失效艺名不参与命名、封面艺名回落、
  `ParentLibraryId` 取影片最多的库、失效组合识别、退化组合不算合并组。
- `AiOptions`：内置 Open Code 必须用代码里固定的地址（忽略配置里残留的旧值）、
  自定义供应商用用户填的地址并 trim、空地址返回空串、供应商名大小写不敏感。

**改命名 / 分组 / 封面规则时先加断言再改实现**：这一层出错线上表现是
"合集名来回跳"或"两个合集抢同一批影片"，排查成本极高。

---

## 10. 发版与部署

```
改代码 → dotnet test → .\release.ps1 -Version x.y.z.w -Changelog "..."
        → CI: test → build.ps1 打包 → MD5 写进 manifest.json → 提交 manifest → 发 Release
        → 在服务器上装新版本 → 重启 → 验证
```

### 版本时间线

| 版本 | 内容 |
|---|---|
| 1.0.0.0 | 首发：建合集 / 封面 / 艺名合并 / AI 建议 |
| 1.0.0.1 | 修 DI：`PluginLog` 注入非泛型 `ILogger` 导致定时任务构造失败 |
| 1.0.0.2 | 安全修复：未配置媒体库时不再回退"所有电影库"（实测会建 431 个合集） |
| 1.0.0.3 | 文案微调（"取最活跃的艺名"、"· 已在合并组"） |
| **1.0.0.4** | 修设置页"没有找到媒体库"：`ApiClient.ajax` 对 GET 不返回解析后的 JSON，改用 `getJSON`；GUID 大小写/连字符归一化 |
| **1.0.0.5** | 修「艺名合并」勾选框点了没反应：动态行不再用 `is="emby-checkbox"`（对勾依赖紧邻兄弟选择器，多一个元素就匹配不上），改为自己画的 `.av-box` + `is-checked` 驱动 |
| **1.0.0.6** | 「艺名合并」区域新增「立即同步」按钮（合并只改配置，合集要同步才变）；顺手修状态框永远不显示（`style.display = ''` 会退回 CSS 的 `display:none`，必须显式给 `block`） |
| **1.0.0.7** | **修合集封面永远设不上**：`IPeopleRepository.GetPeopleByItems` 返回的 `PersonInfo.Id` 不是 Person 条目 Id，`GetItemById` 永远 null → 全体演员被判"没头像"。改为扫描时按名字解析真实 Person 条目 |
| **1.0.0.8** | 封面取图补上"远程头像"分支：`ImageInfo.Path` 是 http(s) 时走 `HttpClient` 下载（等价于旧脚本一律走 REST 取图的做法），本地文件仍直接读盘 |
| **1.0.0.9** | AI 设置独立成组并内置供应商（Open Code / 自定义）；Open Code 的地址与 `x-opencode-session` 头由代码固定，去掉"附带请求头"开关；「艺名合并」按钮行重排为 重新扫描 → 合并 → AI 智能合并 → 预览 → 立即同步 |
| **1.0.1.0** | AI 设置新增「测试连接」；默认超时 60→300 秒；超时改为写日志；session id 改固定值；HTTP 错误码翻译成中文 |
| **1.0.1.1** | 测试连接的 `max_tokens` 16→64（16 会被 reasoning 吃光，`content` 返回空串） |
| **1.0.1.2** | 修「升级后仍然超时」：新增 `ConfigVersion` 一次性迁移，把配置文件里的旧 60 秒提到 300 秒 |
| **1.0.2.0** | **删掉「AI 智能合并」**（判定质量差、数据源拿不到）；AI 改为把演员名翻译成中文名，用中文名命名合集。新增 `AiClient` / `TranslationStore` / `AiNameTranslator`，译名缓存到 `<data>/av-collection/translations.json`，设置页可逐条手改（失焦即保存） |
| **1.0.2.1** | 设置页按功能重新分区：基本设置 / 目标影视库 / **AI 接口** / **合集命名** / **艺名合并** / **其他** / **手动运行** / 关于；去掉「打开计划任务」；「重新扫描演员 / 预览 / 立即同步」三个按钮集中到「手动运行」 |
| **1.0.2.2** | 「重新扫描演员」加结果提示（按钮在「手动运行」、名单在「艺名合并」，中间隔一个分区，不给反馈会像按钮坏了） |

### 仓库推送注意

`gh` 的 OAuth token 只有 `repo` scope，**没有 `workflow` scope**，
直接 `git push` 包含 `.github/workflows/` 的提交会被拒：

```
refusing to allow an OAuth App to create or update workflow ... without `workflow` scope
```

解决办法：这个仓库已配置用 SSH key 推送（SSH 不受 OAuth scope 限制），
`git config core.sshCommand` 指向 `C:/Users/aaa/.ssh/id_ed25519_github_louis15`。
新建克隆时记得重新设置，否则会退回 https 并再次被拒。

**装到服务器**（SSH 免密可用，不需要 Jellyfin API Key）：

```powershell
# release.ps1 本地不产出 zip（编译在 CI 里做），装到服务器要从 Release 下载
curl.exe -sL -o "$env:TEMP\av-collection_<version>.zip" `
  "https://github.com/Louis-15/Jellyfin-AV-Collection/releases/download/v<version>/av-collection_<version>.zip"
scp -P 2222 "$env:TEMP\av-collection_<version>.zip" root@931002.xyz:/tmp/
# 服务器上：解压到 /www/jellyfin/config/plugins/AV Collection_<version>/ 后
ssh -p 2222 root@931002.xyz "docker restart jellyfin"
```

注意：`scp` / `ssh` 的命令串里不要用 PowerShell 的 here-string（`@'...'@`）拼多行脚本，
CRLF 会带进 bash，`ls "AV Collection_1.0.2.1"` 会变成 `'AV Collection_1.0.2.1'$'\r'` 报找不到。
要么单行传，要么先 `-replace "\`r", ""`。

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

## 11. 待办 / 后续可做

1. **迁移 Python 脚本**（等用户拍板，见 §7）。合并功能上线前必须先停脚本。
2. 演员列表缓存：现在设置页每次打开都重新扫库，库很大时会慢。
3. 合并组支持"注解"字段，记录为什么把这两个艺名并在一起。
4. 支持从合集反向操作（按合集名批量补人），方便手工纠错。

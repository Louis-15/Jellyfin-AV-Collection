# AV Collection

一个 **Jellyfin 12.2** 插件：按演员自动维护合集，自动用演员头像做封面，并支持把**同一位演员的多个艺名**合并到一个合集。

## 功能

1. **自动按演员建合集** —— 扫描目标影视库，把每位演员的影片归到他自己的 Jellyfin 合集里，每次计划任务增量同步。
2. **自动封面** —— 用演员头像作为合集封面；头像没变就不重复上传。
3. **艺名合并** —— 同一个人用多个艺名出道（例如「小野夕子」和「葵」）时，可以合并成一个合集，
   合集名默认按作品数取最活跃的两个艺名：`小野夕子 / 葵`。
4. **AI 中文名命名（可选）** —— 用 AI 把日本演员名翻译成中文，用中文名给合集命名。
   演员条目本身的名字不会被修改（那是刮削插件的职责），只影响合集显示名。

## 安装

在 Jellyfin 里添加插件仓库：

```
https://raw.githubusercontent.com/Louis-15/Jellyfin-AV-Collection/main/manifest.json
```

然后：控制台 → 插件 → 目录 → 找到 **AV Collection** → 安装 → 重启 Jellyfin。

也可以手动安装：下载 Release 里的 `av-collection_<version>.zip`，解压到
`/config/plugins/AV Collection_<version>/`，重启。

## 使用

插件设置页（控制台 → 插件 → AV Collection → 设置），从上到下分 8 个区：

- **基本设置**：插件总开关。
- **目标影视库**：**必须勾选**。插件只会动勾选的库；没勾任何库时不会创建或修改任何合集。
- **AI 接口**：启用 AI、供应商（Open Code 内置 / 自定义）、API Key、模型。
  内置供应商的地址与 `x-opencode-session` 请求头由代码固定，界面不提供开关。
  改完先点「测试连接」：几秒内就能确认地址 / Key / 模型名是否都对。
- **合集命名**：合集名里最多显示几个艺名、是否用中文名命名合集、是否清理误匹配的元数据、
  AI 请求超时；「翻译演员名」按钮、译名表（可逐条手改，失焦即保存）和一个就地的「立即同步」。
- **艺名合并**：是否在合并后删除旧合集；勾选两个以上艺名 → 点「合并选中的艺名」。
- **其他**：是否用演员头像做合集封面、最少演员数保护。
- **手动运行**：「重新扫描演员」「预览（不写库）」「立即同步」。
  **预览**只计算不写库；**立即同步**才会真正创建、改名、合并合集并设置封面。
- **关于**：版本与源码地址。

典型操作顺序：勾库 → 保存 → （AI）测试连接 → 翻译演员名 → 核对译名表 →
勾选艺名合并 → 预览 → 立即同步。

计划任务默认每 6 小时跑一次。

## 图片与元数据说明

- 封面取自演员（Person）条目已有的头像。如果演员没有头像，合集也不会设置封面。
- Jellyfin 偶尔会把演员合集匹配成同名电影（例如「かすみ」→《真田女忍者霞传》）。
  「清理误匹配的元数据」会清掉错误的简介、首播日期、ProviderIds 和背景图。

## 构建

```powershell
dotnet build .\Jellyfin.Plugin.AVCollection\Jellyfin.Plugin.AVCollection.csproj -c Release
dotnet test  .\tests\Jellyfin.Plugin.AVCollection.Tests\Jellyfin.Plugin.AVCollection.Tests.csproj -c Release
```

打包（生成 `artifacts/av-collection_<version>.zip`）：

```powershell
.\build.ps1
```

发版（自动改版本号 → commit → tag → push，CI 负责测试/打包/写 checksum/发 Release）：

```powershell
.\release.ps1 -Version 1.0.1.0 -Changelog "改了什么"
```

## 实现要点

- 合集通过 Jellyfin 的 `ICollectionManager` 创建与维护，不直接写数据库。
- 演员信息从 `IPeopleRepository.GetPeopleByItems` 批量读取（Jellyfin 12 起 `BaseItem` 上不再有 `People`）。
- 查询影片时 `CollapseBoxSetItems = false`，否则"已进合集"的影片会从结果里消失，导致合集被清空。
- 运行期状态（分组 → 合集 Id）存在 `<data>/av-collection/state.json`，
  不能只靠合集名找合集：合并组的名字会随作品数变化。
- 中文译名缓存存在 `<data>/av-collection/translations.json`。
  缓存不只是省 AI 调用：合集名依赖它，**名字必须稳定**，
  否则每次翻译结果有差异，合集会被反复改名。
- 设置页的 `<style>` 必须写在 `<body>` 里，Jellyfin 只搬 `<body>` 子节点进 SPA。

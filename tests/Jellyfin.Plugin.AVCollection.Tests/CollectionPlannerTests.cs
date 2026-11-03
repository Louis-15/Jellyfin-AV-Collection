using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AVCollection.Core;
using Xunit;

namespace Jellyfin.Plugin.AVCollection.Tests;

/// <summary>
/// 合集规划逻辑的单元测试。
///
/// 这一层刻意不依赖 Jellyfin：命名规则、合并语义、排序稳定性都能离线验证。
/// 一旦这里出错，线上表现是"合集名来回跳"或"演员的两个合集抢同一批影片"，
/// 排查成本极高，所以覆盖要密。
/// </summary>
public class CollectionPlannerTests
{
    private static ActorInfo Actor(string name, int itemCount, Guid? personId = null)
    {
        var actor = new ActorInfo(name) { PersonId = personId };
        for (var i = 0; i < itemCount; i++)
        {
            // 每部影片都用确定性 Guid，保证测试结果可复现。
            actor.AddItem(DeterministicGuid(name, i), $"{name} 作品 {i}", DeterministicGuid("lib", 0));
        }

        return actor;
    }

    private static Guid DeterministicGuid(string text, int index)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{text}#{index}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    [Fact]
    public void 单个演员生成同名合集()
    {
        var config = new PluginConfiguration();
        var plans = CollectionPlanner.BuildPlans([Actor("小野夕子", 3)], config);

        var plan = Assert.Single(plans);
        Assert.Equal("小野夕子", plan.DisplayName);
        Assert.Equal(3, plan.ItemIds.Count);
        Assert.False(plan.IsMergeGroup);
        Assert.Equal("actor:小野夕子", plan.GroupId);
    }

    [Fact]
    public void 合并组按作品数排序取前两个艺名()
    {
        var config = new PluginConfiguration
        {
            MaxAliasesInName = 2,
            AliasGroups =
            [
                new ActorAliasGroup
                {
                    Id = "g1",
                    Aliases = ["葵", "小野夕子"]
                }
            ]
        };

        var actors = new List<ActorInfo>
        {
            Actor("小野夕子", 5),
            Actor("葵", 2)
        };

        var plan = Assert.Single(CollectionPlanner.BuildPlans(actors, config));
        Assert.Equal("小野夕子 / 葵", plan.DisplayName);
        Assert.Equal(7, plan.ItemIds.Count);
        Assert.True(plan.IsMergeGroup);
        Assert.Equal("group:g1", plan.GroupId);
    }

    [Fact]
    public void 作品数相同时按名字排序保证稳定()
    {
        var config = new PluginConfiguration
        {
            MaxAliasesInName = 2,
            AliasGroups = [new ActorAliasGroup { Id = "g1", Aliases = ["B艺名", "A艺名"] }]
        };

        var actors = new List<ActorInfo> { Actor("B艺名", 3), Actor("A艺名", 3) };

        var first = CollectionPlanner.BuildPlans(actors, config).Single().DisplayName;
        var second = CollectionPlanner.BuildPlans(actors.AsEnumerable().Reverse(), config).Single().DisplayName;

        Assert.Equal(first, second);
        Assert.Equal("A艺名 / B艺名", first);
    }

    [Fact]
    public void 手动指定的合集名优先于自动命名()
    {
        var config = new PluginConfiguration
        {
            AliasGroups =
            [
                new ActorAliasGroup { Id = "g1", Aliases = ["葵", "小野夕子"], DisplayName = "葵（小野夕子）" }
            ]
        };

        var plan = CollectionPlanner.BuildPlans([Actor("葵", 1), Actor("小野夕子", 1)], config).Single();
        Assert.Equal("葵（小野夕子）", plan.DisplayName);
    }

    [Fact]
    public void 没有作品的演员不生成合集()
    {
        var config = new PluginConfiguration();
        var plans = CollectionPlanner.BuildPlans([Actor("没有作品", 0)], config);
        Assert.Empty(plans);
    }

    [Fact]
    public void 合并组里没有作品的艺名不参与命名()
    {
        var config = new PluginConfiguration
        {
            MaxAliasesInName = 2,
            AliasGroups = [new ActorAliasGroup { Id = "g1", Aliases = ["有作品", "没作品"] }]
        };

        var plans = CollectionPlanner.BuildPlans([Actor("有作品", 4), Actor("没作品", 0)], config);
        var plan = Assert.Single(plans);

        Assert.Equal("有作品", plan.DisplayName);
        Assert.Single(plan.Aliases);
    }

    [Fact]
    public void 封面默认取作品数最多的艺名()
    {
        var config = new PluginConfiguration
        {
            AliasGroups = [new ActorAliasGroup { Id = "g1", Aliases = ["葵", "小野夕子"] }]
        };

        var plan = CollectionPlanner.BuildPlans([Actor("葵", 1), Actor("小野夕子", 9)], config).Single();
        Assert.Equal("小野夕子", plan.PosterAlias);
    }

    [Fact]
    public void 指定的封面艺名不存在时回落到作品最多的艺名()
    {
        var config = new PluginConfiguration
        {
            AliasGroups =
            [
                new ActorAliasGroup { Id = "g1", Aliases = ["葵", "小野夕子"], PosterAlias = "已经不在库里的名字" }
            ]
        };

        var plan = CollectionPlanner.BuildPlans([Actor("葵", 1), Actor("小野夕子", 9)], config).Single();
        Assert.Equal("小野夕子", plan.PosterAlias);
    }

    [Fact]
    public void 封面艺人用对应的_PersonId()
    {
        var posterId = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            AliasGroups = [new ActorAliasGroup { Id = "g1", Aliases = ["葵", "小野夕子"], PosterAlias = "葵" }]
        };

        var plan = CollectionPlanner
            .BuildPlans([Actor("葵", 1, posterId), Actor("小野夕子", 9)], config)
            .Single();

        Assert.Equal(posterId, plan.PosterPersonId);
    }

    [Fact]
    public void 被抢走艺名的合并组退化成普通合集()
    {
        // 用户把「乙」同时配进两个组时，先配置的组生效；
        // 后一个组只剩「丙」一个艺名，此时它已经不算"合并"，也不能去删「丙」原来的合集。
        var config = new PluginConfiguration
        {
            AliasGroups =
            [
                new ActorAliasGroup { Id = "g1", Aliases = ["甲", "乙"] },
                new ActorAliasGroup { Id = "g2", Aliases = ["乙", "丙"] }
            ]
        };

        var plans = CollectionPlanner.BuildPlans([Actor("甲", 1), Actor("乙", 2), Actor("丙", 3)], config);

        var merged = Assert.Single(plans, p => p.IsMergeGroup);
        Assert.Equal("group:g1", merged.GroupId);

        var alone = plans.Single(p => p.GroupId == "group:g2");
        Assert.False(alone.IsMergeGroup);
        Assert.Equal("丙", alone.DisplayName);
    }

    [Fact]
    public void 多部影片来自同一个库时_ParentLibraryId_取该库()
    {
        var libId = Guid.NewGuid();
        var actor = new ActorInfo("演员");
        actor.AddItem(Guid.NewGuid(), "片 1", libId);
        actor.AddItem(Guid.NewGuid(), "片 2", libId);

        var plan = CollectionPlanner.BuildPlans([actor], new PluginConfiguration()).Single();
        Assert.Equal(libId, plan.ParentLibraryId);
    }

    [Fact]
    public void 跨库时_ParentLibraryId_取影片最多的库()
    {
        var libA = Guid.NewGuid();
        var libB = Guid.NewGuid();
        var actor = new ActorInfo("演员");
        actor.AddItem(Guid.NewGuid(), "A1", libA);
        actor.AddItem(Guid.NewGuid(), "B1", libB);
        actor.AddItem(Guid.NewGuid(), "B2", libB);

        var plan = CollectionPlanner.BuildPlans([actor], new PluginConfiguration()).Single();
        Assert.Equal(libB, plan.ParentLibraryId);
    }

    [Fact]
    public void 失效的合并组能被识别出来()
    {
        var config = new PluginConfiguration
        {
            AliasGroups =
            [
                new ActorAliasGroup { Id = "alive", Aliases = ["在库里的名字"] },
                new ActorAliasGroup { Id = "stale", Aliases = ["早就改名的艺名"] }
            ]
        };

        var stale = CollectionPlanner.FindStaleGroups([Actor("在库里的名字", 2)], config);
        Assert.Equal(["stale"], stale);
    }

    [Fact]
    public void 合集名最多艺名数为_1_时只显示最活跃的艺名()
    {
        var config = new PluginConfiguration
        {
            MaxAliasesInName = 1,
            AliasGroups = [new ActorAliasGroup { Id = "g1", Aliases = ["葵", "小野夕子"] }]
        };

        var plan = CollectionPlanner.BuildPlans([Actor("葵", 1), Actor("小野夕子", 9)], config).Single();
        Assert.Equal("小野夕子", plan.DisplayName);
    }

    [Fact]
    public void 空配置下每个演员各自一个合集且按名字排序()
    {
        var config = new PluginConfiguration();
        var plans = CollectionPlanner.BuildPlans([Actor("B", 1), Actor("A", 1), Actor("C", 1)], config);

        Assert.Equal(["A", "B", "C"], plans.Select(p => p.DisplayName));
    }
}

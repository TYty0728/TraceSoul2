using System;
using System.Linq;
using TraceSoul2.Data;
using TraceSoul2.Logic;
using TraceSoul2.Manager;

internal static class Program
{
    private static void Main()
    {
        var router = new HierarchicalVectorRouterLogic(new CharacterHashEncoder());
        var ontology = CoreVectorOntologyFactory.Create("小雨", "小光");
        Require(ontology.Count(x => x.Level == VectorNodeLevel.Domain) == 4,
            "第一层必须恰好包含四个固定域槽位");
        Require(ontology.Count(x => x.Level == VectorNodeLevel.Dimension) == 16,
            "第二层固定维度数量发生了意外变化");
        Require(ontology.All(x => x.Level != VectorNodeLevel.Concept),
            "第三层人生 Tag 必须从空库生长");

        ontology.Add(new VectorIndexNode(
            "concept.life.beef-noodles", VectorNodeLevel.Concept, "牛肉面",
            "对话人生中出现的牛肉面、吃牛肉面及其具体经历。", "object",
            new[] { "user", "relation" }, new[] { "dimension.object", "dimension.predicate" },
            new[] { "我中午吃了牛肉面" }, new[] { "我们聊聊电影" }));
        ontology.Add(new VectorIndexNode(
            "concept.life.movie", VectorNodeLevel.Concept, "电影",
            "对话人生中实际谈论、观看和产生感受的电影。", "object",
            new[] { "user", "relation", "world" }, new[] { "dimension.object", "dimension.scope" },
            new[] { "我们聊聊刚看的电影", "电影结局让我想了很久" }, new[] { "今天吃牛肉面" }));
        router.Build(ontology);

        // 字符哈希只检查分层、多父节点与反例逻辑；正式运行使用本地 BGE。
        var route = router.Route(
            "我中午吃了牛肉面，今天有点累",
            new VectorRouteSettings { ConceptMinimumScore = 0.18f });
        Require(route.Domains.Any(x => x.Node.Id == "domain.user"), "生活自述应导航到对方所在的域槽位");
        Require(route.Dimensions.Count > 0 && route.Concepts.Any(x => x.Node.Id == "concept.life.beef-noodles"),
            "牛肉面对话应命中成长后的对应人生 Tag");
        var activationContext = VectorActivationContextLogic.FormatForLlm(route);
        Require(activationContext.Contains("小雨") && activationContext.Contains("概念入口"),
            "分层结果应能形成带名字的激活摘要");

        var movieRoute = router.Route(
            "我们聊聊刚看的电影，结局让我想了很久",
            new VectorRouteSettings { ConceptMinimumScore = 0.18f });
        Require(movieRoute.Concepts.Any(x => x.Node.Id == "concept.life.movie"),
            "电影话题应命中电影人生 Tag");
        Require(movieRoute.Concepts.All(x => x.Node.Id != "concept.life.beef-noodles"),
            "电影话题不能返回牛肉面弱候选");

        var silentSkeleton = router.Route(
            "我中午吃了牛肉面，今天有点累",
            new VectorRouteSettings
            {
                DomainMinimumScore = 0.99f,
                DimensionMinimumScore = 0.99f,
                ConceptMinimumScore = 0.18f
            });
        Require(silentSkeleton.Domains.Count == 0 && silentSkeleton.Dimensions.Count == 0,
            "弱匹配的域和维度不应保底点亮");
        Require(silentSkeleton.Concepts.Any(x => x.Node.Id == "concept.life.beef-noodles"),
            "骨架沉寂时，已经共同到达过的人生 Tag 仍应可以被点亮");

        var tokenizer = new TraceSoul2.Util.BertWordPieceTokenizer(
            "[PAD]\n[UNK]\n[CLS]\n[SEP]\n我\n牛\n肉\n面\ngame\n##s");
        var tokens = tokenizer.Encode("我牛肉面 games", 16).InputIds;
        Require(tokens.SequenceEqual(new[] { 2, 4, 5, 6, 7, 8, 9, 3 }),
            "中文切字与 WordPiece 最长匹配应正确");
        var unknownTokens = tokenizer.Encode("foobar", 16).InputIds;
        Require(unknownTokens.SequenceEqual(new[] { 2, 1, 3 }),
            "未知词必须终止匹配并返回 [UNK]");

        Console.WriteLine("CoreCheck passed: 固定两层 → 生长Tag → 多父路由 → BGE tokenizer。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CharacterHashEncoder : IVectorEncoder
    {
        public string ModelId { get { return "core-check-character-hash"; } }
        public int Dimensions { get { return 512; } }

        public float[] Encode(string text, VectorTextPurpose purpose)
        {
            var result = new float[Dimensions];
            for (var i = 0; i < text.Length; i++)
            {
                Add(result, text[i].ToString(), 1f);
                if (i + 1 < text.Length) Add(result, text.Substring(i, 2), 1.5f);
            }
            TraceSoul2.Util.VectorMathUtil.NormalizeInPlace(result);
            return result;
        }

        private static void Add(float[] target, string token, float weight)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in token) hash = (hash ^ character) * 16777619;
                target[hash % target.Length] += weight;
            }
        }
    }
}

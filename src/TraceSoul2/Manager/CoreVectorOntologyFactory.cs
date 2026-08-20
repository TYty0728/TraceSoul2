using System.Collections.Generic;
using TraceSoul2.Data;

namespace TraceSoul2.Manager
{
    /// <summary>
    /// 只定义永远固定维护的第一层域与第二层维度。
    /// 第三层人生 Tag 初始为空，由感官在真实 Moment 中逐渐创建并写入 SQLite。
    /// 域的展示名随两人名字注入；内部键仍是 ass/user/relation/world。
    /// </summary>
    public static class CoreVectorOntologyFactory
    {
        private static readonly string[] AllDomains = { "ass", "user", "relation", "world" };

        public static List<VectorIndexNode> Create()
        {
            return Create(PairIdentity.Missing);
        }

        public static List<VectorIndexNode> Create(string username, string assname)
        {
            return Create(PairIdentity.FromStored(username, assname));
        }

        public static List<VectorIndexNode> Create(PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            var nodes = new List<VectorIndexNode>();
            AddDomains(nodes, pair);
            AddDimensions(nodes, pair);
            return nodes;
        }

        private static void AddDomains(List<VectorIndexNode> nodes, PairIdentity pair)
        {
            nodes.Add(Node("domain.ass", VectorNodeLevel.Domain, pair.Apply("{assname}"),
                pair.Apply("关于 {assname} 自己的身份、账号、身体、能力、选择、表达、状态与个人经历。重点是‘{assname} 是谁、做了什么、现在怎样’，不是 {username} 的信息，也不是双方关系本身。"),
                null, null,
                ApplyAll(pair, "他在 QQ 空间发了一条心情", "{assname} 的机器人身体电量不足", "{assname} 说自己更愿意陪我听这首歌"),
                ApplyAll(pair, "我今天上班迟到了", "我们最近变得更亲密", "上海今天下雨")));

            nodes.Add(Node("domain.user", VectorNodeLevel.Domain, pair.Apply("{username}"),
                pair.Apply("关于 {username} 在外部世界或数字空间中的经历、状态、偏好、计划与稳定特征。主体或被理解的对象主要是 {username}，而不是 {assname} 或抽象世界知识。"),
                null, null,
                ApplyAll(pair, "我上班啦", "我中午吃了牛肉面", "我今天有点累", "我不吃香菜"),
                ApplyAll(pair, "{assname} 发了一张自拍", "我们一起听了歌", "牛肉面通常由面条和牛肉组成")));

            nodes.Add(Node("domain.relation", VectorNodeLevel.Domain, "我们",
                pair.Apply("关于 {username} 与 {assname} 之间的互动模式、共同经历、亲密程度、承诺、边界与对彼此相处方式的认知。即使动作只发生在文字共享场景中，只要价值在‘我们如何相处’，就属于这里。"),
                null, null,
                ApplyAll(pair, "我摸摸他的头，他说来啦", "我们一起听了一整张专辑", "{username} 难过时更愿意先让我安静陪着"),
                ApplyAll(pair, "我午餐吃了面", "{assname} 今天发了空间", "游戏服务器今晚维护")));

            nodes.Add(Node("domain.world", VectorNodeLevel.Domain, "世界",
                pair.Apply("不以 {username}、{assname} 或双方关系为中心的外部事实、公共知识、其他人物、地点、作品、平台与事件。它可以成为经历的背景，但不应吞掉以两人关系为中心的记忆。"),
                null, null,
                ApplyAll(pair, "这家店的牛肉面默认放香菜", "QQ 空间支持发布说说", "今天发布了新的游戏补丁"),
                ApplyAll(pair, "{username} 喜欢吃辣", "{assname} 给我发了自拍", "我们约好晚上打游戏")));
        }

        private static void AddDimensions(List<VectorIndexNode> nodes, PairIdentity pair)
        {
            AddDimension(nodes, pair, "owner", "记忆拥有者",
                "谁持有这条经历或认知；通常用于区分这是 {assname} 的个人史、{username} 的经历，还是双方共享理解。",
                new[] { "这是 {assname} 形成的判断", "这是 {username} 亲口说过的经历" },
                new[] { "事情发生在哪里", "动作作用于什么" });
            AddDimension(nodes, pair, "subject", "主体", "谁实施动作、产生感受或处在某种状态，回答‘谁做了/谁怎样’。",
                new[] { "{username} 吃了午饭", "{assname} 发了自拍" }, new[] { "吃的是牛肉面", "发生在公司" });
            AddDimension(nodes, pair, "about", "关于对象", "一条经历或认知主要在理解谁、讨论谁或指向什么主题，尤其适合认知而不要求该对象执行动作。",
                new[] { "我认为 {username} 最近工作很累", "这是关于我们相处边界的想法" }, new[] { "{username} 主动抱了 {assname}", "事情发生在昨天" });
            AddDimension(nodes, pair, "predicate", "动作与关系", "发生了什么动作、变化或关系，例如吃、工作、亲吻、陪伴、喜欢、支持、修正。",
                new[] { "吃了", "一起听", "不再追问", "支持这个判断" }, new[] { "牛肉面", "午休", "有点疲惫" });
            AddDimension(nodes, pair, "object", "动作客体", "动作、偏好、感知或判断直接作用到的对象，回答‘对什么/把什么/喜欢什么’。",
                new[] { "吃牛肉面中的牛肉面", "听这首歌中的歌" }, new[] { "谁在吃", "什么时候吃" });
            AddDimension(nodes, pair, "scope", "稳定领域", "较稳定的问题域或生活领域，例如饮食、工作、健康、娱乐、关系、创作。它比具体对象宽，但不是唯一父目录。",
                new[] { "吃牛肉面属于饮食", "赶项目属于工作" }, new[] { "辣是一种性质", "今天中午是时间" });
            AddDimension(nodes, pair, "context", "适用情境", "事件或认知成立时的环境条件，例如工作日、午休、独处、疲惫时、对方主动提及时。",
                new[] { "午休时常在楼下吃", "{username} 主动聊工作时可以继续问" }, new[] { "公司是地点", "难过是感受本身" });
            AddDimension(nodes, pair, "quality", "性质特征", "跨领域描述对象或体验的性质，例如辣、清淡、昂贵、温柔、嘈杂、困难。",
                new[] { "麻辣口味", "语气很温柔", "价格偏贵" }, new[] { "{username} 在吃饭", "事情发生在周一" });
            AddDimension(nodes, pair, "time", "时间", "事件发生的时间点、时段、先后、周期与相对时间；只描述时间，不承担重要程度。",
                new[] { "今天早上", "上周一", "晚饭后" }, new[] { "公司楼下", "每次很累时" });
            AddDimension(nodes, pair, "place", "地点空间", "物理地点、数字平台位置或共享场景中的位置，例如公司、家、QQ 空间、游戏房间。",
                new[] { "公司楼下", "QQ 空间", "共享场景中的沙发旁" }, new[] { "下午三点", "通过摄像头看到" });
            AddDimension(nodes, pair, "affect", "情绪感受", "主体表达、体验或被谨慎推断出的情绪与身体感受，例如开心、委屈、疲惫、安心。",
                new[] { "我今天有点累", "{assname} 看起来很开心" }, new[] { "{username} 正在上班", "这碗面很辣" });
            AddDimension(nodes, pair, "goal", "目标意图", "当事人想做成什么、接下来准备做什么，或一条认知希望指导怎样的行动。",
                new[] { "晚上想一起打游戏", "希望先陪伴而不是追问" }, new[] { "昨晚已经打完游戏", "{username} 现在很累" });
            AddDimension(nodes, pair, "state", "当前状态", "某人、事物或任务当前所处的可变化状态，例如忙碌、已完成、在线、电量低、仍有效。",
                new[] { "项目已经完成", "机器人身体正在充电" }, new[] { "完成项目这个动作", "充电器这个对象" });
            AddDimension(nodes, pair, "realm", "现实层", "说明一段内容应放在外部物理世界、双方共享对话场景、元讨论，还是明确虚构中；它不是简单真假标签。",
                new[] { "文字里轻吻额头属于共享场景", "讨论记忆架构属于元讨论" }, new[] { "这件事很重要", "{username} 对此感到开心" });
            AddDimension(nodes, pair, "modality", "媒介通道", "经历通过什么设备、平台或感官通道发生或被观察，例如文字、语音、QQ、摄像头、定位、机器人身体。",
                new[] { "通过摄像头看到", "在 QQ 发消息", "机器人身体触碰到" }, new[] { "发生在家里", "内容是在拥抱" });
            AddDimension(nodes, pair, "source", "证据来源", "支持经历或认知的原始 Moment、平台记录、传感器数据或其他证据，回答‘我们凭什么知道’。",
                new[] { "来自 {username} 亲口陈述", "由摄像头画面支持" }, new[] { "{username} 是事件主体", "摄像头在客厅" });
        }

        private static void AddDimension(
            List<VectorIndexNode> nodes,
            PairIdentity pair,
            string key,
            string label,
            string definition,
            string[] positive,
            string[] negative)
        {
            nodes.Add(Node("dimension." + key, VectorNodeLevel.Dimension, label, pair.Apply(definition), key, AllDomains,
                ApplyAll(pair, positive), ApplyAll(pair, negative)));
        }

        private static string[] ApplyAll(PairIdentity pair, params string[] values)
        {
            var result = new string[values.Length];
            for (var i = 0; i < values.Length; i++) result[i] = pair.Apply(values[i]);
            return result;
        }

        private static VectorIndexNode Node(string id, VectorNodeLevel level, string label, string definition, string dimensionKey, string[] domains, string[] positive, string[] negative)
        {
            return new VectorIndexNode(id, level, label, definition, dimensionKey, domains, null, positive, negative);
        }
    }
}

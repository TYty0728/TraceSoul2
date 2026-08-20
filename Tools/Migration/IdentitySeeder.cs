using System;
using System.IO;
using System.Linq;
using System.Text;
using TraceSoul2.Data;
using TraceSoul2.Logic;

namespace TraceSoul2.Migrate
{
    /// <summary>保存两人名字，并从四卡种子文件创建身份短卡（人工审后启用）。</summary>
    public static class IdentitySeeder
    {
        public static int Run(MigrationContext context, string[] args)
        {
            var username = CliArgs.Value(args, "--username", "用户");
            var assname = CliArgs.Value(args, "--assname", "助手");
            var callName = CliArgs.Value(args, "--callname", username);
            var cardsPath = CliArgs.Value(args, "--cards");

            if (!string.IsNullOrWhiteSpace(cardsPath))
            {
                cardsPath = Path.GetFullPath(cardsPath);
                if (!File.Exists(cardsPath))
                    throw new InvalidOperationException("短卡种子文件不存在：" + cardsPath);
                IdentityCardLogic.SetSeedJsonOverride(File.ReadAllText(cardsPath, Encoding.UTF8));
            }

            var pair = context.Store.SavePairIdentity(username, assname, callName);
            var cards = context.Store.LoadIdentityCards(MigrationContext.ConversationId);

            // 人格卡与用户档案卡是「人本人控制」的卡：种子文件里的内容永远覆盖当前库内版本，
            // 方便用户直接编辑 identity_cards.json 后重跑本命令生效。
            if (!string.IsNullOrWhiteSpace(cardsPath))
            {
                var seedJson = File.ReadAllText(cardsPath, Encoding.UTF8);
                IdentityCardLogic.SetSeedJsonOverride(seedJson);
                foreach (var slot in new[] { IdentityCardSlotValues.Personality, IdentityCardSlotValues.UserProfile })
                {
                    var body = IdentityCardLogic.DefaultBody(slot, pair);
                    if (body.Length > 0)
                    {
                        var current = context.Store.LoadIdentityCards(MigrationContext.ConversationId)
                            .First(x => x.Slot == slot);
                        if (current.Body != body)
                        {
                            context.Store.SaveIdentityCard(
                                MigrationContext.ConversationId, slot, body, string.Empty);
                            Console.WriteLine("已用种子文件更新卡：" + slot);
                        }
                    }
                }
                cards = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            }

            Console.WriteLine("两人名字：" + pair.Username + " / " + pair.Assname + " / " + pair.CallName);
            Console.WriteLine();
            Console.WriteLine("四张身份短卡：");
            foreach (var card in cards)
                Console.WriteLine("【" + IdentityCardSlotValues.Title(card.Slot, pair) + "】" + card.Body + Environment.NewLine);
            return 0;
        }
    }
}

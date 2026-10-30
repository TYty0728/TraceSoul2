using System;
using System.Collections.Generic;
using TraceSoul2.Data;
using TraceSoul2.Util;
namespace TraceSoul2.Migrate
{
    public static partial class DayBuilder
    {
        public static int PrintCardsCommand(MigrationContext context)
        {
            var pair = context.Store.LoadPairIdentity();
            var cards = context.Store.LoadIdentityCards(MigrationContext.ConversationId);
            PrintCards("四张身份小卡", cards, pair);
            return 0;
        }

        private static void PrintCards(string title, List<IdentityCardRecord> cards, PairIdentity pair)
        {
            Console.WriteLine("========== " + title + " ==========");
            foreach (var card in cards)
            {
                Console.WriteLine("【" + IdentityCardSlotValues.Title(card.Slot, pair) + "】rev" + card.Revision);
                Console.WriteLine(card.Body);
                Console.WriteLine();
            }
        }

        internal static void LogCall(
            MigrationContext context, string dayKey, string kind, int chunkIndex, string digest, string outputJson)
        {
            context.Migration.SaveCallLog(new ReplayCallLogRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                DayKey = dayKey,
                CallKind = kind,
                ChunkIndex = chunkIndex,
                OutputJson = Limit(outputJson, 60000)
            });
            MigrationLive.Instance?.Notify(kind, digest, Limit(outputJson, 4000));
        }

    }
}

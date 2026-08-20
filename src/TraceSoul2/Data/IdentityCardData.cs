using System;
using System.Collections.Generic;
using SQLite;

namespace TraceSoul2.Data
{
    public static class IdentityCardSlotValues
    {
        public const string Personality = "personality";
        public const string Self = "self";
        public const string Other = "other";
        public const string Relation = "relation";
        public const string UserProfile = "user_profile";
        public const string ExpressionHabit = "expression_habit";

        public static readonly string[] All =
        {
            Personality, Self, Other, Relation, UserProfile, ExpressionHabit
        };

        public static bool IsKnown(string slot)
        {
            return slot == Personality || slot == Self || slot == Other ||
                   slot == Relation || slot == UserProfile || slot == ExpressionHabit;
        }

        public static string Title(string slot, PairIdentity pair)
        {
            pair = pair ?? PairIdentity.Missing;
            if (slot == Personality) return "我的人格";
            if (slot == Self) return "我是谁";
            if (slot == Other) return pair.IsComplete ? pair.Username + "是谁" : "她是谁";
            if (slot == Relation) return "我们的关系";
            if (slot == UserProfile) return pair.IsComplete ? pair.Username + "的档案" : "她的档案";
            if (slot == ExpressionHabit) return "表达习惯";
            return slot ?? string.Empty;
        }

        public static int BodyLimit(string slot)
        {
            if (slot == Personality) return 800;
            if (slot == UserProfile) return 400;
            return 300;
        }
    }

    [Table("identity_cards")]
    public sealed class IdentityCardRecord
    {
        [PrimaryKey]
        public string Id { get; set; }

        [Indexed]
        public string ConversationId { get; set; }

        [Indexed]
        public string Slot { get; set; }

        public string Body { get; set; }
        public int Revision { get; set; }
        public string SourceMomentId { get; set; }
        public long UpdatedUnixMs { get; set; }
    }

    [Serializable]
    public sealed class IdentityCardRevisionData
    {
        public string slot;
        public bool changed;
        public string body;
        public string reason;
    }

    [Serializable]
    public sealed class IdentityReviewOutputData
    {
        public string summary;
        public List<IdentityCardRevisionData> cards = new List<IdentityCardRevisionData>();
    }

    [Serializable]
    public sealed class IdentityCardSeedItemData
    {
        public string slot;
        public string body;
    }

    [Serializable]
    public sealed class IdentityCardSeedFileData
    {
        public IdentityCardSeedItemData[] cards;
    }
}
